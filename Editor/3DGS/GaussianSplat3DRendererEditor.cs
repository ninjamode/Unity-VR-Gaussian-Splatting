// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Gaussians.ThreeD;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEditor;
using UnityEditor.EditorTools;
using Gaussians.Core;
using UnityEngine;
using GaussianSplat3DRenderer = Gaussians.ThreeD.GaussianSplat3DRenderer;

namespace Gaussians.ThreeD.Editor
{
    [CustomEditor(typeof(GaussianSplat3DRenderer))]
    [CanEditMultipleObjects]
    public class GaussianSplat3DRendererEditor : UnityEditor.Editor
    {
        const string kPrefExportBake = "net.kleinbeck.gaussians.3d.ExportBakeTransform";

        SerializedProperty m_PropAsset;
        SerializedProperty m_PropRenderOrder;
        SerializedProperty m_PropSplatScale;
        SerializedProperty m_PropOpacityScale;
        SerializedProperty m_PropSHOrder;
        SerializedProperty m_PropSHOnly;
        SerializedProperty m_PropSortNthFrame;
        SerializedProperty m_PropRenderMode;
        SerializedProperty m_PropPointDisplaySize;
        SerializedProperty m_PropCutouts;
        SerializedProperty m_PropShaderSplats;
        SerializedProperty m_PropShaderComposite;
        SerializedProperty m_PropShaderDebugPoints;
        SerializedProperty m_PropShaderDebugBoxes;
        SerializedProperty m_PropCSSplatUtilities;

        bool m_ResourcesExpanded = false;
        bool m_AdvancedExpanded;
        bool m_CutoutsExpanded;
        bool m_EditingExpanded;
        int m_CameraIndex = 0;

        bool m_ExportBakeTransform;

        static int s_EditStatsUpdateCounter = 0;

        static HashSet<GaussianSplat3DRendererEditor> s_AllEditors = new();

        public static void BumpGUICounter()
        {
            ++s_EditStatsUpdateCounter;
        }

        public static void RepaintAll()
        {
            foreach (var e in s_AllEditors)
                e.Repaint();
        }

        public void OnEnable()
        {
            m_ExportBakeTransform = EditorPrefs.GetBool(kPrefExportBake, false);

            m_PropAsset = serializedObject.FindProperty("m_Asset");
            m_PropRenderOrder = serializedObject.FindProperty("m_RenderOrder");
            m_PropSplatScale = serializedObject.FindProperty("m_SplatScale");
            m_PropOpacityScale = serializedObject.FindProperty("m_OpacityScale");
            m_PropSHOrder = serializedObject.FindProperty("m_SHOrder");
            m_PropSHOnly = serializedObject.FindProperty("m_SHOnly");
            m_PropSortNthFrame = serializedObject.FindProperty("m_SortNthFrame");
            m_PropRenderMode = serializedObject.FindProperty("m_RenderMode");
            m_PropPointDisplaySize = serializedObject.FindProperty("m_PointDisplaySize");
            m_PropCutouts = serializedObject.FindProperty("m_Cutouts");
            m_PropShaderSplats = serializedObject.FindProperty("m_ShaderSplats");
            m_PropShaderComposite = serializedObject.FindProperty("m_ShaderComposite");
            m_PropShaderDebugPoints = serializedObject.FindProperty("m_ShaderDebugPoints");
            m_PropShaderDebugBoxes = serializedObject.FindProperty("m_ShaderDebugBoxes");
            m_PropCSSplatUtilities = serializedObject.FindProperty("m_CSSplatUtilities");

            s_AllEditors.Add(this);
        }

        public void OnDisable()
        {
            s_AllEditors.Remove(this);
        }

        public override void OnInspectorGUI()
        {
            var gs = target as GaussianSplat3DRenderer;
            if (!gs)
                return;

            serializedObject.Update();
            Gaussians.Core.Editor.GaussianPipelineWarnings.Draw(is2D: false);
            var activeGroups = targets.Cast<GaussianSplat3DRenderer>().Select(r => r.group && r.group.isActiveAndEnabled ? r.group : null).ToArray();
            bool groupControlled = activeGroups.Any(g => g);
            bool allGroupControlled = activeGroups.All(g => g);
            using var groupSettings = allGroupControlled ? new SerializedObject(activeGroups.Distinct().Cast<UnityEngine.Object>().ToArray()) : null;
            SerializedProperty SharedProperty(string name) => (groupSettings ?? serializedObject).FindProperty(name);
            void SharedField(string name, GUIContent label = null)
            {
                using (new EditorGUI.DisabledScope(groupControlled))
                    EditorGUILayout.PropertyField(SharedProperty(name), label ?? new GUIContent(ObjectNames.NicifyVariableName(name)));
            }


            GUILayout.Label("Data Asset", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(m_PropAsset);

            if (!gs.HasValidAsset)
            {
                var msg = gs.asset != null && gs.asset.formatVersion != GaussianSplat3DAsset.kCurrentVersion
                    ? "Gaussian Splat asset version is not compatible, please recreate the asset"
                    : "Gaussian Splat asset is not assigned or is empty";
                EditorGUILayout.HelpBox(msg, MessageType.Error);
            }

            EditorGUILayout.Space();
            GUILayout.Label("Render Options", EditorStyles.boldLabel);
            if (groupControlled)
                EditorGUILayout.HelpBox(allGroupControlled ? "Disabled fields are controlled by the group. Member settings are retained for independent rendering when the group is disabled." : "Some selected renderers use group overrides. Select a renderer individually to inspect its effective shared settings.", MessageType.Info);
            SharedField("m_WriteDepth", new GUIContent("Write Depth (URP Only)"));
            SharedField("m_AlphaCutoff", new GUIContent("Alpha Cutoff"));
            SharedField("m_OpacityAwareBounds", new GUIContent("Opacity Aware Bounds"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("m_MinimumSplatRadiusPixels"), new GUIContent("Minimum Splat Radius (px)", "Projected three-sigma radius before footprint filtering, in render-target pixels. Optimal uses a conservative projected bound. Zero disables size pruning. Higher values can remove detail; 0.7 is a provisional safeguard for tiny models."));
            var threshold = SharedProperty("m_CompactionThreshold");
            using (new EditorGUI.DisabledScope(groupControlled))
                EditorGUILayout.PropertyField(threshold, new GUIContent("Compaction Threshold", "Rejected splats required to enable compaction and deferred SH. -1: off; 0: always on; positive: automatic using recent visibility. Default 100,000 is experimental."));
            if (!groupControlled && !threshold.hasMultipleDifferentValues) threshold.intValue = Math.Max(-1, threshold.intValue);
            var precision = SharedProperty("m_SortPrecision");
            using (new EditorGUI.DisabledScope(groupControlled))
            {
                EditorGUI.showMixedValue = precision.hasMultipleDifferentValues;
                EditorGUI.BeginChangeCheck();
                bool lowPrecision = EditorGUILayout.Toggle(new GUIContent("16-bit Sorting (Experimental)", "Can reduce sorting cost in large environments, but may cause ordering artifacts and can reduce performance in some scenes."), precision.intValue == 16);
                if (EditorGUI.EndChangeCheck()) precision.intValue = lowPrecision ? 16 : 32;
                EditorGUI.showMixedValue = false;
            }
            SharedField("m_SortNthFrame");

            EditorGUILayout.Space();
            m_AdvancedExpanded = EditorGUILayout.Foldout(m_AdvancedExpanded, "Advanced Options", true, EditorStyles.foldoutHeader);
            if (m_AdvancedExpanded)
            {
                SharedField("m_RenderPath");
                SharedField("m_ProjectionMode", new GUIContent("Projection"));
                SharedField("m_ConvertGammaToLinear");
                var depth = serializedObject.FindProperty("m_MinimumSplatDistance");
                EditorGUILayout.PropertyField(depth, new GUIContent("Minimum View Depth", "Camera-space depth in world units; Optimal rejects the whole tangent quad if its support reaches this limit. Effective minimum is the larger of this value and the rendering camera near plane. Zero uses only the camera near plane."));
                if (!depth.hasMultipleDifferentValues)
                {
                    var camera = Camera.main;
                    if (camera && camera.isActiveAndEnabled && (camera.cullingMask & (1 << gs.gameObject.layer)) != 0 && camera.nearClipPlane > depth.floatValue)
                        EditorGUILayout.HelpBox($"Main camera '{camera.name}' has a near plane of {camera.nearClipPlane:g} and will clip farther than this setting. Each camera uses its own near plane.", MessageType.Info);
                }
                SharedField("m_RenderOrder");
                EditorGUILayout.PropertyField(m_PropSplatScale);
                EditorGUILayout.PropertyField(m_PropOpacityScale);
                EditorGUILayout.PropertyField(m_PropSHOrder);
                EditorGUILayout.PropertyField(m_PropSHOnly);
                if (groupControlled)
                {
                    using (new EditorGUI.DisabledScope(true))
                        EditorGUILayout.EnumPopup("Render Mode (Group)", GaussianSplat3DRenderer.RenderMode.Splats);
                }
                else EditorGUILayout.PropertyField(m_PropRenderMode);
                if (!groupControlled && (m_PropRenderMode.intValue is (int)GaussianSplat3DRenderer.RenderMode.DebugPoints or (int)GaussianSplat3DRenderer.RenderMode.DebugPointIndices))
                    EditorGUILayout.PropertyField(m_PropPointDisplaySize);
            }

            bool validAndEnabled = gs.isActiveAndEnabled && gs.HasValidAsset && gs.HasValidRenderSetup;
            bool canEdit = validAndEnabled && Array.TrueForAll(targets, item => ((GaussianSplat3DRenderer)item).CanEditSplats);

            EditorGUILayout.Space();
            m_CutoutsExpanded = EditorGUILayout.Foldout(m_CutoutsExpanded, "Cutouts", true, EditorStyles.foldoutHeader);
            if (m_CutoutsExpanded)
            {
                int additionalCutouts = AdditionalGroupCutoutCount(gs);
                if (additionalCutouts > 0 && targets.Length == 1)
                    EditorGUILayout.HelpBox($"Group '{gs.group.name}' supplies {additionalCutouts} additional active cutout(s).", MessageType.Info);
                EditorGUILayout.PropertyField(m_PropCutouts, true);
                if (validAndEnabled && targets.Length == 1)
                    CutoutGUI(gs);
            }

            EditorGUILayout.Space();
            m_EditingExpanded = EditorGUILayout.Foldout(m_EditingExpanded, "Editing", true, EditorStyles.foldoutHeader);
            if (m_EditingExpanded)
            {
                if (!Array.TrueForAll(targets, item => ((GaussianSplat3DRenderer)item).CanEditSplats))
                    EditorGUILayout.HelpBox("Canonical models have stable Gaussian IDs. Per-Gaussian editing and merging are unavailable; transforms and cutouts remain supported.", MessageType.Info);
                if (canEdit && targets.Length == 1)
                {
                    EditCameras(gs);
                    EditGUI(gs);
                }
                if (canEdit && targets.Length > 1)
                    MultiEditGUI();
            }

            EditorGUILayout.Space();
            m_ResourcesExpanded = EditorGUILayout.Foldout(m_ResourcesExpanded, "Resources", true, EditorStyles.foldoutHeader);
            if (m_ResourcesExpanded)
            {
                SharedField("m_ShaderSplats");
                SharedField("m_ShaderComposite");
                EditorGUILayout.PropertyField(m_PropShaderDebugPoints);
                EditorGUILayout.PropertyField(m_PropShaderDebugBoxes);
                SharedField("m_CSSplatUtilities");
            }
            if (gs.isActiveAndEnabled && gs.HasValidAsset && !gs.HasValidRenderSetup)
                EditorGUILayout.HelpBox("Shader resources are not set up", MessageType.Error);

            DrawSeparator();
            DrawGroupInformation(gs);
            if (targets.Length == 1)
                DrawSplatInformation(gs, canEdit);
            else
            {
                CountTargetSplats(out var totalSplats, out var totalObjects);
                EditorGUILayout.LabelField("Total Objects", $"{totalObjects}");
                EditorGUILayout.LabelField("Total Splats", $"{totalSplats:N0}");
            }

            serializedObject.ApplyModifiedProperties();
        }

        void EditCameras(GaussianSplat3DRenderer gs)
        {
            var asset = gs.asset;
            var cameras = asset.cameras;
            if (cameras != null && cameras.Length != 0)
            {
                EditorGUILayout.Space();
                GUILayout.Label("Cameras", EditorStyles.boldLabel);
                var camIndex = EditorGUILayout.IntSlider("Camera", m_CameraIndex, 0, cameras.Length - 1);
                camIndex = math.clamp(camIndex, 0, cameras.Length - 1);
                if (camIndex != m_CameraIndex)
                {
                    m_CameraIndex = camIndex;
                    gs.ActivateCamera(camIndex);
                }
            }
        }

        void MultiEditGUI()
        {
            DrawSeparator();
            CountTargetSplats(out var totalSplats, out var totalObjects);
            if (totalSplats > GaussianSplat3DAsset.kMaxSplats)
            {
                EditorGUILayout.HelpBox($"Can't merge, too many splats (max. supported {GaussianSplat3DAsset.kMaxSplats:N0})", MessageType.Warning);
                return;
            }

            var targetGs = (GaussianSplat3DRenderer) target;
            if (!targetGs || !targetGs.HasValidAsset || !targetGs.isActiveAndEnabled)
            {
                EditorGUILayout.HelpBox($"Can't merge into {target.name} (no asset or disable)", MessageType.Warning);
                return;
            }

            if (targetGs.asset.chunkData != null)
            {
                EditorGUILayout.HelpBox($"Can't merge into {target.name} (needs to use Very High quality preset)", MessageType.Warning);
                return;
            }
            if (GUILayout.Button($"Merge into {target.name}"))
            {
                MergeSplatObjects();
            }
        }

        void CountTargetSplats(out int totalSplats, out int totalObjects)
        {
            totalObjects = 0;
            totalSplats = 0;
            foreach (var obj in targets)
            {
                var gs = obj as GaussianSplat3DRenderer;
                if (!gs || !gs.HasValidAsset || !gs.isActiveAndEnabled)
                    continue;
                ++totalObjects;
                totalSplats += gs.splatCount;
            }
        }

        void MergeSplatObjects()
        {
            CountTargetSplats(out var totalSplats, out _);
            if (totalSplats > GaussianSplat3DAsset.kMaxSplats)
                return;
            var targetGs = (GaussianSplat3DRenderer) target;

            int copyDstOffset = targetGs.splatCount;
            targetGs.EditSetSplatCount(totalSplats);
            foreach (var obj in targets)
            {
                var gs = obj as GaussianSplat3DRenderer;
                if (!gs || !gs.HasValidAsset || !gs.isActiveAndEnabled)
                    continue;
                if (gs == targetGs)
                    continue;
                gs.EditCopySplatsInto(targetGs, 0, copyDstOffset, gs.splatCount);
                copyDstOffset += gs.splatCount;
                gs.gameObject.SetActive(false);
            }
            Debug.Assert(copyDstOffset == totalSplats, $"Merge count mismatch, {copyDstOffset} vs {totalSplats}");
            Selection.activeObject = targetGs;
        }

        void EditGUI(GaussianSplat3DRenderer gs)
        {
            bool wasToolActive = ToolManager.activeContextType == typeof(GaussianSplat3DToolContext);
            GUILayout.BeginHorizontal();
            bool isToolActive = GUILayout.Toggle(wasToolActive, "Edit", EditorStyles.miniButton);
            using (new EditorGUI.DisabledScope(!gs.editModified))
            {
                if (GUILayout.Button("Reset", GUILayout.ExpandWidth(false)))
                {
                    if (EditorUtility.DisplayDialog("Reset Splat Modifications?",
                            $"This will reset edits of {gs.name} to match the {gs.asset.name} asset. Continue?",
                            "Yes, reset", "Cancel"))
                    {
                        gs.enabled = false;
                        gs.enabled = true;
                    }
                }
            }

            GUILayout.EndHorizontal();
            if (!wasToolActive && isToolActive)
            {
                ToolManager.SetActiveContext<GaussianSplat3DToolContext>();
                if (Tools.current == Tool.View)
                    Tools.current = Tool.Move;
            }

            if (wasToolActive && !isToolActive)
            {
                ToolManager.SetActiveContext<GameObjectToolContext>();
            }

            if (isToolActive && gs.asset.chunkData != null)
            {
                EditorGUILayout.HelpBox("Splat move/rotate/scale tools need Very High splat quality preset", MessageType.Warning);
            }

            var asset = gs.asset;
            EditorGUILayout.Space();
            EditorGUI.BeginChangeCheck();
            m_ExportBakeTransform = EditorGUILayout.Toggle("Export in world space", m_ExportBakeTransform);
            if (EditorGUI.EndChangeCheck())
            {
                EditorPrefs.SetBool(kPrefExportBake, m_ExportBakeTransform);
            }

            if (GUILayout.Button("Export PLY"))
                ExportPlyFile(gs, m_ExportBakeTransform);
            if (asset.posFormat > GaussianSplat3DAsset.VectorFormat.Norm16 ||
                asset.scaleFormat > GaussianSplat3DAsset.VectorFormat.Norm16 ||
                asset.colorFormat > GaussianSplat3DAsset.ColorFormat.Float16x4 ||
                asset.shFormat > GaussianSplat3DAsset.SHFormat.Float16)
            {
                EditorGUILayout.HelpBox(
                    "It is recommended to use High or VeryHigh quality preset for editing splats, lower levels are lossy",
                    MessageType.Warning);
            }

        }

        void CutoutGUI(GaussianSplat3DRenderer gs)
        {
            EditorGUILayout.Space();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Add Cutout"))
            {
                GaussianCutout cutout = ObjectFactory.CreateGameObject("GSCutout", typeof(GaussianCutout)).GetComponent<GaussianCutout>();
                Transform cutoutTr = cutout.transform;
                cutoutTr.SetParent(gs.transform, false);
                cutoutTr.localScale = (gs.asset.boundsMax - gs.asset.boundsMin) * 0.25f;
                gs.m_Cutouts ??= Array.Empty<GaussianCutout>();
                ArrayUtility.Add(ref gs.m_Cutouts, cutout);
                gs.UpdateEditCountsAndBounds();
                EditorUtility.SetDirty(gs);
                Selection.activeGameObject = cutout.gameObject;
            }
            if (GUILayout.Button("Use All Cutouts"))
            {
                gs.m_Cutouts = FindObjectsByType<GaussianCutout>(FindObjectsSortMode.InstanceID);
                gs.UpdateEditCountsAndBounds();
                EditorUtility.SetDirty(gs);
            }

            if (GUILayout.Button("No Cutouts"))
            {
                gs.m_Cutouts = Array.Empty<GaussianCutout>();
                gs.UpdateEditCountsAndBounds();
                EditorUtility.SetDirty(gs);
            }
            GUILayout.EndHorizontal();
        }

        static int AdditionalGroupCutoutCount(GaussianSplat3DRenderer gs)
        {
            var group = gs.group;
            if (!group || !group.isActiveAndEnabled || group.m_Cutouts == null) return 0;
            var additional = new HashSet<GaussianCutout>();
            foreach (var cutout in group.m_Cutouts)
                if (cutout && cutout.isActiveAndEnabled &&
                    (gs.m_Cutouts == null || Array.IndexOf(gs.m_Cutouts, cutout) < 0))
                    additional.Add(cutout);
            return additional.Count;
        }

        void DrawGroupInformation(GaussianSplat3DRenderer gs)
        {
            if (!Array.TrueForAll(targets, item => ((GaussianSplat3DRenderer)item).group == gs.group))
            {
                EditorGUILayout.HelpBox("Selected renderers have different group memberships.", MessageType.Info);
                return;
            }
            if (!gs.group)
            {
                EditorGUILayout.HelpBox(targets.Length == 1 ? "Renderer is not part of a group." : "Selected renderers are not part of a group.", MessageType.Info);
                return;
            }
            string message = gs.group.MemberRenderingStatus(gs, out bool warning);
            if (targets.Length > 1) message = "Selected renderers belong to this group. Inspect individual renderers for their rendering status.";
            EditorGUILayout.HelpBox(message + " The group reference is assigned automatically.", warning ? MessageType.Warning : MessageType.Info);
            using (new EditorGUI.DisabledScope(true))
                EditorGUILayout.ObjectField("Gaussians Group", gs.group, typeof(GaussiansGroup), true);
        }

        void DrawSplatInformation(GaussianSplat3DRenderer gs, bool canEdit)
        {
            ++s_EditStatsUpdateCounter;
            bool hasCutouts = (gs.m_Cutouts != null && gs.m_Cutouts.Length != 0) || AdditionalGroupCutoutCount(gs) > 0;
            bool displayEditStats = canEdit && (ToolManager.activeContextType == typeof(GaussianSplat3DToolContext) || gs.editModified || hasCutouts);
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Splats", $"{gs.splatCount:N0}");
            if (displayEditStats)
            {
                EditorGUILayout.LabelField("Cut", $"{gs.editCutSplats:N0}");
                EditorGUILayout.LabelField("Deleted", $"{gs.editDeletedSplats:N0}");
                EditorGUILayout.LabelField("Selected", $"{gs.editSelectedSplats:N0}");
                if (hasCutouts)
                {
                    if (s_EditStatsUpdateCounter > 10)
                    {
                        gs.UpdateEditCountsAndBounds();
                        s_EditStatsUpdateCounter = 0;
                    }
                }
            }
        }

        static void DrawSeparator()
        {
            EditorGUILayout.Space(12f, true);
            GUILayout.Box(GUIContent.none, "sv_iconselector_sep", GUILayout.Height(2), GUILayout.ExpandWidth(true));
            EditorGUILayout.Space();
        }

        bool HasFrameBounds()
        {
            return true;
        }

        Bounds OnGetFrameBounds()
        {
            var gs = target as GaussianSplat3DRenderer;
            if (!gs || !gs.HasValidRenderSetup)
                return new Bounds(Vector3.zero, Vector3.one);
            Bounds bounds = default;
            bounds.SetMinMax(gs.asset.boundsMin, gs.asset.boundsMax);
            if (gs.editSelectedSplats > 0)
            {
                bounds = gs.editSelectedBounds;
            }
            bounds.extents *= 0.7f;
            return TransformBounds(gs.transform, bounds);
        }

        public static Bounds TransformBounds(Transform tr, Bounds bounds )
        {
            var center = tr.TransformPoint(bounds.center);

            var ext = bounds.extents;
            var axisX = tr.TransformVector(ext.x, 0, 0);
            var axisY = tr.TransformVector(0, ext.y, 0);
            var axisZ = tr.TransformVector(0, 0, ext.z);

            // sum their absolute value to get the world extents
            ext.x = Mathf.Abs(axisX.x) + Mathf.Abs(axisY.x) + Mathf.Abs(axisZ.x);
            ext.y = Mathf.Abs(axisX.y) + Mathf.Abs(axisY.y) + Mathf.Abs(axisZ.y);
            ext.z = Mathf.Abs(axisX.z) + Mathf.Abs(axisY.z) + Mathf.Abs(axisZ.z);

            return new Bounds { center = center, extents = ext };
        }

        static unsafe void ExportPlyFile(GaussianSplat3DRenderer gs, bool bakeTransform)
        {
            var path = EditorUtility.SaveFilePanel(
                "Export Gaussian Splat PLY file", "", $"{gs.asset.name}-edit.ply", "ply");
            if (string.IsNullOrWhiteSpace(path))
                return;

            int kSplatSize = UnsafeUtility.SizeOf<Utils.InputSplatData>();
            using var gpuData = new GraphicsBuffer(GraphicsBuffer.Target.Structured, gs.splatCount, kSplatSize);

            if (!gs.EditExportData(gpuData, bakeTransform))
                return;

            Utils.InputSplatData[] data = new Utils.InputSplatData[gpuData.count];
            gpuData.GetData(data);

            var gpuDeleted = gs.GpuEditDeleted;
            uint[] deleted = new uint[gpuDeleted.count];
            gpuDeleted.GetData(deleted);

            // count non-deleted splats
            int aliveCount = 0;
            for (int i = 0; i < data.Length; ++i)
            {
                int wordIdx = i >> 5;
                int bitIdx = i & 31;
                bool isDeleted = (deleted[wordIdx] & (1u << bitIdx)) != 0;
                bool isCutout = data[i].nor.sqrMagnitude > 0;
                if (!isDeleted && !isCutout)
                    ++aliveCount;
            }

            using FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write);
            // note: this is a long string! but we don't use multiline literal because we want guaranteed LF line ending
            var header = $"ply\nformat binary_little_endian 1.0\nelement vertex {aliveCount}\nproperty float x\nproperty float y\nproperty float z\nproperty float nx\nproperty float ny\nproperty float nz\nproperty float f_dc_0\nproperty float f_dc_1\nproperty float f_dc_2\nproperty float f_rest_0\nproperty float f_rest_1\nproperty float f_rest_2\nproperty float f_rest_3\nproperty float f_rest_4\nproperty float f_rest_5\nproperty float f_rest_6\nproperty float f_rest_7\nproperty float f_rest_8\nproperty float f_rest_9\nproperty float f_rest_10\nproperty float f_rest_11\nproperty float f_rest_12\nproperty float f_rest_13\nproperty float f_rest_14\nproperty float f_rest_15\nproperty float f_rest_16\nproperty float f_rest_17\nproperty float f_rest_18\nproperty float f_rest_19\nproperty float f_rest_20\nproperty float f_rest_21\nproperty float f_rest_22\nproperty float f_rest_23\nproperty float f_rest_24\nproperty float f_rest_25\nproperty float f_rest_26\nproperty float f_rest_27\nproperty float f_rest_28\nproperty float f_rest_29\nproperty float f_rest_30\nproperty float f_rest_31\nproperty float f_rest_32\nproperty float f_rest_33\nproperty float f_rest_34\nproperty float f_rest_35\nproperty float f_rest_36\nproperty float f_rest_37\nproperty float f_rest_38\nproperty float f_rest_39\nproperty float f_rest_40\nproperty float f_rest_41\nproperty float f_rest_42\nproperty float f_rest_43\nproperty float f_rest_44\nproperty float opacity\nproperty float scale_0\nproperty float scale_1\nproperty float scale_2\nproperty float rot_0\nproperty float rot_1\nproperty float rot_2\nproperty float rot_3\nend_header\n";
            fs.Write(Encoding.UTF8.GetBytes(header));
            for (int i = 0; i < data.Length; ++i)
            {
                int wordIdx = i >> 5;
                int bitIdx = i & 31;
                bool isDeleted = (deleted[wordIdx] & (1u << bitIdx)) != 0;
                bool isCutout = data[i].nor.sqrMagnitude > 0;
                if (!isDeleted && !isCutout)
                {
                    var splat = data[i];
                    byte* ptr = (byte*)&splat;
                    fs.Write(new ReadOnlySpan<byte>(ptr, kSplatSize));
                }
            }

            Debug.Log($"Exported PLY {path} with {aliveCount:N0} splats");
        }
    }
}
