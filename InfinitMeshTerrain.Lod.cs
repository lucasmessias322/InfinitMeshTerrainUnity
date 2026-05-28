using UnityEngine;

public partial class InfinitMeshTerrain
{
    private int CalculateSkirtSideMask(Vector2Int coord)
    {
        int mask = 0;

        if (!visibleChunkCoords.Contains(coord + Vector2Int.up))
        {
            mask |= SkirtNorth;
        }

        if (!visibleChunkCoords.Contains(coord + Vector2Int.right))
        {
            mask |= SkirtEast;
        }

        if (!visibleChunkCoords.Contains(coord + Vector2Int.down))
        {
            mask |= SkirtSouth;
        }

        if (!visibleChunkCoords.Contains(coord + Vector2Int.left))
        {
            mask |= SkirtWest;
        }

        return mask;
    }

    private static int CountSkirtSides(int mask)
    {
        int count = 0;
        count += (mask & SkirtNorth) != 0 ? 1 : 0;
        count += (mask & SkirtEast) != 0 ? 1 : 0;
        count += (mask & SkirtSouth) != 0 ? 1 : 0;
        count += (mask & SkirtWest) != 0 ? 1 : 0;
        return count;
    }

    private int SelectLod(int distanceSqr)
    {
        if (maxLod <= 0 || viewDistanceInChunks <= 1)
        {
            return 0;
        }

        float distance = Mathf.Sqrt(distanceSqr);
        float normalized = distance / viewDistanceInChunks;

        if (normalized < 0.24f)
        {
            return 0;
        }

        if (normalized < 0.48f)
        {
            return Mathf.Min(1, maxLod);
        }

        if (normalized < 0.72f)
        {
            return Mathf.Min(2, maxLod);
        }

        return Mathf.Min(3, maxLod);
    }

    private int GetLodStep(int lod)
    {
        int segmentCount = GetEffectiveSegmentCount();
        int clampedLod = Mathf.Clamp(lod, 0, maxLod);
        int step = clampedLod == 0
            ? 1
            : Mathf.Max(1, lod0VertexMultiplier) << clampedLod;

        while (step > 1 && segmentCount % step != 0)
        {
            step >>= 1;
        }

        return Mathf.Max(1, step);
    }

    private int GetEffectiveSegmentCount()
    {
        int baseSegmentCount = Mathf.Max(1, verticesPerLine - 1);
        return baseSegmentCount * Mathf.Max(1, lod0VertexMultiplier);
    }

    private EdgeStitching CalculateDesiredStitching(Vector2Int coord, int lod)
    {
        int step = GetLodStep(lod);

        return new EdgeStitching(
            GetNeighborStitchStep(coord + Vector2Int.up, step),
            GetNeighborStitchStep(coord + Vector2Int.right, step),
            GetNeighborStitchStep(coord + Vector2Int.down, step),
            GetNeighborStitchStep(coord + Vector2Int.left, step));
    }

    private int GetNeighborStitchStep(Vector2Int neighborCoord, int ownStep)
    {
        if (!chunks.TryGetValue(neighborCoord, out TerrainChunk neighbor) || !visibleChunkCoords.Contains(neighborCoord))
        {
            return ownStep;
        }

        int neighborStep = GetLodStep(neighbor.DesiredLod);
        return neighborStep > ownStep ? neighborStep : ownStep;
    }

    private Vector2Int WorldToChunkCoord(Vector3 worldPosition)
    {
        return new Vector2Int(
            Mathf.FloorToInt(worldPosition.x / chunkSize),
            Mathf.FloorToInt(worldPosition.z / chunkSize));
    }
}
