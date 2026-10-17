Shader "FullScreen/CRT_Filter"
{
    Properties
    {
        _LineDensity ("Line Density", Float) = 250
        _LineTransparency ("Line Transparency", Range(0, 1)) = 0.8
        _VignetteStrength ("Vignette Strength", Float) = 0.8
        _VignetteSmoothness ("Vignette Smoothness", Float) = 0.5
        _PaniniDistortion ("Panini/CRT Distortion", Range(0, 1)) = 0.2
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline" = "UniversalPipeline" }
        LOD 100

        Pass
        {
            Name "CRT Filter Pass"
            ZWrite Off ZTest Always Blend Off Cull Off

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            // URP specific includes for full-screen blitting
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            float _LineDensity;
            float _LineTransparency;
            float _VignetteStrength;
            float _VignetteSmoothness;
            float _PaniniDistortion;

            float2 ApplyPaniniDistortion(float2 uv)
            {
                // Center UVs for distortion (-1 to 1)
                float2 centeredUV = uv * 2.0 - 1.0;

                // Apply a barrel-style distortion (mimicking Panini/CRT curvature)
                float dist = dot(centeredUV, centeredUV);
                float2 distortedUV = centeredUV * (1.0 + dist * _PaniniDistortion);

                // Return to 0 to 1 space
                return distortedUV * 0.5 + 0.5;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                // 1. Distort UV coordinates
                float2 uv = ApplyPaniniDistortion(input.texcoord);

                // Crop edges that warp outside the 0-1 bounds (black borders)
                if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0)
                    return half4(0, 0, 0, 1);

                // 2. Sample the screen color using distorted UVs
                half4 color = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv);

                // 3. Calculate Vignette based on distorted UVs
                float2 distCenter = uv - 0.5;
                float distLength = length(distCenter);
                // smoothstep creates a soft gradient from the strength point
                float vignette = smoothstep(_VignetteStrength, _VignetteStrength - _VignetteSmoothness, distLength);

                // 4. Calculate CRT Scanlines
                // Changed UNITY_PI to PI (URP standard)
                float scanlineVal = sin(uv.y * _LineDensity * PI);
                scanlineVal = scanlineVal * scanlineVal; // Square the wave to sharpen the lines

                // 5. Vignette affects Line Transparency
                float currentLineAlpha = _LineTransparency * vignette;

                // Blend the scanlines into the color
                color.rgb = lerp(color.rgb, color.rgb * scanlineVal, currentLineAlpha);

                // 6. Apply the final vignette shadow
                color.rgb *= vignette;

                return color;
            }
            ENDHLSL
        }
    }
}