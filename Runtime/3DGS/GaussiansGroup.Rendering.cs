// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using Gaussians.Core;
using UnityEngine;
using UnityEngine.Rendering;

namespace Gaussians.ThreeD
{
    public sealed partial class GaussiansGroup
    {
        internal sealed class CameraResources : IDisposable
        {
            internal readonly GaussianSplat3DRenderer.CameraRenderResources Buffers = new();
            internal readonly List<GaussianSplat3DRenderer> Members = new();
            internal readonly List<GaussianSplat3DRenderer.CameraRenderResources> Views = new();
            internal GraphicsBuffer Depths, Selection;
            internal int Count, Capacity, LastUsedFrame, Generation, NextProbe;
            internal bool Compact, HasEstimate, Pending, Initialized;
            internal int PolicyFrame = -1, PolicyThreshold;
            internal bool PolicyCompaction;
            internal uint Visible;
            internal Bounds Bounds;
            internal Material Material, DepthMaterial;
            internal readonly MaterialPropertyBlock Properties = new();
            internal ComputeShader Shader;
            internal GpuSorting Sorter;
            internal GaussianSplat3DRenderer Leader => Members[0];
            internal bool Direct => Leader.usesDirectTransparentPath;
            internal bool WriteDepth => Leader.EffectiveWriteDepth;
            internal Camera Camera;
            public void Dispose()
            {
                ++Generation; Pending = false;
                Buffers.Dispose(); Depths?.Dispose(); Selection?.Dispose();
                Depths = Selection = null;
                DestroyImmediate(Material); Material = null;
                DestroyImmediate(DepthMaterial); DepthMaterial = null;
            }
        }
        readonly Dictionary<Camera, CameraResources> m_Cameras = new();
        readonly HashSet<string> m_ReportedCompatibilityErrors = new();
        readonly List<Camera> m_StaleCameras = new();
        void Update()
        {
            // Runs even when no members pass camera selection, so abandoned GPU allocations expire.
            m_StaleCameras.Clear();
            foreach (var pair in m_Cameras)
                if (!pair.Key || Time.frameCount - pair.Value.LastUsedFrame > 120) m_StaleCameras.Add(pair.Key);
            foreach (var camera in m_StaleCameras) { m_Cameras[camera].Dispose(); m_Cameras.Remove(camera); }
            if (m_Cameras.Count == 0) { AllocatedBytes = 0; ParticipatingSplats = 0; Status = "No participating camera."; }
        }
        internal bool TryGetPreparation(Camera camera, GaussianSplat3DRenderer member,
            out GaussianSplat3DRenderer.ViewPreparationStats stats)
        {
            if (m_Cameras.TryGetValue(camera, out var state) && state.Initialized)
                for (int i = 0; i < state.Members.Count; ++i)
                    if (state.Members[i] == member) { stats = state.Views[i].PreparationStats; return stats.Kernel != null; }
            stats = default; return false;
        }
        public string MemberRenderingStatus(GaussianSplat3DRenderer member, out bool warning)
        {
            warning = false;
            if (!member || !member.isActiveAndEnabled) return "Part of a group, but this renderer is disabled or inactive and is not rendering.";
            if (!member.HasValidAsset) { warning = true; return "Part of a group, but not rendering: the renderer asset is missing or invalid."; }
            if (!isActiveAndEnabled) return "Part of a group, but rendering independently because the group is disabled or inactive.";
            string error = RenderingError;
            if (error != null) { warning = true; return "Part of a group, but not rendering: " + error; }
            if (!member.HasValidRenderSetup) { warning = true; return "Part of a group, but renderer resources are not ready."; }
            foreach (var pair in m_Cameras)
                if (pair.Key && Time.frameCount - pair.Value.LastUsedFrame <= 1 && TryGetPreparation(pair.Key, member, out _))
                    return "Part of a group and rendering as a group.";
            return "Part of an enabled group. No recent grouped draw for this renderer; waiting for a camera that includes its layer.";
        }
        internal void ResetPreparationCounters(Camera camera, GaussianSplat3DRenderer member)
        {
            if (!m_Cameras.TryGetValue(camera, out var state)) return;
            for (int i = 0; i < state.Members.Count; ++i)
                if (state.Members[i] == member)
                {
                    ref var stats = ref state.Views[i].PreparationStats;
                    stats.Preparations = stats.Dispatches = stats.CacheHits = 0;
                }
        }
        internal void InvalidateMember(GaussianSplat3DRenderer member)
        {
            foreach (var camera in m_Cameras.Values)
                for (int i = 0; i < camera.Members.Count; i++)
                    if (camera.Members[i] == member) camera.Views[i].ResetValidity();
        }
        internal void ReleaseResources()
        {
            foreach (var r in m_Cameras.Values) r.Dispose();
            m_Cameras.Clear(); AllocatedBytes = 0; ParticipatingSplats = 0;
        }
        // Settings are group-owned. Only unavailable rendering resources can prevent a draw.
        public string RenderingError
        {
            get
            {
                if (!SystemInfo.supportsComputeShaders) return "Compute shaders are unsupported on this device.";
                if (!m_ShaderSplats || !m_ShaderSplats.isSupported || !m_ShaderSplats.keywordSpace.FindKeyword("GAUSSIANS_GROUPED").isValid)
                    return "The group splat shader is missing or does not support group drawing.";
                if (!m_ShaderComposite || !m_ShaderComposite.isSupported) return "The group composite shader is missing or unsupported.";
                if (!m_CSSplatUtilities || !m_CSSplatUtilities.HasKernel("CSGroupKeys") || !m_CSSplatUtilities.HasKernel("CSGroupScatter") ||
                    !m_CSSplatUtilities.IsSupported(m_CSSplatUtilities.FindKernel("CSGroupKeys")) ||
                    !m_CSSplatUtilities.IsSupported(m_CSSplatUtilities.FindKernel("CSGroupScatter")))
                    return "Group compute kernels are missing or unsupported.";
                return null;
            }
        }
        internal string CompatibilityError(IReadOnlyList<GaussianSplat3DRenderer> members) => RenderingError;
        internal CameraResources Collect(Camera camera, List<GaussianSplat3DRenderer> candidates)
        {
            string error = CompatibilityError(candidates);
            if (error != null)
            {
                string status = error + " Group rendering is unavailable.";
                if (m_ReportedCompatibilityErrors.Add(error)) Debug.LogError($"GaussiansGroup '{name}': {status}", this);
                Status = status;
                if (m_Cameras.Remove(camera, out var old)) old.Dispose();
                return null;
            }
            if (candidates.Count == 0) return null;
            m_StaleCameras.Clear();
            foreach (var pair in m_Cameras)
                if (!pair.Key || Time.frameCount - pair.Value.LastUsedFrame > 120) m_StaleCameras.Add(pair.Key);
            foreach (var c in m_StaleCameras) { m_Cameras[c].Dispose(); m_Cameras.Remove(c); }
            if (!m_Cameras.TryGetValue(camera, out var r))
            {
                r = new CameraResources { Camera = camera }; m_Cameras.Add(camera, r);
            }
            int count = 0;
            foreach (var member in candidates) count = checked(count + member.splatCount);
            int views = GaussianSplatCameraState.ForCamera(camera).ViewCount;
            var leader = candidates[0];
            bool rebuild = r.Capacity < count || r.Buffers.ViewCount != views || r.Shader != leader.EffectiveCSSplatUtilities;
            bool layout = rebuild || r.Count != count || r.Members.Count != candidates.Count;
            if (!layout)
                for (int i = 0, offset = 0; i < candidates.Count; offset += candidates[i++].splatCount)
                    layout |= r.Members[i] != candidates[i] || r.Views[i].ViewBase != offset;
            if (rebuild)
            {
                int capacity = count > r.Capacity ? Math.Max(count, checked(r.Capacity + r.Capacity / 2)) : r.Capacity;
                r.Dispose(); r.Capacity = capacity; r.HasEstimate = false;
                r.Shader = leader.EffectiveCSSplatUtilities; r.Sorter = leader.GroupSorter;
                r.Buffers.EnsureBuffers(capacity, views, r.Sorter, Time.frameCount);
                r.Buffers.EnsureCompactionBuffers();
                r.Depths = new GraphicsBuffer(GraphicsBuffer.Target.Structured, capacity, 4) { name = "GaussianGroupDepths" };
                r.Selection = new GraphicsBuffer(GraphicsBuffer.Target.Structured, capacity, 4) { name = "GaussianGroupSelection" };
            }
            if (layout)
            {
                ++r.Generation; r.Pending = false; r.HasEstimate = false;
                r.Members.Clear(); r.Members.AddRange(candidates); r.Views.Clear();
                int offset = 0;
                foreach (var member in candidates)
                {
                    r.Views.Add(new GaussianSplat3DRenderer.CameraRenderResources {
                        IsGroupView = true, ViewBase = offset, ViewStride = r.Capacity, ViewCount = views,
                        GpuView = r.Buffers.GpuView, GpuSortKeys = r.Buffers.GpuSortKeys });
                    offset += member.splatCount;
                }
                r.Initialized = false; r.PolicyFrame = -1;
            }
            r.Count = count; r.LastUsedFrame = Time.frameCount;
            if (r.PolicyFrame != Time.frameCount || r.PolicyThreshold != leader.EffectiveCompactionThreshold)
            {
                r.PolicyFrame = Time.frameCount; r.PolicyThreshold = leader.EffectiveCompactionThreshold;
                r.PolicyCompaction = GaussianSplat3DRenderer.ShouldCompact(leader.EffectiveCompactionThreshold, r.HasEstimate,
                    (uint)count - Math.Min((uint)count, r.Visible));
            }
            bool compact = leader.EffectiveOptimizationOverrides ? leader.EffectiveCompactVisibleSplats : r.PolicyCompaction;
            if (r.Compact != compact) { r.Initialized = false; r.Compact = compact; }
            foreach (var member in candidates) member.ReleaseStandaloneCamera(camera);
            for (int i = 0; i < r.Views.Count; i++) r.Views[i].GroupCompaction = compact;
            r.Bounds = leader.GetWorldBounds(camera);
            for (int i = 1; i < candidates.Count; i++) r.Bounds.Encapsulate(candidates[i].GetWorldBounds(camera));
            leader.EnsureMaterials();
            var material = r.Direct ? leader.m_MatSplatsDirect : leader.m_MatSplats;
            if (!r.Material || r.Material.shader != material.shader)
            {
                DestroyImmediate(r.Material);
                r.Material = new Material(material) { hideFlags = HideFlags.HideAndDontSave, name = "Gaussian Group" };
            }
            r.Material.CopyPropertiesFromMaterial(material);
            r.Material.EnableKeyword("GAUSSIANS_GROUPED");
            r.Material.renderQueue = (int)RenderQueue.Transparent + Mathf.Clamp(leader.EffectiveRenderOrder, -499, 500);
            leader.BindViewProperties(r.Properties, camera, r.Buffers);
            r.Properties.SetInteger(GaussianSplat3DRenderer.Props.SplatCount, r.Capacity);
            r.Properties.SetBuffer("_GroupSelection", r.Selection);
            ParticipatingSplats = count;
            AllocatedBytes = 0;
            foreach (var state in m_Cameras.Values)
            {
                // Views, key/payload ping-pong, depth/selection and compaction/radix scratch.
                AllocatedBytes += (long)state.Capacity * (40 * state.Buffers.ViewCount + 24) +
                    (long)state.Buffers.CompactGroups.count * 4 + 40 +
                    (long)state.Buffers.SorterArgs.resources.passHistBuffer.count * 4 + 4096;
            }
            Status = $"{camera.name}: {count:N0} participating splats" +
                (r.HasEstimate ? $", {r.Visible:N0} last sampled visible" : "") + (compact ? "; compacted sort." : "; full-population sort.");
            return r;
        }
        internal static void Prepare(CameraResources r, CommandBuffer cmd)
        {
            bool viewChanged = false, sort = !r.Initialized;
            for (int i = 0; i < r.Members.Count; ++i)
            {
                var member = r.Members[i]; var view = r.Views[i];
                sort |= member.ShouldSortForCamera(r.Camera, r.Direct, Time.frameCount, view);
                if (!member.ShouldPrepareViewForCamera(r.Camera, view)) continue;
                viewChanged = true;
                cmd.BeginSample("Gaussians.Group.PrepareView");
                member.CalcViewData(cmd, r.Camera, view);
                cmd.EndSample("Gaussians.Group.PrepareView");
            }
            sort |= r.Compact && viewChanged;
            if (sort || viewChanged)
            {
                cmd.BeginSample("Gaussians.Group.Keys");
                for (int i = 0; i < r.Members.Count; ++i)
                    r.Members[i].PrepareGroupKeys(cmd, r.Camera, r.Depths, r.Selection, r.Views[i].ViewBase);
                cmd.EndSample("Gaussians.Group.Keys");
            }
            var cs = r.Shader; var b = r.Buffers;
            int groups = (r.Count + 255) / 256;
            void Counts()
            {
                cmd.SetComputeIntParam(cs, "_SplatCount", r.Count);
                cmd.SetComputeIntParam(cs, "_ViewDataStride", r.Capacity);
                cmd.SetComputeIntParam(cs, "_CompactViewCount", b.ViewCount);
                cmd.SetComputeIntParam(cs, "_CompactGroupCount", groups);
                cmd.SetComputeIntParam(cs, "_CompactInstanceMultiplier", GaussianSplatCameraState.ForCamera(r.Camera).IndirectInstanceMultiplier);
                int count = cs.FindKernel("CSCountVisibleGroups"), scan = cs.FindKernel("CSScanVisibleGroups");
                cmd.SetComputeBufferParam(cs, count, "_SplatViewData", b.GpuView);
                cmd.SetComputeBufferParam(cs, count, "_CompactGroups", b.CompactGroups);
                cmd.DispatchCompute(cs, count, groups, 1, 1);
                cmd.SetComputeBufferParam(cs, scan, "_CompactGroups", b.CompactGroups);
                cmd.SetComputeBufferParam(cs, scan, "_CompactArgs", b.CompactArgs);
                cmd.DispatchCompute(cs, scan, 1, 1, 1);
            }
            if (sort)
            {
                cmd.BeginSample("Gaussians.Group.CompactAndSort");
                if (r.Compact) Counts();
                int scatter = cs.FindKernel("CSGroupScatter");
                cmd.SetComputeIntParam(cs, "_SplatCount", r.Count);
                cmd.SetComputeIntParam(cs, "_ViewDataStride", r.Capacity);
                cmd.SetComputeIntParam(cs, "_CompactViewCount", b.ViewCount);
                cmd.SetComputeIntParam(cs, "_GroupCompact", r.Compact ? 1 : 0);
                cmd.SetComputeBufferParam(cs, scatter, "_SplatViewData", b.GpuView);
                cmd.SetComputeBufferParam(cs, scatter, "_CompactGroups", b.CompactGroups);
                cmd.SetComputeBufferParam(cs, scatter, "_GroupDepths", r.Depths);
                bool alternate = r.Leader.sortKeyBits == 24;
                cmd.SetComputeBufferParam(cs, scatter, "_SplatSortDistances", alternate ? b.SorterArgs.resources.altBuffer : b.GpuSortDistances);
                cmd.SetComputeBufferParam(cs, scatter, "_SplatSortKeys", alternate ? b.SorterArgs.resources.altPayloadBuffer : b.GpuSortKeys);
                cmd.DispatchCompute(cs, scatter, groups, 1, 1);
                var args = b.SorterArgs; args.count = (uint)r.Count;
                r.Sorter.Dispatch(cmd, args, r.Leader.sortKeyBits, r.Compact ? b.CompactArgs : null);
                ++b.SortDispatchCount;
                cmd.EndSample("Gaussians.Group.CompactAndSort");
                r.Initialized = true;
            }
            if (SystemInfo.supportsAsyncGPUReadback && !r.Pending && Time.frameCount >= r.NextProbe)
            {
                if (!r.Compact || !sort) Counts();
                int generation = r.Generation;
                r.Pending = true; r.NextProbe = Time.frameCount + 30;
                cmd.RequestAsyncReadback(b.CompactArgs, 4, 0, request => {
                    if (r.Generation != generation) return;
                    r.Pending = false;
                    if (!request.hasError) { r.Visible = request.GetData<uint>()[0]; r.HasEstimate = true; }
                });
            }
        }
        internal static GaussianSplat3DRenderSystem.SplatDraw Draw(CameraResources r) => new() {
            Indices = r.Leader.GroupIndices, Matrix = Matrix4x4.identity, Material = r.Material,
            Properties = r.Properties, Count = r.Count, IndirectArgs = r.Compact ? r.Buffers.CompactArgs : null,
            RenderOrder = r.Leader.EffectiveRenderOrder, SortPosition = r.Bounds.center
        };
        internal static GaussianSplat3DRenderSystem.SplatDraw DepthDraw(CameraResources r)
        {
            if (!r.DepthMaterial || r.DepthMaterial.shader != r.Leader.m_MatSplatsDirect.shader)
            {
                DestroyImmediate(r.DepthMaterial);
                r.DepthMaterial = new Material(r.Leader.m_MatSplatsDirect) { hideFlags = HideFlags.HideAndDontSave };
            }
            r.DepthMaterial.CopyPropertiesFromMaterial(r.Leader.m_MatSplatsDirect);
            r.DepthMaterial.EnableKeyword("GAUSSIANS_GROUPED");
            var draw = Draw(r); draw.Material = r.DepthMaterial; return draw;
        }
        internal static void Submit(CameraResources r)
        {
            if (r.Compact)
                Graphics.DrawProceduralIndirect(r.Material, r.Bounds, MeshTopology.Triangles, r.Leader.GroupIndices,
                    r.Buffers.CompactArgs, 20, r.Camera, r.Properties, ShadowCastingMode.Off, false, r.Leader.gameObject.layer);
            else
                Graphics.DrawProcedural(r.Material, r.Bounds, MeshTopology.Triangles, r.Leader.GroupIndices,
                    6, r.Count, r.Camera, r.Properties, ShadowCastingMode.Off, false, r.Leader.gameObject.layer);
        }
    }
}
