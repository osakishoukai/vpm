
Shader "Osaki/VPM/LightMirror/Probe"
{
    Properties
    {
        [NoScaleOffset] _Mask("Mask (白=鏡)", 2D) = "white" {}
        _Tint("Tint", Color) = (1,1,1,1)
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
            #include "UnityStandardUtils.cginc"

            half4 _Tint;

            struct appdata { float4 vertex : POSITION; float3 normal : NORMAL; float2 uv : TEXCOORD0; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; float3 wpos : TEXCOORD1; float3 wnormal : TEXCOORD2; UNITY_VERTEX_OUTPUT_STEREO };

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_OUTPUT(v2f, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                o.wpos = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.wnormal = UnityObjectToWorldNormal(v.normal);
                return o;
            }

            half4 frag(v2f i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                LightMirrorClip(i.uv);
                float3 n = normalize(i.wnormal);
                float3 v = normalize(i.wpos - _WorldSpaceCameraPos);
                float3 r = reflect(v, n);
                #if UNITY_SPECCUBE_BOX_PROJECTION
                r = BoxProjectedCubemapDirection(r, i.wpos, unity_SpecCube0_ProbePosition, unity_SpecCube0_BoxMin, unity_SpecCube0_BoxMax);
                #endif
                half4 c = UNITY_SAMPLE_TEXCUBE_LOD(unity_SpecCube0, r, 0);
                return half4(DecodeHDR(c, unity_SpecCube0_HDR) * _Tint.rgb, 1);
            }
            ENDCG
        }
    }
}
