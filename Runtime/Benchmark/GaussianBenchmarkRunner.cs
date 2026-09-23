using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.XR;

namespace Gaussians.Benchmark
{
    public sealed class GaussianBenchmarkRunner : MonoBehaviour
    {
        public static GaussianBenchmarkRunner Active { get; private set; }
        public static string LastOutput { get; private set; }
        public static string LastStatus { get; private set; }
        public string Progress { get; private set; }
        GaussianBenchmarkExperiment[] sources, originals;
        bool[] activeStates;
        Camera[] cameras;
        bool[] cameraStates;
        GameObject staging;
        GaussianBenchmarkExperiment clone;
        GaussianBenchmarkExtension[] extensions, sourceExtensions;
        GaussianBenchmarkTrial trial;
        GaussianBenchmarkResults results;
        int oldVsync, oldRate, oldWidth, oldHeight;
        FullScreenMode oldScreenMode;
        bool oldBackground, configured, finishCalled, cancelled;
        readonly FrameTiming[] timings = new FrameTiming[1];
        readonly HashSet<ulong> seen = new();
        ulong baseline;
        XRDisplaySubsystem xrDisplay;
        bool xrMode, resolutionChanged;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Bootstrap()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (Application.isEditor || Active || !scene.GetRootGameObjects()
                    .SelectMany(root => root.GetComponentsInChildren<GaussianBenchmarkExperiment>())
                    .Any(experiment => experiment.isActiveAndEnabled)) return;
            try { StartRun(); }
            catch (Exception e)
            {
                LastStatus = "failed";
                Debug.LogError("Gaussian benchmark startup failed: " + e);
                if (!Application.isEditor) Application.Quit(1);
            }
        }

        public static GaussianBenchmarkRunner StartRun(GaussianBenchmarkExperiment only = null)
        {
            if (!Application.isPlaying) throw new InvalidOperationException("Benchmarks run in Play mode or a standalone player.");
            if (Active) throw new InvalidOperationException("A benchmark is already running.");
            var experiments = only ? new[] { only } : FindObjectsByType<GaussianBenchmarkExperiment>(FindObjectsSortMode.None)
                .Where(e => e.enabled).OrderBy(e => e.experimentId, StringComparer.Ordinal).ToArray();
            if (experiments.Length == 0) throw new InvalidOperationException("No active experiments in this scene.");
            var ids = new HashSet<string>();
            foreach (var e in experiments)
            {
                string error = e.ValidationError();
                if (error != null) throw new InvalidOperationException(e.name + ": " + error);
                if (!ids.Add(e.experimentId)) throw new InvalidOperationException("Experiment IDs must be unique in a run.");
                foreach (var other in experiments)
                    if (other != e && e.transform.IsChildOf(other.transform)) throw new InvalidOperationException("Experiments cannot be nested.");
            }
            if (experiments.Select(e => e.config.mode).Distinct().Count() != 1)
                throw new InvalidOperationException("All experiments in a run must use the same benchmark mode.");
            Active = new GameObject("Gaussian benchmark runner").AddComponent<GaussianBenchmarkRunner>();
            Active.sources = experiments;
            Active.StartCoroutine(Active.RunGuarded());
            return Active;
        }

        IEnumerator RunGuarded()
        {
            var routine = Execute();
            string failure = null;
            try
            {
                while (true)
                {
                    object next = null; bool moved = false;
                    try { moved = routine.MoveNext(); if (moved) next = routine.Current; }
                    catch (Exception e) { failure = e.ToString(); }
                    if (failure != null || !moved) break;
                    yield return next;
                }
            }
            finally
            {
                (routine as IDisposable)?.Dispose();
                Finish(cancelled ? "cancelled" : failure == null ? "complete" : "failed", failure);
            }
            if (failure != null) Debug.LogError("Gaussian benchmark failed: " + failure);
            if (!Application.isEditor)
                Application.Quit(failure == null ? 0 : 1);
            Destroy(gameObject);
        }

        IEnumerator Execute()
        {
            oldVsync = QualitySettings.vSyncCount; oldRate = Application.targetFrameRate;
            oldBackground = Application.runInBackground; oldWidth = Screen.width; oldHeight = Screen.height; oldScreenMode = Screen.fullScreenMode;
            configured = true;
            xrMode = sources[0].config.mode == GaussianBenchmarkConfig.RunMode.XrFrameBudget;
            var args = Environment.GetCommandLineArgs();
            int outputArg = Array.IndexOf(args, "--gaussians-benchmark-output");
            string directory = outputArg >= 0 && outputArg + 1 < args.Length ? args[outputArg + 1] : sources[0].config.outputDirectory;
            results = new GaussianBenchmarkResults(directory, SceneManager.GetActiveScene().path);
            results.Info.mode = sources[0].config.mode.ToString(); results.Save();
            LastOutput = results.DirectoryPath; LastStatus = "running";
            Debug.Log("Gaussian benchmark output: " + LastOutput);
            if (xrMode)
            {
                Progress = "Waiting for the XR display and app focus";
                double deadline = Time.realtimeSinceStartupAsDouble + 30;
                while (((xrDisplay = GaussianBenchmarkXr.RunningDisplay()) == null || !Application.isFocused)
                       && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
                if (!Application.isFocused) throw new InvalidOperationException("XR benchmark requires the headset app to have focus.");
            }
            else xrDisplay = GaussianBenchmarkXr.RunningDisplay();
            if (GaussianBenchmarkXr.ModeError(sources[0].config.mode, xrDisplay != null || XRSettings.enabled) is string modeError)
                throw new InvalidOperationException(modeError);
            if (xrMode && xrDisplay == null) throw new InvalidOperationException("No running XR display was found.");
            if (!xrMode) { QualitySettings.vSyncCount = 0; Application.targetFrameRate = -1; }
            Application.runInBackground = true;
            // Camera state is restored even when setup, capture or file output throws.
            cameras = FindObjectsByType<Camera>(FindObjectsSortMode.None);
            cameraStates = cameras.Select(c => c.enabled).ToArray();
            foreach (var c in cameras) c.enabled = false;
            originals = FindObjectsByType<GaussianBenchmarkExperiment>(FindObjectsSortMode.None);
            activeStates = originals.Select(s => s.gameObject.activeSelf).ToArray();
            sourceExtensions = originals.SelectMany(e => e.GetComponents<GaussianBenchmarkExtension>()).Where(e => e.enabled).ToArray();
            foreach (var extension in sourceExtensions) extension.CaptureSourceState();
            foreach (var source in originals) source.gameObject.SetActive(false);
            foreach (var source in sources)
            for (int repetition = 0; repetition < source.config.repetitions; repetition++)
            for (int variantIndex = 0; variantIndex < source.variants.Length; variantIndex++)
            for (int segment = 0; segment < source.SegmentCount; segment++)
            {
                var config = source.config;
                if (!Application.isEditor && !xrMode)
                {
                    Screen.SetResolution(config.width, config.height, FullScreenMode.Windowed);
                    resolutionChanged = true;
                }
                // Allow resolution changes to settle before allocating renderer resources.
                for (int i = 0; i < 3; i++) yield return null;
                if (!Application.isEditor && !xrMode && (Screen.width != config.width || Screen.height != config.height))
                    throw new InvalidOperationException($"Requested {config.width}×{config.height}, got {Screen.width}×{Screen.height}.");
                trial = new GaussianBenchmarkTrial { experiment = source.experimentId, variant = source.variants[variantIndex].id,
                    view = source.sequence == GaussianBenchmarkExperiment.CameraSequence.Path ? "path" : source.cameraPoints[segment].name,
                    repetition = repetition + 1, config = JsonUtility.ToJson(config), width = Screen.width, height = Screen.height };
                Progress = $"{trial.experiment}/{trial.variant}/{trial.view}, repetition {trial.repetition}";
                Debug.Log("Gaussian benchmark: " + Progress);
                staging = new GameObject("Benchmark trial");
                staging.SetActive(false);
                staging.transform.SetParent(source.transform.parent, false);
                clone = Instantiate(source, staging.transform, false);
                clone.gameObject.SetActive(true);
                foreach (var variant in clone.variants) variant.subject.SetActive(false);
                var selected = clone.variants[variantIndex];
                selected.subject.SetActive(true);
                foreach (var c in clone.GetComponentsInChildren<Camera>(true)) c.enabled = c == clone.benchmarkCamera;
                if (xrMode)
                {
                    clone.benchmarkCamera.ResetAspect();
                    clone.benchmarkCamera.stereoTargetEye = StereoTargetEyeMask.Both;
                    XRDevice.DisableAutoXRCameraTracking(clone.benchmarkCamera, true);
                }
                else clone.benchmarkCamera.aspect = config.width / (float)config.height;
                GaussianBenchmarkRenderers.Apply(selected.subject, selected.EffectiveSettings);
                extensions = clone.GetComponents<GaussianBenchmarkExtension>().Where(e => e.enabled).ToArray();
                foreach (var extension in extensions) extension.Configure(selected.subject, selected.id);
                clone.ApplyPose(0, segment);
                staging.SetActive(true);
                foreach (var extension in extensions) extension.Begin();
                yield return null;
                var readiness = GaussianBenchmarkRenderers.CreateReadinessCheck(selected.subject);
                CheckTrial(readiness);
                trial.camera = JsonUtility.ToJson(GaussianBenchmarkCameraInfo.Capture(clone.benchmarkCamera));
                trial.renderWidth = clone.benchmarkCamera.scaledPixelWidth; trial.renderHeight = clone.benchmarkCamera.scaledPixelHeight;
                if (xrMode)
                {
                    trial.xr = GaussianBenchmarkXr.Capture(xrDisplay);
                    trial.renderWidth = trial.xr.eyeWidth; trial.renderHeight = trial.xr.eyeHeight;
                    trial.xrTimings.Capacity = config.measuredFrames;
                }
                trial.renderers = GaussianBenchmarkRenderers.Describe(selected.subject);
                trial.inputs.Capacity = config.measuredFrames;
                trial.timings.Capacity = config.measuredFrames;
                seen.Clear(); seen.EnsureCapacity(config.measuredFrames); baseline = 0;
                for (int frame = 0; frame < config.warmupFrames; frame++)
                {
                    SetSample(frame, segment, config.simulationStep);
                    yield return null;
                    Collect(false);
                    CheckTrial(readiness);
                }
                for (int frame = 0; frame < config.measuredFrames; frame++)
                {
                    SetSample(frame, segment, config.simulationStep);
                    var pose = new Pose(clone.benchmarkCamera.transform.position, clone.benchmarkCamera.transform.rotation);
                    double start = Time.realtimeSinceStartupAsDouble;
                    yield return null;
                    trial.inputs.Add(new GaussianBenchmarkTrial.Input { sample = frame, elapsed = (Time.realtimeSinceStartupAsDouble - start) * 1000, position = pose.position, rotation = pose.rotation });
                    // Discard a conservative boundary for delayed device timing; input rows remain independent.
                    Collect(frame >= 16);
                    if (xrMode && frame >= 16) trial.xrTimings.Add(GaussianBenchmarkXr.Sample(xrDisplay, frame));
                    CheckTrial(readiness);
                }
                trial.extensions = string.Join("\n", extensions.Select(e => e.Describe()));
                trial.status = "complete";
                results.WriteTrial(trial); trial = null;
                staging.SetActive(false); Destroy(staging); staging = null; clone = null;
                yield return null;
            }
        }

        void SetSample(int sample, int segment, float step)
        {
            clone.ApplyPose(sample, segment);
            foreach (var extension in extensions) extension.SetSample(sample, step);
        }
        void CheckTrial(Func<string> readiness)
        {
            if (!clone.benchmarkCamera.isActiveAndEnabled) throw new InvalidOperationException("Benchmark camera is inactive.");
            if (xrMode)
            {
                if (!xrDisplay.running || !Application.isFocused) throw new InvalidOperationException("XR display stopped or the headset app lost focus during the trial.");
                GaussianBenchmarkXr.CheckCamera(clone.benchmarkCamera);
                if (!clone.benchmarkCamera.stereoEnabled) throw new InvalidOperationException("Benchmark camera is not rendering in stereo. Enable XR rendering on this camera and pipeline.");
                if (trial.xr != null && (XRSettings.eyeTextureWidth != trial.xr.eyeWidth || XRSettings.eyeTextureHeight != trial.xr.eyeHeight
                    || XRSettings.renderViewportScale != trial.xr.viewportScale || XRSettings.eyeTextureResolutionScale != trial.xr.eyeResolutionScale))
                    throw new InvalidOperationException("XR render resolution changed during the trial. Disable adaptive resolution for comparable measurements.");
            }
            string error = readiness();
            if (error != null) throw new InvalidOperationException(error);
            foreach (var extension in extensions)
                if ((error = extension.CheckError()) != null) throw new InvalidOperationException(error);
        }
        void Collect(bool record)
        {
            FrameTimingManager.CaptureFrameTimings();
            if (FrameTimingManager.GetLatestTimings(1, timings) == 0) return;
            var timing = timings[0];
            if (!record) { baseline = Math.Max(baseline, timing.frameStartTimestamp); return; }
            if (timing.frameStartTimestamp > baseline && seen.Add(timing.frameStartTimestamp)) trial.timings.Add(timing);
        }
        public void Cancel() { cancelled = true; StopAllCoroutines(); Finish("cancelled", "Cancelled by user."); Destroy(gameObject); }
        void OnDestroy() => Finish("cancelled", "Runner destroyed before completion.");
        void Finish(string status, string error)
        {
            if (finishCalled) return;
            finishCalled = true;
            try
            {
                if (staging) { staging.SetActive(false); Destroy(staging); }
                if (originals != null && activeStates != null)
                    for (int i = 0; i < originals.Length; i++) if (originals[i]) originals[i].gameObject.SetActive(activeStates[i]);
                if (sourceExtensions != null)
                    foreach (var extension in sourceExtensions)
                        if (extension) { try { extension.RestoreSourceState(); } catch (Exception e) { Debug.LogException(e); } }
                if (cameras != null && cameraStates != null)
                    for (int i = 0; i < cameras.Length; i++) if (cameras[i]) cameras[i].enabled = cameraStates[i];
                if (configured)
                {
                    QualitySettings.vSyncCount = oldVsync; Application.targetFrameRate = oldRate; Application.runInBackground = oldBackground;
                    if (resolutionChanged) Screen.SetResolution(oldWidth, oldHeight, oldScreenMode);
                }
                LastStatus = status;
                if (results != null)
                {
                    if (trial != null) { trial.status = status; trial.error = error; results.WriteTrial(trial); trial = null; }
                    results.Info.status = status; results.Info.error = error; results.Save();
                    Debug.Log("Gaussian benchmark " + status + ": " + results.DirectoryPath);
                }
            }
            finally { if (Active == this) Active = null; }
        }
    }
}
