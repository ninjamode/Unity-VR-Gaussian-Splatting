// SPDX-License-Identifier: MIT
#if GAUSSIANS_ENABLE_URP
using Gaussians.Core;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.RenderGraphModule;

namespace Gaussians
{
    internal static class GaussiansURPUtilities
    {
        // Match URP 17's DrawObjectsPass policy without depending on its internal
        // XRPassUniversal API. AVP permits intermediate foveation; Android/WSA XR
        // requires the default viewport scale. Recheck this policy on URP upgrades.
        internal static bool CanFoveateTarget(bool xrSupported, bool mobileXR, float viewportScale, bool backBuffer)
            => xrSupported && (backBuffer || !mobileXR || viewportScale == 1.0f);

        internal static void ConfigureFoveation(IRasterRenderGraphBuilder builder, UniversalCameraData camera,
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

        internal static GaussianSplatCameraState CaptureCamera(in CameraData camera)
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
                Vector2Int.RoundToInt(size), Vector2Int.RoundToInt(rightSize), viewCount,
                indirectInstanceMultiplier: viewCount > 1 && !SystemInfo.supportsMultiview ? viewCount : 1);
        }

        internal static GaussianSplatCameraState CaptureCamera(UniversalCameraData camera)
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
                Vector2Int.RoundToInt(size), Vector2Int.RoundToInt(rightSize), viewCount,
                indirectInstanceMultiplier: viewCount > 1 && !SystemInfo.supportsMultiview ? viewCount : 1);
        }

    }
}
#endif
