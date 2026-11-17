Shader "Osaki/VPM/OsaToon/GlossOnly"
{
    Properties
    {
        [Normal] _BumpMap ("Normal Map", 2D) = "bump" {}
        _BumpScale ("Normal Strength", Range(0,2)) = 1
        _SpecularStrength ("Specular Strength", Range(0,2)) = 1
        _Smoothness ("Smoothness", Range(0,1)) = 0.85
        _ReflectionStrength ("Reflection Strength", Range(0,2)) = 0.35
        // Own copy of a Reflection Probe for GPU-instanced draws (see OsaToonCommon.cginc).
        [Toggle(_REFLECTIONCUBE)] _UseReflectionCube ("Reflection Cube (instancing-safe probe copy)", Float) = 0
        [NoScaleOffset] _ReflectionCube ("Reflection Cube", CUBE) = "" {}
        _ReflectionCubeIntensity ("Reflection Cube Intensity", Range(0, 2)) = 1
        _MatCap ("MatCap", 2D) = "black" {}
        _MatCapStrength ("MatCap Strength", Range(0,2)) = 1
        _Visibility ("Gloss Visibility", Range(0,4)) = 1
        _MinAlpha ("Minimum Alpha", Range(0,0.25)) = 0
        [Enum(Back,2,Off,0)] _Cull ("Cull", Float) = 2
    }

    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" }

        Pass
        {
            Name "ForwardBase"
            Tags { "LightMode"="ForwardBase" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull [_Cull]

            CGPROGRAM
            #pragma target 3.0
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma multi_compile_fog
            #pragma shader_feature_local _REFLECTIONCUBE
            #include "UnityCG.cginc"
            #include "Lighting.cginc"
            #include "OsaToonCommon.cginc"

            sampler2D _BumpMap;
            float4 _BumpMap_ST;
            sampler2D _MatCap;
            half _BumpScale, _SpecularStrength, _Smoothness;
            half _ReflectionStrength, _MatCapStrength, _Visibility, _MinAlpha;

            // Same stable world-up/view-direction basis used by OsakiFoodProduct.
            half2 MatcapUV(half3 viewDir, half3 worldNormal)
            {
                const half3 worldUp = half3(0, 1, 0);
                half3 worldViewUp = normalize(worldUp - viewDir * dot(viewDir, worldUp));
                half3 worldViewRight = normalize(cross(viewDir, worldViewUp));
                return half2(dot(worldViewRight, worldNormal),
                             dot(worldViewUp, worldNormal)) * 0.5h + 0.5h;
            }

            struct appdata {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                float4 tangent : TANGENT;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f {
                float4 pos : SV_POSITION;
                float3 worldPos : TEXCOORD0;
                half3 worldNormal : TEXCOORD1;
                half3 worldTangent : TEXCOORD2;
                half3 worldBitangent : TEXCOORD3;
                float2 uv : TEXCOORD4;
                UNITY_FOG_COORDS(5)
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            v2f vert(appdata v) {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_OUTPUT(v2f, o);
                UNITY_TRANSFER_INSTANCE_ID(v, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.pos = UnityObjectToClipPos(v.vertex);
                o.worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
                half3 n = UnityObjectToWorldNormal(v.normal);
                half3 t = UnityObjectToWorldDir(v.tangent.xyz);
                half s = v.tangent.w * unity_WorldTransformParams.w;
                o.worldNormal = normalize(n);
                o.worldTangent = normalize(t);
                o.worldBitangent = normalize(cross(n,t) * s);
                o.uv = TRANSFORM_TEX(v.uv, _BumpMap);
                UNITY_TRANSFER_FOG(o, o.pos);
                return o;
            }

            half4 frag(v2f i) : SV_Target {
                UNITY_SETUP_INSTANCE_ID(i);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);

                half3 nts = UnpackNormal(tex2D(_BumpMap, i.uv));
                nts.xy *= _BumpScale;
                nts = normalize(nts);
                half3 n = normalize(i.worldTangent*nts.x + i.worldBitangent*nts.y + i.worldNormal*nts.z);

                half3 v = normalize(_WorldSpaceCameraPos.xyz - i.worldPos);
                half3 l = OsaToonGetLightDir(i.worldPos);
                half3 h = normalize(l + v);

                half specPower = exp2(1.0h + _Smoothness * 10.0h);
                half spec = pow(saturate(dot(n,h)), specPower) * _SpecularStrength * _Smoothness;

                half3 r = reflect(-v,n);
                half mip = (1.0h - _Smoothness) * UNITY_SPECCUBE_LOD_STEPS;
                half3 reflection = OsaToonSampleReflection(r, mip) * _ReflectionStrength;

                // MatCap uses the same UV behavior as OsakiFoodProduct and the
                // final normal, so Normal Map detail also affects the MatCap.
                half2 matcapUV = MatcapUV(v, n);
                half3 matcap = tex2D(_MatCap, matcapUV).rgb * _MatCapStrength;

                half3 gloss = reflection + spec.xxx + matcap;
                half lum = dot(gloss, half3(0.2126h,0.7152h,0.0722h));
                half alpha = max(_MinAlpha, saturate(lum * _Visibility));

                half4 result = half4(gloss, alpha);
                UNITY_APPLY_FOG(i.fogCoord, result);
                return result;
            }
            ENDCG
        }
    }
    Fallback Off
}
