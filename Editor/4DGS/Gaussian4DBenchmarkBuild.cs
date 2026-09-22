using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Gaussians.FourD.Editor
{
    public static class Gaussian4DBenchmarkBuild
    {
        [MenuItem("Tools/Gaussians/Benchmark/Build macOS 4DGS Benchmark")]
        public static void BuildMacOS() => BuildMacOSAtPath("Builds/4DGSBenchmark.app");

        public static void BuildMacOSAtPath(string outputPath)
        {
            var scenes = new[] { "Assets/Scenes/4DGS Demo.unity", "Assets/Scenes/4DGS Room Demo.unity" };
            foreach (string scene in scenes)
                if (!File.Exists(scene)) throw new FileNotFoundException("Benchmark demo scene is missing", scene);
            bool timing = PlayerSettings.enableFrameTimingStats;
            try
            {
                PlayerSettings.enableFrameTimingStats = true;
                var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
                {
                    scenes = scenes, locationPathName = outputPath, target = BuildTarget.StandaloneOSX,
                    options = BuildOptions.Development
                });
                if (report.summary.result != BuildResult.Succeeded)
                    throw new InvalidOperationException("4DGS benchmark build failed: " + report.summary.result);
                Debug.Log("4DGS benchmark development player: " + Path.GetFullPath(outputPath));
            }
            finally { PlayerSettings.enableFrameTimingStats = timing; }
        }
    }
}
