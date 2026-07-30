using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

public partial class InfinitMeshTerrain
{
    private const int MaxTerrainBiomeCount = 32;

    [Header("Biomes")]
    [SerializeField] private bool enableBiomes = true;
    [SerializeField] private TerrainBiomeSO[] biomes = Array.Empty<TerrainBiomeSO>();
    [SerializeField, Min(0f)] private float biomeBlendDistance = 128f;
    [SerializeField] private bool useBiomeNoise = true;
    [SerializeField] private int biomeSeed = 7319;
    [SerializeField, Min(0f)] private float biomeNoiseAmplitude = 256f;
    [SerializeField, Min(0.000001f)] private float biomeNoiseFrequency = 0.0012f;
    [SerializeField, Range(1, 8)] private int biomeNoiseOctaves = 3;
    [SerializeField, Min(1f)] private float biomeNoiseLacunarity = 2f;
    [SerializeField, Range(0f, 1f)] private float biomeNoisePersistence = 0.5f;
    [SerializeField] private Vector2 biomeNoiseOffset;

    private readonly List<TerrainBiomeSO> subscribedBiomes = new List<TerrainBiomeSO>();

    private void ValidateBiomeSettings()
    {
        biomeBlendDistance = Mathf.Max(0f, biomeBlendDistance);
        biomeNoiseAmplitude = Mathf.Max(0f, biomeNoiseAmplitude);
        biomeNoiseFrequency = Mathf.Max(0.000001f, biomeNoiseFrequency);
        biomeNoiseOctaves = Mathf.Clamp(biomeNoiseOctaves, 1, 8);
        biomeNoiseLacunarity = Mathf.Max(1f, biomeNoiseLacunarity <= 0f ? 2f : biomeNoiseLacunarity);
        biomeNoisePersistence = Mathf.Clamp01(biomeNoisePersistence <= 0f ? 0.5f : biomeNoisePersistence);

        if (biomes == null)
        {
            return;
        }

        for (int i = 0; i < biomes.Length; i++)
        {
            if (biomes[i] != null)
            {
                biomes[i].ValidateValues();
            }
        }
    }

    private void SyncBiomeSubscriptions()
    {
        UnsubscribeBiomes();

        if (biomes == null)
        {
            return;
        }

        for (int i = 0; i < biomes.Length; i++)
        {
            TerrainBiomeSO biome = biomes[i];
            if (biome == null || subscribedBiomes.Contains(biome))
            {
                continue;
            }

            biome.Changed += OnBiomeChanged;
            subscribedBiomes.Add(biome);
        }
    }

    private void UnsubscribeBiomes()
    {
        for (int i = 0; i < subscribedBiomes.Count; i++)
        {
            TerrainBiomeSO biome = subscribedBiomes[i];
            if (biome != null)
            {
                biome.Changed -= OnBiomeChanged;
            }
        }

        subscribedBiomes.Clear();
    }

    private void OnBiomeChanged()
    {
        ValidateBiomeSettings();
        ClearGrassRuntimeCells();
        ClearGrassFromRuntimeChunks();
        RequestVisibleChunkRebuilds();
    }

    private int GetTerrainBiomeCount()
    {
        if (!enableBiomes || biomes == null)
        {
            return 0;
        }

        int count = 0;
        for (int i = 0; i < biomes.Length && count < MaxTerrainBiomeCount; i++)
        {
            if (biomes[i] != null)
            {
                count++;
            }
        }

        return count;
    }

    private BiomeSamplingSettings CreateBiomeSamplingSettings()
    {
        int count = GetTerrainBiomeCount();
        return new BiomeSamplingSettings
        {
            Count = count,
            Seed = GetTerrainSeed() * 31 + biomeSeed,
            UseNoise = useBiomeNoise && count > 0 ? 1 : 0,
            BlendDistance = biomeBlendDistance,
            NoiseAmplitude = biomeNoiseAmplitude,
            NoiseFrequency = biomeNoiseFrequency,
            NoiseOctaves = biomeNoiseOctaves,
            NoiseLacunarity = biomeNoiseLacunarity,
            NoisePersistence = biomeNoisePersistence,
            NoiseOffset = new float2(biomeNoiseOffset.x, biomeNoiseOffset.y)
        };
    }

    private GrassBiomeData[] CreateBiomeDataArray()
    {
        int count = GetTerrainBiomeCount();
        if (count == 0)
        {
            return Array.Empty<GrassBiomeData>();
        }

        GrassBiomeData[] biomeData = new GrassBiomeData[count];
        int writeIndex = 0;
        for (int i = 0; i < biomes.Length && writeIndex < biomeData.Length; i++)
        {
            TerrainBiomeSO biome = biomes[i];
            if (biome == null)
            {
                continue;
            }

            biomeData[writeIndex] = CreateBiomeData(biome);
            writeIndex++;
        }

        Array.Sort(biomeData, CompareBiomeData);
        return biomeData;
    }

    private TerrainBiomeLayerColorData[] CreateTerrainBiomeLayerColorDataArray()
    {
        int count = GetTerrainBiomeCount();
        if (count == 0)
        {
            return Array.Empty<TerrainBiomeLayerColorData>();
        }

        TerrainBiomeLayerColorData[] biomeData = new TerrainBiomeLayerColorData[count];
        int writeIndex = 0;
        for (int i = 0; i < biomes.Length && writeIndex < biomeData.Length; i++)
        {
            TerrainBiomeSO biome = biomes[i];
            if (biome == null)
            {
                continue;
            }

            biomeData[writeIndex] = CreateTerrainBiomeLayerColorData(biome);
            writeIndex++;
        }

        Array.Sort(biomeData, CompareTerrainBiomeLayerColorData);
        return biomeData;
    }

    private void CopyBiomeData(NativeArray<GrassBiomeData> destination)
    {
        if (!destination.IsCreated || destination.Length == 0)
        {
            return;
        }

        GrassBiomeData[] biomeData = CreateBiomeDataArray();
        int count = Mathf.Min(destination.Length, biomeData.Length);
        for (int i = 0; i < count; i++)
        {
            destination[i] = biomeData[i];
        }
    }

    private static int CompareBiomeData(GrassBiomeData a, GrassBiomeData b)
    {
        int minComparison = a.DistanceRange.x.CompareTo(b.DistanceRange.x);
        if (minComparison != 0)
        {
            return minComparison;
        }

        return a.DistanceRange.y.CompareTo(b.DistanceRange.y);
    }

    private static int CompareTerrainBiomeLayerColorData(TerrainBiomeLayerColorData a, TerrainBiomeLayerColorData b)
    {
        int minComparison = a.DistanceRange.x.CompareTo(b.DistanceRange.x);
        if (minComparison != 0)
        {
            return minComparison;
        }

        return a.DistanceRange.y.CompareTo(b.DistanceRange.y);
    }

    private static GrassBiomeData CreateBiomeData(TerrainBiomeSO biome)
    {
        biome.ValidateValues();
        Color grassColor = biome.GrassColor;
        return new GrassBiomeData
        {
            DistanceRange = new float4(
                biome.MinDistanceFromCenter,
                biome.MaxDistanceFromCenter,
                0f,
                0f),
            GrassColor = new float4(
                Mathf.Max(0f, grassColor.r),
                Mathf.Max(0f, grassColor.g),
                Mathf.Max(0f, grassColor.b),
                Mathf.Clamp01(grassColor.a))
        };
    }

    private static TerrainBiomeLayerColorData CreateTerrainBiomeLayerColorData(TerrainBiomeSO biome)
    {
        biome.ValidateValues();
        TerrainBiomeLayerColorData data = new TerrainBiomeLayerColorData
        {
            DistanceRange = new float4(
                biome.MinDistanceFromCenter,
                biome.MaxDistanceFromCenter,
                0f,
                0f),
            HasLayerColor = new bool[MaxTerrainLayerCount],
            LayerColors = new Color[MaxTerrainLayerCount]
        };

        for (int i = 0; i < data.LayerColors.Length; i++)
        {
            data.LayerColors[i] = Color.white;
        }

        IReadOnlyList<TerrainBiomeLayerColor> layerColors = biome.TerrainLayerColors;
        for (int i = 0; i < layerColors.Count; i++)
        {
            TerrainBiomeLayerColor layerColor = layerColors[i];
            if (!layerColor.Enabled)
            {
                continue;
            }

            int channelIndex = Mathf.Clamp(layerColor.ChannelIndex, 0, MaxTerrainLayerCount - 1);
            data.HasLayerColor[channelIndex] = true;
            data.LayerColors[channelIndex] = layerColor.Color;
        }

        return data;
    }

    private static bool HasTerrainBiomeLayerColorOverride(
        TerrainBiomeLayerColorData[] biomes,
        BiomeSamplingSettings settings,
        int channelIndex)
    {
        int count = biomes != null ? math.min(math.max(0, settings.Count), biomes.Length) : 0;
        for (int i = 0; i < count; i++)
        {
            TerrainBiomeLayerColorData biome = biomes[i];
            if (biome?.HasLayerColor != null
                && channelIndex >= 0
                && channelIndex < biome.HasLayerColor.Length
                && biome.HasLayerColor[channelIndex])
            {
                return true;
            }
        }

        return false;
    }

    private static float3 EvaluateBiomeGrassColor(
        float2 worldXZ,
        GrassBiomeData[] biomes,
        BiomeSamplingSettings settings)
    {
        int count = biomes != null ? math.min(math.max(0, settings.Count), biomes.Length) : 0;
        if (count <= 0)
        {
            return new float3(1f, 1f, 1f);
        }

        float biomeDistance = EvaluateBiomeDistance(worldXZ, settings);
        int biomeIndex = ResolveBiomeIndex(biomeDistance, biomes, count);
        if (biomeIndex < 0)
        {
            return new float3(1f, 1f, 1f);
        }

        GrassBiomeData biome = biomes[biomeIndex];
        float3 grassColor = math.max(float3.zero, biome.GrassColor.xyz);
        float blendDistance = math.max(0f, settings.BlendDistance);
        if (blendDistance <= 0.0001f)
        {
            return grassColor;
        }

        float minDistance = math.max(0f, biome.DistanceRange.x);
        int previousIndex = FindPreviousBiomeIndex(biomeIndex, biomes, count);
        if (previousIndex >= 0 && minDistance > 0f && biomeDistance <= minDistance + blendDistance)
        {
            float t = SmoothStep01(minDistance - blendDistance, minDistance + blendDistance, biomeDistance);
            return math.lerp(math.max(float3.zero, biomes[previousIndex].GrassColor.xyz), grassColor, t);
        }

        int nextIndex = FindNextBiomeIndex(biomeIndex, biomes, count);
        if (nextIndex >= 0)
        {
            float nextMinDistance = math.max(0f, biomes[nextIndex].DistanceRange.x);
            if (biomeDistance >= nextMinDistance - blendDistance)
            {
                float t = SmoothStep01(nextMinDistance - blendDistance, nextMinDistance + blendDistance, biomeDistance);
                grassColor = math.lerp(grassColor, math.max(float3.zero, biomes[nextIndex].GrassColor.xyz), t);
            }
        }

        return grassColor;
    }

    private static float3 EvaluateBiomeLayerColor(
        float2 worldXZ,
        TerrainBiomeLayerColorData[] biomes,
        BiomeSamplingSettings settings,
        int channelIndex,
        Color fallbackColor)
    {
        int count = biomes != null ? math.min(math.max(0, settings.Count), biomes.Length) : 0;
        if (count <= 0)
        {
            return ToFloat3(fallbackColor);
        }

        float biomeDistance = EvaluateBiomeDistance(worldXZ, settings);
        int biomeIndex = ResolveTerrainBiomeLayerColorIndex(biomeDistance, biomes, count);
        if (biomeIndex < 0)
        {
            return ToFloat3(fallbackColor);
        }

        float3 layerColor = ResolveTerrainBiomeLayerColor(biomes[biomeIndex], channelIndex, fallbackColor);
        float blendDistance = math.max(0f, settings.BlendDistance);
        if (blendDistance <= 0.0001f)
        {
            return layerColor;
        }

        float minDistance = math.max(0f, biomes[biomeIndex].DistanceRange.x);
        int previousIndex = FindPreviousTerrainBiomeLayerColorIndex(biomeIndex, biomes, count);
        if (previousIndex >= 0 && minDistance > 0f && biomeDistance <= minDistance + blendDistance)
        {
            float t = SmoothStep01(minDistance - blendDistance, minDistance + blendDistance, biomeDistance);
            return math.lerp(
                ResolveTerrainBiomeLayerColor(biomes[previousIndex], channelIndex, fallbackColor),
                layerColor,
                t);
        }

        int nextIndex = FindNextTerrainBiomeLayerColorIndex(biomeIndex, biomes, count);
        if (nextIndex >= 0)
        {
            float nextMinDistance = math.max(0f, biomes[nextIndex].DistanceRange.x);
            if (biomeDistance >= nextMinDistance - blendDistance)
            {
                float t = SmoothStep01(nextMinDistance - blendDistance, nextMinDistance + blendDistance, biomeDistance);
                layerColor = math.lerp(
                    layerColor,
                    ResolveTerrainBiomeLayerColor(biomes[nextIndex], channelIndex, fallbackColor),
                    t);
            }
        }

        return layerColor;
    }

    private static float3 EvaluateBiomeGrassColor(
        float2 worldXZ,
        NativeArray<GrassBiomeData> biomes,
        BiomeSamplingSettings settings)
    {
        int count = math.min(math.max(0, settings.Count), biomes.Length);
        if (count <= 0)
        {
            return new float3(1f, 1f, 1f);
        }

        float biomeDistance = EvaluateBiomeDistance(worldXZ, settings);
        int biomeIndex = ResolveBiomeIndex(biomeDistance, biomes, count);
        if (biomeIndex < 0)
        {
            return new float3(1f, 1f, 1f);
        }

        GrassBiomeData biome = biomes[biomeIndex];
        float3 grassColor = math.max(float3.zero, biome.GrassColor.xyz);
        float blendDistance = math.max(0f, settings.BlendDistance);
        if (blendDistance <= 0.0001f)
        {
            return grassColor;
        }

        float minDistance = math.max(0f, biome.DistanceRange.x);
        int previousIndex = FindPreviousBiomeIndex(biomeIndex, biomes, count);
        if (previousIndex >= 0 && minDistance > 0f && biomeDistance <= minDistance + blendDistance)
        {
            float t = SmoothStep01(minDistance - blendDistance, minDistance + blendDistance, biomeDistance);
            return math.lerp(math.max(float3.zero, biomes[previousIndex].GrassColor.xyz), grassColor, t);
        }

        int nextIndex = FindNextBiomeIndex(biomeIndex, biomes, count);
        if (nextIndex >= 0)
        {
            float nextMinDistance = math.max(0f, biomes[nextIndex].DistanceRange.x);
            if (biomeDistance >= nextMinDistance - blendDistance)
            {
                float t = SmoothStep01(nextMinDistance - blendDistance, nextMinDistance + blendDistance, biomeDistance);
                grassColor = math.lerp(grassColor, math.max(float3.zero, biomes[nextIndex].GrassColor.xyz), t);
            }
        }

        return grassColor;
    }

    private static float EvaluateBiomeDistance(float2 worldXZ, BiomeSamplingSettings settings)
    {
        float distanceFromCenter = math.length(worldXZ);
        if (settings.UseNoise == 0
            || settings.NoiseAmplitude <= 0f
            || settings.NoiseFrequency <= 0f)
        {
            return distanceFromCenter;
        }

        return math.max(0f, distanceFromCenter + SampleBiomeNoise(worldXZ, settings) * settings.NoiseAmplitude);
    }

    private static float SampleBiomeNoise(float2 worldXZ, BiomeSamplingSettings settings)
    {
        float frequency = math.max(0.000001f, settings.NoiseFrequency);
        float amplitude = 1f;
        float value = 0f;
        float amplitudeSum = 0f;
        int octaveCount = math.clamp(settings.NoiseOctaves, 1, 8);
        float2 seedOffset = settings.NoiseOffset + new float2(settings.Seed * 29.37f, settings.Seed * -17.91f);

        for (int octave = 0; octave < octaveCount; octave++)
        {
            value += noise.snoise((worldXZ + seedOffset) * frequency) * amplitude;
            amplitudeSum += amplitude;
            amplitude *= math.saturate(settings.NoisePersistence);
            frequency *= math.max(1f, settings.NoiseLacunarity);
        }

        return amplitudeSum > 0.0001f ? value / amplitudeSum : 0f;
    }

    private static int ResolveBiomeIndex(float biomeDistance, GrassBiomeData[] biomes, int count)
    {
        int biomeIndex = -1;
        float bestMinDistance = float.MinValue;
        float nearestDistance = float.MaxValue;
        int nearestIndex = -1;

        for (int i = 0; i < count; i++)
        {
            GrassBiomeData biome = biomes[i];
            float minDistance = math.max(0f, biome.DistanceRange.x);
            float maxDistance = math.max(minDistance, biome.DistanceRange.y);
            if (biomeDistance >= minDistance
                && biomeDistance <= maxDistance
                && (minDistance > bestMinDistance || (math.abs(minDistance - bestMinDistance) <= 0.0001f && i > biomeIndex)))
            {
                biomeIndex = i;
                bestMinDistance = minDistance;
            }

            float distanceToRange = DistanceToRange(biomeDistance, minDistance, maxDistance);
            if (distanceToRange < nearestDistance)
            {
                nearestDistance = distanceToRange;
                nearestIndex = i;
            }
        }

        return biomeIndex >= 0 ? biomeIndex : nearestIndex;
    }

    private static int ResolveTerrainBiomeLayerColorIndex(
        float biomeDistance,
        TerrainBiomeLayerColorData[] biomes,
        int count)
    {
        int biomeIndex = -1;
        float bestMinDistance = float.MinValue;
        float nearestDistance = float.MaxValue;
        int nearestIndex = -1;

        for (int i = 0; i < count; i++)
        {
            TerrainBiomeLayerColorData biome = biomes[i];
            float minDistance = math.max(0f, biome.DistanceRange.x);
            float maxDistance = math.max(minDistance, biome.DistanceRange.y);
            if (biomeDistance >= minDistance
                && biomeDistance <= maxDistance
                && (minDistance > bestMinDistance || (math.abs(minDistance - bestMinDistance) <= 0.0001f && i > biomeIndex)))
            {
                biomeIndex = i;
                bestMinDistance = minDistance;
            }

            float distanceToRange = DistanceToRange(biomeDistance, minDistance, maxDistance);
            if (distanceToRange < nearestDistance)
            {
                nearestDistance = distanceToRange;
                nearestIndex = i;
            }
        }

        return biomeIndex >= 0 ? biomeIndex : nearestIndex;
    }

    private static int ResolveBiomeIndex(float biomeDistance, NativeArray<GrassBiomeData> biomes, int count)
    {
        int biomeIndex = -1;
        float bestMinDistance = float.MinValue;
        float nearestDistance = float.MaxValue;
        int nearestIndex = -1;

        for (int i = 0; i < count; i++)
        {
            GrassBiomeData biome = biomes[i];
            float minDistance = math.max(0f, biome.DistanceRange.x);
            float maxDistance = math.max(minDistance, biome.DistanceRange.y);
            if (biomeDistance >= minDistance
                && biomeDistance <= maxDistance
                && (minDistance > bestMinDistance || (math.abs(minDistance - bestMinDistance) <= 0.0001f && i > biomeIndex)))
            {
                biomeIndex = i;
                bestMinDistance = minDistance;
            }

            float distanceToRange = DistanceToRange(biomeDistance, minDistance, maxDistance);
            if (distanceToRange < nearestDistance)
            {
                nearestDistance = distanceToRange;
                nearestIndex = i;
            }
        }

        return biomeIndex >= 0 ? biomeIndex : nearestIndex;
    }

    private static int FindPreviousBiomeIndex(int biomeIndex, GrassBiomeData[] biomes, int count)
    {
        int previousIndex = -1;
        float currentMinDistance = math.max(0f, biomes[biomeIndex].DistanceRange.x);
        float bestMinDistance = float.MinValue;

        for (int i = 0; i < count; i++)
        {
            float minDistance = math.max(0f, biomes[i].DistanceRange.x);
            if (minDistance < currentMinDistance && minDistance > bestMinDistance)
            {
                bestMinDistance = minDistance;
                previousIndex = i;
            }
        }

        return previousIndex;
    }

    private static int FindPreviousTerrainBiomeLayerColorIndex(
        int biomeIndex,
        TerrainBiomeLayerColorData[] biomes,
        int count)
    {
        int previousIndex = -1;
        float currentMinDistance = math.max(0f, biomes[biomeIndex].DistanceRange.x);
        float bestMinDistance = float.MinValue;

        for (int i = 0; i < count; i++)
        {
            float minDistance = math.max(0f, biomes[i].DistanceRange.x);
            if (minDistance < currentMinDistance && minDistance > bestMinDistance)
            {
                bestMinDistance = minDistance;
                previousIndex = i;
            }
        }

        return previousIndex;
    }

    private static int FindPreviousBiomeIndex(int biomeIndex, NativeArray<GrassBiomeData> biomes, int count)
    {
        int previousIndex = -1;
        float currentMinDistance = math.max(0f, biomes[biomeIndex].DistanceRange.x);
        float bestMinDistance = float.MinValue;

        for (int i = 0; i < count; i++)
        {
            float minDistance = math.max(0f, biomes[i].DistanceRange.x);
            if (minDistance < currentMinDistance && minDistance > bestMinDistance)
            {
                bestMinDistance = minDistance;
                previousIndex = i;
            }
        }

        return previousIndex;
    }

    private static int FindNextBiomeIndex(int biomeIndex, GrassBiomeData[] biomes, int count)
    {
        int nextIndex = -1;
        float currentMinDistance = math.max(0f, biomes[biomeIndex].DistanceRange.x);
        float bestMinDistance = float.MaxValue;

        for (int i = 0; i < count; i++)
        {
            float minDistance = math.max(0f, biomes[i].DistanceRange.x);
            if (minDistance > currentMinDistance && minDistance < bestMinDistance)
            {
                bestMinDistance = minDistance;
                nextIndex = i;
            }
        }

        return nextIndex;
    }

    private static int FindNextTerrainBiomeLayerColorIndex(
        int biomeIndex,
        TerrainBiomeLayerColorData[] biomes,
        int count)
    {
        int nextIndex = -1;
        float currentMinDistance = math.max(0f, biomes[biomeIndex].DistanceRange.x);
        float bestMinDistance = float.MaxValue;

        for (int i = 0; i < count; i++)
        {
            float minDistance = math.max(0f, biomes[i].DistanceRange.x);
            if (minDistance > currentMinDistance && minDistance < bestMinDistance)
            {
                bestMinDistance = minDistance;
                nextIndex = i;
            }
        }

        return nextIndex;
    }

    private static int FindNextBiomeIndex(int biomeIndex, NativeArray<GrassBiomeData> biomes, int count)
    {
        int nextIndex = -1;
        float currentMinDistance = math.max(0f, biomes[biomeIndex].DistanceRange.x);
        float bestMinDistance = float.MaxValue;

        for (int i = 0; i < count; i++)
        {
            float minDistance = math.max(0f, biomes[i].DistanceRange.x);
            if (minDistance > currentMinDistance && minDistance < bestMinDistance)
            {
                bestMinDistance = minDistance;
                nextIndex = i;
            }
        }

        return nextIndex;
    }

    private static float DistanceToRange(float value, float minDistance, float maxDistance)
    {
        if (value < minDistance)
        {
            return minDistance - value;
        }

        return value > maxDistance ? value - maxDistance : 0f;
    }

    private static float SmoothStep01(float edge0, float edge1, float value)
    {
        float t = math.saturate((value - edge0) / math.max(edge1 - edge0, 0.0001f));
        return t * t * (3f - 2f * t);
    }

    private static float3 ResolveTerrainBiomeLayerColor(
        TerrainBiomeLayerColorData biome,
        int channelIndex,
        Color fallbackColor)
    {
        if (biome?.HasLayerColor == null
            || biome.LayerColors == null
            || channelIndex < 0
            || channelIndex >= biome.HasLayerColor.Length
            || channelIndex >= biome.LayerColors.Length
            || !biome.HasLayerColor[channelIndex])
        {
            return ToFloat3(fallbackColor);
        }

        return ToFloat3(biome.LayerColors[channelIndex]);
    }

    private static float3 ToFloat3(Color color)
    {
        return new float3(
            Mathf.Max(0f, color.r),
            Mathf.Max(0f, color.g),
            Mathf.Max(0f, color.b));
    }
}
