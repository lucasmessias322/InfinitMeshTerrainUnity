using System.Collections.Generic;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

public partial class InfinitMeshTerrain
{
    private int ClampLodForCollider(Vector2Int coord, Vector2Int viewerChunk, int desiredLod)
    {
        return desiredLod;
    }

    private bool ShouldUseColliderForChunk(Vector2Int coord, int lod)
    {
        return ShouldUseColliderForChunk(coord);
    }

    private bool ShouldUseColliderForChunk(Vector2Int coord)
    {
        if (!useCollider
            || viewer == null
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

    private void UpdateColliderPriorityMotion(Vector3 viewerPosition)
    {
        if (!hasLastViewerFramePosition)
        {
            lastViewerFramePosition = viewerPosition;
            hasLastViewerFramePosition = true;
            colliderMovementDirection = Vector2.zero;
            return;
        }

        Vector3 delta = viewerPosition - lastViewerFramePosition;
        lastViewerFramePosition = viewerPosition;

        Vector2 horizontalDelta = new Vector2(delta.x, delta.z);
        if (horizontalDelta.sqrMagnitude > 0.0001f)
        {
            colliderMovementDirection = horizontalDelta.normalized;
        }
    }

    private void QueueColliderUpdate(Vector2Int coord)
    {
        if (runningColliderTasks.ContainsKey(coord) || !queuedColliderChunks.Add(coord))
        {
            return;
        }

        colliderUpdateQueue.Add(coord);
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
        CompleteFinishedColliderTasks();

        if (!useCollider)
        {
            return;
        }

        int checkedQueuedUpdates = colliderUpdateQueue.Count;
        SortColliderUpdateQueue();
        while (runningColliderTasks.Count < maxConcurrentColliderMeshTasks
            && colliderUpdateQueue.Count > 0
            && checkedQueuedUpdates > 0)
        {
            checkedQueuedUpdates--;
            Vector2Int coord = colliderUpdateQueue[0];
            colliderUpdateQueue.RemoveAt(0);
            queuedColliderChunks.Remove(coord);

            if (!chunks.TryGetValue(coord, out TerrainChunk chunk) || !chunk.HasMesh)
            {
                continue;
            }

            if (!ShouldUseColliderForChunk(coord))
            {
                chunk.DisableCollider();
                continue;
            }

            int colliderLod = GetColliderMeshLod();
            EdgeStitching colliderStitching = CalculateColliderStitching(colliderLod);
            if (chunk.HasColliderMesh
                && chunk.CurrentColliderLod == colliderLod
                && chunk.CurrentColliderVersion == terrainColliderVersion
                && chunk.CurrentColliderStitching.Equals(colliderStitching))
            {
                chunk.SetColliderEnabled(true);
                continue;
            }

            runningColliderTasks.Add(coord, ScheduleColliderBuild(coord, colliderLod, colliderStitching));
        }
    }

    private void SortColliderUpdateQueue()
    {
        if (colliderUpdateQueue.Count > 1)
        {
            colliderUpdateQueue.Sort(CompareColliderUpdatePriority);
        }
    }

    private int CompareColliderUpdatePriority(Vector2Int a, Vector2Int b)
    {
        float scoreA = GetColliderUpdatePriorityScore(a);
        float scoreB = GetColliderUpdatePriorityScore(b);
        int scoreComparison = scoreA.CompareTo(scoreB);
        if (scoreComparison != 0)
        {
            return scoreComparison;
        }

        return GetChunkDistanceSqr(a, lastViewerChunk).CompareTo(GetChunkDistanceSqr(b, lastViewerChunk));
    }

    private float GetColliderUpdatePriorityScore(Vector2Int coord)
    {
        if (viewer == null)
        {
            return GetChunkDistanceSqr(coord, lastViewerChunk);
        }

        float chunkSizeValue = ChunkSize;
        Vector2 viewerPosition = new Vector2(viewer.position.x, viewer.position.z);
        Vector2 chunkCenter = new Vector2(
            (coord.x + 0.5f) * chunkSizeValue,
            (coord.y + 0.5f) * chunkSizeValue);
        Vector2 toChunk = chunkCenter - viewerPosition;
        float distanceSqr = toChunk.sqrMagnitude;

        if (colliderMovementPriority <= 0f || colliderMovementDirection.sqrMagnitude <= 0.0001f)
        {
            return distanceSqr;
        }

        float forwardDistance = Mathf.Max(0f, Vector2.Dot(toChunk, colliderMovementDirection));
        return distanceSqr - forwardDistance * chunkSizeValue * colliderMovementPriority;
    }

    private TerrainColliderBuildTask ScheduleColliderBuild(
        Vector2Int coord,
        int lod,
        EdgeStitching stitching)
    {
        int step = GetLodStep(lod);
        int segmentCount = GetEffectiveSegmentCount();
        int resolution = segmentCount / step + 1;
        int baseVertexCount = resolution * resolution;
        int baseQuadCount = (resolution - 1) * (resolution - 1);
        int indexCount = baseQuadCount * 6;
        TerrainIndexBuffer indexBuffer = GetTerrainIndexBuffer(
            lod,
            stitching,
            resolution,
            baseQuadCount,
            baseVertexCount,
            baseQuadCount,
            0,
            indexCount);
        TerrainColliderBuildTask task = new TerrainColliderBuildTask(
            coord,
            lod,
            stitching,
            terrainColliderVersion,
            baseVertexCount,
            indexCount,
            baseVertexCount,
            indexBuffer.Indices,
            false,
            GetTerrainHeightLayerCount(),
            GetTerrainSplineSampleCount());
        CopyTerrainHeightLayers(task.HeightLayers, task.HeightSplineSamples);

        float chunkSizeValue = ChunkSize;
        float2 chunkOrigin = new float2(coord.x * chunkSizeValue, coord.y * chunkSizeValue);
        GenerateTerrainVerticesJob verticesJob = new GenerateTerrainVerticesJob
        {
            Vertices = task.Vertices,
            Normals = task.DummyNormals,
            Uvs = task.DummyUvs,
            Settings = CreateTerrainSettings(),
            HeightLayers = task.HeightLayers,
            HeightSplineSamples = task.HeightSplineSamples,
            HeightLayerCount = task.HeightLayers.IsCreated ? task.HeightLayers.Length : 0,
            ChunkOrigin = chunkOrigin,
            ChunkSize = chunkSizeValue,
            SkirtDepth = 0f,
            Resolution = resolution,
            BaseVertexCount = baseVertexCount,
            SegmentCount = segmentCount,
            LodStep = step,
            WriteNormals = 0,
            WriteUvs = 0,
            Stitching = stitching
        };

        JobHandle verticesHandle = verticesJob.ScheduleParallel(baseVertexCount, 64, default);
        task.Handle = JobHandle.CombineDependencies(verticesHandle, indexBuffer.Handle);
        return task;
    }

    private void CompleteFinishedColliderTasks()
    {
        if (runningColliderTasks.Count == 0)
        {
            return;
        }

        completedColliderTaskBuffer.Clear();
        foreach (KeyValuePair<Vector2Int, TerrainColliderBuildTask> pair in runningColliderTasks)
        {
            if (pair.Value.Handle.IsCompleted)
            {
                completedColliderTaskBuffer.Add(pair.Key);
            }
        }

        if (completedColliderTaskBuffer.Count == 0)
        {
            return;
        }

        completedTaskSortOrigin = viewer != null ? WorldToChunkCoord(viewer.position) : lastViewerChunk;
        completedColliderTaskBuffer.Sort(CompareCompletedTaskDistance);

        int appliedCount = 0;
        foreach (Vector2Int coord in completedColliderTaskBuffer)
        {
            if (!runningColliderTasks.TryGetValue(coord, out TerrainColliderBuildTask task))
            {
                continue;
            }

            bool canApply = CanApplyCompletedColliderTask(coord, task, out TerrainChunk chunk);
            if (canApply && appliedCount >= maxColliderUpdatesPerFrame)
            {
                continue;
            }

            runningColliderTasks.Remove(coord);
            task.Handle.Complete();

            if (canApply)
            {
                chunk.ApplyCollider(task);
                appliedCount++;
            }
            else if (chunks.TryGetValue(coord, out TerrainChunk staleChunk))
            {
                staleChunk.DisableCollider();
            }

            task.Dispose();
        }
    }

    private bool CanApplyCompletedColliderTask(
        Vector2Int coord,
        TerrainColliderBuildTask task,
        out TerrainChunk chunk)
    {
        int colliderLod = GetColliderMeshLod();
        return chunks.TryGetValue(coord, out chunk)
            && chunk.HasMesh
            && task.Lod == colliderLod
            && task.TerrainVersion == terrainColliderVersion
            && task.Stitching.Equals(CalculateColliderStitching(colliderLod))
            && ShouldUseColliderForChunk(coord);
    }

    private int GetColliderMeshLod()
    {
        return Mathf.Clamp(colliderMeshLod, 0, maxLod);
    }

    private EdgeStitching CalculateColliderStitching(int lod)
    {
        int step = GetLodStep(lod);
        return new EdgeStitching(step, step, step, step);
    }

    private void ClearQueuedColliderUpdates()
    {
        colliderUpdateQueue.Clear();
        queuedColliderChunks.Clear();
    }

    private void CompleteAndDisposeAllColliderTasks()
    {
        foreach (KeyValuePair<Vector2Int, TerrainColliderBuildTask> pair in runningColliderTasks)
        {
            pair.Value.Handle.Complete();
            pair.Value.Dispose();
        }

        runningColliderTasks.Clear();
        ClearQueuedColliderUpdates();
    }

    private void DisableAllChunkColliders()
    {
        ClearQueuedColliderUpdates();
        CompleteAndDisposeAllColliderTasks();

        foreach (TerrainChunk chunk in chunks.Values)
        {
            chunk.DisableCollider();
        }
    }
}
