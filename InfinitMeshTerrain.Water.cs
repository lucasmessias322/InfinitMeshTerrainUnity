using UnityEngine;

public partial class InfinitMeshTerrain
{
    private void UpdateWater()
    {
        if (!enableWater || waterObject == null)
        {
            DestroyWaterInstance();
            return;
        }

        if (viewer == null)
        {
            return;
        }

        waterRemovalBuffer.Clear();
        foreach (var pair in waterInstances)
        {
            if (!visibleChunkCoords.Contains(pair.Key))
            {
                waterRemovalBuffer.Add(pair.Key);
            }
        }

        foreach (Vector2Int coord in waterRemovalBuffer)
        {
            if (waterInstances.TryGetValue(coord, out GameObject waterChunk))
            {
                DestroyWaterObject(waterChunk);
            }

            waterInstances.Remove(coord);
        }

        foreach (Vector2Int coord in visibleChunkCoords)
        {
            GameObject waterChunk = GetOrCreateWaterChunk(coord);
            if (waterChunk == null)
            {
                continue;
            }

            UpdateWaterChunkTransform(coord, waterChunk);
        }
    }

    private GameObject GetOrCreateWaterChunk(Vector2Int coord)
    {
        if (waterInstances.TryGetValue(coord, out GameObject waterChunk) && waterChunk != null)
        {
            return waterChunk;
        }

        waterChunk = Instantiate(waterObject, transform);
        waterChunk.name = $"Procedural Water {coord.x}, {coord.y}";
        waterChunk.SetActive(true);
        waterInstances[coord] = waterChunk;
        return waterChunk;
    }

    private void UpdateWaterChunkTransform(Vector2Int coord, GameObject waterChunk)
    {
        float x = (coord.x + 0.5f) * ChunkSize;
        float z = (coord.y + 0.5f) * ChunkSize;

        waterChunk.transform.position = new Vector3(x, waterHeight, z);
        waterChunk.transform.localScale = waterScale;
    }

    private void DestroyWaterInstance()
    {
        foreach (var pair in waterInstances)
        {
            DestroyWaterObject(pair.Value);
        }

        waterInstances.Clear();
        waterRemovalBuffer.Clear();
    }

    private void DestroyWaterObject(GameObject waterChunk)
    {
        if (waterChunk == null)
        {
            return;
        }

        if (Application.isPlaying)
        {
            Destroy(waterChunk);
        }
        else
        {
            DestroyImmediate(waterChunk);
        }
    }
}
