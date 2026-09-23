using NUnit.Framework;
using UnityEngine;
using Gaussians.FourD;

namespace Gaussians.Benchmark.Tests
{
    public sealed class GaussianBenchmark4DTests
    {
        [TestCase(true)] [TestCase(false)]
        public void RestoresPlayingAndPausedSourceState(bool playing)
        {
            var root = new GameObject("experiment");
            try
            {
                var controls = root.AddComponent<GaussianBenchmark4DControls>();
                var subject = new GameObject("subject"); subject.transform.SetParent(root.transform);
                var player = subject.AddComponent<GaussianSplat4D>();
                player.m_PlayOnEnable = !playing;
                player.SeekSeconds(1.25);
                if (playing) player.Play(); else player.Pause();
                float time = player.ModelTime;
                controls.CaptureSourceState();
                root.SetActive(false); root.SetActive(true);
                // Simulate OnEnable's runtime playback policy in this EditMode regression test.
                if (player.m_PlayOnEnable) player.Play(); else player.Pause();
                controls.RestoreSourceState();
                Assert.That(player.IsPlaying, Is.EqualTo(playing));
                Assert.That(player.PositionSeconds, Is.EqualTo(1.25));
                Assert.That(player.ModelTime, Is.EqualTo(time));
            }
            finally { Object.DestroyImmediate(root); }
        }
        [Test] public void AnimationUsesSampleIndexAndSupportsDescendingRange()
        {
            var root = new GameObject("experiment");
            try
            {
                var controls = root.AddComponent<GaussianBenchmark4DControls>();
                controls.animation = GaussianBenchmark4DControls.Animation.FixedStep;
                controls.durationSeconds = 2; controls.timeRange = new Vector2(1, -1); controls.loop = false;
                Assert.That(controls.TimeAt(0, .1f), Is.EqualTo(1));
                Assert.That(controls.TimeAt(10, .1f), Is.EqualTo(0).Within(.00001));
                Assert.That(controls.TimeAt(100, .1f), Is.EqualTo(-1));
                controls.animation = GaussianBenchmark4DControls.Animation.SampledTimes;
                controls.sampledTimes = new[] { .2f, .8f };
                Assert.That(controls.TimeAt(2, 999), Is.EqualTo(.2f));
            }
            finally { Object.DestroyImmediate(root); }
        }
    }
}
