// SPDX-License-Identifier: MIT
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.XR;

namespace Gaussians.Core
{
    // Built-in camera targets must retain XR array shape and depth-compatible MSAA.
    internal static class GaussianRenderTargets
    {
        internal static RenderTextureDescriptor Accumulation(Camera camera)
        {
            var descriptor = camera.targetTexture != null ? camera.targetTexture.descriptor :
                camera.stereoEnabled ? XRSettings.eyeTextureDesc :
                new RenderTextureDescriptor(camera.pixelWidth, camera.pixelHeight)
                {
                    msaaSamples = camera.allowMSAA ? Mathf.Max(1, QualitySettings.antiAliasing) : 1
                };
            descriptor.depthBufferBits = 0;
            descriptor.depthStencilFormat = GraphicsFormat.None;
            descriptor.graphicsFormat = GraphicsFormat.R16G16B16A16_SFloat;
            descriptor.sRGB = false;
            descriptor.bindMS = false;
            descriptor.enableRandomWrite = false;
            return descriptor;
        }

        internal static int FullscreenInstances(Camera camera) =>
            camera.stereoEnabled && XRSettings.stereoRenderingMode == XRSettings.StereoRenderingMode.SinglePassInstanced ? 2 : 1;
    }
}
