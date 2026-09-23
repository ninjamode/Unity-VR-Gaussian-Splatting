using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Gaussians.Benchmark.Editor
{
    [CustomPropertyDrawer(typeof(GaussianBenchmark4DControls.Variant))]
    public sealed class GaussianBenchmark4DVariantDrawer : PropertyDrawer
    {
        static IEnumerable<string> Fields(SerializedProperty property)
        {
            yield return "variantId";
            yield return "mode";
            yield return "overrideSHStorage";
            if (property.FindPropertyRelative("overrideSHStorage").boolValue) yield return "shStorage";
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
                    EditorGUI.PropertyField(row, child, true);
                }
                EditorGUI.indentLevel--;
            }
            EditorGUI.EndProperty();
        }
    }
}
