Shader "Custom/URP_GridTileOutline"
{
    Properties
    {
        [Header(Base Fill)]
        _BaseColor ("Base Color", Color) = (0.08, 0.08, 0.1, 0.8)

        [Header(Outline Dimensions)]
        _OutlineThickness ("Outline Thickness", Range(0.001, 0.49)) = 0.06
        _OutlineSoftness ("Outline Softness", Range(0.0001, 0.05)) = 0.005

        [Header(Outline Emission)]
        _EmissionIntensity ("Emission Intensity", Float) = 2.5

        [Header(Default Corner Colors)]
        [HDR] _ColorBL ("Bottom-Left Color", Color) = (0.0, 1.0, 1.0, 1.0)
        [HDR] _ColorBR ("Bottom-Right Color", Color) = (0.0, 0.5, 1.0, 1.0)
        [HDR] _ColorTL ("Top-Left Color", Color) = (0.5, 0.0, 1.0, 1.0)
        [HDR] _ColorTR ("Top-Right Color", Color) = (1.0, 0.0, 0.8, 1.0)
    }

    SubShader
    {
        Tags 
        { 
            "RenderType" = "Opaque" 
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Geometry"
        }
        LOD 100

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            Cull Off
            ZWrite On
            ZTest LEqual

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            #define MAX_POINTS 16

            struct Attributes
            {
                float4 positionOS   : POSITION;
                float2 uv           : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS   : SV_POSITION;
                float3 positionWS   : TEXCOORD1;
                float2 uv           : TEXCOORD0;
            };

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseColor;
                float4 _ColorBL;
                float4 _ColorBR;
                float4 _ColorTL;
                float4 _ColorTR;
                float _OutlineThickness;
                float _OutlineSoftness;
                float _EmissionIntensity;

                int _ActivePointCount;
                float4 _PointPositions[MAX_POINTS]; // xyz: world pos
                float4 _PointColors[MAX_POINTS];    // rgb: color, a: (intensity * alpha)
                float4 _PointParams[MAX_POINTS];    // x: innerRadius, y: outerRadius, z: alpha, w: heightDelta
            CBUFFER_END

            Varyings vert(Attributes input)
            {
                Varyings output;
                VertexPositionInputs posInputs = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionCS = posInputs.positionCS;
                output.positionWS = posInputs.positionWS;
                output.uv = input.uv;
                return output;
            }

            float4 frag(Varyings input, half facing : VFACE) : SV_Target
            {
                if (facing < 0.0)
                {
                    return _BaseColor;
                }

                float2 uv = input.uv;

                // Compensate for 3D quad stretching on steep slopes so outline thickness stays uniform
                float3 dPdx = ddx(input.positionWS);
                float3 dPdy = ddy(input.positionWS);
                float2 dUVdx = ddx(uv);
                float2 dUVdy = ddy(uv);

                float det = dUVdx.x * dUVdy.y - dUVdx.y * dUVdy.x;
                float2 stretchScale = float2(1.0, 1.0);
                if (abs(det) > 1e-7)
                {
                    float3 dPdU = (dPdx * dUVdy.y - dPdy * dUVdx.y) / det;
                    float3 dPdV = (dPdy * dUVdx.x - dPdx * dUVdy.x) / det;
                    float lenU = max(0.001, length(dPdU));
                    float lenV = max(0.001, length(dPdV));
                    float minLen = min(lenU, lenV);
                    stretchScale = float2(lenU / minLen, lenV / minLen);
                }

                // Scale edge distance by world-space aspect ratio of the quad
                float distToEdgeX = min(uv.x, 1.0 - uv.x) * stretchScale.x;
                float distToEdgeY = min(uv.y, 1.0 - uv.y) * stretchScale.y;
                float distToEdge = min(distToEdgeX, distToEdgeY);

                float borderFactor = 1.0 - smoothstep(_OutlineThickness - _OutlineSoftness, _OutlineThickness + _OutlineSoftness, distToEdge);

                // Default corner gradient baseline
                float4 botColor = lerp(_ColorBL, _ColorBR, uv.x);
                float4 topColor = lerp(_ColorTL, _ColorTR, uv.x);
                float4 defaultColor = lerp(botColor, topColor, uv.y);

                float3 accumulatedEffectColor = float3(0.0, 0.0, 0.0);
                float accumulatedEmission = 0.0;
                float totalWeight = 0.0;
                float maxWeight = 0.0;

                for (int i = 0; i < _ActivePointCount; i++)
                {
                    float innerR = _PointParams[i].x;
                    float outerR = _PointParams[i].y;
                    float effectAlpha = _PointParams[i].z;
                    float heightDelta = _PointParams[i].w;

                    float3 offset = input.positionWS - _PointPositions[i].xyz;
                    float rXZ2 = dot(offset.xz, offset.xz);
                    float d = length(offset);

                    // If this effect also displaced the terrain, recover the pre-blast 3D distance
                    // so the color sphere stays locked to the crater surface it pushed.
                    if (abs(heightDelta) > 0.0001 && rXZ2 < outerR * outerR)
                    {
                        // Initial guess clamps the vertical displacement along heightDelta
                        float clampedY = offset.y - clamp(offset.y, min(0.0, heightDelta), max(0.0, heightDelta));
                        d = sqrt(rXZ2 + clampedY * clampedY);

                        [unroll]
                        for (int k = 0; k < 2; k++)
                        {
                            float t = saturate(d / outerR);
                            float factor = 0.5 * (1.0 + cos(3.14159265 * t));
                            float dy0 = offset.y - heightDelta * factor;
                            d = sqrt(rXZ2 + dy0 * dy0);
                        }
                    }

                    float radialWeight = (1.0 - smoothstep(innerR, outerR, d)) * effectAlpha;

                    accumulatedEffectColor += _PointColors[i].rgb * radialWeight;
                    accumulatedEmission += _PointColors[i].a * radialWeight;
                    totalWeight += radialWeight;
                    maxWeight = max(maxWeight, radialWeight);
                }

                float4 activeOutlineColor = defaultColor;
                float activeEmission = _EmissionIntensity;

                if (totalWeight > 0.0001)
                {
                    float3 mixedColor = accumulatedEffectColor / totalWeight;
                    float mixedEmission = accumulatedEmission / totalWeight;

                    float blendToEffect = saturate(maxWeight);
                    activeOutlineColor = lerp(defaultColor, float4(mixedColor, 1.0), blendToEffect);
                    activeEmission = lerp(_EmissionIntensity, mixedEmission, blendToEffect);
                }

                float4 emissiveOutline = activeOutlineColor * activeEmission;
                return lerp(_BaseColor, emissiveOutline, borderFactor);
            }
            ENDHLSL
        }
    }
}