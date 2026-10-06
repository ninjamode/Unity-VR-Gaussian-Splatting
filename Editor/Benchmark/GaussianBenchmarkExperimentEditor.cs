using System;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Gaussians.Benchmark.Editor
{
    [CustomEditor(typeof(GaussianBenchmarkExperiment))]
    public sealed class GaussianBenchmarkExperimentEditor : UnityEditor.Editor
    {
        const string LivePreviewUndoName = "Live preview Gaussian benchmark samples";
        static GaussianBenchmarkExperimentEditor previewUndoOwner;
        int variantIndex, sample, view;
        string poseError;
        bool livePreview, applyingPose;
        int previewUndoGroup = -1;

        void OnEnable()
        {
            Undo.postprocessModifications += OnUndoModifications;
            Undo.undoRedoPerformed += OnUndoRedo;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
        }

        void OnDisable()
        {
            StopLivePreview();
            Undo.postprocessModifications -= OnUndoModifications;
            Undo.undoRedoPerformed -= OnUndoRedo;
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
        }

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            var experiment = (GaussianBenchmarkExperiment)target;
            EditorGUILayout.Space();
            EditorGUILayout.HelpBox("Variants share camera points. Align subjects with their child Transforms. Show Pose applies the selection to scene objects. Live Preview follows sample, variant and view changes; turning it off keeps the last pose. Both modify the scene and apply renderer settings. Unchecked overrides inherit the scene's current renderer values.", MessageType.Info);
            if (GUILayout.Button("Capture Scene View as Camera Point")) CapturePoint(experiment);
            string error = experiment.ValidationError();
            if (error != null) EditorGUILayout.HelpBox(error, MessageType.Warning);
            bool canPreview = error == null && !EditorApplication.isPlayingOrWillChangePlaymode && !GaussianBenchmarkRunner.Active;
            if (!canPreview) StopLivePreview();
            using (new EditorGUI.DisabledScope(!canPreview))
            {
                var previousSelection = (variantIndex, sample, view);
                if (experiment.variants != null && experiment.variants.Length > 0)
                    variantIndex = EditorGUILayout.Popup("Variant", Mathf.Clamp(variantIndex, 0, experiment.variants.Length - 1), experiment.variants.Select(v => v?.id ?? "missing").ToArray());
                if (experiment.sequence == GaussianBenchmarkExperiment.CameraSequence.FixedViews && experiment.cameraPoints.Length > 0)
                    view = EditorGUILayout.Popup("View", Mathf.Clamp(view, 0, experiment.cameraPoints.Length - 1), experiment.cameraPoints.Select(p => p ? p.name : "missing").ToArray());
                else view = 0;
                sample = EditorGUILayout.IntSlider("Sample (zero based)", sample, 0, experiment.config ? experiment.config.measuredFrames - 1 : 599);
                bool showPose;
                bool preview;
                using (new EditorGUILayout.HorizontalScope())
                {
                    showPose = GUILayout.Button("Show Pose in Game View");
                    var background = GUI.backgroundColor;
                    if (livePreview) GUI.backgroundColor = new Color(.45f, .85f, .45f);
                    preview = GUILayout.Toggle(livePreview, new GUIContent("Live Preview Samples", "Immediately show the selected sample, variant and view in the Game View."), "Button");
                    GUI.backgroundColor = background;
                }
                if (canPreview)
                {
                    if (preview != livePreview) SetLivePreview(experiment, preview);
                    else if (showPose || (livePreview && previousSelection != (variantIndex, sample, view))) ShowPose(experiment);
                }
            }
            if (poseError != null) EditorGUILayout.HelpBox(poseError, MessageType.Error);
            using (new EditorGUI.DisabledScope(error != null || GaussianBenchmarkRunner.Active))
                if (GUILayout.Button(Application.isPlaying ? "Run This Experiment" : "Enter Play Mode and Run This Experiment"))
                    GaussianBenchmarkMenus.Run(experiment);
            if (GaussianBenchmarkRunner.Active && GUILayout.Button("Cancel Benchmark")) GaussianBenchmarkRunner.Active.Cancel();
        }

        void ShowPose(GaussianBenchmarkExperiment experiment)
        {
            // Flush pending edits before deciding whether this continues our Undo session.
            Undo.FlushUndoRecordObjects();
            if (previewUndoOwner != this || Undo.GetCurrentGroupName() != LivePreviewUndoName) previewUndoGroup = -1;
            applyingPose = true;
            try
            {
                if (livePreview)
                {
                    GaussianBenchmarkScenePose.Apply(experiment, variantIndex, sample, view, LivePreviewUndoName);
                    Undo.FlushUndoRecordObjects();
                    if (previewUndoGroup < 0) previewUndoGroup = Undo.GetCurrentGroup();
                    Undo.CollapseUndoOperations(previewUndoGroup);
                    previewUndoOwner = this;
                }
                else GaussianBenchmarkScenePose.Apply(experiment, variantIndex, sample, view);
                poseError = null;
            }
            catch (Exception e)
            {
                poseError = e.Message;
                StopLivePreview();
                Debug.LogException(e);
            }
            finally
            {
                applyingPose = false;
                // Keep later edits out of the last preview operation, even in the same GUI event.
                Undo.IncrementCurrentGroup();
            }
        }

        void SetLivePreview(GaussianBenchmarkExperiment experiment, bool enabled)
        {
            StopLivePreview();
            livePreview = enabled;
            if (enabled) ShowPose(experiment);
        }

        void StopLivePreview()
        {
            livePreview = false;
            previewUndoGroup = -1;
            if (previewUndoOwner == this) previewUndoOwner = null;
        }

        UndoPropertyModification[] OnUndoModifications(UndoPropertyModification[] modifications)
        {
            if (!applyingPose && modifications.Length > 0) previewUndoGroup = -1;
            return modifications;
        }

        void OnUndoRedo()
        {
            if (applyingPose) return;
            StopLivePreview();
            Repaint();
        }

        void OnPlayModeChanged(PlayModeStateChange state)
        {
            StopLivePreview();
            Repaint();
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
