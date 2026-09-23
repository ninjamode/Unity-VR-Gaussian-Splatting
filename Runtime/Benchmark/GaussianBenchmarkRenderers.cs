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

    [Serializable]
    public sealed class GaussianBenchmarkOverrides
    {
        public enum Path { Inherit, CompositeTexture, DirectTransparent }
        public Path renderPath;
        public BenchmarkInt shOrder = new() { value = 3 };
        public BenchmarkInt sortEveryNthFrame = new() { value = 1 };
        public BenchmarkFloat alphaCutoff = new() { value = 1f / 255 };
        public BenchmarkFloat splatScale = new() { value = 1 };
        public BenchmarkFloat opacityScale = new() { value = 1 };
        public BenchmarkBool writeDepth;
    }

    public static class GaussianBenchmarkRenderers
    {
        public static string Validate(GameObject subject, GaussianBenchmarkOverrides settings)
        {
            if (settings == null) return "Settings cannot be null.";
            if (settings.shOrder.apply && (settings.shOrder.value < 0 || settings.shOrder.value > 3)) return "SH order must be 0–3.";
            if (settings.sortEveryNthFrame.apply && settings.sortEveryNthFrame.value < 1) return "Sort interval must be positive.";
            if (settings.alphaCutoff.apply && (!float.IsFinite(settings.alphaCutoff.value) || settings.alphaCutoff.value < 0 || settings.alphaCutoff.value > 1)) return "Alpha cutoff must be finite and between 0 and 1.";
            if (settings.splatScale.apply && (!float.IsFinite(settings.splatScale.value) || settings.splatScale.value <= 0)) return "Splat scale must be finite and positive.";
            if (settings.opacityScale.apply && (!float.IsFinite(settings.opacityScale.value) || settings.opacityScale.value < 0)) return "Opacity scale must be finite and nonnegative.";
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
            foreach (var r in subject.GetComponentsInChildren<GaussianSplat3DRenderer>(true))
            {
                if (s.renderPath != GaussianBenchmarkOverrides.Path.Inherit) r.m_RenderPath = (GaussianSplat3DRenderer.RenderPath)((int)s.renderPath - 1);
                if (s.shOrder.apply) r.m_SHOrder = s.shOrder.value;
                if (s.sortEveryNthFrame.apply) r.m_SortNthFrame = s.sortEveryNthFrame.value;
                if (s.alphaCutoff.apply) r.m_AlphaCutoff = s.alphaCutoff.value;
                if (s.splatScale.apply) r.m_SplatScale = s.splatScale.value;
                if (s.opacityScale.apply) r.m_OpacityScale = s.opacityScale.value;
                if (s.writeDepth.apply) r.m_WriteDepth = s.writeDepth.value;
            }
        }

        public static string CheckReady(GameObject subject) => CreateReadinessCheck(subject)();

        // Cache component discovery before warm-up; the measurement loop must not allocate arrays each frame.
        public static Func<string> CreateReadinessCheck(GameObject subject)
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
                    if (r && r.isActiveAndEnabled) { any = true; if (!r.HasValidRenderSetup) return r.name + ": 3D render resources unavailable."; }
                foreach (var r in other) if (r && r.enabled && r.gameObject.activeInHierarchy) any = true;
                return any ? null : "Selected subject contains no active enabled renderers.";
            };
        }

        [Serializable] public sealed class RendererInfo
        {
            public string name, modality, asset, assetHash, path, settings;
            public Matrix4x4 localToWorld;
            public int splats;
        }
        public static RendererInfo[] Describe(GameObject subject)
        {
            var result = new List<RendererInfo>();
            foreach (var r in subject.GetComponentsInChildren<GaussianSplat2DRenderer>())
                if (r.enabled) result.Add(new RendererInfo { name = r.name, modality = "2D", asset = r.asset ? r.asset.name : "", assetHash = r.asset ? r.asset.dataHash.ToString() : "", localToWorld = r.transform.localToWorldMatrix, path = r.m_RenderPath.ToString(), splats = r.splatCount, settings = JsonUtility.ToJson(r) });
            foreach (var r in subject.GetComponentsInChildren<GaussianSplat3DRenderer>())
                if (r.enabled) result.Add(new RendererInfo { name = r.name, modality = "3D", asset = r.asset ? r.asset.name : "", assetHash = r.asset ? r.asset.dataHash.ToString() : "", localToWorld = r.transform.localToWorldMatrix, path = r.m_RenderPath.ToString(), splats = r.splatCount, settings = JsonUtility.ToJson(r) });
            return result.ToArray();
        }
    }
}
