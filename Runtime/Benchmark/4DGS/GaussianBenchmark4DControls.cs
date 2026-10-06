using System;
using UnityEngine;
using Gaussians.FourD;
using Gaussians.ThreeD;

namespace Gaussians.Benchmark
{
    [AddComponentMenu("Gaussians/Benchmark/4D Controls"), DisallowMultipleComponent]
    public sealed class GaussianBenchmark4DControls : GaussianBenchmarkExtension
    {
        public enum Mode { Inherit, Canonical, Onnx, Native }
        public enum Animation { Hold, SampledTimes, FixedStep }
        [Serializable] public sealed class Variant
        {
            public string variantId;
            public Mode mode;
            [Tooltip("Override the runtime SH buffer format for ONNX/Native. Canonical rendering uses the imported asset's format.")]
            public bool overrideSHStorage;
            public GaussianSplatSHStorage shStorage;
        }
        public Animation animationMode = Animation.SampledTimes;
        public float heldTime;
        public float[] sampledTimes = { 0, .125f, .25f, .375f, .5f, .625f, .75f, .875f, 1 };
        public Vector2 timeRange = new(0, 1);
        [Min(.001f)] public float durationSeconds = 5;
        public bool loop = true;
        public Variant[] variants = Array.Empty<Variant>();
        GaussianSplat4D[] players = Array.Empty<GaussianSplat4D>();
        Mode mode;
        int initialEvaluations;
        GaussianSplat4D[] sourcePlayers;
        bool[] sourcePlaying;
        double[] sourcePositions;
        float[] sourceTimes;
        
        public override void CaptureSourceState()
        {
            sourcePlayers = GetComponentsInChildren<GaussianSplat4D>(true);
            sourcePlaying = Array.ConvertAll(sourcePlayers, p => p.IsPlaying);
            sourcePositions = Array.ConvertAll(sourcePlayers, p => p.PositionSeconds);
            sourceTimes = Array.ConvertAll(sourcePlayers, p => p.m_ModelTime);
        }
        
        public override void RestoreSourceState()
        {
            if (sourcePlayers == null) return;
            for (int i = 0; i < sourcePlayers.Length; i++)
            {
                var player = sourcePlayers[i];
                if (!player) continue;
                if (sourcePlaying[i]) player.Play(); else player.Pause();
                player.SeekSeconds(sourcePositions[i]);
                player.m_ModelTime = sourceTimes[i];
            }
            sourcePlayers = null;
        }

        public float TimeAt(int sample, float step)
        {
            if (animationMode == Animation.Hold) return heldTime;
            if (animationMode == Animation.SampledTimes) return sampledTimes[sample % sampledTimes.Length];
            double progress = sample * (double)step / durationSeconds;
            progress = loop ? progress - Math.Floor(progress) : Math.Min(1, progress);
            return Mathf.LerpUnclamped(timeRange.x, timeRange.y, (float)progress);
        }

        public override string Validate(GaussianBenchmarkExperiment experiment)
        {
            if (!float.IsFinite(heldTime) || !float.IsFinite(timeRange.x) || !float.IsFinite(timeRange.y) || !float.IsFinite(durationSeconds) || durationSeconds <= 0)
                return "Times/duration must be finite and duration positive.";
            if (animationMode == Animation.SampledTimes)
            {
                if (sampledTimes == null || sampledTimes.Length == 0) return "Provide sampled model times.";
                foreach (float time in sampledTimes) if (!float.IsFinite(time)) return "Sampled times must be finite.";
            }
            var ids = new System.Collections.Generic.HashSet<string>();
            foreach (var entry in variants)
            {
                if (entry == null || !ids.Add(entry.variantId)) return "4D variant IDs must be unique.";
                var variant = Array.Find(experiment.variants, v => v.id == entry.variantId);
                if (variant == null) return "Unknown variant: " + entry.variantId;
                var components = variant.subject.GetComponentsInChildren<GaussianSplat4D>(true);
                if (components.Length == 0) return entry.variantId + " has no 4D component.";
                foreach (var p in components)
                {
                    if (!p.m_Asset || !p.m_Asset.Matches(p.Renderer.asset)) return "4D/canonical asset mismatch on " + p.name;
                    var backend = entry.mode == Mode.Native ? GaussianSplat4D.DeformationBackend.Native : entry.mode == Mode.Onnx ? GaussianSplat4D.DeformationBackend.Onnx : p.m_Backend;
                    if (entry.mode != Mode.Canonical && backend == GaussianSplat4D.DeformationBackend.Native && (!p.m_Asset.Data || !p.m_Asset.Data.HasNative)) return "Native data unavailable for " + entry.variantId;
                    if (entry.mode != Mode.Canonical && backend == GaussianSplat4D.DeformationBackend.Onnx && !p.m_Asset.Model) return "ONNX model unavailable for " + entry.variantId;
                }
            }
            return null;
        }

        public override void Configure(GameObject subject, string variantId)
        {
            players = subject.GetComponentsInChildren<GaussianSplat4D>(true);
            var entry = Array.Find(variants, v => v.variantId == variantId);
            mode = entry?.mode ?? Mode.Inherit;
            foreach (var p in players)
            {
                p.m_PlayOnEnable = false;
                p.Pause();
                if (mode != Mode.Inherit) p.enabled = mode != Mode.Canonical;
                if (mode == Mode.Native) p.m_Backend = GaussianSplat4D.DeformationBackend.Native;
                if (mode == Mode.Onnx) p.m_Backend = GaussianSplat4D.DeformationBackend.Onnx;
                if (entry != null && entry.overrideSHStorage) p.m_SHStorage = entry.shStorage;
            }
        }
        
        public override void Begin() { foreach (var p in players) p.Pause(); initialEvaluations = EvaluationCount(); }
        public override void SetSample(int sample, float stepSeconds) { foreach (var p in players) p.m_ModelTime = TimeAt(sample, stepSeconds); }
        public override string CheckError()
        {
            foreach (var p in players) if (p.isActiveAndEnabled && (p.CompatibilityError ?? p.Error) is string error) return error;
            return null;
        }
        int EvaluationCount() { int count = 0; foreach (var p in players) count += p.EvaluationCount; return count; }
        public override string Describe() => JsonUtility.ToJson(new Report { mode = mode.ToString(), animation = animationMode.ToString(), evaluations = EvaluationCount() - initialEvaluations, settings = JsonUtility.ToJson(this) });
        [Serializable] sealed class Report { public string mode, animation, settings; public int evaluations; }
    }
}
