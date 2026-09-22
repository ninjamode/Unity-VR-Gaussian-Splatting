using System;
using System.Collections.Generic;
using UnityEngine;

namespace Gaussians.FourD
{
    /// <summary>Renderer-owned activated attributes. No worker buffers escape this copy boundary.</summary>
    public sealed class GaussianSplat4DGpuData : Gaussians.ThreeD.GaussianSplat3DData
    {
        readonly ComputeShader m_Shader;
        public GaussianSplat4DGpuData(int count, int coefficientCount, ComputeShader shader, Gaussians.ThreeD.GaussianSplatSHStorage storage = Gaussians.ThreeD.GaussianSplatSHStorage.Float32) : base(count, coefficientCount, storage)
        {
            if (!shader) { Dispose(); throw new ArgumentNullException(nameof(shader)); }
            m_Shader = shader;
        }

        public void Apply(IReadOnlyDictionary<string, ComputeBuffer> raw, bool writeSH = true)
        {
            int kernel = m_Shader.FindKernel("Activate");
            m_Shader.SetInt("_SHStorage", (int)SHStorage);
            m_Shader.SetInt("_Count", Count);
            m_Shader.SetInt("_CoefficientCount", CoefficientCount);
            m_Shader.SetInt("_WriteSH", writeSH ? 1 : 0);
            m_Shader.SetBuffer(kernel, "_Positions", raw["positions"]);
            m_Shader.SetBuffer(kernel, "_LogScales", raw["log_scales"]);
            m_Shader.SetBuffer(kernel, "_Rotations", raw["rotations_wxyz"]);
            m_Shader.SetBuffer(kernel, "_OpacityLogits", raw["opacity_logits"]);
            m_Shader.SetBuffer(kernel, "_RawSH", raw["sh"]);
            m_Shader.SetBuffer(kernel, "_Splats", Splats);
            m_Shader.SetBuffer(kernel, "_SH", SH);
            m_Shader.Dispatch(kernel, (Count + 63) / 64, 1, 1);
        }

    }
}
