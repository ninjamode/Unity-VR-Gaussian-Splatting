// SPDX-License-Identifier: MIT

using Gaussians.ThreeD;
using UnityEditor;
using UnityEditor.EditorTools;
using UnityEngine;

namespace Gaussians.ThreeD.Editor
{
    /* not working correctly yet
    [EditorTool("3D Gaussian Rotate Tool", typeof(GaussianSplat3DRenderer), typeof(GaussianSplat3DToolContext))]
    class GaussianSplat3DRotateTool : GaussianSplat3DTool
    {
        Quaternion m_CurrentRotation = Quaternion.identity;
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
                gs.EditStoreOtherMouseDown();
                m_FrozenSelCenterLocal = selCenterLocal;
                m_FreezePivot = true;
            }
            if (evt.type == EventType.MouseUp)
            {
                m_CurrentRotation = Quaternion.identity;
                m_FreezePivot = false;
            }

            if (m_FreezePivot)
                selCenterLocal = m_FrozenSelCenterLocal;

            EditorGUI.BeginChangeCheck();
            var selCenterWorld = tr.TransformPoint(selCenterLocal);
            var newRotation = Handles.DoRotationHandle(m_CurrentRotation, selCenterWorld);
            if (EditorGUI.EndChangeCheck())
            {
                Matrix4x4 localToWorld = gs.transform.localToWorldMatrix;
                Matrix4x4 worldToLocal = gs.transform.worldToLocalMatrix;
                var wasModified = gs.editModified;
                var rotToApply = newRotation;
                gs.EditRotateSelection(selCenterLocal, localToWorld, worldToLocal, rotToApply);
                m_CurrentRotation = newRotation;
                if (!wasModified)
                    GaussianSplat3DRendererEditor.RepaintAll();

                if(GUIUtility.hotControl == 0)
                {
                    m_CurrentRotation = Tools.handleRotation;
                }
            }
        }
    }
    */
}