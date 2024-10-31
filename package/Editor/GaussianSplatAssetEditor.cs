// SPDX-License-Identifier: MIT

using GaussianSplatting.Runtime;
using Unity.Collections.LowLevel.Unsafe;
using UnityEditor;
using UnityEngine;

namespace GaussianSplatting.Editor
{
    [CustomEditor(typeof(GaussianSplatAsset))]
    [CanEditMultipleObjects]
    public class GaussianSplatAssetEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            var gs = target as GaussianSplatAsset;
            if (!gs)
                return;

            using var _ = new EditorGUI.DisabledScope(true);

            if (targets.Length == 1)
                SingleAssetGUI(gs);
            else
            {
                int totalCount = 0;
                foreach (var tgt in targets)
                {
                    var gss = tgt as GaussianSplatAsset;
                    if (gss)
                    {
                        totalCount += gss.splatCount;
                    }
                }
                EditorGUILayout.TextField("Total Splats", $"{totalCount:N0}");
            }
        }

        static void SingleAssetGUI(GaussianSplatAsset gs)
        {
            var splatCount = gs.splatCount;
            EditorGUILayout.TextField("Splats", $"{splatCount:N0}");
            var prevBackColor = GUI.backgroundColor;
            if (gs.formatVersion != GaussianSplatAsset.kCurrentVersion)
                GUI.backgroundColor *= Color.red;
            EditorGUILayout.IntField("Version", gs.formatVersion);
            GUI.backgroundColor = prevBackColor;

            long sizePos = gs.posDataSize;
            long sizeOther = gs.otherDataSize;
            long sizeCol = gs.colorDataSize;
            long sizeSH = gs.shDataSize;
            long sizeChunk = gs.chunkDataSize;

            EditorGUILayout.TextField("Memory", EditorUtility.FormatBytes(sizePos + sizeOther + sizeSH + sizeCol + sizeChunk));
            EditorGUI.indentLevel++;
            EditorGUILayout.TextField("Positions", $"{EditorUtility.FormatBytes(sizePos)}  ({gs.posFormat})");
            EditorGUILayout.TextField("Other", $"{EditorUtility.FormatBytes(sizeOther)}  ({gs.scaleFormat})");
            EditorGUILayout.TextField("Base color", $"{EditorUtility.FormatBytes(sizeCol)}  ({gs.colorFormat})");
            EditorGUILayout.TextField("SHs", $"{EditorUtility.FormatBytes(sizeSH)}  ({gs.shFormat})");
            EditorGUILayout.TextField("Chunks",
                $"{EditorUtility.FormatBytes(sizeChunk)}  ({UnsafeUtility.SizeOf<GaussianSplatAsset.ChunkInfo>()} B/chunk)");
            EditorGUI.indentLevel--;

            EditorGUILayout.Vector3Field("Bounds Min", gs.boundsMin);
            EditorGUILayout.Vector3Field("Bounds Max", gs.boundsMax);

            EditorGUILayout.Space();
            GUILayout.Label("Layers", EditorStyles.boldLabel);
            foreach (var kv in gs.layerInfo)
            {
                EditorGUILayout.BeginHorizontal();
                GUILayout.Label("Layer");
                EditorGUILayout.FloatField(kv.Key);
                GUILayout.Label("Splat Count");
                EditorGUILayout.FloatField(kv.Value);
                EditorGUILayout.EndHorizontal();
            }
            
            EditorGUILayout.Space();
            EditorGUILayout.TextField("Data Hash", gs.dataHash.ToString());
        }
    }
}
