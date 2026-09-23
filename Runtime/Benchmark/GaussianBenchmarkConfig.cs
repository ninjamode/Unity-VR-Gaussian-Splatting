using UnityEngine;

namespace Gaussians.Benchmark
{
    [CreateAssetMenu(menuName = "Gaussians/Benchmark Config")]
    public sealed class GaussianBenchmarkConfig : ScriptableObject
    {
        public enum RunMode { DesktopThroughput, XrFrameBudget }
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

        public string ValidationError => warmupFrames < 1 || measuredFrames < 2 || repetitions < 1
            ? "Warm-up/repetitions must be positive and measurement requires at least two frames."
            : !System.Enum.IsDefined(typeof(RunMode), mode) ? "Unknown benchmark run mode."
            : mode == RunMode.DesktopThroughput && (width < 1 || height < 1) ? "Resolution must be positive."
            : !float.IsFinite(simulationStep) || simulationStep <= 0 ? "Simulation step must be finite and positive."
            : string.IsNullOrWhiteSpace(outputDirectory) ? "An output directory is required." : null;
    }
}
