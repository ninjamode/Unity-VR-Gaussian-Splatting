// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using Gaussians.Core;
using Unity.Profiling;
using Unity.Profiling.LowLevel;
using UnityEngine;
using UnityEngine.Rendering;

namespace Gaussians.TwoD
{
    public partial class GaussianSplat2DRenderer
    {
        const int kGpuViewDataSize = 48;

        internal struct CameraSortState
        {
            public int LastFrame;
            public int RenderCount;
            public bool BackToFront;
            public bool HasSignature;
            public Matrix4x4 MatrixMV;
            public int RenderDataVersion;
            public int SortNthFrame;
        }

        internal struct ViewSignature : IEquatable<ViewSignature>
        {
            public Matrix4x4 View;
            public Matrix4x4 Projection;
            public Matrix4x4 ObjectToWorld;
            public int ScreenWidth;
            public int ScreenHeight;
            public float SplatScale;
            public float OpacityScale;
            public int SHOrder;
            public bool SHOnly;
            public SmallSplatMode SmallSplatMode;
            public float SmallSplatThresholdPixels;
            public int CutoutRevision;
            public int RenderDataVersion;

            public bool Equals(ViewSignature other)
            {
                return View.Equals(other.View) &&
                       Projection.Equals(other.Projection) &&
                       ObjectToWorld.Equals(other.ObjectToWorld) &&
                       ScreenWidth == other.ScreenWidth &&
                       ScreenHeight == other.ScreenHeight &&
                       SplatScale.Equals(other.SplatScale) &&
                       OpacityScale.Equals(other.OpacityScale) &&
                       SHOrder == other.SHOrder &&
                       SHOnly == other.SHOnly &&
                       SmallSplatMode == other.SmallSplatMode &&
                       SmallSplatThresholdPixels.Equals(other.SmallSplatThresholdPixels) &&
                       CutoutRevision == other.CutoutRevision &&
                       RenderDataVersion == other.RenderDataVersion;
            }
        }

        internal sealed class CameraRenderResources : IDisposable
        {
            internal GraphicsBuffer GpuSortDistances;
            internal GraphicsBuffer GpuSortKeys;
            internal GraphicsBuffer GpuView;
            internal GpuSorting.Args SorterArgs;
            internal readonly MaterialPropertyBlock DirectMaterialProperties = new();
            internal CameraSortState SortState;
            internal ViewSignature ViewSignature;
            internal bool HasViewSignature;
            internal bool SortKeysInitialized;

            internal bool EnsureBuffers(int count, GpuSorting sorter)
            {
                if (count <= 0 || !sorter.Valid)
                    return false;
                if (GpuView != null && GpuView.count == count)
                {
                    if (SorterArgs.resources.altBuffer == null)
                        SorterArgs.resources = GpuSorting.SupportResources.Load((uint)count);
                    return true;
                }

                DisposeBuffers();
                GpuView = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, kGpuViewDataSize)
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
            }
        }

        readonly Dictionary<Camera, CameraRenderResources> m_CameraRenderResources = new();

        readonly List<Camera> m_DestroyedCameraResources = new();

        static readonly ProfilerMarker s_ProfSort = new(ProfilerCategory.Render, "Gaussians.2D.Sort", MarkerFlags.SampleGPU);

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
            return resources.EnsureBuffers(m_SplatCount, m_Sorter) ? resources : null;
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
                if (!kvp.Key)
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
            GaussianSplatCameraState.ForCamera(cam).GetSharedMatrices(out Matrix4x4 view, out Matrix4x4 projection);
            Matrix4x4 objectToWorld = transform.localToWorldMatrix;
            var state = GaussianSplatCameraState.ForCamera(cam);
            m_CutoutBuffer.Refresh(m_Cutouts, objectToWorld);
            return new ViewSignature
            {
                View = view,
                Projection = projection,
                ObjectToWorld = objectToWorld,
                ScreenWidth = state.ScreenSize.x,
                ScreenHeight = state.ScreenSize.y,
                SplatScale = m_SplatScale,
                OpacityScale = m_OpacityScale,
                SHOrder = m_SHOrder,
                SHOnly = m_SHOnly,
                SmallSplatMode = m_SmallSplatMode,
                SmallSplatThresholdPixels = Mathf.Max(0.0f, m_SmallSplatThresholdPixels),
                CutoutRevision = m_CutoutBuffer.Revision,
                RenderDataVersion = m_RenderDataVersion,
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
            state.SortNthFrame = m_SortNthFrame;
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
            if (resources.HasViewSignature && resources.ViewSignature.Equals(signature))
                return false;

            resources.ViewSignature = signature;
            resources.HasViewSignature = true;
            return true;
        }

        internal void PrepareCamera(CommandBuffer cmd, Camera camera, bool backToFront, CameraRenderResources resources)
        {
            if (ShouldSortForCamera(camera, backToFront, Time.frameCount, resources))
                SortPoints(cmd, camera, transform.localToWorldMatrix, backToFront, resources);
            if (ShouldPrepareViewForCamera(camera, resources))
            {
                cmd.BeginSample(GaussianSplat2DRenderSystem.s_ProfCalcView);
                PrepareSplat2DViewData(cmd, camera, resources);
                cmd.EndSample(GaussianSplat2DRenderSystem.s_ProfCalcView);
            }
        }

        internal void PrepareSplat2DViewData(CommandBuffer cmb, Camera cam, CameraRenderResources cameraResources)
        {
            if (cam.cameraType == CameraType.Preview)
                return;

            var tr = transform;

            GaussianSplatCameraState.ForCamera(cam).GetSharedMatrices(out Matrix4x4 matView, out Matrix4x4 cameraProjection);
            Matrix4x4 matO2W = tr.localToWorldMatrix;
            Matrix4x4 matW2O = tr.worldToLocalMatrix;
            Matrix4x4 matProjection = GL.GetGPUProjectionMatrix(cameraProjection, true);
            Matrix4x4 matMVP = matProjection * matView * matO2W;
            var size = GaussianSplatCameraState.ForCamera(cam).ScreenSize;
            Vector4 screenPar = new Vector4(size.x, size.y, 0, 0);
            Vector4 camPos = matView.inverse.GetColumn(3);

            // calculate view dependent data for each splat
            SetAssetDataOnCS(cmb, KernelIndices.PrepareSplat2DViewData, cameraResources);

            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixMV, matView * matO2W);
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixMVP, matMVP);
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixObjectToWorld, matO2W);
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixWorldToObject, matW2O);

            cmb.SetComputeVectorParam(m_CSSplatUtilities, Props.VecScreenParams, screenPar);
            cmb.SetComputeVectorParam(m_CSSplatUtilities, Props.VecWorldSpaceCameraPos, camPos);
            cmb.SetComputeFloatParam(m_CSSplatUtilities, Props.SplatScale, m_SplatScale);
            cmb.SetComputeFloatParam(m_CSSplatUtilities, Props.SplatOpacityScale, m_OpacityScale);
            cmb.SetComputeIntParam(m_CSSplatUtilities, Props.SHOrder, m_SHOrder);
            cmb.SetComputeIntParam(m_CSSplatUtilities, Props.SHOnly, m_SHOnly ? 1 : 0);
            cmb.SetComputeIntParam(m_CSSplatUtilities, Props.SmallSplatMode, (int)m_SmallSplatMode);
            cmb.SetComputeFloatParam(m_CSSplatUtilities, Props.SmallSplatThresholdPixels, Mathf.Max(0.0f, m_SmallSplatThresholdPixels));

            m_CSSplatUtilities.GetKernelThreadGroupSizes((int)KernelIndices.PrepareSplat2DViewData, out uint gsX, out _, out _);
            cmb.DispatchCompute(m_CSSplatUtilities, (int)KernelIndices.PrepareSplat2DViewData, (cameraResources.GpuView.count + (int)gsX - 1)/(int)gsX, 1, 1);
        }

        internal void SortPoints(CommandBuffer cmd, Camera cam, Matrix4x4 matrix, bool backToFront,
            CameraRenderResources cameraResources)
        {
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

            cmd.SetComputeBufferParam(m_CSSplatUtilities, (int)KernelIndices.CalcDistances, Props.SplatSortDistances,
                cameraResources.GpuSortDistances);
            cmd.SetComputeBufferParam(m_CSSplatUtilities, (int)KernelIndices.CalcDistances, Props.SplatSortKeys,
                cameraResources.GpuSortKeys);
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
            m_Sorter.Dispatch(cmd, cameraResources.SorterArgs);
            cmd.EndSample(s_ProfSort);
        }
    }
}
