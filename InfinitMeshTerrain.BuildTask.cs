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
            int noiseLayerCount,
            int terrainSplineSampleCount)
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
            NoiseLayers = new NativeArray<NoiseLayerData>(Mathf.Max(0, noiseLayerCount), Allocator.Persistent);
            TerrainSplineSamples = new NativeArray<float>(Mathf.Max(0, terrainSplineSampleCount), Allocator.Persistent);
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
        public NativeArray<NoiseLayerData> NoiseLayers;
        public NativeArray<float> TerrainSplineSamples;

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

            if (NoiseLayers.IsCreated)
            {
                NoiseLayers.Dispose();
            }

            if (TerrainSplineSamples.IsCreated)
            {
                TerrainSplineSamples.Dispose();
            }
        }

    }
}
