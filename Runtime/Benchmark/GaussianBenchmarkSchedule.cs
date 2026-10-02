using System;
using System.Collections.Generic;
using System.Linq;

namespace Gaussians.Benchmark
{
    public static class GaussianBenchmarkSchedule
    {
        public sealed class Entry
        {
            public GaussianBenchmarkExperiment source;
            public int repetition, variantIndex, segment;
            public bool diagnostics;
        }

        public static List<Entry> Build(GaussianBenchmarkExperiment[] sources)
        {
            var result = new List<Entry>();
            var ordered = sources.OrderBy(e => e.experimentId, StringComparer.Ordinal).ToArray();
            if (ordered.Length == 0) return result;
            // One run seed controls both scene and variant order; configs must agree.
            var config = ordered[0].config;
            if (ordered.Any(e => e.config.randomizeOrder != config.randomizeOrder || e.config.randomSeed != config.randomSeed || e.config.firstRepetition != config.firstRepetition || e.config.pairRenderPaths != config.pairRenderPaths))
                throw new InvalidOperationException("Experiments must share scheduling seed and randomization settings.");
            var random = new Random(config.randomSeed);
            int repetitions = ordered.Max(e => e.config.repetitions);
            for (int repetition = 0; repetition < repetitions; repetition++)
            {
                // Independent per-repeat seed makes separately cooled sessions match the full schedule.
                if (config.pairRenderPaths) random = new Random(unchecked(config.randomSeed + (config.firstRepetition + repetition) * 104729));
                var scenes = ordered.ToArray();
                if (config.randomizeOrder) Shuffle(scenes, random);
                foreach (var source in scenes)
                {
                    if (repetition >= source.config.repetitions || source.config.captureMode == GaussianBenchmarkConfig.CaptureMode.DiagnosticsOnly) continue;
                    var variants = Enumerable.Range(0, source.variants.Length).Where(i => !source.variants[i].diagnosticsOnly).ToArray();
                    if (config.pairRenderPaths)
                        variants = PairedVariants(source, variants, config.firstRepetition + repetition, random);
                    else if (config.randomizeOrder) Shuffle(variants, random);
                    foreach (int variant in variants)
                    for (int segment = 0; segment < source.SegmentCount; segment++)
                        result.Add(new Entry { source = source, repetition = config.firstRepetition + repetition, variantIndex = variant, segment = segment });
                }
            }
            // All diagnostics follow all timing: image encoding/readback cannot heat or stall a later timed trial.
            foreach (var source in ordered)
            {
                if (source.config.captureMode == GaussianBenchmarkConfig.CaptureMode.TimingOnly) continue;
                for (int variant = 0; variant < source.variants.Length; variant++)
                for (int segment = 0; segment < source.SegmentCount; segment++)
                    result.Add(new Entry { source = source, repetition = 1, variantIndex = variant, segment = segment, diagnostics = true });
            }
            return result;
        }

        static int[] PairedVariants(GaussianBenchmarkExperiment source, int[] variants, int repetition, Random random)
        {
            var groups = variants.GroupBy(i => source.variants[i].comparisonGroup)
                .OrderBy(g => g.Key, StringComparer.Ordinal).ToArray();
            var pairs = new List<int[]>();
            int groupIndex = 0;
            foreach (var group in groups)
            {
                var pair = group.OrderBy(i => source.variants[i].EffectiveSettings.renderPath).ToArray();
                if (string.IsNullOrWhiteSpace(group.Key) || pair.Length != 2 ||
                    source.variants[pair[0]].EffectiveSettings.renderPath != GaussianBenchmarkOverrides.Path.CompositeTexture ||
                    source.variants[pair[1]].EffectiveSettings.renderPath != GaussianBenchmarkOverrides.Path.DirectTransparent)
                    throw new InvalidOperationException("Paired scheduling requires exactly one Composite and one Direct variant per nonempty comparison group.");
                if (((repetition + groupIndex++) & 1) == 0) Array.Reverse(pair);
                pairs.Add(pair);
            }
            var orderedPairs = pairs.ToArray();
            if (source.config.randomizeOrder) Shuffle(orderedPairs, random);
            return orderedPairs.SelectMany(pair => pair).ToArray();
        }

        static void Shuffle<T>(T[] values, Random random)
        {
            for (int i = values.Length - 1; i > 0; i--)
            {
                int j = random.Next(i + 1);
                (values[i], values[j]) = (values[j], values[i]);
            }
        }

        public readonly struct ReplayFrame
        {
            public readonly int sample;
            public readonly bool capture;
            public ReplayFrame(int sample, bool capture) { this.sample = sample; this.capture = capture; }
        }

        public static IEnumerable<ReplayFrame> DiagnosticReplay(int measuredFrames, int positions, int sequenceFrames)
        {
            var captures = new HashSet<int>(DiagnosticSamples(measuredFrames, positions, sequenceFrames));
            // Stable low-precision sorting retains tie order from previous frames.
            // Every pose must render even when no image is saved at that position.
            for (int sample = 0; sample < measuredFrames; sample++)
                yield return new ReplayFrame(sample, captures.Contains(sample));
        }

        public static int[] DiagnosticSamples(int measuredFrames, int positions, int sequenceFrames)
        {
            if (measuredFrames < 2 || positions < 2 || positions > measuredFrames || sequenceFrames < 0 || sequenceFrames > measuredFrames)
                throw new ArgumentOutOfRangeException();
            var samples = new SortedSet<int>();
            for (int i = 0; i < positions; i++) samples.Add((int)Math.Round(i * (measuredFrames - 1.0) / (positions - 1)));
            int start = (measuredFrames - sequenceFrames) / 2;
            for (int i = 0; i < sequenceFrames; i++) samples.Add(start + i);
            return samples.ToArray();
        }
    }
}
