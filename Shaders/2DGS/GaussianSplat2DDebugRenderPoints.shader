// SPDX-License-Identifier: MIT
// Intentionally omit #pragma use_dxc: in Unity 6000.3.23f1, Metal single-pass
// instancing fails because Unity's DXC/stereo macros assign a uniform eye index.
// This appears to be a Unity compiler/macro compatibility bug. Upstream reported
// faster Metal rendering with DXC; investigate re-enabling it after verifying
// stereo compilation, both eyes on device, and an actual performance benefit.
Shader "Gaussians/2D/Debug/Render Points"
{
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" }

        Pass
        {
            ZWrite On
            Cull Off
            
CGPROGRAM
#pragma vertex vert
#pragma fragment frag
#pragma require compute
#pragma multi_compile_instancing

#include "UnityCG.cginc"
#include "GaussianSplat2D.hlsl"

struct v2f
{
    half3 color : TEXCOORD0;
    float4 vertex : SV_POSITION;
    UNITY_VERTEX_OUTPUT_STEREO
};

float _SplatSize;
bool _DisplayIndex;
int _SplatCount;

struct appdata
{
    uint vertexID : SV_VertexID;
#if defined(UNITY_INSTANCING_ENABLED) || defined(UNITY_PROCEDURAL_INSTANCING_ENABLED) || defined(UNITY_STEREO_INSTANCING_ENABLED)
    UNITY_VERTEX_INPUT_INSTANCE_ID
#else
    uint instanceID : SV_InstanceID;
#endif
};

float4x4 _MatrixObjectToWorld;
uint _ConvertGammaToLinear;

v2f vert (appdata input)
{
    v2f o = (v2f)0;
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
    uint vtxID = input.vertexID;
    uint instID = input.instanceID;
#if defined(UNITY_INSTANCING_ENABLED) || defined(UNITY_PROCEDURAL_INSTANCING_ENABLED) || defined(UNITY_STEREO_INSTANCING_ENABLED)
    instID = unity_InstanceID;
#endif
    uint splatIndex = instID;

    SplatData splat = LoadSplatData(splatIndex);

    float3 centerWorldPos = splat.pos;
    centerWorldPos = mul(_MatrixObjectToWorld, float4(centerWorldPos,1)).xyz;

    float4 centerClipPos = mul(UNITY_MATRIX_VP, float4(centerWorldPos, 1));

    o.vertex = centerClipPos;
	uint idx = vtxID;
    float2 quadPos = float2(idx&1, (idx>>1)&1) * 2.0 - 1.0;
    o.vertex.xy += (quadPos * _SplatSize / _ScreenParams.xy) * o.vertex.w;

    o.color.rgb = saturate(splat.sh.col);
    if (_ConvertGammaToLinear != 0u)
        o.color.rgb = GammaToLinearSpace(o.color.rgb);
    if (_DisplayIndex)
    {
        o.color.r = frac((float)splatIndex / (float)_SplatCount * 100);
        o.color.g = frac((float)splatIndex / (float)_SplatCount * 10);
        o.color.b = (float)splatIndex / (float)_SplatCount;
    }

    FlipProjectionIfBackbuffer(o.vertex);
    return o;
}

half4 frag (v2f i) : SV_Target
{
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
    return half4(i.color.rgb, 1);
}
ENDCG
        }
    }
}
