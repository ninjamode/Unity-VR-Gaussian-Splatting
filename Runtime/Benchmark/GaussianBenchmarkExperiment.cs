using System;
using System.Collections.Generic;
using System.Linq;
using Gaussians.ThreeD;
using UnityEngine;

namespace Gaussians.Benchmark
{
    [Serializable]
    public sealed class GaussianBenchmarkVariant
    {
        public string id = "baseline";
        public GameObject subject;
        public GaussianBenchmarkRendererSettings sharedSettings;
        public GaussianBenchmarkOverrides settings = new();
        public GaussianBenchmarkOverrides EffectiveSettings => sharedSettings ? sharedSettings.settings : settings;
    }

    [DisallowMultipleComponent, AddComponentMenu("Gaussians/Benchmark/Experiment")]
    public sealed class GaussianBenchmarkExperiment : MonoBehaviour
    {
        public enum CameraSequence { FixedViews, Path }
        public string experimentId = "experiment";
        public GaussianBenchmarkConfig config;
        [Tooltip("A dedicated camera beneath this experiment. Projection comes from this camera.")]
        public Camera benchmarkCamera;
        public CameraSequence sequence;
        [Tooltip("Shared by every variant. Keep these outside subject roots.")]
        public Transform[] cameraPoints = Array.Empty<Transform>();
        [Min(1), Tooltip("Frame intervals between consecutive points. The final point is held afterward.")]
        public int framesPerSegment = 120;
        public GaussianBenchmarkVariant[] variants = { new() };

        public int SegmentCount => sequence == CameraSequence.FixedViews ? cameraPoints.Length : 1;

        public static void PathIndices(int sample, int interval, int count, out int a, out int b, out float t)
        {
            if (interval < 1 || count < 1) throw new ArgumentOutOfRangeException();
            sample = Math.Max(0, sample);
            a = Math.Min(sample / interval, count - 1);
            b = Math.Min(a + 1, count - 1);
            t = a == b ? 0 : (sample % interval) / (float)interval;
        }

        public Pose EvaluatePose(int sample, int segment = 0)
        {
            int a, b; float t;
            if (sequence == CameraSequence.FixedViews) { a = b = segment; t = 0; }
            else PathIndices(sample, framesPerSegment, cameraPoints.Length, out a, out b, out t);
            return new Pose(Vector3.Lerp(cameraPoints[a].position, cameraPoints[b].position, t),
                Quaternion.Slerp(cameraPoints[a].rotation, cameraPoints[b].rotation, t));
        }

        public void ApplyPose(int sample, int segment)
        {
            var pose = EvaluatePose(sample, segment);
            benchmarkCamera.transform.SetPositionAndRotation(pose.position, pose.rotation);
        }

        public string ValidationError()
        {
            if (!config) return "Assign a benchmark config.";
            if (config.ValidationError != null) return config.ValidationError;
            if (string.IsNullOrWhiteSpace(experimentId)) return "Experiment ID is required.";
            if (!benchmarkCamera || !benchmarkCamera.transform.IsChildOf(transform)) return "Camera must be a child of this experiment.";
            for (var parent = transform.parent; parent; parent = parent.parent)
                if (parent.GetComponent<GaussianBenchmarkExperiment>()) return "Experiments cannot be nested.";
            for (var node = benchmarkCamera.transform; node != transform; node = node.parent)
                if (!node.gameObject.activeSelf) return "Camera hierarchy must be active beneath the experiment.";
            if (benchmarkCamera.targetTexture) return "The runtime benchmark camera must render to the display, not a RenderTexture.";
            if (benchmarkCamera.allowDynamicResolution) return "Disable dynamic resolution on the benchmark camera.";
            if (config.mode == GaussianBenchmarkConfig.RunMode.XrFrameBudget && benchmarkCamera.GetComponentsInParent<Behaviour>(true)
                .Any(component => component.enabled && component.GetType().Name == "TrackedPoseDriver"))
                return "Remove or disable the camera's Tracked Pose Driver: this benchmark uses the authored camera path.";
            if (framesPerSegment < 1) return "Frames per segment must be positive.";
            if (cameraPoints == null || cameraPoints.Length == 0) return "Add at least one camera point.";
            foreach (var point in cameraPoints)
                if (!point || !point.IsChildOf(transform) || point == transform || point.IsChildOf(benchmarkCamera.transform))
                    return "Camera points must be experiment children outside the camera hierarchy.";
            if (variants == null || variants.Length == 0) return "Add at least one variant.";
            var ids = new HashSet<string>();
            foreach (var variant in variants)
            {
                if (variant == null || string.IsNullOrWhiteSpace(variant.id) || !ids.Add(variant.id)) return "Variant IDs must be non-empty and unique.";
                if (!variant.subject || variant.subject.transform == transform || !variant.subject.transform.IsChildOf(transform))
                    return "Every subject must be a child of the experiment.";
                if (benchmarkCamera.transform.IsChildOf(variant.subject.transform)) return "Camera must be outside subject roots.";
                for (var parent = variant.subject.transform.parent; parent != transform; parent = parent.parent)
                    if (!parent.gameObject.activeSelf) return "Subject ancestors beneath the experiment must be active.";
                if (variant.subject.GetComponentsInChildren<MonoBehaviour>(true).Any(b => b is IGaussianSplat3DDeformation && b.enabled)
                    && !GetComponents<GaussianBenchmarkExtension>().Any(e => e.enabled))
                    return "Deformed subjects require a benchmark extension to control animation (for 4D, add 4D Controls).";
                foreach (var point in cameraPoints)
                    if (point.IsChildOf(variant.subject.transform)) return "Camera points must be outside subject roots so variants share the same poses.";
                foreach (var other in variants)
                    if (other?.subject && other.subject != variant.subject && variant.subject.transform.IsChildOf(other.subject.transform))
                        return "Subject roots may be reused by variants, but cannot be nested inside each other.";
                string error = GaussianBenchmarkRenderers.Validate(variant.subject, variant.EffectiveSettings);
                if (error != null) return variant.id + ": " + error;
            }
            foreach (var camera in GetComponentsInChildren<Camera>(true))
                if (camera != benchmarkCamera && camera.enabled) return "Disable other cameras under the experiment.";
            foreach (var extension in GetComponents<GaussianBenchmarkExtension>())
                if (extension.enabled)
                {
                    string error = extension.Validate(this);
                    if (error != null) return extension.GetType().Name + ": " + error;
                }
            return null;
        }

        void OnDrawGizmosSelected()
        {
            if (cameraPoints == null) return;
            Gizmos.color = Color.cyan;
            for (int i = 0; i < cameraPoints.Length; i++)
            {
                var point = cameraPoints[i];
                if (!point) continue;
                if (sequence == CameraSequence.Path && i > 0 && cameraPoints[i - 1])
                    Gizmos.DrawLine(cameraPoints[i - 1].position, point.position);
                Gizmos.matrix = Matrix4x4.TRS(point.position, point.rotation, Vector3.one);
                Gizmos.DrawFrustum(Vector3.zero, benchmarkCamera ? benchmarkCamera.fieldOfView : 60, .35f, .02f,
                    config ? config.width / (float)config.height : 16f / 9f);
                Gizmos.matrix = Matrix4x4.identity;
            }
        }
    }
}
