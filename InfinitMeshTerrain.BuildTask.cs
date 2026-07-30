using System;
using Unity.Collections;
using Unity.Jobs;
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
            int heightLayerCount,
            int heightSplineSampleCount,
            int grassInstanceCapacity,
            int grassTerrainLayerCount)
        {
            Coord = coord;
            Lod = lod;
            Stitching = stitching;
            SurfaceIndexCount = surfaceIndexCount;
            SkirtIndexCount = skirtIndexCount;
            BaseVertexCount = baseVertexCount;
            Resolution = Mathf.RoundToInt(Mathf.Sqrt(baseVertexCount));
            Vertices = new NativeArray<Vector3>(vertexCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            Normals = new NativeArray<Vector3>(vertexCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            Uvs = new NativeArray<Vector2>(vertexCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            Indices = new NativeArray<int>(indexCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            if (heightLayerCount > 0)
            {
                HeightLayers = new NativeArray<TerrainHeightNoiseLayerData>(heightLayerCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            }

            if (heightSplineSampleCount > 0)
            {
                HeightSplineSamples = new NativeArray<float>(heightSplineSampleCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            }

            if (grassInstanceCapacity > 0)
            {
                GrassInstances = new NativeArray<GrassInstanceData>(grassInstanceCapacity, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                GrassInstanceCounter = new NativeArray<int>(1, Allocator.Persistent);
                GrassBounds = new NativeArray<GrassChunkBounds>(1, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                GrassTerrainLayers = new NativeArray<GrassTerrainLayerData>(Mathf.Max(0, grassTerrainLayerCount), Allocator.Persistent);
            }
        }

        public Vector2Int Coord { get; }
        public int Lod { get; }
        public EdgeStitching Stitching { get; }
        public int SurfaceIndexCount { get; }
        public int SkirtIndexCount { get; }
        public int BaseVertexCount { get; }
        public int Resolution { get; }
        public JobHandle Handle;
        public NativeArray<Vector3> Vertices;
        public NativeArray<Vector3> Normals;
        public NativeArray<Vector2> Uvs;
        public NativeArray<int> Indices;
        public NativeArray<TerrainHeightNoiseLayerData> HeightLayers;
        public NativeArray<float> HeightSplineSamples;
        public NativeArray<GrassInstanceData> GrassInstances;
        public NativeArray<int> GrassInstanceCounter;
        public NativeArray<GrassChunkBounds> GrassBounds;
        public NativeArray<GrassTerrainLayerData> GrassTerrainLayers;
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

            if (Indices.IsCreated)
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
            bool useGpuGeneration)
        {
            Coord = coord;
            CellSize = cellSize;
            SurfaceResolution = surfaceResolution;
            SurfaceVertexCount = surfaceVertexCount;
            GrassInstanceCapacity = Mathf.Max(0, grassInstanceCapacity);
            UseGpuGeneration = useGpuGeneration;
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
        public JobHandle Handle;
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
