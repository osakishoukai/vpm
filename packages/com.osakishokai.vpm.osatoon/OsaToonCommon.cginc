// Shared helpers for the OsaToon shaders. Include after UnityCG.cginc and Lighting.cginc.
//
// Reflection source
//   Default: the scene Reflection Probe (unity_SpecCube0).
//   GPU-instanced draws (Graphics/VRCGraphics.DrawMeshInstanced) cannot bind a Reflection
//   Probe per draw, so unity_SpecCube0 is not the probe the object sits in. A material can
//   therefore carry its own copy of the probe: turn on _REFLECTIONCUBE and assign the probe's
//   baked cubemap to _ReflectionCube (and the probe's Intensity). It is sampled exactly like
//   unity_SpecCube0 (same reflection vector, same LOD, no box projection), so the look is the
//   probe's look. These are ordinary material properties, so they work with instancing.
#ifndef OSATOON_COMMON_INCLUDED
#define OSATOON_COMMON_INCLUDED

#if defined(_REFLECTIONCUBE)
    UNITY_DECLARE_TEXCUBE(_ReflectionCube);
    half4 _ReflectionCube_HDR;      // filled by Unity from the texture (HDR decode instructions)
    half _ReflectionCubeIntensity;  // the probe's Intensity
#endif

// Direction of the main light for the diffuse/specular cue. Worlds lit only by
// baked lighting have no realtime directional light, so the light
// vector is zero there and normalizing it yields NaN (drawn black).
// In that case fall back to the dominant direction of the Light Probe
// SH, and to world up when the probes carry no direction either.
half3 OsaToonGetLightDir(float3 worldPos)
{
    float3 mainLight = _WorldSpaceLightPos0.xyz - worldPos * _WorldSpaceLightPos0.w;
    float mainLenSq = dot(mainLight, mainLight);
    float3 probeDir = unity_SHAr.xyz * 0.2126 + unity_SHAg.xyz * 0.7152 + unity_SHAb.xyz * 0.0722;
    float probeLenSq = dot(probeDir, probeDir);
    float3 fallbackDir = probeLenSq > 1e-6 ? probeDir * rsqrt(probeLenSq) : float3(0.0, 1.0, 0.0);
    float3 dir = mainLenSq > 1e-6 ? mainLight * rsqrt(max(mainLenSq, 1e-6)) : fallbackDir;
    return half3(dir);
}

// Reflected radiance along reflDir. mip is the cubemap LOD (roughness).
half3 OsaToonSampleReflection(half3 reflDir, half mip)
{
    #if defined(_REFLECTIONCUBE)
        half4 enc = UNITY_SAMPLE_TEXCUBE_LOD(_ReflectionCube, reflDir, mip);
        return DecodeHDR(enc, _ReflectionCube_HDR) * _ReflectionCubeIntensity;
    #else
        half4 enc = UNITY_SAMPLE_TEXCUBE_LOD(unity_SpecCube0, reflDir, mip);
        return DecodeHDR(enc, unity_SpecCube0_HDR);
    #endif
}

#endif
