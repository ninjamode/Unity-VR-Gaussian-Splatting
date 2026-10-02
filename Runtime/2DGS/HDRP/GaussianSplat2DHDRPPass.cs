// SPDX-License-Identifier: MIT
#if GAUSSIANS_ENABLE_HDRP

using UnityEngine;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.Rendering;
using UnityEngine.Experimental.Rendering;

namespace Gaussians.TwoD
{
    [UnityEngine.Scripting.APIUpdating.MovedFrom(true, "Gaussians.TwoD", "Gaussians.TwoD", "GaussianSplat2DHDRPPass")]
    class GaussianSplat2DHDRPPass : CustomPass
    {
        RTHandle m_RenderTarget;

        // Allocate a floating-point accumulation target with the camera's XR layout.
        protected override void Setup(ScriptableRenderContext renderContext, CommandBuffer cmd)
        {
            m_RenderTarget = RTHandles.Alloc(Vector2.one,
                slices: TextureXR.slices, dimension: TextureXR.dimension,
                colorFormat: GraphicsFormat.R16G16B16A16_SFloat, useDynamicScale: true,
                depthBufferBits: DepthBits.None, msaaSamples: MSAASamples.None,
                filterMode: FilterMode.Point, wrapMode: TextureWrapMode.Clamp, name: "_GaussianSplat2DRT");
        }

        protected override void AggregateCullingParameters(ref ScriptableCullingParameters cullingParameters, HDCamera hdCamera)
        {
            var system = GaussianSplat2DRenderSystem.instance;
            if (system.GatherSplatsForCamera(hdCamera.camera) && system.HasDirectSplats)
                system.SubmitDirectSplatsForCamera(hdCamera.camera);
        }

        protected override void Execute(CustomPassContext ctx)
        {
            var cam = ctx.hdCamera.camera;

            var system = GaussianSplat2DRenderSystem.instance;
            if (!system.GatherSplatsForCamera(cam))
                return;

            if (system.HasDirectSplats)
                system.PrepareDirectSplats(cam, ctx.cmd);

            if (!system.HasCompositeSplats)
                return;

            ctx.cmd.SetGlobalTexture(m_RenderTarget.name, m_RenderTarget.nameID);
            CoreUtils.SetRenderTarget(ctx.cmd, m_RenderTarget, ctx.cameraDepthBuffer, ClearFlag.Color,
                new Color(0, 0, 0, 0));

            // add sorting, view calc and drawing commands for each splat object
            Material matComposite =
                GaussianSplat2DRenderSystem.instance.SortAndRenderCompositeSplats(ctx.hdCamera.camera, ctx.cmd);

            ctx.cmd.BeginSample(GaussianSplat2DRenderSystem.s_ProfCompose);
            CoreUtils.SetRenderTarget(ctx.cmd, ctx.cameraColorBuffer, ClearFlag.None);
            CoreUtils.DrawFullScreen(ctx.cmd, matComposite, ctx.propertyBlock, shaderPassId: 0);
            ctx.cmd.EndSample(GaussianSplat2DRenderSystem.s_ProfCompose);
        }

        protected override void Cleanup()
        {
            m_RenderTarget?.Release();
        }
    }
}

#endif
