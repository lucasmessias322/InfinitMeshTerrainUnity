using System.Collections.Generic;
using UnityEngine;

public partial class InfinitMeshTerrain
{
    private void RefreshVisibleChunks(Vector2Int viewerChunk)
    {
        visibleChunkCoords.Clear();
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

        foreach (ChunkCandidate candidate in candidateBuffer)
        {
            Vector2Int coord = candidate.Coord;
            visibleChunkCoords.Add(coord);

            int desiredLod = SelectLod(candidate.DistanceSqr);
            if (!chunks.TryGetValue(coord, out TerrainChunk chunk))
            {
                chunk = CreateChunk(coord);
                chunks.Add(coord, chunk);
            }

            chunk.SetVisible(true);
            chunk.DesiredLod = desiredLod;
        }

        foreach (Vector2Int coord in visibleChunkCoords)
        {
            TerrainChunk chunk = chunks[coord];
            chunk.DesiredStitching = CalculateDesiredStitching(coord, chunk.DesiredLod);

            if (!chunk.HasMesh || chunk.CurrentLod != chunk.DesiredLod || !chunk.CurrentStitching.Equals(chunk.DesiredStitching))
            {
                RequestBuild(coord);
            }
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

            chunk.Dispose();
            chunks.Remove(coord);
            queuedChunks.Remove(coord);
        }

        lastViewerChunk = viewerChunk;
        lastViewerUpdatePosition = viewer.position;
        hasBuiltInitialSet = true;
    }

    private TerrainChunk CreateChunk(Vector2Int coord)
    {
        GameObject chunkObject = new GameObject($"Terrain Chunk {coord.x}, {coord.y}");
        chunkObject.transform.SetParent(transform, false);
        chunkObject.transform.localPosition = new Vector3(coord.x * chunkSize, 0f, coord.y * chunkSize);

        MeshFilter meshFilter = chunkObject.AddComponent<MeshFilter>();
        MeshRenderer meshRenderer = chunkObject.AddComponent<MeshRenderer>();
        ConfigureTerrainRenderer(meshRenderer);

        MeshCollider meshCollider = null;
        if (useCollider)
        {
            meshCollider = chunkObject.AddComponent<MeshCollider>();
            meshCollider.enabled = false;
        }

        return new TerrainChunk(coord, chunkObject, meshFilter, meshRenderer, meshCollider);
    }
}
