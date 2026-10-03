// SPDX-License-Identifier: MIT
using UnityEngine;

namespace Gaussians.Core
{
    internal static class GaussianRenderMath
    {
        internal static Bounds TransformBounds(Matrix4x4 matrix, Bounds bounds)
        {
            Vector3 center = matrix.MultiplyPoint3x4(bounds.center);
            Vector3 extents = bounds.extents;
            Vector3 worldExtents = new(
                Mathf.Abs(matrix.m00) * extents.x + Mathf.Abs(matrix.m01) * extents.y + Mathf.Abs(matrix.m02) * extents.z,
                Mathf.Abs(matrix.m10) * extents.x + Mathf.Abs(matrix.m11) * extents.y + Mathf.Abs(matrix.m12) * extents.z,
                Mathf.Abs(matrix.m20) * extents.x + Mathf.Abs(matrix.m21) * extents.y + Mathf.Abs(matrix.m22) * extents.z);
            return new Bounds(center, worldExtents * 2.0f);
        }
    }
}
