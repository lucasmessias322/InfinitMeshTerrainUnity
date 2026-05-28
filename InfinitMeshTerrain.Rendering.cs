using UnityEngine;
using UnityEngine.Rendering;

public partial class InfinitMeshTerrain
{
    private void ConfigureTerrainRenderer(MeshRenderer meshRenderer)
    {
        meshRenderer.sharedMaterial = chunkMaterial;
        meshRenderer.shadowCastingMode = ShadowCastingMode.On;
        meshRenderer.receiveShadows = true;
        meshRenderer.renderingLayerMask = 1u;
        meshRenderer.allowOcclusionWhenDynamic = true;
    }

    private void ApplyChunkMaterialToRuntimeChunks()
    {
        foreach (TerrainChunk chunk in chunks.Values)
        {
            chunk.SetMaterial(chunkMaterial);
            chunk.ApplyTerrainLayerProperties(terrainLayers);
        }
    }

    private void ValidateTerrainLayers()
    {
        if (terrainLayers == null)
        {
            return;
        }

        for (int i = 0; i < terrainLayers.Length; i++)
        {
            TerrainHeightLayer layer = terrainLayers[i];
            layer.blendRange = Mathf.Max(0f, layer.blendRange);
            layer.channel = (SplatChannel)Mathf.Clamp((int)layer.channel, 0, MaxTerrainLayerCount - 1);
            layer.color = NormalizeLayerColor(layer.color);
            terrainLayers[i] = layer;
        }
    }

    private static Color NormalizeLayerColor(Color color)
    {
        return color.a <= 0f && color.maxColorComponent <= 0f ? Color.white : color;
    }

    private SlopeTextureSettings CreateSlopeTextureSettings()
    {
        return new SlopeTextureSettings
        {
            Enabled = enableSlopeRock && slopeRockStrength > 0f,
            Channel = slopeRockChannel,
            StartAngle = slopeRockStartAngle,
            FullAngle = slopeRockFullAngle,
            Strength = slopeRockStrength
        };
    }
}
