using System.IO;
using Gaussians.FourD.Inference;
using UnityEditor;
using UnityEngine;

namespace Gaussians.FourD.Editor
{
    [CustomEditor(typeof(GaussianSplat4DInferenceAsset))]
    public sealed class Gaussian4DInferenceAssetEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            var asset=(GaussianSplat4DInferenceAsset)target;
            if(asset.Data) EditorGUILayout.LabelField("Model",$"{asset.Data.GaussianCount:N0} splats · SH degree {asset.Data.SHDegree}");
            using(new EditorGUI.DisabledScope(true)) EditorGUILayout.ObjectField("Canonical Model",asset.Canonical,typeof(Gaussians.ThreeD.GaussianSplat3DAsset),false);
            var settings=AssetDatabase.LoadAssetAtPath<Gaussian4DImportSettings>(Path.GetDirectoryName(AssetDatabase.GetAssetPath(asset))+"/ImportSettings.asset");
            if(settings) Gaussian4DImportSettingsEditor.Draw(settings);
            else EditorGUILayout.LabelField("Included backends",asset.Data?asset.Data.Backends.ToString():"No data");
        }
    }
    [CustomEditor(typeof(Gaussian4DImportSettings))]
    public sealed class Gaussian4DImportSettingsEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI() => Draw((Gaussian4DImportSettings)target);
        internal static void Draw(Gaussian4DImportSettings settings)
        {
            EditorGUILayout.LabelField("Included backends",settings.backends.ToString());
            EditorGUILayout.LabelField("Canonical SH",settings.canonicalSH.ToString());
            EditorGUILayout.LabelField("Spatial ordering",settings.mortonOrder?"Morton":"Source order");
            EditorGUILayout.LabelField("Canonical GPU data",EditorUtility.FormatBytes(settings.canonicalBytes));
            EditorGUILayout.LabelField("Runtime tensors",EditorUtility.FormatBytes(settings.runtimeTensorBytes));
            EditorGUILayout.LabelField("ONNX source",EditorUtility.FormatBytes(settings.onnxBytes));
            EditorGUILayout.LabelField("Inference scratch memory is additional.",EditorStyles.miniLabel);
            if(GUILayout.Button("Edit Import Settings…")) GaussianSplat4DAssetCreator.OpenReimport(settings);
        }
    }
    [CustomEditor(typeof(GaussianSplat4DAsset))]
    public sealed class Gaussian4DDataAssetEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            var data=(GaussianSplat4DAsset)target;
            EditorGUILayout.LabelField("Model",$"{data.GaussianCount:N0} splats · SH degree {data.SHDegree}");
            EditorGUILayout.LabelField("Included backends",data.Backends.ToString());
            EditorGUILayout.LabelField("Runtime tensors",(data.Tensors?.Count??0).ToString());
            EditorGUILayout.HelpBox("Runtime data owned by the 4D importer. Change settings through the Inference asset's import controls.",MessageType.Info);
        }
    }
}
