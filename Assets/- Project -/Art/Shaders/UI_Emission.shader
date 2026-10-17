Shader "UI/Emission"
{
    Properties
    {
        [PerRendererData] _MainTex ("Texture", 2D) = "white" {}
        [MainColor] _BaseColor ("Base Color", Color) = (1,1,1,1)
        _EmissionMap ("Emission Map", 2D) = "black" {}
        [HDR] _EmissionColor ("Emission Color", Color) = (0,0,0,1)
        _EmissionStrength ("Emission Strength", Range(0,10)) = 1
        _AlphaClip ("Alpha Clipping", Float) = 0
        _Cutoff ("Alpha Cutoff", Range(0,1)) = 0.5

        _StencilComp ("Stencil Comparison", Float) = 8
        _Stencil ("Stencil ID", Float) = 0
        _StencilOp ("Stencil Operation", Float) = 0
        _StencilWriteMask ("Stencil Write Mask", Float) = 255
        _StencilReadMask ("Stencil Read Mask", Float) = 255
        _ColorMask ("Color Mask", Float) = 15
        [Toggle(UNITY_UI_ALPHACLIP)] _UseUIAlphaClip ("Use UI Alpha Clip", Float) = 0
    }

    SubShader
    {
        Tags
        {
            "Queue"="Transparent"
            "IgnoreProjector"="True"
            "RenderType"="Transparent"
            "RenderPipeline"="UniversalPipeline"
            "CanUseSpriteAtlas"="True"
        }

        Stencil
        {
            Ref [_Stencil]
            Comp [_StencilComp]
            Pass [_StencilOp]
            ReadMask [_StencilReadMask]
            WriteMask [_StencilWriteMask]
        }

        Cull Off
        Lighting Off
        ZWrite Off
        ZTest [unity_GUIZTestMode]
        Blend SrcAlpha OneMinusSrcAlpha
        ColorMask [_ColorMask]

        Pass
        {
            Name "UI Emission"

            HLSLPROGRAM
            #pragma target 2.0
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_local _ UNITY_UI_CLIP_RECT
            #pragma multi_compile_local _ UNITY_UI_ALPHACLIP

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float4 color : COLOR;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float4 color : COLOR;
                float2 uv : TEXCOORD0;
                float2 emissionUV : TEXCOORD1;
                float4 maskPosition : TEXCOORD2;
            };

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);
            TEXTURE2D(_EmissionMap);
            SAMPLER(sampler_EmissionMap);

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                float4 _EmissionMap_ST;
                float4 _BaseColor;
                float4 _EmissionColor;
                float _EmissionStrength;
                float _AlphaClip;
                float _Cutoff;
            CBUFFER_END
            float4 _ClipRect;

            float UnityGet2DClipping(float2 position, float4 clipRect)
            {
                float2 inside = step(clipRect.xy, position) * step(position, clipRect.zw);
                return inside.x * inside.y;
            }

            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.positionHCS = TransformObjectToHClip(input.positionOS.xyz);
                output.uv = TRANSFORM_TEX(input.uv, _MainTex);
                output.emissionUV = TRANSFORM_TEX(input.uv, _EmissionMap);
                output.color = input.color * _BaseColor;
                output.maskPosition = input.positionOS;
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                half4 textureColor = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv);
                half finalAlpha = textureColor.a * input.color.a;

                if (_AlphaClip > 0.5)
                    clip(finalAlpha - _Cutoff);

            #if UNITY_UI_CLIP_RECT
                finalAlpha *= UnityGet2DClipping(input.maskPosition.xy, _ClipRect);
            #endif

            #if UNITY_UI_ALPHACLIP
                clip(finalAlpha - 0.001);
            #endif

                half3 baseRgb = textureColor.rgb * input.color.rgb;
                half3 emissionTexture = SAMPLE_TEXTURE2D(_EmissionMap, sampler_EmissionMap, input.emissionUV).rgb;
                half3 emissionRgb = emissionTexture * _EmissionColor.rgb
                    * (_EmissionStrength * finalAlpha);
                return half4(baseRgb + emissionRgb, finalAlpha);
            }
            ENDHLSL
        }
    }
}
