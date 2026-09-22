using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Gaussians.FourD.Editor
{
    [CustomEditor(typeof(GaussianSplat4D))]
    public sealed class GaussianSplat4DEditor : UnityEditor.Editor
    {
        bool m_Preview, m_Resources;
        static readonly Dictionary<int, GaussianSplat4DEditor> s_PreviewOwners = new();
        int m_PreviewPlayerId;
        double m_PreviousUpdate, m_SavedPosition;
        float m_SavedModelTime;
        GaussianSplat4D Player => target as GaussianSplat4D;

        void OnEnable()
        {
            EditorApplication.update += UpdatePreview;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
        }
        void OnDisable()
        {
            EndPreview();
            EditorApplication.update -= UpdatePreview;
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
        }
        void OnPlayModeChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingEditMode) EndPreview();
        }

        public override void OnInspectorGUI()
        {
            var player = Player;
            float previousDuration = player.m_DurationSeconds;
            Vector2 previousRange = player.m_ModelTimeRange;
            EditorGUI.BeginChangeCheck();
            serializedObject.Update();
            DrawPropertiesExcluding(serializedObject, "m_Script", "m_ComputeShader", "m_NativeShader");
            serializedObject.ApplyModifiedProperties();
            if (EditorGUI.EndChangeCheck())
            {
                ApplyTimingChange(previousDuration,previousRange);
                RefreshView();
            }
            if (player.CompatibilityError != null) EditorGUILayout.HelpBox(player.CompatibilityError, MessageType.Warning);
            if (player.Error != null) EditorGUILayout.HelpBox(player.Error, MessageType.Error);
            if (player.m_Asset && player.m_Asset.Canonical && !player.Renderer.asset && GUILayout.Button("Assign Canonical Model"))
            {
                Undo.RecordObject(player.Renderer, "Assign canonical Gaussian model");
                player.Renderer.m_Asset = player.m_Asset.Canonical;
                player.Renderer.m_RenderPath = Gaussians.ThreeD.GaussianSplat3DRenderer.RenderPath.DirectTransparent;
                EditorUtility.SetDirty(player.Renderer);
                RefreshView();
            }
            m_Resources = EditorGUILayout.Foldout(m_Resources, "Resources", true, EditorStyles.foldoutHeader);
            if (m_Resources)
            {
                serializedObject.Update();
                EditorGUILayout.PropertyField(serializedObject.FindProperty("m_ComputeShader"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("m_NativeShader"));
                if (serializedObject.ApplyModifiedProperties()) player.Rebuild();
                if (GUILayout.Button("Rebuild Deformation Resources")) { player.Rebuild(); RefreshView(); }
            }
            if (player.SettingsError != null) EditorGUILayout.HelpBox(player.SettingsError,MessageType.Error);
            using (new EditorGUI.DisabledScope(!player.isActiveAndEnabled || player.SettingsError != null))
            {
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button(player.IsPlaying ? "Playing" : "Play"))
                    StartPlayback();
                if (GUILayout.Button("Pause")) player.Pause();
                if (GUILayout.Button("Stop"))
                {
                    if (s_PreviewOwners.TryGetValue(player.GetInstanceID(),out var owner)) owner.EndPreview();
                    RecordSeek(); player.Stop(); RefreshView();
                }
                EditorGUILayout.EndHorizontal();
                EditorGUI.BeginChangeCheck();
                float position = EditorGUILayout.Slider("Position",(float)player.NormalizedTime,0,1);
                if (EditorGUI.EndChangeCheck()) { RecordSeek(); player.SeekNormalized(position); RefreshView(); }
                EditorGUILayout.LabelField("Time",$"{player.PositionSeconds:F3} / {player.m_DurationSeconds:F3} s");
            }
            if (!Application.isPlaying)
                EditorGUILayout.HelpBox("Play starts an Editor preview; Pause holds it and Stop rewinds. Closing this Inspector during preview restores the pre-preview time. In Play Mode, the component advances playback independently of this Inspector.",MessageType.Info);
        }

        void ApplyTimingChange(float previousDuration,Vector2 previousRange)
        {
            if (previousDuration == Player.m_DurationSeconds && previousRange == Player.m_ModelTimeRange) return;
            if (!Application.isPlaying && !m_Preview && Player.Renderer)
                Undo.RecordObject(Player,"Change 4D playback timing");
            Player.ApplyTime();
        }

        void StartPlayback()
        {
            if (!Application.isPlaying && !m_Preview)
            {
                m_PreviewPlayerId = Player.GetInstanceID();
                if (s_PreviewOwners.TryGetValue(m_PreviewPlayerId,out var owner) && owner != this) owner.EndPreview();
                m_SavedPosition = Player.PositionSeconds;
                m_SavedModelTime = Player.m_ModelTime;
                m_Preview = true;
                s_PreviewOwners[m_PreviewPlayerId] = this;
            }
            m_PreviousUpdate = EditorApplication.timeSinceStartup;
            Player.Play();
        }

        void RecordSeek()
        {
            if (!Application.isPlaying && !m_Preview && Player.Renderer)
                Undo.RecordObject(Player,"Seek 4D Gaussian time");
        }
        void UpdatePreview()
        {
            if (Application.isPlaying) { if (Player && Player.IsPlaying) Repaint(); return; }
            if (!m_Preview || !Player) return;
            if (!Player.isActiveAndEnabled) { EndPreview(); return; }
            double now = EditorApplication.timeSinceStartup;
            bool wasPlaying = Player.IsPlaying;
            Player.Advance(now-m_PreviousUpdate);
            m_PreviousUpdate = now;
            if (wasPlaying) RefreshView();
        }
        void EndPreview()
        {
            if (!m_Preview) return;
            m_Preview = false;
            if (s_PreviewOwners.TryGetValue(m_PreviewPlayerId,out var owner) && owner == this) s_PreviewOwners.Remove(m_PreviewPlayerId);
            if (!Player) return;
            Player.Pause();
            Player.SeekSeconds(m_SavedPosition);
            if (Player.Renderer) Player.m_ModelTime = m_SavedModelTime;
            RefreshView();
        }
        void RefreshView()
        {
            Repaint();
            SceneView.RepaintAll();
            EditorApplication.QueuePlayerLoopUpdate();
        }
    }
}
