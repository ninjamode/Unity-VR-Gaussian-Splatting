using System;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Gaussians.Benchmark.Editor
{
    /// <summary>Applies a chosen benchmark sample to the authored scene for inspection.</summary>
    public static class GaussianBenchmarkScenePose
    {
        public static void Apply(GaussianBenchmarkExperiment experiment, int variantIndex, int sample, int view)
        {
            if (!experiment) throw new ArgumentNullException(nameof(experiment));
            if (experiment.ValidationError() is string validation) throw new InvalidOperationException(validation);
            if (variantIndex < 0 || variantIndex >= experiment.variants.Length) throw new ArgumentOutOfRangeException(nameof(variantIndex));
            if (!experiment.gameObject.activeInHierarchy) throw new InvalidOperationException("Activate the experiment before showing a pose.");
            if (sample < 0 || sample >= experiment.config.measuredFrames) throw new ArgumentOutOfRangeException(nameof(sample));
            if (view < 0 || view >= experiment.SegmentCount) throw new ArgumentOutOfRangeException(nameof(view));

            var scene = experiment.gameObject.scene;
            var experiments = scene.GetRootGameObjects()
                .SelectMany(root => root.GetComponentsInChildren<GaussianBenchmarkExperiment>(true))
                .Where(e => e.gameObject.activeInHierarchy).ToArray();
            var cameras = scene.GetRootGameObjects()
                .SelectMany(root => root.GetComponentsInChildren<Camera>(true))
                .Where(camera => camera.gameObject.activeInHierarchy).ToArray();
            var selected = experiment.variants[variantIndex];
            var extensions = experiment.GetComponents<GaussianBenchmarkExtension>().Where(e => e.enabled).ToArray();

            Undo.IncrementCurrentGroup();
            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Show Gaussian benchmark pose");
            try
            {
                // The scene is the inspection state. Record it as one normal Unity Undo step.
                foreach (var source in experiments) Undo.RegisterFullObjectHierarchyUndo(source.gameObject, "Show Gaussian benchmark pose");
                foreach (var camera in cameras) Undo.RecordObject(camera, "Show Gaussian benchmark pose");
                foreach (var camera in cameras) camera.enabled = false;
                foreach (var source in experiments)
                    foreach (var subject in source.variants.Select(v => v.subject).Distinct()) subject.SetActive(false);

                GaussianBenchmarkRenderers.Apply(selected.subject, selected.EffectiveSettings);
                foreach (var extension in extensions) extension.Configure(selected.subject, selected.id);
                selected.subject.SetActive(true);
                experiment.ApplyPose(sample, view);
                experiment.benchmarkCamera.gameObject.SetActive(true);
                experiment.benchmarkCamera.enabled = true;
                experiment.benchmarkCamera.ResetAspect();
                foreach (var extension in extensions) extension.Begin();
                foreach (var extension in extensions) extension.SetSample(sample, experiment.config.simulationStep);
                if (GaussianBenchmarkRenderers.CheckReady(selected.subject) is string error) throw new InvalidOperationException(error);
                foreach (var extension in extensions)
                    if (extension.CheckError() is string extensionError) throw new InvalidOperationException(extensionError);

                EditorSceneManager.MarkSceneDirty(scene);
                EditorApplication.QueuePlayerLoopUpdate();
            }
            catch
            {
                Undo.RevertAllDownToGroup(undoGroup);
                throw;
            }
            finally { Undo.CollapseUndoOperations(undoGroup); }
        }
    }
}
