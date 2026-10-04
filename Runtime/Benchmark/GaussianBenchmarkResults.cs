using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

namespace Gaussians.Benchmark
{
    public static class GaussianBenchmarkStatistics
    {
        public static double Percentile(IEnumerable<double> values, double fraction)
        {
            var sorted = values.Where(v => v > 0 && !double.IsNaN(v) && !double.IsInfinity(v)).OrderBy(v => v).ToArray();
            if (sorted.Length == 0) return double.NaN;
            double index = Math.Clamp(fraction, 0, 1) * (sorted.Length - 1);
            int lower = (int)index, upper = Math.Min(lower + 1, sorted.Length - 1);
            return sorted[lower] + (sorted[upper] - sorted[lower]) * (index - lower);
        }
    }

    [Serializable] public sealed class GaussianBenchmarkCameraInfo
    {
        public float fieldOfView, aspect, nearClip, farClip, orthographicSize;
        public bool orthographic, hdr, msaa;
        public int cullingMask;
        public Rect viewport;
        public Matrix4x4 projection;
        public static GaussianBenchmarkCameraInfo Capture(Camera camera) => new()
        {
            fieldOfView = camera.fieldOfView, aspect = camera.aspect, nearClip = camera.nearClipPlane,
            farClip = camera.farClipPlane, orthographicSize = camera.orthographicSize, orthographic = camera.orthographic,
            hdr = camera.allowHDR, msaa = camera.allowMSAA, cullingMask = camera.cullingMask, viewport = camera.rect,
            projection = camera.projectionMatrix
        };
    }

    [Serializable] public sealed class GaussianBenchmarkTrial
    {
        public string experiment, variant, view, status = "running", error, config, camera, extensions;
        public int repetition, width, height, renderWidth, renderHeight, executionOrder;
        public string phase = "timing";
        public GaussianBenchmarkRenderers.RendererInfo[] renderers;
        public GaussianBenchmarkRenderers.StereoPreparationInfo[] stereoPreparation;
        public int inputSamples, cpuSamples, gpuSamples;
        public GaussianBenchmarkXrInfo xr;
        public int xrAppGpuSamples, xrCompositorGpuSamples;
        public int xrAppGpuOverBudgetSamples = -1;
        [NonSerialized] public readonly List<Input> inputs = new();
        [NonSerialized] public readonly List<FrameTiming> timings = new();
        [NonSerialized] public readonly List<XrTiming> xrTimings = new();
        public struct Input { public int sample; public double elapsed; public Vector3 position; public Quaternion rotation; }
        public struct XrTiming { public int observation, droppedFrames, presentedFrames; public double appGpu, compositorGpu; }
    }

    [Serializable] public sealed class GaussianBenchmarkRunInfo
    {
        public int schemaVersion = 7;
        public string utc, unity, device, cpu, gpu, graphicsApi, pipeline, scene, buildGuid, buildMetadata, mode;
        public bool editor, development;
        public string timingStatus = "Unavailable values have no valid device sample. Timings are whole-frame; source timestamps are not mapped to input sample indices.";
        public string status = "running", error;
        public List<GaussianBenchmarkTrial> trials = new();
    }

    public sealed class GaussianBenchmarkResults
    {
        public string DirectoryPath { get; }
        public GaussianBenchmarkRunInfo Info { get; }
        static readonly CultureInfo Culture = CultureInfo.InvariantCulture;
        public GaussianBenchmarkResults(string directory, string scene)
        {
            DirectoryPath = Path.Combine(Path.IsPathRooted(directory) ? directory : Path.Combine(Application.persistentDataPath, directory),
                DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(DirectoryPath);
            var metadata = Resources.Load<TextAsset>("GaussianBenchmarkBuildInfo");
            Info = new GaussianBenchmarkRunInfo
            {
                utc = DateTime.UtcNow.ToString("O"), unity = Application.unityVersion,
                device = SystemInfo.deviceModel, cpu = SystemInfo.processorType, gpu = SystemInfo.graphicsDeviceName,
                graphicsApi = SystemInfo.graphicsDeviceType.ToString(), scene = scene,
                pipeline = GraphicsSettings.currentRenderPipeline ? GraphicsSettings.currentRenderPipeline.name : "Built-in",
                editor = Application.isEditor, development = Debug.isDebugBuild, buildGuid = Application.buildGUID,
                buildMetadata = metadata ? metadata.text : "unavailable (Editor or non-benchmark build)"
            };
            File.WriteAllText(Path.Combine(DirectoryPath, "samples.csv"), "experiment,variant,view,repetition,record,sample,source_timestamp,elapsed_ms,cpu_ms,gpu_ms,x,y,z,qx,qy,qz,qw\n");
            File.WriteAllText(Path.Combine(DirectoryPath, "summary.csv"), "experiment,variant,view,repetition,status,metric,valid_samples,median_ms,p95_ms,p99_ms\n");
            Save();
        }
        public void Save() => File.WriteAllText(Path.Combine(DirectoryPath, "run.json"), JsonUtility.ToJson(Info, true));
        public static string Quote(string value) => "\"" + (value ?? "").Replace("\"", "\"\"") + "\"";
        static string Number(double value) => double.IsNaN(value) || double.IsInfinity(value) ? "unavailable" : value.ToString("R", Culture);
        static string Timing(double value) => value > 0 ? Number(value) : "unavailable";

        public void WriteTrial(GaussianBenchmarkTrial trial)
        {
            if (trial.phase == "diagnostics")
            {
                Info.trials.Add(trial);
                Save();
                return;
            }
            var prefix = string.Join(",", Quote(trial.experiment), Quote(trial.variant), Quote(trial.view), trial.repetition.ToString(Culture));
            var csv = new StringBuilder();
            foreach (var s in trial.inputs)
                csv.AppendLine(string.Join(",", prefix, "input", s.sample.ToString(Culture), "", Number(s.elapsed), "unavailable", "unavailable",
                    Number(s.position.x), Number(s.position.y), Number(s.position.z), Number(s.rotation.x), Number(s.rotation.y), Number(s.rotation.z), Number(s.rotation.w)));
            foreach (var s in trial.timings)
                csv.AppendLine(string.Join(",", prefix, "frame_timing", "", s.frameStartTimestamp.ToString(Culture), "unavailable", Timing(s.cpuFrameTime), Timing(s.gpuFrameTime), "", "", "", "", "", "", ""));
            File.AppendAllText(Path.Combine(DirectoryPath, "samples.csv"), csv.ToString());
            var summary = new StringBuilder();
            AddSummary(summary, prefix, trial.status, "elapsed", trial.inputs.Select(s => s.elapsed));
            trial.cpuSamples = AddSummary(summary, prefix, trial.status, "cpu", trial.timings.Select(s => s.cpuFrameTime));
            trial.gpuSamples = AddSummary(summary, prefix, trial.status, "gpu", trial.timings.Select(s => s.gpuFrameTime));
            if (trial.xr != null)
            {
                string path = Path.Combine(DirectoryPath, "xr-timings.csv");
                if (!File.Exists(path)) File.WriteAllText(path, "experiment,variant,view,repetition,observation_sample,xr_app_gpu_ms,xr_compositor_gpu_ms,xr_dropped_frames_reported,xr_presented_frames_reported\n");
                var xrCsv = new StringBuilder();
                foreach (var timing in trial.xrTimings)
                    xrCsv.AppendLine(string.Join(",", prefix, timing.observation.ToString(Culture), Number(timing.appGpu), Number(timing.compositorGpu), timing.droppedFrames < 0 ? "unavailable" : timing.droppedFrames.ToString(Culture), timing.presentedFrames < 0 ? "unavailable" : timing.presentedFrames.ToString(Culture)));
                File.AppendAllText(path, xrCsv.ToString());
                if (double.TryParse(trial.xr.frameBudgetMs, NumberStyles.Float, Culture, out double budget) && budget > 0)
                    trial.xrAppGpuOverBudgetSamples = trial.xrTimings.Count(t => double.IsFinite(t.appGpu) && t.appGpu > budget);
                trial.xrAppGpuSamples = AddSummary(summary, prefix, trial.status, "xr_app_gpu", trial.xrTimings.Select(s => s.appGpu));
                trial.xrCompositorGpuSamples = AddSummary(summary, prefix, trial.status, "xr_compositor_gpu", trial.xrTimings.Select(s => s.compositorGpu));
            }
            trial.inputSamples = trial.inputs.Count;
            File.AppendAllText(Path.Combine(DirectoryPath, "summary.csv"), summary.ToString());
            Info.trials.Add(trial);
            // Metadata retains counts, not a growing duplicate of the raw capture buffers.
            trial.inputs.Clear(); trial.timings.Clear(); trial.xrTimings.Clear();
            Save();
        }
        static int AddSummary(StringBuilder output, string prefix, string status, string metric, IEnumerable<double> source)
        {
            var values = source.Where(v => v > 0 && double.IsFinite(v)).ToArray();
            output.AppendLine(string.Join(",", prefix, Quote(status), metric, values.Length.ToString(Culture),
                Number(GaussianBenchmarkStatistics.Percentile(values, .5)), Number(GaussianBenchmarkStatistics.Percentile(values, .95)), Number(GaussianBenchmarkStatistics.Percentile(values, .99))));
            return values.Length;
        }
    }
}
