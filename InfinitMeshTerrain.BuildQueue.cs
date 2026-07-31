using System.Collections.Generic;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

public partial class InfinitMeshTerrain
{
    private void RequestBuild(Vector2Int coord)
    {
        if (runningTasks.ContainsKey(coord) || queuedChunks.Contains(coord))
        {
            return;
        }

        buildQueue.Enqueue(coord);
        queuedChunks.Add(coord);
    }

    private void StartQueuedBuilds()
    {
        while (runningTasks.Count < maxConcurrentMeshTasks && buildQueue.Count > 0)
        {
            Vector2Int coord = buildQueue.Dequeue();
            queuedChunks.Remove(coord);

            if (!chunks.TryGetValue(coord, out TerrainChunk chunk) || !visibleChunkCoords.Contains(coord))
            {
                continue;
            }

            TerrainBuildTask task = ScheduleBuild(coord, chunk.DesiredLod, chunk.DesiredStitching);
            runningTasks.Add(coord, task);
        }
    }

    private void PruneQueuedBuildsToVisibleChunks()
    {
        if (buildQueue.Count == 0)
        {
            return;
        }

        int queuedCount = buildQueue.Count;
        queuedChunks.Clear();

        for (int i = 0; i < queuedCount; i++)
        {
            Vector2Int coord = buildQueue.Dequeue();
            if (!visibleChunkCoords.Contains(coord)
                || runningTasks.ContainsKey(coord)
                || !chunks.ContainsKey(coord)
                || !queuedChunks.Add(coord))
            {
                continue;
            }

            buildQueue.Enqueue(coord);
        }
    }

    private TerrainBuildTask ScheduleBuild(Vector2Int coord, int lod, EdgeStitching stitching)
    {
        int step = GetLodStep(lod);
        int segmentCount = GetEffectiveSegmentCount();
        int resolution = segmentCount / step + 1;
        int baseVertexCount = resolution * resolution;
        int skirtSideMask = skirtDepth > 0f ? CalculateSkirtSideMask(coord) : 0;
        int enabledSkirtSideCount = CountSkirtSides(skirtSideMask);
        int skirtVertexCount = enabledSkirtSideCount > 0 ? resolution * 4 : 0;
        int vertexCount = baseVertexCount + skirtVertexCount;
        int baseQuadCount = (resolution - 1) * (resolution - 1);
        int skirtQuadCount = (resolution - 1) * enabledSkirtSideCount;
        int surfaceIndexCount = baseQuadCount * 6;
        int skirtIndexCount = skirtQuadCount * 6;
        int indexCount = surfaceIndexCount + skirtIndexCount;
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
        int grassInstanceCapacity = 0;
        int grassTerrainLayerCount = 0;

        TerrainBuildTask task = new TerrainBuildTask(
            coord,
            lod,
            stitching,
            vertexCount,
            indexCount,
            surfaceIndexCount,
            skirtIndexCount,
            baseVertexCount,
            heightLayerCount,
            heightSplineSampleCount,
            splatLayerCount,
            biomeColorDataCount,
            activeBiomeLayerColorCount,
            activeBiomeLayerColorMask,
            grassInstanceCapacity,
            grassTerrainLayerCount);
        CopyTerrainHeightLayers(task.HeightLayers, task.HeightSplineSamples);
        CopyTerrainSplatLayers(task.SplatLayers, task.TerrainLayerBaseColors);
        CopyTerrainBiomeLayerColorData(task.TerrainBiomeLayerColorData, biomeLayerColorData);
        CopyActiveTerrainBiomeLayerColorChannels(
            task.ActiveBiomeLayerColorChannels,
            activeBiomeLayerColorChannels,
            activeBiomeLayerColorCount);
        CopyGrassTerrainLayers(task.GrassTerrainLayers);

        TerrainSettings settings = CreateTerrainSettings();
        SlopeTextureSettings slopeTextureSettings = CreateSlopeTextureSettings();
        float chunkSizeValue = ChunkSize;
        float2 chunkOrigin = new float2(coord.x * chunkSizeValue, coord.y * chunkSizeValue);

        GenerateTerrainVerticesJob verticesJob = new GenerateTerrainVerticesJob
        {
            Vertices = task.Vertices,
            Normals = task.Normals,
            Uvs = task.Uvs,
            Settings = settings,
            HeightLayers = task.HeightLayers,
            HeightSplineSamples = task.HeightSplineSamples,
            HeightLayerCount = task.HeightLayers.IsCreated ? task.HeightLayers.Length : 0,
            ChunkOrigin = chunkOrigin,
            ChunkSize = chunkSizeValue,
            SkirtDepth = skirtDepth,
            Resolution = resolution,
            BaseVertexCount = baseVertexCount,
            SegmentCount = segmentCount,
            LodStep = step,
            WriteUvs = 1,
            Stitching = stitching
        };

        BuildTerrainIndicesJob indicesJob = new BuildTerrainIndicesJob
        {
            Indices = task.Indices,
            Resolution = resolution,
            BaseQuadCount = baseQuadCount,
            BaseVertexCount = baseVertexCount,
            TotalQuadCount = baseQuadCount + skirtQuadCount,
            SkirtSideMask = skirtSideMask
        };

        JobHandle verticesHandle = verticesJob.ScheduleParallel(vertexCount, 64, default);
        JobHandle indicesHandle = indicesJob.Schedule();
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
        JobHandle splatMapsHandle = splatMapsJob.ScheduleParallel(baseVertexCount, 64, verticesHandle);
        JobHandle grassHandle = default;
        if (task.HasGrassInstances)
        {
            GenerateGrassInstancesJob grassJob = new GenerateGrassInstancesJob
            {
                Vertices = task.Vertices,
                Normals = task.Normals,
                TerrainLayers = task.GrassTerrainLayers,
                GrassInstances = task.GrassInstances,
                GrassInstanceCounter = task.GrassInstanceCounter,
                GrassBounds = task.GrassBounds,
                Settings = CreateGrassBuildSettings(),
                SlopeTextureSettings = slopeTextureSettings,
                ChunkCoord = new int2(coord.x, coord.y),
                ChunkOrigin = chunkOrigin,
                ChunkSize = chunkSizeValue,
                Resolution = resolution,
                BaseVertexCount = baseVertexCount
            };

            grassHandle = grassJob.Schedule(verticesHandle);
        }

        JobHandle meshHandle = JobHandle.CombineDependencies(verticesHandle, indicesHandle);
        JobHandle decorationHandle = JobHandle.CombineDependencies(splatMapsHandle, grassHandle);
        task.Handle = JobHandle.CombineDependencies(meshHandle, decorationHandle);
        return task;
    }

    private void CompleteFinishedTasks()
    {
        if (runningTasks.Count == 0)
        {
            return;
        }

        completedTaskBuffer.Clear();
        foreach (KeyValuePair<Vector2Int, TerrainBuildTask> pair in runningTasks)
        {
            if (pair.Value.Handle.IsCompleted)
            {
                completedTaskBuffer.Add(pair.Key);
            }
        }

        if (completedTaskBuffer.Count == 0)
        {
            return;
        }

        completedTaskSortOrigin = viewer != null ? WorldToChunkCoord(viewer.position) : lastViewerChunk;
        completedTaskBuffer.Sort(CompareCompletedTaskDistance);

        int appliedCount = 0;
        foreach (Vector2Int coord in completedTaskBuffer)
        {
            if (!runningTasks.TryGetValue(coord, out TerrainBuildTask task))
            {
                continue;
            }

            bool canApply = CanApplyCompletedTask(coord, task, out TerrainChunk chunk);
            if (canApply && appliedCount >= maxChunkAppliesPerFrame)
            {
                continue;
            }

            runningTasks.Remove(coord);
            task.Handle.Complete();

            if (canApply)
            {
                ApplyCompletedTask(coord, chunk, task);
                appliedCount++;
            }
            else if (chunks.ContainsKey(coord) && visibleChunkCoords.Contains(coord))
            {
                RequestBuild(coord);
            }

            task.Dispose();
        }
    }

    private int CompareCompletedTaskDistance(Vector2Int a, Vector2Int b)
    {
        int distanceComparison = GetChunkDistanceSqr(a, completedTaskSortOrigin)
            .CompareTo(GetChunkDistanceSqr(b, completedTaskSortOrigin));
        if (distanceComparison != 0)
        {
            return distanceComparison;
        }

        int xComparison = a.x.CompareTo(b.x);
        return xComparison != 0 ? xComparison : a.y.CompareTo(b.y);
    }

    private static int GetChunkDistanceSqr(Vector2Int coord, Vector2Int origin)
    {
        int dx = coord.x - origin.x;
        int dy = coord.y - origin.y;
        return dx * dx + dy * dy;
    }

    private bool CanApplyCompletedTask(Vector2Int coord, TerrainBuildTask task, out TerrainChunk chunk)
    {
        return chunks.TryGetValue(coord, out chunk)
            && visibleChunkCoords.Contains(coord)
            && chunk.DesiredLod == task.Lod
            && chunk.DesiredStitching.Equals(task.Stitching);
    }

    private void ApplyCompletedTask(Vector2Int coord, TerrainChunk chunk, TerrainBuildTask task)
    {
        TreeSettingsSO currentTreeSettings = treeSettings;
        IReadOnlyList<TreeRenderPrototype> currentTreeRenderPrototypes = GetTreeRenderPrototypes(currentTreeSettings);
        IReadOnlyList<TreeBiomeRenderData> currentTreeBiomeRenderData = GetTreeBiomeRenderData(currentTreeSettings);
        chunk.Apply(
            task,
            chunkMaterial,
            ShouldUseColliderForChunk(coord, task.Lod),
            terrainLayers,
            CreateSlopeTextureSettings(),
            CreateBiomeSamplingSettings(),
            !ShouldDeferGrassStreaming(),
            currentTreeSettings,
            currentTreeRenderPrototypes,
            currentTreeBiomeRenderData,
            GetGlobalTreeTotalDensity(),
            GetTreeMaxDensity(),
            HasBiomeSpecificTreeSpawns(currentTreeSettings),
            ShouldBuildTreesForChunk(coord),
            GetTerrainSeed(),
            ChunkSize,
            enableWater,
            waterHeight);
    }

    private void CompleteAndDisposeAllTasks()
    {
        foreach (KeyValuePair<Vector2Int, TerrainBuildTask> pair in runningTasks)
        {
            pair.Value.Handle.Complete();
            pair.Value.Dispose();
        }

        runningTasks.Clear();
        buildQueue.Clear();
        queuedChunks.Clear();
        ClearQueuedColliderUpdates();
        CompleteAndDisposeAllGrassTasks();
    }

    private void ClearRuntimeChunks()
    {
        ReleaseAllInteractiveTrees(true);
        ClearQueuedColliderUpdates();

        foreach (TerrainChunk chunk in chunks.Values)
        {
            chunk.Dispose();
        }

        chunks.Clear();
        visibleChunkCoords.Clear();
        DisposePooledChunks();
    }

    private void DisposePooledChunks()
    {
        while (pooledChunks.Count > 0)
        {
            TerrainChunk chunk = pooledChunks.Pop();
            chunk.Dispose();
        }
    }

    private void RequestVisibleChunkRebuilds()
    {
        foreach (Vector2Int coord in visibleChunkCoords)
        {
            RequestBuild(coord);
        }
    }
}
