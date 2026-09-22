using System;
using System.Collections.Generic;
using Gaussians.FourD.Inference;
using Unity.InferenceEngine;
using UnityEngine;

namespace Gaussians.FourD
{
    /// <summary>Owns the selected GPU deformation backend and caches unchanged model times.</summary>
    public sealed class GaussianSplat4DEvaluator : IDisposable
    {
        [Serializable] sealed class CanonicalNames
        {
            public string positions, log_scales, rotations_wxyz, opacity_logits, sh;
            public string[] All => new[] { positions, log_scales, rotations_wxyz, opacity_logits, sh };
        }
        [Serializable] sealed class Config { public bool no_dx, no_ds, no_dr, no_do, no_dshs; }
        [Serializable] sealed class Manifest { public CanonicalNames canonical; public Config configuration; }

        readonly Dictionary<string, Tensor<float>> m_Inputs = new();
        readonly Dictionary<string, ComputeBuffer> m_Raw = new();
        readonly ComputeShader m_Shader;
        GaussianSplat4DInference m_Inference;
        GaussianSplat4DNative m_Native;
        GaussianSplat4DInferenceAsset m_Asset;
        bool m_Disposed,m_StaticSH,m_HasSH;
        float m_LastTime;
        bool m_LastDeform, m_Valid;
        public GaussianSplat4DGpuData Data { get; private set; }
        public Bounds CanonicalBounds { get; private set; }
        public int EvaluationCount { get; private set; }
        public uint PositionRevision { get; private set; }
        public uint AttributeRevision { get; private set; }
        bool m_DynamicPosition, m_DynamicAttributes;

        public GaussianSplat4DEvaluator(GaussianSplat4DInferenceAsset asset, ComputeShader shader, ComputeShader nativeShader = null, Gaussians.ThreeD.GaussianSplatSHStorage storage = Gaussians.ThreeD.GaussianSplatSHStorage.Float32)
        {
            if (!asset || !asset.Data || !shader) throw new ArgumentException("An inference asset and compute shader are required.");
            m_Shader = shader;
            m_Asset = asset;
            try
            {
                var manifest=JsonUtility.FromJson<Manifest>(asset.Data.Manifest.text);
                var config = manifest.configuration;
                m_DynamicPosition = config == null || !config.no_dx;
                m_DynamicAttributes = config == null || !config.no_dx || !config.no_ds || !config.no_dr || !config.no_do || !config.no_dshs;
                if(nativeShader)
                {
                    m_Native=new GaussianSplat4DNative(asset.Data,nativeShader,shader,storage);
                    Data=m_Native.Data;CanonicalBounds=m_Native.CanonicalBounds;return;
                }
                var names = manifest.canonical.All;
                m_StaticSH=manifest.configuration!=null && manifest.configuration.no_dshs;
                for (int i = 0; i < GaussianSplat4DInference.Attributes.Length; ++i)
                {
                    string attribute = GaussianSplat4DInference.Attributes[i];
                    var tensor = asset.Data.GetTensor(names[i]);
                    float[] values = tensor.ReadFloats();
                    if (attribute == "positions")
                    {
                        var bounds = new Bounds(new Vector3(values[0], values[1], values[2]), Vector3.zero);
                        for (int p = 3; p < values.Length; p += 3) bounds.Encapsulate(new Vector3(values[p], values[p+1], values[p+2]));
                        CanonicalBounds = bounds;
                    }
                    m_Inputs.Add(attribute, new Tensor<float>(new TensorShape(tensor.shape), values));
                    ComputeTensorData.Pin(m_Inputs[attribute]);
                }
                int count = asset.Data.GaussianCount;
                m_Inputs.Add("times", new Tensor<float>(new TensorShape(count, 1)));
                ComputeTensorData.Pin(m_Inputs["times"]);
                Data = new GaussianSplat4DGpuData(count, (asset.Data.SHDegree + 1) * (asset.Data.SHDegree + 1), shader, storage);
                // Load the network lazily; the canonical baseline requires no model worker.
            }
            catch { Dispose(); throw; }
        }

        public bool Evaluate(float modelTime, bool deform = true, bool force = false)
        {
            if (m_Disposed) throw new ObjectDisposedException(nameof(GaussianSplat4DEvaluator));
            if (float.IsNaN(modelTime) || float.IsInfinity(modelTime)) throw new ArgumentOutOfRangeException(nameof(modelTime));
            if (!force && m_Valid && deform == m_LastDeform && (!deform || modelTime == m_LastTime)) return false;
            bool modeChanged = !m_Valid || deform != m_LastDeform;
            if (modeChanged || (deform && m_DynamicPosition)) PositionRevision++;
            if (modeChanged || (deform && m_DynamicAttributes)) AttributeRevision++;
            m_Valid = false;
            if(m_Native!=null)
            {
                m_Native.Evaluate(modelTime,deform);
                if(deform)EvaluationCount++;
                m_LastTime=modelTime;m_LastDeform=deform;m_Valid=true;return true;
            }
            if (deform)
            {
                m_Inference ??= new GaussianSplat4DInference(m_Asset);
                int kernel = m_Shader.FindKernel("FillTimes");
                m_Shader.SetInt("_Count", Data.Count);
                m_Shader.SetFloat("_ModelTime", modelTime);
                m_Shader.SetBuffer(kernel, "_Times", ((ComputeTensorData)m_Inputs["times"].dataOnBackend).buffer);
                m_Shader.Dispatch(kernel, (Data.Count + 63) / 64, 1, 1);
                m_Inference.Schedule(m_Inputs);
                EvaluationCount++;
            }
            foreach (string attribute in GaussianSplat4DInference.Attributes)
                m_Raw[attribute] = deform ? m_Inference.GetOutputBuffer(attribute) : ((ComputeTensorData)m_Inputs[attribute].dataOnBackend).buffer;
            // All inference/copy/draw work uses the graphics queue, in submission order.
            Data.Apply(m_Raw,!m_HasSH || !m_StaticSH);
            m_HasSH=true;
            m_LastTime = modelTime; m_LastDeform = deform; m_Valid = true;
            return true;
        }

        public void Dispose()
        {
            if(m_Native!=null)m_Native.Dispose();else Data?.Dispose();
            m_Native=null;Data = null;m_Disposed=true;
            m_Inference?.Dispose(); m_Inference = null;
            foreach (var tensor in m_Inputs.Values) tensor.Dispose();
            m_Inputs.Clear(); m_Raw.Clear(); m_Valid = false;
        }
    }
}
