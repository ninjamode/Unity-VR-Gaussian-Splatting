// SPDX-License-Identifier: MIT
#if GAUSSIANS_ENABLE_HDRP
using Gaussians.ThreeD;
using Gaussians.TwoD;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace Gaussians
{
    /// <summary>Renders 3DGS (including 4DGS) and 2DGS before transparent rendering.</summary>
    [System.Serializable]
    public sealed class GaussiansHDRPPass : CustomPass
    {
        [Tooltip("Convert the 3D composite group from gamma to linear after accumulation. Direct 3D renderers and 2D renderers control conversion individually.")]
        public bool m_ConvertCompositeGammaToLinear = true;

        RTHandle m_ThreeDTarget;
        RTHandle m_TwoDTarget;

        public GaussiansHDRPPass() { name = "Gaussians"; }

        static RTHandle AllocateTarget(string name) => RTHandles.Alloc(Vector2.one,
            slices: TextureXR.slices, dimension: TextureXR.dimension,
            colorFormat: GraphicsFormat.R16G16B16A16_SFloat, useDynamicScale: true,
            depthBufferBits: DepthBits.None, msaaSamples: MSAASamples.None,
            filterMode: FilterMode.Point, wrapMode: TextureWrapMode.Clamp, name: name);

        protected override void AggregateCullingParameters(ref ScriptableCullingParameters cullingParameters, HDCamera hdCamera)
        {
            var threeD = GaussianSplat3DRenderSystem.instance;
            var threeDSelection = threeD.CollectForCamera(hdCamera.camera);
            if (threeDSelection.HasDirect)
                threeD.SubmitDirectSplatsForCamera(threeDSelection);
            var twoD = GaussianSplat2DRenderSystem.instance;
            var twoDSelection = twoD.CollectForCamera(hdCamera.camera);
            if (twoDSelection.HasDirect)
                twoD.SubmitDirectSplatsForCamera(twoDSelection);
        }

        protected override void Execute(CustomPassContext ctx)
        {
            // Match the URP group's order without combining their color-space semantics.
            Execute2D(ctx);
            Execute3D(ctx);
        }

        void Execute3D(CustomPassContext ctx)
        {
            var camera = ctx.hdCamera.camera;
            var system = GaussianSplat3DRenderSystem.instance;
            var selection = system.CollectForCamera(camera);
            if (!selection.HasSplats)
                return;
            if (selection.HasDirect)
                system.PrepareDirectSplats(selection, ctx.cmd);
            if (!selection.HasComposite)
                return;

            m_ThreeDTarget ??= AllocateTarget("_GaussianSplatRT");
            ctx.cmd.SetGlobalTexture(m_ThreeDTarget.name, m_ThreeDTarget.nameID);
            CoreUtils.SetRenderTarget(ctx.cmd, m_ThreeDTarget, ctx.cameraDepthBuffer, ClearFlag.Color, Color.clear);
            var composite = system.SortAndRenderCompositeSplats(selection, ctx.cmd, m_ConvertCompositeGammaToLinear);
            ctx.cmd.BeginSample(GaussianSplat3DRenderSystem.s_ProfCompose);
            CoreUtils.SetRenderTarget(ctx.cmd, ctx.cameraColorBuffer, ClearFlag.None);
            CoreUtils.DrawFullScreen(ctx.cmd, composite, ctx.propertyBlock, shaderPassId: 0);
            ctx.cmd.EndSample(GaussianSplat3DRenderSystem.s_ProfCompose);
        }

        void Execute2D(CustomPassContext ctx)
        {
            var camera = ctx.hdCamera.camera;
            var system = GaussianSplat2DRenderSystem.instance;
            var selection = system.CollectForCamera(camera);
            if (!selection.HasSplats)
                return;
            if (selection.HasDirect)
                system.PrepareDirectSplats(selection, ctx.cmd);
            if (!selection.HasComposite)
                return;

            m_TwoDTarget ??= AllocateTarget("_GaussianSplat2DRT");
            ctx.cmd.SetGlobalTexture(m_TwoDTarget.name, m_TwoDTarget.nameID);
            CoreUtils.SetRenderTarget(ctx.cmd, m_TwoDTarget, ctx.cameraDepthBuffer, ClearFlag.Color, Color.clear);
            var composite = system.SortAndRenderCompositeSplats(selection, ctx.cmd);
            ctx.cmd.BeginSample(GaussianSplat2DRenderSystem.s_ProfCompose);
            CoreUtils.SetRenderTarget(ctx.cmd, ctx.cameraColorBuffer, ClearFlag.None);
            CoreUtils.DrawFullScreen(ctx.cmd, composite, ctx.propertyBlock, shaderPassId: 0);
            ctx.cmd.EndSample(GaussianSplat2DRenderSystem.s_ProfCompose);
        }

        protected override void Cleanup()
        {
            m_ThreeDTarget?.Release();
            m_TwoDTarget?.Release();
            m_ThreeDTarget = null;
            m_TwoDTarget = null;
        }
    }
}
#endif
