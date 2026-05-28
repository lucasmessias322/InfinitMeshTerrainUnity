using UnityEngine;

public partial class InfinitMeshTerrain
{
    private void EnsureWaterInstance()
    {
        if (!enableWater || waterObject == null || waterInstance != null)
        {
            return;
        }

        waterInstance = Instantiate(waterObject, transform);
        waterInstance.name = "Procedural Water";
        waterInstance.SetActive(true);
        UpdateWater();
    }

    private void UpdateWater()
    {
        if (!enableWater)
        {
            DestroyWaterInstance();
            return;
        }

        EnsureWaterInstance();

        if (waterInstance == null || viewer == null)
        {
            return;
        }

        Vector2Int viewerChunk = WorldToChunkCoord(viewer.position);
        float x = (viewerChunk.x + 0.5f) * chunkSize;
        float z = (viewerChunk.y + 0.5f) * chunkSize;
        float coverScale = Mathf.Max(1f, waterScale) * Mathf.Max(1, viewDistanceInChunks * 2 + 1);

        waterInstance.transform.position = new Vector3(x, waterHeight, z);
        waterInstance.transform.localScale = new Vector3(coverScale, 1f, coverScale);
    }

    private void DestroyWaterInstance()
    {
        if (waterInstance == null)
        {
            return;
        }

        if (Application.isPlaying)
        {
            Destroy(waterInstance);
        }
        else
        {
            DestroyImmediate(waterInstance);
        }

        waterInstance = null;
    }
}
