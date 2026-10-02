using UnityEngine;

namespace Gaussians.Benchmark
{
    [CreateAssetMenu(menuName = "Gaussians/Benchmark Config")]
    public sealed class GaussianBenchmarkConfig : ScriptableObject
    {
        public enum RunMode { DesktopThroughput, XrFrameBudget }
        public enum CaptureMode { TimingOnly, DiagnosticsOnly, TimingAndDiagnostics }
        [Tooltip("Diagnostics run separately after all timing trials, once per variant/view.")]
        public CaptureMode captureMode;
        public bool randomizeOrder;
        [Tooltip("Keep each explicitly grouped Direct/Composite pair adjacent; alternate the leading path between repetitions.")]
        public bool pairRenderPaths;
        [Min(1), Tooltip("First repetition label. Use 1, 2 or 3 with one repetition for separate cooled sessions.")]
        public int firstRepetition = 1;
        [Min(0), Tooltip("XR: require this actual refresh rate before measuring; zero accepts the runtime rate.")]
        public float requiredRefreshRateHz;
        public bool disableXrFoveation;
        public bool requireXrAppGpuTiming;
        public int randomSeed = 260926;
        [Min(2)] public int diagnosticPositions = 32;
        [Min(0), Tooltip("Additional consecutive measured-path samples around the midpoint for temporal inspection.")]
        public int diagnosticSequenceFrames = 16;
        [Tooltip("Desktop uncaps rendering. XR preserves headset pacing and uses the runtime's eye resolution.")]
        public RunMode mode;
        [Min(1)] public int warmupFrames = 120;
        [Min(2)] public int measuredFrames = 600;
        [Min(1)] public int repetitions = 3;
        [Min(1), Tooltip("Desktop only; XR resolution comes from the XR runtime and pipeline settings.")] public int width = 1920;
        [Min(1), Tooltip("Desktop only; XR resolution comes from the XR runtime and pipeline settings.")] public int height = 1080;
        [Min(0.00001f)] public float simulationStep = 1f / 60f;
        [Tooltip("Relative paths are beneath Application.persistentDataPath.")]
        public string outputDirectory = "GaussianBenchmarks";
        public bool developmentBuild;

        public string ValidationError => warmupFrames < 1 || measuredFrames < 2 || repetitions < 1 || firstRepetition < 1
            ? "Warm-up/repetitions must be positive and measurement requires at least two frames."
            : !System.Enum.IsDefined(typeof(CaptureMode), captureMode) ? "Unknown capture mode."
            : captureMode != CaptureMode.TimingOnly && (diagnosticPositions < 2 || diagnosticPositions > measuredFrames || diagnosticSequenceFrames < 0 || diagnosticSequenceFrames > measuredFrames) ? "Diagnostic sample counts must fit the measured path."
            : captureMode != CaptureMode.TimingOnly && mode != RunMode.DesktopThroughput ? "Image/count diagnostics currently require desktop mono rendering."
            : !System.Enum.IsDefined(typeof(RunMode), mode) ? "Unknown benchmark run mode."
            : mode == RunMode.DesktopThroughput && (width < 1 || height < 1) ? "Resolution must be positive."
            : !float.IsFinite(simulationStep) || simulationStep <= 0 ? "Simulation step must be finite and positive."
            : !float.IsFinite(requiredRefreshRateHz) || requiredRefreshRateHz < 0 ? "Required refresh rate must be finite and nonnegative."
            : string.IsNullOrWhiteSpace(outputDirectory) ? "An output directory is required." : null;
    }
}
