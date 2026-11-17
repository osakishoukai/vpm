
Shader "Osaki/VPM/HelloPen/Plane"
{
    Properties
    {
        _Color ("Color", Color) = (0.75, 0.85, 1, 1)
        _Alpha ("Alpha", Range(0,1)) = 0.3
        _Grid ("Grid (cells across)", Float) = 8
    }
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" "IgnoreProjector"="True" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Cull Off
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"
            fixed4 _Color; float _Alpha; float _Grid;
            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; UNITY_VERTEX_OUTPUT_STEREO };
            v2f vert (appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }
            fixed4 frag (v2f i) : SV_Target
            {
                float2 c = i.uv * 2 - 1;
                float r = length(c);
                float disc = saturate((1 - r) / 0.35);
                float2 g = abs(frac(i.uv * _Grid) - 0.5);
                float2 w = fwidth(i.uv * _Grid);
                float grid = 1 - saturate(min((0.5 - g.x) / w.x, (0.5 - g.y) / w.y) - 0.5);
                float a = disc * (0.25 + 0.75 * grid) * _Alpha;
                return fixed4(_Color.rgb, a);
            }
            ENDCG
        }
    }
}
