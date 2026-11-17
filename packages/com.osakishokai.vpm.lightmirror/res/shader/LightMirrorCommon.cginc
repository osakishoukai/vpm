
#ifndef LIGHTMIRROR_COMMON
#define LIGHTMIRROR_COMMON
#include "UnityCG.cginc"

sampler2D _Mask;

inline void LightMirrorClip(float2 uv)
{
    half4 m = tex2D(_Mask, uv);
    clip(min(m.a, max(m.r, max(m.g, m.b))) - 0.5);
}
#endif
