// Greybox surfaces: baked-in fake lighting (no realtime lights needed on Quest 2) and a
// faint 1 m world grid so scale reads correctly in the headset.
Shader "RHMuseum/Greybox"
{
    Properties
    {
        _BaseColor ("Color", Color) = (0.8, 0.8, 0.8, 1)
        _GridStrength ("Grid strength", Range(0, 1)) = 0.12
        _LightDir ("Fake light direction", Vector) = (0.35, 0.8, 0.45, 0)
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Name "Unlit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _BaseColor;
                half _GridStrength;
                float4 _LightDir;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                half4 color : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                half3 normalWS : TEXCOORD1;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes v)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.positionWS = TransformObjectToWorld(v.positionOS.xyz);
                o.positionCS = TransformWorldToHClip(o.positionWS);
                o.normalWS = TransformObjectToWorldNormal(v.normalOS);
                return o;
            }

            half GridLine(float x)
            {
                float f = abs(frac(x) - 0.5);
                return smoothstep(0.47, 0.5, f);
            }

            half4 frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                half3 n = normalize(i.normalWS);
                half lambert = saturate(dot(n, normalize(_LightDir.xyz))) * 0.45 + 0.55;
                half up = n.y * 0.5 + 0.5;                                  // sky/ground tint
                half3 col = _BaseColor.rgb * lambert * lerp(0.92, 1.05, up);

                float3 p = i.positionWS;
                half3 a = abs(n);
                half grid = a.y > 0.5 ? max(GridLine(p.x), GridLine(p.z))
                          : a.x > 0.5 ? max(GridLine(p.y), GridLine(p.z))
                                      : max(GridLine(p.x), GridLine(p.y));
                col *= 1 - grid * _GridStrength;
                return half4(col, 1);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
