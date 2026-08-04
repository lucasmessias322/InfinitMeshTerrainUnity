using System;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

public partial class InfinitMeshTerrain
{
    private sealed class TerrainBuildTask : IDisposable
    {
        public TerrainBuildTask(
            Vector2Int coord,
            int lod,
            EdgeStitching stitching,
            int vertexCount,
            int indexCount,
            int surfaceIndexCount,
            int skirtIndexCount,
            int baseVertexCount,
            NativeArray<int> indices,
            bool ownsIndices,
            int heightLayerCount,
            int heightSplineSampleCount,
            int splatLayerCount,
            int biomeColorDataCount,
            int activeBiomeLayerColorCount,
            int activeBiomeLayerColorMask,
            int grassInstanceCapacity,
            int grassTerrainLayerCount)
        {
            Coord = coord;
            Lod = lod;
            Stitching = stitching;
            SurfaceIndexCount = surfaceIndexCount;
            SkirtIndexCount = skirtIndexCount;
            BaseVertexCount = baseVertexCount;
            OwnsIndices = ownsIndices;
            Resolution = Mathf.RoundToInt(Mathf.Sqrt(baseVertexCount));
            SplatMapPixelCount = baseVertexCount;
            ActiveBiomeLayerColorCount = activeBiomeLayerColorCount > 0 && biomeColorDataCount > 0
                ? activeBiomeLayerColorCount
                : 0;
            ActiveBiomeLayerColorMask = ActiveBiomeLayerColorCount > 0 ? activeBiomeLayerColorMask : 0;
            BiomeLayerColorResolution = Resolution;
            BiomeLayerColorPixelCount = ActiveBiomeLayerColorCount > 0 ? baseVertexCount : 0;
            Heights = new NativeArray<float>(GetHeightMapVertexCount(Resolution), Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            Vertices = new NativeArray<Vector3>(vertexCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            Normals = new NativeArray<Vector3>(vertexCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            Uvs = new NativeArray<Vector2>(vertexCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            Indices = indices.IsCreated
                ? indices
                : new NativeArray<int>(indexCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            OwnsIndices = OwnsIndices || !indices.IsCreated;
            if (heightLayerCount > 0)
            {
                HeightLayers = new NativeArray<TerrainHeightNoiseLayerData>(heightLayerCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            }

            if (heightSplineSampleCount > 0)
            {
                HeightSplineSamples = new NativeArray<float>(heightSplineSampleCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            }

            if (splatLayerCount > 0)
            {
                SplatLayers = new NativeArray<TerrainSplatLayerData>(splatLayerCount, Allocator.Persistent);
            }

            if (SplatMapPixelCount > 0)
            {
                SplatMap0Pixels = new NativeArray<Color32>(SplatMapPixelCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                SplatMap1Pixels = new NativeArray<Color32>(SplatMapPixelCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            }

            if (ActiveBiomeLayerColorCount > 0)
            {
                TerrainLayerBaseColors = new NativeArray<float4>(MaxTerrainLayerCount, Allocator.Persistent);
                TerrainBiomeLayerColorData = new NativeArray<TerrainBiomeLayerColorJobData>(
                    Mathf.Max(0, biomeColorDataCount),
                    Allocator.Persistent);
                ActiveBiomeLayerColorChannels = new NativeArray<int>(
                    ActiveBiomeLayerColorCount,
                    Allocator.Persistent);
                BiomeLayerColorPixels = new NativeArray<Color32>(
                    BiomeLayerColorPixelCount * ActiveBiomeLayerColorCount,
                    Allocator.Persistent,
                    NativeArrayOptions.UninitializedMemory);
            }

            if (grassInstanceCapacity > 0)
            {
                GrassInstances = new NativeArray<GrassInstanceData>(grassInstanceCapacity, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                GrassInstanceCounter = new NativeArray<int>(1, Allocator.Persistent);
                GrassBounds = new NativeArray<GrassChunkBounds>(1, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                GrassTerrainLayers = new NativeArray<GrassTerrainLayerData>(Mathf.Max(0, grassTerrainLayerCount), Allocator.Persistent);
            }

            GrassPackedWidthScale = GrassSettingsSO.DefaultMaxBladeWidth;
        }

        public Vector2Int Coord { get; }
        public int Lod { get; }
        public EdgeStitching Stitching { get; }
        public int SurfaceIndexCount { get; }
        public int SkirtIndexCount { get; }
        public int BaseVertexCount { get; }
        public bool OwnsIndices { get; private set; }
        public int Resolution { get; }
        public int SplatMapPixelCount { get; }
        public int ActiveBiomeLayerColorCount { get; }
        public int ActiveBiomeLayerColorMask { get; }
        public int BiomeLayerColorResolution { get; }
        public int BiomeLayerColorPixelCount { get; }
        public JobHandle Handle;
        public NativeArray<float> Heights;
        public NativeArray<Vector3> Vertices;
        public NativeArray<Vector3> Normals;
        public NativeArray<Vector2> Uvs;
        public NativeArray<int> Indices;
        public NativeArray<TerrainHeightNoiseLayerData> HeightLayers;
        public NativeArray<float> HeightSplineSamples;
        public NativeArray<TerrainSplatLayerData> SplatLayers;
        public NativeArray<float4> TerrainLayerBaseColors;
        public NativeArray<Color32> SplatMap0Pixels;
        public NativeArray<Color32> SplatMap1Pixels;
        public NativeArray<TerrainBiomeLayerColorJobData> TerrainBiomeLayerColorData;
        public NativeArray<int> ActiveBiomeLayerColorChannels;
        public NativeArray<Color32> BiomeLayerColorPixels;
        public NativeArray<GrassInstanceData> GrassInstances;
        public NativeArray<int> GrassInstanceCounter;
        public NativeArray<GrassChunkBounds> GrassBounds;
        public NativeArray<GrassTerrainLayerData> GrassTerrainLayers;
        public float GrassPackedWidthScale;
        public bool HasGrassInstances => GrassInstances.IsCreated
            && GrassInstanceCounter.IsCreated
            && GrassBounds.IsCreated
            && GrassInstances.Length > 0;

        public void Dispose()
        {
            if (Vertices.IsCreated)
            {
                Vertices.Dispose();
            }

            if (Normals.IsCreated)
            {
                Normals.Dispose();
            }

            if (Uvs.IsCreated)
            {
                Uvs.Dispose();
            }

            if (Heights.IsCreated)
            {
                Heights.Dispose();
            }

            if (OwnsIndices && Indices.IsCreated)
            {
                Indices.Dispose();
            }

            if (HeightLayers.IsCreated)
            {
                HeightLayers.Dispose();
            }

            if (HeightSplineSamples.IsCreated)
            {
                HeightSplineSamples.Dispose();
            }

            if (SplatLayers.IsCreated)
            {
                SplatLayers.Dispose();
            }

            if (TerrainLayerBaseColors.IsCreated)
            {
                TerrainLayerBaseColors.Dispose();
            }

            if (SplatMap0Pixels.IsCreated)
            {
                SplatMap0Pixels.Dispose();
            }

            if (SplatMap1Pixels.IsCreated)
            {
                SplatMap1Pixels.Dispose();
            }

            if (TerrainBiomeLayerColorData.IsCreated)
            {
                TerrainBiomeLayerColorData.Dispose();
            }

            if (ActiveBiomeLayerColorChannels.IsCreated)
            {
                ActiveBiomeLayerColorChannels.Dispose();
            }

            if (BiomeLayerColorPixels.IsCreated)
            {
                BiomeLayerColorPixels.Dispose();
            }

            if (GrassInstances.IsCreated)
            {
                GrassInstances.Dispose();
            }

            if (GrassInstanceCounter.IsCreated)
            {
                GrassInstanceCounter.Dispose();
            }

            if (GrassBounds.IsCreated)
            {
                GrassBounds.Dispose();
            }

            if (GrassTerrainLayers.IsCreated)
            {
                GrassTerrainLayers.Dispose();
            }
        }

    }

    private sealed class TerrainColliderBuildTask : IDisposable
    {
        public TerrainColliderBuildTask(
            Vector2Int coord,
            int lod,
            EdgeStitching stitching,
            int terrainVersion,
            int vertexCount,
            int indexCount,
            int baseVertexCount,
            NativeArray<int> indices,
            bool ownsIndices,
            int heightLayerCount,
            int heightSplineSampleCount)
        {
            Coord = coord;
            Lod = lod;
            Stitching = stitching;
            TerrainVersion = terrainVersion;
            BaseVertexCount = baseVertexCount;
            Resolution = Mathf.RoundToInt(Mathf.Sqrt(baseVertexCount));
            OwnsIndices = ownsIndices;
            Heights = new NativeArray<float>(GetHeightMapVertexCount(Resolution), Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            Vertices = new NativeArray<Vector3>(vertexCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            Indices = indices.IsCreated
                ? indices
                : new NativeArray<int>(indexCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            OwnsIndices = OwnsIndices || !indices.IsCreated;
            DummyNormals = new NativeArray<Vector3>(1, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            DummyUvs = new NativeArray<Vector2>(1, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);

            if (heightLayerCount > 0)
            {
                HeightLayers = new NativeArray<TerrainHeightNoiseLayerData>(heightLayerCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            }

            if (heightSplineSampleCount > 0)
            {
                HeightSplineSamples = new NativeArray<float>(heightSplineSampleCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            }
        }

        public Vector2Int Coord { get; }
        public int Lod { get; }
        public EdgeStitching Stitching { get; }
        public int TerrainVersion { get; }
        public int BaseVertexCount { get; }
        public int Resolution { get; }
        public bool OwnsIndices { get; private set; }
        public JobHandle Handle;
        public NativeArray<float> Heights;
        public NativeArray<Vector3> Vertices;
        public NativeArray<Vector3> DummyNormals;
        public NativeArray<Vector2> DummyUvs;
        public NativeArray<int> Indices;
        public NativeArray<TerrainHeightNoiseLayerData> HeightLayers;
        public NativeArray<float> HeightSplineSamples;

        public void Dispose()
        {
            if (Vertices.IsCreated)
            {
                Vertices.Dispose();
            }

            if (Heights.IsCreated)
            {
                Heights.Dispose();
            }

            if (OwnsIndices && Indices.IsCreated)
            {
                Indices.Dispose();
            }

            if (DummyNormals.IsCreated)
            {
                DummyNormals.Dispose();
            }

            if (DummyUvs.IsCreated)
            {
                DummyUvs.Dispose();
            }

            if (HeightLayers.IsCreated)
            {
                HeightLayers.Dispose();
            }

            if (HeightSplineSamples.IsCreated)
            {
                HeightSplineSamples.Dispose();
            }
        }
    }

    private sealed class GrassBuildTask : IDisposable
    {
        public GrassBuildTask(
            Vector2Int coord,
            float cellSize,
            int surfaceVertexCount,
            int surfaceResolution,
            int heightLayerCount,
            int heightSplineSampleCount,
            int grassInstanceCapacity,
            int grassTerrainLayerCount,
            int grassBiomeCount,
            bool useGpuGeneration,
            float grassPackedWidthScale)
        {
            Coord = coord;
            CellSize = cellSize;
            SurfaceResolution = surfaceResolution;
            SurfaceVertexCount = surfaceVertexCount;
            GrassInstanceCapacity = Mathf.Max(0, grassInstanceCapacity);
            UseGpuGeneration = useGpuGeneration;
            GrassPackedWidthScale = Mathf.Max(0.01f, grassPackedWidthScale);
            Heights = new NativeArray<float>(GetHeightMapVertexCount(SurfaceResolution), Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            Vertices = new NativeArray<Vector3>(surfaceVertexCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            Normals = new NativeArray<Vector3>(surfaceVertexCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            Uvs = new NativeArray<Vector2>(1, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);

            if (heightLayerCount > 0)
            {
                HeightLayers = new NativeArray<TerrainHeightNoiseLayerData>(heightLayerCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            }

            if (heightSplineSampleCount > 0)
            {
                HeightSplineSamples = new NativeArray<float>(heightSplineSampleCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            }

            if (grassTerrainLayerCount > 0)
            {
                GrassTerrainLayers = new NativeArray<GrassTerrainLayerData>(Mathf.Max(0, grassTerrainLayerCount), Allocator.Persistent);
            }

            if (grassBiomeCount > 0)
            {
                GrassBiomes = new NativeArray<GrassBiomeData>(Mathf.Max(0, grassBiomeCount), Allocator.Persistent);
            }

            if (!UseGpuGeneration && GrassInstanceCapacity > 0)
            {
                GrassInstances = new NativeArray<GrassInstanceData>(GrassInstanceCapacity, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                GrassInstanceCounter = new NativeArray<int>(1, Allocator.Persistent);
                GrassBounds = new NativeArray<GrassChunkBounds>(1, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            }
        }

        public Vector2Int Coord { get; }
        public float CellSize { get; }
        public int SurfaceResolution { get; }
        public int SurfaceVertexCount { get; }
        public int GrassInstanceCapacity { get; }
        public bool UseGpuGeneration { get; }
        public float GrassPackedWidthScale { get; }
        public JobHandle Handle;
        public NativeArray<float> Heights;
        public NativeArray<Vector3> Vertices;
        public NativeArray<Vector3> Normals;
        public NativeArray<Vector2> Uvs;
        public NativeArray<TerrainHeightNoiseLayerData> HeightLayers;
        public NativeArray<float> HeightSplineSamples;
        public NativeArray<GrassInstanceData> GrassInstances;
        public NativeArray<int> GrassInstanceCounter;
        public NativeArray<GrassChunkBounds> GrassBounds;
        public NativeArray<GrassTerrainLayerData> GrassTerrainLayers;
        public NativeArray<GrassBiomeData> GrassBiomes;
        public bool HasGrassInstances => GrassInstances.IsCreated
            && GrassInstanceCounter.IsCreated
            && GrassBounds.IsCreated
            && GrassInstances.Length > 0;

        public void Dispose()
        {
            if (Vertices.IsCreated)
            {
                Vertices.Dispose();
            }

            if (Normals.IsCreated)
            {
                Normals.Dispose();
            }

            if (Uvs.IsCreated)
            {
                Uvs.Dispose();
            }

            if (Heights.IsCreated)
            {
                Heights.Dispose();
            }

            if (HeightLayers.IsCreated)
            {
                HeightLayers.Dispose();
            }

            if (HeightSplineSamples.IsCreated)
            {
                HeightSplineSamples.Dispose();
            }

            if (GrassInstances.IsCreated)
            {
                GrassInstances.Dispose();
            }

            if (GrassInstanceCounter.IsCreated)
            {
                GrassInstanceCounter.Dispose();
            }

            if (GrassBounds.IsCreated)
            {
                GrassBounds.Dispose();
            }

            if (GrassTerrainLayers.IsCreated)
            {
                GrassTerrainLayers.Dispose();
            }

            if (GrassBiomes.IsCreated)
            {
                GrassBiomes.Dispose();
            }
        }
    }
}
