Shader "Custom/CrossSectionHeart"
{
    Properties
    {
        _BaseMap ("Texture", 2D) = "white" {}
        _BaseColor ("Color", Color) = (1, 1, 1, 1)
        _SlicePlane ("Slice Plane (XYZ: Normal, W: Distance)", Vector) = (0, 0, 1, 0)
        _InteriorColor ("Interior Color", Color) = (0.55, 0.05, 0.08, 1)
        _CutEdgeColor ("Cut Edge Glow Color", Color) = (0.0, 1.0, 1.0, 1)
        _CutEdgeWidth ("Cut Edge Width", Float) = 0.005
    }
    SubShader
    {
        Tags 
        { 
            "RenderType" = "Opaque" 
            "RenderPipeline" = "UniversalPipeline" 
            "Queue" = "Geometry"
        }
        LOD 200
        Cull Off

        Pass
        {
            Name "UniversalForward"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile _ _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float4 positionOS   : POSITION;
                float3 normalOS     : NORMAL;
                float2 uv           : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS   : SV_POSITION;
                float3 positionWS   : TEXCOORD1;
                float3 normalWS     : NORMAL;
                float2 uv           : TEXCOORD0;
            };

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                float4 _BaseColor;
                float4 _SlicePlane;
                float4 _InteriorColor;
                float4 _CutEdgeColor;
                float  _CutEdgeWidth;
            CBUFFER_END

            Varyings vert(Attributes input)
            {
                Varyings output;
                VertexPositionInputs posInputs = GetVertexPositionInputs(input.positionOS.xyz);
                VertexNormalInputs normInputs = GetVertexNormalInputs(input.normalOS);

                output.positionCS = posInputs.positionCS;
                output.positionWS = posInputs.positionWS;
                output.normalWS = normInputs.normalWS;
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                return output;
            }

            half4 frag(Varyings input, bool isFrontFace : SV_IsFrontFace) : SV_Target
            {
                // Plane clipping: dot(worldPos, planeNormal) - planeDistance
                // Discard pixels where dot(worldPos, normal) < distance (W)
                float planeDist = dot(input.positionWS, _SlicePlane.xyz) - _SlicePlane.w;
                clip(planeDist);

                // Backface rendering (the solid "inside" of the heart):
                if (!isFrontFace)
                {
                    // Render solid dark-red/crimson to simulate solid muscle tissue
                    return half4(_InteriorColor.rgb, 1.0);
                }

                // Frontface: sampled diffuse texture * tint
                half4 texColor = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv) * _BaseColor;
                half3 albedo = texColor.rgb;

                // Subtle holographic cut-edge cyan rim
                if (planeDist < _CutEdgeWidth)
                {
                    albedo = lerp(albedo, _CutEdgeColor.rgb, 0.75);
                }

                // URP main light diffuse shading
                Light mainLight = GetMainLight();
                half3 normalWS = normalize(input.normalWS);
                half NdotL = saturate(dot(normalWS, mainLight.direction));
                half3 lightColor = mainLight.color * (NdotL * 0.75 + 0.25);

                half3 ambient = half3(0.02, 0.04, 0.08);
                half3 finalColor = albedo * lightColor + albedo * ambient;

                return half4(finalColor, 1.0);
            }
            ENDHLSL
        }
    }
    FallBack "Universal Render Pipeline/Lit"
}
