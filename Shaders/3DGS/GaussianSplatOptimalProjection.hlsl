// SPDX-License-Identifier: MIT
// Physical tangent plane through the per-eye center. Dividing its covariance by
// |center|^2 gives the paper's unit-sphere covariance; keeping physical units lets
// perspective-correct quad interpolation evaluate the same ray/plane mapping.
bool UsesOptimalPerspective(SplatEyeParameters eye)
{
#if defined(GAUSSIANS_OPTIMAL_PROJECTION)
    return dot(eye.projection[3].xyz, eye.projection[3].xyz) > 1.0e-12;
#else
    return false;
#endif
}

#if defined(GAUSSIANS_OPTIMAL_PROJECTION)
float3 TangentCovariance(float3 a, float3 b, float3 cov0, float3 cov1)
{
    float3x3 sigma = float3x3(cov0.x, cov0.y, cov0.z,
        cov0.y, cov1.x, cov1.y, cov0.z, cov1.y, cov1.z);
    return float3(dot(a, mul(sigma, a)), dot(a, mul(sigma, b)), dot(b, mul(sigma, b)));
}

// Upper bound on the farthest projected point of the UNFILTERED three-sigma
// ellipse, relative to the projected center. Solve its projective conic AABB
// without another eigendecomposition. This may retain extra tiny splats but
// cannot prune an ellipse whose actual radius meets the threshold.
bool OptimalBelowMinimumRadius(float3 covariance, float4 clip, float4 basis1, float4 basis2, float2 screen)
{
    float2 w = float2(basis1.w, basis2.w);
    float2 cw = float2(covariance.x * w.x + covariance.y * w.y,
                       covariance.y * w.x + covariance.z * w.y);
    float denominator = clip.w * clip.w - 9.0 * dot(w, cw);
    if (denominator <= 1.0e-12) return false; // Three-sigma support can cross the eye plane.
    float2 ndc = clip.xy / clip.w;
    float2 dx = float2(basis1.x, basis2.x) - ndc.x * w;
    float2 dy = float2(basis1.y, basis2.y) - ndc.y * w;
    float2 shift = -9.0 * float2(dot(dx, cw), dot(dy, cw)) / denominator;
    float2 variance = float2(
        covariance.x * dx.x * dx.x + 2.0 * covariance.y * dx.x * dx.y + covariance.z * dx.y * dx.y,
        covariance.x * dy.x * dy.x + 2.0 * covariance.y * dy.x * dy.y + covariance.z * dy.y * dy.y);
    float2 extent = sqrt(max(0.0, shift * shift + 9.0 * variance / denominator));
    float radiusBound = length((abs(shift) + extent) * screen * 0.5);
    return isfinite(radiusBound) && radiusBound < _MinimumSplatRadiusPixels;
}

uint PrepareOptimalProjection(float3 pos, float3 cov0, float3 cov1, half opacity,
    SplatEyeParameters eye, inout SplatViewData view)
{
    float3 center = mul(eye.matrixMV, float4(pos, 1)).xyz;
    float3 n = normalize(center);
    // No trigonometry; choose a reference axis away from the parallel case.
    float3 b1 = normalize(cross(n, abs(n.z) < 0.9 ? float3(0, 0, 1) : float3(0, 1, 0)));
    float3 b2 = cross(n, b1);
    float3 covariance = TangentCovariance(mul(b1, (float3x3)eye.matrixMV),
        mul(b2, (float3x3)eye.matrixMV), cov0, cov1);
    float4 clip1 = mul(eye.projection, float4(b1, 0));
    float4 clip2 = mul(eye.projection, float4(b2, 0));
    if (_MinimumSplatRadiusPixels > 0.0 &&
        OptimalBelowMinimumRadius(covariance, view.pos, clip1, clip2, eye.screen.xy)) return 7;

    // Pull back the original 0.3-pixel covariance filter. Adding 0.3 to tangent
    // covariance directly would mix pixels and world units and explode nearby splats.
    float2 ndc = view.pos.xy / view.pos.w;
    float2 k1 = (clip1.xy - ndc * clip1.w) / view.pos.w * eye.screen.xy * 0.5;
    float2 k2 = (clip2.xy - ndc * clip2.w) / view.pos.w * eye.screen.xy * 0.5;
    float determinant = k1.x * k2.y - k2.x * k1.y;
    if (!isfinite(determinant) || abs(determinant) <= 1.0e-12) return 8;
    float2 inverseRow1 = float2(k2.y, -k2.x) / determinant;
    float2 inverseRow2 = float2(-k1.y, k1.x) / determinant;
    covariance += 0.3 * float3(dot(inverseRow1, inverseRow1),
        dot(inverseRow1, inverseRow2), dot(inverseRow2, inverseRow2));

    float mid = 0.5 * (covariance.x + covariance.z);
    float radius = length(float2(0.5 * (covariance.x - covariance.z), covariance.y));
    float2 diagonal = float2(covariance.y, mid + radius - covariance.x);
    float2 direction = dot(diagonal, diagonal) > 1.0e-20 ? normalize(diagonal) : float2(1, 0);
    // Physical axes have no 4096-pixel cap. Near-support rejection below bounds
    // their projective behavior before division by any corner W.
    float2 axis1 = direction * sqrt(2.0 * max(mid + radius, 0.0));
    float2 axis2 = float2(-direction.y, direction.x) * sqrt(2.0 * max(mid - radius, 0.0));
    float4 projected1 = clip1 * axis1.x + clip2 * axis1.y;
    float4 projected2 = clip1 * axis2.x + clip2 * axis2.y;
    view.axis1 = projected1.xyw;
    view.axis2 = projected2.xyw;
    if (!all(isfinite(view.axis1)) || !all(isfinite(view.axis2))) return 8;

    float halfExtent = SplatQuadHalfExtent(opacity, _AlphaCutoff, _OpacityAwareBounds);
    float z1 = b1.z * axis1.x + b2.z * axis1.y;
    float z2 = b1.z * axis2.x + b2.z * axis2.y;
    float supportDepth = -center.z - halfExtent * (abs(z1) + abs(z2));
    if (supportDepth <= _SplatCullDistance)
        return supportDepth <= max(_SplatNearClip, 1.0e-6) ? 4 : 6;
    float minW = view.pos.w - halfExtent * (abs(projected1.w) + abs(projected2.w));
    if (minW <= 1.0e-6) return 9;

    if (_EarlyFrustumCulling != 0u)
    {
        float2 lo = 1.0e30, hi = -1.0e30;
        [unroll] for (uint corner = 0; corner < 4; ++corner)
        {
            float2 uv = (float2(corner & 1, (corner >> 1) & 1) * 2.0 - 1.0) * halfExtent;
            float3 quadPoint = view.pos.xyw + uv.x * view.axis1 + uv.y * view.axis2;
            float2 xy = quadPoint.xy / quadPoint.z;
            lo = min(lo, xy); hi = max(hi, xy);
        }
        // Every corner W is positive: the projective quad is convex, so its
        // corner bounds enclose the actual drawn support. One pixel guards edges.
        float2 guard = 2.0 / eye.screen.xy;
        if (all(isfinite(lo)) && all(isfinite(hi)) &&
            (any(lo > 1.0 + guard) || any(hi < -1.0 - guard))) return 11;
    }
    return 0;
}
#endif

uint PrepareSplatProjection(float3 pos, float3 cov0, float3 cov1, half opacity,
    SplatEyeParameters eye, inout SplatViewData view)
{
#if defined(GAUSSIANS_OPTIMAL_PROJECTION)
    if (UsesOptimalPerspective(eye)) return PrepareOptimalProjection(pos, cov0, cov1, opacity, eye, view);
#endif
    // Orthographic and Standard share the existing affine projection and pruning.
    float3 cov2d = CalcCovariance2D(pos, cov0, cov1, eye.matrixMV, eye.projection, eye.screen);
    if (_MinimumSplatRadiusPixels > 0.0)
    {
        float2 rawDiagonal = cov2d.xz - 0.3;
        float largestVariance = max(0.0, 0.5 * (rawDiagonal.x + rawDiagonal.y) +
            length(float2(0.5 * (rawDiagonal.x - rawDiagonal.y), cov2d.y)));
        if (3.0 * sqrt(largestVariance) < _MinimumSplatRadiusPixels) return 7;
    }
    float2 axis1, axis2;
    DecomposeCovariance(cov2d, axis1, axis2);
    if (!all(isfinite(float4(axis1, axis2)))) return 8;
#if defined(GAUSSIANS_OPTIMAL_PROJECTION)
    view.axis1 = float3(axis1 * (2.0 / eye.screen.xy) * view.pos.w, 0);
    view.axis2 = float3(axis2 * (2.0 / eye.screen.xy) * view.pos.w, 0);
#else
    view.axis1 = axis1; view.axis2 = axis2;
#endif
    return 0;
}
