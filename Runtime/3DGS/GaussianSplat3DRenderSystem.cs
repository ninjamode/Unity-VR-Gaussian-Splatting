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

        readonly List<GaussianSplat3DRenderer> m_Splats = new();
        readonly Dictionary<Camera, CommandBuffer> m_CameraCommandBuffersDone = new();
        readonly List<Camera> m_DestroyedCommandBufferCameras = new();

        CommandBuffer m_CommandBuffer;

        public static bool ConvertCompositeGammaToLinear = true;

        // URP selects camera-stack attachments using the base camera alone.
        // Include composite renderers hidden by its culling mask: an overlay may
        // render them later into those same attachments.
        internal bool RequiresCompositeIntermediate
        {
            get
            {
                foreach (var gs in m_Splats)
                {
                    if (gs != null && gs.isActiveAndEnabled && gs.HasValidAsset && gs.HasValidRenderSetup &&
                        !gs.usesDirectTransparentPath)
                        return true;
                }
                return false;
            }
        }

        public void RegisterSplat(GaussianSplat3DRenderer r)
        {
            if (m_Splats.Contains(r))
                throw new System.ArgumentException("Renderer is already registered.", nameof(r));
            if (m_Splats.Count == 0)
            {
                if (GraphicsSettings.currentRenderPipeline == null)
                    Camera.onPreCull += OnPreCullCamera;
            }

            m_Splats.Add(r);
        }

        public void UnregisterSplat(GaussianSplat3DRenderer r)
        {
            if (!m_Splats.Contains(r))
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
                m_CommandBuffer = null;
                Camera.onPreCull -= OnPreCullCamera;
            }
        }

        // ReSharper disable once MemberCanBePrivate.Global - used by HDRP/URP features that are not always compiled
        // Owned by one render phase; never retained as the system's "current camera".
        internal sealed class CameraSelection
        {
            internal readonly Camera Camera;
            internal readonly List<GaussianSplat3DRenderer> Renderers = new();
            internal readonly List<GaussiansGroup.CameraResources> Groups = new();
            internal bool HasDirect, HasComposite, HasDepth;
            internal bool HasSplats => Renderers.Count != 0 || Groups.Count != 0;

            internal CameraSelection(Camera camera) => Camera = camera;
        }

        internal CameraSelection CollectForCamera(Camera cam)
        {
            var selection = new CameraSelection(cam);
            if (cam == null || cam.cameraType == CameraType.Preview)
                return selection;
            foreach (var gs in m_Splats)
            {
                if (gs == null || !gs.isActiveAndEnabled || !gs.HasValidAsset || !gs.HasValidRenderSetup ||
                    (cam.cullingMask & (1 << gs.gameObject.layer)) == 0)
                    continue;
                gs.PrepareSource();
                selection.Renderers.Add(gs);
                if (gs.usesDirectTransparentPath)
                    selection.HasDirect = true;
                else
                    selection.HasComposite = true;
                selection.HasDepth |= gs.EffectiveWriteDepth && gs.EffectiveRenderMode == GaussianSplat3DRenderer.RenderMode.Splats;
            }

            var groups = new Dictionary<GaussiansGroup, List<GaussianSplat3DRenderer>>();
            foreach (var renderer in selection.Renderers)
            {
                var group = renderer.ActiveGroup;
                if (!group) continue;
                if (!groups.TryGetValue(group, out var members)) groups.Add(group, members = new());
                members.Add(renderer);
            }
            foreach (var entry in groups)
            {
                var resource = entry.Key.Collect(cam, entry.Value);
                // An active group never silently turns into independent submissions.
                if (resource != null) selection.Groups.Add(resource);
                foreach (var member in entry.Value) selection.Renderers.Remove(member);
            }

            var camTr = cam.transform;
            selection.Renderers.Sort((a, b) =>
            {
                var orderA = a.EffectiveRenderOrder;
                var orderB = b.EffectiveRenderOrder;
                if (orderA != orderB)
                    return orderB.CompareTo(orderA);
                var posA = camTr.InverseTransformPoint(a.transform.position);
                var posB = camTr.InverseTransformPoint(b.transform.position);
                return posA.z.CompareTo(posB.z);
            });
            return selection;
        }

        internal void SubmitDirectSplatsForCamera(CameraSelection selection)
        {
            foreach (var gs in selection.Renderers)
                if (gs.usesDirectTransparentPath)
                    gs.QueueDirectTransparentDraw(selection.Camera);
            foreach (var group in selection.Groups) if (group.Direct) GaussiansGroup.Submit(group);
        }

        // ReSharper disable once MemberCanBePrivate.Global - used by HDRP/URP features that are not always compiled
        public Material SortAndRenderCompositeSplats(CameraSelection selection, CommandBuffer cmb, bool convertComposite = true)
        {
            var draws = GatherCompositeDraws(selection, out var composite, convertComposite);
            PrepareCompositeSplats(selection, cmb);
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

        internal void PrepareCompositeSplats(CameraSelection selection, CommandBuffer cmb)
        {
            foreach (var group in selection.Groups) if (!group.Direct) GaussiansGroup.Prepare(group, cmb);
            foreach (var gs in selection.Renderers)
            {
                if (gs.usesDirectTransparentPath)
                    continue;
                var resources = gs.GetCameraRenderResources(selection.Camera);
                if (resources != null)
                    gs.PrepareCamera(cmb, selection.Camera, false, resources);
            }
        }

        internal List<SplatDraw> GatherCompositeDraws(CameraSelection selection, out Material matComposite, bool convertComposite = true)
        {
            var cam = selection.Camera;
            matComposite = null;
            var draws = new List<SplatDraw>();
            foreach (var gs in selection.Renderers)
            {
                if (gs.usesDirectTransparentPath)
                    continue;
                gs.EnsureMaterials();
                matComposite = gs.GetCompositeMaterial(convertComposite);
                var mpb = new MaterialPropertyBlock();
                var cameraResources = gs.GetCameraRenderResources(cam);
                if (cameraResources == null)
                    continue;

                var matrix = gs.transform.localToWorldMatrix;
                Material displayMat = gs.EffectiveRenderMode switch
                {
                    GaussianSplat3DRenderer.RenderMode.DebugPoints => gs.m_MatDebugPoints,
                    GaussianSplat3DRenderer.RenderMode.DebugPointIndices => gs.m_MatDebugPoints,
                    GaussianSplat3DRenderer.RenderMode.DebugBoxes => gs.m_MatDebugBoxes,
                    GaussianSplat3DRenderer.RenderMode.DebugChunkBounds => gs.m_MatDebugBoxes,
                    _ => gs.m_MatSplats
                };
                if (displayMat == null)
                    continue;

                gs.BindViewProperties(mpb, cam, cameraResources);
                mpb.SetFloat(GaussianSplat3DRenderer.Props.SplatSize, gs.m_PointDisplaySize);
                mpb.SetInteger(GaussianSplat3DRenderer.Props.DisplayIndex, gs.EffectiveRenderMode == GaussianSplat3DRenderer.RenderMode.DebugPointIndices ? 1 : 0);
                mpb.SetInteger(GaussianSplat3DRenderer.Props.DisplayChunks, gs.EffectiveRenderMode == GaussianSplat3DRenderer.RenderMode.DebugChunkBounds ? 1 : 0);

                int indexCount = 6;
                int instanceCount = gs.splatCount;
                if (gs.EffectiveRenderMode is GaussianSplat3DRenderer.RenderMode.DebugBoxes or GaussianSplat3DRenderer.RenderMode.DebugChunkBounds)
                    indexCount = 36;
                if (gs.EffectiveRenderMode == GaussianSplat3DRenderer.RenderMode.DebugChunkBounds)
                    instanceCount = gs.m_GpuChunksValid ? gs.m_GpuChunks.count : 0;

                draws.Add(new SplatDraw
                {
                    RenderOrder = gs.EffectiveRenderOrder, SortPosition = gs.transform.position,
                    Indices = gs.m_GpuIndexBuffer, Matrix = matrix, Material = displayMat,
                    Properties = mpb, IndexCount = indexCount, Count = instanceCount,
                    IndirectArgs = gs.GetIndirectArgs(cameraResources),
                });
            }
            foreach (var group in selection.Groups)
                if (!group.Direct) { draws.Add(GaussiansGroup.Draw(group)); matComposite = group.Leader.GetCompositeMaterial(convertComposite); }
            draws.Sort((a, b) => a.RenderOrder != b.RenderOrder ? b.RenderOrder.CompareTo(a.RenderOrder) :
                cam.transform.InverseTransformPoint(a.SortPosition).z.CompareTo(cam.transform.InverseTransformPoint(b.SortPosition).z));
            return draws;
        }

        // Direct splats are submitted through Unity's transparent queue during each pipeline's
        // pre-cull callback. This pass updates their ordering and view-dependent GPU data before
        // Unity reaches its normal transparent rendering phase.
        public void PrepareDirectSplats(CameraSelection selection, CommandBuffer cmb)
        {
            foreach (var group in selection.Groups) if (group.Direct) GaussiansGroup.Prepare(group, cmb);
            foreach (var gs in selection.Renderers)
            {
                if (!gs.usesDirectTransparentPath)
                    continue;
                var resources = gs.GetCameraRenderResources(selection.Camera);
                if (resources != null)
                    gs.PrepareCamera(cmb, selection.Camera, true, resources);
            }
        }

        internal sealed class SplatDraw
        {
            internal GraphicsBuffer Indices;
            internal GraphicsBuffer IndirectArgs;
            internal Matrix4x4 Matrix;
            internal Material Material;
            internal MaterialPropertyBlock Properties;
            internal int RenderOrder;
            internal Vector3 SortPosition;
            internal int IndexCount = 6;
            internal int Count;
        }

        // Snapshot draw state per camera while recording the graph. The late pass must
        // not depend on the global active-camera list or mutate a queued color draw's MPB.
        internal List<SplatDraw> GatherDepthDraws(CameraSelection selection)
        {
            var camera = selection.Camera;
            var draws = new List<SplatDraw>();
            foreach (var gs in selection.Renderers)
            {
                if (!gs.EffectiveWriteDepth || gs.EffectiveRenderMode != GaussianSplat3DRenderer.RenderMode.Splats)
                    continue;

                var resources = gs.GetCameraRenderResources(camera);
                if (resources == null)
                    continue;
                gs.EnsureMaterials();
                var properties = new MaterialPropertyBlock();
                gs.BindViewProperties(properties, camera, resources);
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
            foreach (var group in selection.Groups)
                if (group.WriteDepth) draws.Add(GaussiansGroup.DepthDraw(group));
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
            var selection = CollectForCamera(cam);
            if (!selection.HasSplats)
                return;

            if (selection.HasDirect)
                SubmitDirectSplatsForCamera(selection);

            if (selection.HasDirect)
                PrepareDirectSplats(selection, m_CommandBuffer);

            if (!selection.HasComposite)
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
            Material matComposite = SortAndRenderCompositeSplats(selection, m_CommandBuffer, convert);

            m_CommandBuffer.BeginSample(s_ProfCompose);
            m_CommandBuffer.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
            m_CommandBuffer.DrawProcedural(Matrix4x4.identity, matComposite, 0, MeshTopology.Triangles, 3, GaussianRenderTargets.FullscreenInstances(cam));
            m_CommandBuffer.EndSample(s_ProfCompose);
            m_CommandBuffer.ReleaseTemporaryRT(GaussianSplat3DRenderer.Props.GaussianSplatRT);
        }
    }
}
