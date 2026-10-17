Shader "Custom/CRT_Glitch"
{
    Properties
    {
        // The = "white" {} here ensures an opaque white texture is used if left empty
        [MainTexture] _BaseMap("Base Map", 2D) = "white" {}
        [MainColor] _BaseColor("Base Color", Color) = (1,1,1,1)

        _EmissionMap("Emission Map", 2D) = "black" {}
        [HDR] _EmissionColor("Emission Color", Color) = (0,0,0,1)

        [Header(CRT Glitch Settings)]
        _GlitchFrequency("Glitch Frequency (Seconds)", Float) = 3.0
        _GlitchDuration("Glitch Duration (Seconds)", Float) = 0.2
        _GlitchStrength("Glitch Strength", Float) = 0.05
        _BlockSize("Glitch Block Size", Float) = 15.0
        _ScanlineAmount("Scanline Density", Float) = 150.0

        [Header(Pulse Settings)]
        _PulseSpeed("Pulse Speed", Float) = 2.0
        _PulseMin("Pulse Min Multiplier", Float) = 0.5
        _PulseMax("Pulse Max Multiplier", Float) = 1.5
    }

    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" "RenderPipeline"="UniversalPipeline" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Cull Off

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                float4 color : COLOR; 
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float4 color : COLOR;
            };

            sampler2D _BaseMap;
            sampler2D _EmissionMap;

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                float4 _BaseColor;
                float4 _EmissionColor;
                float _GlitchFrequency;
                float _GlitchDuration;
                float _GlitchStrength;
                float _BlockSize;
                float _ScanlineAmount;
                float _PulseSpeed;
                float _PulseMin;
                float _PulseMax;
            CBUFFER_END

            Varyings vert(Attributes v)
            {
                Varyings o;
                o.positionHCS = TransformObjectToHClip(v.positionOS.xyz);
                o.uv = TRANSFORM_TEX(v.uv, _BaseMap);
                o.color = v.color;
                return o;
            }

            float random(float2 uv)
            {
                return frac(sin(dot(uv, float2(12.9898, 78.233))) * 43758.5453123);
            }

            half4 frag(Varyings i) : SV_Target
            {
                float2 uv = i.uv;
                float time = _Time.y;

                // 1. Glitch Timing Logic
                float glitchCycle = fmod(time, _GlitchFrequency);
                float isGlitched = step(glitchCycle, _GlitchDuration); 

                // 2. Generate Blocky Glitch Offset
                float jitterTime = floor(time * 15.0); 
                float blockY = floor(uv.y * _BlockSize);
                
                float randVal = (random(float2(blockY, jitterTime)) - 0.5) * 2.0; 
                float glitchOffset = randVal * _GlitchStrength * isGlitched;

                // 3. Chromatic Aberration (RGB Split)
                float2 uvR = uv + float2(glitchOffset, 0);       
                float2 uvG = uv;                                 
                float2 uvB = uv - float2(glitchOffset * 0.5, 0); 

                half4 texR = tex2D(_BaseMap, uvR);
                half4 texG = tex2D(_BaseMap, uvG);
                half4 texB = tex2D(_BaseMap, uvB);

                half3 baseColor = half3(texR.r, texG.g, texB.b) * _BaseColor.rgb * i.color.rgb;

                // 4. Sample Emission Map & Apply Pulse
                half4 emR = tex2D(_EmissionMap, uvR);
                half4 emG = tex2D(_EmissionMap, uvG);
                half4 emB = tex2D(_EmissionMap, uvB);

                half3 emission = half3(emR.r, emG.g, emB.b) * _EmissionColor.rgb;

                float pulseWave = (sin(time * _PulseSpeed) * 0.5) + 0.5;
                float currentPulse = lerp(_PulseMin, _PulseMax, pulseWave);
                
                emission *= currentPulse;

                // 5. Combine everything
                half3 finalColor = baseColor + emission;

                // 6. Handle Alpha 
                half finalAlpha = max(texR.a, max(texG.a, texB.a)) * _BaseColor.a * i.color.a;

                // 7. Apply simple CRT Scanlines
                float scanline = sin(uv.y * _ScanlineAmount + time * 5.0) * 0.1 + 0.9;
                finalColor *= scanline;

                return half4(finalColor, finalAlpha);
            }
            ENDHLSL
        }
    }
}