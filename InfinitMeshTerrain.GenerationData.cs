using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

public partial class InfinitMeshTerrain
{
    private TerrainSettings CreateTerrainSettings()
    {
        TerrainShapeSettingsSO shapeSettings = terrainShapeSettings;
        if (shapeSettings != null)
        {
            shapeSettings.ValidateValues();
        }

        return new TerrainSettings
        {
            NoiseOffset = shapeSettings != null ? shapeSettings.NoiseOffset : TerrainShapeSettingsSO.DefaultNoiseOffset,
            TerrainSeed = GetTerrainSeed(),
            MinHeight = shapeSettings != null ? shapeSettings.MinHeight : 0f,
            MaxHeight = shapeSettings != null ? shapeSettings.MaxHeight : TerrainShapeSettingsSO.DefaultLayeredMaxHeight
        };
    }

    private int GetTerrainHeightLayerCount()
    {
        TerrainShapeSettingsSO shapeSettings = terrainShapeSettings;
        if (shapeSettings == null)
        {
            return 0;
        }

        shapeSettings.ValidateValues();
        if (!shapeSettings.UseLayeredHeight)
        {
            return 0;
        }

        IReadOnlyList<TerrainHeightLayerDefinition> layers = shapeSettings.HeightLayers;
        int count = 0;
        for (int i = 0; i < layers.Count; i++)
        {
            if (layers[i].HasContribution)
            {
                count++;
            }
        }

        return count;
    }

    private int GetTerrainSplineSampleCount()
    {
        TerrainShapeSettingsSO shapeSettings = terrainShapeSettings;
        if (shapeSettings == null)
        {
            return 0;
        }

        shapeSettings.ValidateValues();
        if (!shapeSettings.UseLayeredHeight)
        {
            return 0;
        }

        int sampleCount = 0;
        if (shapeSettings.UseContinentalSpline)
        {
            sampleCount += TerrainShapeSettingsSO.SplineSampleCount;
        }

        if (shapeSettings.UseMountainSpline)
        {
            sampleCount += TerrainShapeSettingsSO.SplineSampleCount;
        }

        return sampleCount;
    }

    private void CopyTerrainHeightLayers(
        NativeArray<TerrainHeightNoiseLayerData> target,
        NativeArray<float> splineSamples)
    {
        if (!target.IsCreated || target.Length == 0)
        {
            return;
        }

        TerrainShapeSettingsSO shapeSettings = terrainShapeSettings;
        if (shapeSettings == null)
        {
            return;
        }

        shapeSettings.ValidateValues();
        if (!shapeSettings.UseLayeredHeight)
        {
            return;
        }

        int sampleOffset = 0;
        int continentalSplineSampleOffset = CopyContinentalSplineSamples(shapeSettings, splineSamples, sampleOffset)
            ? sampleOffset
            : -1;
        if (continentalSplineSampleOffset >= 0)
        {
            sampleOffset += TerrainShapeSettingsSO.SplineSampleCount;
        }

        int mountainSplineSampleOffset = CopyMountainSplineSamples(shapeSettings, splineSamples, sampleOffset)
            ? sampleOffset
            : -1;
        int continentalSplineLayerIndex = shapeSettings.ContinentalSplineLayerIndex;
        int mountainSplineLayerIndex = shapeSettings.MountainSplineLayerIndex;
        int mountainSplineInputLayerIndex = ResolvePackedLayerIndex(shapeSettings.HeightLayers, shapeSettings.MountainSplineInputLayerIndex);
        IReadOnlyList<TerrainHeightLayerDefinition> layers = shapeSettings.HeightLayers;
        int writeIndex = 0;
        for (int i = 0; i < layers.Count && writeIndex < target.Length; i++)
        {
            TerrainHeightLayerDefinition layer = layers[i];
            if (!layer.HasContribution)
            {
                continue;
            }

            target[writeIndex] = new TerrainHeightNoiseLayerData
            {
                Operation = (int)layer.operation,
                NoiseShape = (int)layer.noiseShape,
                Frequency = layer.frequency,
                Amplitude = layer.amplitude,
                Octaves = layer.octaves,
                Lacunarity = layer.lacunarity,
                Persistence = layer.persistence,
                Offset = new float2(layer.offset.x, layer.offset.y),
                Threshold = layer.threshold,
                BlendRange = layer.blendRange,
                SplineSampleOffset = continentalSplineSampleOffset >= 0 && i == continentalSplineLayerIndex ? continentalSplineSampleOffset : -1,
                SplineSampleCount = continentalSplineSampleOffset >= 0 && i == continentalSplineLayerIndex ? TerrainShapeSettingsSO.SplineSampleCount : 0,
                SplineInfluence = continentalSplineSampleOffset >= 0 && i == continentalSplineLayerIndex
                    ? shapeSettings.ContinentalSplineInfluence
                    : 0f,
                MaskSplineSampleOffset = mountainSplineSampleOffset >= 0 && i == mountainSplineLayerIndex && mountainSplineInputLayerIndex >= 0 ? mountainSplineSampleOffset : -1,
                MaskSplineSampleCount = mountainSplineSampleOffset >= 0 && i == mountainSplineLayerIndex && mountainSplineInputLayerIndex >= 0 ? TerrainShapeSettingsSO.SplineSampleCount : 0,
                MaskSplineInputLayerIndex = mountainSplineSampleOffset >= 0 && i == mountainSplineLayerIndex ? mountainSplineInputLayerIndex : -1,
                MaskSplineInfluence = mountainSplineSampleOffset >= 0 && i == mountainSplineLayerIndex && mountainSplineInputLayerIndex >= 0
                    ? shapeSettings.MountainSplineInfluence
                    : 0f
            };
            writeIndex++;
        }
    }

    private static bool CopyContinentalSplineSamples(
        TerrainShapeSettingsSO shapeSettings,
        NativeArray<float> splineSamples,
        int sampleOffset)
    {
        if (!shapeSettings.UseContinentalSpline)
        {
            return false;
        }

        CopySplineSamples(splineSamples, sampleOffset, shapeSettings.EvaluateContinentalSpline);
        return true;
    }

    private static bool CopyMountainSplineSamples(
        TerrainShapeSettingsSO shapeSettings,
        NativeArray<float> splineSamples,
        int sampleOffset)
    {
        if (!shapeSettings.UseMountainSpline)
        {
            return false;
        }

        CopySplineSamples(splineSamples, sampleOffset, shapeSettings.EvaluateMountainSpline);
        return true;
    }

    private static void CopySplineSamples(
        NativeArray<float> splineSamples,
        int sampleOffset,
        System.Func<float, float> evaluate)
    {
        if (!splineSamples.IsCreated
            || sampleOffset < 0
            || sampleOffset + TerrainShapeSettingsSO.SplineSampleCount > splineSamples.Length)
        {
            return;
        }

        for (int i = 0; i < TerrainShapeSettingsSO.SplineSampleCount; i++)
        {
            float t = i / (float)(TerrainShapeSettingsSO.SplineSampleCount - 1);
            splineSamples[sampleOffset + i] = evaluate(t);
        }
    }

    private static int ResolvePackedLayerIndex(IReadOnlyList<TerrainHeightLayerDefinition> layers, int originalLayerIndex)
    {
        if (layers == null || originalLayerIndex < 0 || originalLayerIndex >= layers.Count)
        {
            return -1;
        }

        int packedIndex = 0;
        for (int i = 0; i < layers.Count; i++)
        {
            if (!layers[i].HasContribution)
            {
                continue;
            }

            if (i == originalLayerIndex)
            {
                return packedIndex;
            }

            packedIndex++;
        }

        return -1;
    }

    private int GetTerrainSeed()
    {
        return terrainShapeSettings != null
            ? terrainShapeSettings.TerrainSeed
            : TerrainShapeSettingsSO.DefaultTerrainSeed;
    }
}
