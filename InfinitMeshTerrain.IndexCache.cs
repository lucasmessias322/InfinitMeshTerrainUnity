using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;

public partial class InfinitMeshTerrain
{
    private readonly Dictionary<TerrainIndexCacheKey, TerrainIndexBuffer> terrainIndexCache =
        new Dictionary<TerrainIndexCacheKey, TerrainIndexBuffer>();

    private TerrainIndexBuffer GetTerrainIndexBuffer(
        int lod,
        EdgeStitching stitching,
        int resolution,
        int baseQuadCount,
        int baseVertexCount,
        int totalQuadCount,
        int skirtSideMask,
        int indexCount)
    {
        TerrainIndexCacheKey key = new TerrainIndexCacheKey(lod, resolution, skirtSideMask, stitching);
        if (terrainIndexCache.TryGetValue(key, out TerrainIndexBuffer buffer))
        {
            return buffer;
        }

        buffer = new TerrainIndexBuffer(
            resolution,
            baseQuadCount,
            baseVertexCount,
            totalQuadCount,
            skirtSideMask,
            indexCount);
        terrainIndexCache.Add(key, buffer);
        return buffer;
    }

    private void DisposeTerrainIndexCache()
    {
        foreach (TerrainIndexBuffer buffer in terrainIndexCache.Values)
        {
            buffer.Dispose();
        }

        terrainIndexCache.Clear();
    }

    private readonly struct TerrainIndexCacheKey : IEquatable<TerrainIndexCacheKey>
    {
        public TerrainIndexCacheKey(int lod, int resolution, int skirtSideMask, EdgeStitching stitching)
        {
            Lod = lod;
            Resolution = resolution;
            SkirtSideMask = skirtSideMask;
            Stitching = stitching;
        }

        private int Lod { get; }
        private int Resolution { get; }
        private int SkirtSideMask { get; }
        private EdgeStitching Stitching { get; }

        public bool Equals(TerrainIndexCacheKey other)
        {
            return Lod == other.Lod
                && Resolution == other.Resolution
                && SkirtSideMask == other.SkirtSideMask
                && Stitching.Equals(other.Stitching);
        }

        public override bool Equals(object obj)
        {
            return obj is TerrainIndexCacheKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hashCode = Lod;
                hashCode = (hashCode * 397) ^ Resolution;
                hashCode = (hashCode * 397) ^ SkirtSideMask;
                hashCode = (hashCode * 397) ^ Stitching.GetHashCode();
                return hashCode;
            }
        }
    }

    private sealed class TerrainIndexBuffer : IDisposable
    {
        public TerrainIndexBuffer(
            int resolution,
            int baseQuadCount,
            int baseVertexCount,
            int totalQuadCount,
            int skirtSideMask,
            int indexCount)
        {
            Indices = new NativeArray<int>(indexCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            BuildTerrainIndicesJob indicesJob = new BuildTerrainIndicesJob
            {
                Indices = Indices,
                Resolution = resolution,
                BaseQuadCount = baseQuadCount,
                BaseVertexCount = baseVertexCount,
                TotalQuadCount = totalQuadCount,
                SkirtSideMask = skirtSideMask
            };
            Handle = indicesJob.Schedule();
        }

        public NativeArray<int> Indices { get; private set; }
        public JobHandle Handle { get; private set; }

        public void Dispose()
        {
            Handle.Complete();
            if (Indices.IsCreated)
            {
                Indices.Dispose();
            }
        }
    }
}
