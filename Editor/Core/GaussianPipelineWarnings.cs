// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Gaussians.Core.Editor
{
    /// <summary>Editor-only setup diagnostics without requiring either optional SRP package.</summary>
    public static class GaussianPipelineWarnings
    {
        const string UrpAsset = "UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset";
        const string HdrpAsset = "UnityEngine.Rendering.HighDefinition.HDRenderPipelineAsset";
        const string HdrpVolume = "UnityEngine.Rendering.HighDefinition.CustomPassVolume";

        public static void Draw(bool is2D)
        {
            var warning = GetWarning(GraphicsSettings.currentRenderPipeline, is2D);
            if (!string.IsNullOrEmpty(warning))
                EditorGUILayout.HelpBox(warning, MessageType.Warning);
        }

        public static string GetWarning(RenderPipelineAsset pipeline, bool is2D)
        {
            if (pipeline == null) return null; // Built-in registers its own camera hooks.
            if (IsType(pipeline.GetType(), UrpAsset)) return GetUrpWarning(pipeline, is2D);
            if (IsType(pipeline.GetType(), HdrpAsset)) return GetHdrpWarning(is2D);
            return null;
        }

        static bool IsType(Type type, string fullName)
        {
            for (; type != null; type = type.BaseType)
                if (type.FullName == fullName) return true;
            return false;
        }

        static string GetUrpWarning(RenderPipelineAsset pipeline, bool is2D)
        {
            // SerializedObject keeps this editor assembly independent of optional pipeline assemblies.
            using var settings = new SerializedObject(pipeline);
            var renderers = settings.FindProperty("m_RendererDataList");
            if (renderers == null || !renderers.isArray) return null;
            var missing = new List<string>();
            var visited = new HashSet<UnityEngine.Object>();
            for (int i = 0; i < renderers.arraySize; i++)
            {
                var data = renderers.GetArrayElementAtIndex(i).objectReferenceValue;
                if (data == null || !visited.Add(data)) continue;
                using var renderer = new SerializedObject(data);
                var features = renderer.FindProperty("m_RendererFeatures");
                bool found = false;
                if (features != null)
                    for (int j = 0; j < features.arraySize; j++)
                    {
                        var feature = features.GetArrayElementAtIndex(j).objectReferenceValue;
                        if (feature == null || !IsGaussianIntegration(feature.GetType(), false)) continue;
                        using var serializedFeature = new SerializedObject(feature);
                        var active = serializedFeature.FindProperty("m_Active");
                        if (active != null && active.boolValue) { found = true; break; }
                    }
                if (!found) missing.Add("'" + data.name + "'");
            }
            return missing.Count == 0 ? null :
                "No enabled Gaussians renderer feature on " + string.Join(", ", missing) +
                " in the active URP asset '" + pipeline.name + "'. Cameras using these renderer assets cannot render " +
                (is2D ? "2D" : "3D") + " splats. Add or enable Gaussians in each asset's Renderer Features list.";
        }

        static bool IsGaussianIntegration(Type type, bool hdrp)
            => type.FullName == (hdrp ? "Gaussians.GaussiansHDRPPass" : "Gaussians.GaussiansURPFeature");

        static string GetHdrpWarning(bool is2D)
        {
            Type volumeType = null;
            foreach (var type in TypeCache.GetTypesDerivedFrom<MonoBehaviour>())
                if (type.FullName == HdrpVolume) { volumeType = type; break; }
            if (volumeType == null) return null;
            foreach (var obj in UnityEngine.Object.FindObjectsByType(volumeType, FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (obj is not Behaviour volume || !volume.isActiveAndEnabled || !volume.gameObject.scene.IsValid()) continue;
                using var serialized = new SerializedObject(volume);
                var injection = serialized.FindProperty("injectionPoint");
                if (injection == null || injection.enumNames[injection.enumValueIndex] != "BeforeTransparent") continue;
                var passes = serialized.FindProperty("customPasses");
                if (passes == null) continue;
                for (int i = 0; i < passes.arraySize; i++)
                {
                    var pass = passes.GetArrayElementAtIndex(i);
                    var value = pass.managedReferenceValue;
                    if (value == null || !IsGaussianIntegration(value.GetType(), true)) continue;
                    var enabled = pass.FindPropertyRelative("enabled");
                    if (enabled != null && enabled.boolValue) return null;
                }
            }
            return "No enabled Gaussians HDRP Pass for " + (is2D ? "2D" : "3D") +
                " splats was found at Before Transparent in an active Custom Pass Volume in the loaded scenes. " +
                "Add or enable the pass in a volume that covers the viewing camera. Local volumes and camera-specific settings still determine where the pass runs.";
        }
    }
}
