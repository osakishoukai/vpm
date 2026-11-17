// 大崎商会の標準のガラス(UT#226。ぱんが GlassLab で選んだ。材質は OsakiGlass_Standard / _Kiriko / _Frost)。透明で向こうが見え、景色(反射プローブ)を映す。
//   1 パス目: 向こうの色に色ガラスの色を掛ける(うす緑など。_Color.a = 0 なら何もしない)
//   2 パス目: 映り込み(正面は弱く、斜めほど強い)・ハイライト・MatCap・すりの白い膜を重ねる
// 板は表と裏を別の面で作る(Cull Back)。面ごとに反射プローブを選べる(外向きの面は外、内向きの面は部屋)。
Shader "Osaki/VPM/Glass"
{
    Properties
    {
        _Color ("Tint (RGB) / Tint Amount (A)", Color) = (1,1,1,0)
        [Normal] _BumpMap ("Normal Map", 2D) = "bump" {}
        _BumpScale ("Normal Strength", Range(0,2)) = 0.2
        _Smoothness ("Smoothness", Range(0,1)) = 0.95
        _F0 ("Front Reflectance", Range(0,0.5)) = 0.05
        _ReflectionStrength ("Reflection Strength", Range(0,3)) = 1
        _SpecularStrength ("Specular Strength", Range(0,4)) = 1
        _MatCap ("MatCap", 2D) = "black" {}
        _MatCapStrength ("MatCap Strength", Range(0,2)) = 0
        _Frost ("Frost (milky veil)", Range(0,1)) = 0
        _FrostSmoothness ("Frost Smoothness", Range(0,1)) = 0.45
        _FrostMask ("Frost Mask (R)", 2D) = "white" {}
        [Enum(Back,2,Off,0)] _Cull ("Cull", Float) = 2
    }

    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" }

        CGINCLUDE
        #include "UnityCG.cginc"
        #include "Lighting.cginc"

        sampler2D _BumpMap, _MatCap, _FrostMask;
        float4 _BumpMap_ST;
        half4 _Color;
        half _BumpScale, _Smoothness, _F0, _ReflectionStrength, _SpecularStrength, _MatCapStrength, _Frost, _FrostSmoothness;

        struct appdata {
            float4 vertex : POSITION;
            float3 normal : NORMAL;
            float4 tangent : TANGENT;
            float2 uv : TEXCOORD0;
            UNITY_VERTEX_INPUT_INSTANCE_ID
        };
        ENDCG

        Pass
        {
            Name "Tint"
            Blend DstColor Zero
            ZWrite Off
            Cull [_Cull]

            CGPROGRAM
            #pragma target 3.0
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing

            struct v2f {
                float4 pos : SV_POSITION;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            v2f vert(appdata v) {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_OUTPUT(v2f, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.pos = UnityObjectToClipPos(v.vertex);
                return o;
            }

            half4 frag(v2f i) : SV_Target {
                return half4(lerp(half3(1,1,1), _Color.rgb, _Color.a), 1);
            }
            ENDCG
        }

        Pass
        {
            Name "Surface"
            Tags { "LightMode"="ForwardBase" }
            Blend One OneMinusSrcAlpha
            ZWrite Off
            Cull [_Cull]

            CGPROGRAM
            #pragma target 3.0
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma multi_compile_fog

            struct v2f {
                float4 pos : SV_POSITION;
                float3 worldPos : TEXCOORD0;
                half3 worldNormal : TEXCOORD1;
                half3 worldTangent : TEXCOORD2;
                half3 worldBitangent : TEXCOORD3;
                float2 uv : TEXCOORD4;
                UNITY_FOG_COORDS(5)
                UNITY_VERTEX_OUTPUT_STEREO
            };

            v2f vert(appdata v) {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_OUTPUT(v2f, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.pos = UnityObjectToClipPos(v.vertex);
                o.worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
                half3 n = UnityObjectToWorldNormal(v.normal);
                half3 t = UnityObjectToWorldDir(v.tangent.xyz);
                o.worldNormal = n;
                o.worldTangent = t;
                o.worldBitangent = cross(n, t) * v.tangent.w * unity_WorldTransformParams.w;
                o.uv = TRANSFORM_TEX(v.uv, _BumpMap);
                UNITY_TRANSFER_FOG(o, o.pos);
                return o;
            }

            half4 frag(v2f i, fixed facing : VFACE) : SV_Target {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);

                half3 nts = UnpackNormal(tex2D(_BumpMap, i.uv));
                nts.xy *= _BumpScale;
                half3 n = normalize(i.worldTangent * nts.x + i.worldBitangent * nts.y + i.worldNormal * nts.z);
                n *= facing > 0 ? 1 : -1;

                half3 v = normalize(_WorldSpaceCameraPos.xyz - i.worldPos);
                half ndv = saturate(dot(n, v));
                half veil = _Frost * tex2D(_FrostMask, i.uv).r;
                half smooth = lerp(_Smoothness, _FrostSmoothness, veil);

                // 映り込み: 正面は _F0、斜めほど強い
                half fres = _F0 + (1 - _F0) * pow(1 - ndv, 5);
                float3 r = reflect(-v, n);
                #ifdef UNITY_SPECCUBE_BOX_PROJECTION
                    r = BoxProjectedCubemapDirection(r, i.worldPos, unity_SpecCube0_ProbePosition, unity_SpecCube0_BoxMin, unity_SpecCube0_BoxMax);
                #endif
                half rough = 1 - smooth;
                half mip = rough * (1.7h - 0.7h * rough) * UNITY_SPECCUBE_LOD_STEPS;
                half3 refl = DecodeHDR(UNITY_SAMPLE_TEXCUBE_LOD(unity_SpecCube0, r, mip), unity_SpecCube0_HDR) * _ReflectionStrength;

                // 光の向き: 実時間のライトが無い(焼きだけの)ワールドではライトプローブの向きを使う
                float3 mainLight = _WorldSpaceLightPos0.xyz - i.worldPos * _WorldSpaceLightPos0.w;
                float3 probeDir = unity_SHAr.xyz * 0.2126 + unity_SHAg.xyz * 0.7152 + unity_SHAb.xyz * 0.0722;
                bool hasLight = dot(mainLight, mainLight) > 1e-6;
                half3 l = hasLight ? normalize(mainLight) : (dot(probeDir, probeDir) > 1e-6 ? normalize(probeDir) : half3(0, 1, 0));
                half3 amb = max(0, ShadeSH9(half4(n, 1)));
                half3 ambBack = max(0, ShadeSH9(half4(-n, 1)));
                half3 lightCol = hasLight ? _LightColor0.rgb : max(amb, ambBack);
                half3 h = normalize(l + v);
                half spec = pow(saturate(dot(n, h)), exp2(1 + smooth * 10)) * _SpecularStrength * smooth;

                half3 worldViewUp = normalize(half3(0, 1, 0) - v * v.y);
                half3 worldViewRight = normalize(cross(v, worldViewUp));
                half2 mcUV = half2(dot(worldViewRight, n), dot(worldViewUp, n)) * 0.5h + 0.5h;
                half3 matcap = tex2D(_MatCap, mcUV).rgb * _MatCapStrength;

                // すりの白い膜: 表と裏の両方の光で光る
                half3 veilCol = (amb + ambBack) * 0.5h * lerp(half3(1, 1, 1), _Color.rgb, _Color.a);

                half3 col = refl * fres + lightCol * spec + matcap * max(lightCol, 0.15h) + veilCol * veil * (1 - fres);
                half alpha = 1 - (1 - veil) * (1 - fres);
                UNITY_APPLY_FOG_COLOR(i.fogCoord, col, half4(0, 0, 0, 0));
                return half4(col, alpha);
            }
            ENDCG
        }
    }
    Fallback Off
}
