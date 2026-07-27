using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

public partial class InfinitMeshTerrain
{
    private TerrainSettings CreateTerrainSettings()
    {
        TerrainShapeSettingsSO shapeSettings = terrainShapeSettings;

        return new TerrainSettings
        {
            NoiseOffset = shapeSettings != null ? shapeSettings.NoiseOffset : TerrainShapeSettingsSO.DefaultNoiseOffset,
            TerrainSeed = GetTerrainSeed(),
            ContinentFrequency = shapeSettings != null ? shapeSettings.ContinentFrequency : TerrainShapeSettingsSO.DefaultContinentFrequency,
            DomainWarpFrequency = shapeSettings != null ? shapeSettings.DomainWarpFrequency : TerrainShapeSettingsSO.DefaultDomainWarpFrequency,
            DomainWarpStrength = shapeSettings != null ? shapeSettings.DomainWarpStrength : TerrainShapeSettingsSO.DefaultDomainWarpStrength,
            BiomeFrequency = shapeSettings != null ? shapeSettings.BiomeFrequency : TerrainShapeSettingsSO.DefaultBiomeFrequency,
            RidgeFrequency = shapeSettings != null ? shapeSettings.RidgeFrequency : TerrainShapeSettingsSO.DefaultRidgeFrequency,
            DetailFrequency = shapeSettings != null ? shapeSettings.DetailFrequency : TerrainShapeSettingsSO.DefaultDetailFrequency,
            SeaCoverage = shapeSettings != null ? shapeSettings.SeaCoverage : TerrainShapeSettingsSO.DefaultSeaCoverage,
            MountainStart = shapeSettings != null ? shapeSettings.MountainStart : TerrainShapeSettingsSO.DefaultMountainStart,
            PlainsStrength = shapeSettings != null ? shapeSettings.PlainsStrength : TerrainShapeSettingsSO.DefaultPlainsStrength,
            HillsStrength = shapeSettings != null ? shapeSettings.HillsStrength : TerrainShapeSettingsSO.DefaultHillsStrength,
            MountainStrength = shapeSettings != null ? shapeSettings.MountainStrength : TerrainShapeSettingsSO.DefaultMountainStrength,
            CliffStrength = shapeSettings != null ? shapeSettings.CliffStrength : TerrainShapeSettingsSO.DefaultCliffStrength,
            DetailStrength = shapeSettings != null ? shapeSettings.DetailStrength : TerrainShapeSettingsSO.DefaultDetailStrength,
            TerraceStrength = shapeSettings != null ? shapeSettings.TerraceStrength : TerrainShapeSettingsSO.DefaultTerraceStrength,
            TerraceSteps = shapeSettings != null ? shapeSettings.TerraceSteps : TerrainShapeSettingsSO.DefaultTerraceSteps,
            TerrainSplineInfluence = shapeSettings != null ? shapeSettings.TerrainSplineInfluence : TerrainShapeSettingsSO.DefaultTerrainSplineInfluence,
            NoiseLayerInfluence = shapeSettings != null ? shapeSettings.NoiseLayerInfluence : TerrainShapeSettingsSO.DefaultNoiseLayerInfluence
        };
    }

    private float GetTerrainHeightMultiplier()
    {
        return terrainShapeSettings != null
            ? terrainShapeSettings.HeightMultiplier
            : TerrainShapeSettingsSO.DefaultHeightMultiplier;
    }

    private int GetTerrainSeed()
    {
        return terrainShapeSettings != null
            ? terrainShapeSettings.TerrainSeed
            : TerrainShapeSettingsSO.DefaultTerrainSeed;
    }

    private TerrainSplinesSO GetTerrainSplines()
    {
        return terrainShapeSettings != null ? terrainShapeSettings.TerrainSplines : null;
    }

    private NoiseLayersSO GetNoiseSettings()
    {
        return terrainShapeSettings != null ? terrainShapeSettings.NoiseSettings : null;
    }

    private void CopyNoiseLayers(NativeArray<NoiseLayerData> destination)
    {
        NoiseLayersSO settings = GetNoiseSettings();
        if (!destination.IsCreated || destination.Length == 0 || settings == null || settings.NoiseLayers == null)
        {
            return;
        }

        IReadOnlyList<NoiseLayer> source = settings.NoiseLayers;
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
        NoiseLayersSO settings = GetNoiseSettings();
        return settings != null && settings.NoiseLayers != null ? settings.NoiseLayers.Count : 0;
    }

    private void CopyTerrainSplineSamples(NativeArray<float> destination)
    {
        TerrainSplinesSO splines = GetTerrainSplines();
        if (!destination.IsCreated || destination.Length == 0 || splines == null)
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
                destination[baseIndex + i] = splines.Evaluate(splineChannel, input);
            }
        }
    }

    private int GetTerrainSplineSampleCount()
    {
        return GetTerrainSplines() != null ? TerrainSplineSampleCount * TerrainSplineChannelCount : 0;
    }
}
