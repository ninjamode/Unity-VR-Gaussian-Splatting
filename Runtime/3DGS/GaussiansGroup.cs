// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using Gaussians.Core;
using UnityEngine;

namespace Gaussians.ThreeD
{
    [ExecuteAlways, DisallowMultipleComponent, AddComponentMenu("Gaussians/Gaussians Group")]
    public sealed partial class GaussiansGroup : MonoBehaviour
    {
        [SerializeField] List<GaussianSplat3DRenderer> m_Members = new();
        [Header("Shared Settings Override")]
        [Range(0, 1)] public float m_AlphaCutoff = GaussianSplat3DRenderer.DefaultAlphaCutoff;
        public bool m_OpacityAwareBounds = true;
        public bool m_WriteDepth;
        [Tooltip("Optimal (default) projects onto a per-eye tangent plane and evaluates its perspective-correct footprint. Standard uses the screen-space Jacobian ellipse.")]
        public GaussianSplat3DRenderer.ProjectionMode m_ProjectionMode = GaussianSplat3DRenderer.ProjectionMode.Optimal;
        [Min(-1), Tooltip("Rejected splats required to enable compaction and deferred SH. -1: off; 0: always on; positive: automatic using recent visibility.")]
        public int m_CompactionThreshold = 100000;
        public GaussianSplat3DRenderer.SortPrecision m_SortPrecision = GaussianSplat3DRenderer.SortPrecision.Bits32;
        [Range(1, 30)] public int m_SortNthFrame = 1;
        public GaussianSplat3DRenderer.RenderPath m_RenderPath = GaussianSplat3DRenderer.RenderPath.DirectTransparent;
        public bool m_ConvertGammaToLinear = true;
        public int m_RenderOrder;
        public Shader m_ShaderSplats;
        public Shader m_ShaderComposite;
        public ComputeShader m_CSSplatUtilities;
        // Benchmark controls; ordinary use selects the compaction policy above.
        [HideInInspector] public bool m_OptimizationOverrides;
        [HideInInspector] public bool m_CompactVisibleSplats;
        [Tooltip("Appended after each member's own cutouts, with duplicate references removed. Existing order-sensitive cutout rules apply.")]
        public GaussianCutout[] m_Cutouts = Array.Empty<GaussianCutout>();
        public IReadOnlyList<GaussianSplat3DRenderer> Members => m_Members;
        public string Status { get; private set; } = "No camera has rendered this group.";
        public int ParticipatingSplats { get; private set; }
        public long AllocatedBytes { get; private set; }
        public int ActiveMemberCount
        {
            get { int count = 0; foreach (var r in m_Members) if (r && r.isActiveAndEnabled) ++count; return count; }
        }
        public bool AddMember(GaussianSplat3DRenderer renderer)
        {
            if (!renderer || m_Members.Contains(renderer)) return false;
            if (renderer.group && renderer.group != this)
            {
                Debug.LogWarning($"Cannot add '{renderer.name}' to '{name}': already assigned to '{renderer.group.name}'.", this);
                return false;
            }
            int emptySlot = m_Members.FindIndex(r => !r);
            if (emptySlot >= 0) m_Members[emptySlot] = renderer;
            else m_Members.Add(renderer);
            renderer.m_Group = this;
            return true;
        }
        public bool RemoveMember(GaussianSplat3DRenderer renderer)
        {
            if (!m_Members.Remove(renderer)) return false;
            if (renderer && renderer.m_Group == this) renderer.m_Group = null;
            return true;
        }
        public int AddHierarchy()
        {
            int count = 0;
            foreach (var r in GetComponentsInChildren<GaussianSplat3DRenderer>(true)) if (AddMember(r)) ++count;
            return count;
        }
        public void ValidateMembers()
        {
            var seen = new HashSet<GaussianSplat3DRenderer>();
            for (int i = 0; i < m_Members.Count; ++i)
            {
                var r = m_Members[i];
                if (!r) continue;
                if (!seen.Add(r)) { m_Members[i] = null; continue; }
                if (r.group && r.group != this)
                {
                    Debug.LogWarning($"Cannot add '{r.name}' to '{name}': already assigned to '{r.group.name}'.", this);
                    m_Members[i] = null; continue;
                }
                r.m_Group = this;
            }
        }
        void OnEnable() { ResolveShaders(); ValidateMembers(); }
        void OnValidate()
        {
            m_AlphaCutoff = Mathf.Clamp01(m_AlphaCutoff);
            m_CompactionThreshold = Math.Max(-1, m_CompactionThreshold);
            m_SortNthFrame = Math.Max(1, m_SortNthFrame);
            ResolveShaders(); ValidateMembers();
        }
        void Reset() => ResolveShaders();
        void ResolveShaders()
        {
#if UNITY_EDITOR
            const string path = "Packages/net.kleinbeck.gaussians/Shaders/3DGS/";
            if (!m_ShaderSplats) m_ShaderSplats = UnityEditor.AssetDatabase.LoadAssetAtPath<Shader>(path + "RenderGaussianSplats.shader");
            if (!m_ShaderComposite) m_ShaderComposite = UnityEditor.AssetDatabase.LoadAssetAtPath<Shader>(path + "GaussianComposite.shader");
            if (!m_CSSplatUtilities) m_CSSplatUtilities = UnityEditor.AssetDatabase.LoadAssetAtPath<ComputeShader>(path + "SplatUtilities.compute");
#endif
        }
        void OnDisable() { ReleaseResources(); }
        void OnDestroy()
        {
            ReleaseResources();
            foreach (var r in m_Members) if (r && r.m_Group == this) r.m_Group = null;
        }
    }
}
