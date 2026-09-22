using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Gaussians.FourD
{
    public enum Gaussian4DBackends { Both, Native, Onnx }

    [Serializable]
    public sealed class GaussianTensorData
    {
        public string name;
        public string dtype;
        public int[] shape;
        public TextAsset data;
        public int byteOffset;
        public bool packed;

        public float[] ReadFloats()
        {
            if (dtype != "<f4" || !BitConverter.IsLittleEndian)
                throw new NotSupportedException("Expected little-endian FP32 tensor: " + name);
            long count = 1;
            foreach (int dimension in shape) count = checked(count * dimension);
            long byteLength = data ? data.dataSize : 0;
            if (count < 0 || count > int.MaxValue || (packed ? byteOffset < 0 || byteOffset % 4 != 0 || byteOffset + count * 4 > byteLength : byteLength != count * 4))
                throw new InvalidDataException("Invalid tensor byte length: " + name);
            var result = new float[(int)count];
            data.GetData<float>().GetSubArray(byteOffset / 4, (int)count).CopyTo(result);
            return result;
        }
    }

    /// <summary>Canonical data and exported fixtures, independent of the inference package.</summary>
    public sealed class GaussianSplat4DAsset : ScriptableObject
    {
        [SerializeField] Gaussian4DBackends m_Backends;
        public Gaussian4DBackends Backends => m_Backends;
        public bool HasNative => m_Backends != Gaussian4DBackends.Onnx;
        [SerializeField] int m_GaussianCount;
        [SerializeField] int m_SHDegree;
        [SerializeField] int m_BundleVersion;
        [SerializeField] TextAsset m_Manifest;
        [SerializeField, HideInInspector] GaussianTensorData[] m_Tensors;
        Dictionary<string, GaussianTensorData> m_Lookup;

        public int GaussianCount => m_GaussianCount;
        public int SHDegree => m_SHDegree;
        public int BundleVersion => m_BundleVersion;
        public TextAsset Manifest => m_Manifest;
        public IReadOnlyList<GaussianTensorData> Tensors => m_Tensors;

        public GaussianTensorData GetTensor(string name)
        {
            if (m_Lookup == null)
            {
                m_Lookup = new Dictionary<string, GaussianTensorData>(StringComparer.Ordinal);
                foreach (var tensor in m_Tensors) m_Lookup.Add(tensor.name, tensor);
            }
            if (!m_Lookup.TryGetValue(name, out var value)) throw new KeyNotFoundException("Missing tensor: " + name);
            return value;
        }

        public void Initialize(int count, int degree, int version, TextAsset manifest, GaussianTensorData[] tensors, Gaussian4DBackends backends = Gaussian4DBackends.Both)
        {
            m_Backends = backends;
            m_GaussianCount = count;
            m_SHDegree = degree;
            m_BundleVersion = version;
            m_Manifest = manifest;
            m_Tensors = tensors;
            m_Lookup = null;
        }

        void OnEnable() => m_Lookup = null;
    }
}
