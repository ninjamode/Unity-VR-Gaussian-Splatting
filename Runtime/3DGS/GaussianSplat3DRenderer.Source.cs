// SPDX-License-Identifier: MIT
using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace Gaussians.ThreeD
{
    public partial class GaussianSplat3DRenderer
    {
        static readonly int SHStorageId = Shader.PropertyToID("_SplatSHStorage");
        static readonly int FloatSourceId = Shader.PropertyToID("_SplatFloatSource");
        static readonly int CoefficientCountId = Shader.PropertyToID("_SplatCoefficientCount");
        static readonly int FloatAttributesId = Shader.PropertyToID("_SplatFloatAttributes");
        static readonly int FloatSHId = Shader.PropertyToID("_SplatFloatSH");
        GaussianSplat3DData m_FloatData;
        IGaussianSplat3DDeformation m_Deformation;
        GaussianSplat3DFrame m_SourceFrame;
        public bool CanEditSplats => m_Asset && !m_Asset.isFloatSource && m_Deformation == null;
        public bool IsDeformed => m_SourceFrame.Data != null;
        GaussianSplat3DData sourceData => m_SourceFrame.Data ?? m_FloatData;
        bool floatSource => IsDeformed || (m_Asset && m_Asset.isFloatSource);

        /// <summary>One provider per renderer. Detach before disposing provider-owned buffers.</summary>
        public void SetDeformation(IGaussianSplat3DDeformation provider)
        {
            if (ReferenceEquals(m_Deformation, provider)) return;
            if (provider != null && m_Deformation != null)
                throw new InvalidOperationException("Only one deformation provider can own a Gaussian renderer.");
            m_Deformation = provider;
            m_SourceFrame = default;
            ++m_RenderDataVersion;
            ResetStereoFrameCaches();
        }

        internal void PrepareSource()
        {
            GaussianSplat3DFrame next = default;
            if (m_Deformation != null && m_Deformation.TryGetFrame(m_Asset, out var frame) &&
                frame.Data != null && frame.Data.Count == m_SplatCount && frame.Data.Splats != null && frame.Data.SH != null)
                next = frame;
            if (!ReferenceEquals(next.Data, m_SourceFrame.Data)) ++m_RenderDataVersion;
            m_SourceFrame = next;
        }

        void BindSource(CommandBuffer cmd, ComputeShader shader, int kernel)
        {
            cmd.SetComputeIntParam(shader, SHStorageId, (int)sourceData.SHStorage);
            cmd.SetComputeIntParam(shader, FloatSourceId, floatSource ? 1 : 0);
            cmd.SetComputeIntParam(shader, CoefficientCountId, sourceData.CoefficientCount);
            cmd.SetComputeBufferParam(shader, kernel, FloatAttributesId, sourceData.Splats);
            cmd.SetComputeBufferParam(shader, kernel, FloatSHId, sourceData.SH);
        }
        void BindSource(MaterialPropertyBlock properties)
        {
            properties.SetInt(SHStorageId, (int)sourceData.SHStorage);
            properties.SetInt(FloatSourceId, floatSource ? 1 : 0);
            properties.SetInt(CoefficientCountId, sourceData.CoefficientCount);
            properties.SetBuffer(FloatAttributesId, sourceData.Splats);
            properties.SetBuffer(FloatSHId, sourceData.SH);
        }

        void Reset() => ResolveShaders();
        void ResolveShaders()
        {
#if UNITY_EDITOR
            const string path = "Packages/net.kleinbeck.gaussians/Shaders/3DGS/";
            if (!m_ShaderSplats) m_ShaderSplats = UnityEditor.AssetDatabase.LoadAssetAtPath<Shader>(path + "RenderGaussianSplats.shader");
            if (!m_ShaderComposite) m_ShaderComposite = UnityEditor.AssetDatabase.LoadAssetAtPath<Shader>(path + "GaussianComposite.shader");
            if (!m_ShaderDebugPoints) m_ShaderDebugPoints = UnityEditor.AssetDatabase.LoadAssetAtPath<Shader>(path + "GaussianDebugRenderPoints.shader");
            if (!m_ShaderDebugBoxes) m_ShaderDebugBoxes = UnityEditor.AssetDatabase.LoadAssetAtPath<Shader>(path + "GaussianDebugRenderBoxes.shader");
            if (!m_CSSplatUtilities) m_CSSplatUtilities = UnityEditor.AssetDatabase.LoadAssetAtPath<ComputeShader>(path + "SplatUtilities.compute");
#endif
        }
    }
}
