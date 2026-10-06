// SPDX-License-Identifier: MIT
// View-only kernels; source formats and sorting remain unchanged.
uint _ViewDataBase, _ViewDataStride;
struct SplatEyeParameters
{
    float4x4 matrixMV, matrixMVP, projection;
    float4 screen, worldCamera;
};
float4x4 _StereoRightMatrixMV, _StereoRightMatrixMVP, _StereoRightProjection;
float4 _StereoRightScreen, _StereoRightCamera;

SplatEyeParameters GetSplatEye(uint eyeIndex)
{
    SplatEyeParameters eye;
    if (eyeIndex == 0)
    {
        eye.matrixMV = _MatrixMV; eye.matrixMVP = _MatrixMVP;
        eye.projection = _ProjectionMatrix; eye.screen = _VecScreenParams;
        eye.worldCamera = _VecWorldSpaceCameraPos;
    }
    else
    {
        eye.matrixMV = _StereoRightMatrixMV; eye.matrixMVP = _StereoRightMatrixMVP;
        eye.projection = _StereoRightProjection; eye.screen = _StereoRightScreen;
        eye.worldCamera = _StereoRightCamera;
    }
    return eye;
}

// Fail open for uncertain projections. Bounds are in pixels around the clip center.
// One pixel of guard also covers edge rasterization and small arithmetic differences.
bool OutsideSplatViewport(float4 clip, float2 extent, SplatEyeParameters eye)
{
    if (clip.w <= 1.0e-6 || !all(isfinite(clip)) || !all(isfinite(extent))) return false;
    float2 distance = (abs(clip.xy / clip.w) - 1.0) * eye.screen.xy * 0.5;
    return any(distance > extent * 1.001 + 1.0);
}

bool EarlyOutsideSplatViewport(float3 pos, float3x3 rotScale, float4 centerClip, SplatEyeParameters eye)
{
    // In-view centers cannot be rejected; avoid extra matrix work for these.
    if (centerClip.w <= 1.0e-6 || all(abs(centerClip.xy) <= centerClip.w)) return false;
    // Match CalcCovariance2D's clamped Jacobian, including asymmetric/orthographic
    // projections. Work on current (already deformed) attributes, never stale visibility.
    float4 clip = mul(eye.projection, mul(eye.matrixMV, float4(pos, 1)));
    if (clip.w <= 1.0e-6 || !all(isfinite(clip))) return false;
    float2 ndc = clamp(clip.xy / clip.w, -1.3, 1.3);
    float3 jx = (eye.projection[0].xyz - ndc.x * eye.projection[3].xyz) / clip.w * eye.screen.x * 0.5;
    float3 jy = (eye.projection[1].xyz - ndc.y * eye.projection[3].xyz) / clip.w * eye.screen.y * 0.5;
    float3 rx = mul(mul(jx, (float3x3)eye.matrixMV), rotScale) * _SplatScale;
    float3 ry = mul(mul(jy, (float3x3)eye.matrixMV), rotScale) * _SplatScale;
    float2 diagonal = float2(dot(rx, rx), dot(ry, ry)) + 0.3;
    // For axes sqrt(2*lambda)*eigenvector and half-extent 2, Cauchy-Schwarz
    // gives quad AABB <= 4*sqrt(diagonal). Do not assume a 3-sigma ellipse:
    // the rasterized square extends beyond it. Inflate for cancellation in the
    // full covariance path; axis clamping can only make its footprint smaller.
    float errorGuard = (diagonal.x + diagonal.y) * 1.0e-4;
    return OutsideSplatViewport(centerClip, 4.0 * sqrt(diagonal + errorGuard), eye);
}

#include "GaussianSplatOptimalProjection.hlsl"

void CalculateViewDataForEye(uint idx, SplatEyeParameters eye, uint outputOffset)
{
    if (idx >= _SplatCount)
        return;

    BENCHMARK_COUNT(0);
    SplatViewData view = (SplatViewData)0;
    // Rejected records overwrite the previous view; valid records are written once.
#define REJECT_SPLAT_VIEW(reason) { BENCHMARK_COUNT(reason); _SplatViewData[outputOffset + idx] = (SplatViewData)0; return; }
    bool selected = false;
    if (_SplatBitsValid)
    {
        uint mask = 1u << (idx & 31u);
        if ((_SplatDeletedBits.Load((idx / 32u) * 4u) & mask) != 0u) REJECT_SPLAT_VIEW(1)
        selected = (_SplatSelectedBits.Load((idx / 32u) * 4u) & mask) != 0u;
    }
    // Float sources are already deformed by the source provider at this point.
    // Optional deferred SH allows an independent A/B comparison.
    SplatData splat = LoadSplatData(idx, _DeferredSHLoading == 0);
    if (!all(isfinite(float4(splat.pos, splat.opacity))) ||
        !all(isfinite(splat.scale)) || !all(isfinite(splat.rot))) REJECT_SPLAT_VIEW(2)
    if (IsSplatCut(splat.pos)) REJECT_SPLAT_VIEW(3)
    float depth = -mul(eye.matrixMV, float4(splat.pos, 1)).z;
    // CPU combines camera near plane and optional minimum center depth. The reason
    // expression compiles out outside diagnostics, leaving one depth comparison.
    if (depth <= _SplatCullDistance)
        REJECT_SPLAT_VIEW(depth <= max(_SplatNearClip, 1.0e-6) ? 4 : 6)
    half effectiveOpacity = min(splat.opacity * (half)_SplatOpacityScale, 65000);
    if (!selected && (effectiveOpacity <= 0 || effectiveOpacity < _MinimumSplatOpacity)) REJECT_SPLAT_VIEW(5)
    float3x3 splatRotScaleMat = CalcMatrixFromRotationScale(splat.rot, splat.scale);

    float3 centerWorldPos = mul(_MatrixObjectToWorld, float4(splat.pos,1)).xyz;
    float4 centerClipPos = mul(eye.matrixMVP, float4(splat.pos, 1));
    float splatScale = _SplatScale;

    view.pos = centerClipPos;
    if (centerClipPos.w > 0)
    {

        if (!UsesOptimalPerspective(eye) && _EarlyFrustumCulling != 0u && EarlyOutsideSplatViewport(splat.pos, splatRotScaleMat, centerClipPos, eye))
            REJECT_SPLAT_VIEW(11)

        float3 cov3d0, cov3d1;
        CalcCovariance3D(splatRotScaleMat, cov3d0, cov3d1);
        float splatScale2 = splatScale * splatScale;
        cov3d0 *= splatScale2;
        cov3d1 *= splatScale2;
        uint projectionReason = PrepareSplatProjection(splat.pos, cov3d0, cov3d1,
            selected ? (half)-1 : effectiveOpacity, eye, view);
        if (projectionReason != 0u) REJECT_SPLAT_VIEW(projectionReason)

        if (_DeferredSHLoading != 0) splat.sh = LoadSplatSH(idx, splat.sh.col);

        float3 worldViewDir = eye.worldCamera.xyz - centerWorldPos;
        float3 objViewDir = mul((float3x3)_MatrixWorldToObject, worldViewDir);
        objViewDir = normalize(objViewDir);

        half4 col;
        col.rgb = ShadeSH(splat.sh, objViewDir, _SHOrder, _SHOnly != 0);
        col.a = effectiveOpacity;
        view.color.x = (f32tof16(col.r) << 16) | f32tof16(col.g);
        view.color.y = (f32tof16(col.b) << 16) | f32tof16(col.a);
        BENCHMARK_COUNT(10);
    }
    else { BENCHMARK_COUNT(9); }
    
    _SplatViewData[outputOffset + idx] = view;
#undef REJECT_SPLAT_VIEW
}

// No cross-eye synchronization: an invocation owns both output records.
// False can retain a nonpositive clip-W record, matching the reference kernel.
bool BeginStereoEye(float3 pos, float3x3 rotScale, SplatEyeParameters eye, out SplatViewData view)
{
    view = (SplatViewData)0;
    float depth = -mul(eye.matrixMV, float4(pos, 1)).z;
    if (depth <= _SplatCullDistance) return false;
    view.pos = mul(eye.matrixMVP, float4(pos, 1));
    if (view.pos.w <= 0) return false;
    if (!UsesOptimalPerspective(eye) && _EarlyFrustumCulling != 0u && EarlyOutsideSplatViewport(pos, rotScale, view.pos, eye))
    { view = (SplatViewData)0; return false; }
    return true;
}

bool FinishStereoEye(float3 pos, float3 cov3d0, float3 cov3d1, half opacity, SplatEyeParameters eye, inout SplatViewData view)
{
    if (PrepareSplatProjection(pos, cov3d0, cov3d1, opacity, eye, view) != 0u)
    { view = (SplatViewData)0; return false; }
    return true;
}

void ShadeStereoEye(SplatSHData sh, float3 centerWorldPos, half opacity, SplatEyeParameters eye, inout SplatViewData view)
{
    float3 worldViewDir = eye.worldCamera.xyz - centerWorldPos;
    float3 objViewDir = mul((float3x3)_MatrixWorldToObject, worldViewDir);
    objViewDir = normalize(objViewDir);
    half4 col;
    col.rgb = ShadeSH(sh, objViewDir, _SHOrder, _SHOnly != 0);
    col.a = opacity;
    view.color.x = (f32tof16(col.r) << 16) | f32tof16(col.g);
    view.color.y = (f32tof16(col.b) << 16) | f32tof16(col.a);
}

[numthreads(GROUP_SIZE,1,1)]
void CSCalcViewDataStereoShared(uint3 id : SV_DispatchThreadID)
{
    uint idx = id.x;
    if (idx >= _SplatCount) return;
#define REJECT_STEREO { _SplatViewData[_ViewDataBase + idx] = (SplatViewData)0; _SplatViewData[_ViewDataBase + _ViewDataStride + idx] = (SplatViewData)0; return; }
    bool selected = false;
    if (_SplatBitsValid)
    {
        uint mask = 1u << (idx & 31u);
        if ((_SplatDeletedBits.Load((idx / 32u) * 4u) & mask) != 0u) REJECT_STEREO
        selected = (_SplatSelectedBits.Load((idx / 32u) * 4u) & mask) != 0u;
    }
    SplatData splat = LoadSplatData(idx, _DeferredSHLoading == 0);
    if (!all(isfinite(float4(splat.pos, splat.opacity))) ||
        !all(isfinite(splat.scale)) || !all(isfinite(splat.rot))) REJECT_STEREO
    if (IsSplatCut(splat.pos)) REJECT_STEREO
    half effectiveOpacity = min(splat.opacity * (half)_SplatOpacityScale, 65000);
    if (!selected && (effectiveOpacity <= 0 || effectiveOpacity < _MinimumSplatOpacity)) REJECT_STEREO

    SplatEyeParameters left = GetSplatEye(0), right = GetSplatEye(1);
    // Reject both near-clipped eyes before constructing covariance/rotation data.
    if (-mul(left.matrixMV, float4(splat.pos, 1)).z <= _SplatCullDistance &&
        -mul(right.matrixMV, float4(splat.pos, 1)).z <= _SplatCullDistance) REJECT_STEREO
    float3x3 rotScale = CalcMatrixFromRotationScale(splat.rot, splat.scale);
    SplatViewData leftView, rightView;
    bool shadeLeft = BeginStereoEye(splat.pos, rotScale, left, leftView);
    bool shadeRight = BeginStereoEye(splat.pos, rotScale, right, rightView);
    if (shadeLeft || shadeRight)
    {
        float3 cov3d0, cov3d1;
        CalcCovariance3D(rotScale, cov3d0, cov3d1);
        float scale2 = _SplatScale * _SplatScale;
        cov3d0 *= scale2; cov3d1 *= scale2;
        if (shadeLeft) shadeLeft = FinishStereoEye(splat.pos, cov3d0, cov3d1, selected ? (half)-1 : effectiveOpacity, left, leftView);
        if (shadeRight) shadeRight = FinishStereoEye(splat.pos, cov3d0, cov3d1, selected ? (half)-1 : effectiveOpacity, right, rightView);
        if (shadeLeft || shadeRight)
        {
            if (_DeferredSHLoading != 0) splat.sh = LoadSplatSH(idx, splat.sh.col);
            float3 centerWorldPos = mul(_MatrixObjectToWorld, float4(splat.pos, 1)).xyz;
            if (shadeLeft) ShadeStereoEye(splat.sh, centerWorldPos, effectiveOpacity, left, leftView);
            if (shadeRight) ShadeStereoEye(splat.sh, centerWorldPos, effectiveOpacity, right, rightView);
        }
    }
    _SplatViewData[_ViewDataBase + idx] = leftView;
    _SplatViewData[_ViewDataBase + _ViewDataStride + idx] = rightView;
#undef REJECT_STEREO
}

[numthreads(GROUP_SIZE,1,1)]
void CSCalcViewData(uint3 id : SV_DispatchThreadID)
{ CalculateViewDataForEye(id.x, GetSplatEye(0), _ViewDataBase); }
[numthreads(GROUP_SIZE,1,1)]
void CSCalcViewDataDiagnostics(uint3 id : SV_DispatchThreadID)
{ CalculateViewDataForEye(id.x, GetSplatEye(0), _ViewDataBase); }
#undef BENCHMARK_COUNT
