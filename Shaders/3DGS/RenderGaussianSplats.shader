// SPDX-License-Identifier: MIT
// Match the 2D Metal stereo compiler policy: Unity 6000.3's DXC stereo
// macros cannot assign the eye index correctly on this path.
Shader "Gaussians/3D/Render Splats"
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
        #include "GaussianSplatting.hlsl"
        #include_with_pragmas "../Core/GaussianFoveation.hlsl"

        StructuredBuffer<uint> _OrderBuffer;
        StructuredBuffer<SplatViewData> _SplatViewData;
        ByteAddressBuffer _SplatSelectedBits;
        uint _SplatBitsValid;
        uint _SplatCount;
        uint _SplatViewCount;
        uint _SplatEyeIndex;
        uint _ConvertGammaToLinear;
        float _AlphaCutoff;

        struct appdata
        {
            uint vertexID : SV_VertexID;
        #if defined(UNITY_INSTANCING_ENABLED) || defined(UNITY_PROCEDURAL_INSTANCING_ENABLED) || defined(UNITY_STEREO_INSTANCING_ENABLED)
            UNITY_VERTEX_INPUT_INSTANCE_ID
        #else
            uint instanceID : SV_InstanceID;
        #endif
        };
        struct v2f
        {
            half4 col : COLOR0;
            float2 pos : TEXCOORD0;
            float4 vertex : SV_POSITION;
        #if defined(GAUSSIANS_METAL_FOVEATION)
            nointerpolation float2 centerUV : TEXCOORD1;
            nointerpolation float4 inverseAxes : TEXCOORD2;
        #endif
            UNITY_VERTEX_OUTPUT_STEREO
        };

        v2f vert(appdata input)
        {
            v2f o = (v2f)0;
            UNITY_SETUP_INSTANCE_ID(input);
            UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
            uint instance = input.instanceID;
        #if defined(UNITY_INSTANCING_ENABLED) || defined(UNITY_PROCEDURAL_INSTANCING_ENABLED) || defined(UNITY_STEREO_INSTANCING_ENABLED)
            instance = unity_InstanceID;
        #endif
            uint eye = _SplatEyeIndex;
        #if defined(UNITY_STEREO_INSTANCING_ENABLED) || defined(UNITY_STEREO_MULTIVIEW_ENABLED)
            eye = unity_StereoEyeIndex;
        #endif
            eye = _SplatViewCount > 1u ? min(eye, 1u) : 0u;
            uint index = _OrderBuffer[instance];
            SplatViewData view = _SplatViewData[eye * _SplatCount + index];
            if (view.pos.w <= 0)
            {
                o.vertex = asfloat(0x7fc00000);
                return o;
            }
            o.col = half4(f16tof32(view.color.x >> 16), f16tof32(view.color.x),
                          f16tof32(view.color.y >> 16), f16tof32(view.color.y));
            if (_ConvertGammaToLinear != 0u)
                o.col.rgb = GammaToLinearSpace(o.col.rgb);
            float2 corner = (float2(input.vertexID & 1, (input.vertexID >> 1) & 1) * 2.0 - 1.0) * 2.0;
            o.pos = corner;
            o.vertex = view.pos;
            o.vertex.xy += (corner.x * view.axis1 + corner.y * view.axis2) * (2.0 / _ScreenParams.xy) * view.pos.w;
        #if defined(GAUSSIANS_METAL_FOVEATION)
            float4 centerScreen = ComputeScreenPos(view.pos);
            o.centerUV = centerScreen.xy / centerScreen.w;
            float2 axis1 = view.axis1;
            float2 axis2 = view.axis2;
            axis1.y *= _ProjectionParams.x;
            axis2.y *= _ProjectionParams.x;
            float det = axis1.x * axis2.y - axis2.x * axis1.y;
            o.inverseAxes = abs(det) > 1.0e-12 ? float4(axis2.y, -axis2.x, -axis1.y, axis1.x) / det : 0;
        #endif
            if (_SplatBitsValid != 0u && (_SplatSelectedBits.Load((index / 32u) * 4u) & (1u << (index & 31u))) != 0u)
                o.col.a = -1;
        #if defined(GAUSSIANS_DIRECT_TRANSPARENT)
        #if UNITY_UV_STARTS_AT_TOP
            // View records use render-texture projection. Match the actual camera
            // target when a direct draw reaches a top-origin backbuffer.
            o.vertex.y *= -_ProjectionParams.x;
        #endif
        #else
            FlipProjectionIfBackbuffer(o.vertex);
        #endif
            return o;
        }

        half EvaluateSplatAlpha(inout v2f i)
        {
            float2 pos = i.pos;
        #if defined(GAUSSIANS_METAL_FOVEATION)
            UNITY_BRANCH if (_FOVEATED_RENDERING_NON_UNIFORM_RASTER)
            {
                float2 delta = (SplatRasterToLinearUV(i.vertex.xy / _ScreenParams.xy) - i.centerUV) * _ScreenParams.xy;
                pos = float2(dot(i.inverseAxes.xy, delta), dot(i.inverseAxes.zw, delta));
            }
        #endif
            half alpha = exp(-dot(pos, pos));
            if (i.col.a >= 0)
                alpha = saturate(alpha * i.col.a);
            else
            {
                half3 selectedColor = half3(1, 0, 1);
                if (alpha > 7.0 / 255.0)
                {
                    if (alpha < 10.0 / 255.0) { alpha = 1; i.col.rgb = selectedColor; }
                    alpha = saturate(alpha + 0.3);
                }
                i.col.rgb = lerp(i.col.rgb, selectedColor, 0.5);
            }
            if (alpha <= 0 || alpha < _AlphaCutoff) discard;
            return alpha;
        }
        half4 frag(v2f i) : SV_Target
        {
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
            half alpha = EvaluateSplatAlpha(i);
            return half4(i.col.rgb * alpha, alpha);
        }
        half4 fragDepth(v2f i) : SV_Target
        {
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
            EvaluateSplatAlpha(i);
            // Approximate center depth, not a reconstructed Gaussian surface.
            return 0;
        }
        ENDCG
        Pass
        {
            Name "SplatColor"
            ZWrite Off ZTest LEqual Cull Off
            Blend [_SrcBlend] [_DstBlend]
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
            Tags { "LightMode"="GaussianSplatDepth" }
            ZWrite On ZTest LEqual ColorMask 0 Cull Off
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
