#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Gaussians.FourD.Diagnostics
{
    // Intentionally separate from production rendering. No component is created without the flag.
    public sealed class Gaussian4DBenchmark : MonoBehaviour
    {
        const int WarmupFrames = 120, MeasuredFrames = 300, Trials = 3, TimingGuardFrames = 16;
        static readonly float[] ModelTimes = { 0f, .125f, .25f, .375f, .5f, .625f, .75f, .875f, 1f };
        readonly FrameTiming[] m_Timings = new FrameTiming[1];
        readonly HashSet<ulong> m_Seen = new(MeasuredFrames);
        readonly List<FrameTiming> m_Samples = new(MeasuredFrames + 16);
        readonly List<double> m_WallSamples = new(MeasuredFrames);
        readonly StringBuilder m_Csv = new();
        int m_PreviousVSync, m_PreviousTargetRate;
        bool m_PreviousRunInBackground;
        string m_Output;
        ulong m_Baseline;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "--gaussians-4d-benchmark") < 0) return;
            var instance = new GameObject("4DGS opt-in benchmark").AddComponent<Gaussian4DBenchmark>();
            DontDestroyOnLoad(instance.gameObject);
        }

        IEnumerator Start()
        {
            m_PreviousVSync = QualitySettings.vSyncCount;
            m_PreviousTargetRate = Application.targetFrameRate;
            m_PreviousRunInBackground = Application.runInBackground;
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = -1;
            Application.runInBackground = true;
            var args = Environment.GetCommandLineArgs();
            int outputFlag = Array.IndexOf(args, "--gaussians-4d-benchmark-output");
            m_Output = outputFlag >= 0 && outputFlag + 1 < args.Length ? args[outputFlag + 1]
                : Path.Combine(Application.persistentDataPath, "4dgs-benchmark-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + ".csv");
            m_Csv.AppendLine("utc,unity,device,gpu,api,scene,model,gaussians,path,trial,width,height,sequence,warmup_frames,measured_frames,record,sample,cpu_frame_ms,gpu_frame_ms,wall_frame_ms,gpu_deformation_ms,status");
            try
            {
                int sceneCount = SceneManager.sceneCountInBuildSettings;
                for (int sceneIndex = 0; sceneIndex < Math.Max(1, sceneCount); ++sceneIndex)
                {
                    if (sceneCount > 0) yield return SceneManager.LoadSceneAsync(sceneIndex);
                    yield return null;
                    var renderers = FindObjectsByType<GaussianSplat4D>(FindObjectsSortMode.None);
                    var players = renderers;
                    var rendererEnabled = Array.ConvertAll(renderers, r => r.Renderer.enabled);
                    var playerEnabled = Array.ConvertAll(players, p => p.enabled);
                    var playerPlaying = Array.ConvertAll(players, p => p.IsPlaying);
                    foreach (var player in players) player.Pause();
                    foreach (var renderer in renderers) renderer.Renderer.enabled = false;
                    try
                    {
                        for (int model = 0; model < renderers.Length; ++model)
                        {
                            var renderer = renderers[model];
                            if (!renderer.m_Asset) continue;
                            bool enabled = renderer.enabled;
                            float modelTime = renderer.m_ModelTime;
                            var backend = renderer.m_Backend;
                            try
                            {
                                for (int trial = 0; trial < Trials; ++trial)
                                for (int path = 0; path < 3; ++path)
                                {
                                    renderer.Renderer.enabled = false;
                                    renderer.enabled = path != 0;
                                    renderer.Pause();
                                    renderer.m_Backend = path == 2 ? GaussianSplat4D.DeformationBackend.Native
                                        : GaussianSplat4D.DeformationBackend.Onnx;
                                    renderer.Rebuild();
                                    renderer.Renderer.enabled = true;
                                    string pathName = path == 0 ? "canonical" : path == 1 ? "onnx" : "native";
                                    Debug.Log($"4DGS benchmark: {SceneManager.GetActiveScene().name}/{renderer.m_Asset.name}/{pathName}, trial {trial + 1}");
                                    m_Baseline = 0;
                                    for (int i = 0; i < WarmupFrames; ++i)
                                    {
                                        renderer.m_ModelTime = ModelTimes[i % ModelTimes.Length];
                                        yield return null;
                                        CollectTimings(false);
                                    }
                                    m_Seen.Clear(); m_Samples.Clear(); m_WallSamples.Clear();
                                    for (int frame = 0; frame < MeasuredFrames; ++frame)
                                    {
                                        renderer.m_ModelTime = ModelTimes[(WarmupFrames + frame) % ModelTimes.Length];
                                        double start = Time.realtimeSinceStartupAsDouble;
                                        yield return null;
                                        m_WallSamples.Add((Time.realtimeSinceStartupAsDouble - start) * 1000);
                                        // Unity returns completed frames with four-frame latency. Refresh
                                        // throughout, discard a conservative 16-poll boundary, and take only
                                        // the latest result so a historical warmup backlog cannot enter.
                                        CollectTimings(frame >= TimingGuardFrames);
                                    }
                                    string status = renderer.Error ?? (path != 0 && renderer.EvaluationCount == 0 ? "no_evaluation" : "ok");
                                    WriteTrial(renderer, pathName, trial + 1, status);
                                    renderer.Renderer.enabled = false;
                                }
                            }
                            finally
                            {
                                renderer.Renderer.enabled = false;
                                renderer.enabled = enabled; renderer.m_Backend = backend; renderer.m_ModelTime = modelTime;
                                renderer.Rebuild();
                            }
                        }
                    }
                    finally
                    {
                        for (int i = 0; i < renderers.Length; ++i) if (renderers[i]) renderers[i].Renderer.enabled = rendererEnabled[i];
                        for (int i = 0; i < players.Length; ++i)
                            if (players[i]) { players[i].enabled = playerEnabled[i]; if (playerPlaying[i]) players[i].Play(); else players[i].Pause(); }
                    }
                }
            }
            finally
            {
                QualitySettings.vSyncCount = m_PreviousVSync;
                Application.targetFrameRate = m_PreviousTargetRate;
                Application.runInBackground = m_PreviousRunInBackground;
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(m_Output)));
                File.WriteAllText(m_Output, m_Csv.ToString());
                Debug.Log("4DGS benchmark CSV: " + Path.GetFullPath(m_Output));
            }
            if (!Application.isEditor) Application.Quit();
            Destroy(gameObject);
        }

        void CollectTimings(bool record)
        {
            FrameTimingManager.CaptureFrameTimings();
            uint count = FrameTimingManager.GetLatestTimings((uint)m_Timings.Length, m_Timings);
            for (int i = 0; i < count; ++i)
            {
                var timing = m_Timings[i];
                if (!record) { m_Baseline = Math.Max(m_Baseline, timing.frameStartTimestamp); continue; }
                if (timing.frameStartTimestamp <= m_Baseline || !m_Seen.Add(timing.frameStartTimestamp)) continue;
                m_Samples.Add(timing);
            }
        }

        void WriteTrial(GaussianSplat4D renderer, string path, int trial, string status)
        {
            string prefix = string.Join(",", Escape(DateTime.UtcNow.ToString("O")), Escape(Application.unityVersion),
                Escape(SystemInfo.deviceModel), Escape(SystemInfo.graphicsDeviceName), Escape(SystemInfo.graphicsDeviceType.ToString()),
                Escape(SceneManager.GetActiveScene().path), Escape(renderer.name + "/" + renderer.m_Asset.name),
                renderer.m_Asset.Data ? renderer.m_Asset.Data.GaussianCount.ToString() : "0", path, trial.ToString(),
                Screen.width.ToString(), Screen.height.ToString(), Escape("0|0.125|0.25|0.375|0.5|0.625|0.75|0.875|1"),
                WarmupFrames.ToString(), MeasuredFrames.ToString());
            for (int i = 0; i < m_WallSamples.Count; ++i)
                m_Csv.AppendLine($"{prefix},wall,{i},unavailable,unavailable,{Number(m_WallSamples[i])},unavailable,{Escape(status)}");
            if (m_Samples.Count == 0)
                m_Csv.AppendLine($"{prefix},frame_timing,0,unavailable,unavailable,unavailable,unavailable,{Escape(status + ";frame_timing_unavailable")}");
            m_Samples.Sort((a, b) => a.frameStartTimestamp.CompareTo(b.frameStartTimestamp));
            for (int i = 0; i < m_Samples.Count; ++i)
                m_Csv.AppendLine($"{prefix},frame_timing,{i},{Number(m_Samples[i].cpuFrameTime)},{Number(m_Samples[i].gpuFrameTime)},unavailable,unavailable,{Escape(status)}");
        }
        static string Number(double value) => value > 0 && !double.IsNaN(value) && !double.IsInfinity(value)
            ? value.ToString("R", CultureInfo.InvariantCulture) : "unavailable";
        static string Escape(string value) => "\"" + (value ?? "").Replace("\"", "\"\"") + "\"";
    }
}
#endif
