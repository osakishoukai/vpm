// OsaToon v1.0
// Lightweight GPU-instancing-capable shader for VRChat pickup items.
//
// Philosophy:
//   - Preserve authored albedo; never create deep realtime-lighting darkness.
//   - Still react convincingly to light direction, Light Probes and normal maps.
//   - Provide a small set of material primitives for dry/wet/plastic/metal looks.
//   - Opaque/Cutout only. No realtime shadow receiving and no ShadowCaster pass.
//
// Packed material mask channels:
//   R = Metal
//   G = MatCap
//   B = Emission
//   A = Smoothness
// Default packed mask is black for fail-safe behavior when no mask is assigned.
// Assign a packed mask when using smoothness/metal/matcap/emission features. Without one the mask is not sampled at all
// (the unassigned default texture is detected by its 4 px size), so a mask must be larger than 4 px.
//
// Quest cost notes: no specular highlight (gloss comes from MatCap and the reflection cube; with baked lighting the
// highlight had only a probe-guessed direction, and no material in use had it visible), and the Light Probe tint is
// evaluated per vertex unless a normal map needs it per pixel.
//
// Quest compatibility means the shader is intended to compile/render on the
// VRChat Android/Quest path rather than becoming an unsupported/pink material.
Shader "Osaki/VPM/OsaToon"
{
    Properties
    {
        _MainTex ("Main Texture", 2D) = "white" {}
        // Tint multiplied into the albedo. Leave white when the albedo texture carries the
        // colour; use it for solid-colour materials (black plastic) and to tint metals.
        _Color ("Color", Color) = (1, 1, 1, 1)
        [Toggle(_ATLAS_4X4)] _UseAtlas ("Atlas 4x4 Grid", Float) = 0
        _VariantID ("Variant ID", Float) = 0
        [Enum(Back,2,Off,0)] _Cull ("Culling", Float) = 2

        [Toggle(_NORMALMAP)] _UseNormalMap ("Normal Map", Float) = 0
        [Normal] _BumpMap ("Normal Map", 2D) = "bump" {}
        _BumpScale ("Normal Strength", Range(0, 2)) = 1

        _LightingStrength ("Lighting Strength", Range(0, 1)) = 1

        _PackedMask ("Packed Mask (R Metal / G MatCap / B Emission / A Smooth)", 2D) = "black" {}
        _Metallic ("Metal Strength", Range(0, 1)) = 0
        _Smoothness ("Smoothness", Range(0, 1)) = 1.0

        // Kept so existing materials load unchanged; the specular highlight itself was removed (see the header).
        [HideInInspector] _UseSpecular ("Specular (removed)", Float) = 0
        [HideInInspector] _SpecularStrength ("Specular Strength (removed)", Range(0, 2)) = 0.35

        [Toggle(_METALREFLECTION)] _UseMetalReflection ("Reflection (metal, and gloss on non-metal)", Float) = 0
        // Probe/cube reflection added on non-metal surfaces (glossy plastic). 0 = metal only.
        _ReflectionStrength ("Non-metal Reflection Strength", Range(0, 2)) = 0

        // Own copy of a Reflection Probe for GPU-instanced draws (see OsaToonCommon.cginc).
        [Toggle(_REFLECTIONCUBE)] _UseReflectionCube ("Reflection Cube (instancing-safe probe copy)", Float) = 0
        [NoScaleOffset] _ReflectionCube ("Reflection Cube", CUBE) = "" {}
        _ReflectionCubeIntensity ("Reflection Cube Intensity", Range(0, 2)) = 1

        [Toggle(_EMISSION)] _UseEmission ("Emission", Float) = 0
        [HDR] _EmissionMultiplier ("Emission HDR Multiplier", Range(1, 16)) = 2

        [Toggle(_MATCAP)] _UseMatCap ("MatCap", Float) = 0
        _MatCap ("MatCap Texture", 2D) = "white" {}
        _MatCapStrength ("MatCap Strength", Range(0, 2)) = 1
        [Enum(Add,0,Multiply,1,Replace,2)] _MatCapBlend ("MatCap Blend", Float) = 0

        [Toggle(_ALPHATEST_ON)] _UseCutout ("Alpha Cutout", Float) = 0
        _Cutoff ("Cutoff", Range(0, 1)) = 0.5
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        LOD 150
        Cull [_Cull]

        Pass
        {
            Name "ForwardBase"
            Tags { "LightMode"="ForwardBase" }

            CGPROGRAM
            #pragma target 3.0
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma multi_compile_fog

            // Disabled optional features compile out, including their texture
            // samples and (for normal mapping) tangent interpolators.
            #pragma shader_feature_local _NORMALMAP
            #pragma shader_feature_local _METALREFLECTION
            #pragma shader_feature_local _REFLECTIONCUBE
            #pragma shader_feature_local _EMISSION
            #pragma shader_feature_local _MATCAP
            #pragma shader_feature_local _ALPHATEST_ON
            #pragma shader_feature_local _ATLAS_4X4

            #include "UnityCG.cginc"
            #include "Lighting.cginc"
            #include "OsaToonCommon.cginc"

            sampler2D _MainTex;
            float4 _MainTex_ST;
            half4 _Color;
            sampler2D _PackedMask;
            float4 _PackedMask_TexelSize;

            half _Metallic;
            half _Smoothness;
            half _LightingStrength;
            half _ReflectionStrength;
            half _Cull;

            #if defined(_NORMALMAP)
                sampler2D _BumpMap;
                half _BumpScale;
            #endif

            #if defined(_EMISSION)
                half _EmissionMultiplier;
            #endif

            #if defined(_MATCAP)
                sampler2D _MatCap;
                half _MatCapStrength;
                half _MatCapBlend;
            #endif

            #if defined(_ALPHATEST_ON)
                half _Cutoff;
            #endif

            UNITY_INSTANCING_BUFFER_START(Props)
                UNITY_DEFINE_INSTANCED_PROP(float, _VariantID)
            UNITY_INSTANCING_BUFFER_END(Props)

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                #if defined(_NORMALMAP)
                    float4 tangent : TANGENT;
                #endif
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 worldPos : TEXCOORD1;
                half3 worldNormal : TEXCOORD2;

                #if defined(_NORMALMAP)
                    half3 worldTangent : TEXCOORD3;
                    half3 worldBitangent : TEXCOORD4;
                #endif

                UNITY_FOG_COORDS(5)
                #if !defined(_NORMALMAP)
                    // Light Probe tint for the face and for its back (two-sided materials turn the normal to the viewer).
                    half3 probeTint : TEXCOORD6;
                    half3 probeTintBack : TEXCOORD7;
                #endif
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            // Light Probes are deliberately used as a directional/color cue, not
            // as permission to make a pickup dark. Normalize SH luminance around
            // 1.0 and apply only a restrained amount of its color/directional bias.
            half3 GetProbeTint(half3 worldNormal)
            {
                half3 sh = max(ShadeSH9(half4(worldNormal, 1.0h)), 0.0h);
                half lum = dot(sh, half3(0.2126h, 0.7152h, 0.0722h));
                half valid = step(0.001h, lum);
                half3 normalizedSH = sh / max(lum, 0.001h);
                // Prevent extreme probe chroma from producing pathological tinting.
                normalizedSH = clamp(normalizedSH, 0.5h.xxx, 2.0h.xxx);
                half3 tint = lerp(1.0h.xxx, normalizedSH, 0.15h);
                return lerp(1.0h.xxx, tint, valid);
            }

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_OUTPUT(v2f, o);
                UNITY_TRANSFER_INSTANCE_ID(v, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

                o.pos = UnityObjectToClipPos(v.vertex);
                o.worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.worldNormal = UnityObjectToWorldNormal(v.normal);

                #if defined(_ATLAS_4X4)
                    float variantID = round(UNITY_ACCESS_INSTANCED_PROP(Props, _VariantID));
                    float col = fmod(variantID, 4.0);
                    float row = floor(variantID * 0.25 + 0.001);
                    float2 baseUV = v.uv * _MainTex_ST.xy + _MainTex_ST.zw;
                    float2 innerUV = baseUV * (496.0 / 512.0) + (8.0 / 512.0);
                    o.uv = innerUV * 0.25 + float2(col * 0.25, (3.0 - row) * 0.25);
                #else
                    o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                #endif

                #if defined(_NORMALMAP)
                    o.worldTangent = UnityObjectToWorldDir(v.tangent.xyz);
                    half tangentSign = v.tangent.w * unity_WorldTransformParams.w;
                    o.worldBitangent = cross(o.worldNormal, o.worldTangent) * tangentSign;
                #endif

                #if !defined(_NORMALMAP)
                    half3 vn = normalize(o.worldNormal);
                    o.probeTint = GetProbeTint(vn);
                    o.probeTintBack = GetProbeTint(-vn);
                #endif

                UNITY_TRANSFER_FOG(o, o.pos);
                return o;
            }

            half3 GetWorldNormal(v2f i)
            {
                half3 n = normalize(i.worldNormal);

                #if defined(_NORMALMAP)
                    half3 t = normalize(i.worldTangent);
                    half3 b = normalize(i.worldBitangent);
                    half3 normalTS = UnpackNormal(tex2D(_BumpMap, i.uv));
                    normalTS.xy *= _BumpScale;
                    normalTS.z = sqrt(saturate(1.0h - dot(normalTS.xy, normalTS.xy)));
                    n = normalize(t * normalTS.x + b * normalTS.y + n * normalTS.z);
                #endif

                return n;
            }

            // Match OsakiFoodProduct / VRChat Toon Standard GetVRMatcapUV:
            // build a stable view-oriented basis from world up and view direction.
            // The final world normal (including the normal map) drives the MatCap.
            half2 GetMatCapUV(half3 viewDir, half3 worldNormal)
            {
                const half3 worldUp = half3(0.0h, 1.0h, 0.0h);
                half3 worldViewUp = normalize(worldUp - viewDir * dot(viewDir, worldUp));
                half3 worldViewRight = normalize(cross(viewDir, worldViewUp));
                return half2(dot(worldViewRight, worldNormal),
                             dot(worldViewUp, worldNormal)) * 0.5h + 0.5h;
            }

            half4 frag(v2f i) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(i);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);

                half4 baseSample = tex2D(_MainTex, i.uv);

                #if defined(_ALPHATEST_ON)
                    clip(baseSample.a - _Cutoff);
                #endif

                half3 albedo = baseSample.rgb * _Color.rgb;
                // No mask assigned: Unity binds its 4 px default (black), so skip the fetch. The branch is uniform per draw.
                half4 mask = 0.0h;
                float2 maskDx = ddx(i.uv), maskDy = ddy(i.uv);
                UNITY_BRANCH
                if (_PackedMask_TexelSize.z > 4.5)
                    mask = tex2Dgrad(_PackedMask, i.uv, maskDx, maskDy);
                half metallic = saturate(mask.r * _Metallic);
                half smoothness = saturate(mask.a * _Smoothness);

                half3 normal = GetWorldNormal(i);
                half3 viewDir = normalize(_WorldSpaceCameraPos.xyz - i.worldPos);
                // Two-sided (Cull Off) materials: turn the normal toward the viewer so back faces,
                // and meshes exported with flipped normals, light and reflect like front faces.
                bool flipped = _Cull < 0.5h && dot(normal, viewDir) < 0.0h;
                if (flipped)
                    normal = -normal;
                half3 lightDir = OsaToonGetLightDir(i.worldPos);

                // OsaToon lighting: direction is visible, darkness is not.
                // Back-facing light remains 88% of authored albedo; the lit side
                // reaches 104%. World light intensity is intentionally ignored.
                half ndl01 = dot(normal, lightDir) * 0.5h + 0.5h;
                half directional = lerp(0.88h, 1.04h, ndl01);
                half diffuseFactor = lerp(1.0h, directional, _LightingStrength);
                #if defined(_NORMALMAP)
                    half3 probeTint = GetProbeTint(normal);
                #else
                    half3 probeTint = flipped ? i.probeTintBack : i.probeTint;
                #endif
                half3 color = albedo * diffuseFactor * probeTint;

                // Reflection Probe metal (and optional gloss on non-metals). Smoothness controls
                // cubemap mip: low = rough/blurred, high = mirror-like.
                #if defined(_METALREFLECTION)
                    half3 reflDir = reflect(-viewDir, normal);
                    half perceptualRoughness = 1.0h - smoothness;
                    half mip = perceptualRoughness * UNITY_SPECCUBE_LOD_STEPS;
                    half3 probe = OsaToonSampleReflection(reflDir, mip);
                    // MainTex is the metal tint: white gives silver/mirror, while
                    // yellow/orange textures naturally produce gold/copper-like metals.
                    half3 metalReflection = probe * albedo;
                    color = lerp(color, metalReflection, metallic);
                    // Glossy non-metals (plastic): the probe/cube added on top, white and not tinted,
                    // weak head-on and strong at grazing angles (Schlick, F0 = 0.04). Strength 1 = physical.
                    half fresnel = 0.04h + 0.96h * pow(1.0h - saturate(dot(normal, viewDir)), 5.0h);
                    color += probe * (_ReflectionStrength * (1.0h - metallic) * fresnel);
                #endif

                // Emission B is intentionally texture-free: B=0 is normal output,
                // B=1 is authored albedo at the requested HDR multiplier and is
                // completely independent of lighting. Intermediate mask values blend.
                #if defined(_EMISSION)
                    half emissionMask = saturate(mask.b);
                    half3 emissionColor = albedo * max(_EmissionMultiplier, 1.0h);
                    color = lerp(color, emissionColor, emissionMask);
                #endif

                // MatCap is applied last. Replace therefore really means replace:
                // at mask G=1 and strength=1, no lighting/specular/metal/emission
                // contribution survives. Strength scales/blends the selected effect.
                #if defined(_MATCAP)
                    half2 matcapUV = GetMatCapUV(viewDir, normal);
                    half3 matcap = tex2D(_MatCap, matcapUV).rgb;
                    half matcapWeight = saturate(mask.g * _MatCapStrength);

                    if (_MatCapBlend < 0.5h) // Add
                    {
                        color += matcap * (mask.g * _MatCapStrength);
                    }
                    else if (_MatCapBlend < 1.5h) // Multiply
                    {
                        half3 multiplied = color * matcap;
                        color = lerp(color, multiplied, matcapWeight);
                    }
                    else // Replace
                    {
                        color = lerp(color, matcap, matcapWeight);
                    }
                #endif

                half4 result = half4(color, 1.0h);
                UNITY_APPLY_FOG(i.fogCoord, result);
                return result;
            }
            ENDCG
        }
    }

    // No fallback: supported targets should compile OsaToon itself rather than
    // silently changing appearance through a legacy fallback shader.
    Fallback Off
}
