using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Gaussians.Benchmark
{
    public static class GaussianBenchmarkUrp
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Register()
        {
            GaussianBenchmarkXr.ValidatePipelineCamera -= ValidateCamera;
            GaussianBenchmarkXr.ValidatePipelineCamera += ValidateCamera;
        }

        public static void ValidateCamera(Camera camera)
        {
            if (GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset
                && camera.TryGetComponent<UniversalAdditionalCameraData>(out var data) && !data.allowXRRendering)
                throw new InvalidOperationException("Enable Allow XR Rendering on the benchmark camera's URP settings.");
        }
    }
}
