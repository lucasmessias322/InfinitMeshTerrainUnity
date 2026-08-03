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

    private int SelectLod(Vector2Int coord, Vector3 viewerPosition, TerrainChunk chunk)
    {
        if (maxLod <= 0)
        {
            return 0;
        }

        float distanceInChunks = GetViewerDistanceToChunkBoundsInChunks(coord, viewerPosition);
        int rawLod = SelectRawLod(distanceInChunks);
        int lod = chunk != null && chunk.HasMesh
            ? ApplyLodHysteresis(rawLod, distanceInChunks, chunk.DesiredLod)
            : rawLod;

        return ApplyLod0Deferral(lod, chunk);
    }

    private int SelectRawLod(float distanceInChunks)
    {
        for (int lod = 0; lod < maxLod; lod++)
        {
            if (distanceInChunks <= GetLodDistanceBandInChunks(lod))
            {
                return lod;
            }
        }

        return maxLod;
    }

    private int ApplyLodHysteresis(int rawLod, float distanceInChunks, int previousLod)
    {
        if (lodHysteresisInChunks <= 0f)
        {
            return rawLod;
        }

        int clampedPreviousLod = Mathf.Clamp(previousLod, 0, maxLod);
        if (rawLod == clampedPreviousLod)
        {
            return rawLod;
        }

        float hysteresis = Mathf.Max(0f, lodHysteresisInChunks);
        if (rawLod > clampedPreviousLod)
        {
            float leavePreviousDistance = GetLodUpperDistanceInChunks(clampedPreviousLod) + hysteresis;
            return distanceInChunks <= leavePreviousDistance ? clampedPreviousLod : rawLod;
        }

        float enterRawDistance = Mathf.Max(0f, GetLodUpperDistanceInChunks(rawLod) - hysteresis);
        return distanceInChunks > enterRawDistance ? clampedPreviousLod : rawLod;
    }

    private int ApplyLod0Deferral(int lod, TerrainChunk chunk)
    {
        if (lod != 0 || !ShouldDeferLod0Refinement())
        {
            return lod;
        }

        if (chunk != null && chunk.HasMesh && chunk.CurrentLod == 0)
        {
            return 0;
        }

        return Mathf.Min(1, maxLod);
    }

    private bool ShouldDeferLod0Refinement()
    {
        if (lod0MaxViewerSpeed <= 0f)
        {
            return false;
        }

        if (terrainViewerSpeed > lod0MaxViewerSpeed)
        {
            return true;
        }

        return Time.unscaledTime - lastFastLod0MoveTime < lod0SettleDelay;
    }

    private float GetLodUpperDistanceInChunks(int lod)
    {
        return lod >= maxLod ? float.PositiveInfinity : GetLodDistanceBandInChunks(lod);
    }

    private float GetViewerDistanceToChunkBoundsInChunks(Vector2Int coord, Vector3 viewerPosition)
    {
        float chunkSizeValue = ChunkSize;
        float minX = coord.x * chunkSizeValue;
        float maxX = minX + chunkSizeValue;
        float minZ = coord.y * chunkSizeValue;
        float maxZ = minZ + chunkSizeValue;
        float dx = viewerPosition.x < minX
            ? minX - viewerPosition.x
            : viewerPosition.x > maxX
                ? viewerPosition.x - maxX
                : 0f;
        float dz = viewerPosition.z < minZ
            ? minZ - viewerPosition.z
            : viewerPosition.z > maxZ
                ? viewerPosition.z - maxZ
                : 0f;

        return Mathf.Sqrt(dx * dx + dz * dz) / chunkSizeValue;
    }

    private float GetLodDistanceBandInChunks(int lod)
    {
        if (lodDistanceBandsInChunks == null
            || lod < 0
            || lod >= lodDistanceBandsInChunks.Length)
        {
            return GetDefaultLodDistanceBandInChunks(lod);
        }

        return Mathf.Max(0f, lodDistanceBandsInChunks[lod]);
    }

    private void ValidateLodDistanceBands()
    {
        if (lodDistanceBandsInChunks == null || lodDistanceBandsInChunks.Length != MaxSupportedLod)
        {
            float[] resizedBands = new float[MaxSupportedLod];
            for (int i = 0; i < resizedBands.Length; i++)
            {
                resizedBands[i] = lodDistanceBandsInChunks != null && i < lodDistanceBandsInChunks.Length
                    ? lodDistanceBandsInChunks[i]
                    : GetDefaultLodDistanceBandInChunks(i);
            }

            lodDistanceBandsInChunks = resizedBands;
        }

        float previousDistance = 0f;
        for (int i = 0; i < lodDistanceBandsInChunks.Length; i++)
        {
            float distance = lodDistanceBandsInChunks[i];
            if (float.IsNaN(distance) || float.IsInfinity(distance))
            {
                distance = GetDefaultLodDistanceBandInChunks(i);
            }

            distance = Mathf.Max(0f, distance);
            if (i > 0)
            {
                distance = Mathf.Max(previousDistance, distance);
            }

            lodDistanceBandsInChunks[i] = distance;
            previousDistance = distance;
        }
    }

    private static float GetDefaultLodDistanceBandInChunks(int lod)
    {
        switch (lod)
        {
            case 0:
                return 1.25f;
            case 1:
                return 3f;
            case 2:
                return 6f;
            case 3:
                return 10f;
            case 4:
                return 14f;
            default:
                return 14f;
        }
    }

    private int GetLodStep(int lod)
    {
        int segmentCount = GetEffectiveSegmentCount();
        int clampedLod = Mathf.Clamp(lod, 0, maxLod);
        int step = useGeomipmappingLod
            ? 1 << clampedLod
            : GetLegacyLodStep(clampedLod);

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

    private int GetLegacyLodStep(int clampedLod)
    {
        return clampedLod == 0
            ? 1
            : Mathf.Max(1, lod0VertexMultiplier) << clampedLod;
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
            Mathf.FloorToInt(worldPosition.x / ChunkSize),
            Mathf.FloorToInt(worldPosition.z / ChunkSize));
    }
}
