

Shader "Osaki/VPM/Dimmer/Overlay"
{
    Properties
    {
        _Brightness ("Brightness (0-1)", Range(0, 1)) = 1
    }
    SubShader
    {

        Tags { "Queue" = "Overlay+999" "RenderType" = "Overlay" "IgnoreProjector" = "True" "DisableBatching" = "True" "PreviewType" = "Plane" }
        Pass
        {
            Blend DstColor Zero, Zero One
            ZWrite Off
            ZTest Always
            Cull Off
            Lighting Off
            Fog { Mode Off }

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            float _Brightness;
            float _VRChatMirrorMode;

            struct appdata
            {
                float4 vertex : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_OUTPUT(v2f, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

                o.pos = float4(v.vertex.x * 2.0, v.vertex.y * 2.0, UNITY_NEAR_CLIP_VALUE * 0.5, 1.0);

                if (_VRChatMirrorMode > 0.5) o.pos = float4(0, 0, 0, 0);
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float b = saturate(_Brightness);
            #ifndef UNITY_COLORSPACE_GAMMA
                b = pow(b, 2.2);
            #endif
                return float4(b, b, b, 1);
            }
            ENDCG
        }
    }
    Fallback Off
}
