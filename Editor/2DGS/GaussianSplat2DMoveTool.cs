// SPDX-License-Identifier: MIT

using Gaussians.TwoD;
using UnityEditor;
using UnityEditor.EditorTools;
using UnityEngine;

namespace Gaussians.TwoD.Editor
{
    [EditorTool("2D Gaussian Move Tool", typeof(GaussianSplat2DRenderer), typeof(GaussianSplat2DToolContext))]
    class GaussianSplat2DMoveTool : GaussianSplat2DTool
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
                    GaussianSplat2DRendererEditor.RepaintAll();
                Event.current.Use();
            }
        }
    }
}
