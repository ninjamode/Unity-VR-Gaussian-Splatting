// SPDX-License-Identifier: MIT

using Gaussians.ThreeD;
using UnityEditor;
using UnityEditor.EditorTools;
using UnityEngine;

namespace Gaussians.ThreeD.Editor
{
    [EditorTool("3D Gaussian Move Tool", typeof(GaussianSplat3DRenderer), typeof(GaussianSplat3DToolContext))]
    class GaussianSplat3DMoveTool : GaussianSplat3DTool
    {
        public override void OnToolGUI(EditorWindow window)
        {
            var gs = GetRenderer();
            if (!gs || !CanBeEdited() || !HasSelection())
                return;
            var tr = gs.transform;

            EditorGUI.BeginChangeCheck();
            var selCenterLocal = GetSelectionCenterLocal();
            var selCenterWorld = tr.TransformPoint(selCenterLocal);
            var newPosWorld = Handles.DoPositionHandle(selCenterWorld, Tools.handleRotation);
            if (EditorGUI.EndChangeCheck())
            {
                var newPosLocal = tr.InverseTransformPoint(newPosWorld);
                var wasModified = gs.editModified;
                gs.EditTranslateSelection(newPosLocal - selCenterLocal);
                if (!wasModified)
                    GaussianSplat3DRendererEditor.RepaintAll();
                Event.current.Use();
            }
        }
    }
}
