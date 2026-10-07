// SPDX-License-Identifier: MIT

using System;
using System.IO;
using Gaussians.ThreeD.Editor.Utils;
using Gaussians.ThreeD;
using Gaussians.Core.Editor.Utils;
using UnityEditor;
using UnityEngine;

namespace Gaussians.ThreeD.Editor
{
    [UnityEngine.Scripting.APIUpdating.MovedFrom(true, "GaussianSplatting.Editor", "Gaussians.ThreeD.Editor", "GaussianSplatAssetCreator")]
    public class GaussianSplat3DAssetCreator : EditorWindow
    {
        const string kPrefQuality = "net.kleinbeck.gaussians.3d.CreatorQuality";
        const string kPrefOutputFolder = "net.kleinbeck.gaussians.3d.CreatorOutputFolder";

        enum DataQuality
        {
            VeryHigh,
            High,
            Medium,
            Low,
            VeryLow,
            Custom,
        }

        readonly FilePickerControl m_FilePicker = new();

        [SerializeField] string m_InputFile;
        [SerializeField] bool m_ImportCameras = true;

        [SerializeField] string m_OutputFolder = "Assets/Gaussians/3D";
        [SerializeField] DataQuality m_Quality = DataQuality.Medium;
        [SerializeField] GaussianSplat3DAsset.VectorFormat m_FormatPos;
        [SerializeField] GaussianSplat3DAsset.VectorFormat m_FormatScale;
        [SerializeField] GaussianSplat3DAsset.ColorFormat m_FormatColor;
        [SerializeField] GaussianSplat3DAsset.SHFormat m_FormatSH;

        string m_ErrorMessage;
        GaussianSplat3DSourceInfo m_SourceInfo;
        string m_PrevFilePath;
        int m_PrevVertexCount;
        long m_PrevFileSize;

        bool isUsingChunks =>
            m_FormatPos != GaussianSplat3DAsset.VectorFormat.Float32 ||
            m_FormatScale != GaussianSplat3DAsset.VectorFormat.Float32 ||
            m_FormatColor != GaussianSplat3DAsset.ColorFormat.Float32x4 ||
            m_FormatSH != GaussianSplat3DAsset.SHFormat.Float32;

        [MenuItem("Tools/Gaussians/3D/Create Splat Asset")]
        public static void Init()
        {
            var window = GetWindowWithRect<GaussianSplat3DAssetCreator>(new Rect(50, 50, 360, 420), false, "3D Gaussian Splat Creator", true);
            window.minSize = new Vector2(320, 320);
            window.maxSize = new Vector2(1500, 1500);
            window.Show();
        }

        void Awake()
        {
            m_Quality = (DataQuality)EditorPrefs.GetInt(kPrefQuality, (int)DataQuality.Medium);
            m_OutputFolder = EditorPrefs.GetString(kPrefOutputFolder, "Assets/Gaussians/3D");
        }

        void OnEnable()
        {
            ApplyQualityLevel();
        }

        void OnGUI()
        {
            EditorGUILayout.Space();
            GUILayout.Label("Input data", EditorStyles.boldLabel);
            var rect = EditorGUILayout.GetControlRect(true);
            m_InputFile = m_FilePicker.PathFieldGUI(rect, new GUIContent("Input PLY/SPZ File"), m_InputFile, "ply,spz", "PointCloudFile");
            m_ImportCameras = EditorGUILayout.Toggle("Import Cameras", m_ImportCameras);

            if (m_InputFile != m_PrevFilePath)
            {
                m_PrevVertexCount = 0;
                m_ErrorMessage = null;
                m_SourceInfo = default;
                try
                {
                    if (!string.IsNullOrWhiteSpace(m_InputFile))
                    {
                        m_SourceInfo = GaussianSplat3DImporter.Inspect(m_InputFile);
                        m_PrevVertexCount = m_SourceInfo.SplatCount;
                        if (m_PrevVertexCount == 0)
                            m_ErrorMessage = "The source contains no Gaussians to import";
                    }
                }
                catch (Exception ex)
                {
                    m_ErrorMessage = ex.Message;
                }

                m_PrevFileSize = File.Exists(m_InputFile) ? new FileInfo(m_InputFile).Length : 0;
                m_PrevFilePath = m_InputFile;
            }

            if (m_PrevVertexCount > 0)
                EditorGUILayout.LabelField("Detected Format", m_SourceInfo.DisplayName);
            if (m_SourceInfo.Format == GaussianSplat3DSourceFormat.Layered3DGS && m_PrevVertexCount > 0)
                EditorGUILayout.HelpBox("One Gaussian asset will be created for each layer.", MessageType.Info);
            if (m_PrevVertexCount > 0)
                EditorGUILayout.LabelField("File Size", $"{EditorUtility.FormatBytes(m_PrevFileSize)} - {m_PrevVertexCount:N0} splats");
            else
                GUILayout.Space(EditorGUIUtility.singleLineHeight);

            EditorGUILayout.Space();
            GUILayout.Label("Output", EditorStyles.boldLabel);
            rect = EditorGUILayout.GetControlRect(true);
            string newOutputFolder = m_FilePicker.PathFieldGUI(rect, new GUIContent("Output Folder"), m_OutputFolder, null, "Gaussians.3D.AssetOutputFolder");
            if (newOutputFolder != m_OutputFolder)
            {
                m_OutputFolder = newOutputFolder;
                EditorPrefs.SetString(kPrefOutputFolder, m_OutputFolder);
            }

            var newQuality = (DataQuality) EditorGUILayout.EnumPopup("Quality", m_Quality);
            if (newQuality != m_Quality)
            {
                m_Quality = newQuality;
                EditorPrefs.SetInt(kPrefQuality, (int)m_Quality);
                ApplyQualityLevel();
            }

            long sizePos = 0, sizeOther = 0, sizeCol = 0, sizeSHs = 0, totalSize = 0;
            if (m_PrevVertexCount > 0 && m_SourceInfo.Format != GaussianSplat3DSourceFormat.Layered3DGS)
            {
                sizePos = GaussianSplat3DAsset.CalcPosDataSize(m_PrevVertexCount, m_FormatPos);
                sizeOther = GaussianSplat3DAsset.CalcOtherDataSize(m_PrevVertexCount, m_FormatScale);
                sizeCol = GaussianSplat3DAsset.CalcColorDataSize(m_PrevVertexCount, m_FormatColor);
                sizeSHs = GaussianSplat3DAsset.CalcSHDataSize(m_PrevVertexCount, m_FormatSH);
                long sizeChunk = isUsingChunks ? GaussianSplat3DAsset.CalcChunkDataSize(m_PrevVertexCount) : 0;
                totalSize = sizePos + sizeOther + sizeCol + sizeSHs + sizeChunk;
            }

            const float kSizeColWidth = 70;
            EditorGUI.BeginDisabledGroup(m_Quality != DataQuality.Custom);
            EditorGUI.indentLevel++;
            GUILayout.BeginHorizontal();
            m_FormatPos = (GaussianSplat3DAsset.VectorFormat)EditorGUILayout.EnumPopup("Position", m_FormatPos);
            GUILayout.Label(sizePos > 0 ? EditorUtility.FormatBytes(sizePos) : string.Empty, GUILayout.Width(kSizeColWidth));
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            m_FormatScale = (GaussianSplat3DAsset.VectorFormat)EditorGUILayout.EnumPopup("Scale", m_FormatScale);
            GUILayout.Label(sizeOther > 0 ? EditorUtility.FormatBytes(sizeOther) : string.Empty, GUILayout.Width(kSizeColWidth));
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            m_FormatColor = (GaussianSplat3DAsset.ColorFormat)EditorGUILayout.EnumPopup("Color", m_FormatColor);
            GUILayout.Label(sizeCol > 0 ? EditorUtility.FormatBytes(sizeCol) : string.Empty, GUILayout.Width(kSizeColWidth));
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            m_FormatSH = (GaussianSplat3DAsset.SHFormat) EditorGUILayout.EnumPopup("SH", m_FormatSH);
            GUIContent shGC = new GUIContent();
            shGC.text = sizeSHs > 0 ? EditorUtility.FormatBytes(sizeSHs) : string.Empty;
            if (m_FormatSH >= GaussianSplat3DAsset.SHFormat.Cluster64k)
            {
                shGC.tooltip = "Note that SH clustering is not fast! (3-10 minutes for 6M splats)";
                shGC.image = EditorGUIUtility.IconContent("console.warnicon.sml").image;
            }
            GUILayout.Label(shGC, GUILayout.Width(kSizeColWidth));
            GUILayout.EndHorizontal();
            EditorGUI.indentLevel--;
            EditorGUI.EndDisabledGroup();
            if (totalSize > 0)
                EditorGUILayout.LabelField("Asset Size", $"{EditorUtility.FormatBytes(totalSize)} - {(double) m_PrevFileSize / totalSize:F2}x smaller");
            else
                GUILayout.Space(EditorGUIUtility.singleLineHeight);


            EditorGUILayout.Space();
            GUILayout.BeginHorizontal();
            GUILayout.Space(30);
            EditorGUI.BeginDisabledGroup(m_PrevVertexCount <= 0);
            if (GUILayout.Button("Create Asset"))
            {
                CreateAsset();
            }
            EditorGUI.EndDisabledGroup();
            GUILayout.Space(30);
            GUILayout.EndHorizontal();

            if (!string.IsNullOrWhiteSpace(m_ErrorMessage))
            {
                EditorGUILayout.HelpBox(m_ErrorMessage, MessageType.Error);
            }
        }

        void ApplyQualityLevel()
        {
            switch (m_Quality)
            {
                case DataQuality.Custom:
                    break;
                case DataQuality.VeryLow: // 18.62x smaller, 32.27 PSNR
                    m_FormatPos = GaussianSplat3DAsset.VectorFormat.Norm11;
                    m_FormatScale = GaussianSplat3DAsset.VectorFormat.Norm6;
                    m_FormatColor = GaussianSplat3DAsset.ColorFormat.BC7;
                    m_FormatSH = GaussianSplat3DAsset.SHFormat.Cluster4k;
                    break;
                case DataQuality.Low: // 14.01x smaller, 35.17 PSNR
                    m_FormatPos = GaussianSplat3DAsset.VectorFormat.Norm11;
                    m_FormatScale = GaussianSplat3DAsset.VectorFormat.Norm6;
                    m_FormatColor = GaussianSplat3DAsset.ColorFormat.Norm8x4;
                    m_FormatSH = GaussianSplat3DAsset.SHFormat.Cluster16k;
                    break;
                case DataQuality.Medium: // 5.14x smaller, 47.46 PSNR
                    m_FormatPos = GaussianSplat3DAsset.VectorFormat.Norm11;
                    m_FormatScale = GaussianSplat3DAsset.VectorFormat.Norm11;
                    m_FormatColor = GaussianSplat3DAsset.ColorFormat.Norm8x4;
                    m_FormatSH = GaussianSplat3DAsset.SHFormat.Norm6;
                    break;
                case DataQuality.High: // 2.94x smaller, 57.77 PSNR
                    m_FormatPos = GaussianSplat3DAsset.VectorFormat.Norm16;
                    m_FormatScale = GaussianSplat3DAsset.VectorFormat.Norm16;
                    m_FormatColor = GaussianSplat3DAsset.ColorFormat.Float16x4;
                    m_FormatSH = GaussianSplat3DAsset.SHFormat.Norm11;
                    break;
                case DataQuality.VeryHigh: // 1.05x smaller
                    m_FormatPos = GaussianSplat3DAsset.VectorFormat.Float32;
                    m_FormatScale = GaussianSplat3DAsset.VectorFormat.Float32;
                    m_FormatColor = GaussianSplat3DAsset.ColorFormat.Float32x4;
                    m_FormatSH = GaussianSplat3DAsset.SHFormat.Float32;
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }


        void CreateAsset()
        {
            m_ErrorMessage = null;
            try
            {
                var settings = new GaussianSplat3DImportSettings(m_FormatPos, m_FormatScale, m_FormatColor, m_FormatSH);
                var assets = GaussianSplat3DImporter.Import(m_InputFile, m_OutputFolder, settings, m_ImportCameras);
                Selection.objects = assets;
                Debug.Log($"Imported {assets.Length} Gaussian asset(s) from {m_InputFile}");
            }
            catch (Exception exception) { m_ErrorMessage = exception.Message; Debug.LogException(exception); }
            finally { EditorUtility.ClearProgressBar(); }
        }
    }
}
