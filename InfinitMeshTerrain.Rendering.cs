using System;
using Unity.Collections;
using Unity.Mathematics;
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

    private Bounds CreateTerrainLocalBounds(float chunkSizeValue, bool includeSkirt)
    {
        TerrainSettings settings = CreateTerrainSettings();
        float safeChunkSize = Mathf.Max(1f, chunkSizeValue);
        float minY = settings.MinHeight - (includeSkirt ? Mathf.Max(0f, skirtDepth) : 0f);
        float maxY = Mathf.Max(minY + 0.01f, settings.MaxHeight);
        float height = maxY - minY;
        const float verticalPadding = 2f;

        return new Bounds(
            new Vector3(safeChunkSize * 0.5f, (minY + maxY) * 0.5f, safeChunkSize * 0.5f),
            new Vector3(safeChunkSize, height + verticalPadding * 2f, safeChunkSize));
    }

    private void ApplyChunkMaterialToRuntimeChunks()
    {
        foreach (TerrainChunk chunk in chunks.Values)
        {
            chunk.SetMaterial(chunkMaterial);
            chunk.ApplyTerrainLayerProperties(terrainLayers);
        }

        ApplyChunkMaterialToFarHlodChunks();
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
            layer.textureScale = NormalizeLayerTextureScale(layer.textureScale);
            layer.normalScale = NormalizeLayerNormalScale(layer.normalScale);
            layer.smoothness = NormalizeLayerSmoothness(layer.smoothness);
            terrainLayers[i] = layer;
        }
    }

    private static Color NormalizeLayerColor(Color color)
    {
        return color.a <= 0f && color.maxColorComponent <= 0f ? Color.white : color;
    }

    private static Vector2 NormalizeLayerTextureScale(Vector2 textureScale)
    {
        if (textureScale == Vector2.zero)
        {
            return Vector2.one;
        }

        return new Vector2(
            Mathf.Max(0.001f, textureScale.x),
            Mathf.Max(0.001f, textureScale.y));
    }

    private static float NormalizeLayerNormalScale(float normalScale)
    {
        return Mathf.Clamp(normalScale, 0f, 4f);
    }

    private static float NormalizeLayerSmoothness(float smoothness)
    {
        return Mathf.Clamp01(smoothness);
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

    private int GetTerrainSplatLayerCount()
    {
        return terrainLayers != null ? Mathf.Min(terrainLayers.Length, MaxTerrainLayerCount) : 0;
    }

    private void CopyTerrainSplatLayers(
        NativeArray<TerrainSplatLayerData> destination,
        NativeArray<float4> baseLayerColors)
    {
        if (baseLayerColors.IsCreated)
        {
            for (int channelIndex = 0; channelIndex < baseLayerColors.Length; channelIndex++)
            {
                baseLayerColors[channelIndex] = new float4(1f, 1f, 1f, 1f);
            }
        }

        if (!destination.IsCreated || destination.Length == 0 || terrainLayers == null)
        {
            return;
        }

        TerrainSplatLayerData[] sortedLayers = new TerrainSplatLayerData[destination.Length];
        for (int i = 0; i < sortedLayers.Length; i++)
        {
            TerrainHeightLayer layer = terrainLayers[i];
            int channelIndex = Mathf.Clamp((int)layer.channel, 0, MaxTerrainLayerCount - 1);
            Color layerColor = NormalizeLayerColor(layer.color);
            sortedLayers[i] = new TerrainSplatLayerData
            {
                Channel = channelIndex,
                StartHeight = layer.startHeight,
                BlendRange = Mathf.Max(0.0001f, layer.blendRange)
            };

            if (baseLayerColors.IsCreated && channelIndex < baseLayerColors.Length)
            {
                baseLayerColors[channelIndex] = new float4(
                    Mathf.Max(0f, layerColor.r),
                    Mathf.Max(0f, layerColor.g),
                    Mathf.Max(0f, layerColor.b),
                    Mathf.Clamp01(layerColor.a));
            }
        }

        Array.Sort(sortedLayers, (a, b) => a.StartHeight.CompareTo(b.StartHeight));

        for (int i = 0; i < sortedLayers.Length; i++)
        {
            destination[i] = sortedLayers[i];
        }
    }
}
