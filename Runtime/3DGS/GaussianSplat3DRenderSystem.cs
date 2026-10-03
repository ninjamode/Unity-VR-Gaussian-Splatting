// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using Gaussians.Core;
using Unity.Profiling;
using Unity.Profiling.LowLevel;
using UnityEngine;
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
}
