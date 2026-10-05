using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using Gaussians.TwoD;
using Gaussians.ThreeD;

namespace Gaussians.Benchmark
{
    [Serializable] public struct BenchmarkInt { public bool apply; public int value; }
    [Serializable] public struct BenchmarkFloat { public bool apply; public float value; }
    [Serializable] public struct BenchmarkBool { public bool apply; public bool value; }
    [Serializable] public struct BenchmarkSortPrecision
    {
        public bool apply;
        public GaussianSplat3DRenderer.SortPrecision value;
    }

    [Serializable] public struct BenchmarkStereoViewMode
    {
        public bool apply;
        public GaussianSplat3DRenderer.StereoViewMode value;
    }

    [Serializable]
    public sealed class GaussianBenchmarkOverrides
    {
        public enum Path { Inherit, CompositeTexture, DirectTransparent }
        public Path renderPath;
        public BenchmarkStereoViewMode stereoViewMode;
        public BenchmarkInt shOrder = new() { value = 3 };
        public BenchmarkInt sortEveryNthFrame = new() { value = 1 };
        public BenchmarkFloat alphaCutoff = new() { value = 1f / 255 };
        public BenchmarkFloat splatScale = new() { value = 1 };
        public BenchmarkFloat opacityScale = new() { value = 1 };
        public BenchmarkBool writeDepth;
        public BenchmarkBool opacityAwareBounds;
        public BenchmarkBool earlyRejection;
        public BenchmarkFloat minimumSplatRadiusPixels;
        public BenchmarkFloat minimumSplatDistance;
        public BenchmarkFloat minimumSplatOpacity;
        public BenchmarkBool deferredSHLoading;
        public BenchmarkBool earlyFrustumCulling;
        [HideInInspector] public BenchmarkBool projectedFrustumCulling; // Retired; reject stale enabled configurations.
        public BenchmarkBool compactVisibleSplats;
        public BenchmarkInt compactionThreshold;
        [Tooltip("Sort depth-key precision. Select 16, 24, or 32 bits.")]
        public BenchmarkSortPrecision sortPrecision = new() { value = GaussianSplat3DRenderer.SortPrecision.Bits32 };
    }

    public static class GaussianBenchmarkRenderers
    {
        public static string Validate(GameObject subject, GaussianBenchmarkOverrides settings)
        {
            if (settings == null) return "Settings cannot be null.";
            if (settings.stereoViewMode.apply && !Enum.IsDefined(typeof(GaussianSplat3DRenderer.StereoViewMode), settings.stereoViewMode.value))
                return "Unknown stereo view mode.";
            if (settings.projectedFrustumCulling.apply && settings.projectedFrustumCulling.value) return "Projected frustum culling was removed. Update this legacy benchmark variant.";
            if (settings.compactionThreshold.apply && settings.compactionThreshold.value < -1) return "Compaction threshold must be -1 or greater.";
            if (settings.shOrder.apply && (settings.shOrder.value < 0 || settings.shOrder.value > 3)) return "SH order must be 0–3.";
            if (settings.sortEveryNthFrame.apply && settings.sortEveryNthFrame.value < 1) return "Sort interval must be positive.";
            if (settings.alphaCutoff.apply && (!float.IsFinite(settings.alphaCutoff.value) || settings.alphaCutoff.value < 0 || settings.alphaCutoff.value > 1)) return "Alpha cutoff must be finite and between 0 and 1.";
            if (settings.splatScale.apply && (!float.IsFinite(settings.splatScale.value) || settings.splatScale.value <= 0)) return "Splat scale must be finite and positive.";
            if (settings.opacityScale.apply && (!float.IsFinite(settings.opacityScale.value) || settings.opacityScale.value < 0)) return "Opacity scale must be finite and nonnegative.";
            if (settings.minimumSplatRadiusPixels.apply && (!float.IsFinite(settings.minimumSplatRadiusPixels.value) || settings.minimumSplatRadiusPixels.value < 0)) return "Minimum splat radius must be finite and nonnegative.";
            if (settings.minimumSplatDistance.apply && (!float.IsFinite(settings.minimumSplatDistance.value) || settings.minimumSplatDistance.value < 0)) return "Minimum splat distance must be finite and nonnegative.";
            if (settings.minimumSplatOpacity.apply && (!float.IsFinite(settings.minimumSplatOpacity.value) || settings.minimumSplatOpacity.value < 0 || settings.minimumSplatOpacity.value > 1)) return "Minimum splat opacity must be finite and between zero and one.";
            if (settings.sortPrecision.apply && !Enum.IsDefined(typeof(GaussianSplat3DRenderer.SortPrecision), settings.sortPrecision.value)) return "Sort precision must be 16, 24, or 32 bits.";
            if (settings.writeDepth.apply && settings.writeDepth.value &&
                !(GraphicsSettings.currentRenderPipeline && GraphicsSettings.currentRenderPipeline.GetType().Name.Contains("Universal")))
                return "Depth output requires URP.";
            foreach (var r in subject.GetComponentsInChildren<GaussianSplat2DRenderer>(true))
                if (r.enabled && !r.HasValidAsset) return r.name + " has no valid 2D asset.";
            foreach (var r in subject.GetComponentsInChildren<GaussianSplat3DRenderer>(true))
                if (r.enabled && !r.HasValidAsset) return r.name + " has no valid 3D asset.";
            return null;
        }

        public static void Apply(GameObject subject, GaussianBenchmarkOverrides s)
        {
            foreach (var r in subject.GetComponentsInChildren<GaussianSplat2DRenderer>(true))
            {
                if (s.renderPath != GaussianBenchmarkOverrides.Path.Inherit) r.m_RenderPath = (GaussianSplat2DRenderer.RenderPath)((int)s.renderPath - 1);
                if (s.shOrder.apply) r.m_SHOrder = s.shOrder.value;
                if (s.sortEveryNthFrame.apply) r.m_SortNthFrame = s.sortEveryNthFrame.value;
                if (s.alphaCutoff.apply) r.m_AlphaCutoff = s.alphaCutoff.value;
                if (s.splatScale.apply) r.m_SplatScale = s.splatScale.value;
                if (s.opacityScale.apply) r.m_OpacityScale = s.opacityScale.value;
                if (s.writeDepth.apply) r.m_WriteDepth = s.writeDepth.value;
            }
            foreach (var g in subject.GetComponentsInChildren<GaussiansGroup>(true))
            {
                g.m_OptimizationOverrides = !s.compactionThreshold.apply;
                g.m_CompactVisibleSplats = s.compactVisibleSplats.apply && s.compactVisibleSplats.value;
                if (s.compactionThreshold.apply) g.m_CompactionThreshold = s.compactionThreshold.value;
                if (s.renderPath != GaussianBenchmarkOverrides.Path.Inherit) g.m_RenderPath = (GaussianSplat3DRenderer.RenderPath)((int)s.renderPath - 1);
                if (s.sortEveryNthFrame.apply) g.m_SortNthFrame = s.sortEveryNthFrame.value;
                if (s.sortPrecision.apply) g.m_SortPrecision = s.sortPrecision.value;
                if (s.alphaCutoff.apply) g.m_AlphaCutoff = s.alphaCutoff.value;
                if (s.opacityAwareBounds.apply) g.m_OpacityAwareBounds = s.opacityAwareBounds.value;
                if (s.writeDepth.apply) g.m_WriteDepth = s.writeDepth.value;
            }
            foreach (var r in subject.GetComponentsInChildren<GaussianSplat3DRenderer>(true))
            {
                if (s.stereoViewMode.apply) r.m_StereoViewMode = s.stereoViewMode.value;
                r.m_OptimizationOverrides = !s.compactionThreshold.apply; // Explicit automatic-policy experiments opt in.
                if (s.compactionThreshold.apply) r.m_CompactionThreshold = s.compactionThreshold.value;
                r.m_CompactVisibleSplats = false; r.m_DeferredSHLoading = false;
                if (s.renderPath != GaussianBenchmarkOverrides.Path.Inherit) r.m_RenderPath = (GaussianSplat3DRenderer.RenderPath)((int)s.renderPath - 1);
                if (s.shOrder.apply) r.m_SHOrder = s.shOrder.value;
                if (s.sortEveryNthFrame.apply) r.m_SortNthFrame = s.sortEveryNthFrame.value;
                if (s.alphaCutoff.apply) r.m_AlphaCutoff = s.alphaCutoff.value;
                if (s.splatScale.apply) r.m_SplatScale = s.splatScale.value;
                if (s.opacityScale.apply) r.m_OpacityScale = s.opacityScale.value;
                if (s.writeDepth.apply) r.m_WriteDepth = s.writeDepth.value;
                if (s.opacityAwareBounds.apply) r.m_OpacityAwareBounds = s.opacityAwareBounds.value;
                if (s.earlyRejection.apply) r.m_EarlyRejection = s.earlyRejection.value;
                if (s.minimumSplatRadiusPixels.apply) r.m_MinimumSplatRadiusPixels = s.minimumSplatRadiusPixels.value;
                if (s.minimumSplatDistance.apply) r.m_MinimumSplatDistance = s.minimumSplatDistance.value;
                if (s.minimumSplatOpacity.apply) r.m_MinimumSplatOpacity = s.minimumSplatOpacity.value;
                if (s.earlyFrustumCulling.apply) r.m_EarlyFrustumCulling = s.earlyFrustumCulling.value;

                if (s.deferredSHLoading.apply) r.m_DeferredSHLoading = s.deferredSHLoading.value;
                if (s.compactVisibleSplats.apply) r.m_CompactVisibleSplats = s.compactVisibleSplats.value;
                if (s.sortPrecision.apply) r.m_SortPrecision = s.sortPrecision.value;
            }
        }

        public static string CheckReady(GameObject subject) => CreateReadinessCheck(subject)();

        // Cache component discovery before warm-up; the measurement loop must not allocate arrays each frame.
        public static Func<string> CreateReadinessCheck(GameObject subject, Camera camera = null)
        {
            var twoD = subject.GetComponentsInChildren<GaussianSplat2DRenderer>();
            var threeD = subject.GetComponentsInChildren<GaussianSplat3DRenderer>();
            var other = subject.GetComponentsInChildren<Renderer>();
            return () =>
            {
                if (!subject || !subject.activeInHierarchy) return "Selected subject hierarchy is inactive.";
                bool any = false;
                foreach (var r in twoD)
                    if (r && r.isActiveAndEnabled) { any = true; if (!r.HasValidRenderSetup) return r.name + ": 2D render resources unavailable."; }
                foreach (var r in threeD)
                    if (r && r.isActiveAndEnabled)
                    {
                        any = true;
                        if (!r.HasValidRenderSetup) return r.name + ": 3D render resources unavailable.";
                        if (camera && r.ActiveGroup && !r.ActiveGroup.TryGetPreparation(camera, r, out _))
                            return r.name + ": requested group did not prepare this member. " + r.ActiveGroup.Status;
                        if (camera && r.m_StereoViewMode != GaussianSplat3DRenderer.StereoViewMode.Automatic)
                        {
                            if (!r.TryGetViewPreparationStats(camera, out var stats)) return r.name + ": no stereo preparation recorded for the benchmark camera.";
                            if (stats.RequestedMode != r.m_StereoViewMode || stats.EffectiveMode != r.m_StereoViewMode ||
                                (r.m_StereoViewMode != GaussianSplat3DRenderer.StereoViewMode.PerEye && stats.ViewCount != 2) || stats.Fallbacks != 0)
                                return r.name + ": requested stereo kernel did not run for every preparation. " + stats.FallbackReason;
                        }
                    }
                foreach (var r in other) if (r && r.enabled && r.gameObject.activeInHierarchy) any = true;
                return any ? null : "Selected subject contains no active enabled renderers.";
            };
        }

        [Serializable] public sealed class RendererInfo
        {
            public string name, modality, asset, assetHash, path, settings, group;
            public Matrix4x4 localToWorld;
            public int splats;
        }

        // Keep benchmark settings independent of Unity object references. Besides
        // making trial JSON ingestible, this avoids JsonUtility traversing renderer
        // fields that reference shaders, buffers or assets.
        [Serializable] sealed class RendererSettingsSnapshot
        {
            public int shOrder, sortEveryNthFrame, sortPrecision, compactionThreshold;
            public string stereoViewMode;
            public bool optimizationOverrides;
            public float alphaCutoff, splatScale, opacityScale;
            public bool writeDepth, opacityAwareBounds, earlyRejection, earlyFrustumCulling, projectedFrustumCulling;
            public float minimumSplatRadiusPixels, minimumSplatDistance, minimumSplatOpacity;
            public bool deferredSHLoading, compactVisibleSplats;
        }

        static string DescribeSettings(GaussianSplat2DRenderer r) => JsonUtility.ToJson(new RendererSettingsSnapshot
        {
            shOrder = r.m_SHOrder, sortEveryNthFrame = r.m_SortNthFrame, sortPrecision = 32,
            alphaCutoff = r.m_AlphaCutoff, splatScale = r.m_SplatScale, opacityScale = r.m_OpacityScale,
            writeDepth = r.m_WriteDepth
        });

        static string DescribeSettings(GaussianSplat3DRenderer r) => JsonUtility.ToJson(new RendererSettingsSnapshot
        {
            shOrder = r.m_SHOrder, sortEveryNthFrame = r.EffectiveSortNthFrame,
            stereoViewMode = r.m_StereoViewMode.ToString(),
            compactionThreshold = r.EffectiveCompactionThreshold, optimizationOverrides = r.EffectiveOptimizationOverrides,
            sortPrecision = (int)r.EffectiveSortPrecision, alphaCutoff = r.EffectiveAlphaCutoff,
            splatScale = r.m_SplatScale, opacityScale = r.m_OpacityScale, writeDepth = r.EffectiveWriteDepth,
            opacityAwareBounds = r.EffectiveOpacityAwareBounds, earlyRejection = r.m_EarlyRejection,
            earlyFrustumCulling = r.m_EarlyFrustumCulling, projectedFrustumCulling = false,
            minimumSplatRadiusPixels = r.m_MinimumSplatRadiusPixels,
            minimumSplatDistance = r.m_MinimumSplatDistance,
            minimumSplatOpacity = r.m_MinimumSplatOpacity,
            deferredSHLoading = r.m_DeferredSHLoading, compactVisibleSplats = r.EffectiveCompactVisibleSplats
        });

        [Serializable] public sealed class StereoPreparationInfo
        {
            public string renderer, requestedMode, effectiveMode, kernel, fallbackReason;
            public int viewCount, groupSize, lastPreparedFrame;
            public long preparations, enqueuedDispatches, cacheHits, fallbacks;
        }

        public static StereoPreparationInfo[] DescribeStereoPreparation(GameObject subject, Camera camera)
        {
            var result = new List<StereoPreparationInfo>();
            foreach (var r in subject.GetComponentsInChildren<GaussianSplat3DRenderer>())
            {
                if (!r.enabled) continue;
                bool available = r.TryGetViewPreparationStats(camera, out var stats);
                result.Add(new StereoPreparationInfo
                {
                    renderer = r.name, requestedMode = r.m_StereoViewMode.ToString(),
                    effectiveMode = available ? stats.EffectiveMode.ToString() : "unavailable",
                    kernel = available ? stats.Kernel : "unavailable",
                    fallbackReason = available ? stats.FallbackReason : "No preparation recorded for this camera",
                    viewCount = stats.ViewCount, groupSize = stats.GroupSize, lastPreparedFrame = stats.LastFrame,
                    preparations = stats.Preparations, enqueuedDispatches = stats.Dispatches, cacheHits = stats.CacheHits, fallbacks = stats.Fallbacks
                });
            }
            return result.ToArray();
        }

        public static void ResetStereoPreparationCounters(GameObject subject, Camera camera)
        {
            foreach (var r in subject.GetComponentsInChildren<GaussianSplat3DRenderer>())
                r.ResetViewPreparationStats(camera);
        }

        public static RendererInfo[] Describe(GameObject subject)
        {
            var result = new List<RendererInfo>();
            foreach (var r in subject.GetComponentsInChildren<GaussianSplat2DRenderer>())
                if (r.enabled) result.Add(new RendererInfo { name = r.name, modality = "2D", asset = r.asset ? r.asset.name : "", assetHash = r.asset ? r.asset.dataHash.ToString() : "", localToWorld = r.transform.localToWorldMatrix, path = r.m_RenderPath.ToString(), splats = r.splatCount, settings = DescribeSettings(r) });
            foreach (var r in subject.GetComponentsInChildren<GaussianSplat3DRenderer>())
                if (r.enabled) result.Add(new RendererInfo { name = r.name, modality = "3D", group = r.ActiveGroup ? r.ActiveGroup.name : "", asset = r.asset ? r.asset.name : "", assetHash = r.asset ? r.asset.dataHash.ToString() : "", localToWorld = r.transform.localToWorldMatrix, path = r.EffectiveRenderPath.ToString(), splats = r.splatCount, settings = DescribeSettings(r) });
            return result.ToArray();
        }
    }
}
