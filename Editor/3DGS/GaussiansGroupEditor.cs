// SPDX-License-Identifier: MIT
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Gaussians.ThreeD.Editor
{
    [CustomEditor(typeof(GaussiansGroup))]
    public sealed class GaussiansGroupEditor : UnityEditor.Editor
    {
        bool m_AdvancedExpanded, m_ResourcesExpanded;
        public override void OnInspectorGUI()
        {
            var group = (GaussiansGroup)target;
            serializedObject.Update();
            // Record affected ownership references as well as the serialized membership list.
            var before = group.Members.Where(r => r).ToArray();
            Undo.RecordObjects(before, "Edit Gaussian group membership");
            EditorGUI.BeginChangeCheck();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("m_Members"), true);
            if (EditorGUI.EndChangeCheck())
            {
                var members = serializedObject.FindProperty("m_Members");
                for (int i = 0; i < members.arraySize; i++)
                    if (members.GetArrayElementAtIndex(i).objectReferenceValue is GaussianSplat3DRenderer r)
                        Undo.RecordObject(r, "Edit Gaussian group membership");
                serializedObject.ApplyModifiedProperties();
                group.ValidateMembers();
                foreach (var r in before)
                    if (!group.Members.Contains(r) && r.group == null)
                    {
                        var owner = new SerializedObject(r); owner.FindProperty("m_Group").objectReferenceValue = null; owner.ApplyModifiedProperties();
                    }
                foreach (var r in group.Members) if (r) { EditorUtility.SetDirty(r); PrefabUtility.RecordPrefabInstancePropertyModifications(r); }
                EditorUtility.SetDirty(group);
                serializedObject.Update();
            }
            if (GUILayout.Button("Add Hierarchy"))
            {
                Undo.RecordObject(group, "Add Gaussian hierarchy");
                var renderers = group.GetComponentsInChildren<GaussianSplat3DRenderer>(true);
                Undo.RecordObjects(renderers, "Add Gaussian hierarchy");
                group.AddHierarchy();
                foreach (var r in renderers) { EditorUtility.SetDirty(r); PrefabUtility.RecordPrefabInstancePropertyModifications(r); }
                EditorUtility.SetDirty(group); PrefabUtility.RecordPrefabInstancePropertyModifications(group);
                serializedObject.Update();
            }
            EditorGUILayout.Space();
            // Header is drawn by the alpha property's HeaderAttribute.
            EditorGUILayout.PropertyField(serializedObject.FindProperty("m_AlphaCutoff"), new GUIContent("Alpha Cutoff"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("m_OpacityAwareBounds"), new GUIContent("Opacity Aware Bounds"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("m_WriteDepth"), new GUIContent("Write Depth (URP Only)"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("m_CompactionThreshold"));
            var precision = serializedObject.FindProperty("m_SortPrecision");
            EditorGUI.BeginChangeCheck();
            bool lowPrecision = EditorGUILayout.Toggle(new GUIContent("16-bit Sorting (Experimental)"), precision.intValue == 16);
            if (EditorGUI.EndChangeCheck()) precision.intValue = lowPrecision ? 16 : 32;
            EditorGUILayout.PropertyField(serializedObject.FindProperty("m_SortNthFrame"));
            
            EditorGUILayout.Space();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("m_Cutouts"), new GUIContent("Group Cutouts"), true);
            
            EditorGUILayout.Space();
            m_AdvancedExpanded = EditorGUILayout.Foldout(m_AdvancedExpanded, "Advanced Options", true, EditorStyles.foldoutHeader);
            if (m_AdvancedExpanded)
            {
                EditorGUILayout.PropertyField(serializedObject.FindProperty("m_RenderPath"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("m_ProjectionMode"), new GUIContent("Projection"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("m_ConvertGammaToLinear"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("m_RenderOrder"));
                using (new EditorGUI.DisabledScope(true))
                    EditorGUILayout.EnumPopup("Render Mode", GaussianSplat3DRenderer.RenderMode.Splats);
            }
            m_ResourcesExpanded = EditorGUILayout.Foldout(m_ResourcesExpanded, "Resources", true, EditorStyles.foldoutHeader);
            if (m_ResourcesExpanded)
            {
                EditorGUILayout.PropertyField(serializedObject.FindProperty("m_ShaderSplats"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("m_ShaderComposite"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("m_CSSplatUtilities"));
            }
            serializedObject.ApplyModifiedProperties();
            
            EditorGUILayout.Space();
            int assignedMembers = group.Members.Count(r => r);
            EditorGUILayout.LabelField("Members", $"{assignedMembers} total / {group.ActiveMemberCount} active / {assignedMembers - group.ActiveMemberCount} inactive");
            EditorGUILayout.LabelField("Group GPU memory", EditorUtility.FormatBytes(group.AllocatedBytes));
            string error = group.isActiveAndEnabled ? group.RenderingError : null;
            EditorGUILayout.HelpBox(error ?? (group.isActiveAndEnabled ? group.Status : "Group is disabled. Enabled members render independently using their own settings."), error != null ? MessageType.Error : MessageType.Info);
        }
        public override bool RequiresConstantRepaint() => EditorApplication.isPlaying;
    }
}
