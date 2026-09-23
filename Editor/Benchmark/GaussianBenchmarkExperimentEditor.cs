using System;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Gaussians.Benchmark.Editor
{
    [CustomEditor(typeof(GaussianBenchmarkExperiment))]
    public sealed class GaussianBenchmarkExperimentEditor : UnityEditor.Editor
    {
        int variantIndex, sample, view;
        string poseError;

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            var experiment = (GaussianBenchmarkExperiment)target;
            EditorGUILayout.Space();
            EditorGUILayout.HelpBox("Variants share camera points. Align subjects with their child Transforms. Show Pose applies the selected variant and sample to scene objects, so the Game View remains on that pose after you change selection. Unchecked overrides inherit the scene's current renderer values.", MessageType.Info);
            if (GUILayout.Button("Capture Scene View as Camera Point")) CapturePoint(experiment);
            string error = experiment.ValidationError();
            if (error != null) EditorGUILayout.HelpBox(error, MessageType.Warning);
            using (new EditorGUI.DisabledScope(error != null || Application.isPlaying))
            {
                if (experiment.variants != null && experiment.variants.Length > 0)
                    variantIndex = EditorGUILayout.Popup("Variant", Mathf.Clamp(variantIndex, 0, experiment.variants.Length - 1), experiment.variants.Select(v => v?.id ?? "missing").ToArray());
                if (experiment.sequence == GaussianBenchmarkExperiment.CameraSequence.FixedViews && experiment.cameraPoints.Length > 0)
                    view = EditorGUILayout.Popup("View", Mathf.Clamp(view, 0, experiment.cameraPoints.Length - 1), experiment.cameraPoints.Select(p => p ? p.name : "missing").ToArray());
                else view = 0;
                sample = EditorGUILayout.IntSlider("Sample (zero based)", sample, 0, experiment.config ? experiment.config.measuredFrames - 1 : 599);
                if (GUILayout.Button("Show Pose in Game View")) ShowPose(experiment);
            }
            if (poseError != null) EditorGUILayout.HelpBox(poseError, MessageType.Error);
            using (new EditorGUI.DisabledScope(error != null || GaussianBenchmarkRunner.Active))
                if (GUILayout.Button(Application.isPlaying ? "Run This Experiment" : "Enter Play Mode and Run This Experiment"))
                    GaussianBenchmarkMenus.Run(experiment);
            if (GaussianBenchmarkRunner.Active && GUILayout.Button("Cancel Benchmark")) GaussianBenchmarkRunner.Active.Cancel();
        }

        void ShowPose(GaussianBenchmarkExperiment experiment)
        {
            try
            {
                GaussianBenchmarkScenePose.Apply(experiment, variantIndex, sample, view);
                poseError = null;
            }
            catch (Exception e)
            {
                poseError = e.Message;
                Debug.LogException(e);
            }
        }

        public static void CapturePoint(GaussianBenchmarkExperiment experiment)
        {
            var sceneView = SceneView.lastActiveSceneView;
            if (!sceneView) throw new InvalidOperationException("Open a Scene View first.");
            var point = new GameObject("View " + (experiment.cameraPoints.Length + 1));
            Undo.RegisterCreatedObjectUndo(point, "Capture benchmark point");
            point.transform.SetParent(experiment.transform, false);
            point.transform.SetPositionAndRotation(sceneView.camera.transform.position, sceneView.camera.transform.rotation);
            Undo.RecordObject(experiment, "Capture benchmark point");
            ArrayUtility.Add(ref experiment.cameraPoints, point.transform);
            EditorUtility.SetDirty(experiment);
        }
    }
}
