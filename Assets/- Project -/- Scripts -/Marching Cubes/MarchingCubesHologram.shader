Shader "Hidden/MarchingCubes/Hologram"
{
    Properties
    {
        _BaseColor ("Base Color", Color) = (1,1,1,1)
        _LowTint ("Low Tint", Color) = (0.12,0.87,1,0.66)
        _HighTint ("High Tint", Color) = (1,0.55,0.25,0.92)
        _MinSurfaceHeight ("Min Surface Height", Float) = 1.0
        _MaxSurfaceHeight ("Max Surface Height", Float) = 4.3
        _WallGradientStrength ("Wall Gradient Strength", Range(0,1)) = 1.0
        _AccentTint ("Accent Tint", Color) = (0.5,1,0.95,1)
        _EdgeGlowStrength ("Edge Glow Strength", Float) = 1.0
        _ChunkHalfExtents ("Chunk Half Extents (XZ)", Vector) = (0,0,0,0)
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" "RenderPipeline" = "UniversalPipeline" }
        Blend Off
        ZWrite On
        ZTest LEqual
        Cull Off

        Pass
        {
            Tags { "LightMode" = "SRPDefaultUnlit" }
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseColor;
                float4 _LowTint;
                float4 _HighTint;
                float _MinSurfaceHeight;
                float _MaxSurfaceHeight;
                float _WallGradientStrength;
                float4 _AccentTint;
                float _EdgeGlowStrength;
                float4 _ChunkHalfExtents;
            CBUFFER_END

            struct Attributes
            {
                float3 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float4 color : COLOR;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 normalWS : TEXCOORD1;
                float4 color : TEXCOORD2;
                // xyz = object-space position, w = object-space normal.y
                float4 positionOSNormalY : TEXCOORD3;
            };

            Varyings vert(Attributes input)
            {
                Varyings output;
                VertexPositionInputs positionInputs = GetVertexPositionInputs(input.positionOS);
                VertexNormalInputs normalInputs = GetVertexNormalInputs(input.normalOS);
                output.positionCS = positionInputs.positionCS;
                output.normalWS = normalize(normalInputs.normalWS);
                output.color = float4(input.color.rgb * _BaseColor.rgb, 1.0);
                output.positionOSNormalY = float4(input.positionOS, input.normalOS.y);
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                float edgeGlow = pow(saturate(1.0 - abs(input.normalWS.y)), 2.25) * _EdgeGlowStrength;
                float3 positionOS = input.positionOSNormalY.xyz;

                // Outer side walls sit exactly on the chunk's X/Z bounds (vertices are clamped there); no accent glow on them.
                float2 boundaryGap = _ChunkHalfExtents.xy - abs(positionOS.xz);
                float onSide = (_ChunkHalfExtents.x > 0.0 && min(boundaryGap.x, boundaryGap.y) < 1e-3) ? 1.0 : 0.0;
                edgeGlow *= 1.0 - onSide;

                float heightT = saturate((positionOS.y - _MinSurfaceHeight) / max(_MaxSurfaceHeight - _MinSurfaceHeight, 1e-4));
                float3 wallColor = lerp(_LowTint.rgb, _HighTint.rgb, heightT) * _BaseColor.rgb;
                float topMask = smoothstep(0.25, 0.65, input.positionOSNormalY.w);
                float wallMask = (1.0 - topMask) * _WallGradientStrength;

                float3 finalColor = lerp(input.color.rgb, wallColor, wallMask);
                finalColor += _AccentTint.rgb * edgeGlow;
                return half4(finalColor, 1.0);
            }
            ENDHLSL
        }
    }
}
