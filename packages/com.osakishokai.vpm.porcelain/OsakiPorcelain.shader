// 大崎商会の標準の陶器・磁器。見る向きに付く照り返し(matcap の考え)で、映す物の少ないワールドでもつやが見える。
// 元は FlatCafeWorldDev の FCWD_Porcelain(CafePlume #23 でぱんが通した見た目)。違いは焼いた光(ライトマップ)を受けることだけ:
// 焼いたワールドでは Standard に戻すしかなく、つや 1 でもマットに見えた(ObaachanNoIe #77)。
// _MetalMask の R = 金(縁の金彩など)。不透明・Cull Back・Quest 可。焼く時の色と影は Fallback の Standard から借りる。
Shader "Osaki/VPM/Porcelain"
{
    Properties
    {
        _Color ("地の色", Color) = (1, 1, 1, 1)
        _MainTex ("地の絵", 2D) = "white" {}
        _MinLight ("暗いワールドでも沈まない明るさ", Range(0, 1)) = 0.55
        _Gloss ("照り返しの鋭さ", Range(8, 512)) = 160
        _Studio ("見る向きに付いた照り返しの強さ", Range(0, 2)) = 1.1
        _Reflect ("映り込みの強さ(縁ほど強い)", Range(0, 1)) = 0.35
        [NoScaleOffset] _MetalMask ("金のマスク(R = 金。sRGB オフ)", 2D) = "black" {}
        _Metal ("金の照り返しの強さ", Range(0, 3)) = 1.4
    }
    SubShader
    {
        Tags { "Queue"="Geometry" "RenderType"="Opaque" }
        Pass
        {
            Tags { "LightMode"="ForwardBase" }
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fwdbase
            #pragma multi_compile_fog
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"
            #include "Lighting.cginc"

            fixed4 _Color;
            sampler2D _MainTex;
            sampler2D _MetalMask;
            float4 _MainTex_ST;
            float _MinLight, _Gloss, _Studio, _Reflect, _Metal;

            struct appdata { float4 vertex : POSITION; float3 normal : NORMAL; float2 uv : TEXCOORD0; float2 uv1 : TEXCOORD1; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 wn : TEXCOORD0;
                float3 wp : TEXCOORD1;
                float2 uv : TEXCOORD3;
                float2 lm : TEXCOORD4;
                UNITY_FOG_COORDS(2)
                UNITY_VERTEX_OUTPUT_STEREO
            };

            v2f vert (appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.pos = UnityObjectToClipPos(v.vertex);
                o.wn = UnityObjectToWorldNormal(v.normal);
                o.wp = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                o.lm = v.uv1 * unity_LightmapST.xy + unity_LightmapST.zw;
                UNITY_TRANSFER_FOG(o, o.pos);
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float3 n = normalize(i.wn);
                float3 vdir = normalize(_WorldSpaceCameraPos - i.wp);
                float3 ld = normalize(_WorldSpaceLightPos0.xyz + float3(0, 1e-4, 0));
                float ndl = saturate(dot(n, ld));
            #ifdef LIGHTMAP_ON
                float3 light = DecodeLightmap(UNITY_SAMPLE_TEX2D(unity_Lightmap, i.lm)) + _LightColor0.rgb * ndl;
            #else
                float3 light = _LightColor0.rgb * ndl + ShadeSH9(float4(n, 1));
            #endif
                float3 albedo = _Color.rgb * tex2D(_MainTex, i.uv).rgb;
                float metal = tex2D(_MetalMask, i.uv).r;
                float3 diffuse = albedo * max(light, _MinLight.xxx) * (1 - 0.7 * metal);

                float3 h = normalize(ld + vdir);
                float spec = pow(saturate(dot(n, h)), _Gloss) * 1.5;
                float3 col = diffuse + spec * _LightColor0.rgb;

                float3 vn = normalize(mul((float3x3)UNITY_MATRIX_V, n));
                float s1 = pow(saturate(dot(vn, normalize(float3(-0.45, 0.55, 0.70)))), _Gloss);
                float s2 = pow(saturate(dot(vn, normalize(float3(0.55, -0.25, 0.80)))), _Gloss * 2) * 0.5;
                col += (s1 + s2) * _Studio;

                float fres = pow(1 - saturate(dot(n, vdir)), 4);
                float3 r = reflect(-vdir, n);
                half4 env = UNITY_SAMPLE_TEXCUBE_LOD(unity_SpecCube0, r, 0);
                float3 envc = DecodeHDR(env, unity_SpecCube0_HDR);
                col = lerp(col, envc, saturate(_Reflect * (0.15 + fres)) * (1 - metal));

                float g1 = pow(saturate(dot(vn, normalize(float3(-0.45, 0.55, 0.70)))), 12);
                float g2 = pow(saturate(dot(n, h)), 24);
                col += metal * albedo * (envc * 0.8 + 0.35 + (g1 + g2 * _LightColor0.rgb) * _Metal);

                fixed4 o = fixed4(col, 1);
                UNITY_APPLY_FOG(i.fogCoord, o);
                return o;
            }
            ENDCG
        }
    }
    Fallback "Standard"
}
