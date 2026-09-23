using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Gaussians.Benchmark.Editor
{
    [CustomPropertyDrawer(typeof(GaussianBenchmarkVariant))]
    public sealed class GaussianBenchmarkVariantDrawer : PropertyDrawer
    {
        static IEnumerable<string> Fields(SerializedProperty property)
        {
            yield return "id";
            yield return "subject";
            yield return "sharedSettings";
            if (!property.FindPropertyRelative("sharedSettings").objectReferenceValue) yield return "settings";
        }

        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
        {
            float height = EditorGUIUtility.singleLineHeight;
            if (property.isExpanded)
                foreach (string field in Fields(property))
                    height += EditorGUIUtility.standardVerticalSpacing + EditorGUI.GetPropertyHeight(property.FindPropertyRelative(field), true);
            return height;
        }

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            EditorGUI.BeginProperty(position, label, property);
            var row = new Rect(position.x, position.y, position.width, EditorGUIUtility.singleLineHeight);
            property.isExpanded = EditorGUI.Foldout(row, property.isExpanded, label, true);
            if (property.isExpanded)
            {
                EditorGUI.indentLevel++;
                foreach (string field in Fields(property))
                {
                    var child = property.FindPropertyRelative(field);
                    row.y += row.height + EditorGUIUtility.standardVerticalSpacing;
                    row.height = EditorGUI.GetPropertyHeight(child, true);
                    var fieldLabel = field == "settings" ? new GUIContent("Inline Settings")
                        : field == "sharedSettings" ? new GUIContent("Shared Settings", "When assigned, this asset replaces the inline settings entirely. Clear it to use inline settings.")
                        : new GUIContent(child.displayName);
                    EditorGUI.PropertyField(row, child, fieldLabel, true);
                }
                EditorGUI.indentLevel--;
            }
            EditorGUI.EndProperty();
        }
    }
}
