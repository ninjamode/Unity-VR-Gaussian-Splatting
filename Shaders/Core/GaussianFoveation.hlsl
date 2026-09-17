// SPDX-License-Identifier: MIT
#ifndef GAUSSIANS_FOVEATION_INCLUDED
#define GAUSSIANS_FOVEATION_INCLUDED

// UnityCG-compatible subset of Core 17.3 FoveatedRenderingKeywords.hlsl and
// API/FoveatedRendering_Metal.hlsl. Keep this adapter package-independent: the
// Built-in pipeline is supported and does not require the SRP Core package.
// Unity supplies the keyword and per-eye map; these are compiler intrinsics,
// not a custom approximation of Apple's rasterization-rate map.
// Recheck against Unity's helpers when upgrading the supported Editor version.
#if UNITY_VERSION >= 600000 && defined(SHADER_API_METAL) && (defined(UNITY_PLATFORM_OSX) || defined(UNITY_PLATFORM_IOS) || defined(UNITY_PLATFORM_VISIONOS))
    // Metal VRR is currently not supported with DXC, independently of SPI.
    #pragma never_use_dxc metal
    #pragma dynamic_branch _ _FOVEATED_RENDERING_NON_UNIFORM_RASTER
    #if !defined(UNITY_COMPILER_DXC)
        #define GAUSSIANS_METAL_FOVEATION 1
        // HLSLcc recognizes these exact names and mad(uv, token, eye) operand
        // order and replaces them with the native per-eye Metal VRR lookup.
        float2 _UV_HlslccVRRDistort;
        float2 _UV_HlslccVRRResolve;
    #endif
#endif

float2 SplatLinearToRasterUV(float2 uv)
{
#if defined(GAUSSIANS_METAL_FOVEATION)
    UNITY_BRANCH if (_FOVEATED_RENDERING_NON_UNIFORM_RASTER)
    {
        uv.y = 1.0 - uv.y;
        uv = mad(uv, _UV_HlslccVRRResolve, unity_StereoEyeIndex);
        uv.y = 1.0 - uv.y;
    }
#endif
    return uv;
}

float2 SplatRasterToLinearUV(float2 uv)
{
#if defined(GAUSSIANS_METAL_FOVEATION)
    UNITY_BRANCH if (_FOVEATED_RENDERING_NON_UNIFORM_RASTER)
    {
        uv.y = 1.0 - uv.y;
        uv = mad(uv, _UV_HlslccVRRDistort, unity_StereoEyeIndex);
        uv.y = 1.0 - uv.y;
    }
#endif
    return uv;
}

// Surface interpolants remain valid. Only screen-space low-pass distance needs
// reconstruction on a non-uniform raster. Keeping the non-foveated interpolant
// preserves the legacy pipelines' projection handling. Color and depth call
// this same helper after UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX.
float2 SplatLowPassPixelDelta(float2 delta, float2 rasterPosition, float2 centerUV)
{
#if defined(GAUSSIANS_METAL_FOVEATION)
    UNITY_BRANCH if (_FOVEATED_RENDERING_NON_UNIFORM_RASTER)
        delta = (SplatRasterToLinearUV(rasterPosition / _ScreenParams.xy) - centerUV) * _ScreenParams.xy;
#endif
    return delta;
}

#endif
