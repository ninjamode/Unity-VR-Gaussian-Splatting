// SPDX-License-Identifier: MIT
using System.Collections.Generic;
using Gaussians.Core;
using UnityEngine;
using UnityEngine.Rendering;

namespace Gaussians.ThreeD
{
    public partial class GaussianSplat3DRenderer
    {
        [SerializeField, HideInInspector] internal GaussiansGroup m_Group;
        public GaussiansGroup group => m_Group && ContainsGroupMember() ? m_Group : null;
        bool ContainsGroupMember()
        {
            foreach (var r in m_Group.Members) if (r == this) return true;
            return false;
        }
        internal GaussiansGroup ActiveGroup => group && group.isActiveAndEnabled ? group : null;
        internal float EffectiveAlphaCutoff => ActiveGroup ? Mathf.Clamp01(ActiveGroup.m_AlphaCutoff) : Mathf.Clamp01(m_AlphaCutoff);
        internal bool EffectiveOpacityAwareBounds => ActiveGroup ? ActiveGroup.m_OpacityAwareBounds : m_OpacityAwareBounds;
        internal RenderPath EffectiveRenderPath => ActiveGroup ? ActiveGroup.m_RenderPath : m_RenderPath;
        internal int EffectiveRenderOrder => ActiveGroup ? ActiveGroup.m_RenderOrder : m_RenderOrder;
        internal SortPrecision EffectiveSortPrecision => ActiveGroup ? ActiveGroup.m_SortPrecision : m_SortPrecision;
        internal int EffectiveSortNthFrame => ActiveGroup ? ActiveGroup.m_SortNthFrame : m_SortNthFrame;
        internal int EffectiveCompactionThreshold => ActiveGroup ? ActiveGroup.m_CompactionThreshold : m_CompactionThreshold;
        internal bool EffectiveOptimizationOverrides => ActiveGroup ? ActiveGroup.m_OptimizationOverrides : m_OptimizationOverrides;
        internal bool EffectiveCompactVisibleSplats => ActiveGroup ? ActiveGroup.m_CompactVisibleSplats : m_CompactVisibleSplats;
        internal bool EffectiveWriteDepth => ActiveGroup ? ActiveGroup.m_WriteDepth : m_WriteDepth;
        internal bool EffectiveConvertGammaToLinear => ActiveGroup ? ActiveGroup.m_ConvertGammaToLinear : m_ConvertGammaToLinear;
        internal Shader EffectiveShaderSplats => ActiveGroup ? ActiveGroup.m_ShaderSplats : m_ShaderSplats;
        internal Shader EffectiveShaderComposite => ActiveGroup ? ActiveGroup.m_ShaderComposite : m_ShaderComposite;
        internal ComputeShader EffectiveCSSplatUtilities => ActiveGroup ? ActiveGroup.m_CSSplatUtilities : m_CSSplatUtilities;
        internal RenderMode EffectiveRenderMode => ActiveGroup ? RenderMode.Splats : m_RenderMode;
        readonly List<GaussianCutout> m_EffectiveCutouts = new();
        internal void RefreshCutouts(Matrix4x4 objectToWorld)
        {
            m_EffectiveCutouts.Clear();
            if (m_Cutouts != null) foreach (var c in m_Cutouts) if (c && !m_EffectiveCutouts.Contains(c)) m_EffectiveCutouts.Add(c);
            var g = ActiveGroup;
            if (g && g.m_Cutouts != null) foreach (var c in g.m_Cutouts) if (c && !m_EffectiveCutouts.Contains(c)) m_EffectiveCutouts.Add(c);
            m_CutoutBuffer.Refresh(m_EffectiveCutouts, objectToWorld);
        }
        internal void ReleaseStandaloneCamera(Camera camera)
        {
            if (m_CameraRenderResources.TryGetValue(camera, out var r)) { r.Dispose(); m_CameraRenderResources.Remove(camera); }
        }
        internal GraphicsBuffer GroupIndices => m_GpuIndexBuffer;
        internal GpuSorting GroupSorter { get { EnsureSorterAndRegister(); return m_Sorter; } }
        internal void PrepareGroupKeys(CommandBuffer cmd, Camera camera, GraphicsBuffer depths, GraphicsBuffer selected, int offset)
        {
            int kernel = EffectiveCSSplatUtilities.FindKernel("CSGroupKeys");
            SetAssetDataOnCS(cmd, kernel, null);
            GaussianSplatCameraState.ForCamera(camera).GetSharedMatrices(out var view, out _);
            view.m20 *= -1; view.m21 *= -1; view.m22 *= -1;
            cmd.SetComputeMatrixParam(EffectiveCSSplatUtilities, Props.MatrixMV, view * transform.localToWorldMatrix);
            cmd.SetComputeIntParam(EffectiveCSSplatUtilities, "_GroupBase", offset);
            cmd.SetComputeIntParam(EffectiveCSSplatUtilities, Props.SortDescending, usesDirectTransparentPath ? 1 : 0);
            cmd.SetComputeBufferParam(EffectiveCSSplatUtilities, kernel, "_GroupDepths", depths);
            cmd.SetComputeBufferParam(EffectiveCSSplatUtilities, kernel, "_GroupSelection", selected);
            cmd.DispatchCompute(EffectiveCSSplatUtilities, kernel, (m_SplatCount + 255) / 256, 1, 1);
        }
    }
}
