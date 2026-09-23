using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Gaussians.Benchmark.Editor
{
    [InitializeOnLoad]
    public static class GaussianBenchmarkMenus
    {
        const string Pending = "Gaussians.Benchmark.Pending";
        static GaussianBenchmarkMenus() { EditorApplication.playModeStateChanged += OnPlayMode; }
        static void OnPlayMode(PlayModeStateChange state)
        {
            if (state != PlayModeStateChange.EnteredPlayMode) return;
            string id = SessionState.GetString(Pending, "");
            SessionState.EraseString(Pending);
            if (id.Length == 0) return;
            EditorApplication.delayCall += () =>
            {
                try
                {
                    var experiment = UnityEngine.Object.FindObjectsByType<GaussianBenchmarkExperiment>(FindObjectsSortMode.None).Single(e => e.experimentId == id);
                    GaussianBenchmarkRunner.StartRun(experiment);
                }
                catch (Exception e) { Debug.LogException(e); }
            };
        }
        public static void Run(GaussianBenchmarkExperiment experiment)
        {
            if (EditorApplication.isPlaying) { GaussianBenchmarkRunner.StartRun(experiment); return; }
            SessionState.SetString(Pending, experiment.experimentId);
            EditorApplication.isPlaying = true;
        }

        [MenuItem("Tools/Gaussians/Benchmark/Create Experiment")]
        public static void CreateExperiment()
        {
            var root = new GameObject("Gaussian Experiment");
            Undo.RegisterCreatedObjectUndo(root, "Create Gaussian experiment");
            var experiment = root.AddComponent<GaussianBenchmarkExperiment>();
            experiment.experimentId = "experiment-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            if (!AssetDatabase.IsValidFolder("Assets/Benchmarks")) AssetDatabase.CreateFolder("Assets", "Benchmarks");
            var config = ScriptableObject.CreateInstance<GaussianBenchmarkConfig>();
            AssetDatabase.CreateAsset(config, AssetDatabase.GenerateUniqueAssetPath("Assets/Benchmarks/BenchmarkConfig.asset"));
            experiment.config = config;
            var subject = new GameObject("Subject (put models here)"); subject.transform.SetParent(root.transform, false);
            var camera = new GameObject("Benchmark Camera").AddComponent<Camera>(); camera.transform.SetParent(root.transform, false);
            camera.transform.localPosition = new Vector3(0, 0, -3); camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = Color.black;
            experiment.benchmarkCamera = camera;
            var point = new GameObject("Front").transform; point.SetParent(root.transform, false); point.SetPositionAndRotation(camera.transform.position, camera.transform.rotation);
            experiment.cameraPoints = new[] { point };
            experiment.variants = new[] { new GaussianBenchmarkVariant { id = "baseline", subject = subject } };
            Selection.activeGameObject = root;
        }

        [MenuItem("Tools/Gaussians/Benchmark/Validate Scene")]
        public static void ValidateScene()
        {
            ValidateCurrentScene();
            Debug.Log("Gaussian benchmark scene configuration is valid. Use Show Pose in Game View to verify visible output before measuring.");
        }
        public static GaussianBenchmarkExperiment[] ValidateCurrentScene()
        {
            var scene = SceneManager.GetActiveScene();
            var experiments = scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<GaussianBenchmarkExperiment>()).Where(e => e.enabled).ToArray();
            if (experiments.Length == 0) throw new InvalidOperationException("The active scene has no enabled Gaussian benchmark experiments.");
            if (experiments.Select(e => e.experimentId).Distinct().Count() != experiments.Length) throw new InvalidOperationException("Experiment IDs must be unique.");
            foreach (var e in experiments)
                if (e.ValidationError() is string error) throw new InvalidOperationException(e.name + ": " + error);
            if (experiments.Select(e => e.config.developmentBuild).Distinct().Count() > 1) throw new InvalidOperationException("All experiments in a build must agree on Development Build.");
            if (experiments.Select(e => e.config.mode).Distinct().Count() > 1) throw new InvalidOperationException("All experiments in a build must use the same benchmark mode.");
            return experiments;
        }

        [MenuItem("Tools/Gaussians/Benchmark/Build Current Scene")]
        public static void Build() => ChooseBuild(false);
        [MenuItem("Tools/Gaussians/Benchmark/Build and Run Current Scene")]
        public static void BuildAndRun() => ChooseBuild(true);
        static void ChooseBuild(bool run)
        {
            string extension = EditorUserBuildSettings.activeBuildTarget == BuildTarget.StandaloneOSX ? "app" : EditorUserBuildSettings.activeBuildTarget == BuildTarget.StandaloneWindows64 ? "exe"
                : EditorUserBuildSettings.activeBuildTarget == BuildTarget.Android ? "apk" : "";
            string path = EditorUtility.SaveFilePanel("Gaussian benchmark player", "Builds", "GaussianBenchmark", extension);
            if (!string.IsNullOrEmpty(path)) BuildCurrentScene(path, run);
        }
        [Serializable] sealed class BuildInfo { public string scene, dependencyHash, unity; public string[] dependencies; public string[] experiments; }
        public static void BuildCurrentScene(string path, bool launch = false)
        {
            if (EditorApplication.isPlaying) throw new InvalidOperationException("Exit Play mode before building.");
            var experiments = ValidateCurrentScene();
            var scene = SceneManager.GetActiveScene();
            if (string.IsNullOrEmpty(scene.path) || scene.isDirty) throw new InvalidOperationException("Save the benchmark scene before building.");
            var target = EditorUserBuildSettings.activeBuildTarget;
            bool android = target == BuildTarget.Android;
            if (target != BuildTarget.StandaloneOSX && target != BuildTarget.StandaloneWindows64 && target != BuildTarget.StandaloneLinux64 && !android)
                throw new InvalidOperationException("Select a desktop or Android build target.");
            if (android && experiments[0].config.mode != GaussianBenchmarkConfig.RunMode.XrFrameBudget)
                throw new InvalidOperationException("Android headset builds require XR Frame Budget mode in the benchmark config.");
            if (android && (EditorUserBuildSettings.buildAppBundle || EditorUserBuildSettings.exportAsGoogleAndroidProject))
                throw new InvalidOperationException("Disable Build App Bundle and Export Project to build a directly installable benchmark APK.");
            if (!BuildPipeline.IsBuildTargetSupported(BuildPipeline.GetBuildTargetGroup(target), target))
                throw new InvalidOperationException("Install the Unity build-support module for " + target + " first.");
            const string temporary = "Assets/__GaussianBenchmarkBuild";
            if (AssetDatabase.IsValidFolder(temporary) || Directory.Exists(temporary)) throw new InvalidOperationException(temporary + " already exists; refusing to overwrite it.");
            bool timing = PlayerSettings.enableFrameTimingStats;
            try
            {
                AssetDatabase.CreateFolder("Assets", "__GaussianBenchmarkBuild");
                AssetDatabase.CreateFolder(temporary, "Resources");
                File.WriteAllText(temporary + "/Resources/GaussianBenchmarkBuildInfo.json", JsonUtility.ToJson(new BuildInfo
                {
                    scene = scene.path, dependencyHash = AssetDatabase.GetAssetDependencyHash(scene.path).ToString(), unity = Application.unityVersion,
                    dependencies = AssetDatabase.GetDependencies(scene.path), experiments = experiments.Select(e => e.experimentId).ToArray()
                }, true));
                AssetDatabase.Refresh();
                PlayerSettings.enableFrameTimingStats = true;
                var options = experiments[0].config.developmentBuild ? BuildOptions.Development : BuildOptions.None;
                if (launch && android) options |= BuildOptions.AutoRunPlayer;
                var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions { scenes = new[] { scene.path }, locationPathName = path,
                    target = target, options = options });
                if (report.summary.result != BuildResult.Succeeded) throw new InvalidOperationException("Benchmark build failed: " + report.summary.result);
                Debug.Log("Gaussian benchmark player: " + Path.GetFullPath(path));
            }
            finally { PlayerSettings.enableFrameTimingStats = timing; AssetDatabase.DeleteAsset(temporary); }
            if (launch && !android)
            {
                string executable = target == BuildTarget.StandaloneOSX ? Directory.GetFiles(Path.Combine(path, "Contents/MacOS")).Single() : path;
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Path.GetFullPath(executable)) { UseShellExecute = false });
            }
        }
    }
}
