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
            chunk.ApplyTerrainLayerTextures(terrainLayers);
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
            terrainLayers[i] = layer;
        }
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
