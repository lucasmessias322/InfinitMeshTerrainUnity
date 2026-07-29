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
            NoiseFrequency = shapeSettings != null ? shapeSettings.NoiseFrequency : TerrainShapeSettingsSO.DefaultNoiseFrequency
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
}
