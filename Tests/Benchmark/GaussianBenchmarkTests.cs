using NUnit.Framework;
using UnityEngine;
using Gaussians.ThreeD;

namespace Gaussians.Benchmark.Tests
{
    public sealed class GaussianBenchmarkTests
    {
        GameObject root;
        GaussianBenchmarkExperiment experiment;
        GaussianBenchmarkConfig config;
        [SetUp] public void Setup()
        {
            root = new GameObject("experiment");
            experiment = root.AddComponent<GaussianBenchmarkExperiment>();
            config = ScriptableObject.CreateInstance<GaussianBenchmarkConfig>();
            experiment.config = config;
            var camera = new GameObject("camera"); camera.transform.SetParent(root.transform);
            experiment.benchmarkCamera = camera.AddComponent<Camera>();
            var subject = new GameObject("subject"); subject.transform.SetParent(root.transform);
            experiment.variants = new[] { new GaussianBenchmarkVariant { id = "baseline", subject = subject } };
            experiment.cameraPoints = new[] { Point("front", new Vector3(0, 0, -3)), Point("side", new Vector3(4, 2, 0)) };
        }
        Transform Point(string name, Vector3 position)
        {
            var point = new GameObject(name).transform; point.SetParent(root.transform); point.position = position; return point;
        }
        [TearDown] public void Cleanup() { Object.DestroyImmediate(root); Object.DestroyImmediate(config); }

        [Test] public void RejectsInactiveCameraHierarchy()
        {
            experiment.benchmarkCamera.gameObject.SetActive(false);
            Assert.That(experiment.ValidationError(), Does.Contain("Camera").IgnoreCase);
        }
        [Test] public void CameraMetadataSerializesWithoutEngineObjects()
        {
            string json = JsonUtility.ToJson(GaussianBenchmarkCameraInfo.Capture(experiment.benchmarkCamera));
            StringAssert.Contains("fieldOfView", json);
            StringAssert.Contains("projection", json);
        }
        [Test] public void EmptySubjectCannotProduceSuccessfulMeasurement()
        {
            Assert.That(GaussianBenchmarkRenderers.CheckReady(experiment.variants[0].subject), Is.Not.Null);
        }
        [Test] public void PathIncludesEndpointsAndHoldsFinalPose()
        {
            experiment.sequence = GaussianBenchmarkExperiment.CameraSequence.Path;
            experiment.framesPerSegment = 10;
            Assert.That(experiment.EvaluatePose(0).position, Is.EqualTo(new Vector3(0, 0, -3)));
            Assert.That(experiment.EvaluatePose(5).position, Is.EqualTo(new Vector3(2, 1, -1.5f)));
            Assert.That(experiment.EvaluatePose(10).position, Is.EqualTo(new Vector3(4, 2, 0)));
            Assert.That(experiment.EvaluatePose(100).position, Is.EqualTo(experiment.EvaluatePose(10).position));
        }
        [Test] public void SubjectAlignmentNeverChangesCameraSequence()
        {
            var before = experiment.EvaluatePose(0);
            experiment.variants[0].subject.transform.SetPositionAndRotation(Vector3.one * 100, Quaternion.Euler(30, 60, 90));
            experiment.variants[0].subject.transform.localScale = Vector3.one * .01f;
            Assert.That(experiment.EvaluatePose(0).position, Is.EqualTo(before.position));
            Assert.That(experiment.EvaluatePose(0).rotation, Is.EqualTo(before.rotation));
        }
        [Test] public void ReplayProducesIdenticalInputs()
        {
            experiment.sequence = GaussianBenchmarkExperiment.CameraSequence.Path;
            var poses = new Pose[200];
            for (int i = 0; i < poses.Length; i++) poses[i] = experiment.EvaluatePose(i);
            for (int i = 0; i < 120; i++) experiment.ApplyPose(i, 0);
            for (int i = 0; i < poses.Length; i++)
            {
                Assert.That(experiment.EvaluatePose(i).position, Is.EqualTo(poses[i].position));
                Assert.That(experiment.EvaluatePose(i).rotation, Is.EqualTo(poses[i].rotation));
            }
        }
        [Test] public void FixedViewsDoNotAdvanceWithSampleIndex()
        {
            Assert.That(experiment.EvaluatePose(500, 1).position, Is.EqualTo(experiment.cameraPoints[1].position));
        }
        [Test] public void RejectsCameraPointsInsideVariantSubject()
        {
            experiment.cameraPoints[0].SetParent(experiment.variants[0].subject.transform);
            StringAssert.Contains("outside subject", experiment.ValidationError());
        }
        [Test] public void RejectsNestedSubjectsAndDuplicateVariantIds()
        {
            experiment.variants = new[] { experiment.variants[0], new GaussianBenchmarkVariant { id = "baseline", subject = experiment.variants[0].subject } };
            StringAssert.Contains("unique", experiment.ValidationError());
            experiment.variants[1].id = "second";
            var child = new GameObject("nested"); child.transform.SetParent(experiment.variants[0].subject.transform);
            experiment.variants[1].subject = child;
            StringAssert.Contains("nested", experiment.ValidationError());
        }
        [Test] public void AllowsSameSubjectWithDifferentSettings()
        {
            experiment.variants = new[] { experiment.variants[0], new GaussianBenchmarkVariant { id = "other", subject = experiment.variants[0].subject } };
            Assert.That(experiment.ValidationError(), Is.Null);
        }
        [Test] public void RejectsNonFiniteAndInvalidRunSettings()
        {
            config.simulationStep = float.NaN;
            Assert.That(config.ValidationError, Is.Not.Null);
            config.simulationStep = 1f / 60;
            config.measuredFrames = 1;
            Assert.That(config.ValidationError, Is.Not.Null);
        }
        [Test] public void PercentilesExcludeUnavailableTimings()
        {
            var values = new[] { double.NaN, 0, -1, double.PositiveInfinity, 1, 2, 3, 4, 5 };
            Assert.That(GaussianBenchmarkStatistics.Percentile(values, .5), Is.EqualTo(3));
            Assert.That(GaussianBenchmarkStatistics.Percentile(values, .95), Is.EqualTo(4.8).Within(.0001));
            Assert.That(double.IsNaN(GaussianBenchmarkStatistics.Percentile(new[] { 0d }, .5)), Is.True);
        }
        [TestCase(GaussianBenchmarkConfig.RunMode.DesktopThroughput, false, false)]
        [TestCase(GaussianBenchmarkConfig.RunMode.DesktopThroughput, true, true)]
        [TestCase(GaussianBenchmarkConfig.RunMode.XrFrameBudget, false, true)]
        [TestCase(GaussianBenchmarkConfig.RunMode.XrFrameBudget, true, false)]
        public void RunModeRequiresMatchingDisplay(GaussianBenchmarkConfig.RunMode mode, bool xrRunning, bool rejected)
        {
            Assert.That(GaussianBenchmarkXr.ModeError(mode, xrRunning) != null, Is.EqualTo(rejected));
        }
        [Test] public void XrUsesDeviceResolutionInsteadOfDesktopSize()
        {
            config.width = config.height = 0;
            Assert.That(config.ValidationError, Is.Not.Null);
            config.mode = GaussianBenchmarkConfig.RunMode.XrFrameBudget;
            Assert.That(config.ValidationError, Is.Null);
        }
        [Test] public void XrTimingConvertsSecondsAndKeepsMissingMetricsUnavailable()
        {
            Assert.That(GaussianBenchmarkXr.Milliseconds(true, .005f), Is.EqualTo(5d).Within(.00001));
            Assert.That(double.IsNaN(GaussianBenchmarkXr.Milliseconds(false, .005f)), Is.True);
            Assert.That(double.IsNaN(GaussianBenchmarkXr.Milliseconds(true, 0)), Is.True);
            Assert.That(double.IsNaN(GaussianBenchmarkXr.Milliseconds(true, float.NaN)), Is.True);
        }
        [Test] public void XrResultsSeparateProviderTimingsAndWriteUnavailableValues()
        {
            string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "GaussianBenchmarkTest-" + System.Guid.NewGuid().ToString("N"));
            try
            {
                var output = new GaussianBenchmarkResults(directory, "test");
                var trial = new GaussianBenchmarkTrial { experiment = "room", variant = "native", status = "complete", xr = new GaussianBenchmarkXrInfo() };
                trial.xrTimings.Add(new GaussianBenchmarkTrial.XrTiming { observation = 17, appGpu = 5, compositorGpu = double.NaN });
                output.WriteTrial(trial);
                string summary = System.IO.File.ReadAllText(System.IO.Path.Combine(output.DirectoryPath, "summary.csv"));
                StringAssert.Contains("xr_app_gpu,1,5,5,5", summary);
                StringAssert.Contains("xr_compositor_gpu,0,unavailable,unavailable,unavailable", summary);
                Assert.That(trial.gpuSamples, Is.Zero);
                Assert.That(trial.xrAppGpuSamples, Is.EqualTo(1));
                StringAssert.Contains("17,5,unavailable", System.IO.File.ReadAllText(System.IO.Path.Combine(output.DirectoryPath, "xr-timings.csv")));
            }
            finally { if (System.IO.Directory.Exists(directory)) System.IO.Directory.Delete(directory, true); }
        }
        [Test] public void OverridesRespectInheritanceAndExplicitZero()
        {
            var subject = experiment.variants[0].subject;
            subject.SetActive(false);
            var renderer = subject.AddComponent<GaussianSplat3DRenderer>();
            renderer.m_SHOrder = 2; renderer.m_AlphaCutoff = .2f;
            var settings = new GaussianBenchmarkOverrides();
            GaussianBenchmarkRenderers.Apply(subject, settings);
            Assert.That(renderer.m_SHOrder, Is.EqualTo(2));
            Assert.That(renderer.m_AlphaCutoff, Is.EqualTo(.2f));
            settings.shOrder = new BenchmarkInt { apply = true, value = 0 };
            settings.alphaCutoff = new BenchmarkFloat { apply = true, value = 0 };
            settings.renderPath = GaussianBenchmarkOverrides.Path.DirectTransparent;
            GaussianBenchmarkRenderers.Apply(subject, settings);
            Assert.That(renderer.m_SHOrder, Is.Zero);
            Assert.That(renderer.m_AlphaCutoff, Is.Zero);
            Assert.That(renderer.m_RenderPath, Is.EqualTo(GaussianSplat3DRenderer.RenderPath.DirectTransparent));
        }
        [Test] public void SharedSettingsReplaceInlineSettingsWithoutMerging()
        {
            var asset = ScriptableObject.CreateInstance<GaussianBenchmarkRendererSettings>();
            try
            {
                var first = experiment.variants[0];
                first.settings.shOrder = new BenchmarkInt { apply = true, value = 1 };
                first.sharedSettings = asset;
                var second = new GaussianBenchmarkVariant { sharedSettings = asset };
                Assert.That(first.EffectiveSettings, Is.SameAs(second.EffectiveSettings));
                Assert.That(first.EffectiveSettings.shOrder.apply, Is.False, "Inline overrides must not leak into shared settings.");
                asset.settings.shOrder = new BenchmarkInt { apply = true, value = 2 };
                Assert.That(second.EffectiveSettings.shOrder.value, Is.EqualTo(2));
                asset.settings.shOrder.value = 9;
                StringAssert.Contains("SH order", experiment.ValidationError());
                first.sharedSettings = null;
                Assert.That(first.EffectiveSettings.shOrder.value, Is.EqualTo(1));
            }
            finally { Object.DestroyImmediate(asset); }
        }
    }
}
