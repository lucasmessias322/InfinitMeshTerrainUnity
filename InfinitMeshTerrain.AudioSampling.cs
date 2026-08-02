using Unity.Mathematics;
using UnityEngine;

public partial class InfinitMeshTerrain
{
    public float WaterHeight => waterHeight;
    public Vector3 WaterScale => waterScale;
    public bool IsWaterEnabled => enableWater && waterObject != null;

    public TerrainBiomeSO SampleTerrainBiome(Vector3 worldPosition)
    {
        return SampleTerrainBiome(new Vector2(worldPosition.x, worldPosition.z));
    }

    public TerrainBiomeSO SampleTerrainBiome(Vector2 worldXZ)
    {
        TrySampleTerrainBiome(worldXZ, out TerrainBiomeSO biome);
        return biome;
    }

    public bool TrySampleTerrainBiome(Vector3 worldPosition, out TerrainBiomeSO biome)
    {
        return TrySampleTerrainBiome(new Vector2(worldPosition.x, worldPosition.z), out biome);
    }

    public bool TrySampleTerrainBiome(Vector2 worldXZ, out TerrainBiomeSO biome)
    {
        biome = null;

        GrassBiomeData[] biomeData = CreateBiomeDataArray();
        BiomeSamplingSettings biomeSettings = CreateBiomeSamplingSettings();
        int biomeDataCount = Mathf.Min(Mathf.Max(0, biomeSettings.Count), biomeData.Length);
        if (biomeDataCount <= 0)
        {
            return false;
        }

        float2 samplePosition = new float2(worldXZ.x, worldXZ.y);
        float biomeDistance = EvaluateBiomeDistance(samplePosition, biomeSettings);
        int sortedBiomeIndex = ResolveBiomeIndex(
            samplePosition,
            biomeDistance,
            biomeData,
            biomeSettings,
            biomeDataCount);
        if (sortedBiomeIndex < 0 || sortedBiomeIndex >= biomeDataCount)
        {
            return false;
        }

        int packedBiomeIndex = Mathf.RoundToInt(biomeData[sortedBiomeIndex].DistanceRange.w);
        biome = GetTerrainBiomeByPackedIndex(packedBiomeIndex);
        return biome != null;
    }

    private TerrainBiomeSO GetTerrainBiomeByPackedIndex(int packedBiomeIndex)
    {
        if (biomes == null || packedBiomeIndex < 0)
        {
            return null;
        }

        int scannedCount = 0;
        for (int i = 0; i < biomes.Length && scannedCount < MaxTerrainBiomeCount; i++)
        {
            TerrainBiomeSO biome = biomes[i];
            if (biome == null)
            {
                continue;
            }

            if (scannedCount == packedBiomeIndex)
            {
                return biome;
            }

            scannedCount++;
        }

        return null;
    }
}
