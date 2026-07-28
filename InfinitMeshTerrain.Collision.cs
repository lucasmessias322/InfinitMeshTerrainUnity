using UnityEngine;

public partial class InfinitMeshTerrain
{
    private int ClampLodForCollider(Vector2Int coord, Vector2Int viewerChunk, int desiredLod)
    {
        if (!useCollider || !IsChunkInsideColliderDistance(coord, viewerChunk))
        {
            return desiredLod;
        }

        return Mathf.Min(desiredLod, colliderMaxLod);
    }

    private bool ShouldUseColliderForChunk(Vector2Int coord, int lod)
    {
        if (!useCollider
            || viewer == null
            || lod < 0
            || lod > colliderMaxLod
            || !visibleChunkCoords.Contains(coord))
        {
            return false;
        }

        return IsChunkInsideColliderDistance(coord, WorldToChunkCoord(viewer.position));
    }

    private bool IsChunkInsideColliderDistance(Vector2Int coord, Vector2Int viewerChunk)
    {
        int radius = Mathf.Clamp(colliderDistanceInChunks, 0, viewDistanceInChunks);
        Vector2Int delta = coord - viewerChunk;
        return Mathf.Max(Mathf.Abs(delta.x), Mathf.Abs(delta.y)) <= radius;
    }

    private void QueueColliderUpdate(Vector2Int coord)
    {
        if (!queuedColliderChunks.Add(coord))
        {
            return;
        }

        colliderUpdateQueue.Enqueue(coord);
    }

    private void QueueVisibleColliderUpdates()
    {
        Vector2Int viewerChunk = viewer != null ? WorldToChunkCoord(viewer.position) : default;

        foreach (Vector2Int coord in visibleChunkCoords)
        {
            if (!chunks.TryGetValue(coord, out TerrainChunk chunk))
            {
                continue;
            }

            if (viewer == null || !useCollider || !IsChunkInsideColliderDistance(coord, viewerChunk))
            {
                chunk.DisableCollider();
                continue;
            }

            QueueColliderUpdate(coord);
        }
    }

    private void ProcessQueuedColliderUpdates()
    {
        int updateBudget = Mathf.Max(1, maxColliderUpdatesPerFrame);
        int updateCount = 0;

        while (updateCount < updateBudget && colliderUpdateQueue.Count > 0)
        {
            Vector2Int coord = colliderUpdateQueue.Dequeue();
            queuedColliderChunks.Remove(coord);

            if (!chunks.TryGetValue(coord, out TerrainChunk chunk) || !chunk.HasMesh)
            {
                continue;
            }

            chunk.SetColliderEnabled(ShouldUseColliderForChunk(coord, chunk.CurrentLod));
            updateCount++;
        }
    }

    private void ClearQueuedColliderUpdates()
    {
        colliderUpdateQueue.Clear();
        queuedColliderChunks.Clear();
    }

    private void DisableAllChunkColliders()
    {
        ClearQueuedColliderUpdates();

        foreach (TerrainChunk chunk in chunks.Values)
        {
            chunk.DisableCollider();
        }
    }
}
