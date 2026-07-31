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
        treeRenderCacheDirty = true;
        ClearGrassRuntimeCells();
        ClearGrassFromRuntimeChunks();
        ReleaseAllInteractiveTrees(true);
        ClearTreesFromRuntimeChunks();
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
            NoiseOffset = new float2(biomeNoiseOffset.x, biomeNoiseOffset.y),
            TerrainChunkSize = Mathf.Max(1f, ChunkSize),
            BiomeSampleSpacing = CalculateBiomeLayerColorMapSampleSpacing()
        };
    }

    private static int CalculateBiomeLayerColorMapResolution(int terrainResolution)
    {
        return Mathf.Max(1, terrainResolution);
    }

    private float CalculateBiomeLayerColorMapSampleSpacing()
    {
        return Mathf.Max(0.0001f, ChunkSize) / Mathf.Max(1, GetEffectiveSegmentCount());
    }

    private void GetGrassBiomeCapacityMultipliers(
        out float densityMultiplier,
        out float bladeHeightMultiplier,
        out float bladeWidthMultiplier)
    {
        densityMultiplier = 1f;
        bladeHeightMultiplier = 1f;
        bladeWidthMultiplier = 1f;

        if (GetTerrainBiomeCount() == 0)
        {
            return;
        }

        densityMultiplier = 0f;
        if (biomes == null)
        {
            return;
        }

        int scannedCount = 0;
        for (int i = 0; i < biomes.Length && scannedCount < MaxTerrainBiomeCount; i++)
        {
            TerrainBiomeSO biome = biomes[i];
            if (biome == null)
            {
                continue;
            }

            scannedCount++;
            biome.ValidateValues();
            BiomeGrassSettings grass = biome.Grass;
            float biomeDensityMultiplier = grass.DensityMultiplier;
            if (biomeDensityMultiplier <= 0f)
            {
                continue;
            }

            densityMultiplier = Mathf.Max(densityMultiplier, biomeDensityMultiplier);
            bladeHeightMultiplier = Mathf.Max(bladeHeightMultiplier, grass.BladeHeightMultiplier);
            bladeWidthMultiplier = Mathf.Max(bladeWidthMultiplier, grass.BladeWidthMultiplier);
        }

        if (densityMultiplier > 0f)
        {
            densityMultiplier = Mathf.Max(1f, densityMultiplier);
        }
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

            biomeData[writeIndex] = CreateBiomeData(biome, writeIndex);
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

            biomeData[writeIndex] = CreateTerrainBiomeLayerColorData(biome, writeIndex);
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

    private static int CollectActiveTerrainBiomeLayerColorChannels(
        TerrainBiomeLayerColorData[] biomeData,
        BiomeSamplingSettings settings,
        int[] activeChannels,
        out int activeMask)
    {
        activeMask = 0;
        if (activeChannels == null || biomeData == null || biomeData.Length == 0 || settings.Count <= 0)
        {
            return 0;
        }

        int activeCount = 0;
        for (int channelIndex = 0; channelIndex < MaxTerrainLayerCount; channelIndex++)
        {
            if (!HasTerrainBiomeLayerColorOverride(biomeData, settings, channelIndex))
            {
                continue;
            }

            activeChannels[activeCount] = channelIndex;
            activeMask |= 1 << channelIndex;
            activeCount++;
        }

        return activeCount;
    }

    private static void CopyActiveTerrainBiomeLayerColorChannels(
        NativeArray<int> destination,
        int[] source,
        int count)
    {
        if (!destination.IsCreated || source == null || count <= 0)
        {
            return;
        }

        int copyCount = math.min(destination.Length, math.min(source.Length, count));
        for (int i = 0; i < copyCount; i++)
        {
            destination[i] = source[i];
        }
    }

    private static void CopyTerrainBiomeLayerColorData(
        NativeArray<TerrainBiomeLayerColorJobData> destination,
        TerrainBiomeLayerColorData[] source)
    {
        if (!destination.IsCreated || destination.Length == 0 || source == null)
        {
            return;
        }

        int count = math.min(destination.Length, source.Length);
        for (int i = 0; i < count; i++)
        {
            destination[i] = CreateTerrainBiomeLayerColorJobData(source[i]);
        }
    }

    private static TerrainBiomeLayerColorJobData CreateTerrainBiomeLayerColorJobData(TerrainBiomeLayerColorData source)
    {
        TerrainBiomeLayerColorJobData data = new TerrainBiomeLayerColorJobData
        {
            DistanceRange = source != null ? source.DistanceRange : float4.zero
        };

        if (source?.HasLayerColor == null || source.LayerColors == null)
        {
            return data;
        }

        int count = math.min(source.HasLayerColor.Length, math.min(source.LayerColors.Length, MaxTerrainLayerCount));
        for (int channelIndex = 0; channelIndex < count; channelIndex++)
        {
            if (!source.HasLayerColor[channelIndex])
            {
                continue;
            }

            data.HasLayerColorMask |= 1 << channelIndex;
            SetTerrainBiomeLayerColor(ref data, channelIndex, ToFloat4(source.LayerColors[channelIndex]));
        }

        return data;
    }

    private static void SetTerrainBiomeLayerColor(
        ref TerrainBiomeLayerColorJobData data,
        int channelIndex,
        float4 color)
    {
        switch (channelIndex)
        {
            case 1:
                data.LayerColor1 = color;
                break;
            case 2:
                data.LayerColor2 = color;
                break;
            case 3:
                data.LayerColor3 = color;
                break;
            case 4:
                data.LayerColor4 = color;
                break;
            case 5:
                data.LayerColor5 = color;
                break;
            case 6:
                data.LayerColor6 = color;
                break;
            case 7:
                data.LayerColor7 = color;
                break;
            default:
                data.LayerColor0 = color;
                break;
        }
    }

    private static float4 ToFloat4(Color color)
    {
        return new float4(
            Mathf.Max(0f, color.r),
            Mathf.Max(0f, color.g),
            Mathf.Max(0f, color.b),
            Mathf.Clamp01(color.a));
    }

    private static int CompareBiomeData(GrassBiomeData a, GrassBiomeData b)
    {
        int minComparison = a.DistanceRange.x.CompareTo(b.DistanceRange.x);
        if (minComparison != 0)
        {
            return minComparison;
        }

        int maxComparison = a.DistanceRange.y.CompareTo(b.DistanceRange.y);
        return maxComparison != 0 ? maxComparison : a.DistanceRange.w.CompareTo(b.DistanceRange.w);
    }

    private static int CompareTerrainBiomeLayerColorData(TerrainBiomeLayerColorData a, TerrainBiomeLayerColorData b)
    {
        int minComparison = a.DistanceRange.x.CompareTo(b.DistanceRange.x);
        if (minComparison != 0)
        {
            return minComparison;
        }

        int maxComparison = a.DistanceRange.y.CompareTo(b.DistanceRange.y);
        return maxComparison != 0 ? maxComparison : a.DistanceRange.w.CompareTo(b.DistanceRange.w);
    }

    private static GrassBiomeData CreateBiomeData(TerrainBiomeSO biome, int biomeIndex)
    {
        biome.ValidateValues();
        Color grassColor = biome.GrassColor;
        BiomeGrassSettings grass = biome.Grass;
        return new GrassBiomeData
        {
            DistanceRange = new float4(
                biome.MinDistanceFromCenter,
                biome.MaxDistanceFromCenter,
                biome.SelectionWeight,
                biomeIndex),
            GrassColor = new float4(
                Mathf.Max(0f, grassColor.r),
                Mathf.Max(0f, grassColor.g),
                Mathf.Max(0f, grassColor.b),
                Mathf.Clamp01(grassColor.a)),
            GrassSettings = new float4(
                grass.DensityMultiplier,
                grass.BladeHeightMultiplier,
                grass.BladeWidthMultiplier,
                grass.ColorVariation)
        };
    }

    private static TerrainBiomeLayerColorData CreateTerrainBiomeLayerColorData(TerrainBiomeSO biome, int biomeIndex)
    {
        biome.ValidateValues();
        TerrainBiomeLayerColorData data = new TerrainBiomeLayerColorData
        {
            DistanceRange = new float4(
                biome.MinDistanceFromCenter,
                biome.MaxDistanceFromCenter,
                biome.SelectionWeight,
                biomeIndex),
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
        return EvaluateBiomeGrassSample(worldXZ, biomes, settings).GrassColor;
    }

    private static GrassBiomeSample EvaluateBiomeGrassSample(
        float2 worldXZ,
        GrassBiomeData[] biomes,
        BiomeSamplingSettings settings)
    {
        int count = biomes != null ? math.min(math.max(0, settings.Count), biomes.Length) : 0;
        if (count <= 0)
        {
            return CreateDefaultGrassBiomeSample(0f);
        }

        float2 placementWorldXZ = QuantizeBiomeGrassWorldXZ(worldXZ, settings);
        float placementBiomeDistance = EvaluateBiomeDistance(placementWorldXZ, settings);
        float colorBiomeDistance = settings.BlendDistance > 0.0001f
            ? EvaluateBiomeDistance(worldXZ, settings)
            : placementBiomeDistance;
        return ResolveGrassBiomeSample(
            placementWorldXZ,
            placementBiomeDistance,
            worldXZ,
            colorBiomeDistance,
            biomes,
            settings,
            count);
    }

    private static float3 EvaluateBiomeGrassColor(
        float2 worldXZ,
        NativeArray<GrassBiomeData> biomes,
        BiomeSamplingSettings settings)
    {
        return EvaluateBiomeGrassSample(worldXZ, biomes, settings).GrassColor;
    }

    private static GrassBiomeSample EvaluateBiomeGrassSample(
        float2 worldXZ,
        NativeArray<GrassBiomeData> biomes,
        BiomeSamplingSettings settings)
    {
        int count = math.min(math.max(0, settings.Count), biomes.Length);
        if (count <= 0)
        {
            return CreateDefaultGrassBiomeSample(0f);
        }

        float2 placementWorldXZ = QuantizeBiomeGrassWorldXZ(worldXZ, settings);
        float placementBiomeDistance = EvaluateBiomeDistance(placementWorldXZ, settings);
        float colorBiomeDistance = settings.BlendDistance > 0.0001f
            ? EvaluateBiomeDistance(worldXZ, settings)
            : placementBiomeDistance;
        return ResolveGrassBiomeSample(
            placementWorldXZ,
            placementBiomeDistance,
            worldXZ,
            colorBiomeDistance,
            biomes,
            settings,
            count);
    }

    private static GrassBiomeSample CreateDefaultGrassBiomeSample(float colorVariation)
    {
        return new GrassBiomeSample
        {
            GrassColor = new float3(1f, 1f, 1f),
            DensityMultiplier = 1f,
            BladeHeightMultiplier = 1f,
            BladeWidthMultiplier = 1f,
            ColorVariation = math.saturate(colorVariation)
        };
    }

    private static GrassBiomeSample CreateGrassBiomeSample(GrassBiomeData biome)
    {
        return new GrassBiomeSample
        {
            GrassColor = math.max(float3.zero, biome.GrassColor.xyz),
            DensityMultiplier = math.max(0f, biome.GrassSettings.x),
            BladeHeightMultiplier = math.max(0.01f, biome.GrassSettings.y),
            BladeWidthMultiplier = math.max(0.01f, biome.GrassSettings.z),
            ColorVariation = math.saturate(biome.GrassSettings.w)
        };
    }

    private static GrassBiomeSample ResolveGrassBiomeSample(
        float2 placementWorldXZ,
        float placementBiomeDistance,
        float2 colorWorldXZ,
        float colorBiomeDistance,
        GrassBiomeData[] biomes,
        BiomeSamplingSettings settings,
        int count)
    {
        PrepareGrassBiomeBlendState(
            out int placementPrimaryIndex,
            out float placementBestScore,
            out int placementSecondaryIndex,
            out float placementSecondScore,
            out int placementNearestIndex,
            out float placementNearestDistance,
            out int placementNearestTransitionIndex,
            out float placementNearestTransitionDistance,
            out int colorPrimaryIndex,
            out float colorBestScore,
            out int colorSecondaryIndex,
            out float colorSecondScore,
            out int colorNearestIndex,
            out float colorNearestDistance,
            out int colorNearestTransitionIndex,
            out float colorNearestTransitionDistance);

        float blendDistance = math.max(0f, settings.BlendDistance);
        for (int i = 0; i < count; i++)
        {
            float4 distanceRange = biomes[i].DistanceRange;
            TrackGrassBiomeCandidate(
                placementWorldXZ,
                placementBiomeDistance,
                settings,
                i,
                distanceRange,
                0f,
                ref placementPrimaryIndex,
                ref placementBestScore,
                ref placementSecondaryIndex,
                ref placementSecondScore,
                ref placementNearestIndex,
                ref placementNearestDistance,
                ref placementNearestTransitionIndex,
                ref placementNearestTransitionDistance);
            TrackGrassBiomeCandidate(
                colorWorldXZ,
                colorBiomeDistance,
                settings,
                i,
                distanceRange,
                blendDistance,
                ref colorPrimaryIndex,
                ref colorBestScore,
                ref colorSecondaryIndex,
                ref colorSecondScore,
                ref colorNearestIndex,
                ref colorNearestDistance,
                ref colorNearestTransitionIndex,
                ref colorNearestTransitionDistance);
        }

        if (placementPrimaryIndex < 0)
        {
            placementPrimaryIndex = placementNearestIndex;
        }

        if (placementPrimaryIndex < 0)
        {
            return CreateDefaultGrassBiomeSample(0f);
        }

        GrassBiomeSample sample = CreateGrassBiomeSample(biomes[placementPrimaryIndex]);
        ApplySmoothGrassBiomeColor(
            ref sample,
            colorPrimaryIndex,
            colorSecondaryIndex,
            colorSecondScore,
            colorBestScore,
            colorNearestIndex,
            colorNearestTransitionIndex,
            colorNearestTransitionDistance,
            biomes,
            settings,
            blendDistance);
        return sample;
    }

    private static GrassBiomeSample ResolveGrassBiomeSample(
        float2 placementWorldXZ,
        float placementBiomeDistance,
        float2 colorWorldXZ,
        float colorBiomeDistance,
        NativeArray<GrassBiomeData> biomes,
        BiomeSamplingSettings settings,
        int count)
    {
        PrepareGrassBiomeBlendState(
            out int placementPrimaryIndex,
            out float placementBestScore,
            out int placementSecondaryIndex,
            out float placementSecondScore,
            out int placementNearestIndex,
            out float placementNearestDistance,
            out int placementNearestTransitionIndex,
            out float placementNearestTransitionDistance,
            out int colorPrimaryIndex,
            out float colorBestScore,
            out int colorSecondaryIndex,
            out float colorSecondScore,
            out int colorNearestIndex,
            out float colorNearestDistance,
            out int colorNearestTransitionIndex,
            out float colorNearestTransitionDistance);

        float blendDistance = math.max(0f, settings.BlendDistance);
        for (int i = 0; i < count; i++)
        {
            float4 distanceRange = biomes[i].DistanceRange;
            TrackGrassBiomeCandidate(
                placementWorldXZ,
                placementBiomeDistance,
                settings,
                i,
                distanceRange,
                0f,
                ref placementPrimaryIndex,
                ref placementBestScore,
                ref placementSecondaryIndex,
                ref placementSecondScore,
                ref placementNearestIndex,
                ref placementNearestDistance,
                ref placementNearestTransitionIndex,
                ref placementNearestTransitionDistance);
            TrackGrassBiomeCandidate(
                colorWorldXZ,
                colorBiomeDistance,
                settings,
                i,
                distanceRange,
                blendDistance,
                ref colorPrimaryIndex,
                ref colorBestScore,
                ref colorSecondaryIndex,
                ref colorSecondScore,
                ref colorNearestIndex,
                ref colorNearestDistance,
                ref colorNearestTransitionIndex,
                ref colorNearestTransitionDistance);
        }

        if (placementPrimaryIndex < 0)
        {
            placementPrimaryIndex = placementNearestIndex;
        }

        if (placementPrimaryIndex < 0)
        {
            return CreateDefaultGrassBiomeSample(0f);
        }

        GrassBiomeSample sample = CreateGrassBiomeSample(biomes[placementPrimaryIndex]);
        ApplySmoothGrassBiomeColor(
            ref sample,
            colorPrimaryIndex,
            colorSecondaryIndex,
            colorSecondScore,
            colorBestScore,
            colorNearestIndex,
            colorNearestTransitionIndex,
            colorNearestTransitionDistance,
            biomes,
            settings,
            blendDistance);
        return sample;
    }

    private static void PrepareGrassBiomeBlendState(
        out int placementPrimaryIndex,
        out float placementBestScore,
        out int placementSecondaryIndex,
        out float placementSecondScore,
        out int placementNearestIndex,
        out float placementNearestDistance,
        out int placementNearestTransitionIndex,
        out float placementNearestTransitionDistance,
        out int colorPrimaryIndex,
        out float colorBestScore,
        out int colorSecondaryIndex,
        out float colorSecondScore,
        out int colorNearestIndex,
        out float colorNearestDistance,
        out int colorNearestTransitionIndex,
        out float colorNearestTransitionDistance)
    {
        placementPrimaryIndex = -1;
        placementBestScore = float.MinValue;
        placementSecondaryIndex = -1;
        placementSecondScore = float.MinValue;
        placementNearestIndex = -1;
        placementNearestDistance = float.MaxValue;
        placementNearestTransitionIndex = -1;
        placementNearestTransitionDistance = float.MaxValue;

        colorPrimaryIndex = -1;
        colorBestScore = float.MinValue;
        colorSecondaryIndex = -1;
        colorSecondScore = float.MinValue;
        colorNearestIndex = -1;
        colorNearestDistance = float.MaxValue;
        colorNearestTransitionIndex = -1;
        colorNearestTransitionDistance = float.MaxValue;
    }

    private static void TrackGrassBiomeCandidate(
        float2 worldXZ,
        float biomeDistance,
        BiomeSamplingSettings settings,
        int candidateIndex,
        float4 distanceRange,
        float blendDistance,
        ref int primaryIndex,
        ref float bestScore,
        ref int secondaryIndex,
        ref float secondScore,
        ref int nearestIndex,
        ref float nearestDistance,
        ref int nearestTransitionIndex,
        ref float nearestTransitionDistance)
    {
        float minDistance = math.max(0f, distanceRange.x);
        float maxDistance = math.max(minDistance, distanceRange.y);
        float selectionWeight = GetBiomeSelectionWeight(distanceRange);
        if (selectionWeight <= 0f)
        {
            return;
        }

        float distanceToRange = DistanceToRange(biomeDistance, minDistance, maxDistance);
        if (distanceToRange < nearestDistance)
        {
            nearestDistance = distanceToRange;
            nearestIndex = candidateIndex;
        }

        if (distanceToRange <= 0.0001f)
        {
            int biomeSeedIndex = GetBiomeSeedIndex(distanceRange, candidateIndex);
            float score = EvaluateBiomeSelectionScore(worldXZ, settings, biomeSeedIndex, selectionWeight);
            if (score > bestScore || (math.abs(score - bestScore) <= 0.0001f && candidateIndex > primaryIndex))
            {
                secondaryIndex = primaryIndex;
                secondScore = bestScore;
                primaryIndex = candidateIndex;
                bestScore = score;
            }
            else if (score > secondScore || (math.abs(score - secondScore) <= 0.0001f && candidateIndex > secondaryIndex))
            {
                secondaryIndex = candidateIndex;
                secondScore = score;
            }
        }
        else if (blendDistance > 0.0001f
            && distanceToRange < nearestTransitionDistance
            && distanceToRange <= blendDistance)
        {
            nearestTransitionDistance = distanceToRange;
            nearestTransitionIndex = candidateIndex;
        }
    }

    private static void ApplySmoothGrassBiomeColor(
        ref GrassBiomeSample sample,
        int colorPrimaryIndex,
        int colorSecondaryIndex,
        float colorSecondScore,
        float colorBestScore,
        int colorNearestIndex,
        int colorNearestTransitionIndex,
        float colorNearestTransitionDistance,
        GrassBiomeData[] biomes,
        BiomeSamplingSettings settings,
        float blendDistance)
    {
        if (colorPrimaryIndex < 0)
        {
            colorPrimaryIndex = colorNearestIndex;
        }

        if (colorPrimaryIndex < 0)
        {
            return;
        }

        GrassBiomeSample colorSample = CreateGrassBiomeSample(biomes[colorPrimaryIndex]);
        sample.GrassColor = colorSample.GrassColor;
        sample.ColorVariation = colorSample.ColorVariation;
        if (TryGetGrassBiomeColorBlend(
            colorSecondaryIndex,
            colorSecondScore,
            colorBestScore,
            colorNearestTransitionIndex,
            colorNearestTransitionDistance,
            colorPrimaryIndex,
            settings,
            blendDistance,
            out int blendIndex,
            out float blendWeight))
        {
            BlendGrassBiomeColor(ref sample, CreateGrassBiomeSample(biomes[blendIndex]), blendWeight);
        }
    }

    private static void ApplySmoothGrassBiomeColor(
        ref GrassBiomeSample sample,
        int colorPrimaryIndex,
        int colorSecondaryIndex,
        float colorSecondScore,
        float colorBestScore,
        int colorNearestIndex,
        int colorNearestTransitionIndex,
        float colorNearestTransitionDistance,
        NativeArray<GrassBiomeData> biomes,
        BiomeSamplingSettings settings,
        float blendDistance)
    {
        if (colorPrimaryIndex < 0)
        {
            colorPrimaryIndex = colorNearestIndex;
        }

        if (colorPrimaryIndex < 0)
        {
            return;
        }

        GrassBiomeSample colorSample = CreateGrassBiomeSample(biomes[colorPrimaryIndex]);
        sample.GrassColor = colorSample.GrassColor;
        sample.ColorVariation = colorSample.ColorVariation;
        if (TryGetGrassBiomeColorBlend(
            colorSecondaryIndex,
            colorSecondScore,
            colorBestScore,
            colorNearestTransitionIndex,
            colorNearestTransitionDistance,
            colorPrimaryIndex,
            settings,
            blendDistance,
            out int blendIndex,
            out float blendWeight))
        {
            BlendGrassBiomeColor(ref sample, CreateGrassBiomeSample(biomes[blendIndex]), blendWeight);
        }
    }

    private static bool TryGetGrassBiomeColorBlend(
        int secondaryIndex,
        float secondScore,
        float bestScore,
        int nearestTransitionIndex,
        float nearestTransitionDistance,
        int primaryIndex,
        BiomeSamplingSettings settings,
        float blendDistance,
        out int blendIndex,
        out float blendWeight)
    {
        blendIndex = -1;
        blendWeight = 0f;

        if (blendDistance > 0.0001f && secondaryIndex >= 0)
        {
            float selectionBlendWidth = GetBiomeSelectionBlendWidth(settings);
            if (selectionBlendWidth > 0.0001f)
            {
                float scoreGap = math.max(0f, bestScore - secondScore);
                float scoreBlend = 0.5f - scoreGap / (selectionBlendWidth * 2f);
                if (scoreBlend > blendWeight)
                {
                    blendIndex = secondaryIndex;
                    blendWeight = math.saturate(scoreBlend);
                }
            }
        }

        if (blendDistance > 0.0001f
            && nearestTransitionIndex >= 0
            && nearestTransitionIndex != primaryIndex)
        {
            float t = math.saturate(nearestTransitionDistance / blendDistance);
            float rangeBlend = (1f - SmoothBiomeBlend01(t)) * 0.5f;
            if (rangeBlend > blendWeight)
            {
                blendIndex = nearestTransitionIndex;
                blendWeight = rangeBlend;
            }
        }

        return blendIndex >= 0 && blendWeight > 0.0001f;
    }

    private static void BlendGrassBiomeColor(
        ref GrassBiomeSample sample,
        GrassBiomeSample secondarySample,
        float weight)
    {
        float blendWeight = math.saturate(weight);
        sample.GrassColor = math.lerp(sample.GrassColor, secondarySample.GrassColor, blendWeight);
        sample.ColorVariation = math.lerp(sample.ColorVariation, secondarySample.ColorVariation, blendWeight);
    }

    private static float2 QuantizeBiomeGrassWorldXZ(float2 worldXZ, BiomeSamplingSettings settings)
    {
        if (settings.TerrainChunkSize <= 0.0001f || settings.BiomeSampleSpacing <= 0.0001f)
        {
            return worldXZ;
        }

        float2 chunkCoord = math.floor(worldXZ / settings.TerrainChunkSize);
        float2 chunkOrigin = chunkCoord * settings.TerrainChunkSize;
        float2 local = worldXZ - chunkOrigin;
        return chunkOrigin + math.round(local / settings.BiomeSampleSpacing) * settings.BiomeSampleSpacing;
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
        return SampleBiomeFractalValueNoise(worldXZ, settings, -10007);
    }

    private static float GetBiomeSelectionWeight(float4 distanceRange)
    {
        return math.max(0f, distanceRange.z);
    }

    private static int GetBiomeSeedIndex(float4 distanceRange, int fallbackIndex)
    {
        return distanceRange.w >= 0f ? (int)math.round(distanceRange.w) : fallbackIndex;
    }

    private static float EvaluateBiomeSelectionScore(
        float2 worldXZ,
        BiomeSamplingSettings settings,
        int biomeIndex,
        float selectionWeight)
    {
        float score = settings.UseNoise != 0
            ? SampleBiomeSelectionNoise(worldXZ, settings, biomeIndex)
            : 0f;
        float weightBias = math.log(math.max(0.0001f, selectionWeight)) * 0.35f;
        return score + weightBias + biomeIndex * 0.00001f;
    }

    private static float SampleBiomeSelectionNoise(
        float2 worldXZ,
        BiomeSamplingSettings settings,
        int biomeIndex)
    {
        return SampleBiomeFractalValueNoise(worldXZ, settings, biomeIndex);
    }

    private static float SampleBiomeFractalValueNoise(
        float2 worldXZ,
        BiomeSamplingSettings settings,
        int salt)
    {
        float frequency = math.max(0.000001f, settings.NoiseFrequency);
        float amplitude = 1f;
        float value = 0f;
        float amplitudeSum = 0f;
        int octaveCount = math.clamp(settings.NoiseOctaves, 1, 8);
        float2 seedOffset = settings.NoiseOffset + new float2(settings.Seed * 17.13f, settings.Seed * -31.71f);

        for (int octave = 0; octave < octaveCount; octave++)
        {
            value += SampleBiomeValueNoise((worldXZ + seedOffset) * frequency, settings.Seed, salt, octave) * amplitude;
            amplitudeSum += amplitude;
            amplitude *= math.saturate(settings.NoisePersistence);
            frequency *= math.max(1f, settings.NoiseLacunarity);
        }

        return amplitudeSum > 0.0001f ? value / amplitudeSum : 0f;
    }

    private static float SampleBiomeValueNoise(float2 sample, int seed, int salt, int octave)
    {
        int x0 = (int)math.floor(sample.x);
        int z0 = (int)math.floor(sample.y);
        int x1 = x0 + 1;
        int z1 = z0 + 1;
        float2 t = new float2(sample.x - x0, sample.y - z0);
        t = t * t * (3f - 2f * t);

        float v00 = BiomeHash01(BiomeNoiseHash(x0, z0, seed, salt, octave)) * 2f - 1f;
        float v10 = BiomeHash01(BiomeNoiseHash(x1, z0, seed, salt, octave)) * 2f - 1f;
        float v01 = BiomeHash01(BiomeNoiseHash(x0, z1, seed, salt, octave)) * 2f - 1f;
        float v11 = BiomeHash01(BiomeNoiseHash(x1, z1, seed, salt, octave)) * 2f - 1f;

        return math.lerp(math.lerp(v00, v10, t.x), math.lerp(v01, v11, t.x), t.y);
    }

    private static uint BiomeNoiseHash(int x, int z, int seed, int salt, int octave)
    {
        unchecked
        {
            uint hash = 2166136261u;
            hash = BiomeMix(hash, (uint)x);
            hash = BiomeMix(hash, (uint)z);
            hash = BiomeMix(hash, (uint)seed);
            hash = BiomeMix(hash, (uint)salt);
            hash = BiomeMix(hash, (uint)octave);
            return hash;
        }
    }

    private static uint BiomeMix(uint hash, uint value)
    {
        unchecked
        {
            hash ^= value;
            hash *= 16777619u;
            return hash;
        }
    }

    private static uint BiomeHash(uint value)
    {
        unchecked
        {
            value ^= value >> 16;
            value *= 0x7feb352du;
            value ^= value >> 15;
            value *= 0x846ca68bu;
            value ^= value >> 16;
            return value;
        }
    }

    private static float BiomeHash01(uint value)
    {
        return (BiomeHash(value) >> 8) * (1f / 16777216f);
    }

    private static int ResolveBiomeIndex(
        float2 worldXZ,
        float biomeDistance,
        GrassBiomeData[] biomes,
        BiomeSamplingSettings settings,
        int count)
    {
        int biomeIndex = -1;
        float bestScore = float.MinValue;
        float nearestDistance = float.MaxValue;
        int nearestIndex = -1;

        for (int i = 0; i < count; i++)
        {
            GrassBiomeData biome = biomes[i];
            float minDistance = math.max(0f, biome.DistanceRange.x);
            float maxDistance = math.max(minDistance, biome.DistanceRange.y);
            float selectionWeight = GetBiomeSelectionWeight(biome.DistanceRange);
            if (selectionWeight <= 0f)
            {
                continue;
            }

            float distanceToRange = DistanceToRange(biomeDistance, minDistance, maxDistance);
            if (distanceToRange < nearestDistance)
            {
                nearestDistance = distanceToRange;
                nearestIndex = i;
            }

            if (distanceToRange <= 0.0001f)
            {
                int biomeSeedIndex = GetBiomeSeedIndex(biome.DistanceRange, i);
                float score = EvaluateBiomeSelectionScore(worldXZ, settings, biomeSeedIndex, selectionWeight);
                if (score > bestScore || (math.abs(score - bestScore) <= 0.0001f && i > biomeIndex))
                {
                    biomeIndex = i;
                    bestScore = score;
                }
            }
        }

        return biomeIndex >= 0 ? biomeIndex : nearestIndex;
    }

    private static TerrainBiomeLayerColorBlend ResolveTerrainBiomeLayerColorBlend(
        float2 worldXZ,
        float biomeDistance,
        TerrainBiomeLayerColorData[] biomes,
        BiomeSamplingSettings settings,
        int count)
    {
        TerrainBiomeLayerColorBlend blend = new TerrainBiomeLayerColorBlend
        {
            PrimaryIndex = -1,
            SecondaryIndex = -1,
            SecondaryWeight = 0f
        };

        float bestScore = float.MinValue;
        float secondScore = float.MinValue;
        int secondIndex = -1;
        float nearestDistance = float.MaxValue;
        int nearestIndex = -1;
        float nearestTransitionDistance = float.MaxValue;
        int nearestTransitionIndex = -1;
        float blendDistance = math.max(0f, settings.BlendDistance);

        for (int i = 0; i < count; i++)
        {
            TerrainBiomeLayerColorData biome = biomes[i];
            float minDistance = math.max(0f, biome.DistanceRange.x);
            float maxDistance = math.max(minDistance, biome.DistanceRange.y);
            float selectionWeight = GetBiomeSelectionWeight(biome.DistanceRange);
            if (selectionWeight <= 0f)
            {
                continue;
            }

            float distanceToRange = DistanceToRange(biomeDistance, minDistance, maxDistance);
            if (distanceToRange < nearestDistance)
            {
                nearestDistance = distanceToRange;
                nearestIndex = i;
            }

            if (distanceToRange <= 0.0001f)
            {
                int biomeSeedIndex = GetBiomeSeedIndex(biome.DistanceRange, i);
                float score = EvaluateBiomeSelectionScore(worldXZ, settings, biomeSeedIndex, selectionWeight);
                if (score > bestScore || (math.abs(score - bestScore) <= 0.0001f && i > blend.PrimaryIndex))
                {
                    secondIndex = blend.PrimaryIndex;
                    secondScore = bestScore;
                    blend.PrimaryIndex = i;
                    bestScore = score;
                }
                else if (score > secondScore || (math.abs(score - secondScore) <= 0.0001f && i > secondIndex))
                {
                    secondIndex = i;
                    secondScore = score;
                }
            }
            else if (blendDistance > 0.0001f
                && distanceToRange < nearestTransitionDistance
                && distanceToRange <= blendDistance)
            {
                nearestTransitionDistance = distanceToRange;
                nearestTransitionIndex = i;
            }
        }

        if (blend.PrimaryIndex < 0)
        {
            blend.PrimaryIndex = nearestIndex;
            return blend;
        }

        if (blendDistance <= 0.0001f)
        {
            return blend;
        }

        if (secondIndex >= 0)
        {
            float selectionBlendWidth = GetBiomeSelectionBlendWidth(settings);
            if (selectionBlendWidth > 0.0001f)
            {
                float scoreGap = math.max(0f, bestScore - secondScore);
                float scoreBlend = 0.5f - scoreGap / (selectionBlendWidth * 2f);
                if (scoreBlend > blend.SecondaryWeight)
                {
                    blend.SecondaryIndex = secondIndex;
                    blend.SecondaryWeight = math.saturate(scoreBlend);
                }
            }
        }

        if (nearestTransitionIndex >= 0 && nearestTransitionIndex != blend.PrimaryIndex)
        {
            float t = math.saturate(nearestTransitionDistance / blendDistance);
            float rangeBlend = (1f - SmoothBiomeBlend01(t)) * 0.5f;
            if (rangeBlend > blend.SecondaryWeight)
            {
                blend.SecondaryIndex = nearestTransitionIndex;
                blend.SecondaryWeight = rangeBlend;
            }
        }

        return blend;
    }

    private static TerrainBiomeLayerColorBlend ResolveTerrainBiomeLayerColorBlend(
        float2 worldXZ,
        float biomeDistance,
        NativeArray<TerrainBiomeLayerColorJobData> biomes,
        BiomeSamplingSettings settings,
        int count)
    {
        TerrainBiomeLayerColorBlend blend = new TerrainBiomeLayerColorBlend
        {
            PrimaryIndex = -1,
            SecondaryIndex = -1,
            SecondaryWeight = 0f
        };

        float bestScore = float.MinValue;
        float secondScore = float.MinValue;
        int secondIndex = -1;
        float nearestDistance = float.MaxValue;
        int nearestIndex = -1;
        float nearestTransitionDistance = float.MaxValue;
        int nearestTransitionIndex = -1;
        float blendDistance = math.max(0f, settings.BlendDistance);
        int biomeCount = math.min(math.max(0, count), biomes.Length);

        for (int i = 0; i < biomeCount; i++)
        {
            TerrainBiomeLayerColorJobData biome = biomes[i];
            float minDistance = math.max(0f, biome.DistanceRange.x);
            float maxDistance = math.max(minDistance, biome.DistanceRange.y);
            float selectionWeight = GetBiomeSelectionWeight(biome.DistanceRange);
            if (selectionWeight <= 0f)
            {
                continue;
            }

            float distanceToRange = DistanceToRange(biomeDistance, minDistance, maxDistance);
            if (distanceToRange < nearestDistance)
            {
                nearestDistance = distanceToRange;
                nearestIndex = i;
            }

            if (distanceToRange <= 0.0001f)
            {
                int biomeSeedIndex = GetBiomeSeedIndex(biome.DistanceRange, i);
                float score = EvaluateBiomeSelectionScore(worldXZ, settings, biomeSeedIndex, selectionWeight);
                if (score > bestScore || (math.abs(score - bestScore) <= 0.0001f && i > blend.PrimaryIndex))
                {
                    secondIndex = blend.PrimaryIndex;
                    secondScore = bestScore;
                    blend.PrimaryIndex = i;
                    bestScore = score;
                }
                else if (score > secondScore || (math.abs(score - secondScore) <= 0.0001f && i > secondIndex))
                {
                    secondIndex = i;
                    secondScore = score;
                }
            }
            else if (blendDistance > 0.0001f
                && distanceToRange < nearestTransitionDistance
                && distanceToRange <= blendDistance)
            {
                nearestTransitionDistance = distanceToRange;
                nearestTransitionIndex = i;
            }
        }

        if (blend.PrimaryIndex < 0)
        {
            blend.PrimaryIndex = nearestIndex;
            return blend;
        }

        if (blendDistance <= 0.0001f)
        {
            return blend;
        }

        if (secondIndex >= 0)
        {
            float selectionBlendWidth = GetBiomeSelectionBlendWidth(settings);
            if (selectionBlendWidth > 0.0001f)
            {
                float scoreGap = math.max(0f, bestScore - secondScore);
                float scoreBlend = 0.5f - scoreGap / (selectionBlendWidth * 2f);
                if (scoreBlend > blend.SecondaryWeight)
                {
                    blend.SecondaryIndex = secondIndex;
                    blend.SecondaryWeight = math.saturate(scoreBlend);
                }
            }
        }

        if (nearestTransitionIndex >= 0 && nearestTransitionIndex != blend.PrimaryIndex)
        {
            float t = math.saturate(nearestTransitionDistance / blendDistance);
            float rangeBlend = (1f - SmoothBiomeBlend01(t)) * 0.5f;
            if (rangeBlend > blend.SecondaryWeight)
            {
                blend.SecondaryIndex = nearestTransitionIndex;
                blend.SecondaryWeight = rangeBlend;
            }
        }

        return blend;
    }

    private static float GetBiomeSelectionBlendWidth(BiomeSamplingSettings settings)
    {
        if (settings.UseNoise == 0 || settings.BlendDistance <= 0.0001f)
        {
            return 0f;
        }

        return math.clamp(settings.BlendDistance * math.max(0.000001f, settings.NoiseFrequency), 0.02f, 0.5f);
    }

    private static float SmoothBiomeBlend01(float value)
    {
        float t = math.saturate(value);
        return t * t * (3f - 2f * t);
    }

    private static int ResolveBiomeIndex(
        float2 worldXZ,
        float biomeDistance,
        NativeArray<GrassBiomeData> biomes,
        BiomeSamplingSettings settings,
        int count)
    {
        int biomeIndex = -1;
        float bestScore = float.MinValue;
        float nearestDistance = float.MaxValue;
        int nearestIndex = -1;

        for (int i = 0; i < count; i++)
        {
            GrassBiomeData biome = biomes[i];
            float minDistance = math.max(0f, biome.DistanceRange.x);
            float maxDistance = math.max(minDistance, biome.DistanceRange.y);
            float selectionWeight = GetBiomeSelectionWeight(biome.DistanceRange);
            if (selectionWeight <= 0f)
            {
                continue;
            }

            float distanceToRange = DistanceToRange(biomeDistance, minDistance, maxDistance);
            if (distanceToRange < nearestDistance)
            {
                nearestDistance = distanceToRange;
                nearestIndex = i;
            }

            if (distanceToRange <= 0.0001f)
            {
                int biomeSeedIndex = GetBiomeSeedIndex(biome.DistanceRange, i);
                float score = EvaluateBiomeSelectionScore(worldXZ, settings, biomeSeedIndex, selectionWeight);
                if (score > bestScore || (math.abs(score - bestScore) <= 0.0001f && i > biomeIndex))
                {
                    biomeIndex = i;
                    bestScore = score;
                }
            }
        }

        return biomeIndex >= 0 ? biomeIndex : nearestIndex;
    }

    private static float DistanceToRange(float value, float minDistance, float maxDistance)
    {
        if (value < minDistance)
        {
            return minDistance - value;
        }

        return value > maxDistance ? value - maxDistance : 0f;
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

    private static float3 ResolveTerrainBiomeLayerColor(
        TerrainBiomeLayerColorJobData biome,
        int channelIndex,
        float3 fallbackColor)
    {
        int clampedChannelIndex = math.clamp(channelIndex, 0, MaxTerrainLayerCount - 1);
        if ((biome.HasLayerColorMask & (1 << clampedChannelIndex)) == 0)
        {
            return fallbackColor;
        }

        return GetTerrainBiomeLayerColor(biome, clampedChannelIndex).xyz;
    }

    private static float4 GetTerrainBiomeLayerColor(TerrainBiomeLayerColorJobData biome, int channelIndex)
    {
        switch (channelIndex)
        {
            case 1:
                return biome.LayerColor1;
            case 2:
                return biome.LayerColor2;
            case 3:
                return biome.LayerColor3;
            case 4:
                return biome.LayerColor4;
            case 5:
                return biome.LayerColor5;
            case 6:
                return biome.LayerColor6;
            case 7:
                return biome.LayerColor7;
            default:
                return biome.LayerColor0;
        }
    }

    private static float3 ToFloat3(Color color)
    {
        return new float3(
            Mathf.Max(0f, color.r),
            Mathf.Max(0f, color.g),
            Mathf.Max(0f, color.b));
    }
}
