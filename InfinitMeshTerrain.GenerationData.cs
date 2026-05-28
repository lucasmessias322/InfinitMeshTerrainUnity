using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

public partial class InfinitMeshTerrain
{
    private TerrainSettings CreateTerrainSettings()
    {
        return new TerrainSettings
        {
            NoiseOffset = noiseOffset,
            TerrainSeed = terrainSeed,
            ContinentFrequency = continentFrequency,
            DomainWarpFrequency = domainWarpFrequency,
            DomainWarpStrength = domainWarpStrength,
            BiomeFrequency = biomeFrequency,
            RidgeFrequency = ridgeFrequency,
            DetailFrequency = detailFrequency,
            SeaCoverage = seaCoverage,
            MountainStart = mountainStart,
            PlainsStrength = plainsStrength,
            HillsStrength = hillsStrength,
            MountainStrength = mountainStrength,
            CliffStrength = cliffStrength,
            DetailStrength = detailStrength,
            TerraceStrength = terraceStrength,
            TerraceSteps = terraceSteps,
            TerrainSplineInfluence = terrainSplineInfluence,
            NoiseLayerInfluence = noiseLayerInfluence
        };
    }

    private void CopyNoiseLayers(NativeArray<NoiseLayerData> destination)
    {
        if (!destination.IsCreated || destination.Length == 0 || noiseSettings == null || noiseSettings.NoiseLayers == null)
        {
            return;
        }

        IReadOnlyList<NoiseLayer> source = noiseSettings.NoiseLayers;
        for (int i = 0; i < destination.Length; i++)
        {
            NoiseLayer layer = source[i];
            destination[i] = new NoiseLayerData
            {
                Scale = new float2(
                    Mathf.Max(0.000001f, Mathf.Abs(layer.scaleX)),
                    Mathf.Max(0.000001f, Mathf.Abs(layer.scaleY))),
                Amplitude = layer.amplitude,
                Role = (int)layer.role,
                Octaves = Mathf.Clamp(layer.octaves <= 0 ? 1 : layer.octaves, 1, 12),
                Lacunarity = Mathf.Max(1f, layer.lacunarity <= 0f ? 2f : layer.lacunarity),
                Gain = Mathf.Clamp01(layer.persistence <= 0f ? 0.5f : layer.persistence),
                HeightThreshold = layer.heightThreshold,
                Offset = layer.offset,
                BlendRange = math.max(0.0001f, layer.blendRange)
            };
        }
    }

    private int GetNoiseLayerCount()
    {
        return noiseSettings != null && noiseSettings.NoiseLayers != null ? noiseSettings.NoiseLayers.Count : 0;
    }

    private void CopyTerrainSplineSamples(NativeArray<float> destination)
    {
        if (!destination.IsCreated || destination.Length == 0 || terrainSplines == null)
        {
            return;
        }

        for (int channel = 0; channel < TerrainSplineChannelCount; channel++)
        {
            int baseIndex = channel * TerrainSplineSampleCount;
            TerrainSplineChannel splineChannel = (TerrainSplineChannel)channel;

            for (int i = 0; i < TerrainSplineSampleCount; i++)
            {
                float input = i / (float)(TerrainSplineSampleCount - 1);
                destination[baseIndex + i] = terrainSplines.Evaluate(splineChannel, input);
            }
        }
    }

    private int GetTerrainSplineSampleCount()
    {
        return terrainSplines != null ? TerrainSplineSampleCount * TerrainSplineChannelCount : 0;
    }
}
