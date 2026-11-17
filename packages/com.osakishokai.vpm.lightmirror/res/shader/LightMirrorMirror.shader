

Shader "Osaki/VPM/LightMirror/Mirror"
{
    Properties
    {
        [NoScaleOffset] _Mask("Mask (白=鏡)", 2D) = "white" {}
        [Toggle] _UseBg("Use Background", Float) = 0
        _BgColor("Background Color", Color) = (0.16,0.17,0.2,1)
        [Toggle] _UseBgTex("Use Background Texture", Float) = 0
        _BgTex("Background", 2D) = "black" {}
        [HideInInspector] _ReflectionTex0("", 2D) = "white" {}
        [HideInInspector] _ReflectionTex1("", 2D) = "white" {}
    }
    SubShader
    {
        Tags { "RenderType"="TransparentCutout" "Queue"="AlphaTest" }
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "LightMirrorCommon.cginc"

            sampler2D _ReflectionTex0;
            sampler2D _ReflectionTex1;
            sampler2D _BgTex;
            float4 _BgTex_ST;
            half4 _BgColor;
            float _UseBg;
            float _UseBgTex;

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; float4 refl : TEXCOORD1; UNITY_VERTEX_OUTPUT_STEREO };

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_OUTPUT(v2f, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                o.refl = ComputeNonStereoScreenPos(o.pos);
                return o;
            }

            half4 frag(v2f i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                LightMirrorClip(i.uv);
                half4 refl = unity_StereoEyeIndex == 0 ? tex2Dproj(_ReflectionTex0, UNITY_PROJ_COORD(i.refl)) : tex2Dproj(_ReflectionTex1, UNITY_PROJ_COORD(i.refl));
                if (_UseBg > 0.5)
                {

                    half3 bg = _UseBgTex > 0.5 ? tex2D(_BgTex, TRANSFORM_TEX(i.uv, _BgTex)).rgb : _BgColor.rgb;
                    refl.rgb = lerp(bg, refl.rgb, saturate(refl.a));
                }
                return half4(refl.rgb, 1);
            }
            ENDCG
        }
    }
}
