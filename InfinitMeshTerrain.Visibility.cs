using System.Collections.Generic;
using UnityEngine;

public partial class InfinitMeshTerrain
{
    private void RefreshVisibleChunks(Vector2Int viewerChunk)
    {
        visibleChunkCoords.Clear();
        visibleFarHlodCoords.Clear();
        candidateBuffer.Clear();

        int radius = Mathf.Max(1, viewDistanceInChunks);
        for (int z = -radius; z <= radius; z++)
        {
            for (int x = -radius; x <= radius; x++)
            {
                Vector2Int offset = new Vector2Int(x, z);
                int distanceSqr = x * x + z * z;

                if (distanceSqr > radius * radius)
                {
                    continue;
                }

                candidateBuffer.Add(new ChunkCandidate(viewerChunk + offset, distanceSqr));
            }
        }

        candidateBuffer.Sort((a, b) => a.DistanceSqr.CompareTo(b.DistanceSqr));

        if (ShouldUseFarChunkHlod())
        {
            foreach (ChunkCandidate candidate in candidateBuffer)
            {
                Vector2Int farCoord = ChunkToFarHlodCoord(candidate.Coord);
                if (visibleFarHlodCoords.Contains(farCoord)
                    || !IsFarHlodClusterVisible(farCoord, viewer.position))
                {
                    continue;
                }

                visibleFarHlodCoords.Add(farCoord);
            }
        }

        foreach (ChunkCandidate candidate in candidateBuffer)
        {
            if (ShouldUseFarChunkHlod() && IsChunkCoveredByReadyFarHlod(candidate.Coord))
            {
                continue;
            }

            visibleChunkCoords.Add(candidate.Coord);
        }

        removalBuffer.Clear();
        int unloadRadius = viewDistanceInChunks + cachedChunkPadding;
        int unloadRadiusSqr = unloadRadius * unloadRadius;
        foreach (KeyValuePair<Vector2Int, TerrainChunk> pair in chunks)
        {
            if (visibleChunkCoords.Contains(pair.Key))
            {
                continue;
            }

            pair.Value.SetVisible(false);
            pair.Value.DisableCollider();

            Vector2Int delta = pair.Key - viewerChunk;
            bool outsideCache = delta.x * delta.x + delta.y * delta.y > unloadRadiusSqr;

            if (outsideCache && !runningTasks.ContainsKey(pair.Key))
            {
                removalBuffer.Add(pair.Key);
            }
        }

        foreach (Vector2Int coord in removalBuffer)
        {
            if (!chunks.TryGetValue(coord, out TerrainChunk chunk))
            {
                continue;
            }

            RecycleOrDisposeChunk(coord, chunk);
        }

        foreach (ChunkCandidate candidate in candidateBuffer)
        {
            Vector2Int coord = candidate.Coord;
            if (!visibleChunkCoords.Contains(coord))
            {
                continue;
            }

            if (!chunks.TryGetValue(coord, out TerrainChunk chunk))
            {
                chunk = CreateChunk(coord);
                chunks.Add(coord, chunk);
            }

            int desiredLod = ClampLodForCollider(coord, viewerChunk, SelectLod(coord, viewer.position, chunk));
            chunk.SetVisible(true);
            chunk.DesiredLod = desiredLod;
            if (useCollider && IsChunkInsideColliderDistance(coord, viewerChunk))
            {
                QueueColliderUpdate(coord);
            }
            else
            {
                chunk.DisableCollider();
            }
        }

        foreach (ChunkCandidate candidate in candidateBuffer)
        {
            Vector2Int coord = candidate.Coord;
            if (!visibleChunkCoords.Contains(coord))
            {
                continue;
            }

            TerrainChunk chunk = chunks[coord];
            chunk.DesiredStitching = CalculateDesiredStitching(coord, chunk.DesiredLod);

            if (!chunk.HasMesh || chunk.CurrentLod != chunk.DesiredLod || !chunk.CurrentStitching.Equals(chunk.DesiredStitching))
            {
                RequestBuild(coord);
            }
        }

        PruneQueuedBuildsToVisibleChunks();
        PruneQueuedTreeBuildsToVisibleChunks();
        RefreshVisibleFarHlodChunks(viewerChunk);

        lastViewerChunk = viewerChunk;
        lastViewerUpdatePosition = viewer.position;
        hasBuiltInitialSet = true;
        UpdateWater();
    }

    private TerrainChunk CreateChunk(Vector2Int coord)
    {
        if (pooledChunks.Count > 0)
        {
            TerrainChunk pooledChunk = pooledChunks.Pop();
            pooledChunk.PrepareForUse(coord, transform, ChunkSize, chunkMaterial);
            return pooledChunk;
        }

        GameObject chunkObject = new GameObject($"Terrain Chunk {coord.x}, {coord.y}");
        chunkObject.transform.SetParent(transform, false);
        chunkObject.transform.localPosition = new Vector3(coord.x * ChunkSize, 0f, coord.y * ChunkSize);

        MeshFilter meshFilter = chunkObject.AddComponent<MeshFilter>();
        MeshRenderer meshRenderer = chunkObject.AddComponent<MeshRenderer>();
        ConfigureTerrainRenderer(meshRenderer);

        return new TerrainChunk(this, coord, chunkObject, meshFilter, meshRenderer, null);
    }

    private void RecycleOrDisposeChunk(Vector2Int coord, TerrainChunk chunk)
    {
        ReleaseInteractiveTreesForChunk(coord, true);
        chunks.Remove(coord);
        queuedChunks.Remove(coord);
        queuedTreeBuildChunks.Remove(coord);
        queuedColliderChunks.Remove(coord);

        if (Application.isPlaying && pooledChunks.Count < maxPooledTerrainChunks)
        {
            chunk.ReleaseForReuse();
            pooledChunks.Push(chunk);
            return;
        }

        chunk.Dispose();
    }
}
