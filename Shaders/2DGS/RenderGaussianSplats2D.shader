// SPDX-License-Identifier: MIT
// Intentionally omit #pragma use_dxc: in Unity 6000.3.23f1, Metal single-pass
// instancing fails because Unity's DXC/stereo macros assign a uniform eye index.
// This appears to be a Unity compiler/macro compatibility bug. Upstream reported
// faster Metal rendering with DXC; investigate re-enabling it after verifying
// stereo compilation, both eyes on device, and an actual performance benefit.
Shader "Gaussians/2D/Render Splats"
{
    Properties
    {
        [HideInInspector] _SrcBlend ("Source Blend", Float) = 8
        [HideInInspector] _DstBlend ("Destination Blend", Float) = 1
    }

    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" }

        CGINCLUDE
#include "UnityCG.cginc"
#include "GaussianSplat2D.hlsl"
#include_with_pragmas "../Core/GaussianFoveation.hlsl"

#if defined(UNITY_INSTANCING_ENABLED) || defined(UNITY_PROCEDURAL_INSTANCING_ENABLED) || defined(UNITY_STEREO_INSTANCING_ENABLED)
    #define TWO_DGS_UNITY_INSTANCING_ENABLED 1
#endif

StructuredBuffer<uint> _OrderBuffer;

struct v2f
{
    half4 col : COLOR0;
    float2 localUV : TEXCOORD0;
    noperspective float2 pixelDelta : TEXCOORD1;
    nointerpolation uint flags : TEXCOORD2;
    nointerpolation float2 centerUV : TEXCOORD3;
    float4 vertex : SV_POSITION;
    UNITY_VERTEX_OUTPUT_STEREO
};

struct appdata
{
    uint vertexID : SV_VertexID;
#if defined(TWO_DGS_UNITY_INSTANCING_ENABLED)
    UNITY_VERTEX_INPUT_INSTANCE_ID
#else
    // Procedural draws still need SV_InstanceID in the non-instanced shader variant.
    uint instanceID : SV_InstanceID;
#endif
};

StructuredBuffer<SplatViewData2D> _SplatViewData;
ByteAddressBuffer _SplatSelectedBits;
uint _SplatBitsValid;
float4x4 _MatrixObjectToWorld;
uint _ConvertGammaToLinear;
float _AlphaCutoff;

float4 SplatObjectToClipPos(float3 positionOS)
{
    // Explicit object matrix avoids indexing Unity's per-instance transform array
    // with a splat ID: all splats in this draw share one object transform.
    return mul(UNITY_MATRIX_VP, mul(_MatrixObjectToWorld, float4(positionOS, 1.0)));
}

v2f vert (appdata input)
{
    v2f o = (v2f)0;
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

    uint splatDrawInstance = input.instanceID;
#if defined(TWO_DGS_UNITY_INSTANCING_ENABLED)
    // Unity removes the stereo-eye bits (and applies the base instance) here.
    splatDrawInstance = unity_InstanceID;
#endif
    uint instID = _OrderBuffer[splatDrawInstance];
    SplatViewData2D view = _SplatViewData[instID];
    o.flags = GetSplatViewFlags(view);
    if ((o.flags & SPLAT_VIEW_FLAG_INVALID) != 0u)
    {
        o.vertex = asfloat(0x7fc00000); // NaN discards the primitive
        return o;
    }

    o.col.r = f16tof32(view.colorRG >> 16);
    o.col.g = f16tof32(view.colorRG);
    o.col.b = f16tof32(view.colorBA >> 16);
    o.col.a = f16tof32(view.colorBA);
    if (_ConvertGammaToLinear != 0u)
        o.col.rgb = GammaToLinearSpace(o.col.rgb);

    uint vtxID = input.vertexID;
    float2 corner = float2(vtxID & 1, (vtxID >> 1) & 1) * 2.0 - 1.0;
    float4 centerClipPos = SplatObjectToClipPos(view.centerOS);
    if ((o.flags & SPLAT_VIEW_FLAG_LOW_PASS) != 0u)
    {
        // exp(-|pixelDelta|^2) reaches the same three-sigma truncation at 3/sqrt(2).
        const float kLowPassHalfExtentPixels = 2.12132034356;
        float4 centerScreen = ComputeScreenPos(centerClipPos);
        o.centerUV = centerScreen.xy / centerScreen.w;
        o.pixelDelta = corner * kLowPassHalfExtentPixels;
        o.vertex = centerClipPos;
        o.vertex.xy += o.pixelDelta * (2.0 / _ScreenParams.xy) * centerClipPos.w;
    }
    else
    {
        o.localUV = corner * 3.0;
        float3 vertexOS = view.centerOS + view.tangentUOS * o.localUV.x + view.tangentVOS * o.localUV.y;
        o.vertex = SplatObjectToClipPos(vertexOS);
    }

    // is this splat selected?
    if (_SplatBitsValid)
    {
        uint wordIdx = instID / 32;
        uint bitIdx = instID & 31;
        uint selVal = _SplatSelectedBits.Load(wordIdx * 4);
        if (selVal & (1 << bitIdx))
            o.col.a = -1;
    }

#if !defined(GAUSSIANS_DIRECT_TRANSPARENT)
    // The composite path is a custom command-buffer draw and needs the inherited
    // BiRP backbuffer correction. Unity handles this for normal queued renderers.
    FlipProjectionIfBackbuffer(o.vertex);
#endif
    return o;
}

half EvaluateSplatAlpha(inout v2f i)
{
    float rho = dot(i.localUV, i.localUV);
    if ((i.flags & SPLAT_VIEW_FLAG_LOW_PASS) != 0u)
    {
        float2 pixelDelta = SplatLowPassPixelDelta(i.pixelDelta, i.vertex.xy, i.centerUV);
        rho = 2.0 * dot(pixelDelta, pixelDelta);
    }
    half alpha = exp(-0.5 * rho);
	if (i.col.a >= 0)
	{
		alpha = saturate(alpha * i.col.a);
	}
	else
	{
		// "selected" splat: magenta outline, increase opacity, magenta tint
		half3 selectedColor = half3(1,0,1);
		if (alpha > 7.0/255.0)
		{
			if (alpha < 10.0/255.0)
			{
				alpha = 1;
				i.col.rgb = selectedColor;
			}
			alpha = saturate(alpha + 0.3);
		}
		i.col.rgb = lerp(i.col.rgb, selectedColor, 0.5);
	}
	
    // A shared per-fragment cutoff keeps visible color and depth coverage aligned.
    // Zero opacity never writes depth, even when the user sets the cutoff to zero.
    if (alpha <= 0 || alpha < _AlphaCutoff)
        discard;

    return alpha;
}

half4 frag (v2f i) : SV_Target
{
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
    half alpha = EvaluateSplatAlpha(i);
    return half4(i.col.rgb * alpha, alpha);
}

half4 fragDepth(v2f i) : SV_Target
{
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
    EvaluateSplatAlpha(i);
    // The rasterizer writes perspective-correct plane depth; low-pass splats
    // retain their center depth. No linear-depth conversion or fullscreen depth.
    return 0;
}
        ENDCG

        Pass
        {
            Name "SplatColor"
            ZWrite Off
            ZTest LEqual
            Blend [_SrcBlend] [_DstBlend]
            Cull Off
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma require compute
            #pragma multi_compile_instancing
            #pragma multi_compile_local __ GAUSSIANS_DIRECT_TRANSPARENT
            ENDCG
        }

        Pass
        {
            Name "SplatDepth"
            // Explicitly submitted after transparent color, never as a URP prepass.
            Tags { "LightMode"="GaussianSplatDepth" }
            ZWrite On
            ZTest LEqual
            ColorMask 0
            Cull Off
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragDepth
            #pragma require compute
            #pragma multi_compile_instancing
            #pragma multi_compile_local __ GAUSSIANS_DIRECT_TRANSPARENT
            ENDCG
        }
    }
}
