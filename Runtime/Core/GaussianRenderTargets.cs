// SPDX-License-Identifier: MIT
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.XR;

namespace Gaussians.Core
{
    // Built-in camera targets must retain XR array shape and depth-compatible MSAA.
    internal static class GaussianRenderTargets
    {
        static readonly List<XRDisplaySubsystem> s_Displays = new();
        static XRDisplaySubsystem s_RunningDisplay;

        internal static XRDisplaySubsystem RunningXrDisplay()
        {
            if (s_RunningDisplay != null && s_RunningDisplay.running)
                return s_RunningDisplay;

            SubsystemManager.GetSubsystems(s_Displays);
            foreach (var display in s_Displays)
                if (display.running)
                    return s_RunningDisplay = display;

            return s_RunningDisplay = null;
        }

        internal static bool TryGetXrPass(XRDisplaySubsystem display, int eyeIndex, out XRDisplaySubsystem.XRRenderPass pass)
        {
            pass = default;
            if (display == null || !display.running)
                return false;

            int count = display.GetRenderPassCount();
            if (count == 0)
                return false;

            display.GetRenderPass(Mathf.Min(eyeIndex, count - 1), out pass);
            return pass.renderTargetDesc.width > 0 && pass.renderTargetDesc.height > 0;
        }

        internal static bool TryGetXrPass(Camera camera, out XRDisplaySubsystem.XRRenderPass pass)
        {
            pass = default;
            return camera.stereoEnabled && TryGetXrPass(RunningXrDisplay(),
                camera.stereoActiveEye == Camera.MonoOrStereoscopicEye.Right ? 1 : 0, out pass);
        }

        internal static bool IsInstancedStereo(XRDisplaySubsystem.XRRenderPass pass) =>
            pass.renderTargetDesc.dimension == TextureDimension.Tex2DArray &&
            pass.GetRenderParameterCount() == 2 && !SystemInfo.supportsMultiview;

        internal static RenderTextureDescriptor Accumulation(Camera camera)
        {
            var descriptor = camera.targetTexture != null ? camera.targetTexture.descriptor :
                TryGetXrPass(camera, out var pass) ? pass.renderTargetDesc :
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
            TryGetXrPass(camera, out var pass) && IsInstancedStereo(pass) ? 2 : 1;
    }
}
