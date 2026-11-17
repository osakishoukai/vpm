
Shader "Osaki/VPM/HelloPen/Glow"
{
    Properties
    {
        _Color ("Color", Color) = (1, 0.45, 0.55, 1)
        _Alpha ("Alpha", Range(0,1)) = 0.35
    }
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" "IgnoreProjector"="True" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Cull Back
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"
            fixed4 _Color; float _Alpha;
            struct appdata { float4 vertex : POSITION; float3 normal : NORMAL; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct v2f { float4 pos : SV_POSITION; float rim : TEXCOORD0; UNITY_VERTEX_OUTPUT_STEREO };
            v2f vert (appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.pos = UnityObjectToClipPos(v.vertex);
                float3 n = normalize(UnityObjectToWorldNormal(v.normal));
                float3 vd = normalize(WorldSpaceViewDir(v.vertex));
                o.rim = 1 - abs(dot(n, vd));
                return o;
            }
            fixed4 frag (v2f i) : SV_Target
            {
                float pulse = 0.8 + 0.2 * sin(_Time.y * 8);
                float a = _Alpha * (0.35 + 0.65 * i.rim * i.rim) * pulse;
                return fixed4(_Color.rgb, a);
            }
            ENDCG
        }
    }
}
