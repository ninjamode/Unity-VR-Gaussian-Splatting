using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.XR;

namespace Gaussians.Benchmark
{
    [Serializable] public sealed class GaussianBenchmarkXrInfo
    {
        public string display, stereoMode, refreshRateHz, frameBudgetMs;
        public string cameraPolicy = "Authored center-camera path; automatic camera transform tracking disabled. Runtime eye offsets/projections and compositor reprojection remain device-controlled.";
        public int eyeWidth, eyeHeight;
        public float eyeResolutionScale, viewportScale;
    }

    public static class GaussianBenchmarkXr
    {
        public static event Action<Camera> ValidatePipelineCamera;
        public static void CheckCamera(Camera camera) => ValidatePipelineCamera?.Invoke(camera);
        static readonly List<XRDisplaySubsystem> Displays = new();
        public static XRDisplaySubsystem RunningDisplay()
        {
            SubsystemManager.GetSubsystems(Displays);
            foreach (var display in Displays) if (display.running) return display;
            return null;
        }

        public static string ModeError(GaussianBenchmarkConfig.RunMode mode, bool xrRunning)
        {
            if (mode == GaussianBenchmarkConfig.RunMode.XrFrameBudget && !xrRunning)
                return "XR Frame Budget requires a running XR display. Enable an XR loader and Initialize XR on Startup for the build target.";
            if (mode == GaussianBenchmarkConfig.RunMode.DesktopThroughput && xrRunning)
                return "An XR display is running. Select XR Frame Budget instead of Desktop Throughput.";
            return null;
        }

        public static GaussianBenchmarkXrInfo Capture(XRDisplaySubsystem display)
        {
            bool refreshAvailable = display.TryGetDisplayRefreshRate(out float refresh) && float.IsFinite(refresh) && refresh > 0;
            return new GaussianBenchmarkXrInfo
            {
                display = display.SubsystemDescriptor.id, stereoMode = XRSettings.stereoRenderingMode.ToString(),
                eyeWidth = XRSettings.eyeTextureWidth, eyeHeight = XRSettings.eyeTextureHeight,
                eyeResolutionScale = XRSettings.eyeTextureResolutionScale, viewportScale = XRSettings.renderViewportScale,
                refreshRateHz = refreshAvailable ? refresh.ToString("R", CultureInfo.InvariantCulture) : "unavailable",
                frameBudgetMs = refreshAvailable ? (1000f / refresh).ToString("R", CultureInfo.InvariantCulture) : "unavailable"
            };
        }

        public static GaussianBenchmarkTrial.XrTiming Sample(XRDisplaySubsystem display, int observation)
        {
            return new GaussianBenchmarkTrial.XrTiming
            {
                observation = observation,
                appGpu = Milliseconds(display.TryGetAppGPUTimeLastFrame(out float app), app),
                compositorGpu = Milliseconds(display.TryGetCompositorGPUTimeLastFrame(out float compositor), compositor)
            };
        }

        public static double Milliseconds(bool available, float seconds) => available && float.IsFinite(seconds) && seconds > 0
            ? seconds * 1000d : double.NaN;
    }
}
