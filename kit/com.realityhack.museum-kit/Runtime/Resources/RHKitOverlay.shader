// Camera-attached overlay for the kit splash / return prompt. Plain CG with no LightMode tag, so it
// renders in both the built-in pipeline and URP (as SRPDefaultUnlit). Lives in Resources so it ships.
Shader "Hidden/RHKit/Overlay"
{
    Properties { _Color ("Color", Color) = (0, 0, 0, 1) }
    SubShader
    {
        Tags { "Queue" = "Overlay+200" "RenderType" = "Transparent" "IgnoreProjector" = "True" }
        ZTest Always ZWrite Off Cull Off
        Blend SrcAlpha OneMinusSrcAlpha
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"
            fixed4 _Color;
            struct appdata { float4 vertex : POSITION; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct v2f { float4 pos : SV_POSITION; UNITY_VERTEX_OUTPUT_STEREO };
            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.pos = UnityObjectToClipPos(v.vertex);
                return o;
            }
            fixed4 frag(v2f i) : SV_Target { return _Color; }
            ENDCG
        }
    }
}
