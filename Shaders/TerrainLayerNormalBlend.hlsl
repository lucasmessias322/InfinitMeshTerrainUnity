#ifndef TERRAIN_LAYER_NORMAL_BLEND_INCLUDED
#define TERRAIN_LAYER_NORMAL_BLEND_INCLUDED

float3 ApplyTerrainNormalStrength_float(float3 normalTS, float strength)
{
    return float3(normalTS.xy * strength, lerp(1.0, normalTS.z, saturate(strength)));
}

void BlendTerrainLayerNormals_float(
    float4 UV,
    UnityTexture2D SplatMap,
    UnityTexture2D SplatMap2,
    TEXTURE2D(Layer1Normal),
    TEXTURE2D(Layer2Normal),
    TEXTURE2D(Layer3Normal),
    TEXTURE2D(Layer4Normal),
    TEXTURE2D(Layer5Normal),
    TEXTURE2D(Layer6Normal),
    TEXTURE2D(Layer7Normal),
    TEXTURE2D(Layer8Normal),
    float Layer1NormalScale,
    float Layer2NormalScale,
    float Layer3NormalScale,
    float Layer4NormalScale,
    float Layer5NormalScale,
    float Layer6NormalScale,
    float Layer7NormalScale,
    float Layer8NormalScale,
    UnitySamplerState NormalSampler,
    out float3 NormalTS)
{
    float2 uv = UV.xy;
    float4 weights0 = SAMPLE_TEXTURE2D(SplatMap.tex, SplatMap.samplerstate, SplatMap.GetTransformedUV(uv));
    float4 weights1 = SAMPLE_TEXTURE2D(SplatMap2.tex, SplatMap2.samplerstate, SplatMap2.GetTransformedUV(uv));

    float3 normal1 = ApplyTerrainNormalStrength_float(UnpackNormal(SAMPLE_TEXTURE2D(Layer1Normal, NormalSampler.samplerstate, uv)), Layer1NormalScale);
    float3 normal2 = ApplyTerrainNormalStrength_float(UnpackNormal(SAMPLE_TEXTURE2D(Layer2Normal, NormalSampler.samplerstate, uv)), Layer2NormalScale);
    float3 normal3 = ApplyTerrainNormalStrength_float(UnpackNormal(SAMPLE_TEXTURE2D(Layer3Normal, NormalSampler.samplerstate, uv)), Layer3NormalScale);
    float3 normal4 = ApplyTerrainNormalStrength_float(UnpackNormal(SAMPLE_TEXTURE2D(Layer4Normal, NormalSampler.samplerstate, uv)), Layer4NormalScale);
    float3 normal5 = ApplyTerrainNormalStrength_float(UnpackNormal(SAMPLE_TEXTURE2D(Layer5Normal, NormalSampler.samplerstate, uv)), Layer5NormalScale);
    float3 normal6 = ApplyTerrainNormalStrength_float(UnpackNormal(SAMPLE_TEXTURE2D(Layer6Normal, NormalSampler.samplerstate, uv)), Layer6NormalScale);
    float3 normal7 = ApplyTerrainNormalStrength_float(UnpackNormal(SAMPLE_TEXTURE2D(Layer7Normal, NormalSampler.samplerstate, uv)), Layer7NormalScale);
    float3 normal8 = ApplyTerrainNormalStrength_float(UnpackNormal(SAMPLE_TEXTURE2D(Layer8Normal, NormalSampler.samplerstate, uv)), Layer8NormalScale);

    float3 blendedNormal =
        normal1 * weights0.r +
        normal2 * weights0.g +
        normal3 * weights0.b +
        normal4 * weights0.a +
        normal5 * weights1.r +
        normal6 * weights1.g +
        normal7 * weights1.b +
        normal8 * weights1.a;

    NormalTS = dot(blendedNormal, blendedNormal) > 0.00001
        ? normalize(blendedNormal)
        : float3(0.0, 0.0, 1.0);
}

void BlendTerrainLayerNormals_half(
    half4 UV,
    UnityTexture2D SplatMap,
    UnityTexture2D SplatMap2,
    TEXTURE2D(Layer1Normal),
    TEXTURE2D(Layer2Normal),
    TEXTURE2D(Layer3Normal),
    TEXTURE2D(Layer4Normal),
    TEXTURE2D(Layer5Normal),
    TEXTURE2D(Layer6Normal),
    TEXTURE2D(Layer7Normal),
    TEXTURE2D(Layer8Normal),
    half Layer1NormalScale,
    half Layer2NormalScale,
    half Layer3NormalScale,
    half Layer4NormalScale,
    half Layer5NormalScale,
    half Layer6NormalScale,
    half Layer7NormalScale,
    half Layer8NormalScale,
    UnitySamplerState NormalSampler,
    out half3 NormalTS)
{
    float3 normalTS;
    BlendTerrainLayerNormals_float(
        (float4)UV,
        SplatMap,
        SplatMap2,
        Layer1Normal,
        Layer2Normal,
        Layer3Normal,
        Layer4Normal,
        Layer5Normal,
        Layer6Normal,
        Layer7Normal,
        Layer8Normal,
        (float)Layer1NormalScale,
        (float)Layer2NormalScale,
        (float)Layer3NormalScale,
        (float)Layer4NormalScale,
        (float)Layer5NormalScale,
        (float)Layer6NormalScale,
        (float)Layer7NormalScale,
        (float)Layer8NormalScale,
        NormalSampler,
        normalTS);

    NormalTS = (half3)normalTS;
}

#endif
