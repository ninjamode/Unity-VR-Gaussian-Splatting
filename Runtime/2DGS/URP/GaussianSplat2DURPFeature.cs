// SPDX-License-Identifier: MIT
#if GAUSSIANS_ENABLE_URP

#if !UNITY_6000_0_OR_NEWER
#error Unity 2D Gaussian Splatting URP support only works in Unity 6 or later
#endif

using System.Collections.Generic;
using Gaussians.Core;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.RenderGraphModule;

namespace Gaussians.TwoD
{
    // ReSharper disable once InconsistentNaming
    [UnityEngine.Scripting.APIUpdating.MovedFrom(true, "Gaussians.TwoD", "Gaussians.TwoD", "GaussianSplat2DURPFeature")]
    class GaussianSplat2DURPFeature : ScriptableRendererFeature
    {
        // Match URP 17's DrawObjectsPass policy without depending on its internal
        // XRPassUniversal API. AVP permits intermediate foveation; Android/WSA XR
        // requires the default viewport scale. Recheck this policy on URP upgrades.
        internal static bool CanFoveateTarget(bool xrSupported, bool mobileXR, float viewportScale, bool backBuffer)
            => xrSupported && (backBuffer || !mobileXR || viewportScale == 1.0f);

        static void ConfigureFoveation(IRasterRenderGraphBuilder builder, UniversalCameraData camera,
            bool backBuffer)
        {
            if (!camera.xr.enabled || !camera.xr.supportsFoveatedRendering)
            {
                builder.EnableFoveatedRasterization(false);
                return;
            }
            bool mobileXR = Application.platform == RuntimePlatform.Android ||
                Application.platform == RuntimePlatform.WSAPlayerX86 ||
                Application.platform == RuntimePlatform.WSAPlayerX64 ||
                Application.platform == RuntimePlatform.WSAPlayerARM;
            builder.EnableFoveatedRasterization(CanFoveateTarget(camera.xr.supportsFoveatedRendering,
                mobileXR, XRSystem.GetRenderViewportScale(), backBuffer));
        }

        static GaussianSplatCameraState CaptureCamera(in CameraData camera)
        {
            bool xr = camera.xr != null && camera.xr.enabled;
            int viewCount = xr ? camera.xr.viewCount : 1;
            var size = xr ? camera.xr.GetViewport(0).size :
                new Vector2(camera.cameraTargetDescriptor.width, camera.cameraTargetDescriptor.height);
            var rightSize = viewCount > 1 ? camera.xr.GetViewport(1).size : size;
            var view = camera.GetViewMatrix();
            var projection = camera.GetProjectionMatrix();
            return new GaussianSplatCameraState(view, projection,
                viewCount > 1 ? camera.GetViewMatrix(1) : view,
                viewCount > 1 ? camera.GetProjectionMatrix(1) : projection,
                Vector2Int.RoundToInt(size), Vector2Int.RoundToInt(rightSize), viewCount);
        }

        static GaussianSplatCameraState CaptureCamera(UniversalCameraData camera)
        {
            bool xr = camera.xr != null && camera.xr.enabled;
            int viewCount = xr ? camera.xr.viewCount : 1;
            var size = xr ? camera.xr.GetViewport(0).size :
                new Vector2(camera.cameraTargetDescriptor.width, camera.cameraTargetDescriptor.height);
            var rightSize = viewCount > 1 ? camera.xr.GetViewport(1).size : size;
            var view = camera.GetViewMatrix();
            var projection = camera.GetProjectionMatrix();
            return new GaussianSplatCameraState(view, projection,
                viewCount > 1 ? camera.GetViewMatrix(1) : view,
                viewCount > 1 ? camera.GetProjectionMatrix(1) : projection,
                Vector2Int.RoundToInt(size), Vector2Int.RoundToInt(rightSize), viewCount);
        }

        static void DrawSplats(RasterCommandBuffer cmd, List<GaussianSplat2DRenderSystem.SplatDraw> draws, int pass)
        {
            foreach (var draw in draws)
                cmd.DrawProcedural(draw.Indices, draw.Matrix, draw.Material, pass,
                    MeshTopology.Triangles, draw.IndexCount, draw.Count, draw.Properties);
        }

        class GaussianSplat2DRenderPass : ScriptableRenderPass
        {
            class PrepareData { internal Camera Camera; internal GaussianSplatCameraState CameraState; }
            class AccumulateData { internal List<GaussianSplat2DRenderSystem.SplatDraw> Draws; }
            class CompositeData
            {
                internal TextureHandle Source;
                internal Material Material;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                var camera = frameData.Get<UniversalCameraData>();
                var cameraState = CaptureCamera(camera);
                using var cameraScope = cameraState.Apply(camera.camera);
                var resources = frameData.Get<UniversalResourceData>();
                var system = GaussianSplat2DRenderSystem.instance;
                // Recording/execution may interleave cameras. Capture independent draw
                // properties now and re-gather this camera before issuing compute work.
                if (!system.GatherSplatsForCamera(camera.camera))
                    return;
                var draws = system.GatherCompositeDraws(camera.camera, out var composite);

                using (var builder = renderGraph.AddUnsafePass<PrepareData>("Gaussians.2D.Prepare", out var data))
                {
                    data.Camera = camera.camera;
                    data.CameraState = cameraState;
                    // Compute uses externally owned buffers also consumed by Unity's
                    // queued direct renderers. Preserve this preparation as a side effect.
                    builder.AllowPassCulling(false);
                    builder.AllowGlobalStateModification(true);
                    builder.SetRenderFunc(static (PrepareData pass, UnsafeGraphContext context) =>
                    {
                        using var executionScope = pass.CameraState.Apply(pass.Camera);
                        var cmd = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);
                        var renderSystem = GaussianSplat2DRenderSystem.instance;
                        renderSystem.GatherSplatsForCamera(pass.Camera);
                        renderSystem.PrepareDirectSplats(pass.Camera, cmd);
                        renderSystem.PrepareCompositeSplats(pass.Camera, cmd);
                    });
                }

                if (draws.Count == 0 || composite == null)
                    return;

                var desc = camera.cameraTargetDescriptor;
                desc.depthBufferBits = 0;
                desc.depthStencilFormat = GraphicsFormat.None;
                desc.graphicsFormat = GraphicsFormat.R16G16B16A16_SFloat;
                desc.bindMS = false;
                // Retain XR array layout, viewport sizing and camera MSAA. Accumulation
                // shares camera depth, so it must also use the camera's raster mapping.
                var accumulation = UniversalRenderer.CreateRenderGraphTexture(renderGraph, desc, "_GaussianSplat2DRT", true);
                using (var builder = renderGraph.AddRasterRenderPass<AccumulateData>("Gaussians.2D.Accumulate", out var data))
                {
                    data.Draws = draws;
                    builder.SetRenderAttachment(accumulation, 0, AccessFlags.ReadWrite);
                    // Debug points can write depth; splat color only tests it.
                    builder.SetRenderAttachmentDepth(resources.activeDepthTexture, AccessFlags.ReadWrite);
                    ConfigureFoveation(builder, camera, false);
                    builder.SetRenderFunc(static (AccumulateData pass, RasterGraphContext context) =>
                        DrawSplats(context.cmd, pass.Draws, 0));
                }
                using (var builder = renderGraph.AddRasterRenderPass<CompositeData>("Gaussians.2D.Composite", out var data))
                {
                    data.Source = accumulation;
                    data.Material = composite;
                    builder.UseTexture(accumulation);
                    builder.SetRenderAttachment(resources.activeColorTexture, 0, AccessFlags.ReadWrite);
                    ConfigureFoveation(builder, camera, resources.isActiveTargetBackBuffer);
                    builder.SetRenderFunc(static (CompositeData pass, RasterGraphContext context) =>
                    {
                        RTHandle source = pass.Source;
                        var scale = source.useScaling ? source.rtHandleProperties.rtHandleScale : Vector4.one;
                        Blitter.BlitTexture(context.cmd, source, new Vector4(scale.x, scale.y, 0, 0), pass.Material, 1);
                    });
                }
            }
        }

        sealed class GaussianSplat2DDepthPass : ScriptableRenderPass
        {
            sealed class PassData
            {
                internal List<GaussianSplat2DRenderSystem.SplatDraw> Draws;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                var camera = frameData.Get<UniversalCameraData>();
                var cameraState = CaptureCamera(camera);
                using var cameraScope = cameraState.Apply(camera.camera);
                var draws = GaussianSplat2DRenderSystem.instance.GatherDepthDraws(camera.camera);
                if (draws.Count == 0)
                    return;

                var resources = frameData.Get<UniversalResourceData>();
                using var builder = renderGraph.AddRasterRenderPass<PassData>("Gaussians.2D.Depth", out var data);
                data.Draws = draws;
                // A camera color attachment also supplies a compatible XR raster
                // layout on Metal backends that require one for depth-only draws.
                // SplatDepth has ColorMask 0, so existing color is preserved.
                builder.SetRenderAttachment(resources.activeColorTexture, 0, AccessFlags.ReadWrite);
                builder.SetRenderAttachmentDepth(resources.activeDepthTexture, AccessFlags.ReadWrite);
                ConfigureFoveation(builder, camera, resources.isActiveTargetBackBuffer);
                builder.SetRenderFunc(static (PassData pass, RasterGraphContext context) =>
                    DrawSplats(context.cmd, pass.Draws, 1));
            }
        }

        GaussianSplat2DRenderPass m_Pass;
        GaussianSplat2DDepthPass m_DepthPass;
        bool m_HasCamera;

        public override void Create()
        {
            m_Pass = new GaussianSplat2DRenderPass
            {
                renderPassEvent = RenderPassEvent.BeforeRenderingTransparents
            };
            m_DepthPass = new GaussianSplat2DDepthPass
            {
                // Preserve all transparent color contributions, then populate the
                // active depth attachment before URP's final XR Depth Copy.
                renderPassEvent = RenderPassEvent.AfterRenderingTransparents
            };
        }

        public override void OnCameraPreCull(ScriptableRenderer renderer, in CameraData cameraData)
        {
            using var cameraScope = CaptureCamera(cameraData).Apply(cameraData.camera);
            m_HasCamera = false;
            var system = GaussianSplat2DRenderSystem.instance;
            if (!system.GatherSplatsForCamera(cameraData.camera))
                return;

            if (system.HasDirectSplats)
                system.SubmitDirectSplatsForCamera(cameraData.camera);

            m_HasCamera = true;
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            using var cameraScope = CaptureCamera(renderingData.cameraData).Apply(renderingData.cameraData.camera);
            bool compositeIntermediate = GaussianSplat2DRenderSystem.instance.RequiresCompositeIntermediate;
            if (!m_HasCamera && !compositeIntermediate)
                return;
            // Intermediate accumulation shares camera depth. Force the camera to
            // use an intermediate too, so mobile XR viewport restrictions cannot
            // make their raster mappings disagree. URP handles the final resolve.
            // Apply to base cameras even when only an overlay sees the composite
            // object. URP chooses the stack's attachments from the base camera.
            m_Pass.requiresIntermediateTexture = compositeIntermediate;
            renderer.EnqueuePass(m_Pass);
            if (m_HasCamera && GaussianSplat2DRenderSystem.instance.HasDepthSplatsForCamera(renderingData.cameraData.camera))
                renderer.EnqueuePass(m_DepthPass);
        }

        protected override void Dispose(bool disposing)
        {
            m_Pass = null;
            m_DepthPass = null;
        }
    }
}

#endif // #if GAUSSIANS_ENABLE_URP
