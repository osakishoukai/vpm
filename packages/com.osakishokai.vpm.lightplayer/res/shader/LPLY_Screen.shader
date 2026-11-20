

Shader "Osaki/VPM/LightPlayer/Screen"
{
    Properties
    {
        _MainTex ("Unity の再生機の絵", 2D) = "black" {}
        _AvproTex ("AVPro の絵", 2D) = "black" {}
        _IdleTex ("定常の静止画", 2D) = "black" {}
        _IdleColor ("静止画が無い時の色", Color) = (0.02, 0.02, 0.03, 1)
        _Brightness ("動画の明るさ", Range(0, 2)) = 1
        _IdleBrightness ("静止画の明るさ", Range(0, 2)) = 1
        [Toggle] _AvproFlip ("AVPro の絵を上下反転", Float) = 1
        [Toggle] _AvproGamma ("AVPro の絵をリニアへ直す", Float) = 1
        _Mode ("絵の種類", Float) = 0
        _UiShade ("操作の列の陰(0 = 出ていない / 1 = 出ている)", Float) = 0
        _ShadeHeight ("陰の高さ(画面の高さに対する割合)", Range(0.05, 1)) = 0.34
        _ShadeDark ("陰の下端の暗さ", Range(0, 1)) = 0.82
        _ShadeCurve ("陰の曲がり(1 = 直線)", Range(0.3, 3)) = 0.9
        [Toggle] _Glass ("ブラウン管のガラス(左上の映り込み・縁の暗さ・斜めの照り返し)", Float) = 0
        _GlassSheen ("映り込みの強さ", Range(0, 0.5)) = 0.14
        _GlassVignette ("縁の暗さ", Range(0, 1)) = 0.35
        _GlassFresnel ("斜めの照り返し", Range(0, 1)) = 0.25
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" }
        Pass
        {
            Offset -1, -1
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"
            sampler2D _MainTex; float4 _MainTex_ST, _MainTex_TexelSize;
            sampler2D _AvproTex; float4 _AvproTex_TexelSize;
            sampler2D _IdleTex; float4 _IdleTex_TexelSize;
            half4 _IdleColor;
            half _Brightness, _IdleBrightness, _AvproFlip, _AvproGamma, _Mode;
            half _UiShade, _ShadeHeight, _ShadeDark, _ShadeCurve;

            half3 SampleEdge(sampler2D t, float2 uv, float4 ts)
            {
                float2 dx = ddx(uv), dy = ddy(uv);
                float l = max(length(dx * ts.zw), length(dy * ts.zw));
                float2 m = min(ts.xy * max(0.5, l), 0.5);
                return tex2Dgrad(t, clamp(uv, m, 1 - m), dx, dy).rgb;
            }
            half3 Shade(half3 c, float v)
            {
                half d = _UiShade * _ShadeDark * pow(saturate(1 - v / _ShadeHeight), _ShadeCurve);

                return c * pow(1 - d, 2.2);
            }
            half _Glass, _GlassSheen, _GlassVignette, _GlassFresnel;

            half3 Glass(half3 c, float2 uv, float3 wn, float3 wpos)
            {
                if (_Glass < 0.5) return c;
                float2 e = abs(uv - 0.5) * 2;
                half vig = 1 - _GlassVignette * saturate((pow(e.x, 6) + pow(e.y, 6)) * 0.6 + pow(e.x * e.y, 2) * 0.8);
                float2 d = (uv - float2(0.24, 0.80)) * float2(1.0, 1.7);
                half hl = exp(-dot(d, d) * 14) * _GlassSheen;
                float3 v = normalize(_WorldSpaceCameraPos - wpos);
                half fr = pow(1 - saturate(abs(dot(normalize(wn), v))), 4) * _GlassFresnel;
                return c * vig + (hl + fr) * 0.8;
            }
            struct appdata { float4 vertex : POSITION; float3 normal : NORMAL; float2 uv : TEXCOORD0; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; float3 wn : TEXCOORD1; float3 wpos : TEXCOORD2; UNITY_VERTEX_OUTPUT_STEREO };
            v2f vert (appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                o.wn = UnityObjectToWorldNormal(v.normal);
                o.wpos = mul(unity_ObjectToWorld, v.vertex).xyz;
                return o;
            }
            half4 frag (v2f i) : SV_Target
            {
                half3 c;
                if (_Mode > 1.5)
                {
                    float2 uv = _AvproFlip > 0.5 ? float2(i.uv.x, 1 - i.uv.y) : i.uv;
                    half3 a = SampleEdge(_AvproTex, uv, _AvproTex_TexelSize);
                    if (_AvproGamma > 0.5) a = GammaToLinearSpace(a);
                    c = a * _Brightness;
                }
                else if (_Mode > 0.5) c = SampleEdge(_MainTex, TRANSFORM_TEX(i.uv, _MainTex), _MainTex_TexelSize) * _Brightness;
                else c = _IdleTex_TexelSize.z > 4 ? SampleEdge(_IdleTex, i.uv, _IdleTex_TexelSize) * _IdleBrightness : _IdleColor.rgb;
                return half4(Glass(Shade(c, i.uv.y), i.uv, i.wn, i.wpos), 1);
            }
            ENDCG
        }
    }
    FallBack Off
}
