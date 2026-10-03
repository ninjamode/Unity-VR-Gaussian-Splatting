// SPDX-License-Identifier: MIT
#if GAUSSIANS_ENABLE_URP
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace Gaussians
{
    /// <summary>Schedules the 3DGS (including 4DGS) and 2DGS paths needed by each camera.</summary>
    [DisallowMultipleRendererFeature("Gaussians")]
    public sealed class GaussiansURPFeature : ScriptableRendererFeature
    {
        [Tooltip("Convert the 3D composite group from gamma to linear after accumulation. Preserves legacy 3D color when enabled. Direct 3D renderers and 2D renderers control conversion individually.")]
        public bool m_ConvertCompositeGammaToLinear = true;

        GaussianSplat3DURPPasses m_ThreeD;
        GaussianSplat2DURPPasses m_TwoD;

        public override void Create()
        {
            m_ThreeD = new GaussianSplat3DURPPasses();
            m_TwoD = new GaussianSplat2DURPPasses();
            m_ThreeD.Create();
            m_TwoD.Create();
        }

        public override void OnCameraPreCull(ScriptableRenderer renderer, in CameraData cameraData)
        {
            m_TwoD.OnCameraPreCull(renderer, cameraData);
            m_ThreeD.OnCameraPreCull(renderer, cameraData);
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            // Keep the composite groups separate: they have different color-space semantics.
            // Equal-event passes preserve this order; both depth passes run after transparents.
            m_ThreeD.m_ConvertCompositeGammaToLinear = m_ConvertCompositeGammaToLinear;
            m_TwoD.AddRenderPasses(renderer, ref renderingData);
            m_ThreeD.AddRenderPasses(renderer, ref renderingData);
        }

    }
}
#endif
