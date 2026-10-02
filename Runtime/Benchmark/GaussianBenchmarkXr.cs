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
        public float eyeResolutionScale, viewportScale, foveationLevel;
        public string foveationFlags;
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
                foveationLevel = display.foveatedRenderingLevel, foveationFlags = display.foveatedRenderingFlags.ToString(),
                refreshRateHz = refreshAvailable ? refresh.ToString("R", CultureInfo.InvariantCulture) : "unavailable",
                frameBudgetMs = refreshAvailable ? (1000f / refresh).ToString("R", CultureInfo.InvariantCulture) : "unavailable"
            };
        }

        public static string SettingsError(GaussianBenchmarkConfig config, bool refreshAvailable, float refresh, float foveation)
        {
            if (config.requiredRefreshRateHz > 0 && (!refreshAvailable || !float.IsFinite(refresh) || Mathf.Abs(refresh - config.requiredRefreshRateHz) > .1f))
                return $"XR refresh must be {config.requiredRefreshRateHz} Hz; actual is {(refreshAvailable ? refresh.ToString(CultureInfo.InvariantCulture) : "unavailable")}.";
            if (config.disableXrFoveation && (!float.IsFinite(foveation) || foveation != 0))
                return "Foveation must remain off for this benchmark.";
            return null;
        }

        public static GaussianBenchmarkTrial.XrTiming Sample(XRDisplaySubsystem display, int observation)
        {
            // Preserve provider-reported counts without assuming cumulative or per-frame semantics.
            // Unity providers may differ; inspect the pilot before deriving a missed-frame total.
            if (!display.TryGetDroppedFrameCount(out int dropped)) dropped = -1;
            if (!display.TryGetFramePresentCount(out int presented)) presented = -1;
            return new GaussianBenchmarkTrial.XrTiming
            {
                observation = observation, droppedFrames = dropped, presentedFrames = presented,
                appGpu = Milliseconds(display.TryGetAppGPUTimeLastFrame(out float app), app),
                compositorGpu = Milliseconds(display.TryGetCompositorGPUTimeLastFrame(out float compositor), compositor)
            };
        }

        public static double Milliseconds(bool available, float seconds) => available && float.IsFinite(seconds) && seconds > 0
            ? seconds * 1000d : double.NaN;
    }
}
