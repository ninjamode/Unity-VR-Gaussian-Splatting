// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using Gaussians.Core;
using Unity.Profiling;
using Unity.Profiling.LowLevel;
using UnityEngine;
using UnityEngine.Rendering;

namespace Gaussians.TwoD
{
    class GaussianSplat2DRenderSystem
    {
        // ReSharper disable MemberCanBePrivate.Global - used by HDRP/URP features that are not always compiled
        internal static readonly ProfilerMarker s_ProfDraw = new(ProfilerCategory.Render, "Gaussians.2D.Draw", MarkerFlags.SampleGPU);
        internal static readonly ProfilerMarker s_ProfCompose = new(ProfilerCategory.Render, "Gaussians.2D.Compose", MarkerFlags.SampleGPU);
        internal static readonly ProfilerMarker s_ProfCalcView = new(ProfilerCategory.Render, "Gaussians.2D.CalcView", MarkerFlags.SampleGPU);
        // ReSharper restore MemberCanBePrivate.Global

        public static GaussianSplat2DRenderSystem instance => ms_Instance ??= new GaussianSplat2DRenderSystem();
        static GaussianSplat2DRenderSystem ms_Instance;

        readonly List<GaussianSplat2DRenderer> m_Splats = new();
        readonly HashSet<Camera> m_CameraCommandBuffersDone = new();

        CommandBuffer m_CommandBuffer;

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

        public void RegisterSplat(GaussianSplat2DRenderer r)
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

        public void UnregisterSplat(GaussianSplat2DRenderer r)
        {
            if (!m_Splats.Contains(r))
                return;
            m_Splats.Remove(r);
            if (m_Splats.Count == 0)
            {
                if (m_CameraCommandBuffersDone != null)
                {
                    if (m_CommandBuffer != null)
                    {
                        foreach (var cam in m_CameraCommandBuffersDone)
                        {
                            if (cam)
                                cam.RemoveCommandBuffer(CameraEvent.BeforeForwardAlpha, m_CommandBuffer);
                        }
                    }
                    m_CameraCommandBuffersDone.Clear();
                }

                m_CommandBuffer?.Dispose();
                m_CommandBuffer = null;
                Camera.onPreCull -= OnPreCullCamera;
            }
        }

        // ReSharper disable once MemberCanBePrivate.Global - used by HDRP/URP features that are not always compiled
        // Owned by one render phase; never retained as the system's "current camera".
        internal sealed class CameraSelection
        {
            internal readonly Camera Camera;
            internal readonly List<GaussianSplat2DRenderer> Renderers = new();
            internal bool HasDirect, HasComposite, HasDepth;
            internal bool HasSplats => Renderers.Count != 0;

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
                selection.Renderers.Add(gs);
                if (gs.usesDirectTransparentPath)
                    selection.HasDirect = true;
                else
                    selection.HasComposite = true;
                selection.HasDepth |= gs.m_WriteDepth && gs.m_RenderMode == GaussianSplat2DRenderer.RenderMode.Splats;
            }

            var camTr = cam.transform;
            selection.Renderers.Sort((a, b) =>
            {
                var orderA = a.m_RenderOrder;
                var orderB = b.m_RenderOrder;
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
        }

        // ReSharper disable once MemberCanBePrivate.Global - used by HDRP/URP features that are not always compiled
        public Material SortAndRenderCompositeSplats(CameraSelection selection, CommandBuffer cmb)
        {
            var draws = GatherCompositeDraws(selection, out var composite);
            PrepareCompositeSplats(selection, cmb);
            foreach (var draw in draws)
                cmb.DrawProcedural(draw.Indices, draw.Matrix, draw.Material, 0,
                    MeshTopology.Triangles, draw.IndexCount, draw.Count, draw.Properties);
            return composite;
        }

        internal void PrepareCompositeSplats(CameraSelection selection, CommandBuffer cmb)
        {
            foreach (var gs in selection.Renderers)
            {
                if (gs.usesDirectTransparentPath)
                    continue;
                var resources = gs.GetCameraRenderResources(selection.Camera);
                if (resources != null)
                    gs.PrepareCamera(cmb, selection.Camera, false, resources);
            }
        }

        internal List<SplatDraw> GatherCompositeDraws(CameraSelection selection, out Material matComposite)
        {
            var cam = selection.Camera;
            matComposite = null;
            var draws = new List<SplatDraw>();
            foreach (var gs in selection.Renderers)
            {
                if (gs.usesDirectTransparentPath)
                    continue;
                gs.EnsureMaterials();
                matComposite = gs.m_MatComposite;
                var mpb = new MaterialPropertyBlock();
                var cameraResources = gs.GetCameraRenderResources(cam);
                if (cameraResources == null)
                    continue;

                var matrix = gs.transform.localToWorldMatrix;
                Material displayMat = gs.m_RenderMode switch
                {
                    GaussianSplat2DRenderer.RenderMode.DebugPoints => gs.m_MatDebugPoints,
                    GaussianSplat2DRenderer.RenderMode.DebugPointIndices => gs.m_MatDebugPoints,
                    GaussianSplat2DRenderer.RenderMode.DebugBoxes => gs.m_MatDebugBoxes,
                    GaussianSplat2DRenderer.RenderMode.DebugChunkBounds => gs.m_MatDebugBoxes,
                    _ => gs.m_MatSplats
                };
                if (displayMat == null)
                    continue;

                gs.BindViewProperties(mpb, cam, cameraResources);
                mpb.SetFloat(GaussianSplat2DRenderer.Props.SplatSize, gs.m_PointDisplaySize);
                mpb.SetInteger(GaussianSplat2DRenderer.Props.DisplayIndex, gs.m_RenderMode == GaussianSplat2DRenderer.RenderMode.DebugPointIndices ? 1 : 0);
                mpb.SetInteger(GaussianSplat2DRenderer.Props.DisplayChunks, gs.m_RenderMode == GaussianSplat2DRenderer.RenderMode.DebugChunkBounds ? 1 : 0);

                int indexCount = 6;
                int instanceCount = gs.splatCount;
                if (gs.m_RenderMode is GaussianSplat2DRenderer.RenderMode.DebugBoxes or GaussianSplat2DRenderer.RenderMode.DebugChunkBounds)
                    indexCount = 36;
                if (gs.m_RenderMode == GaussianSplat2DRenderer.RenderMode.DebugChunkBounds)
                    instanceCount = gs.m_GpuChunksValid ? gs.m_GpuChunks.count : 0;

                draws.Add(new SplatDraw
                {
                    Indices = gs.m_GpuIndexBuffer, Matrix = matrix, Material = displayMat,
                    Properties = mpb, IndexCount = indexCount, Count = instanceCount,
                });
            }
            return draws;
        }

        // Direct splats are submitted through Unity's transparent queue during each pipeline's
        // pre-cull callback. This pass updates their ordering and view-dependent GPU data before
        // Unity reaches its normal transparent rendering phase.
        public void PrepareDirectSplats(CameraSelection selection, CommandBuffer cmb)
        {
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
            internal Matrix4x4 Matrix;
            internal Material Material;
            internal MaterialPropertyBlock Properties;
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
                if (!gs.m_WriteDepth || gs.m_RenderMode != GaussianSplat2DRenderer.RenderMode.Splats)
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
                });
            }
            return draws;
        }

        // ReSharper disable once MemberCanBePrivate.Global - used by HDRP/URP features that are not always compiled
        // ReSharper disable once UnusedMethodReturnValue.Global - used by HDRP/URP features that are not always compiled
        public CommandBuffer InitialClearCmdBuffer(Camera cam)
        {
            m_CommandBuffer ??= new CommandBuffer {name = "Gaussians.2D.Render"};
            if (GraphicsSettings.currentRenderPipeline == null && cam != null && !m_CameraCommandBuffersDone.Contains(cam))
            {
                cam.AddCommandBuffer(CameraEvent.BeforeForwardAlpha, m_CommandBuffer);
                m_CameraCommandBuffersDone.Add(cam);
            }

            // get render target for all splats
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

            m_CommandBuffer.GetTemporaryRT(GaussianSplat2DRenderer.Props.GaussianSplatRT, GaussianRenderTargets.Accumulation(cam), FilterMode.Point);
            m_CommandBuffer.SetRenderTarget(GaussianSplat2DRenderer.Props.GaussianSplatRT, BuiltinRenderTextureType.CurrentActive);
            m_CommandBuffer.ClearRenderTarget(RTClearFlags.Color, new Color(0, 0, 0, 0), 0, 0);

            // We only need this to determine whether we're rendering into backbuffer or not. However, detection this
            // way only works in BiRP so only do it here.
            m_CommandBuffer.SetGlobalTexture(GaussianSplat2DRenderer.Props.CameraTargetTexture, BuiltinRenderTextureType.CameraTarget);

            // add sorting, view calc and drawing commands for each splat object
            Material matComposite = SortAndRenderCompositeSplats(selection, m_CommandBuffer);

            m_CommandBuffer.BeginSample(s_ProfCompose);
            m_CommandBuffer.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
            m_CommandBuffer.DrawProcedural(Matrix4x4.identity, matComposite, 0, MeshTopology.Triangles, 3, GaussianRenderTargets.FullscreenInstances(cam));
            m_CommandBuffer.EndSample(s_ProfCompose);
            m_CommandBuffer.ReleaseTemporaryRT(GaussianSplat2DRenderer.Props.GaussianSplatRT);
        }
    }
}
