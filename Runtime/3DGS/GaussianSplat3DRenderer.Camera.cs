// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using Gaussians.Core;
using Unity.Profiling;
using Unity.Profiling.LowLevel;
using UnityEngine;
using UnityEngine.Rendering;

namespace Gaussians.ThreeD
{
    public partial class GaussianSplat3DRenderer
    {
        const int kGpuViewDataSize = 40;

        bool UseCompaction(CameraRenderResources resources)
        {
            if (m_RenderMode != RenderMode.Splats) return false;
            if (m_OptimizationOverrides) return m_CompactVisibleSplats;
            // Latch positive-threshold decisions per frame so a readback callback cannot
            // change the draw submission mode between pre-cull submission and preparation.
            if (m_CompactionThreshold <= 0) return m_CompactionThreshold == 0;
            if (resources.PolicyFrame != Time.frameCount || resources.PolicyThreshold != m_CompactionThreshold)
            {
                resources.PolicyFrame = Time.frameCount;
                resources.PolicyThreshold = m_CompactionThreshold;
                resources.PolicyCompaction = ShouldCompact(m_CompactionThreshold, resources.HasVisibilityEstimate, resources.RejectedSplats);
            }
            return resources.PolicyCompaction;
        }
        internal static bool ShouldCompact(int threshold, bool hasEstimate, uint rejected) =>
            threshold == 0 || (threshold > 0 && hasEstimate && rejected >= (uint)threshold);
        bool UseDeferredSH(CameraRenderResources resources) => m_OptimizationOverrides ? m_DeferredSHLoading : UseCompaction(resources);

        internal struct CameraSortState
        {
            public int LastFrame;
            public int RenderCount;
            public bool BackToFront;
            public bool HasSignature;
            public Matrix4x4 MatrixMV;
            public int RenderDataVersion;
            public int SortNthFrame;
            public int SortKeyBits;
            public uint PositionRevision;
        }

        internal struct ViewSignature : IEquatable<ViewSignature>
        {
            public Matrix4x4 View;
            public Matrix4x4 Projection;
            public Matrix4x4 RightView, RightProjection;
            public int ViewCount;
            public StereoViewMode StereoMode;
            public int IndirectInstanceMultiplier;
            public Vector2Int RightScreenSize;
            public bool ConvertColor;
            public Matrix4x4 ObjectToWorld;
            public int ScreenWidth;
            public int ScreenHeight;
            public float SplatScale;
            public float OpacityScale;
            public int SHOrder;
            public bool SHOnly;
            public float MinimumSplatRadiusPixels;
            public int CutoutRevision;
            public int RenderDataVersion;
            public uint AttributeRevision;
            public float NearClip;
            public float MinimumDistance, MinimumOpacity;
            public bool DeferredSHLoading;
            public bool EarlyFrustumCulling, OpacityAwareBounds;
            public float AlphaCutoff;

            public bool Equals(ViewSignature other)
            {
                return StereoMode == other.StereoMode && EarlyFrustumCulling == other.EarlyFrustumCulling &&
                       OpacityAwareBounds == other.OpacityAwareBounds && AlphaCutoff.Equals(other.AlphaCutoff) &&
                       IndirectInstanceMultiplier == other.IndirectInstanceMultiplier && DeferredSHLoading == other.DeferredSHLoading && MinimumDistance.Equals(other.MinimumDistance) && MinimumOpacity.Equals(other.MinimumOpacity) &&
                       AttributeRevision == other.AttributeRevision && NearClip.Equals(other.NearClip) && RightView.Equals(other.RightView) && RightProjection.Equals(other.RightProjection) &&
                       RightScreenSize.Equals(other.RightScreenSize) && ViewCount == other.ViewCount && ConvertColor == other.ConvertColor && View.Equals(other.View) &&
                       Projection.Equals(other.Projection) &&
                       ObjectToWorld.Equals(other.ObjectToWorld) &&
                       ScreenWidth == other.ScreenWidth &&
                       ScreenHeight == other.ScreenHeight &&
                       SplatScale.Equals(other.SplatScale) &&
                       OpacityScale.Equals(other.OpacityScale) &&
                       SHOrder == other.SHOrder &&
                       SHOnly == other.SHOnly &&
                       MinimumSplatRadiusPixels.Equals(other.MinimumSplatRadiusPixels) &&
                       CutoutRevision == other.CutoutRevision &&
                       RenderDataVersion == other.RenderDataVersion;
            }
        }

        internal sealed class CameraRenderResources : IDisposable
        {
            internal GraphicsBuffer GpuSortDistances;
            internal GraphicsBuffer GpuSortKeys;
            internal GraphicsBuffer GpuView;
            internal GraphicsBuffer CompactGroups;
            internal GraphicsBuffer CompactArgs;
            internal bool Compacted;
            internal bool HasVisibilityEstimate, VisibilityReadbackPending;
            internal uint RejectedSplats;
            internal int NextVisibilityProbeFrame, VisibilityGeneration;
            internal int PolicyFrame = -1, PolicyThreshold;
            internal bool PolicyCompaction;
            internal ViewPreparationStats PreparationStats;
            internal int SortDispatchCount; // Regression-test diagnostic; no GPU readback.


            internal void EnsureCompactionBuffers()
            {
                if (CompactArgs != null) return;
                CompactGroups = new GraphicsBuffer(GraphicsBuffer.Target.Structured, (GpuSortKeys.count + 255) / 256, 4)
                    { name = "GaussianVisibilityGroupOffsets" };
                CompactArgs = new GraphicsBuffer(GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.IndirectArguments, 10, 4)
                    { name = "GaussianVisibilityIndirectArgs" };
                CompactArgs.SetData(new uint[10]);
            }
            internal GpuSorting.Args SorterArgs;
            internal readonly MaterialPropertyBlock DirectMaterialProperties = new();
            internal CameraSortState SortState;
            internal ViewSignature ViewSignature;
            internal bool HasViewSignature;
            internal bool SortKeysInitialized;

            internal Material DirectMaterial;
            internal int ViewCount;
            internal int LastUsedFrame;

            internal bool EnsureBuffers(int count, int viewCount, GpuSorting sorter, int frame)
            {
                if (count <= 0)
                    return false;
                LastUsedFrame = frame;
                if (GpuView != null && GpuView.count == checked(count * viewCount) &&
                    ViewCount == viewCount && GpuSortKeys.count == count)
                    return true;

                DisposeBuffers();
                ViewCount = viewCount;
                GpuView = new GraphicsBuffer(GraphicsBuffer.Target.Structured, checked(count * viewCount), kGpuViewDataSize)
                {
                    name = "GaussianSplatViewData"
                };
                GpuSortDistances = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, 4)
                {
                    name = "GaussianSplatSortDistances"
                };
                GpuSortKeys = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, 4)
                {
                    name = "GaussianSplatSortIndices"
                };

                SorterArgs.inputKeys = GpuSortDistances;
                SorterArgs.inputValues = GpuSortKeys;
                SorterArgs.count = (uint)count;
                if (sorter.Valid)
                    SorterArgs.resources = GpuSorting.SupportResources.Load((uint)count);

                SortKeysInitialized = false;
                ResetValidity();
                return true;
            }

            internal void ResetValidity()
            {
                SortState = default;
                SortState.LastFrame = int.MinValue;
                HasViewSignature = false;
            }

            void DisposeBuffers()
            {
                ++VisibilityGeneration;
                PolicyFrame = -1;
                HasVisibilityEstimate = VisibilityReadbackPending = false;
                NextVisibilityProbeFrame = 0;
                RejectedSplats = 0;
                CompactGroups?.Dispose();
                CompactArgs?.Dispose();
                CompactGroups = null;
                CompactArgs = null;
                Compacted = false;
                GpuView?.Dispose();
                GpuSortDistances?.Dispose();
                GpuSortKeys?.Dispose();
                SorterArgs.resources.Dispose();
                GpuView = null;
                GpuSortDistances = null;
                GpuSortKeys = null;
                SorterArgs = default;
                SortKeysInitialized = false;
            }

            public void Dispose()
            {
                DisposeBuffers();
                DestroyImmediate(DirectMaterial);
            }
        }

        readonly Dictionary<Camera, CameraRenderResources> m_CameraRenderResources = new();

        readonly List<Camera> m_DestroyedCameraResources = new();

        static readonly ProfilerMarker s_ProfSort = new(ProfilerCategory.Render, "Gaussians.3D.Sort", MarkerFlags.SampleGPU);

        CameraRenderResources GetOrCreateCameraRenderResources(Camera cam)
        {
            if (cam == null)
                return null;

            if (!m_CameraRenderResources.TryGetValue(cam, out CameraRenderResources resources))
            {
                resources = new CameraRenderResources();
                m_CameraRenderResources.Add(cam, resources);
            }
            return resources;
        }

        internal CameraRenderResources GetCameraRenderResources(Camera cam)
        {
            CameraRenderResources resources = GetOrCreateCameraRenderResources(cam);
            if (resources == null)
                return null;

            EnsureSorterAndRegister();
            return resources.EnsureBuffers(m_SplatCount, GaussianSplatCameraState.ForCamera(cam).ViewCount, m_Sorter, Time.frameCount) ? resources : null;
        }

        void DisposeCameraRenderResources()
        {
            foreach (CameraRenderResources resources in m_CameraRenderResources.Values)
                resources.Dispose();
            m_CameraRenderResources.Clear();
            m_DestroyedCameraResources.Clear();
        }

        void ReleaseDestroyedCameraRenderResources()
        {
            m_DestroyedCameraResources.Clear();
            foreach (var kvp in m_CameraRenderResources)
            {
                if (!kvp.Key || Time.frameCount - kvp.Value.LastUsedFrame > 120)
                    m_DestroyedCameraResources.Add(kvp.Key);
            }

            foreach (Camera cam in m_DestroyedCameraResources)
            {
                m_CameraRenderResources[cam].Dispose();
                m_CameraRenderResources.Remove(cam);
            }
            m_DestroyedCameraResources.Clear();
        }

        void InvalidateCameraPreparation()
        {
            foreach (CameraRenderResources resources in m_CameraRenderResources.Values)
                resources.ResetValidity();
        }

        ViewSignature CreateViewSignature(Camera cam, CameraRenderResources resources)
        {
            Matrix4x4 objectToWorld = transform.localToWorldMatrix;
            var state = GaussianSplatCameraState.ForCamera(cam);
            m_CutoutBuffer.Refresh(m_Cutouts, objectToWorld);
            return new ViewSignature
            {
                View = state.View,
                RightView = state.RightView,
                RightProjection = state.RightProjection,
                ViewCount = state.ViewCount,
                StereoMode = ResolveStereoViewMode(state, false),
                IndirectInstanceMultiplier = state.IndirectInstanceMultiplier,
                RightScreenSize = state.RightScreenSize,
                ConvertColor = effectiveDirectConversion,
                Projection = state.Projection,
                ObjectToWorld = objectToWorld,
                ScreenWidth = state.ScreenSize.x,
                ScreenHeight = state.ScreenSize.y,
                SplatScale = m_SplatScale,
                OpacityScale = m_OpacityScale,
                SHOrder = m_SHOrder,
                SHOnly = m_SHOnly,
                MinimumSplatRadiusPixels = (m_EarlyRejection ? Mathf.Max(0.0f, m_MinimumSplatRadiusPixels) : 0),
                CutoutRevision = m_CutoutBuffer.Revision,
                RenderDataVersion = m_RenderDataVersion,
                AttributeRevision = m_SourceFrame.AttributeRevision,
                NearClip = cam.nearClipPlane,
                MinimumDistance = (m_EarlyRejection ? Mathf.Max(0, m_MinimumSplatDistance) : 0),
                MinimumOpacity = MinimumOpacity,
                DeferredSHLoading = UseDeferredSH(resources),
                EarlyFrustumCulling = m_EarlyFrustumCulling,
                OpacityAwareBounds = m_OpacityAwareBounds, AlphaCutoff = Mathf.Clamp01(m_AlphaCutoff),
            };
        }

        internal bool ShouldSortForCamera(Camera cam, bool backToFront)
        {
            return ShouldSortForCameraAtFrame(cam, backToFront, Time.frameCount);
        }

        bool ShouldSortForCameraAtFrame(Camera cam, bool backToFront, int frame)
        {
            return ShouldSortForCamera(cam, backToFront, frame, GetOrCreateCameraRenderResources(cam));
        }

        bool ShouldSortForCamera(Camera cam, bool backToFront, int frame, CameraRenderResources resources)
        {
            if (resources == null)
                return false;

            GaussianSplatCameraState.ForCamera(cam).GetSharedMatrices(out Matrix4x4 view, out _);
            Matrix4x4 matrixMV = view * transform.localToWorldMatrix;
            CameraSortState state = resources.SortState;
            bool settingsChanged = !state.HasSignature ||
                                   state.BackToFront != backToFront ||
                                   state.RenderDataVersion != m_RenderDataVersion ||
                                   state.PositionRevision != m_SourceFrame.PositionRevision ||
                                   state.SortKeyBits != sortKeyBits ||
                                   state.SortNthFrame != m_SortNthFrame;
            bool matrixChanged = !state.HasSignature || !state.MatrixMV.Equals(matrixMV);
            bool renderedThisFrame = state.HasSignature && state.LastFrame == frame;

            // Single-pass stereo shares a center-eye order. Multipass uses the current
            // XR pass view, so changed eye matrices trigger a new order in the same frame.
            // Ordinary movement across frames still honors the configured sort cadence.
            if (!settingsChanged && !matrixChanged && renderedThisFrame)
                return false;

            if (settingsChanged)
                state.RenderCount = 0;

            bool shouldSort = settingsChanged ||
                              (matrixChanged && renderedThisFrame) ||
                              state.RenderCount % Mathf.Max(1, m_SortNthFrame) == 0;
            state.LastFrame = frame;
            state.RenderCount++;
            state.BackToFront = backToFront;
            state.HasSignature = true;
            state.MatrixMV = matrixMV;
            state.RenderDataVersion = m_RenderDataVersion;
            state.PositionRevision = m_SourceFrame.PositionRevision;
            state.SortNthFrame = m_SortNthFrame;
            state.SortKeyBits = sortKeyBits;
            resources.SortState = state;
            return shouldSort;
        }

        internal bool ShouldPrepareViewForCamera(Camera cam)
        {
            return ShouldPrepareViewForCamera(cam, GetOrCreateCameraRenderResources(cam));
        }

        bool ShouldPrepareViewForCamera(Camera cam, CameraRenderResources resources)
        {
            if (resources == null)
                return false;

            ViewSignature signature = CreateViewSignature(cam, resources);
            if (!(cam == m_BenchmarkCamera && m_BenchmarkCaptureFrame) && resources.HasViewSignature && resources.ViewSignature.Equals(signature))
            {
                ++resources.PreparationStats.CacheHits;
                return false;
            }

            resources.ViewSignature = signature;
            resources.HasViewSignature = true;
            return true;
        }

        internal void CalcViewData(CommandBuffer cmb, Camera cam, CameraRenderResources resources)
        {
            if (cam.cameraType == CameraType.Preview) return;
            bool diagnostics = cam == m_BenchmarkCamera && m_BenchmarkCaptureFrame && m_BenchmarkCounts != null;
            var state = GaussianSplatCameraState.ForCamera(cam);
            var mode = ResolveStereoViewMode(state, diagnostics);
            int kernel = diagnostics ? m_CSSplatUtilities.FindKernel("CSCalcViewDataDiagnostics") : ViewKernel(mode);
            SetAssetDataOnCS(cmb, kernel, resources);
            if (diagnostics)
            {
                if (resources.ViewCount != 1) throw new InvalidOperationException("Diagnostics require mono rendering.");
                cmb.SetBufferData(m_BenchmarkCounts, s_EmptyBenchmarkCounts);
                cmb.SetComputeBufferParam(m_CSSplatUtilities, kernel, "_BenchmarkCounts", m_BenchmarkCounts);
                m_BenchmarkCountFrame = Time.frameCount;
            }
            var objectToWorld = transform.localToWorldMatrix;
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixObjectToWorld, objectToWorld);
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixWorldToObject, transform.worldToLocalMatrix);
            cmb.SetComputeIntParam(m_CSSplatUtilities, "_DeferredSHLoading", UseDeferredSH(resources) ? 1 : 0);
            cmb.SetComputeIntParam(m_CSSplatUtilities, "_EarlyFrustumCulling", m_EarlyFrustumCulling ? 1 : 0);
            cmb.SetComputeIntParam(m_CSSplatUtilities, Props.OpacityAwareBounds, m_OpacityAwareBounds ? 1 : 0);
            cmb.SetComputeFloatParam(m_CSSplatUtilities, Props.AlphaCutoff, Mathf.Clamp01(m_AlphaCutoff));
            cmb.SetComputeFloatParam(m_CSSplatUtilities, "_SplatNearClip", cam.nearClipPlane);
            float minimumDistance = m_EarlyRejection ? Mathf.Max(0, m_MinimumSplatDistance) : 0;
            // One center-depth check on the GPU, before covariance and frustum work.
            cmb.SetComputeFloatParam(m_CSSplatUtilities, "_SplatCullDistance", Mathf.Max(1.0e-6f, Mathf.Max(cam.nearClipPlane, minimumDistance)));
            cmb.SetComputeFloatParam(m_CSSplatUtilities, "_MinimumSplatOpacity", MinimumOpacity);
            cmb.SetComputeFloatParam(m_CSSplatUtilities, Props.SplatScale, m_SplatScale);
            cmb.SetComputeFloatParam(m_CSSplatUtilities, Props.SplatOpacityScale, m_OpacityScale);
            cmb.SetComputeIntParam(m_CSSplatUtilities, Props.SHOrder, m_SHOrder);
            cmb.SetComputeIntParam(m_CSSplatUtilities, Props.SHOnly, m_SHOnly ? 1 : 0);
            cmb.SetComputeFloatParam(m_CSSplatUtilities, Props.MinimumSplatRadiusPixels, (m_EarlyRejection ? Mathf.Max(0, m_MinimumSplatRadiusPixels) : 0));
            m_CSSplatUtilities.GetKernelThreadGroupSizes(kernel, out uint groupSize, out _, out _);
            if (mode == StereoViewMode.PerEye)
            {
                for (int eye = 0; eye < resources.ViewCount; ++eye)
                {
                    BindViewEye(cmb, eye == 0 ? state.View : state.RightView,
                        eye == 0 ? state.Projection : state.RightProjection,
                        eye == 0 ? state.ScreenSize : state.RightScreenSize, objectToWorld, false);
                    cmb.SetComputeIntParam(m_CSSplatUtilities, Props.ViewDataOffset, eye * m_SplatCount);
                    cmb.DispatchCompute(m_CSSplatUtilities, kernel, (m_SplatCount + (int)groupSize - 1) / (int)groupSize, 1, 1);
                }
            }
            else
            {
                BindViewEye(cmb, state.View, state.Projection, state.ScreenSize, objectToWorld, false);
                BindViewEye(cmb, state.RightView, state.RightProjection, state.RightScreenSize, objectToWorld, true);
                cmb.DispatchCompute(m_CSSplatUtilities, kernel, (m_SplatCount + (int)groupSize - 1) / (int)groupSize,
                    1, mode == StereoViewMode.EyeParallel ? 2 : 1);
            }
            ref var stats = ref resources.PreparationStats;
            stats.RequestedMode = m_StereoViewMode;
            stats.EffectiveMode = mode;
            stats.Kernel = diagnostics ? "CSCalcViewDataDiagnostics" : ViewKernelName(mode);
            stats.ViewCount = resources.ViewCount;
            stats.GroupSize = (int)groupSize;
            stats.LastFrame = Time.frameCount;
            ++stats.Preparations;
            stats.Dispatches += mode == StereoViewMode.PerEye ? resources.ViewCount : 1;
            stats.FallbackReason = m_StereoViewMode == StereoViewMode.Automatic ? "Automatic retains PerEye pending device measurements" :
                m_StereoViewMode == mode ? "" : diagnostics ? "Mono diagnostics" :
                state.ViewCount != 2 ? "Pass has one view" : "Stereo kernel unavailable";
            if (m_StereoViewMode != StereoViewMode.Automatic && m_StereoViewMode != mode) ++stats.Fallbacks;
        }

        static readonly int s_RightMV = Shader.PropertyToID("_StereoRightMatrixMV");
        static readonly int s_RightMVP = Shader.PropertyToID("_StereoRightMatrixMVP");
        static readonly int s_RightProjection = Shader.PropertyToID("_StereoRightProjection");
        static readonly int s_RightScreen = Shader.PropertyToID("_StereoRightScreen");
        static readonly int s_RightCamera = Shader.PropertyToID("_StereoRightCamera");

        void BindViewEye(CommandBuffer cmd, Matrix4x4 view, Matrix4x4 projection, Vector2Int size,
            Matrix4x4 objectToWorld, bool right)
        {
            projection = GL.GetGPUProjectionMatrix(projection, true);
            cmd.SetComputeVectorParam(m_CSSplatUtilities, right ? s_RightScreen : Props.VecScreenParams, new Vector4(size.x, size.y, 0, 0));
            cmd.SetComputeMatrixParam(m_CSSplatUtilities, right ? s_RightMV : Props.MatrixMV, view * objectToWorld);
            cmd.SetComputeMatrixParam(m_CSSplatUtilities, right ? s_RightMVP : Props.MatrixMVP, projection * view * objectToWorld);
            cmd.SetComputeMatrixParam(m_CSSplatUtilities, right ? s_RightProjection : Props.ProjectionMatrix, projection);
            cmd.SetComputeVectorParam(m_CSSplatUtilities, right ? s_RightCamera : Props.VecWorldSpaceCameraPos, view.inverse.GetColumn(3));
        }

        ComputeShader m_StereoKernelShader;
        int m_EyeParallelKernel = -1, m_SharedSourceKernel = -1;
        internal StereoViewMode ResolveStereoViewMode(in GaussianSplatCameraState state, bool diagnostics)
        {
            if (diagnostics || state.ViewCount != 2 ||
                (m_StereoViewMode != StereoViewMode.EyeParallel && m_StereoViewMode != StereoViewMode.SharedSource))
                return StereoViewMode.PerEye;
            if (m_StereoKernelShader != m_CSSplatUtilities)
            {
                m_StereoKernelShader = m_CSSplatUtilities;
                m_EyeParallelKernel = FindViewKernel("CSCalcViewDataStereoEyes");
                m_SharedSourceKernel = FindViewKernel("CSCalcViewDataStereoShared");
            }
            int kernel = ViewKernel(m_StereoViewMode);
            return kernel >= 0 && m_CSSplatUtilities.IsSupported(kernel) ? m_StereoViewMode : StereoViewMode.PerEye;
        }
        int FindViewKernel(string name) => m_CSSplatUtilities && m_CSSplatUtilities.HasKernel(name) ? m_CSSplatUtilities.FindKernel(name) : -1;
        int ViewKernel(StereoViewMode mode) => mode == StereoViewMode.EyeParallel ? m_EyeParallelKernel :
            mode == StereoViewMode.SharedSource ? m_SharedSourceKernel : (int)KernelIndices.CalcViewData;
        static string ViewKernelName(StereoViewMode mode) => mode == StereoViewMode.EyeParallel ? "CSCalcViewDataStereoEyes" :
            mode == StereoViewMode.SharedSource ? "CSCalcViewDataStereoShared" : "CSCalcViewData";

        // Enqueued work only, not GPU execution timings. Per-camera so other passes cannot replace benchmark evidence.
        internal struct ViewPreparationStats
        {
            public StereoViewMode RequestedMode, EffectiveMode;
            public string Kernel, FallbackReason;
            public int ViewCount, GroupSize, LastFrame;
            public long Preparations, Dispatches, CacheHits, Fallbacks;
        }
        internal bool TryGetViewPreparationStats(Camera camera, out ViewPreparationStats stats)
        {
            if (camera && m_CameraRenderResources.TryGetValue(camera, out var resources) && resources.PreparationStats.Kernel != null)
            { stats = resources.PreparationStats; return true; }
            stats = default; return false;
        }
        internal void ResetViewPreparationStats(Camera camera)
        {
            if (camera && m_CameraRenderResources.TryGetValue(camera, out var resources))
            {
                ref var stats = ref resources.PreparationStats;
                stats.Preparations = stats.Dispatches = stats.CacheHits = stats.Fallbacks = 0;
            }
        }

        internal void SetCameraProperties(MaterialPropertyBlock properties, Camera camera, CameraRenderResources resources)
        {
            properties.SetInt(Props.SplatViewCount, resources.ViewCount);
            properties.SetInt(Props.SplatEyeIndex, GaussianSplatCameraState.ForCamera(camera).EyeIndex);
        }

        internal GraphicsBuffer GetIndirectArgs(CameraRenderResources resources)
        {
            if (!UseCompaction(resources)) return null;
            resources.EnsureCompactionBuffers();
            return resources.CompactArgs;
        }

        internal void PrepareCamera(CommandBuffer cmd, Camera camera, bool backToFront, CameraRenderResources resources)
        {
            bool prepare = ShouldPrepareViewForCamera(camera, resources);
            bool sort = ShouldSortForCamera(camera, backToFront, Time.frameCount, resources);
            if (UseCompaction(resources))
            {
                if (prepare)
                {
                    cmd.BeginSample(GaussianSplat3DRenderSystem.s_ProfCalcView);
                    CalcViewData(cmd, camera, resources);
                    cmd.EndSample(GaussianSplat3DRenderSystem.s_ProfCalcView);
                }
                // Rebuilding IDs invalidates the previous order, including on skipped cadence frames.
                if (prepare || sort || !resources.Compacted)
                    CompactAndSort(cmd, camera, backToFront, resources);
                resources.Compacted = true;
            }
            else
            {
                if (resources.Compacted)
                {
                    resources.SortKeysInitialized = false;
                    resources.Compacted = false;
                    sort = true;
                }
                if (sort) SortPoints(cmd, camera, transform.localToWorldMatrix, backToFront, resources);
                if (prepare)
                {
                    cmd.BeginSample(GaussianSplat3DRenderSystem.s_ProfCalcView);
                    CalcViewData(cmd, camera, resources);
                    cmd.EndSample(GaussianSplat3DRenderSystem.s_ProfCalcView);
                }
            }
            ProbeVisibility(cmd, camera, resources);
        }

        void CountVisible(CommandBuffer cmd, Camera camera, CameraRenderResources resources)
        {
            resources.EnsureCompactionBuffers();
            int countKernel = m_CSSplatUtilities.FindKernel("CSCountVisibleGroups");
            int scanKernel = m_CSSplatUtilities.FindKernel("CSScanVisibleGroups");
            int groups = resources.CompactGroups.count;
            cmd.SetComputeIntParam(m_CSSplatUtilities, Props.SplatCount, m_SplatCount);
            cmd.SetComputeIntParam(m_CSSplatUtilities, "_CompactViewCount", resources.ViewCount);
            cmd.SetComputeIntParam(m_CSSplatUtilities, "_CompactGroupCount", groups);
            // Unity doubles ordinary procedural instance counts for SPI; indirect counts are GPU-owned.
            int instances = GaussianSplatCameraState.ForCamera(camera).IndirectInstanceMultiplier;
            cmd.SetComputeIntParam(m_CSSplatUtilities, "_CompactInstanceMultiplier", instances);
            cmd.SetComputeBufferParam(m_CSSplatUtilities, countKernel, Props.SplatViewData, resources.GpuView);
            cmd.SetComputeBufferParam(m_CSSplatUtilities, countKernel, "_CompactGroups", resources.CompactGroups);
            cmd.DispatchCompute(m_CSSplatUtilities, countKernel, groups, 1, 1);
            cmd.SetComputeBufferParam(m_CSSplatUtilities, scanKernel, "_CompactGroups", resources.CompactGroups);
            cmd.SetComputeBufferParam(m_CSSplatUtilities, scanKernel, "_CompactArgs", resources.CompactArgs);
            cmd.DispatchCompute(m_CSSplatUtilities, scanKernel, 1, 1, 1);

        }

        void ProbeVisibility(CommandBuffer cmd, Camera camera, CameraRenderResources resources)
        {
            if (m_OptimizationOverrides || m_CompactionThreshold <= 0 || m_CompactionThreshold > m_SplatCount ||
                !SystemInfo.supportsAsyncGPUReadback || resources.VisibilityReadbackPending ||
                Time.frameCount < resources.NextVisibilityProbeFrame || camera.cameraType == CameraType.Preview) return;
            // Compacted frames already have a current union count; otherwise sample at low cadence.
            if (!resources.Compacted) CountVisible(cmd, camera, resources);
            resources.VisibilityReadbackPending = true;
            resources.NextVisibilityProbeFrame = Time.frameCount + 16;
            int generation = resources.VisibilityGeneration;
            uint population = (uint)m_SplatCount;
            cmd.RequestAsyncReadback(resources.CompactArgs, sizeof(uint), 0, request =>
            {
                // The camera/asset may have been destroyed or resized while this request was in flight.
                if (resources.VisibilityGeneration != generation) return;
                resources.VisibilityReadbackPending = false;
                if (request.hasError) return;
                uint visible = request.GetData<uint>()[0];
                resources.RejectedSplats = population - Math.Min(population, visible);
                resources.HasVisibilityEstimate = true;
            });
        }

        internal void CompactAndSort(CommandBuffer cmd, Camera camera, bool backToFront, CameraRenderResources resources)
        {
            resources.EnsureCompactionBuffers();
            cmd.BeginSample("GaussianSplat.CompactVisible");
            CountVisible(cmd, camera, resources);
            int scatterKernel = m_CSSplatUtilities.FindKernel("CSCompactVisible");
            int groups = resources.CompactGroups.count;

            GaussianSplatCameraState.ForCamera(camera).GetSharedMatrices(out Matrix4x4 view, out _);
            view.m20 *= -1; view.m21 *= -1; view.m22 *= -1;
            BindSource(cmd, m_CSSplatUtilities, scatterKernel);
            cmd.SetComputeBufferParam(m_CSSplatUtilities, scatterKernel, Props.SplatViewData, resources.GpuView);
            cmd.SetComputeBufferParam(m_CSSplatUtilities, scatterKernel, "_CompactGroups", resources.CompactGroups);
            bool alternate = sortKeyBits == 24;
            cmd.SetComputeBufferParam(m_CSSplatUtilities, scatterKernel, Props.SplatSortDistances,
                alternate ? resources.SorterArgs.resources.altBuffer : resources.GpuSortDistances);
            cmd.SetComputeBufferParam(m_CSSplatUtilities, scatterKernel, Props.SplatSortKeys,
                alternate ? resources.SorterArgs.resources.altPayloadBuffer : resources.GpuSortKeys);
            cmd.SetComputeBufferParam(m_CSSplatUtilities, scatterKernel, Props.SplatChunks, m_GpuChunks);
            cmd.SetComputeBufferParam(m_CSSplatUtilities, scatterKernel, Props.SplatPos, m_GpuPosData);
            cmd.SetComputeIntParam(m_CSSplatUtilities, Props.SplatFormat, (int)m_Asset.posFormat);
            cmd.SetComputeIntParam(m_CSSplatUtilities, Props.SplatChunkCount, m_GpuChunksValid ? m_GpuChunks.count : 0);
            cmd.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixMV, view * transform.localToWorldMatrix);
            cmd.SetComputeIntParam(m_CSSplatUtilities, Props.SortDescending, backToFront ? 1 : 0);
            cmd.DispatchCompute(m_CSSplatUtilities, scatterKernel, groups, 1, 1);
            cmd.EndSample("GaussianSplat.CompactVisible");
            cmd.BeginSample(s_ProfSort);
            ++resources.SortDispatchCount;
            m_Sorter.Dispatch(cmd, resources.SorterArgs, sortKeyBits, resources.CompactArgs);
            cmd.EndSample(s_ProfSort);
        }

        internal void SortPoints(CommandBuffer cmd, Camera cam, Matrix4x4 matrix, bool backToFront,
            CameraRenderResources cameraResources)
        {
            ++cameraResources.SortDispatchCount;
            if (cam.cameraType == CameraType.Preview)
                return;

            GaussianSplatCameraState.ForCamera(cam).GetSharedMatrices(out Matrix4x4 worldToCamMatrix, out _);
            worldToCamMatrix.m20 *= -1;
            worldToCamMatrix.m21 *= -1;
            worldToCamMatrix.m22 *= -1;

            cmd.BeginSample(s_ProfSort);
            if (!cameraResources.SortKeysInitialized)
            {
                cmd.SetComputeBufferParam(m_CSSplatUtilities, (int)KernelIndices.SetIndices, Props.SplatSortKeys,
                    cameraResources.GpuSortKeys);
                cmd.SetComputeIntParam(m_CSSplatUtilities, Props.SplatCount, cameraResources.GpuSortKeys.count);
                m_CSSplatUtilities.GetKernelThreadGroupSizes((int)KernelIndices.SetIndices, out uint initGroupSize, out _, out _);
                cmd.DispatchCompute(m_CSSplatUtilities, (int)KernelIndices.SetIndices,
                    (cameraResources.GpuSortKeys.count + (int)initGroupSize - 1) / (int)initGroupSize, 1, 1);
                cameraResources.SortKeysInitialized = true;
            }

            BindSource(cmd, m_CSSplatUtilities, (int)KernelIndices.CalcDistances);
            bool alternateInput = sortKeyBits == 24;
            cmd.SetComputeIntParam(m_CSSplatUtilities, "_SortKeyBits", sortKeyBits);
            cmd.SetComputeBufferParam(m_CSSplatUtilities, (int)KernelIndices.CalcDistances, "_SplatSortInput",
                alternateInput ? cameraResources.GpuSortKeys : cameraResources.SorterArgs.resources.altPayloadBuffer);
            cmd.SetComputeBufferParam(m_CSSplatUtilities, (int)KernelIndices.CalcDistances, Props.SplatSortDistances,
                alternateInput ? cameraResources.SorterArgs.resources.altBuffer : cameraResources.GpuSortDistances);
            cmd.SetComputeBufferParam(m_CSSplatUtilities, (int)KernelIndices.CalcDistances, Props.SplatSortKeys,
                alternateInput ? cameraResources.SorterArgs.resources.altPayloadBuffer : cameraResources.GpuSortKeys);
            cmd.SetComputeBufferParam(m_CSSplatUtilities, (int)KernelIndices.CalcDistances, Props.SplatChunks, m_GpuChunks);
            cmd.SetComputeBufferParam(m_CSSplatUtilities, (int)KernelIndices.CalcDistances, Props.SplatPos, m_GpuPosData);
            cmd.SetComputeIntParam(m_CSSplatUtilities, Props.SplatFormat, (int)m_Asset.posFormat);
            cmd.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixMV, worldToCamMatrix * matrix);
            cmd.SetComputeIntParam(m_CSSplatUtilities, Props.SplatCount, m_SplatCount);
            cmd.SetComputeIntParam(m_CSSplatUtilities, Props.SplatChunkCount, m_GpuChunksValid ? m_GpuChunks.count : 0);
            cmd.SetComputeIntParam(m_CSSplatUtilities, Props.SortDescending, backToFront ? 1 : 0);
            m_CSSplatUtilities.GetKernelThreadGroupSizes((int)KernelIndices.CalcDistances, out uint gsX, out _, out _);
            cmd.DispatchCompute(m_CSSplatUtilities, (int)KernelIndices.CalcDistances,
                (cameraResources.GpuSortDistances.count + (int)gsX - 1)/(int)gsX, 1, 1);

            EnsureSorterAndRegister();
            m_Sorter.Dispatch(cmd, cameraResources.SorterArgs, sortKeyBits);
            cmd.EndSample(s_ProfSort);
        }
    }
}
