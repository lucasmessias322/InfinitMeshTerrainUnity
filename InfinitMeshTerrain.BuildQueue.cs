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
        int grassInstanceCapacity = ShouldBuildGrassForChunk(coord) ? CalculateGrassInstanceCapacity() : 0;
        int grassTerrainLayerCount = grassInstanceCapacity > 0 ? GetGrassTerrainLayerCount() : 0;

        TerrainBuildTask task = new TerrainBuildTask(
            coord,
            lod,
            stitching,
            vertexCount,
            indexCount,
            surfaceIndexCount,
            skirtIndexCount,
            baseVertexCount,
            GetNoiseLayerCount(),
            GetTerrainSplineSampleCount(),
            grassInstanceCapacity,
            grassTerrainLayerCount);
        CopyNoiseLayers(task.NoiseLayers);
        CopyTerrainSplineSamples(task.TerrainSplineSamples);
        CopyGrassTerrainLayers(task.GrassTerrainLayers);

        TerrainSettings settings = CreateTerrainSettings();

        GenerateTerrainVerticesJob verticesJob = new GenerateTerrainVerticesJob
        {
            Vertices = task.Vertices,
            Normals = task.Normals,
            Uvs = task.Uvs,
            NoiseLayers = task.NoiseLayers,
            TerrainSplineSamples = task.TerrainSplineSamples,
            Settings = settings,
            ChunkOrigin = new float2(coord.x * chunkSize, coord.y * chunkSize),
            ChunkSize = chunkSize,
            HeightMultiplier = GetTerrainHeightMultiplier(),
            SkirtDepth = skirtDepth,
            Resolution = resolution,
            BaseVertexCount = baseVertexCount,
            SegmentCount = segmentCount,
            LodStep = step,
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
                SlopeTextureSettings = CreateSlopeTextureSettings(),
                ChunkCoord = new int2(coord.x, coord.y),
                ChunkOrigin = new float2(coord.x * chunkSize, coord.y * chunkSize),
                ChunkSize = chunkSize,
                Resolution = resolution,
                BaseVertexCount = baseVertexCount
            };

            grassHandle = grassJob.Schedule(verticesHandle);
        }

        task.Handle = JobHandle.CombineDependencies(verticesHandle, indicesHandle, grassHandle);
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

        foreach (Vector2Int coord in completedTaskBuffer)
        {
            TerrainBuildTask task = runningTasks[coord];
            runningTasks.Remove(coord);

            task.Handle.Complete();

            bool canApply = chunks.TryGetValue(coord, out TerrainChunk chunk)
                && visibleChunkCoords.Contains(coord)
                && chunk.DesiredLod == task.Lod
                && chunk.DesiredStitching.Equals(task.Stitching);

            if (canApply)
            {
                TreeSettingsSO currentTreeSettings = treeSettings;
                IReadOnlyList<TreeRenderPrototype> currentTreeRenderPrototypes = GetTreeRenderPrototypes(currentTreeSettings);
                chunk.Apply(
                    task,
                    chunkMaterial,
                    useCollider,
                    colliderMaxLod,
                    terrainLayers,
                    CreateSlopeTextureSettings(),
                    currentTreeSettings,
                    currentTreeRenderPrototypes,
                    GetTreeTotalDensity(),
                    ShouldBuildTreesForChunk(coord),
                    GetTerrainSeed(),
                    chunkSize,
                    enableWater,
                    waterHeight);
            }
            else if (chunks.ContainsKey(coord) && visibleChunkCoords.Contains(coord))
            {
                RequestBuild(coord);
            }

            task.Dispose();
        }
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
    }

    private void ClearRuntimeChunks()
    {
        foreach (TerrainChunk chunk in chunks.Values)
        {
            chunk.Dispose();
        }

        chunks.Clear();
        visibleChunkCoords.Clear();
    }

    private void RequestVisibleChunkRebuilds()
    {
        foreach (Vector2Int coord in visibleChunkCoords)
        {
            RequestBuild(coord);
        }
    }
}
