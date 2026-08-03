using System.Collections.Generic;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

public partial class InfinitMeshTerrain
{
    private bool ShouldUseFarChunkHlod()
    {
        return useFarChunkHlod
            && farHlodClusterSizeInChunks > 1
            && farHlodStartDistanceInChunks < viewDistanceInChunks
            && maxLod > 0;
    }

    private void RefreshVisibleFarHlodChunks(Vector2Int viewerChunk)
    {
        if (!ShouldUseFarChunkHlod())
        {
            ClearVisibleFarHlodChunks();
            return;
        }

        farHlodRemovalBuffer.Clear();
        foreach (KeyValuePair<Vector2Int, TerrainChunk> pair in farHlodChunks)
        {
            if (visibleFarHlodCoords.Contains(pair.Key))
            {
                continue;
            }

            pair.Value.SetVisible(false);
            farHlodRemovalBuffer.Add(pair.Key);
        }

        foreach (Vector2Int coord in farHlodRemovalBuffer)
        {
            if (farHlodChunks.TryGetValue(coord, out TerrainChunk chunk))
            {
                RecycleOrDisposeFarHlodChunk(coord, chunk);
            }
        }

        int lod = GetFarHlodLod();
        foreach (Vector2Int coord in visibleFarHlodCoords)
        {
            if (!farHlodChunks.TryGetValue(coord, out TerrainChunk chunk))
            {
                chunk = CreateFarHlodChunk(coord);
                farHlodChunks.Add(coord, chunk);
            }

            chunk.SetVisible(true);
            chunk.DesiredLod = lod;
            chunk.DesiredStitching = default;

            if (!chunk.HasMesh || chunk.CurrentLod != lod)
            {
                RequestFarHlodBuild(coord);
            }
        }

        PruneQueuedFarHlodBuildsToVisibleChunks();
    }

    private void ClearVisibleFarHlodChunks()
    {
        visibleFarHlodCoords.Clear();
        farHlodBuildQueue.Clear();
        queuedFarHlodChunks.Clear();

        farHlodRemovalBuffer.Clear();
        foreach (Vector2Int coord in farHlodChunks.Keys)
        {
            farHlodRemovalBuffer.Add(coord);
        }

        foreach (Vector2Int coord in farHlodRemovalBuffer)
        {
            if (farHlodChunks.TryGetValue(coord, out TerrainChunk chunk))
            {
                RecycleOrDisposeFarHlodChunk(coord, chunk);
            }
        }
    }

    private void RequestFarHlodBuild(Vector2Int coord)
    {
        if (!visibleFarHlodCoords.Contains(coord)
            || runningFarHlodTasks.ContainsKey(coord)
            || !queuedFarHlodChunks.Add(coord))
        {
            return;
        }

        farHlodBuildQueue.Enqueue(coord);
    }

    private void StartQueuedFarHlodBuilds()
    {
        int checkedQueuedBuilds = farHlodBuildQueue.Count;
        while (runningFarHlodTasks.Count < maxConcurrentFarHlodTasks
            && farHlodBuildQueue.Count > 0
            && checkedQueuedBuilds > 0)
        {
            checkedQueuedBuilds--;
            Vector2Int coord = farHlodBuildQueue.Dequeue();
            queuedFarHlodChunks.Remove(coord);

            if (!visibleFarHlodCoords.Contains(coord)
                || !farHlodChunks.TryGetValue(coord, out TerrainChunk chunk))
            {
                continue;
            }

            TerrainBuildTask task = ScheduleFarHlodBuild(coord, chunk.DesiredLod);
            runningFarHlodTasks.Add(coord, task);
        }
    }

    private TerrainBuildTask ScheduleFarHlodBuild(Vector2Int farCoord, int lod)
    {
        int clusterSizeInChunks = Mathf.Max(2, farHlodClusterSizeInChunks);
        int clampedLod = Mathf.Clamp(lod, 1, maxLod);
        int step = GetLodStep(clampedLod);
        int segmentCount = GetEffectiveSegmentCount() * clusterSizeInChunks;
        while (step > 1 && segmentCount % step != 0)
        {
            step >>= 1;
        }

        int resolution = segmentCount / Mathf.Max(1, step) + 1;
        int baseVertexCount = resolution * resolution;
        int baseQuadCount = (resolution - 1) * (resolution - 1);
        int indexCount = baseQuadCount * 6;
        TerrainIndexBuffer indexBuffer = GetTerrainIndexBuffer(
            clampedLod,
            default,
            resolution,
            baseQuadCount,
            baseVertexCount,
            baseQuadCount,
            0,
            indexCount);

        int heightLayerCount = GetTerrainHeightLayerCount();
        int heightSplineSampleCount = GetTerrainSplineSampleCount();
        int splatLayerCount = GetTerrainSplatLayerCount();
        TerrainBiomeLayerColorData[] biomeLayerColorData = CreateTerrainBiomeLayerColorDataArray();
        BiomeSamplingSettings biomeSettings = CreateBiomeSamplingSettings();
        int[] activeBiomeLayerColorChannels = new int[MaxTerrainLayerCount];
        int activeBiomeLayerColorCount = CollectActiveTerrainBiomeLayerColorChannels(
            biomeLayerColorData,
            biomeSettings,
            activeBiomeLayerColorChannels,
            out int activeBiomeLayerColorMask);
        int biomeColorDataCount = activeBiomeLayerColorCount > 0
            ? Mathf.Min(Mathf.Max(0, biomeSettings.Count), biomeLayerColorData.Length)
            : 0;
        Vector2Int originChunk = FarHlodCoordToChunkOrigin(farCoord);

        TerrainBuildTask task = new TerrainBuildTask(
            originChunk,
            clampedLod,
            default,
            baseVertexCount,
            indexCount,
            indexCount,
            0,
            baseVertexCount,
            indexBuffer.Indices,
            false,
            heightLayerCount,
            heightSplineSampleCount,
            splatLayerCount,
            biomeColorDataCount,
            activeBiomeLayerColorCount,
            activeBiomeLayerColorMask,
            0,
            0);
        CopyTerrainHeightLayers(task.HeightLayers, task.HeightSplineSamples);
        CopyTerrainSplatLayers(task.SplatLayers, task.TerrainLayerBaseColors);
        CopyTerrainBiomeLayerColorData(task.TerrainBiomeLayerColorData, biomeLayerColorData);
        CopyActiveTerrainBiomeLayerColorChannels(
            task.ActiveBiomeLayerColorChannels,
            activeBiomeLayerColorChannels,
            activeBiomeLayerColorCount);

        TerrainSettings settings = CreateTerrainSettings();
        SlopeTextureSettings slopeTextureSettings = CreateSlopeTextureSettings();
        float baseChunkSize = ChunkSize;
        float clusterWorldSize = baseChunkSize * clusterSizeInChunks;
        float2 chunkOrigin = new float2(originChunk.x * baseChunkSize, originChunk.y * baseChunkSize);
        int heightMapResolution = GetHeightMapResolution(resolution);

        GenerateTerrainHeightMapJob heightMapJob = new GenerateTerrainHeightMapJob
        {
            Heights = task.Heights,
            Settings = settings,
            HeightLayers = task.HeightLayers,
            HeightSplineSamples = task.HeightSplineSamples,
            HeightLayerCount = task.HeightLayers.IsCreated ? task.HeightLayers.Length : 0,
            ChunkOrigin = chunkOrigin,
            ChunkSize = clusterWorldSize,
            Resolution = resolution,
            HeightMapResolution = heightMapResolution,
            SegmentCount = segmentCount,
            LodStep = step,
            Stitching = default
        };

        JobHandle heightMapHandle = heightMapJob.ScheduleParallel(task.Heights.Length, 64, default);
        GenerateTerrainVerticesJob verticesJob = new GenerateTerrainVerticesJob
        {
            Heights = task.Heights,
            Vertices = task.Vertices,
            Normals = task.Normals,
            Uvs = task.Uvs,
            ChunkSize = clusterWorldSize,
            SkirtDepth = 0f,
            Resolution = resolution,
            HeightMapResolution = heightMapResolution,
            BaseVertexCount = baseVertexCount,
            WriteNormals = 1,
            WriteUvs = 1
        };

        JobHandle verticesHandle = verticesJob.ScheduleParallel(baseVertexCount, 64, heightMapHandle);
        BuildTerrainSplatMapsJob splatMapsJob = new BuildTerrainSplatMapsJob
        {
            Vertices = task.Vertices,
            Normals = task.Normals,
            SplatMap0Pixels = task.SplatMap0Pixels,
            SplatMap1Pixels = task.SplatMap1Pixels,
            BiomeLayerColorPixels = task.BiomeLayerColorPixels,
            SplatLayers = task.SplatLayers,
            TerrainLayerBaseColors = task.TerrainLayerBaseColors,
            TerrainBiomeLayerColorData = task.TerrainBiomeLayerColorData,
            ActiveBiomeLayerColorChannels = task.ActiveBiomeLayerColorChannels,
            SlopeTextureSettings = slopeTextureSettings,
            BiomeSettings = biomeSettings,
            ChunkOrigin = chunkOrigin,
            TerrainLayerCount = task.SplatLayers.IsCreated ? task.SplatLayers.Length : 0,
            BiomeDataCount = task.TerrainBiomeLayerColorData.IsCreated ? task.TerrainBiomeLayerColorData.Length : 0,
            ActiveBiomeLayerColorCount = task.ActiveBiomeLayerColorCount,
            BiomeLayerColorPixelCount = task.BiomeLayerColorPixelCount
        };

        JobHandle meshHandle = JobHandle.CombineDependencies(verticesHandle, indexBuffer.Handle);
        JobHandle splatHandle = splatMapsJob.ScheduleParallel(baseVertexCount, 64, verticesHandle);
        task.Handle = JobHandle.CombineDependencies(meshHandle, splatHandle);
        return task;
    }

    private void CompleteFinishedFarHlodTasks()
    {
        if (runningFarHlodTasks.Count == 0)
        {
            return;
        }

        completedFarHlodTaskBuffer.Clear();
        foreach (KeyValuePair<Vector2Int, TerrainBuildTask> pair in runningFarHlodTasks)
        {
            if (pair.Value.Handle.IsCompleted)
            {
                completedFarHlodTaskBuffer.Add(pair.Key);
            }
        }

        if (completedFarHlodTaskBuffer.Count == 0)
        {
            return;
        }

        completedTaskSortOrigin = viewer != null ? WorldToChunkCoord(viewer.position) : lastViewerChunk;
        completedFarHlodTaskBuffer.Sort(CompareFarHlodCompletedTaskDistance);

        int appliedCount = 0;
        foreach (Vector2Int coord in completedFarHlodTaskBuffer)
        {
            if (!runningFarHlodTasks.TryGetValue(coord, out TerrainBuildTask task))
            {
                continue;
            }

            bool canApply = CanApplyCompletedFarHlodTask(coord, task, out TerrainChunk chunk);
            if (canApply && appliedCount >= maxFarHlodAppliesPerFrame)
            {
                continue;
            }

            runningFarHlodTasks.Remove(coord);
            task.Handle.Complete();

            if (canApply)
            {
                ApplyCompletedFarHlodTask(coord, chunk, task);
                appliedCount++;
            }
            else if (visibleFarHlodCoords.Contains(coord) && farHlodChunks.ContainsKey(coord))
            {
                RequestFarHlodBuild(coord);
            }

            task.Dispose();
        }
    }

    private bool CanApplyCompletedFarHlodTask(Vector2Int coord, TerrainBuildTask task, out TerrainChunk chunk)
    {
        int lod = GetFarHlodLod();
        return farHlodChunks.TryGetValue(coord, out chunk)
            && visibleFarHlodCoords.Contains(coord)
            && task.Lod == lod;
    }

    private void ApplyCompletedFarHlodTask(Vector2Int coord, TerrainChunk chunk, TerrainBuildTask task)
    {
        float clusterWorldSize = ChunkSize * Mathf.Max(2, farHlodClusterSizeInChunks);
        chunk.Apply(
            task,
            chunkMaterial,
            false,
            terrainLayers,
            false,
            false,
            clusterWorldSize);
        chunk.SetShadowCastingMode(ShadowCastingMode.Off);
        HideIndividualChunksCoveredByFarHlod(coord);
    }

    private int CompareFarHlodCompletedTaskDistance(Vector2Int a, Vector2Int b)
    {
        Vector2Int originA = FarHlodCoordToChunkOrigin(a);
        Vector2Int originB = FarHlodCoordToChunkOrigin(b);
        int distanceComparison = GetChunkDistanceSqr(originA, completedTaskSortOrigin)
            .CompareTo(GetChunkDistanceSqr(originB, completedTaskSortOrigin));
        if (distanceComparison != 0)
        {
            return distanceComparison;
        }

        int xComparison = a.x.CompareTo(b.x);
        return xComparison != 0 ? xComparison : a.y.CompareTo(b.y);
    }

    private TerrainChunk CreateFarHlodChunk(Vector2Int farCoord)
    {
        Vector2Int originChunk = FarHlodCoordToChunkOrigin(farCoord);
        if (pooledFarHlodChunks.Count > 0)
        {
            TerrainChunk pooledChunk = pooledFarHlodChunks.Pop();
            pooledChunk.PrepareForUse(originChunk, transform, ChunkSize, chunkMaterial);
            return pooledChunk;
        }

        GameObject chunkObject = new GameObject($"Far Terrain Cluster {farCoord.x}, {farCoord.y}");
        chunkObject.transform.SetParent(transform, false);
        chunkObject.transform.localPosition = new Vector3(originChunk.x * ChunkSize, 0f, originChunk.y * ChunkSize);

        MeshFilter meshFilter = chunkObject.AddComponent<MeshFilter>();
        MeshRenderer meshRenderer = chunkObject.AddComponent<MeshRenderer>();
        ConfigureTerrainRenderer(meshRenderer);
        meshRenderer.shadowCastingMode = ShadowCastingMode.Off;

        return new TerrainChunk(this, originChunk, chunkObject, meshFilter, meshRenderer, null);
    }

    private void RecycleOrDisposeFarHlodChunk(Vector2Int coord, TerrainChunk chunk)
    {
        farHlodChunks.Remove(coord);
        queuedFarHlodChunks.Remove(coord);

        if (Application.isPlaying && pooledFarHlodChunks.Count < maxPooledFarHlodChunks)
        {
            chunk.ReleaseForReuse();
            pooledFarHlodChunks.Push(chunk);
            return;
        }

        chunk.Dispose();
    }

    private void HideIndividualChunksCoveredByFarHlod(Vector2Int farCoord)
    {
        Vector2Int origin = FarHlodCoordToChunkOrigin(farCoord);
        int clusterSize = Mathf.Max(2, farHlodClusterSizeInChunks);
        for (int z = 0; z < clusterSize; z++)
        {
            for (int x = 0; x < clusterSize; x++)
            {
                Vector2Int coord = origin + new Vector2Int(x, z);
                if (!visibleChunkCoords.Remove(coord))
                {
                    continue;
                }

                if (chunks.TryGetValue(coord, out TerrainChunk chunk))
                {
                    chunk.SetVisible(false);
                    chunk.DisableCollider();
                }
            }
        }

        PruneQueuedBuildsToVisibleChunks();
    }

    private void PruneQueuedFarHlodBuildsToVisibleChunks()
    {
        if (farHlodBuildQueue.Count == 0)
        {
            return;
        }

        int queuedCount = farHlodBuildQueue.Count;
        queuedFarHlodChunks.Clear();

        for (int i = 0; i < queuedCount; i++)
        {
            Vector2Int coord = farHlodBuildQueue.Dequeue();
            if (!visibleFarHlodCoords.Contains(coord)
                || runningFarHlodTasks.ContainsKey(coord)
                || !farHlodChunks.ContainsKey(coord)
                || !queuedFarHlodChunks.Add(coord))
            {
                continue;
            }

            farHlodBuildQueue.Enqueue(coord);
        }
    }

    private void CompleteAndDisposeAllFarHlodTasks()
    {
        foreach (KeyValuePair<Vector2Int, TerrainBuildTask> pair in runningFarHlodTasks)
        {
            pair.Value.Handle.Complete();
            pair.Value.Dispose();
        }

        runningFarHlodTasks.Clear();
        farHlodBuildQueue.Clear();
        queuedFarHlodChunks.Clear();
        completedFarHlodTaskBuffer.Clear();
    }

    private void ClearRuntimeFarHlodChunks()
    {
        CompleteAndDisposeAllFarHlodTasks();
        foreach (TerrainChunk chunk in farHlodChunks.Values)
        {
            chunk.Dispose();
        }

        farHlodChunks.Clear();
        visibleFarHlodCoords.Clear();
        DisposePooledFarHlodChunks();
    }

    private void DisposePooledFarHlodChunks()
    {
        while (pooledFarHlodChunks.Count > 0)
        {
            TerrainChunk chunk = pooledFarHlodChunks.Pop();
            chunk.Dispose();
        }
    }

    private void RequestVisibleFarHlodRebuilds()
    {
        foreach (Vector2Int coord in visibleFarHlodCoords)
        {
            RequestFarHlodBuild(coord);
        }
    }

    private void ApplyChunkMaterialToFarHlodChunks()
    {
        foreach (TerrainChunk chunk in farHlodChunks.Values)
        {
            chunk.SetMaterial(chunkMaterial);
            chunk.ApplyTerrainLayerProperties(terrainLayers);
        }
    }

    private bool IsChunkCoveredByReadyFarHlod(Vector2Int coord)
    {
        Vector2Int farCoord = ChunkToFarHlodCoord(coord);
        return visibleFarHlodCoords.Contains(farCoord)
            && farHlodChunks.TryGetValue(farCoord, out TerrainChunk chunk)
            && chunk.HasMesh;
    }

    private bool IsFarHlodClusterVisible(Vector2Int farCoord, Vector3 viewerPosition)
    {
        float distance = GetViewerDistanceToFarHlodBoundsInChunks(farCoord, viewerPosition);
        return distance >= farHlodStartDistanceInChunks
            && distance <= viewDistanceInChunks;
    }

    private float GetViewerDistanceToFarHlodBoundsInChunks(Vector2Int farCoord, Vector3 viewerPosition)
    {
        int clusterSize = Mathf.Max(2, farHlodClusterSizeInChunks);
        Vector2Int originChunk = FarHlodCoordToChunkOrigin(farCoord);
        float chunkSizeValue = ChunkSize;
        float minX = originChunk.x * chunkSizeValue;
        float maxX = minX + clusterSize * chunkSizeValue;
        float minZ = originChunk.y * chunkSizeValue;
        float maxZ = minZ + clusterSize * chunkSizeValue;
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

    private Vector2Int ChunkToFarHlodCoord(Vector2Int chunkCoord)
    {
        int clusterSize = Mathf.Max(2, farHlodClusterSizeInChunks);
        return new Vector2Int(
            FloorDiv(chunkCoord.x, clusterSize),
            FloorDiv(chunkCoord.y, clusterSize));
    }

    private Vector2Int FarHlodCoordToChunkOrigin(Vector2Int farCoord)
    {
        int clusterSize = Mathf.Max(2, farHlodClusterSizeInChunks);
        return new Vector2Int(farCoord.x * clusterSize, farCoord.y * clusterSize);
    }

    private int GetFarHlodLod()
    {
        return Mathf.Clamp(farHlodLod, 1, maxLod);
    }

    private static int FloorDiv(int value, int divisor)
    {
        int quotient = value / divisor;
        int remainder = value % divisor;
        if (remainder != 0 && ((remainder < 0) != (divisor < 0)))
        {
            quotient--;
        }

        return quotient;
    }
}
