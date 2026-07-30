using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

public partial class InfinitMeshTerrain
{
    private sealed partial class TerrainChunk : IDisposable
    {
        private readonly GameObject gameObject;
        private readonly MeshRenderer meshRenderer;
        private MeshCollider meshCollider;
        private readonly Mesh mesh;
        private MaterialPropertyBlock propertyBlock;
        private readonly Texture2D[] splatMaps = new Texture2D[SplatMapCount];
        private readonly Texture2D[] biomeLayerColorMaps = new Texture2D[MaxTerrainLayerCount];

        public TerrainChunk(
            Vector2Int coord,
            GameObject gameObject,
            MeshFilter meshFilter,
            MeshRenderer meshRenderer,
            MeshCollider meshCollider)
        {
            this.gameObject = gameObject;
            this.meshRenderer = meshRenderer;
            this.meshCollider = meshCollider;
            mesh = new Mesh
            {
                name = $"Terrain Chunk Mesh {coord.x}, {coord.y}",
                indexFormat = IndexFormat.UInt32
            };
            mesh.MarkDynamic();
            meshFilter.sharedMesh = mesh;
            Coord = coord;
        }

        public Vector2Int Coord { get; private set; }
        public int CurrentLod { get; private set; } = -1;
        public int DesiredLod { get; set; }
        public EdgeStitching CurrentStitching { get; private set; }
        public EdgeStitching DesiredStitching { get; set; }
        public bool HasMesh { get; private set; }

        public void PrepareForUse(Vector2Int coord, Transform parent, float chunkSize, Material material)
        {
            Coord = coord;
            gameObject.name = $"Terrain Chunk {coord.x}, {coord.y}";
            gameObject.transform.SetParent(parent, false);
            gameObject.transform.localPosition = new Vector3(coord.x * chunkSize, 0f, coord.y * chunkSize);
            mesh.name = $"Terrain Chunk Mesh {coord.x}, {coord.y}";
            SetMaterial(material);
            SetVisible(true);
        }

        public void Apply(
            TerrainBuildTask task,
            Material material,
            bool enableCollider,
            TerrainHeightLayer[] terrainLayers,
            SlopeTextureSettings slopeTextureSettings,
            TerrainBiomeLayerColorData[] biomeData,
            BiomeSamplingSettings biomeSettings,
            bool applyGrass,
            TreeSettingsSO treeSettings,
            IReadOnlyList<TreeRenderPrototype> treeRenderPrototypes,
            float treeTotalDensity,
            bool shouldBuildTrees,
            int terrainSeed,
            float chunkSize,
            bool enableWater,
            float waterHeight)
        {
            mesh.Clear();
            mesh.indexFormat = task.Vertices.Length > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.SetVertices(task.Vertices);
            mesh.SetNormals(task.Normals);
            mesh.SetUVs(0, task.Uvs);
            mesh.subMeshCount = 1;
            mesh.SetIndices(task.Indices, MeshTopology.Triangles, 0, true);
            mesh.RecalculateBounds();

            SetMaterial(material);
            RebuildSplatMap(task, terrainLayers, slopeTextureSettings, biomeData, biomeSettings, chunkSize);
            meshRenderer.shadowCastingMode = ShadowCastingMode.On;
            meshRenderer.receiveShadows = true;
            meshRenderer.renderingLayerMask = 1u;

            CurrentLod = task.Lod;
            CurrentStitching = task.Stitching;
            HasMesh = true;
            SetColliderEnabled(enableCollider, true);
            if (task.HasGrassInstances && applyGrass)
            {
                ApplyGrass(task);
            }

            ApplyTrees(task, treeSettings, treeRenderPrototypes, treeTotalDensity, shouldBuildTrees, terrainSeed, chunkSize, enableWater, waterHeight, terrainLayers, slopeTextureSettings);
        }

        public void SetMaterial(Material material)
        {
            meshRenderer.sharedMaterial = material;
            ApplyPropertyBlock();
        }

        public void ApplyTerrainLayerProperties(TerrainHeightLayer[] terrainLayers)
        {
            EnsurePropertyBlock();
            SetLayerProperties(terrainLayers);
            ApplyPropertyBlock();
        }

        public void SetVisible(bool visible)
        {
            if (gameObject.activeSelf != visible)
            {
                gameObject.SetActive(visible);
            }
        }

        public void SetColliderEnabled(bool enabled, bool forceMeshRefresh = false)
        {
            if (!enabled || !HasMesh)
            {
                DisableCollider();
                return;
            }

            if (meshCollider == null)
            {
                meshCollider = gameObject.AddComponent<MeshCollider>();
                meshCollider.enabled = false;
                forceMeshRefresh = true;
            }

            if (forceMeshRefresh || meshCollider.sharedMesh != mesh)
            {
                meshCollider.enabled = false;
                meshCollider.sharedMesh = null;
                meshCollider.sharedMesh = mesh;
            }

            if (!meshCollider.enabled)
            {
                meshCollider.enabled = true;
            }
        }

        public void DisableCollider()
        {
            if (meshCollider == null)
            {
                return;
            }

            if (meshCollider.enabled)
            {
                meshCollider.enabled = false;
            }

            if (meshCollider.sharedMesh != null)
            {
                meshCollider.sharedMesh = null;
            }
        }

        public void ReleaseForReuse()
        {
            DisableCollider();
            ClearGrass();
            ClearTrees();
            DestroyBiomeLayerColorMaps();
            mesh.Clear();
            mesh.indexFormat = IndexFormat.UInt32;
            CurrentLod = -1;
            DesiredLod = 0;
            CurrentStitching = default;
            DesiredStitching = default;
            HasMesh = false;
            if (propertyBlock != null)
            {
                propertyBlock.Clear();
                ApplyPropertyBlock();
            }

            SetVisible(false);
        }

        public void Dispose()
        {
            ClearGrass();
            ClearTrees();
            DestroySplatMaps();
            DestroyBiomeLayerColorMaps();

            if (Application.isPlaying)
            {
                Destroy(mesh);
                Destroy(gameObject);
            }
            else
            {
                DestroyImmediate(mesh);
                DestroyImmediate(gameObject);
            }
        }

        private void RebuildSplatMap(
            TerrainBuildTask task,
            TerrainHeightLayer[] terrainLayers,
            SlopeTextureSettings slopeTextureSettings,
            TerrainBiomeLayerColorData[] biomeData,
            BiomeSamplingSettings biomeSettings,
            float chunkSize)
        {
            int resolution = task.Resolution;
            if (resolution <= 0 || task.BaseVertexCount <= 0)
            {
                return;
            }

            for (int mapIndex = 0; mapIndex < splatMaps.Length; mapIndex++)
            {
                Texture2D splatMap = splatMaps[mapIndex];
                if (splatMap != null && splatMap.width == resolution && splatMap.height == resolution)
                {
                    splatMap.name = $"Terrain SplatMap {mapIndex} {Coord.x}, {Coord.y}";
                    continue;
                }

                DestroySplatMap(mapIndex);
                splatMaps[mapIndex] = new Texture2D(resolution, resolution, TextureFormat.RGBA32, false, true)
                {
                    name = $"Terrain SplatMap {mapIndex} {Coord.x}, {Coord.y}",
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp,
                    hideFlags = HideFlags.DontSave
                };
            }

            TerrainHeightLayer[] sortedLayers = CopySortedLayers(terrainLayers);
            Color32[] pixels0 = new Color32[resolution * resolution];
            Color32[] pixels1 = new Color32[pixels0.Length];
            Color[] baseLayerColors = CreateLayerBaseColors(sortedLayers);
            Color32[][] biomeLayerPixels = new Color32[MaxTerrainLayerCount][];
            bool hasBiomeLayerData = biomeData != null && biomeData.Length > 0 && biomeSettings.Count > 0;
            if (hasBiomeLayerData)
            {
                for (int channelIndex = 0; channelIndex < MaxTerrainLayerCount; channelIndex++)
                {
                    if (HasTerrainBiomeLayerColorOverride(biomeData, biomeSettings, channelIndex))
                    {
                        biomeLayerPixels[channelIndex] = new Color32[pixels0.Length];
                        EnsureBiomeLayerColorMap(channelIndex, resolution);
                    }
                    else
                    {
                        DestroyBiomeLayerColorMap(channelIndex);
                    }
                }
            }
            else
            {
                DestroyBiomeLayerColorMaps();
            }

            for (int i = 0; i < pixels0.Length; i++)
            {
                float height = task.Vertices[i].y;
                SplatWeights weights = EvaluateSplatWeights(height, sortedLayers);
                weights = ApplySlopeTexture(weights, task.Normals[i], slopeTextureSettings);
                pixels0[i] = ToColor32(weights.Map0);
                pixels1[i] = ToColor32(weights.Map1);

                if (hasBiomeLayerData)
                {
                    Vector3 vertex = task.Vertices[i];
                    float2 worldXZ = new float2(
                        Coord.x * chunkSize + vertex.x,
                        Coord.y * chunkSize + vertex.z);
                    for (int channelIndex = 0; channelIndex < biomeLayerPixels.Length; channelIndex++)
                    {
                        if (biomeLayerPixels[channelIndex] == null)
                        {
                            continue;
                        }

                        biomeLayerPixels[channelIndex][i] = ToColor32(EvaluateBiomeLayerColor(
                            worldXZ,
                            biomeData,
                            biomeSettings,
                            channelIndex,
                            baseLayerColors[channelIndex]));
                    }
                }
            }

            splatMaps[0].SetPixels32(pixels0);
            splatMaps[0].Apply(false, false);
            splatMaps[1].SetPixels32(pixels1);
            splatMaps[1].Apply(false, false);

            for (int channelIndex = 0; channelIndex < biomeLayerPixels.Length; channelIndex++)
            {
                if (biomeLayerPixels[channelIndex] == null || biomeLayerColorMaps[channelIndex] == null)
                {
                    continue;
                }

                biomeLayerColorMaps[channelIndex].SetPixels32(biomeLayerPixels[channelIndex]);
                biomeLayerColorMaps[channelIndex].Apply(false, false);
            }

            EnsurePropertyBlock();
            for (int mapIndex = 0; mapIndex < splatMaps.Length; mapIndex++)
            {
                propertyBlock.SetTexture(SplatMapPropertyIds[mapIndex], splatMaps[mapIndex]);
            }

            for (int channelIndex = 0; channelIndex < MaxTerrainLayerCount; channelIndex++)
            {
                bool hasLayerColorMap = biomeLayerPixels[channelIndex] != null && biomeLayerColorMaps[channelIndex] != null;
                propertyBlock.SetTexture(
                    BiomeLayerColorMapPropertyIds[channelIndex],
                    hasLayerColorMap ? biomeLayerColorMaps[channelIndex] : Texture2D.whiteTexture);
                propertyBlock.SetFloat(
                    UseBiomeLayerColorMapPropertyIds[channelIndex],
                    hasLayerColorMap ? 1f : 0f);
            }

            SetLayerProperties(sortedLayers);
            ApplyPropertyBlock();
        }

        private void SetLayerProperties(TerrainHeightLayer[] terrainLayers)
        {
            if (terrainLayers == null)
            {
                return;
            }

            for (int i = 0; i < terrainLayers.Length; i++)
            {
                TerrainHeightLayer layer = terrainLayers[i];
                int channelIndex = Mathf.Clamp((int)layer.channel, 0, LayerTexturePropertyIds.Length - 1);
                if (layer.texture != null)
                {
                    propertyBlock.SetTexture(LayerTexturePropertyIds[channelIndex], layer.texture);
                }

                Vector2 textureScale = NormalizeLayerTextureScale(layer.textureScale);
                propertyBlock.SetVector(LayerTextureScalePropertyIds[channelIndex], new Vector4(textureScale.x, textureScale.y, 0f, 0f));
                Texture normalTexture = layer.normalTexture != null ? layer.normalTexture : Texture2D.normalTexture;
                propertyBlock.SetTexture(LayerNormalTexturePropertyIds[channelIndex], normalTexture);
                propertyBlock.SetFloat(LayerNormalScalePropertyIds[channelIndex], NormalizeLayerNormalScale(layer.normalScale));
                propertyBlock.SetFloat(LayerSmoothnessPropertyIds[channelIndex], NormalizeLayerSmoothness(layer.smoothness));
                propertyBlock.SetColor(LayerColorPropertyIds[channelIndex], NormalizeLayerColor(layer.color));
            }
        }

        private void EnsurePropertyBlock()
        {
            propertyBlock ??= new MaterialPropertyBlock();
            meshRenderer.GetPropertyBlock(propertyBlock);
        }

        private void ApplyPropertyBlock()
        {
            if (propertyBlock != null)
            {
                meshRenderer.SetPropertyBlock(propertyBlock);
            }
        }

        private void DestroySplatMaps()
        {
            for (int mapIndex = 0; mapIndex < splatMaps.Length; mapIndex++)
            {
                DestroySplatMap(mapIndex);
            }
        }

        private void DestroySplatMap(int mapIndex)
        {
            Texture2D splatMap = splatMaps[mapIndex];
            if (splatMap == null)
            {
                return;
            }

            if (Application.isPlaying)
            {
                Destroy(splatMap);
            }
            else
            {
                DestroyImmediate(splatMap);
            }

            splatMaps[mapIndex] = null;
        }

        private void EnsureBiomeLayerColorMap(int channelIndex, int resolution)
        {
            if (channelIndex < 0 || channelIndex >= biomeLayerColorMaps.Length)
            {
                return;
            }

            Texture2D biomeLayerColorMap = biomeLayerColorMaps[channelIndex];
            if (biomeLayerColorMap != null
                && biomeLayerColorMap.width == resolution
                && biomeLayerColorMap.height == resolution)
            {
                biomeLayerColorMap.name = $"Biome Layer {channelIndex + 1} Color Map {Coord.x}, {Coord.y}";
                return;
            }

            DestroyBiomeLayerColorMap(channelIndex);
            biomeLayerColorMaps[channelIndex] = new Texture2D(resolution, resolution, TextureFormat.RGBA32, false, false)
            {
                name = $"Biome Layer {channelIndex + 1} Color Map {Coord.x}, {Coord.y}",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.DontSave
            };
        }

        private void DestroyBiomeLayerColorMaps()
        {
            for (int channelIndex = 0; channelIndex < biomeLayerColorMaps.Length; channelIndex++)
            {
                DestroyBiomeLayerColorMap(channelIndex);
            }
        }

        private void DestroyBiomeLayerColorMap(int channelIndex)
        {
            if (channelIndex < 0 || channelIndex >= biomeLayerColorMaps.Length)
            {
                return;
            }

            Texture2D biomeLayerColorMap = biomeLayerColorMaps[channelIndex];
            if (biomeLayerColorMap == null)
            {
                return;
            }

            if (Application.isPlaying)
            {
                Destroy(biomeLayerColorMap);
            }
            else
            {
                DestroyImmediate(biomeLayerColorMap);
            }

            biomeLayerColorMaps[channelIndex] = null;
        }

        private static TerrainHeightLayer[] CopySortedLayers(TerrainHeightLayer[] terrainLayers)
        {
            if (terrainLayers == null || terrainLayers.Length == 0)
            {
                return Array.Empty<TerrainHeightLayer>();
            }

            TerrainHeightLayer[] sortedLayers = new TerrainHeightLayer[terrainLayers.Length];
            Array.Copy(terrainLayers, sortedLayers, terrainLayers.Length);
            Array.Sort(sortedLayers, (a, b) => a.startHeight.CompareTo(b.startHeight));
            return sortedLayers;
        }

        private static Color[] CreateLayerBaseColors(TerrainHeightLayer[] terrainLayers)
        {
            Color[] layerColors = new Color[MaxTerrainLayerCount];
            for (int i = 0; i < layerColors.Length; i++)
            {
                layerColors[i] = Color.white;
            }

            if (terrainLayers == null)
            {
                return layerColors;
            }

            for (int i = 0; i < terrainLayers.Length; i++)
            {
                TerrainHeightLayer layer = terrainLayers[i];
                int channelIndex = Mathf.Clamp((int)layer.channel, 0, MaxTerrainLayerCount - 1);
                layerColors[channelIndex] = NormalizeLayerColor(layer.color);
            }

            return layerColors;
        }

        private struct SplatWeights
        {
            public Vector4 Map0;
            public Vector4 Map1;
        }

        private static SplatWeights EvaluateSplatWeights(float height, TerrainHeightLayer[] sortedLayers)
        {
            SplatWeights weights = default;

            if (sortedLayers.Length == 0)
            {
                weights.Map0.x = 1f;
                return weights;
            }

            AddWeight(ref weights, sortedLayers[0].channel, 1f);

            for (int i = 1; i < sortedLayers.Length; i++)
            {
                TerrainHeightLayer layer = sortedLayers[i];
                float blendRange = Mathf.Max(0.0001f, layer.blendRange);
                float blend = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(layer.startHeight, layer.startHeight + blendRange, height));

                weights.Map0 *= 1f - blend;
                weights.Map1 *= 1f - blend;
                AddWeight(ref weights, layer.channel, blend);
            }

            return NormalizeWeights(weights);
        }

        private static SplatWeights ApplySlopeTexture(
            SplatWeights weights,
            Vector3 normal,
            SlopeTextureSettings settings)
        {
            if (!settings.Enabled)
            {
                return weights;
            }

            float normalY = Mathf.Clamp(normal.y, -1f, 1f);
            float slopeAngle = Mathf.Acos(normalY) * Mathf.Rad2Deg;
            float slopeBlend = Mathf.SmoothStep(
                0f,
                1f,
                Mathf.InverseLerp(settings.StartAngle, settings.FullAngle, slopeAngle));
            float rockAmount = Mathf.Clamp01(slopeBlend * settings.Strength);

            if (rockAmount <= 0.0001f)
            {
                return weights;
            }

            weights.Map0 *= 1f - rockAmount;
            weights.Map1 *= 1f - rockAmount;
            AddWeight(ref weights, settings.Channel, rockAmount);
            return NormalizeWeights(weights);
        }

        private static SplatWeights NormalizeWeights(SplatWeights weights)
        {
            float sum = SumWeights(weights.Map0) + SumWeights(weights.Map1);
            if (sum > 0.0001f)
            {
                weights.Map0 /= sum;
                weights.Map1 /= sum;
            }

            return weights;
        }

        private static void AddWeight(ref SplatWeights weights, SplatChannel channel, float value)
        {
            switch (channel)
            {
                case SplatChannel.Map0G:
                    weights.Map0.y += value;
                    break;
                case SplatChannel.Map0B:
                    weights.Map0.z += value;
                    break;
                case SplatChannel.Map0A:
                    weights.Map0.w += value;
                    break;
                case SplatChannel.Map1R:
                    weights.Map1.x += value;
                    break;
                case SplatChannel.Map1G:
                    weights.Map1.y += value;
                    break;
                case SplatChannel.Map1B:
                    weights.Map1.z += value;
                    break;
                case SplatChannel.Map1A:
                    weights.Map1.w += value;
                    break;
                default:
                    weights.Map0.x += value;
                    break;
            }
        }

        private static Color32 ToColor32(Vector4 weights)
        {
            return new Color32(
                ToByte(weights.x),
                ToByte(weights.y),
                ToByte(weights.z),
                ToByte(weights.w));
        }

        private static Color32 ToColor32(float3 color)
        {
            return new Color32(
                ToByte(color.x),
                ToByte(color.y),
                ToByte(color.z),
                255);
        }

        private static float SumWeights(Vector4 weights)
        {
            return weights.x + weights.y + weights.z + weights.w;
        }

        private static byte ToByte(float value)
        {
            return (byte)Mathf.RoundToInt(Mathf.Clamp01(value) * 255f);
        }
    }
}
