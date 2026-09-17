// SPDX-License-Identifier: MIT
#if GAUSSIANS_ENABLE_HDRP

using UnityEngine;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.Rendering;
using UnityEngine.Experimental.Rendering;

namespace Gaussians.ThreeD
{
    [UnityEngine.Scripting.APIUpdating.MovedFrom(true, "Gaussians.ThreeD", "Gaussians.ThreeD", "GaussianSplat3DHDRPPass")]
    class GaussianSplat3DHDRPPass : CustomPass
    {
        [Tooltip("Convert the 3D composite group after accumulation. Direct renderers control conversion individually.")]
        public bool m_ConvertCompositeGammaToLinear = true;
        RTHandle m_RenderTarget;

        // It can be used to configure render targets and their clear state. Also to create temporary render target textures.
        // When empty this render pass will render to the active camera render target.
        // You should never call CommandBuffer.SetRenderTarget. Instead call <c>ConfigureTarget</c> and <c>ConfigureClear</c>.
        // The render pipeline will ensure target setup and clearing happens in an performance manner.
        protected override void Setup(ScriptableRenderContext renderContext, CommandBuffer cmd)
        {
            m_RenderTarget = RTHandles.Alloc(Vector2.one,
                slices: TextureXR.slices, dimension: TextureXR.dimension,
                colorFormat: GraphicsFormat.R16G16B16A16_SFloat, useDynamicScale: true,
                depthBufferBits: DepthBits.None, msaaSamples: MSAASamples.None,
                filterMode: FilterMode.Point, wrapMode: TextureWrapMode.Clamp, name: "_GaussianSplatRT");
        }

        protected override void AggregateCullingParameters(ref ScriptableCullingParameters cullingParameters, HDCamera hdCamera)
        {
            var system = GaussianSplat3DRenderSystem.instance;
            if (system.GatherSplatsForCamera(hdCamera.camera) && system.HasDirectSplats)
                system.SubmitDirectSplatsForCamera(hdCamera.camera);
        }

        protected override void Execute(CustomPassContext ctx)
        {
            var cam = ctx.hdCamera.camera;

            var system = GaussianSplat3DRenderSystem.instance;
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
                GaussianSplat3DRenderSystem.instance.SortAndRenderCompositeSplats(ctx.hdCamera.camera, ctx.cmd, m_ConvertCompositeGammaToLinear);

            // compose
            ctx.cmd.BeginSample(GaussianSplat3DRenderSystem.s_ProfCompose);
            CoreUtils.SetRenderTarget(ctx.cmd, ctx.cameraColorBuffer, ClearFlag.None);
            CoreUtils.DrawFullScreen(ctx.cmd, matComposite, ctx.propertyBlock, shaderPassId: 0);
            ctx.cmd.EndSample(GaussianSplat3DRenderSystem.s_ProfCompose);
        }

        protected override void Cleanup()
        {
            m_RenderTarget?.Release();
        }
    }
}

#endif
