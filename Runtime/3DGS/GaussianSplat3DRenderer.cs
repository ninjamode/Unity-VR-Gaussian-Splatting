// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using Unity.Profiling;
using Unity.Profiling.LowLevel;
using Gaussians.Core;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace Gaussians.ThreeD
{
    class GaussianSplat3DRenderSystem
    {
        // ReSharper disable MemberCanBePrivate.Global - used by HDRP/URP features that are not always compiled
        internal static readonly ProfilerMarker s_ProfDraw = new(ProfilerCategory.Render, "Gaussians.3D.Draw", MarkerFlags.SampleGPU);
        internal static readonly ProfilerMarker s_ProfCompose = new(ProfilerCategory.Render, "Gaussians.3D.Compose", MarkerFlags.SampleGPU);
        internal static readonly ProfilerMarker s_ProfCalcView = new(ProfilerCategory.Render, "Gaussians.3D.CalcView", MarkerFlags.SampleGPU);
        // ReSharper restore MemberCanBePrivate.Global

        public static GaussianSplat3DRenderSystem instance => ms_Instance ??= new GaussianSplat3DRenderSystem();
        static GaussianSplat3DRenderSystem ms_Instance;

        readonly Dictionary<GaussianSplat3DRenderer, MaterialPropertyBlock> m_Splats = new();
        readonly Dictionary<Camera, CommandBuffer> m_CameraCommandBuffersDone = new();
        readonly List<Camera> m_DestroyedCommandBufferCameras = new();
        readonly List<(GaussianSplat3DRenderer, MaterialPropertyBlock)> m_ActiveSplats = new();

        CommandBuffer m_CommandBuffer;

        public static bool ConvertCompositeGammaToLinear = true;

        internal bool HasDepthSplatsForCamera(Camera camera)
        {
            foreach (var entry in m_Splats)
            {
                var gs = entry.Key;
                if (gs && gs.isActiveAndEnabled && gs.HasValidAsset && gs.HasValidRenderSetup && gs.m_WriteDepth &&
                    gs.m_RenderMode == GaussianSplat3DRenderer.RenderMode.Splats &&
                    (camera.cullingMask & (1 << gs.gameObject.layer)) != 0) return true;
            }
            return false;
        }

        public bool HasCompositeSplats { get; private set; }
        public bool HasDirectSplats { get; private set; }

        // URP selects camera-stack attachments using the base camera alone.
        // Include composite renderers hidden by its culling mask: an overlay may
        // render them later into those same attachments.
        internal bool RequiresCompositeIntermediate
        {
            get
            {
                foreach (var entry in m_Splats)
                {
                    var gs = entry.Key;
                    if (gs != null && gs.isActiveAndEnabled && gs.HasValidAsset && gs.HasValidRenderSetup &&
                        !gs.usesDirectTransparentPath)
                        return true;
                }
                return false;
            }
        }

        public void RegisterSplat(GaussianSplat3DRenderer r)
        {
            if (m_Splats.Count == 0)
            {
                if (GraphicsSettings.currentRenderPipeline == null)
                    Camera.onPreCull += OnPreCullCamera;
            }

            m_Splats.Add(r, new MaterialPropertyBlock());
        }

        public void UnregisterSplat(GaussianSplat3DRenderer r)
        {
            if (!m_Splats.ContainsKey(r))
                return;
            m_Splats.Remove(r);
            if (m_Splats.Count == 0)
            {
                foreach (var entry in m_CameraCommandBuffersDone)
                {
                    if (entry.Key) entry.Key.RemoveCommandBuffer(CameraEvent.BeforeForwardAlpha, entry.Value);
                    entry.Value.Dispose();
                }
                if (m_CameraCommandBuffersDone.Count == 0) m_CommandBuffer?.Dispose();
                m_CameraCommandBuffersDone.Clear();
                m_ActiveSplats.Clear();
                m_CommandBuffer = null;
                Camera.onPreCull -= OnPreCullCamera;
            }
        }

        // ReSharper disable once MemberCanBePrivate.Global - used by HDRP/URP features that are not always compiled
        public bool GatherSplatsForCamera(Camera cam)
        {
            HasCompositeSplats = false;
            HasDirectSplats = false;
            m_ActiveSplats.Clear();
            if (cam.cameraType == CameraType.Preview)
                return false;
            m_ActiveSplats.Clear();
            foreach (var kvp in m_Splats)
            {
                var gs = kvp.Key;
                if (gs == null || !gs.isActiveAndEnabled || !gs.HasValidAsset || !gs.HasValidRenderSetup ||
                    (cam.cullingMask & (1 << gs.gameObject.layer)) == 0)
                    continue;
                gs.PrepareSource();
                m_ActiveSplats.Add((kvp.Key, kvp.Value));
                if (gs.usesDirectTransparentPath)
                    HasDirectSplats = true;
                else
                    HasCompositeSplats = true;
            }
            if (m_ActiveSplats.Count == 0)
                return false;

            var camTr = cam.transform;
            m_ActiveSplats.Sort((a, b) =>
            {
                var orderA = a.Item1.m_RenderOrder;
                var orderB = b.Item1.m_RenderOrder;
                if (orderA != orderB)
                    return orderB.CompareTo(orderA);
                var trA = a.Item1.transform;
                var trB = b.Item1.transform;
                var posA = camTr.InverseTransformPoint(trA.position);
                var posB = camTr.InverseTransformPoint(trB.position);
                return posA.z.CompareTo(posB.z);
            });

            return true;
        }

        internal void SubmitDirectSplatsForCamera(Camera cam)
        {
            GatherSplatsForCamera(cam);
            foreach (var kvp in m_ActiveSplats)
            {
                GaussianSplat3DRenderer gs = kvp.Item1;
                if (gs.usesDirectTransparentPath)
                    gs.QueueDirectTransparentDraw(cam);
            }
        }

        // ReSharper disable once MemberCanBePrivate.Global - used by HDRP/URP features that are not always compiled
        public Material SortAndRenderCompositeSplats(Camera cam, CommandBuffer cmb, bool convertComposite = true)
        {
            var draws = GatherCompositeDraws(cam, out var composite, convertComposite);
            PrepareCompositeSplats(cam, cmb);
            foreach (var draw in draws)
            {
                if (draw.IndirectArgs != null)
                    cmb.DrawProceduralIndirect(draw.Indices, draw.Matrix, draw.Material, 0,
                        MeshTopology.Triangles, draw.IndirectArgs, 20, draw.Properties);
                else
                    cmb.DrawProcedural(draw.Indices, draw.Matrix, draw.Material, 0,
                        MeshTopology.Triangles, draw.IndexCount, draw.Count, draw.Properties);
            }
            return composite;
        }

        internal void PrepareCompositeSplats(Camera cam, CommandBuffer cmb)
        {
            GatherSplatsForCamera(cam);
            foreach (var entry in m_ActiveSplats)
            {
                var gs = entry.Item1;
                if (gs.usesDirectTransparentPath)
                    continue;
                var resources = gs.GetCameraRenderResources(cam);
                if (resources == null)
                    continue;
                gs.PrepareCamera(cmb, cam, false, resources);
            }
        }

        internal List<SplatDraw> GatherCompositeDraws(Camera cam, out Material matComposite, bool convertComposite = true)
        {
            GatherSplatsForCamera(cam);
            matComposite = null;
            var draws = new List<SplatDraw>();
            foreach (var kvp in m_ActiveSplats)
            {
                var gs = kvp.Item1;
                if (gs.usesDirectTransparentPath)
                    continue;
                gs.EnsureMaterials();
                matComposite = gs.GetCompositeMaterial(convertComposite);
                var mpb = new MaterialPropertyBlock();
                var cameraResources = gs.GetCameraRenderResources(cam);
                if (cameraResources == null)
                    continue;

                var matrix = gs.transform.localToWorldMatrix;
                Material displayMat = gs.m_RenderMode switch
                {
                    GaussianSplat3DRenderer.RenderMode.DebugPoints => gs.m_MatDebugPoints,
                    GaussianSplat3DRenderer.RenderMode.DebugPointIndices => gs.m_MatDebugPoints,
                    GaussianSplat3DRenderer.RenderMode.DebugBoxes => gs.m_MatDebugBoxes,
                    GaussianSplat3DRenderer.RenderMode.DebugChunkBounds => gs.m_MatDebugBoxes,
                    _ => gs.m_MatSplats
                };
                if (displayMat == null)
                    continue;

                gs.SetAssetDataOnMaterial(mpb);
                gs.SetCameraProperties(mpb, cam, cameraResources);
                mpb.SetBuffer(GaussianSplat3DRenderer.Props.SplatChunks, gs.m_GpuChunks);

                mpb.SetBuffer(GaussianSplat3DRenderer.Props.SplatViewData, cameraResources.GpuView);

                mpb.SetBuffer(GaussianSplat3DRenderer.Props.OrderBuffer, cameraResources.GpuSortKeys);
                mpb.SetFloat(GaussianSplat3DRenderer.Props.SplatScale, gs.m_SplatScale);
                mpb.SetFloat(GaussianSplat3DRenderer.Props.SplatOpacityScale, gs.m_OpacityScale);
                mpb.SetFloat(GaussianSplat3DRenderer.Props.SplatSize, gs.m_PointDisplaySize);
                mpb.SetInteger(GaussianSplat3DRenderer.Props.SHOrder, gs.m_SHOrder);
                mpb.SetInteger(GaussianSplat3DRenderer.Props.SHOnly, gs.m_SHOnly ? 1 : 0);
                mpb.SetInteger(GaussianSplat3DRenderer.Props.DisplayIndex, gs.m_RenderMode == GaussianSplat3DRenderer.RenderMode.DebugPointIndices ? 1 : 0);
                mpb.SetInteger(GaussianSplat3DRenderer.Props.DisplayChunks, gs.m_RenderMode == GaussianSplat3DRenderer.RenderMode.DebugChunkBounds ? 1 : 0);

                int indexCount = 6;
                int instanceCount = gs.splatCount;
                if (gs.m_RenderMode is GaussianSplat3DRenderer.RenderMode.DebugBoxes or GaussianSplat3DRenderer.RenderMode.DebugChunkBounds)
                    indexCount = 36;
                if (gs.m_RenderMode == GaussianSplat3DRenderer.RenderMode.DebugChunkBounds)
                    instanceCount = gs.m_GpuChunksValid ? gs.m_GpuChunks.count : 0;

                draws.Add(new SplatDraw
                {
                    Indices = gs.m_GpuIndexBuffer, Matrix = matrix, Material = displayMat,
                    Properties = mpb, IndexCount = indexCount, Count = instanceCount,
                    IndirectArgs = gs.GetIndirectArgs(cameraResources),
                });
            }
            return draws;
        }

        // Direct splats are submitted through Unity's transparent queue during each pipeline's
        // pre-cull callback. This pass updates their ordering and view-dependent GPU data before
        // Unity reaches its normal transparent rendering phase.
        public void PrepareDirectSplats(Camera cam, CommandBuffer cmb)
        {
            GatherSplatsForCamera(cam);
            foreach (var kvp in m_ActiveSplats)
            {
                var gs = kvp.Item1;
                if (!gs.usesDirectTransparentPath)
                    continue;

                var cameraResources = gs.GetCameraRenderResources(cam);
                if (cameraResources == null)
                    continue;

                gs.PrepareCamera(cmb, cam, true, cameraResources);
            }
        }

        internal sealed class SplatDraw
        {
            internal GraphicsBuffer Indices;
            internal GraphicsBuffer IndirectArgs;
            internal Matrix4x4 Matrix;
            internal Material Material;
            internal MaterialPropertyBlock Properties;
            internal int IndexCount = 6;
            internal int Count;
        }

        // Snapshot draw state per camera while recording the graph. The late pass must
        // not depend on the global active-camera list or mutate a queued color draw's MPB.
        internal List<SplatDraw> GatherDepthDraws(Camera camera)
        {
            var draws = new List<SplatDraw>();
            if (camera.cameraType == CameraType.Preview)
                return draws;
            foreach (var entry in m_Splats)
            {
                var gs = entry.Key;
                if (gs == null || !gs.isActiveAndEnabled || !gs.HasValidAsset || !gs.HasValidRenderSetup ||
                    !gs.m_WriteDepth || gs.m_RenderMode != GaussianSplat3DRenderer.RenderMode.Splats ||
                    (camera.cullingMask & (1 << gs.gameObject.layer)) == 0)
                    continue;

                var resources = gs.GetCameraRenderResources(camera);
                if (resources == null)
                    continue;
                gs.EnsureMaterials();
                var properties = new MaterialPropertyBlock();
                gs.SetAssetDataOnMaterial(properties);
                gs.SetCameraProperties(properties, camera, resources);
                properties.SetBuffer(GaussianSplat3DRenderer.Props.SplatViewData, resources.GpuView);
                properties.SetBuffer(GaussianSplat3DRenderer.Props.OrderBuffer, resources.GpuSortKeys);
                draws.Add(new SplatDraw
                {
                    Indices = gs.m_GpuIndexBuffer,
                    Matrix = gs.transform.localToWorldMatrix,
                    Material = gs.m_MatSplatsDirect,
                    Properties = properties,
                    Count = gs.splatCount,
                    IndirectArgs = gs.GetIndirectArgs(resources),
                });
            }
            return draws;
        }

        // ReSharper disable once MemberCanBePrivate.Global - used by HDRP/URP features that are not always compiled
        // ReSharper disable once UnusedMethodReturnValue.Global - used by HDRP/URP features that are not always compiled
        public CommandBuffer InitialClearCmdBuffer(Camera cam)
        {
            if (GraphicsSettings.currentRenderPipeline == null && cam != null)
            {
                m_DestroyedCommandBufferCameras.Clear();
                foreach (var entry in m_CameraCommandBuffersDone)
                    if (!entry.Key) m_DestroyedCommandBufferCameras.Add(entry.Key);
                foreach (var destroyedCamera in m_DestroyedCommandBufferCameras)
                {
                    m_CameraCommandBuffersDone[destroyedCamera].Dispose();
                    m_CameraCommandBuffersDone.Remove(destroyedCamera);
                }
                if (!m_CameraCommandBuffersDone.TryGetValue(cam, out m_CommandBuffer))
                {
                    m_CommandBuffer = new CommandBuffer { name = "Gaussians.3D.Render" };
                    cam.AddCommandBuffer(CameraEvent.BeforeForwardAlpha, m_CommandBuffer);
                    m_CameraCommandBuffersDone.Add(cam, m_CommandBuffer);
                }
            }
            else m_CommandBuffer ??= new CommandBuffer { name = "Gaussians.3D.Render" };
            m_CommandBuffer.Clear();
            return m_CommandBuffer;
        }

        void OnPreCullCamera(Camera cam)
        {
            InitialClearCmdBuffer(cam);
            if (!GatherSplatsForCamera(cam))
                return;

            if (HasDirectSplats)
                SubmitDirectSplatsForCamera(cam);

            if (HasDirectSplats)
                PrepareDirectSplats(cam, m_CommandBuffer);

            if (!HasCompositeSplats)
                return;

            m_CommandBuffer.GetTemporaryRT(GaussianSplat3DRenderer.Props.GaussianSplatRT, GaussianRenderTargets.Accumulation(cam), FilterMode.Point);
            m_CommandBuffer.SetRenderTarget(GaussianSplat3DRenderer.Props.GaussianSplatRT, BuiltinRenderTextureType.CurrentActive);
            m_CommandBuffer.ClearRenderTarget(RTClearFlags.Color, new Color(0, 0, 0, 0), 0, 0);

            // We only need this to determine whether we're rendering into backbuffer or not. However, detection this
            // way only works in BiRP so only do it here.
            m_CommandBuffer.SetGlobalTexture(GaussianSplat3DRenderer.Props.CameraTargetTexture, BuiltinRenderTextureType.CameraTarget);

            // add sorting, view calc and drawing commands for each splat object
            var cameraSettings = cam.GetComponent<GaussianSplat3DBuiltinSettings>();
            bool convert = cameraSettings != null && cameraSettings.isActiveAndEnabled
                ? cameraSettings.m_ConvertCompositeGammaToLinear : ConvertCompositeGammaToLinear;
            Material matComposite = SortAndRenderCompositeSplats(cam, m_CommandBuffer, convert);

            m_CommandBuffer.BeginSample(s_ProfCompose);
            m_CommandBuffer.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
            m_CommandBuffer.DrawProcedural(Matrix4x4.identity, matComposite, 0, MeshTopology.Triangles, 3, GaussianRenderTargets.FullscreenInstances(cam));
            m_CommandBuffer.EndSample(s_ProfCompose);
            m_CommandBuffer.ReleaseTemporaryRT(GaussianSplat3DRenderer.Props.GaussianSplatRT);
        }
    }

    [ExecuteInEditMode]
    [AddComponentMenu("Gaussians/3D Splat Renderer")]
    public partial class GaussianSplat3DRenderer : MonoBehaviour
    {
        public enum RenderMode
        {
            Splats,
            DebugPoints,
            DebugPointIndices,
            DebugBoxes,
            DebugChunkBounds,
        }

        public enum RenderPath
        {
            CompositeTexture,
            DirectTransparent,
        }

        public GaussianSplat3DAsset m_Asset;

        [Tooltip("Composite Texture preserves the original front-to-back offscreen accumulation. Direct Transparent sorts back-to-front and submits premultiplied splats to Unity's transparent queue.")]
        public RenderPath m_RenderPath = RenderPath.DirectTransparent;
        [Tooltip("Rendering order within the selected render path. Higher values render later/on top. When paths are mixed, the Composite Texture group is always resolved before the Direct Transparent group.")]
        public int m_RenderOrder;
        [Tooltip("Direct Transparent: convert gamma-encoded colors before blending into a Linear project target. Leave off for assets already trained/exported in linear space.")]
        public bool m_ConvertGammaToLinear = true;
        [Tooltip("URP only: adds a draw writing approximate splat-center depth after transparent color for XR reprojection. Does not change color blending or fill empty background pixels.")]
        public bool m_WriteDepth = false;
        // One 8-bit alpha step. This is a coverage and
        // quality/performance convention, not a scene-specific depth criterion.
        public const float DefaultAlphaCutoff = 1.0f / 255.0f;
        [Range(0.0f, 1.0f)]
        [Tooltip("Advanced: minimum per-splat fragment opacity retained by BOTH color and depth. Default is 1/255. Higher values remove faint contributions and can reduce blending work, but may thin surfaces or cause popping. This is not accumulated opacity. Zero retains all positive alpha within the existing splat geometry.")]
        public float m_AlphaCutoff = DefaultAlphaCutoff;
        [Tooltip("Shrink splat quads to the current opacity cutoff without changing the Gaussian falloff. Disable to compare against the original bounds. Applies to color and depth; selected splats retain their full footprint.")]
        [HideInInspector] public bool m_OpacityAwareBounds = true;
        [Range(0.1f, 2.0f)] [Tooltip("Additional scaling factor for the splats")]
        public float m_SplatScale = 1.0f;
        [Range(0.05f, 20.0f)]
        [Tooltip("Additional scaling factor for opacity")]
        public float m_OpacityScale = 1.0f;
        [Range(0, 3)] [Tooltip("Spherical Harmonics order to use")]
        public int m_SHOrder = 3;
        [Tooltip("Show only Spherical Harmonics contribution, using gray color")]
        public bool m_SHOnly;
        [Range(1,30)] [Tooltip("Sort splats only every N frames")]
        public int m_SortNthFrame = 1;
        [Tooltip("Compact surviving splat IDs on the GPU, then sort and draw only that population. View changes force a fresh compacted sort regardless of Sort Nth Frame.")]
        [HideInInspector] public bool m_CompactVisibleSplats;
        [Tooltip("Enable the optional size, distance and opacity rejection thresholds. Disabling preserves their configured values; mandatory near-plane and invalid-data guards remain.")]
        [HideInInspector] public bool m_EarlyRejection = true;
        [Tooltip("Load SH only after visibility checks. Disable to measure the cost of loading SH before rejection.")]
        [HideInInspector] public bool m_DeferredSHLoading = true;
        [Tooltip("Reject offscreen splats using conservative footprint bounds before covariance projection. Independent of threshold pruning.")]
        [HideInInspector] public bool m_EarlyFrustumCulling = true;
        [Min(-1), Tooltip("Rejected splats required to enable compaction and deferred SH. -1 disables, 0 always enables. Positive values use a recent asynchronous visibility estimate; 100,000 is an experimental default.")]
        public int m_CompactionThreshold = 100000;
        // Explicit benchmark overrides bypass the automatic bundle; not normal inspector controls.
        [HideInInspector] public bool m_OptimizationOverrides;
        bool UseCompaction(CameraRenderResources resources)
        {
            if (m_RenderMode != RenderMode.Splats) return false;
            if (m_OptimizationOverrides) return m_CompactVisibleSplats;
            // Latch positive-threshold decisions per frame so a readback callback cannot
            // change the draw submission mode between pre-cull submission and preparation.
            if (m_CompactionThreshold <= 0) return m_CompactionThreshold == 0;
            if (resources.PolicyFrame != Time.frameCount || resources.PolicyThreshold != m_CompactionThreshold)
            {
                resources.PolicyFrame = Time.frameCount;
                resources.PolicyThreshold = m_CompactionThreshold;
                resources.PolicyCompaction = ShouldCompact(m_CompactionThreshold, resources.HasVisibilityEstimate, resources.RejectedSplats);
            }
            return resources.PolicyCompaction;
        }
        internal static bool ShouldCompact(int threshold, bool hasEstimate, uint rejected) =>
            threshold == 0 || (threshold > 0 && hasEstimate && rejected >= (uint)threshold);
        bool UseDeferredSH(CameraRenderResources resources) => m_OptimizationOverrides ? m_DeferredSHLoading : UseCompaction(resources);
        float MinimumOpacity => m_OptimizationOverrides ? (m_EarlyRejection ? Mathf.Clamp01(m_MinimumSplatOpacity) : 0) :
            Mathf.Max(0, Mathf.Clamp01(m_AlphaCutoff) * 0.998f - 5.96046448e-8f);
        public enum SortPrecision { Bits32 = 32, Bits24 = 24, Bits16 = 16 }
        [Tooltip("Depth-key precision: 32 retains full precision; 24/16 discard low mantissa bits and use three/two radix passes. Lower precision can cause ordering artifacts.")]
        public SortPrecision m_SortPrecision = SortPrecision.Bits32;
        internal int sortKeyBits => m_SortPrecision == SortPrecision.Bits16 ? 16 : m_SortPrecision == SortPrecision.Bits24 ? 24 : 32;
        [Min(0)] [Tooltip("Minimum camera-space center depth in world units while Early Rejection is enabled. Combined with the camera near plane using the larger distance. Default 0.1 protects against very close splats; zero uses only the camera near plane.")]
        public float m_MinimumSplatDistance = 0.1f;
        [Range(0, 1)] [Tooltip("Reject splats below this peak opacity after opacity scaling, before SH loading. Zero disables additional opacity rejection. Selected splats are exempt.")]
        [HideInInspector] public float m_MinimumSplatOpacity;
        [Min(0.0f)]
        [Tooltip("Cull splats below this projected three-sigma pixel radius, before covariance filtering. Zero preserves existing filtering. Higher values can remove detail or cause popping.")]
        public float m_MinimumSplatRadiusPixels = 0.7f;

        public RenderMode m_RenderMode = RenderMode.Splats;
        [Range(1.0f,15.0f)] public float m_PointDisplaySize = 3.0f;

        public GaussianCutout[] m_Cutouts;

        public Shader m_ShaderSplats;
        public Shader m_ShaderComposite;
        public Shader m_ShaderDebugPoints;
        public Shader m_ShaderDebugBoxes;
        [Tooltip("3D Gaussian splatting compute shader")]
        public ComputeShader m_CSSplatUtilities;

        int m_SplatCount; // initially same as asset splat count, but editing can change this
        GraphicsBuffer m_GpuPosData;
        GraphicsBuffer m_GpuOtherData;
        GraphicsBuffer m_GpuSHData;
        Texture m_GpuColorData;
        internal GraphicsBuffer m_GpuChunks;
        internal bool m_GpuChunksValid;
        internal GraphicsBuffer m_GpuIndexBuffer;

        // these buffers are only for splat editing, and are lazily created
        GraphicsBuffer m_GpuEditCutouts;
        GraphicsBuffer m_GpuEditCountsBounds;
        GraphicsBuffer m_GpuEditSelected;
        GraphicsBuffer m_GpuEditDeleted;
        GraphicsBuffer m_GpuEditSelectedMouseDown; // selection state at start of operation
        GraphicsBuffer m_GpuEditPosMouseDown; // position state at start of operation
        GraphicsBuffer m_GpuEditOtherMouseDown; // rotation/scale state at start of operation

        GpuSorting m_Sorter;

        internal Material m_MatSplats;
        internal Material m_MatSplatsDirect;
        internal Material m_MatComposite;
        internal Material m_MatCompositeRaw;
        internal Material GetCompositeMaterial(bool convert) => convert ? m_MatComposite : m_MatCompositeRaw;
        internal Material m_MatDebugPoints;
        internal Material m_MatDebugBoxes;
        Mesh m_DirectQuadMesh;

        internal struct CameraSortState
        {
            public int LastFrame;
            public int RenderCount;
            public bool BackToFront;
            public bool HasSignature;
            public Matrix4x4 MatrixMV;
            public int RenderDataVersion;
            public int SortNthFrame;
            public int SortKeyBits;
            public uint PositionRevision;
        }

        internal struct ViewSignature : IEquatable<ViewSignature>
        {
            public Matrix4x4 View;
            public Matrix4x4 Projection;
            public Matrix4x4 RightView, RightProjection;
            public int ViewCount;
            public int IndirectInstanceMultiplier;
            public Vector2Int RightScreenSize;
            public bool ConvertColor;
            public Matrix4x4 ObjectToWorld;
            public int ScreenWidth;
            public int ScreenHeight;
            public float SplatScale;
            public float OpacityScale;
            public int SHOrder;
            public bool SHOnly;
            public float MinimumSplatRadiusPixels;
            public int CutoutHash;
            public int RenderDataVersion;
            public uint AttributeRevision;
            public float NearClip;
            public float MinimumDistance, MinimumOpacity;
            public bool DeferredSHLoading;
            public bool EarlyFrustumCulling, OpacityAwareBounds;
            public float AlphaCutoff;

            public bool Equals(ViewSignature other)
            {
                return EarlyFrustumCulling == other.EarlyFrustumCulling &&
                       OpacityAwareBounds == other.OpacityAwareBounds && AlphaCutoff.Equals(other.AlphaCutoff) &&
                       IndirectInstanceMultiplier == other.IndirectInstanceMultiplier && DeferredSHLoading == other.DeferredSHLoading && MinimumDistance.Equals(other.MinimumDistance) && MinimumOpacity.Equals(other.MinimumOpacity) &&
                       AttributeRevision == other.AttributeRevision && NearClip.Equals(other.NearClip) && RightView.Equals(other.RightView) && RightProjection.Equals(other.RightProjection) &&
                       RightScreenSize.Equals(other.RightScreenSize) && ViewCount == other.ViewCount && ConvertColor == other.ConvertColor && View.Equals(other.View) &&
                       Projection.Equals(other.Projection) &&
                       ObjectToWorld.Equals(other.ObjectToWorld) &&
                       ScreenWidth == other.ScreenWidth &&
                       ScreenHeight == other.ScreenHeight &&
                       SplatScale.Equals(other.SplatScale) &&
                       OpacityScale.Equals(other.OpacityScale) &&
                       SHOrder == other.SHOrder &&
                       SHOnly == other.SHOnly &&
                       MinimumSplatRadiusPixels.Equals(other.MinimumSplatRadiusPixels) &&
                       CutoutHash == other.CutoutHash &&
                       RenderDataVersion == other.RenderDataVersion;
            }
        }

        internal sealed class CameraRenderResources : IDisposable
        {
            internal GraphicsBuffer GpuSortDistances;
            internal GraphicsBuffer GpuSortKeys;
            internal GraphicsBuffer GpuView;
            internal GraphicsBuffer CompactGroups;
            internal GraphicsBuffer CompactArgs;
            internal bool Compacted;
            internal bool HasVisibilityEstimate, VisibilityReadbackPending;
            internal uint RejectedSplats;
            internal int NextVisibilityProbeFrame, VisibilityGeneration;
            internal int PolicyFrame = -1, PolicyThreshold;
            internal bool PolicyCompaction;
            internal int SortDispatchCount; // Regression-test diagnostic; no GPU readback.


            internal void EnsureCompactionBuffers()
            {
                if (CompactArgs != null) return;
                CompactGroups = new GraphicsBuffer(GraphicsBuffer.Target.Structured, (GpuSortKeys.count + 255) / 256, 4)
                    { name = "GaussianVisibilityGroupOffsets" };
                CompactArgs = new GraphicsBuffer(GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.IndirectArguments, 10, 4)
                    { name = "GaussianVisibilityIndirectArgs" };
                CompactArgs.SetData(new uint[10]);
            }
            internal GpuSorting.Args SorterArgs;
            internal readonly MaterialPropertyBlock DirectMaterialProperties = new();
            internal CameraSortState SortState;
            internal ViewSignature ViewSignature;
            internal bool HasViewSignature;
            internal bool SortKeysInitialized;

            internal Material DirectMaterial;
            internal int ViewCount;
            internal int LastUsedFrame;

            internal bool EnsureBuffers(GaussianSplat3DRenderer owner, Camera camera)
            {
                int count = owner.m_SplatCount;
                if (count <= 0)
                    return false;
                LastUsedFrame = Time.frameCount;
                int viewCount = GaussianSplatCameraState.ForCamera(camera).ViewCount;
                if (GpuView != null && GpuView.count == checked(count * viewCount) &&
                    ViewCount == viewCount && GpuSortKeys.count == count)
                    return true;

                DisposeBuffers();
                ViewCount = viewCount;
                GpuView = new GraphicsBuffer(GraphicsBuffer.Target.Structured, checked(count * viewCount), kGpuViewDataSize)
                {
                    name = "GaussianSplatViewData"
                };
                GpuSortDistances = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, 4)
                {
                    name = "GaussianSplatSortDistances"
                };
                GpuSortKeys = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, 4)
                {
                    name = "GaussianSplatSortIndices"
                };

                SorterArgs.inputKeys = GpuSortDistances;
                SorterArgs.inputValues = GpuSortKeys;
                SorterArgs.count = (uint)count;
                if (owner.m_Sorter.Valid)
                    SorterArgs.resources = GpuSorting.SupportResources.Load((uint)count);

                SortKeysInitialized = false;
                ResetValidity();
                return true;
            }

            internal void ResetValidity()
            {
                SortState = default;
                SortState.LastFrame = int.MinValue;
                HasViewSignature = false;
            }

            void DisposeBuffers()
            {
                ++VisibilityGeneration;
                PolicyFrame = -1;
                HasVisibilityEstimate = VisibilityReadbackPending = false;
                NextVisibilityProbeFrame = 0;
                RejectedSplats = 0;
                CompactGroups?.Dispose();
                CompactArgs?.Dispose();
                CompactGroups = null;
                CompactArgs = null;
                Compacted = false;
                GpuView?.Dispose();
                GpuSortDistances?.Dispose();
                GpuSortKeys?.Dispose();
                SorterArgs.resources.Dispose();
                GpuView = null;
                GpuSortDistances = null;
                GpuSortKeys = null;
                SorterArgs = default;
                SortKeysInitialized = false;
            }

            public void Dispose()
            {
                DisposeBuffers();
                DestroyImmediate(DirectMaterial);
            }
        }

        // Explicitly scoped to the separate benchmark diagnostic replay. No counter buffer
        // or instrumented shader is used by normal rendering / timing trials.
        Camera m_BenchmarkCamera;
        bool m_BenchmarkCaptureFrame;
        GraphicsBuffer m_BenchmarkCounts;
        int m_BenchmarkCountFrame = -1;
        // Diagnostic CSVs reserve slot 12 so counter columns keep stable indices.
        static readonly uint[] s_EmptyBenchmarkCounts = new uint[13];

        public void BeginBenchmarkDiagnostics(Camera camera)
        {
            if (!camera || camera.stereoEnabled) throw new ArgumentException("Diagnostics require a mono camera.");
            EndBenchmarkDiagnostics();
            m_BenchmarkCamera = camera;
            m_BenchmarkCounts = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 13, sizeof(uint));
            m_BenchmarkCounts.SetData(s_EmptyBenchmarkCounts);
        }

        public void SetBenchmarkCaptureFrame(bool capture)
        {
            m_BenchmarkCaptureFrame = capture;
            m_BenchmarkCountFrame = -1;
        }

        public void EndBenchmarkDiagnostics()
        {
            m_BenchmarkCamera = null;
            m_BenchmarkCaptureFrame = false;
            m_BenchmarkCounts?.Dispose();
            m_BenchmarkCounts = null;
            m_BenchmarkCountFrame = -1;
        }

        static ulong SumBenchmarkOutcomes(uint[] counts)
        {
            ulong sum = 0;
            for (int i = 1; i < counts.Length; i++) sum += counts[i];
            return sum;
        }

        public uint[] ReadBenchmarkDiagnostics(out uint sortPopulation, out uint submittedInstances)
        {
            if (m_BenchmarkCounts == null || m_BenchmarkCountFrame != Time.frameCount)
                throw new InvalidOperationException("No current-frame diagnostic dispatch for this renderer/camera.");
            var counts = new uint[13];
            m_BenchmarkCounts.GetData(counts); // Deliberately blocking; diagnostic pass only.
            if (counts[0] != m_SplatCount || SumBenchmarkOutcomes(counts) != counts[0])
                throw new InvalidOperationException("Diagnostic input/rejection counts are inconsistent.");
            sortPopulation = submittedInstances = (uint)m_SplatCount;
            if (m_CameraRenderResources[m_BenchmarkCamera].Compacted)
            {
                var resources = m_CameraRenderResources[m_BenchmarkCamera];
                var args = new uint[10];
                resources.CompactArgs.GetData(args);
                sortPopulation = args[0];
                submittedInstances = args[6];
            }
            return counts;
        }

        readonly Dictionary<Camera, CameraRenderResources> m_CameraRenderResources = new();
        readonly List<Camera> m_DestroyedCameraResources = new();
        GaussianSplat3DAsset m_PrevAsset;
        Hash128 m_PrevHash;
        bool m_Registered;
        bool m_PreviousDirectTransparentPath;
        int m_RenderDataVersion;

        static readonly ProfilerMarker s_ProfSort = new(ProfilerCategory.Render, "Gaussians.3D.Sort", MarkerFlags.SampleGPU);

        internal static class Props
        {
            public static readonly int ConvertGammaToLinear = Shader.PropertyToID("_ConvertGammaToLinear");
            public static readonly int AlphaCutoff = Shader.PropertyToID("_AlphaCutoff");
            public static readonly int OpacityAwareBounds = Shader.PropertyToID("_OpacityAwareBounds");
            public static readonly int SplatPos = Shader.PropertyToID("_SplatPos");
            public static readonly int SplatOther = Shader.PropertyToID("_SplatOther");
            public static readonly int SplatSH = Shader.PropertyToID("_SplatSH");
            public static readonly int SplatColor = Shader.PropertyToID("_SplatColor");
            public static readonly int SplatSelectedBits = Shader.PropertyToID("_SplatSelectedBits");
            public static readonly int SplatDeletedBits = Shader.PropertyToID("_SplatDeletedBits");
            public static readonly int SplatBitsValid = Shader.PropertyToID("_SplatBitsValid");
            public static readonly int SplatFormat = Shader.PropertyToID("_SplatFormat");
            public static readonly int SplatChunks = Shader.PropertyToID("_SplatChunks");
            public static readonly int SplatChunkCount = Shader.PropertyToID("_SplatChunkCount");
            public static readonly int SplatViewData = Shader.PropertyToID("_SplatViewData");
            public static readonly int OrderBuffer = Shader.PropertyToID("_OrderBuffer");
            public static readonly int SplatScale = Shader.PropertyToID("_SplatScale");
            public static readonly int SplatOpacityScale = Shader.PropertyToID("_SplatOpacityScale");
            public static readonly int SplatSize = Shader.PropertyToID("_SplatSize");
            public static readonly int SplatCount = Shader.PropertyToID("_SplatCount");
            public static readonly int SHOrder = Shader.PropertyToID("_SHOrder");
            public static readonly int SHOnly = Shader.PropertyToID("_SHOnly");
            public static readonly int DisplayIndex = Shader.PropertyToID("_DisplayIndex");
            public static readonly int DisplayChunks = Shader.PropertyToID("_DisplayChunks");
            public static readonly int GaussianSplatRT = Shader.PropertyToID("_GaussianSplatRT");
            public static readonly int SplatSortKeys = Shader.PropertyToID("_SplatSortKeys");
            public static readonly int SplatSortDistances = Shader.PropertyToID("_SplatSortDistances");
            public static readonly int SrcBuffer = Shader.PropertyToID("_SrcBuffer");
            public static readonly int DstBuffer = Shader.PropertyToID("_DstBuffer");
            public static readonly int BufferSize = Shader.PropertyToID("_BufferSize");
            public static readonly int ProjectionMatrix = Shader.PropertyToID("_ProjectionMatrix");
            public static readonly int ViewDataOffset = Shader.PropertyToID("_ViewDataOffset");
            public static readonly int SplatViewCount = Shader.PropertyToID("_SplatViewCount");
            public static readonly int SplatEyeIndex = Shader.PropertyToID("_SplatEyeIndex");
            public static readonly int MatrixMV = Shader.PropertyToID("_MatrixMV");
            public static readonly int MatrixMVP = Shader.PropertyToID("_MatrixMVP");
            public static readonly int MatrixObjectToWorld = Shader.PropertyToID("_MatrixObjectToWorld");
            public static readonly int MatrixWorldToObject = Shader.PropertyToID("_MatrixWorldToObject");
            public static readonly int VecScreenParams = Shader.PropertyToID("_VecScreenParams");
            public static readonly int VecWorldSpaceCameraPos = Shader.PropertyToID("_VecWorldSpaceCameraPos");
            public static readonly int MinimumSplatRadiusPixels = Shader.PropertyToID("_MinimumSplatRadiusPixels");
            public static readonly int SortDescending = Shader.PropertyToID("_SortDescending");
            public static readonly int SrcBlend = Shader.PropertyToID("_SrcBlend");
            public static readonly int DstBlend = Shader.PropertyToID("_DstBlend");
            public static readonly int CameraTargetTexture = Shader.PropertyToID("_CameraTargetTexture");
            public static readonly int SelectionCenter = Shader.PropertyToID("_SelectionCenter");
            public static readonly int SelectionDelta = Shader.PropertyToID("_SelectionDelta");
            public static readonly int SelectionDeltaRot = Shader.PropertyToID("_SelectionDeltaRot");
            public static readonly int SplatCutoutsCount = Shader.PropertyToID("_SplatCutoutsCount");
            public static readonly int SplatCutouts = Shader.PropertyToID("_SplatCutouts");
            public static readonly int SelectionMode = Shader.PropertyToID("_SelectionMode");
            public static readonly int SplatPosMouseDown = Shader.PropertyToID("_SplatPosMouseDown");
            public static readonly int SplatOtherMouseDown = Shader.PropertyToID("_SplatOtherMouseDown");
        }

        [field: NonSerialized] public bool editModified { get; private set; }
        [field: NonSerialized] public uint editSelectedSplats { get; private set; }
        [field: NonSerialized] public uint editDeletedSplats { get; private set; }
        [field: NonSerialized] public uint editCutSplats { get; private set; }
        [field: NonSerialized] public Bounds editSelectedBounds { get; private set; }

        public GaussianSplat3DAsset asset => m_Asset;
        public int splatCount => m_SplatCount;
        bool effectiveDirectConversion => usesDirectTransparentPath && m_ConvertGammaToLinear && QualitySettings.activeColorSpace == ColorSpace.Linear;
        internal bool usesDirectTransparentPath =>
            m_RenderPath == RenderPath.DirectTransparent && m_RenderMode == RenderMode.Splats;

        enum KernelIndices
        {
            SetIndices,
            CalcDistances,
            CalcViewData,
            UpdateEditData,
            InitEditData,
            ClearBuffer,
            InvertSelection,
            SelectAll,
            OrBuffers,
            SelectionUpdate,
            TranslateSelection,
            RotateSelection,
            ScaleSelection,
            ExportData,
            CopySplats,
        }

        public bool HasValidAsset =>
            m_Asset != null &&
            m_Asset.splatCount > 0 &&
            m_Asset.formatVersion == GaussianSplat3DAsset.kCurrentVersion &&
            (m_Asset.hasValidFloatData || (m_Asset.posData != null &&
            m_Asset.otherData != null &&
            m_Asset.shData != null &&
            m_Asset.colorData != null));
        public bool HasValidRenderSetup => m_GpuPosData != null && m_GpuOtherData != null && m_GpuChunks != null;

        const int kGpuViewDataSize = 40;

        void CreateResourcesForAsset()
        {
            if (!HasValidAsset)
                return;

            m_SplatCount = asset.splatCount;
            m_FloatData = new GaussianSplat3DData(asset.isFloatSource ? m_SplatCount : 1,
                asset.isFloatSource ? (asset.floatSHDegree + 1) * (asset.floatSHDegree + 1) : 1, asset.floatSHStorage);
            if (asset.isFloatSource)
            {
                m_FloatData.Splats.SetData(asset.floatSplats.GetData<Vector4>());
                m_FloatData.SH.SetData(asset.floatSH.GetData<uint>());
                // Bind valid dummies for the packed branch; no packed copy of the cloud.
                m_GpuPosData = new GraphicsBuffer(GraphicsBuffer.Target.Raw, 4, 4);
                m_GpuOtherData = new GraphicsBuffer(GraphicsBuffer.Target.Raw, 4, 4);
                m_GpuSHData = new GraphicsBuffer(GraphicsBuffer.Target.Raw, 48, 4);
                m_GpuColorData = Texture2D.blackTexture;
            }
            else
            {
                m_GpuPosData = new GraphicsBuffer(GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopySource, (int) (asset.posData.dataSize / 4), 4) { name = "GaussianPosData" };
                m_GpuPosData.SetData(asset.posData.GetData<uint>());
                m_GpuOtherData = new GraphicsBuffer(GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopySource, (int) (asset.otherData.dataSize / 4), 4) { name = "GaussianOtherData" };
                m_GpuOtherData.SetData(asset.otherData.GetData<uint>());
                m_GpuSHData = new GraphicsBuffer(GraphicsBuffer.Target.Raw, (int) (asset.shData.dataSize / 4), 4) { name = "GaussianSHData" };
                m_GpuSHData.SetData(asset.shData.GetData<uint>());
                var (texWidth, texHeight) = GaussianSplat3DAsset.CalcTextureSize(asset.splatCount);
                var texFormat = GaussianSplat3DAsset.ColorFormatToGraphics(asset.colorFormat);
                var tex = new Texture2D(texWidth, texHeight, texFormat, TextureCreationFlags.DontInitializePixels | TextureCreationFlags.IgnoreMipmapLimit | TextureCreationFlags.DontUploadUponCreate) { name = "GaussianColorData" };
                tex.SetPixelData(asset.colorData.GetData<byte>(), 0);
                tex.Apply(false, true);
                m_GpuColorData = tex;
            }
            if (asset.chunkData != null && asset.chunkData.dataSize != 0)
            {
                m_GpuChunks = new GraphicsBuffer(GraphicsBuffer.Target.Structured,
                    (int) (asset.chunkData.dataSize / UnsafeUtility.SizeOf<GaussianSplat3DAsset.ChunkInfo>()),
                    UnsafeUtility.SizeOf<GaussianSplat3DAsset.ChunkInfo>()) {name = "GaussianChunkData"};
                m_GpuChunks.SetData(asset.chunkData.GetData<GaussianSplat3DAsset.ChunkInfo>());
                m_GpuChunksValid = true;
            }
            else
            {
                // Bind a valid buffer even when the unchunked shader branch does not read it.
                m_GpuChunks = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1,
                    UnsafeUtility.SizeOf<GaussianSplat3DAsset.ChunkInfo>()) {name = "GaussianChunkData"};
                m_GpuChunksValid = false;
            }

            m_GpuIndexBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Index, 36, 2);
            // cube indices, most often we use only the first quad
            m_GpuIndexBuffer.SetData(new ushort[]
            {
                0, 1, 2, 1, 3, 2,
                4, 6, 5, 5, 6, 7,
                0, 2, 4, 4, 2, 6,
                1, 5, 3, 5, 7, 3,
                0, 4, 1, 4, 5, 1,
                2, 3, 6, 3, 7, 6
            });
        }

        CameraRenderResources GetOrCreateCameraRenderResources(Camera cam)
        {
            if (cam == null)
                return null;

            if (!m_CameraRenderResources.TryGetValue(cam, out CameraRenderResources resources))
            {
                resources = new CameraRenderResources();
                m_CameraRenderResources.Add(cam, resources);
            }
            return resources;
        }

        internal CameraRenderResources GetCameraRenderResources(Camera cam)
        {
            CameraRenderResources resources = GetOrCreateCameraRenderResources(cam);
            if (resources == null)
                return null;

            EnsureSorterAndRegister();
            return resources.EnsureBuffers(this, cam) ? resources : null;
        }

        void DisposeCameraRenderResources()
        {
            foreach (CameraRenderResources resources in m_CameraRenderResources.Values)
                resources.Dispose();
            m_CameraRenderResources.Clear();
            m_DestroyedCameraResources.Clear();
        }

        void ReleaseDestroyedCameraRenderResources()
        {
            m_DestroyedCameraResources.Clear();
            foreach (var kvp in m_CameraRenderResources)
            {
                if (!kvp.Key || Time.frameCount - kvp.Value.LastUsedFrame > 120)
                    m_DestroyedCameraResources.Add(kvp.Key);
            }

            foreach (Camera cam in m_DestroyedCameraResources)
            {
                m_CameraRenderResources[cam].Dispose();
                m_CameraRenderResources.Remove(cam);
            }
            m_DestroyedCameraResources.Clear();
        }

        bool resourcesAreSetUp => m_ShaderSplats != null && m_ShaderComposite != null && m_ShaderDebugPoints != null &&
                                  m_ShaderDebugBoxes != null && m_CSSplatUtilities != null && SystemInfo.supportsComputeShaders;

        public void EnsureMaterials()
        {
            if (m_MatSplats == null && resourcesAreSetUp)
            {
                m_MatSplats = new Material(m_ShaderSplats)
                {
                    name = "GaussianSplats3D",
                    enableInstancing = true,
                };
                m_MatSplats.SetInt(Props.SrcBlend, (int)BlendMode.OneMinusDstAlpha);
                m_MatSplats.SetInt(Props.DstBlend, (int)BlendMode.One);

                m_MatSplatsDirect = new Material(m_ShaderSplats)
                {
                    name = "GaussianSplats3DDirectTransparent",
                    enableInstancing = true,
                };
                m_MatSplatsDirect.SetInt(Props.SrcBlend, (int)BlendMode.One);
                m_MatSplatsDirect.SetInt(Props.DstBlend, (int)BlendMode.OneMinusSrcAlpha);
                m_MatSplatsDirect.EnableKeyword("GAUSSIANS_DIRECT_TRANSPARENT");
                m_MatComposite = new Material(m_ShaderComposite)
                {
                    name = "GaussianSplat3DComposite",
                    enableInstancing = true,
                };
                m_MatComposite.SetInteger("_ConvertCompositeGammaToLinear", 1);
                m_MatCompositeRaw = new Material(m_MatComposite);
                m_MatCompositeRaw.SetInteger("_ConvertCompositeGammaToLinear", 0);
                m_MatDebugPoints = new Material(m_ShaderDebugPoints) {name = "GaussianSplat3DDebugPoints", enableInstancing = true};
                m_MatDebugBoxes = new Material(m_ShaderDebugBoxes) {name = "GaussianSplat3DDebugBoxes", enableInstancing = true};
            }
        }

        void EnsureDirectQuadMesh()
        {
            if (m_DirectQuadMesh != null)
                return;

            m_DirectQuadMesh = new Mesh
            {
                name = "GaussianSplat3DDirectQuad",
                hideFlags = HideFlags.HideAndDontSave,
            };
            m_DirectQuadMesh.vertices = new[]
            {
                Vector3.zero,
                Vector3.zero,
                Vector3.zero,
                Vector3.zero,
            };
            m_DirectQuadMesh.SetIndices(new[] {0, 1, 2, 1, 3, 2}, MeshTopology.Triangles, 0, false);
            m_DirectQuadMesh.UploadMeshData(true);
        }

        public void EnsureSorterAndRegister()
        {
            if (m_Sorter == null && resourcesAreSetUp)
            {
                m_Sorter = new GpuSorting(m_CSSplatUtilities);
            }

            if (!m_Registered && resourcesAreSetUp)
            {
                GaussianSplat3DRenderSystem.instance.RegisterSplat(this);
                m_Registered = true;
            }
        }

        public void OnEnable()
        {
            ResolveShaders();
#if UNITY_EDITOR
            UnityEditor.Undo.undoRedoPerformed += OnUndoRedo;
#endif
            ResetStereoFrameCaches();
            m_PreviousDirectTransparentPath = usesDirectTransparentPath;
            if (!resourcesAreSetUp)
                return;

            EnsureMaterials();
            EnsureSorterAndRegister();

            CreateResourcesForAsset();
        }

        void SetAssetDataOnCS(CommandBuffer cmb, KernelIndices kernel, CameraRenderResources cameraResources = null)
        {
            ComputeShader cs = m_CSSplatUtilities;
            int kernelIndex = (int) kernel;
            BindSource(cmb, cs, kernelIndex);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatPos, m_GpuPosData);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatChunks, m_GpuChunks);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatOther, m_GpuOtherData);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatSH, m_GpuSHData);
            cmb.SetComputeTextureParam(cs, kernelIndex, Props.SplatColor, m_GpuColorData);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatSelectedBits, m_GpuEditSelected ?? m_GpuPosData);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatDeletedBits, m_GpuEditDeleted ?? m_GpuPosData);
            if (cameraResources != null)
            {
                cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatViewData, cameraResources.GpuView);
                cmb.SetComputeBufferParam(cs, kernelIndex, Props.OrderBuffer, cameraResources.GpuSortKeys);
            }

            cmb.SetComputeIntParam(cs, Props.SplatBitsValid, m_GpuEditSelected != null && m_GpuEditDeleted != null ? 1 : 0);
            uint format = (uint)m_Asset.posFormat | ((uint)m_Asset.scaleFormat << 8) | ((uint)m_Asset.shFormat << 16);
            cmb.SetComputeIntParam(cs, Props.SplatFormat, (int)format);
            cmb.SetComputeIntParam(cs, Props.SplatCount, m_SplatCount);
            cmb.SetComputeIntParam(cs, Props.SplatChunkCount, m_GpuChunksValid ? m_GpuChunks.count : 0);

            UpdateCutoutsBuffer();
            cmb.SetComputeIntParam(cs, Props.SplatCutoutsCount, m_Cutouts?.Length ?? 0);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatCutouts, m_GpuEditCutouts);
        }

        internal void SetAssetDataOnMaterial(MaterialPropertyBlock mat)
        {
            BindSource(mat);
            mat.SetFloat(Props.AlphaCutoff, Mathf.Clamp01(m_AlphaCutoff));
            mat.SetInt(Props.OpacityAwareBounds, m_OpacityAwareBounds ? 1 : 0);
            mat.SetInt(Props.ConvertGammaToLinear, effectiveDirectConversion ? 1 : 0);
            mat.SetFloat(Props.MinimumSplatRadiusPixels, (m_EarlyRejection ? Mathf.Max(0, m_MinimumSplatRadiusPixels) : 0));
            mat.SetInt(Props.SortDescending, usesDirectTransparentPath ? 1 : 0);
            mat.SetMatrix(Props.MatrixObjectToWorld, transform.localToWorldMatrix);
            mat.SetBuffer(Props.SplatPos, m_GpuPosData);
            mat.SetBuffer(Props.SplatOther, m_GpuOtherData);
            mat.SetBuffer(Props.SplatSH, m_GpuSHData);
            mat.SetTexture(Props.SplatColor, m_GpuColorData);
            mat.SetBuffer(Props.SplatSelectedBits, m_GpuEditSelected ?? m_GpuPosData);
            mat.SetBuffer(Props.SplatDeletedBits, m_GpuEditDeleted ?? m_GpuPosData);
            mat.SetInt(Props.SplatBitsValid, m_GpuEditSelected != null && m_GpuEditDeleted != null ? 1 : 0);
            uint format = (uint)m_Asset.posFormat | ((uint)m_Asset.scaleFormat << 8) | ((uint)m_Asset.shFormat << 16);
            mat.SetInteger(Props.SplatFormat, (int)format);
            mat.SetInteger(Props.SplatCount, m_SplatCount);
            mat.SetInteger(Props.SplatChunkCount, m_GpuChunksValid ? m_GpuChunks.count : 0);
        }

        void UpdateDirectMaterialProperties(CameraRenderResources cameraResources)
        {
            MaterialPropertyBlock properties = cameraResources.DirectMaterialProperties;
            properties.Clear();
            SetAssetDataOnMaterial(properties);
            properties.SetBuffer(Props.SplatChunks, m_GpuChunks);
            properties.SetBuffer(Props.SplatViewData, cameraResources.GpuView);
            properties.SetBuffer(Props.OrderBuffer, cameraResources.GpuSortKeys);
            properties.SetFloat(Props.SplatScale, m_SplatScale);
            properties.SetFloat(Props.SplatOpacityScale, m_OpacityScale);
            properties.SetInteger(Props.SHOrder, m_SHOrder);
            properties.SetInteger(Props.SHOnly, m_SHOnly ? 1 : 0);
            properties.SetMatrix(Props.MatrixObjectToWorld, transform.localToWorldMatrix);
        }

        static Bounds TransformBounds(Matrix4x4 matrix, Bounds bounds)
        {
            Vector3 center = matrix.MultiplyPoint3x4(bounds.center);
            Vector3 extents = bounds.extents;
            Vector3 worldExtents = new(
                Mathf.Abs(matrix.m00) * extents.x + Mathf.Abs(matrix.m01) * extents.y + Mathf.Abs(matrix.m02) * extents.z,
                Mathf.Abs(matrix.m10) * extents.x + Mathf.Abs(matrix.m11) * extents.y + Mathf.Abs(matrix.m12) * extents.z,
                Mathf.Abs(matrix.m20) * extents.x + Mathf.Abs(matrix.m21) * extents.y + Mathf.Abs(matrix.m22) * extents.z);
            return new Bounds(center, worldExtents * 2.0f);
        }

        internal void QueueDirectTransparentDraw(Camera cam)
        {
            if (cam == null || !usesDirectTransparentPath || !HasValidAsset || !HasValidRenderSetup)
                return;

            EnsureMaterials();
            EnsureDirectQuadMesh();
            CameraRenderResources cameraResources = GetCameraRenderResources(cam);
            if (m_MatSplatsDirect == null || m_DirectQuadMesh == null || cameraResources == null)
                return;

            UpdateDirectMaterialProperties(cameraResources);
            SetCameraProperties(cameraResources.DirectMaterialProperties, cam, cameraResources);
            cameraResources.DirectMaterial ??= new Material(m_MatSplatsDirect);
            cameraResources.DirectMaterial.renderQueue = (int)RenderQueue.Transparent + Mathf.Clamp(m_RenderOrder, -499, 500);

            Vector3 boundsMin = asset.boundsIncludeSplatExtents ? asset.renderBoundsMin : asset.boundsMin;
            Vector3 boundsMax = asset.boundsIncludeSplatExtents ? asset.renderBoundsMax : asset.boundsMax;
            Bounds localBounds = new((boundsMin + boundsMax) * 0.5f, boundsMax - boundsMin);
            Bounds worldBounds = TransformBounds(transform.localToWorldMatrix, localBounds);
            if (m_SourceFrame.Data != null || !asset.boundsIncludeSplatExtents || editModified || Mathf.Abs(m_SplatScale) > 2.0f)
            {
                // Assets created before splat-extent bounds were introduced only contain
                // center bounds. Keep the original center (used by transparent sorting), but
                // expand symmetrically until the current camera is inside. This guarantees the
                // old asset is not frustum-culled; recreating it restores tight object culling.
                Vector3 cameraOffset = cam.transform.position - worldBounds.center;
                cameraOffset = new Vector3(Mathf.Abs(cameraOffset.x), Mathf.Abs(cameraOffset.y), Mathf.Abs(cameraOffset.z));
                worldBounds.extents = Vector3.Max(worldBounds.extents, cameraOffset + Vector3.one);
                Vector3 interior = cam.ViewportToWorldPoint(new Vector3(0.5f, 0.5f,
                    Mathf.Lerp(cam.nearClipPlane, cam.farClipPlane, 0.5f))) - worldBounds.center;
                worldBounds.extents = Vector3.Max(worldBounds.extents,
                    new Vector3(Mathf.Abs(interior.x), Mathf.Abs(interior.y), Mathf.Abs(interior.z)) + Vector3.one);
            }
            // Covariance filtering adds a pixel footprint beyond the baked ellipsoid.
            float pixelMargin = cam.orthographic ? cam.orthographicSize * 2 / Mathf.Max(1, cam.pixelHeight) :
                2 * (Vector3.Distance(cam.transform.position, worldBounds.center) + worldBounds.extents.magnitude) *
                Mathf.Tan(cam.fieldOfView * Mathf.Deg2Rad * 0.5f) / Mathf.Max(1, cam.pixelHeight);
            worldBounds.Expand(Mathf.Max(0, pixelMargin) * 8);
            var renderParams = new RenderParams(cameraResources.DirectMaterial)
            {
                worldBounds = worldBounds,
                matProps = cameraResources.DirectMaterialProperties,
                layer = gameObject.layer,
                camera = cam,
            };
            if (UseCompaction(cameraResources))
                Graphics.DrawProceduralIndirect(cameraResources.DirectMaterial, worldBounds, MeshTopology.Triangles,
                    m_GpuIndexBuffer, GetIndirectArgs(cameraResources), 20, cam,
                    cameraResources.DirectMaterialProperties, ShadowCastingMode.Off, false, gameObject.layer);
            else
                Graphics.RenderMeshPrimitives(renderParams, m_DirectQuadMesh, 0, splatCount);
        }

        static void DisposeBuffer(ref GraphicsBuffer buf)
        {
            buf?.Dispose();
            buf = null;
        }

        void DisposeResourcesForAsset()
        {
            if (m_GpuColorData != Texture2D.blackTexture) DestroyImmediate(m_GpuColorData);
            m_GpuColorData = null;
            m_FloatData?.Dispose(); m_FloatData = null;
            m_SourceFrame = default;

            DisposeBuffer(ref m_GpuPosData);
            DisposeBuffer(ref m_GpuOtherData);
            DisposeBuffer(ref m_GpuSHData);
            DisposeBuffer(ref m_GpuChunks);

            DisposeBuffer(ref m_GpuIndexBuffer);
            DisposeCameraRenderResources();

            DisposeBuffer(ref m_GpuEditSelectedMouseDown);
            DisposeBuffer(ref m_GpuEditPosMouseDown);
            DisposeBuffer(ref m_GpuEditOtherMouseDown);
            DisposeBuffer(ref m_GpuEditSelected);
            DisposeBuffer(ref m_GpuEditDeleted);
            DisposeBuffer(ref m_GpuEditCountsBounds);
            DisposeBuffer(ref m_GpuEditCutouts);

            m_SplatCount = 0;
            m_GpuChunksValid = false;

            editSelectedSplats = 0;
            editDeletedSplats = 0;
            editCutSplats = 0;
            editModified = false;
            editSelectedBounds = default;
        }

        void OnUndoRedo() { ++m_RenderDataVersion; ResetStereoFrameCaches(); }

        public void OnDisable()
        {
            EndBenchmarkDiagnostics();
#if UNITY_EDITOR
            UnityEditor.Undo.undoRedoPerformed -= OnUndoRedo;
#endif
            ResetStereoFrameCaches();
            DisposeResourcesForAsset();
            GaussianSplat3DRenderSystem.instance.UnregisterSplat(this);
            m_Registered = false;
            m_Sorter = null;

            DestroyImmediate(m_MatSplats);
            DestroyImmediate(m_MatSplatsDirect);
            DestroyImmediate(m_MatComposite);
            DestroyImmediate(m_MatCompositeRaw);
            DestroyImmediate(m_MatDebugPoints);
            DestroyImmediate(m_MatDebugBoxes);
            DestroyImmediate(m_DirectQuadMesh);
        }

        static Matrix4x4 AverageMatrices(Matrix4x4 a, Matrix4x4 b)
        {
            Matrix4x4 result = default;
            for (int row = 0; row < 4; ++row)
            {
                for (int column = 0; column < 4; ++column)
                    result[row, column] = (a[row, column] + b[row, column]) * 0.5f;
            }
            return result;
        }

        // Stereo eye views share their orientation in Unity XR. Averaging their view matrices
        // therefore places the shared sort/preparation camera at the exact eye midpoint while
        // preserving Unity's camera-space handedness.
        static Matrix4x4 CalculateCenterEyeView(Matrix4x4 leftView, Matrix4x4 rightView)
        {
            return AverageMatrices(leftView, rightView);
        }

        static Matrix4x4 CalculateCenterEyeProjection(Matrix4x4 leftProjection, Matrix4x4 rightProjection)
        {
            return AverageMatrices(leftProjection, rightProjection);
        }

        static bool GetSharedStereoMatrices(Camera cam, out Matrix4x4 view, out Matrix4x4 projection)
        {
            var state = GaussianSplatCameraState.ForCamera(cam);
            view = state.ViewCount > 1 ? CalculateCenterEyeView(state.View, state.RightView) : state.View;
            projection = state.ViewCount > 1 ? CalculateCenterEyeProjection(state.Projection, state.RightProjection) : state.Projection;
            return state.ViewCount > 1;
        }

        void ResetStereoFrameCaches()
        {
            foreach (CameraRenderResources resources in m_CameraRenderResources.Values)
                resources.ResetValidity();
        }

        int CalculateCutoutHash(Matrix4x4 rendererMatrix)
        {
            unchecked
            {
                int hash = 17;
                int count = m_Cutouts?.Length ?? 0;
                hash = hash * 31 + count;
                for (int i = 0; i < count; ++i)
                {
                    GaussianCutout.ShaderData data =
                        GaussianCutout.GetShaderData(m_Cutouts[i], rendererMatrix);
                    hash = hash * 31 + data.matrix.GetHashCode();
                    hash = hash * 31 + data.typeAndFlags.GetHashCode();
                }
                return hash;
            }
        }

        ViewSignature CreateViewSignature(Camera cam)
        {
            Matrix4x4 objectToWorld = transform.localToWorldMatrix;
            var state = GaussianSplatCameraState.ForCamera(cam);
            return new ViewSignature
            {
                View = state.View,
                RightView = state.RightView,
                RightProjection = state.RightProjection,
                ViewCount = state.ViewCount,
                IndirectInstanceMultiplier = state.IndirectInstanceMultiplier,
                RightScreenSize = state.RightScreenSize,
                ConvertColor = effectiveDirectConversion,
                Projection = state.Projection,
                ObjectToWorld = objectToWorld,
                ScreenWidth = state.ScreenSize.x,
                ScreenHeight = state.ScreenSize.y,
                SplatScale = m_SplatScale,
                OpacityScale = m_OpacityScale,
                SHOrder = m_SHOrder,
                SHOnly = m_SHOnly,
                MinimumSplatRadiusPixels = (m_EarlyRejection ? Mathf.Max(0.0f, m_MinimumSplatRadiusPixels) : 0),
                CutoutHash = CalculateCutoutHash(objectToWorld),
                RenderDataVersion = m_RenderDataVersion,
                AttributeRevision = m_SourceFrame.AttributeRevision,
                NearClip = cam.nearClipPlane,
                MinimumDistance = (m_EarlyRejection ? Mathf.Max(0, m_MinimumSplatDistance) : 0),
                MinimumOpacity = MinimumOpacity,
                DeferredSHLoading = UseDeferredSH(GetOrCreateCameraRenderResources(cam)),
                EarlyFrustumCulling = m_EarlyFrustumCulling,
                OpacityAwareBounds = m_OpacityAwareBounds, AlphaCutoff = Mathf.Clamp01(m_AlphaCutoff),
            };
        }

        internal bool ShouldSortForCamera(Camera cam, bool backToFront)
        {
            return ShouldSortForCameraAtFrame(cam, backToFront, Time.frameCount);
        }

        bool ShouldSortForCameraAtFrame(Camera cam, bool backToFront, int frame)
        {
            CameraRenderResources resources = GetOrCreateCameraRenderResources(cam);
            if (resources == null)
                return false;

            GetSharedStereoMatrices(cam, out Matrix4x4 view, out _);
            Matrix4x4 matrixMV = view * transform.localToWorldMatrix;
            CameraSortState state = resources.SortState;
            bool settingsChanged = !state.HasSignature ||
                                   state.BackToFront != backToFront ||
                                   state.RenderDataVersion != m_RenderDataVersion ||
                                   state.PositionRevision != m_SourceFrame.PositionRevision ||
                                   state.SortKeyBits != sortKeyBits ||
                                   state.SortNthFrame != m_SortNthFrame;
            bool matrixChanged = !state.HasSignature || !state.MatrixMV.Equals(matrixMV);
            bool renderedThisFrame = state.HasSignature && state.LastFrame == frame;

            // Single-pass stereo shares a center-eye order. Multipass uses the current
            // XR pass view, so changed eye matrices trigger a new order in the same frame.
            // Ordinary movement across frames still honors the configured sort cadence.
            if (!settingsChanged && !matrixChanged && renderedThisFrame)
                return false;

            if (settingsChanged)
                state.RenderCount = 0;

            bool shouldSort = settingsChanged ||
                              (matrixChanged && renderedThisFrame) ||
                              state.RenderCount % Mathf.Max(1, m_SortNthFrame) == 0;
            state.LastFrame = frame;
            state.RenderCount++;
            state.BackToFront = backToFront;
            state.HasSignature = true;
            state.MatrixMV = matrixMV;
            state.RenderDataVersion = m_RenderDataVersion;
            state.PositionRevision = m_SourceFrame.PositionRevision;
            state.SortNthFrame = m_SortNthFrame;
            state.SortKeyBits = sortKeyBits;
            resources.SortState = state;
            return shouldSort;
        }

        internal bool ShouldPrepareViewForCamera(Camera cam)
        {
            CameraRenderResources resources = GetOrCreateCameraRenderResources(cam);
            if (resources == null)
                return false;

            ViewSignature signature = CreateViewSignature(cam);
            if (!(cam == m_BenchmarkCamera && m_BenchmarkCaptureFrame) && resources.HasViewSignature && resources.ViewSignature.Equals(signature))
                return false;

            resources.ViewSignature = signature;
            resources.HasViewSignature = true;
            return true;
        }

        internal void CalcViewData(CommandBuffer cmb, Camera cam, CameraRenderResources resources)
        {
            if (cam.cameraType == CameraType.Preview) return;
            bool diagnostics = cam == m_BenchmarkCamera && m_BenchmarkCaptureFrame && m_BenchmarkCounts != null;
            int kernel = diagnostics ? m_CSSplatUtilities.FindKernel("CSCalcViewDataDiagnostics") : (int)KernelIndices.CalcViewData;
            SetAssetDataOnCS(cmb, (KernelIndices)kernel, resources);
            if (diagnostics)
            {
                if (resources.ViewCount != 1) throw new InvalidOperationException("Diagnostics require mono rendering.");
                cmb.SetBufferData(m_BenchmarkCounts, s_EmptyBenchmarkCounts);
                cmb.SetComputeBufferParam(m_CSSplatUtilities, kernel, "_BenchmarkCounts", m_BenchmarkCounts);
                m_BenchmarkCountFrame = Time.frameCount;
            }
            var objectToWorld = transform.localToWorldMatrix;
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixObjectToWorld, objectToWorld);
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixWorldToObject, transform.worldToLocalMatrix);
            var state = GaussianSplatCameraState.ForCamera(cam);
            cmb.SetComputeIntParam(m_CSSplatUtilities, "_DeferredSHLoading", UseDeferredSH(resources) ? 1 : 0);
            cmb.SetComputeIntParam(m_CSSplatUtilities, "_EarlyFrustumCulling", m_EarlyFrustumCulling ? 1 : 0);
            cmb.SetComputeIntParam(m_CSSplatUtilities, Props.OpacityAwareBounds, m_OpacityAwareBounds ? 1 : 0);
            cmb.SetComputeFloatParam(m_CSSplatUtilities, Props.AlphaCutoff, Mathf.Clamp01(m_AlphaCutoff));
            cmb.SetComputeFloatParam(m_CSSplatUtilities, "_SplatNearClip", cam.nearClipPlane);
            float minimumDistance = m_EarlyRejection ? Mathf.Max(0, m_MinimumSplatDistance) : 0;
            // One center-depth check on the GPU, before covariance and frustum work.
            cmb.SetComputeFloatParam(m_CSSplatUtilities, "_SplatCullDistance", Mathf.Max(1.0e-6f, Mathf.Max(cam.nearClipPlane, minimumDistance)));
            cmb.SetComputeFloatParam(m_CSSplatUtilities, "_MinimumSplatOpacity", MinimumOpacity);
            cmb.SetComputeFloatParam(m_CSSplatUtilities, Props.SplatScale, m_SplatScale);
            cmb.SetComputeFloatParam(m_CSSplatUtilities, Props.SplatOpacityScale, m_OpacityScale);
            cmb.SetComputeIntParam(m_CSSplatUtilities, Props.SHOrder, m_SHOrder);
            cmb.SetComputeIntParam(m_CSSplatUtilities, Props.SHOnly, m_SHOnly ? 1 : 0);
            cmb.SetComputeFloatParam(m_CSSplatUtilities, Props.MinimumSplatRadiusPixels, (m_EarlyRejection ? Mathf.Max(0, m_MinimumSplatRadiusPixels) : 0));
            m_CSSplatUtilities.GetKernelThreadGroupSizes(kernel, out uint groupSize, out _, out _);
            for (int eye = 0; eye < resources.ViewCount; ++eye)
            {
                var view = eye == 0 ? state.View : state.RightView;
                var projection = eye == 0 ? state.Projection : state.RightProjection;
                var size = eye == 0 ? state.ScreenSize : state.RightScreenSize;
                cmb.SetComputeVectorParam(m_CSSplatUtilities, Props.VecScreenParams, new Vector4(size.x, size.y, 0, 0));
                projection = GL.GetGPUProjectionMatrix(projection, true);
                cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixMV, view * objectToWorld);
                cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixMVP, projection * view * objectToWorld);
                cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.ProjectionMatrix, projection);
                cmb.SetComputeVectorParam(m_CSSplatUtilities, Props.VecWorldSpaceCameraPos, view.inverse.GetColumn(3));
                cmb.SetComputeIntParam(m_CSSplatUtilities, Props.ViewDataOffset, eye * m_SplatCount);
                cmb.DispatchCompute(m_CSSplatUtilities, kernel, (m_SplatCount + (int)groupSize - 1) / (int)groupSize, 1, 1);
            }
        }

        internal void SetCameraProperties(MaterialPropertyBlock properties, Camera camera, CameraRenderResources resources)
        {
            properties.SetInt(Props.SplatViewCount, resources.ViewCount);
            properties.SetInt(Props.SplatEyeIndex, GaussianSplatCameraState.ForCamera(camera).EyeIndex);
        }

        internal GraphicsBuffer GetIndirectArgs(CameraRenderResources resources)
        {
            if (!UseCompaction(resources)) return null;
            resources.EnsureCompactionBuffers();
            return resources.CompactArgs;
        }

        internal void PrepareCamera(CommandBuffer cmd, Camera camera, bool backToFront, CameraRenderResources resources)
        {
            bool prepare = ShouldPrepareViewForCamera(camera);
            bool sort = ShouldSortForCamera(camera, backToFront);
            if (UseCompaction(resources))
            {
                if (prepare)
                {
                    cmd.BeginSample(GaussianSplat3DRenderSystem.s_ProfCalcView);
                    CalcViewData(cmd, camera, resources);
                    cmd.EndSample(GaussianSplat3DRenderSystem.s_ProfCalcView);
                }
                // Rebuilding IDs invalidates the previous order, including on skipped cadence frames.
                if (prepare || sort || !resources.Compacted)
                    CompactAndSort(cmd, camera, backToFront, resources);
                resources.Compacted = true;
            }
            else
            {
                if (resources.Compacted)
                {
                    resources.SortKeysInitialized = false;
                    resources.Compacted = false;
                    sort = true;
                }
                if (sort) SortPoints(cmd, camera, transform.localToWorldMatrix, backToFront, resources);
                if (prepare)
                {
                    cmd.BeginSample(GaussianSplat3DRenderSystem.s_ProfCalcView);
                    CalcViewData(cmd, camera, resources);
                    cmd.EndSample(GaussianSplat3DRenderSystem.s_ProfCalcView);
                }
            }
            ProbeVisibility(cmd, camera, resources);
        }

        void CountVisible(CommandBuffer cmd, Camera camera, CameraRenderResources resources)
        {
            resources.EnsureCompactionBuffers();
            int countKernel = m_CSSplatUtilities.FindKernel("CSCountVisibleGroups");
            int scanKernel = m_CSSplatUtilities.FindKernel("CSScanVisibleGroups");
            int groups = resources.CompactGroups.count;
            cmd.SetComputeIntParam(m_CSSplatUtilities, Props.SplatCount, m_SplatCount);
            cmd.SetComputeIntParam(m_CSSplatUtilities, "_CompactViewCount", resources.ViewCount);
            cmd.SetComputeIntParam(m_CSSplatUtilities, "_CompactGroupCount", groups);
            // Unity doubles ordinary procedural instance counts for SPI; indirect counts are GPU-owned.
            int instances = GaussianSplatCameraState.ForCamera(camera).IndirectInstanceMultiplier;
            cmd.SetComputeIntParam(m_CSSplatUtilities, "_CompactInstanceMultiplier", instances);
            cmd.SetComputeBufferParam(m_CSSplatUtilities, countKernel, Props.SplatViewData, resources.GpuView);
            cmd.SetComputeBufferParam(m_CSSplatUtilities, countKernel, "_CompactGroups", resources.CompactGroups);
            cmd.DispatchCompute(m_CSSplatUtilities, countKernel, groups, 1, 1);
            cmd.SetComputeBufferParam(m_CSSplatUtilities, scanKernel, "_CompactGroups", resources.CompactGroups);
            cmd.SetComputeBufferParam(m_CSSplatUtilities, scanKernel, "_CompactArgs", resources.CompactArgs);
            cmd.DispatchCompute(m_CSSplatUtilities, scanKernel, 1, 1, 1);

        }

        void ProbeVisibility(CommandBuffer cmd, Camera camera, CameraRenderResources resources)
        {
            if (m_OptimizationOverrides || m_CompactionThreshold <= 0 || m_CompactionThreshold > m_SplatCount ||
                !SystemInfo.supportsAsyncGPUReadback || resources.VisibilityReadbackPending ||
                Time.frameCount < resources.NextVisibilityProbeFrame || camera.cameraType == CameraType.Preview) return;
            // Compacted frames already have a current union count; otherwise sample at low cadence.
            if (!resources.Compacted) CountVisible(cmd, camera, resources);
            resources.VisibilityReadbackPending = true;
            resources.NextVisibilityProbeFrame = Time.frameCount + 16;
            int generation = resources.VisibilityGeneration;
            uint population = (uint)m_SplatCount;
            cmd.RequestAsyncReadback(resources.CompactArgs, sizeof(uint), 0, request =>
            {
                // The camera/asset may have been destroyed or resized while this request was in flight.
                if (resources.VisibilityGeneration != generation) return;
                resources.VisibilityReadbackPending = false;
                if (request.hasError) return;
                uint visible = request.GetData<uint>()[0];
                resources.RejectedSplats = population - Math.Min(population, visible);
                resources.HasVisibilityEstimate = true;
            });
        }

        internal void CompactAndSort(CommandBuffer cmd, Camera camera, bool backToFront, CameraRenderResources resources)
        {
            resources.EnsureCompactionBuffers();
            cmd.BeginSample("GaussianSplat.CompactVisible");
            CountVisible(cmd, camera, resources);
            int scatterKernel = m_CSSplatUtilities.FindKernel("CSCompactVisible");
            int groups = resources.CompactGroups.count;

            GetSharedStereoMatrices(camera, out Matrix4x4 view, out _);
            view.m20 *= -1; view.m21 *= -1; view.m22 *= -1;
            BindSource(cmd, m_CSSplatUtilities, scatterKernel);
            cmd.SetComputeBufferParam(m_CSSplatUtilities, scatterKernel, Props.SplatViewData, resources.GpuView);
            cmd.SetComputeBufferParam(m_CSSplatUtilities, scatterKernel, "_CompactGroups", resources.CompactGroups);
            bool alternate = sortKeyBits == 24;
            cmd.SetComputeBufferParam(m_CSSplatUtilities, scatterKernel, Props.SplatSortDistances,
                alternate ? resources.SorterArgs.resources.altBuffer : resources.GpuSortDistances);
            cmd.SetComputeBufferParam(m_CSSplatUtilities, scatterKernel, Props.SplatSortKeys,
                alternate ? resources.SorterArgs.resources.altPayloadBuffer : resources.GpuSortKeys);
            cmd.SetComputeBufferParam(m_CSSplatUtilities, scatterKernel, Props.SplatChunks, m_GpuChunks);
            cmd.SetComputeBufferParam(m_CSSplatUtilities, scatterKernel, Props.SplatPos, m_GpuPosData);
            cmd.SetComputeIntParam(m_CSSplatUtilities, Props.SplatFormat, (int)m_Asset.posFormat);
            cmd.SetComputeIntParam(m_CSSplatUtilities, Props.SplatChunkCount, m_GpuChunksValid ? m_GpuChunks.count : 0);
            cmd.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixMV, view * transform.localToWorldMatrix);
            cmd.SetComputeIntParam(m_CSSplatUtilities, Props.SortDescending, backToFront ? 1 : 0);
            cmd.DispatchCompute(m_CSSplatUtilities, scatterKernel, groups, 1, 1);
            cmd.EndSample("GaussianSplat.CompactVisible");
            cmd.BeginSample(s_ProfSort);
            ++resources.SortDispatchCount;
            m_Sorter.Dispatch(cmd, resources.SorterArgs, sortKeyBits, resources.CompactArgs);
            cmd.EndSample(s_ProfSort);
        }

        internal void SortPoints(CommandBuffer cmd, Camera cam, Matrix4x4 matrix, bool backToFront,
            CameraRenderResources cameraResources)
        {
            ++cameraResources.SortDispatchCount;
            if (cam.cameraType == CameraType.Preview)
                return;

            GetSharedStereoMatrices(cam, out Matrix4x4 worldToCamMatrix, out _);
            worldToCamMatrix.m20 *= -1;
            worldToCamMatrix.m21 *= -1;
            worldToCamMatrix.m22 *= -1;

            cmd.BeginSample(s_ProfSort);
            if (!cameraResources.SortKeysInitialized)
            {
                cmd.SetComputeBufferParam(m_CSSplatUtilities, (int)KernelIndices.SetIndices, Props.SplatSortKeys,
                    cameraResources.GpuSortKeys);
                cmd.SetComputeIntParam(m_CSSplatUtilities, Props.SplatCount, cameraResources.GpuSortKeys.count);
                m_CSSplatUtilities.GetKernelThreadGroupSizes((int)KernelIndices.SetIndices, out uint initGroupSize, out _, out _);
                cmd.DispatchCompute(m_CSSplatUtilities, (int)KernelIndices.SetIndices,
                    (cameraResources.GpuSortKeys.count + (int)initGroupSize - 1) / (int)initGroupSize, 1, 1);
                cameraResources.SortKeysInitialized = true;
            }

            BindSource(cmd, m_CSSplatUtilities, (int)KernelIndices.CalcDistances);
            bool alternateInput = sortKeyBits == 24;
            cmd.SetComputeIntParam(m_CSSplatUtilities, "_SortKeyBits", sortKeyBits);
            cmd.SetComputeBufferParam(m_CSSplatUtilities, (int)KernelIndices.CalcDistances, "_SplatSortInput",
                alternateInput ? cameraResources.GpuSortKeys : cameraResources.SorterArgs.resources.altPayloadBuffer);
            cmd.SetComputeBufferParam(m_CSSplatUtilities, (int)KernelIndices.CalcDistances, Props.SplatSortDistances,
                alternateInput ? cameraResources.SorterArgs.resources.altBuffer : cameraResources.GpuSortDistances);
            cmd.SetComputeBufferParam(m_CSSplatUtilities, (int)KernelIndices.CalcDistances, Props.SplatSortKeys,
                alternateInput ? cameraResources.SorterArgs.resources.altPayloadBuffer : cameraResources.GpuSortKeys);
            cmd.SetComputeBufferParam(m_CSSplatUtilities, (int)KernelIndices.CalcDistances, Props.SplatChunks, m_GpuChunks);
            cmd.SetComputeBufferParam(m_CSSplatUtilities, (int)KernelIndices.CalcDistances, Props.SplatPos, m_GpuPosData);
            cmd.SetComputeIntParam(m_CSSplatUtilities, Props.SplatFormat, (int)m_Asset.posFormat);
            cmd.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixMV, worldToCamMatrix * matrix);
            cmd.SetComputeIntParam(m_CSSplatUtilities, Props.SplatCount, m_SplatCount);
            cmd.SetComputeIntParam(m_CSSplatUtilities, Props.SplatChunkCount, m_GpuChunksValid ? m_GpuChunks.count : 0);
            cmd.SetComputeIntParam(m_CSSplatUtilities, Props.SortDescending, backToFront ? 1 : 0);
            m_CSSplatUtilities.GetKernelThreadGroupSizes((int)KernelIndices.CalcDistances, out uint gsX, out _, out _);
            cmd.DispatchCompute(m_CSSplatUtilities, (int)KernelIndices.CalcDistances,
                (cameraResources.GpuSortDistances.count + (int)gsX - 1)/(int)gsX, 1, 1);

            EnsureSorterAndRegister();
            m_Sorter.Dispatch(cmd, cameraResources.SorterArgs, sortKeyBits);
            cmd.EndSample(s_ProfSort);
        }

        public void Update()
        {
            ReleaseDestroyedCameraRenderResources();

            bool directTransparentPath = usesDirectTransparentPath;
            if (m_PreviousDirectTransparentPath != directTransparentPath)
            {
                // The two paths require opposite sort orders. Force a sort immediately after
                // switching, even when Sort Nth Frame is greater than one.
                ResetStereoFrameCaches();
                m_PreviousDirectTransparentPath = directTransparentPath;
            }

            var curHash = m_Asset ? m_Asset.dataHash : new Hash128();
            if (m_PrevAsset != m_Asset || m_PrevHash != curHash)
            {
                m_PrevAsset = m_Asset;
                m_PrevHash = curHash;
                if (resourcesAreSetUp)
                {
                    DisposeResourcesForAsset();
                    CreateResourcesForAsset();
                    ++m_RenderDataVersion;
                }
                else
                {
                    Debug.LogError($"{nameof(GaussianSplat3DRenderer)} component is not set up correctly (Resource references are missing), or platform does not support compute shaders");
                }
            }

        }

        public void ActivateCamera(int index)
        {
            Camera mainCam = Camera.main;
            if (!mainCam)
                return;
            if (!m_Asset || m_Asset.cameras == null)
                return;

            var selfTr = transform;
            var camTr = mainCam.transform;
            var prevParent = camTr.parent;
            var cam = m_Asset.cameras[index];
            camTr.parent = selfTr;
            camTr.localPosition = cam.pos;
            camTr.localRotation = Quaternion.LookRotation(cam.axisZ, cam.axisY);
            camTr.parent = prevParent;
            camTr.localScale = Vector3.one;
#if UNITY_EDITOR
            UnityEditor.EditorUtility.SetDirty(camTr);
#endif
        }

        void ClearGraphicsBuffer(GraphicsBuffer buf)
        {
            m_CSSplatUtilities.SetBuffer((int)KernelIndices.ClearBuffer, Props.DstBuffer, buf);
            m_CSSplatUtilities.SetInt(Props.BufferSize, buf.count);
            m_CSSplatUtilities.GetKernelThreadGroupSizes((int)KernelIndices.ClearBuffer, out uint gsX, out _, out _);
            m_CSSplatUtilities.Dispatch((int)KernelIndices.ClearBuffer, (int)((buf.count+gsX-1)/gsX), 1, 1);
        }

        void UnionGraphicsBuffers(GraphicsBuffer dst, GraphicsBuffer src)
        {
            m_CSSplatUtilities.SetBuffer((int)KernelIndices.OrBuffers, Props.SrcBuffer, src);
            m_CSSplatUtilities.SetBuffer((int)KernelIndices.OrBuffers, Props.DstBuffer, dst);
            m_CSSplatUtilities.SetInt(Props.BufferSize, dst.count);
            m_CSSplatUtilities.GetKernelThreadGroupSizes((int)KernelIndices.OrBuffers, out uint gsX, out _, out _);
            m_CSSplatUtilities.Dispatch((int)KernelIndices.OrBuffers, (int)((dst.count+gsX-1)/gsX), 1, 1);
        }

        static float SortableUintToFloat(uint v)
        {
            uint mask = ((v >> 31) - 1) | 0x80000000u;
            return math.asfloat(v ^ mask);
        }

        public void UpdateEditCountsAndBounds()
        {
            ++m_RenderDataVersion;
            if (m_GpuEditSelected == null)
            {
                ++m_RenderDataVersion;
                editSelectedSplats = 0;
                editDeletedSplats = 0;
                editCutSplats = 0;
                editModified = false;
                editSelectedBounds = default;
                return;
            }

            m_CSSplatUtilities.SetBuffer((int)KernelIndices.InitEditData, Props.DstBuffer, m_GpuEditCountsBounds);
            m_CSSplatUtilities.Dispatch((int)KernelIndices.InitEditData, 1, 1, 1);

            using CommandBuffer cmb = new CommandBuffer();
            SetAssetDataOnCS(cmb, KernelIndices.UpdateEditData);
            cmb.SetComputeBufferParam(m_CSSplatUtilities, (int)KernelIndices.UpdateEditData, Props.DstBuffer, m_GpuEditCountsBounds);
            cmb.SetComputeIntParam(m_CSSplatUtilities, Props.BufferSize, m_GpuEditSelected.count);
            m_CSSplatUtilities.GetKernelThreadGroupSizes((int)KernelIndices.UpdateEditData, out uint gsX, out _, out _);
            cmb.DispatchCompute(m_CSSplatUtilities, (int)KernelIndices.UpdateEditData, (int)((m_GpuEditSelected.count+gsX-1)/gsX), 1, 1);
            Graphics.ExecuteCommandBuffer(cmb);

            uint[] res = new uint[m_GpuEditCountsBounds.count];
            m_GpuEditCountsBounds.GetData(res);
            editSelectedSplats = res[0];
            editDeletedSplats = res[1];
            editCutSplats = res[2];
            Vector3 min = new Vector3(SortableUintToFloat(res[3]), SortableUintToFloat(res[4]), SortableUintToFloat(res[5]));
            Vector3 max = new Vector3(SortableUintToFloat(res[6]), SortableUintToFloat(res[7]), SortableUintToFloat(res[8]));
            Bounds bounds = default;
            bounds.SetMinMax(min, max);
            if (bounds.extents.sqrMagnitude < 0.01)
                bounds.extents = new Vector3(0.1f,0.1f,0.1f);
            editSelectedBounds = bounds;
        }

        void UpdateCutoutsBuffer()
        {
            int bufferSize = m_Cutouts?.Length ?? 0;
            if (bufferSize == 0)
                bufferSize = 1;
            if (m_GpuEditCutouts == null || m_GpuEditCutouts.count != bufferSize)
            {
                m_GpuEditCutouts?.Dispose();
                m_GpuEditCutouts = new GraphicsBuffer(GraphicsBuffer.Target.Structured, bufferSize, UnsafeUtility.SizeOf<GaussianCutout.ShaderData>()) { name = "GaussianSplat3DCutouts" };
            }

            NativeArray<GaussianCutout.ShaderData> data = new(bufferSize, Allocator.Temp);
            if (m_Cutouts != null)
            {
                var matrix = transform.localToWorldMatrix;
                for (var i = 0; i < m_Cutouts.Length; ++i)
                {
                    data[i] = GaussianCutout.GetShaderData(m_Cutouts[i], matrix);
                }
            }

            m_GpuEditCutouts.SetData(data);
            data.Dispose();
        }

        bool EnsureEditingBuffers()
        {
            if (!CanEditSplats || !HasValidAsset || !HasValidRenderSetup)
                return false;

            if (m_GpuEditSelected == null)
            {
                ++m_RenderDataVersion;
                var target = GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopySource |
                             GraphicsBuffer.Target.CopyDestination;
                var size = (m_SplatCount + 31) / 32;
                m_GpuEditSelected = new GraphicsBuffer(target, size, 4) {name = "GaussianSplatSelected"};
                m_GpuEditSelectedMouseDown = new GraphicsBuffer(target, size, 4) {name = "GaussianSplatSelectedInit"};
                m_GpuEditDeleted = new GraphicsBuffer(target, size, 4) {name = "GaussianSplatDeleted"};
                m_GpuEditCountsBounds = new GraphicsBuffer(target, 3 + 6, 4) {name = "GaussianSplatEditData"}; // selected count, deleted bound, cut count, float3 min, float3 max
                ClearGraphicsBuffer(m_GpuEditSelected);
                ClearGraphicsBuffer(m_GpuEditSelectedMouseDown);
                ClearGraphicsBuffer(m_GpuEditDeleted);
            }
            return m_GpuEditSelected != null;
        }

        public void EditStoreSelectionMouseDown()
        {
            if (!EnsureEditingBuffers()) return;
            Graphics.CopyBuffer(m_GpuEditSelected, m_GpuEditSelectedMouseDown);
        }

        public void EditStorePosMouseDown()
        {
            if (!CanEditSplats) return;
            if (m_GpuEditPosMouseDown == null)
            {
                m_GpuEditPosMouseDown = new GraphicsBuffer(m_GpuPosData.target | GraphicsBuffer.Target.CopyDestination, m_GpuPosData.count, m_GpuPosData.stride) {name = "GaussianSplatEditPosMouseDown"};
            }
            Graphics.CopyBuffer(m_GpuPosData, m_GpuEditPosMouseDown);
        }
        public void EditStoreOtherMouseDown()
        {
            if (!CanEditSplats) return;
            if (m_GpuEditOtherMouseDown == null)
            {
                m_GpuEditOtherMouseDown = new GraphicsBuffer(m_GpuOtherData.target | GraphicsBuffer.Target.CopyDestination, m_GpuOtherData.count, m_GpuOtherData.stride) {name = "GaussianSplatEditOtherMouseDown"};
            }
            Graphics.CopyBuffer(m_GpuOtherData, m_GpuEditOtherMouseDown);
        }

        public void EditUpdateSelection(Vector2 rectMin, Vector2 rectMax, Camera cam, bool subtract)
        {
            if (!EnsureEditingBuffers()) return;

            Graphics.CopyBuffer(m_GpuEditSelectedMouseDown, m_GpuEditSelected);

            var tr = transform;
            Matrix4x4 matView = cam.worldToCameraMatrix;
            Matrix4x4 matO2W = tr.localToWorldMatrix;
            Matrix4x4 matW2O = tr.worldToLocalMatrix;
            int screenW = cam.pixelWidth, screenH = cam.pixelHeight;
            Vector4 screenPar = new Vector4(screenW, screenH, 0, 0);
            Vector4 camPos = cam.transform.position;

            using var cmb = new CommandBuffer { name = "SplatSelectionUpdate" };
            SetAssetDataOnCS(cmb, KernelIndices.SelectionUpdate);

            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixMV, matView * matO2W);
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixObjectToWorld, matO2W);
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixWorldToObject, matW2O);

            cmb.SetComputeVectorParam(m_CSSplatUtilities, Props.VecScreenParams, screenPar);
            cmb.SetComputeVectorParam(m_CSSplatUtilities, Props.VecWorldSpaceCameraPos, camPos);

            cmb.SetComputeVectorParam(m_CSSplatUtilities, "_SelectionRect", new Vector4(rectMin.x, rectMax.y, rectMax.x, rectMin.y));
            cmb.SetComputeIntParam(m_CSSplatUtilities, Props.SelectionMode, subtract ? 0 : 1);

            DispatchUtilsAndExecute(cmb, KernelIndices.SelectionUpdate, m_SplatCount);
            UpdateEditCountsAndBounds();
        }

        public void EditTranslateSelection(Vector3 localSpacePosDelta)
        {
            if (!EnsureEditingBuffers()) return;

            using var cmb = new CommandBuffer { name = "SplatTranslateSelection" };
            SetAssetDataOnCS(cmb, KernelIndices.TranslateSelection);

            cmb.SetComputeVectorParam(m_CSSplatUtilities, Props.SelectionDelta, localSpacePosDelta);

            DispatchUtilsAndExecute(cmb, KernelIndices.TranslateSelection, m_SplatCount);
            ++m_RenderDataVersion;
            UpdateEditCountsAndBounds();
            editModified = true;
        }

        public void EditRotateSelection(Vector3 localSpaceCenter, Matrix4x4 localToWorld, Matrix4x4 worldToLocal, Quaternion rotation)
        {
            if (!EnsureEditingBuffers()) return;
            if (m_GpuEditPosMouseDown == null || m_GpuEditOtherMouseDown == null) return; // should have captured initial state

            using var cmb = new CommandBuffer { name = "SplatRotateSelection" };
            SetAssetDataOnCS(cmb, KernelIndices.RotateSelection);

            cmb.SetComputeBufferParam(m_CSSplatUtilities, (int)KernelIndices.RotateSelection, Props.SplatPosMouseDown, m_GpuEditPosMouseDown);
            cmb.SetComputeBufferParam(m_CSSplatUtilities, (int)KernelIndices.RotateSelection, Props.SplatOtherMouseDown, m_GpuEditOtherMouseDown);
            cmb.SetComputeVectorParam(m_CSSplatUtilities, Props.SelectionCenter, localSpaceCenter);
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixObjectToWorld, localToWorld);
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixWorldToObject, worldToLocal);
            cmb.SetComputeVectorParam(m_CSSplatUtilities, Props.SelectionDeltaRot, new Vector4(rotation.x, rotation.y, rotation.z, rotation.w));

            DispatchUtilsAndExecute(cmb, KernelIndices.RotateSelection, m_SplatCount);
            ++m_RenderDataVersion;
            UpdateEditCountsAndBounds();
            editModified = true;
        }


        public void EditScaleSelection(Vector3 localSpaceCenter, Matrix4x4 localToWorld, Matrix4x4 worldToLocal, Vector3 scale)
        {
            if (!EnsureEditingBuffers()) return;
            if (m_GpuEditPosMouseDown == null) return; // should have captured initial state

            using var cmb = new CommandBuffer { name = "SplatScaleSelection" };
            SetAssetDataOnCS(cmb, KernelIndices.ScaleSelection);

            cmb.SetComputeBufferParam(m_CSSplatUtilities, (int)KernelIndices.ScaleSelection, Props.SplatPosMouseDown, m_GpuEditPosMouseDown);
            cmb.SetComputeVectorParam(m_CSSplatUtilities, Props.SelectionCenter, localSpaceCenter);
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixObjectToWorld, localToWorld);
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixWorldToObject, worldToLocal);
            cmb.SetComputeVectorParam(m_CSSplatUtilities, Props.SelectionDelta, scale);

            DispatchUtilsAndExecute(cmb, KernelIndices.ScaleSelection, m_SplatCount);
            ++m_RenderDataVersion;
            UpdateEditCountsAndBounds();
            editModified = true;
        }

        public void EditDeleteSelected()
        {
            if (!EnsureEditingBuffers()) return;
            UnionGraphicsBuffers(m_GpuEditDeleted, m_GpuEditSelected);
            ++m_RenderDataVersion;
            EditDeselectAll();
            UpdateEditCountsAndBounds();
            if (editDeletedSplats != 0)
                editModified = true;
        }

        public void EditSelectAll()
        {
            if (!EnsureEditingBuffers()) return;
            using var cmb = new CommandBuffer { name = "SplatSelectAll" };
            SetAssetDataOnCS(cmb, KernelIndices.SelectAll);
            cmb.SetComputeBufferParam(m_CSSplatUtilities, (int)KernelIndices.SelectAll, Props.DstBuffer, m_GpuEditSelected);
            cmb.SetComputeIntParam(m_CSSplatUtilities, Props.BufferSize, m_GpuEditSelected.count);
            DispatchUtilsAndExecute(cmb, KernelIndices.SelectAll, m_GpuEditSelected.count);
            UpdateEditCountsAndBounds();
        }

        public void EditDeselectAll()
        {
            if (!EnsureEditingBuffers()) return;
            ClearGraphicsBuffer(m_GpuEditSelected);
            UpdateEditCountsAndBounds();
        }

        public void EditInvertSelection()
        {
            if (!EnsureEditingBuffers()) return;

            using var cmb = new CommandBuffer { name = "SplatInvertSelection" };
            SetAssetDataOnCS(cmb, KernelIndices.InvertSelection);
            cmb.SetComputeBufferParam(m_CSSplatUtilities, (int)KernelIndices.InvertSelection, Props.DstBuffer, m_GpuEditSelected);
            cmb.SetComputeIntParam(m_CSSplatUtilities, Props.BufferSize, m_GpuEditSelected.count);
            DispatchUtilsAndExecute(cmb, KernelIndices.InvertSelection, m_GpuEditSelected.count);
            UpdateEditCountsAndBounds();
        }

        public bool EditExportData(GraphicsBuffer dstData, bool bakeTransform)
        {
            if (!EnsureEditingBuffers()) return false;

            int flags = 0;
            var tr = transform;
            Quaternion bakeRot = tr.localRotation;
            Vector3 bakeScale = tr.localScale;

            if (bakeTransform)
                flags = 1;

            using var cmb = new CommandBuffer { name = "SplatExportData" };
            SetAssetDataOnCS(cmb, KernelIndices.ExportData);
            cmb.SetComputeIntParam(m_CSSplatUtilities, "_ExportTransformFlags", flags);
            cmb.SetComputeVectorParam(m_CSSplatUtilities, "_ExportTransformRotation", new Vector4(bakeRot.x, bakeRot.y, bakeRot.z, bakeRot.w));
            cmb.SetComputeVectorParam(m_CSSplatUtilities, "_ExportTransformScale", bakeScale);
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixObjectToWorld, tr.localToWorldMatrix);
            cmb.SetComputeBufferParam(m_CSSplatUtilities, (int)KernelIndices.ExportData, "_ExportBuffer", dstData);

            DispatchUtilsAndExecute(cmb, KernelIndices.ExportData, m_SplatCount);
            return true;
        }

        public void EditSetSplatCount(int newSplatCount)
        {
            if (!CanEditSplats) return;
            if (newSplatCount <= 0 || newSplatCount > GaussianSplat3DAsset.kMaxSplats)
            {
                Debug.LogError($"Invalid new splat count: {newSplatCount}");
                return;
            }
            if (asset.chunkData != null)
            {
                Debug.LogError("Only splats with VeryHigh quality can be resized");
                return;
            }
            if (newSplatCount == splatCount)
                return;

            int posStride = (int)(asset.posData.dataSize / asset.splatCount);
            int otherStride = (int)(asset.otherData.dataSize / asset.splatCount);
            int shStride = (int) (asset.shData.dataSize / asset.splatCount);

            var newPosData = new GraphicsBuffer(GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopySource, newSplatCount * posStride / 4, 4) { name = "GaussianPosData" };
            var newOtherData = new GraphicsBuffer(GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopySource, newSplatCount * otherStride / 4, 4) { name = "GaussianOtherData" };
            var newSHData = new GraphicsBuffer(GraphicsBuffer.Target.Raw, newSplatCount * shStride / 4, 4) { name = "GaussianSHData" };

            // new texture is a RenderTexture so we can write to it from a compute shader
            var (texWidth, texHeight) = GaussianSplat3DAsset.CalcTextureSize(newSplatCount);
            var texFormat = GaussianSplat3DAsset.ColorFormatToGraphics(asset.colorFormat);
            var newColorData = new RenderTexture(texWidth, texHeight, texFormat, GraphicsFormat.None) { name = "GaussianColorData", enableRandomWrite = true };
            newColorData.Create();

            var selTarget = GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopySource | GraphicsBuffer.Target.CopyDestination;
            var selSize = (newSplatCount + 31) / 32;
            var newEditSelected = new GraphicsBuffer(selTarget, selSize, 4) {name = "GaussianSplatSelected"};
            var newEditSelectedMouseDown = new GraphicsBuffer(selTarget, selSize, 4) {name = "GaussianSplatSelectedInit"};
            var newEditDeleted = new GraphicsBuffer(selTarget, selSize, 4) {name = "GaussianSplatDeleted"};
            ClearGraphicsBuffer(newEditSelected);
            ClearGraphicsBuffer(newEditSelectedMouseDown);
            ClearGraphicsBuffer(newEditDeleted);

            EditCopySplats(transform, newPosData, newOtherData, newSHData, newColorData, newEditDeleted, newSplatCount, 0, 0, m_SplatCount);

            m_GpuPosData.Dispose();
            m_GpuOtherData.Dispose();
            m_GpuSHData.Dispose();
            if (m_GpuColorData != Texture2D.blackTexture) DestroyImmediate(m_GpuColorData);
            m_GpuColorData = null;
            DisposeCameraRenderResources();

            m_GpuEditSelected?.Dispose();
            m_GpuEditSelectedMouseDown?.Dispose();
            m_GpuEditDeleted?.Dispose();

            m_GpuPosData = newPosData;
            m_GpuOtherData = newOtherData;
            m_GpuSHData = newSHData;
            m_GpuColorData = newColorData;
            m_GpuEditSelected = newEditSelected;
            m_GpuEditSelectedMouseDown = newEditSelectedMouseDown;
            m_GpuEditDeleted = newEditDeleted;

            DisposeBuffer(ref m_GpuEditPosMouseDown);
            DisposeBuffer(ref m_GpuEditOtherMouseDown);

            m_SplatCount = newSplatCount;
            ++m_RenderDataVersion;
            editModified = true;
        }

        public void EditCopySplatsInto(GaussianSplat3DRenderer dst, int copySrcStartIndex, int copyDstStartIndex, int copyCount)
        {
            if (!CanEditSplats || !dst || !dst.CanEditSplats) return;
            EditCopySplats(
                dst.transform,
                dst.m_GpuPosData, dst.m_GpuOtherData, dst.m_GpuSHData, dst.m_GpuColorData, dst.m_GpuEditDeleted,
                dst.splatCount,
                copySrcStartIndex, copyDstStartIndex, copyCount);
            ++dst.m_RenderDataVersion;
            dst.editModified = true;
        }

        public void EditCopySplats(
            Transform dstTransform,
            GraphicsBuffer dstPos, GraphicsBuffer dstOther, GraphicsBuffer dstSH, Texture dstColor,
            GraphicsBuffer dstEditDeleted,
            int dstSize,
            int copySrcStartIndex, int copyDstStartIndex, int copyCount)
        {
            if (!EnsureEditingBuffers()) return;

            Matrix4x4 copyMatrix = dstTransform.worldToLocalMatrix * transform.localToWorldMatrix;
            Quaternion copyRot = copyMatrix.rotation;
            Vector3 copyScale = copyMatrix.lossyScale;

            using var cmb = new CommandBuffer { name = "SplatCopy" };
            SetAssetDataOnCS(cmb, KernelIndices.CopySplats);

            cmb.SetComputeBufferParam(m_CSSplatUtilities, (int)KernelIndices.CopySplats, "_CopyDstPos", dstPos);
            cmb.SetComputeBufferParam(m_CSSplatUtilities, (int)KernelIndices.CopySplats, "_CopyDstOther", dstOther);
            cmb.SetComputeBufferParam(m_CSSplatUtilities, (int)KernelIndices.CopySplats, "_CopyDstSH", dstSH);
            cmb.SetComputeTextureParam(m_CSSplatUtilities, (int)KernelIndices.CopySplats, "_CopyDstColor", dstColor);
            cmb.SetComputeBufferParam(m_CSSplatUtilities, (int)KernelIndices.CopySplats, "_CopyDstEditDeleted", dstEditDeleted);

            cmb.SetComputeIntParam(m_CSSplatUtilities, "_CopyDstSize", dstSize);
            cmb.SetComputeIntParam(m_CSSplatUtilities, "_CopySrcStartIndex", copySrcStartIndex);
            cmb.SetComputeIntParam(m_CSSplatUtilities, "_CopyDstStartIndex", copyDstStartIndex);
            cmb.SetComputeIntParam(m_CSSplatUtilities, "_CopyCount", copyCount);

            cmb.SetComputeVectorParam(m_CSSplatUtilities, "_CopyTransformRotation", new Vector4(copyRot.x, copyRot.y, copyRot.z, copyRot.w));
            cmb.SetComputeVectorParam(m_CSSplatUtilities, "_CopyTransformScale", copyScale);
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, "_CopyTransformMatrix", copyMatrix);

            DispatchUtilsAndExecute(cmb, KernelIndices.CopySplats, copyCount);
        }

        void DispatchUtilsAndExecute(CommandBuffer cmb, KernelIndices kernel, int count)
        {
            m_CSSplatUtilities.GetKernelThreadGroupSizes((int)kernel, out uint gsX, out _, out _);
            cmb.DispatchCompute(m_CSSplatUtilities, (int)kernel, (int)((count + gsX - 1)/gsX), 1, 1);
            Graphics.ExecuteCommandBuffer(cmb);
        }

        public GraphicsBuffer GpuEditDeleted => m_GpuEditDeleted;
    }
}
