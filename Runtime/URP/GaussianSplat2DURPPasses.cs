// SPDX-License-Identifier: MIT
#if GAUSSIANS_ENABLE_URP

#if !UNITY_6000_0_OR_NEWER
#error Unity 2D Gaussian Splatting URP support only works in Unity 6 or later
#endif

using System.Collections.Generic;
using Gaussians.Core;
using Gaussians.TwoD;
using static Gaussians.GaussiansURPUtilities;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.RenderGraphModule;

namespace Gaussians
{
    internal sealed class GaussianSplat2DURPPasses
    {
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

        internal void Create()
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

        internal void OnCameraPreCull(ScriptableRenderer renderer, in CameraData cameraData)
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

        internal void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
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

    }
}

#endif
