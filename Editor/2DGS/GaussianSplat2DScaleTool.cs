// SPDX-License-Identifier: MIT

using Gaussians.TwoD;
using UnityEditor;
using UnityEditor.EditorTools;
using UnityEngine;

namespace Gaussians.TwoD.Editor
{
    /* // not working correctly yet when the GS itself has scale
    [EditorTool("2D Gaussian Scale Tool", typeof(GaussianSplat2DRenderer), typeof(GaussianSplat2DToolContext))]
    class GaussianSplat2DScaleTool : GaussianSplat2DTool
    {
        Vector3 m_CurrentScale = Vector3.one;
        Vector3 m_FrozenSelCenterLocal = Vector3.zero;
        bool m_FreezePivot = false;

        public override void OnActivated()
        {
            m_FreezePivot = false;
        }

        public override void OnToolGUI(EditorWindow window)
        {
            var gs = GetRenderer();
            if (!gs || !CanBeEdited() || !HasSelection())
                return;
            var tr = gs.transform;
            var evt = Event.current;

            var selCenterLocal = GetSelectionCenterLocal();
            if (evt.type == EventType.MouseDown)
            {
                gs.EditStorePosMouseDown();
                m_FrozenSelCenterLocal = selCenterLocal;
                m_FreezePivot = true;
            }
            if (evt.type == EventType.MouseUp)
            {
                m_CurrentScale = Vector3.one;
                m_FreezePivot = false;
            }

            if (m_FreezePivot)
                selCenterLocal = m_FrozenSelCenterLocal;

            EditorGUI.BeginChangeCheck();
            var selCenterWorld = tr.TransformPoint(selCenterLocal);
            m_CurrentScale = Handles.DoScaleHandle(m_CurrentScale, selCenterWorld, Tools.handleRotation, HandleUtility.GetHandleSize(selCenterWorld));
            if (EditorGUI.EndChangeCheck())
            {
                Matrix4x4 localToWorld = Matrix4x4.identity;
                Matrix4x4 worldToLocal = Matrix4x4.identity;
                if (Tools.pivotRotation == PivotRotation.Global)
                {
                    localToWorld = gs.transform.localToWorldMatrix;
                    worldToLocal = gs.transform.worldToLocalMatrix;
                }
                var wasModified = gs.editModified;
                gs.EditScaleSelection(selCenterLocal, localToWorld, worldToLocal, m_CurrentScale);
                if (!wasModified)
                    GaussianSplat2DRendererEditor.RepaintAll();
                evt.Use();
            }
        }
    }
    */
}
