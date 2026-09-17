// SPDX-License-Identifier: MIT
// Intentionally omit #pragma use_dxc: in Unity 6000.3.23f1, Metal single-pass
// instancing fails because Unity's DXC/stereo macros assign a uniform eye index.
// This appears to be a Unity compiler/macro compatibility bug. Upstream reported
// faster Metal rendering with DXC; investigate re-enabling it after verifying
// stereo compilation, both eyes on device, and an actual performance benefit.
Shader "Hidden/Gaussians/2D/Composite"
{
    SubShader
    {
        ZWrite Off
        ZTest Always
        Cull Off
        // Both paths now blend premultiplied color in the selected color space.
        Blend One OneMinusSrcAlpha

        CGINCLUDE
        #include "UnityCG.cginc"
        #include_with_pragmas "../Core/GaussianFoveation.hlsl"

        struct appdata
        {
            uint vertexID : SV_VertexID;
            UNITY_VERTEX_INPUT_INSTANCE_ID
        };

        struct v2f
        {
            float4 vertex : SV_POSITION;
            float2 uv : TEXCOORD0;
            UNITY_VERTEX_OUTPUT_STEREO
        };

        UNITY_DECLARE_SCREENSPACE_TEXTURE(_GaussianSplat2DRT);
        float4 _GaussianSplat2DRT_TexelSize;
        UNITY_DECLARE_SCREENSPACE_TEXTURE(_BlitTexture);
        float4 _BlitScaleBias;

        v2f vert(appdata input)
        {
            v2f o = (v2f)0;
            UNITY_SETUP_INSTANCE_ID(input);
            UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
            float2 uv = float2(input.vertexID & 1, (input.vertexID >> 1) & 1) * 2.0;
            o.vertex = float4(uv * 2.0 - 1.0, 1, 1);
        #if UNITY_UV_STARTS_AT_TOP
            uv.y = 1.0 - uv.y;
        #endif
            o.uv = uv;
            return o;
        }

        half4 frag(v2f i) : SV_Target
        {
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
            float2 uv = i.vertex.xy * _GaussianSplat2DRT_TexelSize.xy;
            return UNITY_SAMPLE_SCREENSPACE_TEXTURE(_GaussianSplat2DRT, uv);
        }

        half4 fragBlit(v2f i) : SV_Target
        {
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
            // URP Blitter supplies the texture and viewport scale/bias. Color has
            // already been converted per renderer, before Gaussian blending.
            // Interpolated UV is logical screen space; accumulation is in the
            // camera's raster space. Remap once, before RTHandle scale/bias.
            float2 uv = SplatLinearToRasterUV(i.uv) * _BlitScaleBias.xy + _BlitScaleBias.zw;
            return UNITY_SAMPLE_SCREENSPACE_TEXTURE(_BlitTexture, uv);
        }
        ENDCG

        Pass
        {
            Name "Composite"
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5
            #pragma multi_compile_instancing
            ENDCG
        }
        Pass
        {
            Name "CompositeURP"
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragBlit
            #pragma target 3.5
            #pragma multi_compile_instancing
            ENDCG
        }
    }
}
