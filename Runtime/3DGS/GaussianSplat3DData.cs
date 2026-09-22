// SPDX-License-Identifier: MIT
using System;
using UnityEngine;

namespace Gaussians.ThreeD
{
    /// <summary>Activated, object-space FP32 attributes in stable Gaussian order.
    /// Geometry is three float4s: position/opacity, scale/padding, normalized xyzw rotation.
    /// SH is coefficient-major RGB, including DC. The creator owns both buffers.</summary>
    public enum GaussianSplatSHStorage { Float32, Float16 }

    public class GaussianSplat3DData : IDisposable
    {
        public GraphicsBuffer Splats { get; private set; }
        public GraphicsBuffer SH { get; private set; }
        public GaussianSplatSHStorage SHStorage { get; }
        public static int SHWordsPerSplat(int coefficients, GaussianSplatSHStorage storage) =>
            storage == GaussianSplatSHStorage.Float16 ? (coefficients * 3 + 1) / 2 : coefficients * 3;
        public int Count { get; }
        public int CoefficientCount { get; }
        public GaussianSplat3DData(int count, int coefficientCount, GaussianSplatSHStorage storage = GaussianSplatSHStorage.Float32)
        {
            if (count <= 0 || coefficientCount is not (1 or 4 or 9 or 16))
                throw new ArgumentOutOfRangeException(nameof(count));
            SHStorage = storage;
            Count = count;
            CoefficientCount = coefficientCount;
            try
            {
                Splats = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, 48) { name = "Gaussian FP32 attributes" };
                SH = new GraphicsBuffer(GraphicsBuffer.Target.Structured, checked(count * SHWordsPerSplat(coefficientCount, storage)), 4) { name = "Gaussian SH " + storage };
            }
            catch { Dispose(); throw; }
        }
        public void Dispose()
        {
            Splats?.Dispose(); SH?.Dispose();
            Splats = null; SH = null;
        }
    }

    /// <summary>Borrowed attributes, valid until the provider is detached or prepares a new frame.
    /// Position changes invalidate sorting; all attribute changes invalidate per-eye projection.
    /// Providers must preserve Gaussian count and canonical IDs.</summary>
    public readonly struct GaussianSplat3DFrame
    {
        public readonly GaussianSplat3DData Data;
        public readonly uint PositionRevision, AttributeRevision;
        public GaussianSplat3DFrame(GaussianSplat3DData data, uint positionRevision, uint attributeRevision)
        { Data = data; PositionRevision = positionRevision; AttributeRevision = attributeRevision; }
    }

    public interface IGaussianSplat3DDeformation
    {
        bool TryGetFrame(GaussianSplat3DAsset canonical, out GaussianSplat3DFrame frame);
    }
}
