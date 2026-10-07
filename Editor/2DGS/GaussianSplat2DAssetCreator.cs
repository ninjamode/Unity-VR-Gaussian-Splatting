// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.Runtime.CompilerServices;
using Gaussians.TwoD.Editor.Utils;
using Gaussians.TwoD;
using Gaussians.Core.Editor.Utils;
using UnityEditor;
using UnityEngine;

[assembly: InternalsVisibleTo("Gaussians.TwoD.Editor.Tests")]

namespace Gaussians.TwoD.Editor
{
    [UnityEngine.Scripting.APIUpdating.MovedFrom(true, "TwoDGS.Editor", "Gaussians.TwoD.Editor", "GaussianSplat2DAssetCreator")]
    public class GaussianSplat2DAssetCreator : EditorWindow
    {
        const string kPrefQuality = "net.kleinbeck.gaussians.2d.CreatorQuality";
        const string kPrefOutputFolder = "net.kleinbeck.gaussians.2d.CreatorOutputFolder";

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

        [SerializeField] string m_OutputFolder = "Assets/Gaussians/2D";
        [SerializeField] DataQuality m_Quality = DataQuality.Medium;
        [SerializeField] GaussianSplat2DAsset.VectorFormat m_FormatPos;
        [SerializeField] GaussianSplat2DAsset.ScaleFormat m_FormatScale;
        [SerializeField] GaussianSplat2DAsset.ColorFormat m_FormatColor;
        [SerializeField] GaussianSplat2DAsset.SHFormat m_FormatSH;

        string m_ErrorMessage;
        string m_PrevFilePath;
        int m_PrevVertexCount;
        long m_PrevFileSize;

        bool isUsingChunks =>
            m_FormatPos != GaussianSplat2DAsset.VectorFormat.Float32 ||
            m_FormatScale != GaussianSplat2DAsset.ScaleFormat.Float32x2 ||
            m_FormatColor != GaussianSplat2DAsset.ColorFormat.Float32x4 ||
            m_FormatSH != GaussianSplat2DAsset.SHFormat.Float32;

        [MenuItem("Tools/Gaussians/2D/Create Splat Asset")]
        public static void Init()
        {
            var window = GetWindowWithRect<GaussianSplat2DAssetCreator>(new Rect(50, 50, 360, 340), false, "2D Gaussian Splat Creator", true);
            window.minSize = new Vector2(320, 320);
            window.maxSize = new Vector2(1500, 1500);
            window.Show();
        }

        void Awake()
        {
            m_Quality = (DataQuality)EditorPrefs.GetInt(kPrefQuality, (int)DataQuality.Medium);
            m_OutputFolder = EditorPrefs.GetString(kPrefOutputFolder, "Assets/Gaussians/2D");
        }

        void OnEnable()
        {
            if (!Enum.IsDefined(typeof(GaussianSplat2DAsset.ScaleFormat), m_FormatScale))
                m_FormatScale = GaussianSplat2DAsset.ScaleFormat.Norm8x2;
            ApplyQualityLevel();
        }

        void OnGUI()
        {
            EditorGUILayout.Space();
            GUILayout.Label("Input data", EditorStyles.boldLabel);
            var rect = EditorGUILayout.GetControlRect(true);
            m_InputFile = m_FilePicker.PathFieldGUI(rect, new GUIContent("Input 2DGS PLY File"), m_InputFile, "ply", "PointCloudFile");
            m_ImportCameras = EditorGUILayout.Toggle("Import Cameras", m_ImportCameras);

            if (m_InputFile != m_PrevFilePath)
            {
                m_PrevVertexCount = 0;
                m_ErrorMessage = null;
                try
                {
                    if (!string.IsNullOrWhiteSpace(m_InputFile)) m_PrevVertexCount = GaussianSplat2DFileReader.ReadFileHeader(m_InputFile);
                }
                catch (Exception ex)
                {
                    m_ErrorMessage = ex.Message;
                }

                m_PrevFileSize = File.Exists(m_InputFile) ? new FileInfo(m_InputFile).Length : 0;
                m_PrevFilePath = m_InputFile;
            }

            if (m_PrevVertexCount > 0)
                EditorGUILayout.LabelField("File Size", $"{EditorUtility.FormatBytes(m_PrevFileSize)} - {m_PrevVertexCount:N0} splats");
            else
                GUILayout.Space(EditorGUIUtility.singleLineHeight);

            EditorGUILayout.Space();
            GUILayout.Label("Output", EditorStyles.boldLabel);
            rect = EditorGUILayout.GetControlRect(true);
            string newOutputFolder = m_FilePicker.PathFieldGUI(rect, new GUIContent("Output Folder"), m_OutputFolder, null, "Gaussians.2D.AssetOutputFolder");
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
            if (m_PrevVertexCount > 0)
            {
                sizePos = GaussianSplat2DAsset.CalcPosDataSize(m_PrevVertexCount, m_FormatPos);
                sizeOther = GaussianSplat2DAsset.CalcOtherDataSize(m_PrevVertexCount, m_FormatScale);
                sizeCol = GaussianSplat2DAsset.CalcColorDataSize(m_PrevVertexCount, m_FormatColor);
                sizeSHs = GaussianSplat2DAsset.CalcSHDataSize(m_PrevVertexCount, m_FormatSH);
                long sizeChunk = isUsingChunks ? GaussianSplat2DAsset.CalcChunkDataSize(m_PrevVertexCount) : 0;
                totalSize = sizePos + sizeOther + sizeCol + sizeSHs + sizeChunk;
            }

            const float kSizeColWidth = 70;
            EditorGUI.BeginDisabledGroup(m_Quality != DataQuality.Custom);
            EditorGUI.indentLevel++;
            GUILayout.BeginHorizontal();
            m_FormatPos = (GaussianSplat2DAsset.VectorFormat)EditorGUILayout.EnumPopup("Position", m_FormatPos);
            GUILayout.Label(sizePos > 0 ? EditorUtility.FormatBytes(sizePos) : string.Empty, GUILayout.Width(kSizeColWidth));
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            m_FormatScale = (GaussianSplat2DAsset.ScaleFormat)EditorGUILayout.EnumPopup("Scale", m_FormatScale);
            GUILayout.Label(sizeOther > 0 ? EditorUtility.FormatBytes(sizeOther) : string.Empty, GUILayout.Width(kSizeColWidth));
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            m_FormatColor = (GaussianSplat2DAsset.ColorFormat)EditorGUILayout.EnumPopup("Color", m_FormatColor);
            GUILayout.Label(sizeCol > 0 ? EditorUtility.FormatBytes(sizeCol) : string.Empty, GUILayout.Width(kSizeColWidth));
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            m_FormatSH = (GaussianSplat2DAsset.SHFormat) EditorGUILayout.EnumPopup("SH", m_FormatSH);
            GUIContent shGC = new GUIContent();
            shGC.text = sizeSHs > 0 ? EditorUtility.FormatBytes(sizeSHs) : string.Empty;
            if (m_FormatSH >= GaussianSplat2DAsset.SHFormat.Cluster64k)
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
            if (GUILayout.Button("Create Asset"))
            {
                CreateAsset();
            }
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
                case DataQuality.VeryLow:
                    m_FormatPos = GaussianSplat2DAsset.VectorFormat.Norm11;
                    m_FormatScale = GaussianSplat2DAsset.ScaleFormat.Norm8x2;
                    m_FormatColor = GaussianSplat2DAsset.ColorFormat.BC7;
                    m_FormatSH = GaussianSplat2DAsset.SHFormat.Cluster4k;
                    break;
                case DataQuality.Low:
                    m_FormatPos = GaussianSplat2DAsset.VectorFormat.Norm11;
                    m_FormatScale = GaussianSplat2DAsset.ScaleFormat.Norm8x2;
                    m_FormatColor = GaussianSplat2DAsset.ColorFormat.Norm8x4;
                    m_FormatSH = GaussianSplat2DAsset.SHFormat.Cluster16k;
                    break;
                case DataQuality.Medium:
                    m_FormatPos = GaussianSplat2DAsset.VectorFormat.Norm11;
                    m_FormatScale = GaussianSplat2DAsset.ScaleFormat.Norm8x2;
                    m_FormatColor = GaussianSplat2DAsset.ColorFormat.Norm8x4;
                    m_FormatSH = GaussianSplat2DAsset.SHFormat.Norm6;
                    break;
                case DataQuality.High:
                    m_FormatPos = GaussianSplat2DAsset.VectorFormat.Norm16;
                    m_FormatScale = GaussianSplat2DAsset.ScaleFormat.Norm16x2;
                    m_FormatColor = GaussianSplat2DAsset.ColorFormat.Float16x4;
                    m_FormatSH = GaussianSplat2DAsset.SHFormat.Norm11;
                    break;
                case DataQuality.VeryHigh:
                    m_FormatPos = GaussianSplat2DAsset.VectorFormat.Float32;
                    m_FormatScale = GaussianSplat2DAsset.ScaleFormat.Float32x2;
                    m_FormatColor = GaussianSplat2DAsset.ColorFormat.Float32x4;
                    m_FormatSH = GaussianSplat2DAsset.SHFormat.Float32;
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
                var settings = new GaussianSplat2DImportSettings(m_FormatPos, m_FormatScale, m_FormatColor, m_FormatSH);
                Selection.activeObject = GaussianSplat2DImporter.Import(m_InputFile, m_OutputFolder, settings, m_ImportCameras);
            }
            catch (Exception exception) { m_ErrorMessage = exception.Message; Debug.LogException(exception); }
            finally { EditorUtility.ClearProgressBar(); }
        }
    }
}
