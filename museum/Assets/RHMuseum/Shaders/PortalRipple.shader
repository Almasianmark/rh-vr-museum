// Painting surface: touch ripples + "jump in" swirl. Original effect (concentric damped
// waves displaced along the normal, with refraction and slope shading); no third-party assets.
// URP, single-pass-instanced stereo safe, unlit (cheap on Quest 2).
Shader "RHMuseum/PortalRipple"
{
    Properties
    {
        _MainTex ("Painting", 2D) = "white" {}
        _Tint ("Tint", Color) = (1,1,1,1)
        _Aspect ("Width / Height", Float) = 1.6
        _Depth ("Displacement depth (m)", Float) = 0.035
        _Speed ("Wave speed (uv per s)", Float) = 0.8
        _Frequency ("Wave frequency", Float) = 42
        _Decay ("Decay per s", Float) = 1.4
        _Enter ("Enter amount", Range(0, 1)) = 0
        _EnterOrigin ("Enter origin (uv)", Vector) = (0.5, 0.5, 0, 0)
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Name "Unlit"
            Tags { "LightMode" = "UniversalForward" }
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            #define MAX_RIPPLES 4

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                half4 _Tint;
                float _Aspect;
                float _Depth;
                float _Speed;
                float _Frequency;
                float _Decay;
                float _Enter;
                float4 _EnterOrigin;
            CBUFFER_END

            // Set per painting from C# (MaterialPropertyBlock):
            // xy = touch point in uv, z = start time (Time.timeSinceLevelLoad), w = amplitude (0 = slot unused)
            float4 _Ripples[MAX_RIPPLES];

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            // Sum of damped circular waves. Returns height in [-1, 1]-ish and its uv gradient.
            float RippleHeight(float2 uv, out float2 grad)
            {
                float h = 0;
                grad = 0;
                float2 scale = float2(_Aspect, 1);
                [unroll]
                for (int i = 0; i < MAX_RIPPLES; i++)
                {
                    float4 r = _Ripples[i];
                    float t = _Time.y - r.z;
                    float active = step(0, t) * step(1e-4, r.w);
                    float2 d2 = (uv - r.xy) * scale;
                    float d = length(d2) + 1e-5;
                    float behind = t * _Speed - d;                   // > 0 once the wavefront has passed
                    float env = saturate(behind * 6) * exp(-max(t, 0) * _Decay) * exp(-d * 1.5) * r.w * active;
                    float phase = behind * _Frequency;
                    h += sin(phase) * env;
                    grad += -cos(phase) * _Frequency * env * (d2 / d) * scale;
                }
                return h;
            }

            float EnterFalloff(float2 uv)
            {
                return exp(-length((uv - _EnterOrigin.xy) * float2(_Aspect, 1)) * 2.5);
            }

            Varyings vert(Attributes v)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

                float2 grad;
                float h = RippleHeight(v.uv, grad);
                float3 pos = v.positionOS.xyz;
                // The mesh normal faces the viewer; push "into" the wall.
                pos -= v.normalOS * (h * _Depth + _Enter * EnterFalloff(v.uv) * _Depth * 6);

                o.positionCS = TransformObjectToHClip(pos);
                o.uv = v.uv;   // raw 0..1: ripples/touch live here; the crop is applied when sampling
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);

                float2 grad;
                RippleHeight(i.uv, grad);
                float2 uv = i.uv + grad * 0.0025;                     // refraction

                // Jump-in: swirl + zoom toward the touch point.
                float2 o = _EnterOrigin.xy;
                float2 v = uv - o;
                float fall = EnterFalloff(uv);
                float ang = _Enter * 7.0 * fall;
                float s, c;
                sincos(ang, s, c);
                v = float2(c * v.x - s * v.y, s * v.x + c * v.y) * (1 - 0.65 * _Enter);
                uv = o + v;

                half4 col = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uv * _MainTex_ST.xy + _MainTex_ST.zw) * _Tint;
                col.rgb *= 1 + clamp(grad.x * 0.004 + grad.y * 0.006, -0.25, 0.35);   // slope shading
                col.rgb = lerp(col.rgb, half3(1, 1, 1), saturate(_Enter * 0.8 * fall));
                col.a = 1;
                return col;
            }
            ENDHLSL
        }
    }
    FallBack Off
}
