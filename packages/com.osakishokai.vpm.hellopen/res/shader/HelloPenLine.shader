

Shader "Osaki/VPM/HelloPen/Line"
{
    Properties
    {
        _HalfWidth ("Half Width (m。HelloPenBoard が lineWidth から入れる)", Float) = 0.002
    }

    SubShader
    {
        Tags { "RenderType"="TransparentCutout" "Queue"="AlphaTest" }
        Cull Off
        AlphaToMask On
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma geometry geom
            #pragma fragment frag
            #pragma target 4.0
            #pragma only_renderers d3d11
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"
            float _HalfWidth;
            struct appdata { float4 vertex : POSITION; float3 normal : NORMAL; fixed4 color : COLOR; float2 uv : TEXCOORD0; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct v2g { float3 wp : TEXCOORD0; float3 n : TEXCOORD1; fixed4 color : COLOR; float2 uv : TEXCOORD2; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct g2f { float4 pos : SV_POSITION; fixed4 color : COLOR; float3 uvk : TEXCOORD0; UNITY_VERTEX_OUTPUT_STEREO };

            v2g vert (appdata v)
            {
                v2g o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_TRANSFER_INSTANCE_ID(v, o);

                o.wp = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.n = UnityObjectToWorldNormal(v.normal);
                o.color = v.color;
                o.uv = v.uv;
                return o;
            }

            float3 Lift(float3 p, float3 n, float3 view)
            {
                float c = dot(view, n);
                return p + (c >= 0 ? n : -n) * (_HalfWidth * sqrt(saturate(1 - c * c)));
            }

            void Emit(inout TriangleStream<g2f> st, g2f o, float3 p, float3 uvk)
            {
                o.pos = UnityWorldToClipPos(p);
                o.uvk = uvk;
                st.Append(o);
            }

            void Cap(inout TriangleStream<g2f> st, g2f o, float3 p, float3 e1, float3 e2)
            {
                const float R = 1.2;
                Emit(st, o, p + (e2 * 2) * R * _HalfWidth, float3(0, 2 * R, 1));
                Emit(st, o, p + (e1 * -1.7320508 - e2) * R * _HalfWidth, float3(-1.7320508 * R, -R, 1));
                Emit(st, o, p + (e1 * 1.7320508 - e2) * R * _HalfWidth, float3(1.7320508 * R, -R, 1));
                st.RestartStrip();
            }

            [maxvertexcount(10)]
            void geom (triangle v2g IN[3], inout TriangleStream<g2f> st)
            {

                float x0 = IN[0].uv.x, x1 = IN[1].uv.x, x2 = IN[2].uv.x;
                float lo = min(x0, min(x1, x2)), hi = max(x0, max(x1, x2));
                if (hi - lo < 1e-7) return;
                float e = (hi - lo) * 1e-3;
                int nlo = (x0 - lo < e) + (x1 - lo < e) + (x2 - lo < e);
                if (nlo != 2) return;
                int ia = x0 - lo < e ? 0 : (x1 - lo < e ? 1 : 2);
                int ib = hi - x0 < e ? 0 : (hi - x1 < e ? 1 : 2);

                UNITY_SETUP_INSTANCE_ID(IN[0]);
                g2f o;
                UNITY_INITIALIZE_OUTPUT(g2f, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.color = IN[ia].color;

                float3 a = IN[ia].wp, b = IN[ib].wp;
                float3 n = normalize(IN[ia].n);
                float3 ab = b - a;
                float len = length(ab);
                float3 t = len > 1e-7 ? ab / len : float3(1, 0, 0);

                float3 va = normalize(_WorldSpaceCameraPos - a);
                float3 vb = normalize(_WorldSpaceCameraPos - b);
                a = Lift(a, n, va);
                b = Lift(b, n, vb);

                float3 da = cross(t, va), db = cross(t, vb);
                float la = length(da), lb = length(db);
                float3 w = normalize(cross(t, n));
                da = la > 1e-3 ? da / la : w;
                db = lb > 1e-3 ? db / lb : w;
                db = dot(da, db) < 0 ? -db : db;

                Emit(st, o, a + da * _HalfWidth, float3(0, 1, 0));
                Emit(st, o, a - da * _HalfWidth, float3(0, -1, 0));
                Emit(st, o, b + db * _HalfWidth, float3(1, 1, 0));
                Emit(st, o, b - db * _HalfWidth, float3(1, -1, 0));
                st.RestartStrip();

                Cap(st, o, a, da, cross(va, da));
                Cap(st, o, b, db, cross(vb, db));
            }

            fixed4 frag (g2f i) : SV_Target
            {

                float d = i.uvk.z > 0.5 ? length(i.uvk.xy) : abs(i.uvk.y);
                float w = max(fwidth(d), 1e-4);
                float a = saturate((1 - d) / w + 0.5);
                return fixed4(i.color.rgb, a);
            }
            ENDCG
        }
    }

    SubShader
    {
        Tags { "RenderType"="TransparentCutout" "Queue"="AlphaTest" }
        Cull Off
        AlphaToMask On
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"

            float _HalfWidth;
            struct appdata { float4 vertex : POSITION; float3 normal : NORMAL; float4 tangent : TANGENT; fixed4 color : COLOR; float2 uv : TEXCOORD0; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct v2f { float4 pos : SV_POSITION; fixed4 color : COLOR; float2 uv : TEXCOORD0; UNITY_VERTEX_OUTPUT_STEREO };
            v2f vert (appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                float3 wp = mul(unity_ObjectToWorld, v.vertex).xyz;
                float3 t = normalize(UnityObjectToWorldDir(v.tangent.xyz));
                float3 n = normalize(UnityObjectToWorldNormal(v.normal));
                float3 w = cross(t, n);
                float s = 1 - 2 * v.uv.y;
                float3 view = normalize(_WorldSpaceCameraPos - wp);
                float3 d = cross(t, view);
                float l = length(d);
                d = l > 1e-3 ? d / l : w;
                d = dot(d, w) < 0 ? -d : d;

                float3 up = dot(view, n) > 0 ? n : -n;
                float rise = abs(dot(d, n));
                float3 p = wp + (d - w) * (s * _HalfWidth) + up * (rise * _HalfWidth);
                o.pos = UnityWorldToClipPos(p);
                o.color = v.color;
                o.uv = v.uv;
                return o;
            }
            fixed4 frag (v2f i) : SV_Target
            {
                float d = abs(i.uv.y * 2 - 1);
                float w = max(fwidth(i.uv.y * 2), 1e-4);
                float a = saturate((1 - d) / w + 0.5);
                return fixed4(i.color.rgb, a);
            }
            ENDCG
        }
    }
}
