// SPDX-License-Identifier: MIT
// Intentionally omit #pragma use_dxc: in Unity 6000.3.23f1, Metal single-pass
// instancing fails because Unity's DXC/stereo macros assign a uniform eye index.
// This appears to be a Unity compiler/macro compatibility bug. Upstream reported
// faster Metal rendering with DXC; investigate re-enabling it after verifying
// stereo compilation, both eyes on device, and an actual performance benefit.
Shader "Gaussians/2D/Debug/Render Boxes"
{
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" }

        Pass
        {
            ZWrite Off
            Blend OneMinusDstAlpha One
            Cull Front

CGPROGRAM
#pragma vertex vert
#pragma fragment frag
#pragma require compute
#pragma multi_compile_instancing

#include "UnityCG.cginc"
#include "GaussianSplat2D.hlsl"

StructuredBuffer<uint> _OrderBuffer;

bool _DisplayChunks;

struct v2f
{
    half4 col : COLOR0;
    float4 vertex : SV_POSITION;
    UNITY_VERTEX_OUTPUT_STEREO
};

float _SplatScale;
float _SplatOpacityScale;

// based on https://iquilezles.org/articles/palettes/
// cosine based palette, 4 vec3 params
half3 palette(float t, half3 a, half3 b, half3 c, half3 d)
{
    return a + b*cos(6.28318*(c*t+d));
}

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
    bool chunks = _DisplayChunks;
	uint idx = vtxID;
	float3 localPos = float3(idx&1, (idx>>1)&1, (idx>>2)&1) * 2.0 - 1.0;

    float3 centerWorldPos = 0;

    if (!chunks)
    {
        // display splat boxes
        instID = _OrderBuffer[instID];
        SplatData splat = LoadSplatData(instID);

        float4 boxRot = splat.rot;
        float2 boxSize = splat.scale;
        boxSize *= _SplatScale;

        float3x3 splatRotScaleMat = CalcMatrixFromRotationScale(boxRot, boxSize);
        splatRotScaleMat = mul((float3x3)_MatrixObjectToWorld, splatRotScaleMat);

        centerWorldPos = splat.pos;
        centerWorldPos = mul(_MatrixObjectToWorld, float4(centerWorldPos,1)).xyz;

        o.col.rgb = saturate(splat.sh.col);
        if (_ConvertGammaToLinear != 0u)
            o.col.rgb = GammaToLinearSpace(o.col.rgb);
        o.col.a = saturate(splat.opacity * _SplatOpacityScale);

        localPos = mul(splatRotScaleMat, localPos) * 2;
    }
    else
    {
        // display chunk boxes
        localPos = localPos * 0.5 + 0.5;
        SplatChunkInfo chunk = _SplatChunks[instID];
        float3 posMin = float3(chunk.posX.x, chunk.posY.x, chunk.posZ.x);
        float3 posMax = float3(chunk.posX.y, chunk.posY.y, chunk.posZ.y);

        localPos = lerp(posMin, posMax, localPos);
        localPos = mul(_MatrixObjectToWorld, float4(localPos,1)).xyz;

        o.col.rgb = palette((float)instID / (float)_SplatChunkCount, half3(0.5,0.5,0.5), half3(0.5,0.5,0.5), half3(1,1,1), half3(0.0, 0.33, 0.67));
        o.col.a = 0.1;
    }

    float3 worldPos = centerWorldPos + localPos;
    o.vertex = UnityWorldToClipPos(worldPos);
    FlipProjectionIfBackbuffer(o.vertex);
    return o;
}

half4 frag (v2f i) : SV_Target
{
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
    half4 res = half4(i.col.rgb * i.col.a, i.col.a);
    return res;
}
ENDCG
        }
    }
}
