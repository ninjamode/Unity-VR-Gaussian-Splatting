using Unity.InferenceEngine;
using UnityEngine;
using Gaussians.ThreeD;

namespace Gaussians.FourD.Inference
{
    public sealed class GaussianSplat4DInferenceAsset : ScriptableObject
    {
        [SerializeField] int m_Revision;
        public int Revision => m_Revision;
        [SerializeField] GaussianSplat4DAsset m_Data;
        [SerializeField] ModelAsset m_Model;
        [SerializeField] GaussianSplat3DAsset m_Canonical;
        [SerializeField] Hash128 m_CanonicalHash;
        public GaussianSplat3DAsset Canonical => m_Canonical;
        public void SetCanonical(GaussianSplat3DAsset canonical)
        {
            m_Canonical = canonical;
            m_CanonicalHash = canonical ? canonical.dataHash : default;
        }
        public bool Matches(GaussianSplat3DAsset canonical) => canonical && canonical == m_Canonical &&
            canonical.hasValidFloatData && canonical.dataHash == m_CanonicalHash && Data &&
            canonical.splatCount == Data.GaussianCount && canonical.floatSHDegree == Data.SHDegree;
        public GaussianSplat4DAsset Data => m_Data;
        public ModelAsset Model => m_Model;

        public void Initialize(GaussianSplat4DAsset data, ModelAsset model)
        {
            ++m_Revision;
            m_Data = data;
            m_Model = model;
        }
    }
}
