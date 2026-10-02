using System.Linq;
using Gaussians.Benchmark;
using Gaussians.ThreeD;
using NUnit.Framework;
using UnityEngine;

namespace Gaussians.Package.Tests
{
    public sealed class GaussianBenchmarkBaselineTests
    {
        GameObject root;
        GaussianBenchmarkConfig config;
        GaussianBenchmarkExperiment experiment;
        [SetUp] public void SetUp()
        {
            root = new GameObject("Benchmark baseline") { hideFlags = HideFlags.HideAndDontSave };
            config = ScriptableObject.CreateInstance<GaussianBenchmarkConfig>();
            experiment = root.AddComponent<GaussianBenchmarkExperiment>(); experiment.config = config;
            experiment.benchmarkCamera = Child("camera").gameObject.AddComponent<Camera>();
            experiment.variants = new[] { new GaussianBenchmarkVariant { id = "baseline", subject = Child("subject").gameObject } };
            experiment.cameraPoints = new[] { Child("start"), Child("end") };
            experiment.cameraPoints[1].position = new Vector3(4, 2, 0);
        }
        Transform Child(string name)
        {
            var child = new GameObject(name) { hideFlags = HideFlags.HideAndDontSave }.transform;
            child.SetParent(root.transform); return child;
        }
        [TearDown] public void TearDown() { Object.DestroyImmediate(root); Object.DestroyImmediate(config); }

        [Test] public void CameraPathUsesPhaseFrameBudgetAndAppliesThePose()
        {
            experiment.sequence = GaussianBenchmarkExperiment.CameraSequence.Path;
            Assert.That(experiment.EvaluatePose(0, 0, 7).position, Is.EqualTo(Vector3.zero));
            Assert.That(experiment.EvaluatePose(3, 0, 7).position, Is.EqualTo(new Vector3(2, 1, 0)));
            Assert.That(experiment.EvaluatePose(6, 0, 7).position, Is.EqualTo(new Vector3(4, 2, 0)));
            Assert.That(experiment.EvaluatePose(100, 0, 7).position, Is.EqualTo(new Vector3(4, 2, 0)));
            experiment.ApplyPose(3, 0, 7);
            Assert.That(experiment.benchmarkCamera.transform.position, Is.EqualTo(new Vector3(2, 1, 0)));
        }

        [Test] public void PercentilesExcludeUnavailableTimingSamples()
        {
            var values = new[] { double.NaN, 0, -1, double.PositiveInfinity, 1, 2, 3, 4, 5 };
            Assert.That(GaussianBenchmarkStatistics.Percentile(values, .5), Is.EqualTo(3));
            Assert.That(GaussianBenchmarkStatistics.Percentile(values, .95), Is.EqualTo(4.8).Within(1e-6));
            Assert.That(double.IsNaN(GaussianBenchmarkStatistics.Percentile(new[] { 0d }, .5)), Is.True);
        }

        [Test] public void DiagnosticVariantsRequireDiagnosticCapture()
        {
            experiment.variants[0].diagnosticsOnly = true;
            config.captureMode = GaussianBenchmarkConfig.CaptureMode.TimingOnly;
            Assert.That(experiment.ValidationError(), Is.Not.Null);
            config.captureMode = GaussianBenchmarkConfig.CaptureMode.DiagnosticsOnly;
            Assert.That(experiment.ValidationError(), Is.Null);
            config.captureMode = GaussianBenchmarkConfig.CaptureMode.TimingAndDiagnostics;
            Assert.That(experiment.ValidationError(), Is.Null);
        }

        [Test] public void OverridesApplyExplicitZeroAndPreserveUnspecifiedSettings()
        {
            var subject = experiment.variants[0].subject; subject.SetActive(false);
            var renderer = subject.AddComponent<GaussianSplat3DRenderer>();
            renderer.m_SHOrder = 2; renderer.m_AlphaCutoff = .2f;
            var settings = new GaussianBenchmarkOverrides();
            GaussianBenchmarkRenderers.Apply(subject, settings);
            Assert.That(renderer.m_SHOrder, Is.EqualTo(2));
            settings.alphaCutoff = new BenchmarkFloat { apply = true, value = 0 };
            GaussianBenchmarkRenderers.Apply(subject, settings);
            Assert.That(renderer.m_AlphaCutoff, Is.Zero);
            Assert.That(renderer.m_SHOrder, Is.EqualTo(2));
            settings.shOrder = new BenchmarkInt { apply = true, value = 0 };
            settings.renderPath = GaussianBenchmarkOverrides.Path.DirectTransparent;
            GaussianBenchmarkRenderers.Apply(subject, settings);
            Assert.That(renderer.m_SHOrder, Is.Zero);
            Assert.That(renderer.m_RenderPath, Is.EqualTo(GaussianSplat3DRenderer.RenderPath.DirectTransparent));
        }

        [Test] public void PairedScheduleMatchesSeparateRepetitionsAndAlternatesPaths()
        {
            config.randomizeOrder = config.pairRenderPaths = true; config.repetitions = 2;
            config.captureMode = GaussianBenchmarkConfig.CaptureMode.TimingOnly;
            config.firstRepetition = 1;
            experiment.sequence = GaussianBenchmarkExperiment.CameraSequence.Path;
            experiment.variants = new[]
            {
                new GaussianBenchmarkVariant { id = "composite", comparisonGroup = "pair",
                    settings = new GaussianBenchmarkOverrides { renderPath = GaussianBenchmarkOverrides.Path.CompositeTexture } },
                new GaussianBenchmarkVariant { id = "direct", comparisonGroup = "pair",
                    settings = new GaussianBenchmarkOverrides { renderPath = GaussianBenchmarkOverrides.Path.DirectTransparent } }
            };
            var full = GaussianBenchmarkSchedule.Build(new[] { experiment });
            Assert.That(full.Count, Is.EqualTo(4));
            for (int i = 0; i < full.Count; i += 2)
            {
                Assert.That(full[i].segment, Is.EqualTo(full[i + 1].segment));
                Assert.That(full[i].repetition, Is.EqualTo(full[i + 1].repetition));
                Assert.That(full[i].variantIndex, Is.Not.EqualTo(full[i + 1].variantIndex));
            }
            config.firstRepetition = 2; config.repetitions = 1;
            string Key(GaussianBenchmarkSchedule.Entry entry) => $"{entry.segment}/{entry.variantIndex}/{entry.repetition}";
            CollectionAssert.AreEqual(full.Where(entry => entry.repetition == 2).Select(Key),
                GaussianBenchmarkSchedule.Build(new[] { experiment }).Select(Key));
            Assert.That(full.First(entry => entry.repetition == 1).variantIndex,
                Is.Not.EqualTo(full.First(entry => entry.repetition == 2).variantIndex));
        }
    }
}
