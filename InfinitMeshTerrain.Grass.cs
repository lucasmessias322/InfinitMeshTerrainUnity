using System;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

public partial class InfinitMeshTerrain
{
    private const string GrassShaderName = "Shader Graphs/GrassShaderGraph";
    private const int GrassInstanceStride = 48;
    private const int MaxGrassSurfaceResolution = 257;
    private static readonly int GrassInstancesPropertyId = Shader.PropertyToID("_GrassInstances");
    private static readonly int GrassViewerPositionPropertyId = Shader.PropertyToID("_ViewerPosition");
    private static readonly int GrassFadeDistancesPropertyId = Shader.PropertyToID("_FadeDistances");
    private static readonly int GrassWindPropertyId = Shader.PropertyToID("_Wind");
    private static readonly int GrassMeshGroundingPropertyId = Shader.PropertyToID("_MeshGrounding");

    [Header("Detail Grass")]
    [SerializeField] private GrassSettingsSO grassSettings;
    [Tooltip("Grass buffer uploads are skipped while the viewer is faster than this. Use 0 to never defer grass uploads.")]
    [SerializeField, Min(0f)] private float grassUploadMaxViewerSpeed = 192f;
    [Tooltip("Extra time to wait after fast movement before scheduling grass rebuilds again.")]
    [SerializeField, Min(0f)] private float grassUploadSettleDelay = 0.2f;
    [Tooltip("Maximum grass instances copied to GPU buffers per frame. Lower values reduce upload spikes.")]
    [SerializeField, Min(1)] private int maxGrassUploadInstancesPerFrame = 8192;
    [SerializeField, Min(1)] private int maxGrassBuildRequestsPerFrame = 2;
    [SerializeField, Min(1)] private int maxConcurrentGrassBuildTasks = 2;

    private Mesh runtimeGrassMesh;
    private Material runtimeGrassMaterial;
    private GrassSettingsSO runtimeDefaultGrassSettings;
    private readonly Dictionary<Vector2Int, GrassCell> grassCells = new Dictionary<Vector2Int, GrassCell>();
    private readonly Dictionary<Vector2Int, GrassBuildTask> runningGrassTasks = new Dictionary<Vector2Int, GrassBuildTask>();
    private readonly Queue<Vector2Int> grassBuildQueue = new Queue<Vector2Int>();
    private readonly HashSet<Vector2Int> queuedGrassCells = new HashSet<Vector2Int>();
    private readonly HashSet<Vector2Int> visibleGrassCellCoords = new HashSet<Vector2Int>();
    private readonly List<Vector2Int> grassRemovalBuffer = new List<Vector2Int>();
    private readonly List<Vector2Int> completedGrassTaskBuffer = new List<Vector2Int>();
    private readonly List<ChunkCandidate> grassVisibilityCandidateBuffer = new List<ChunkCandidate>();
    private readonly List<ChunkCandidate> grassBuildCandidateBuffer = new List<ChunkCandidate>();
    private readonly List<ChunkCandidate> grassUploadCandidateBuffer = new List<ChunkCandidate>();
    private Vector3 lastGrassViewerPosition;
    private Vector2Int completedGrassTaskSortOrigin;
    private float grassViewerSpeed;
    private float lastFastGrassMoveTime = float.NegativeInfinity;
    private bool hasGrassViewerMotionSample;

    private void ValidateGrassSettings()
    {
        grassUploadMaxViewerSpeed = Mathf.Max(0f, grassUploadMaxViewerSpeed);
        grassUploadSettleDelay = Mathf.Max(0f, grassUploadSettleDelay);
        maxGrassUploadInstancesPerFrame = Mathf.Max(1, maxGrassUploadInstancesPerFrame);
        maxGrassBuildRequestsPerFrame = Mathf.Max(1, maxGrassBuildRequestsPerFrame);
        maxConcurrentGrassBuildTasks = Mathf.Max(1, maxConcurrentGrassBuildTasks);

        if (grassSettings != null)
        {
            grassSettings.ValidateValues();
        }
    }

    private void UpdateGrassStreamingMotion()
    {
        if (viewer == null)
        {
            grassViewerSpeed = 0f;
            hasGrassViewerMotionSample = false;
            return;
        }

        Vector3 viewerPosition = viewer.position;
        if (!hasGrassViewerMotionSample)
        {
            lastGrassViewerPosition = viewerPosition;
            grassViewerSpeed = 0f;
            hasGrassViewerMotionSample = true;
            return;
        }

        float deltaTime = Mathf.Max(Time.unscaledDeltaTime, 0.0001f);
        grassViewerSpeed = (viewerPosition - lastGrassViewerPosition).magnitude / deltaTime;
        lastGrassViewerPosition = viewerPosition;

        if (grassUploadMaxViewerSpeed > 0f && grassViewerSpeed > grassUploadMaxViewerSpeed)
        {
            lastFastGrassMoveTime = Time.unscaledTime;
        }
    }

    private bool ShouldDeferGrassStreaming()
    {
        if (grassUploadMaxViewerSpeed <= 0f)
        {
            return false;
        }

        if (grassViewerSpeed > grassUploadMaxViewerSpeed)
        {
            return true;
        }

        return Time.unscaledTime - lastFastGrassMoveTime < grassUploadSettleDelay;
    }

    private void ProcessQueuedGrassUploads()
    {
        if (viewer == null || ShouldDeferGrassStreaming())
        {
            return;
        }

        grassUploadCandidateBuffer.Clear();
        float grassCellSize = GetGrassStreamingCellSize(GetGrassSettings());
        Vector2Int viewerCell = WorldToGrassCellCoord(viewer.position, grassCellSize);

        foreach (Vector2Int coord in visibleGrassCellCoords)
        {
            if (grassCells.TryGetValue(coord, out GrassCell cell) && cell.HasPendingGrassUpload)
            {
                grassUploadCandidateBuffer.Add(new ChunkCandidate(coord, GetGrassCellDistanceSqr(coord, viewerCell)));
            }
        }

        if (grassUploadCandidateBuffer.Count == 0)
        {
            return;
        }

        grassUploadCandidateBuffer.Sort((a, b) => a.DistanceSqr.CompareTo(b.DistanceSqr));
        int remainingUploadBudget = maxGrassUploadInstancesPerFrame;
        for (int i = 0; i < grassUploadCandidateBuffer.Count && remainingUploadBudget > 0; i++)
        {
            Vector2Int coord = grassUploadCandidateBuffer[i].Coord;
            if (!grassCells.TryGetValue(coord, out GrassCell cell))
            {
                continue;
            }

            remainingUploadBudget -= cell.UploadPendingGrass(remainingUploadBudget);
        }
    }

    private void UpdateGrassDetails()
    {
        GrassSettingsSO settings = GetGrassSettings();
        if (settings == null
            || !settings.EnableGrass
            || viewer == null
            || settings.DensityPerSquareMeter <= 0f
            || settings.DetailDistance <= 0f
            || settings.MaxInstancesPerGrassCell <= 0)
        {
            ClearGrassRuntimeCells();
            ClearGrassFromRuntimeChunks();
            return;
        }

        RefreshVisibleGrassCells(settings);

        Mesh drawMesh = GetGrassRenderMesh(settings);
        Material drawMaterial = GetGrassRenderMaterial(settings);
        if (drawMesh == null || drawMaterial == null)
        {
            return;
        }

        drawMaterial.enableInstancing = true;

        Vector2 windDirection = settings.WindDirection.sqrMagnitude > 0.0001f
            ? settings.WindDirection.normalized
            : Vector2.right;
        Vector4 wind = new Vector4(windDirection.x, windDirection.y, settings.WindStrength, settings.WindSpeed);
        Vector4 fadeDistances = new Vector4(settings.FadeStartDistance, settings.DetailDistance, 0f, 0f);
        Vector4 meshGrounding = new Vector4(0f, settings.SurfaceOffset, 0f, 0f);
        int layer = gameObject.layer;
        bool deferGrassStreaming = ShouldDeferGrassStreaming();
        float grassCellSize = GetGrassStreamingCellSize(settings);
        Vector2Int viewerCell = WorldToGrassCellCoord(viewer.position, grassCellSize);
        grassBuildCandidateBuffer.Clear();

        foreach (Vector2Int coord in visibleGrassCellCoords)
        {
            if (!grassCells.TryGetValue(coord, out GrassCell cell))
            {
                continue;
            }

            if (!cell.HasGrassBuild)
            {
                if (!deferGrassStreaming)
                {
                    grassBuildCandidateBuffer.Add(new ChunkCandidate(coord, GetGrassCellDistanceSqr(coord, viewerCell)));
                }

                continue;
            }

            cell.DrawGrass(
                drawMesh,
                drawMaterial,
                viewer.position,
                fadeDistances,
                wind,
                meshGrounding,
                settings.ShadowCastingMode,
                settings.ReceiveShadows,
                layer);
        }

        if (deferGrassStreaming || grassBuildCandidateBuffer.Count == 0)
        {
            return;
        }

        grassBuildCandidateBuffer.Sort((a, b) => a.DistanceSqr.CompareTo(b.DistanceSqr));
        int requestCount = Mathf.Min(maxGrassBuildRequestsPerFrame, grassBuildCandidateBuffer.Count);
        for (int i = 0; i < requestCount; i++)
        {
            RequestGrassBuild(grassBuildCandidateBuffer[i].Coord);
        }
    }

    private void RefreshVisibleGrassCells(GrassSettingsSO settings)
    {
        visibleGrassCellCoords.Clear();
        grassVisibilityCandidateBuffer.Clear();

        if (viewer == null)
        {
            return;
        }

        float grassCellSize = GetGrassStreamingCellSize(settings);
        Vector2Int viewerCell = WorldToGrassCellCoord(viewer.position, grassCellSize);
        int radius = Mathf.Max(1, Mathf.CeilToInt(settings.DetailDistance / grassCellSize) + 1);

        for (int z = -radius; z <= radius; z++)
        {
            for (int x = -radius; x <= radius; x++)
            {
                Vector2Int coord = viewerCell + new Vector2Int(x, z);
                if (!IsGrassCellInsideDistance(coord, grassCellSize, settings.DetailDistance))
                {
                    continue;
                }

                grassVisibilityCandidateBuffer.Add(new ChunkCandidate(coord, x * x + z * z));
            }
        }

        grassVisibilityCandidateBuffer.Sort((a, b) => a.DistanceSqr.CompareTo(b.DistanceSqr));
        foreach (ChunkCandidate candidate in grassVisibilityCandidateBuffer)
        {
            visibleGrassCellCoords.Add(candidate.Coord);
            if (!grassCells.ContainsKey(candidate.Coord))
            {
                grassCells.Add(candidate.Coord, new GrassCell(candidate.Coord));
            }
        }

        if (settings.UnloadOutsideDetailDistance)
        {
            grassRemovalBuffer.Clear();
            foreach (KeyValuePair<Vector2Int, GrassCell> pair in grassCells)
            {
                if (!visibleGrassCellCoords.Contains(pair.Key))
                {
                    grassRemovalBuffer.Add(pair.Key);
                }
            }

            foreach (Vector2Int coord in grassRemovalBuffer)
            {
                RemoveGrassCell(coord);
            }
        }

        PruneQueuedGrassBuildsToVisibleCells();
    }

    private bool IsGrassCellInsideDistance(Vector2Int coord, float grassCellSize, float distance)
    {
        if (viewer == null)
        {
            return false;
        }

        Vector2 cellCenter = new Vector2((coord.x + 0.5f) * grassCellSize, (coord.y + 0.5f) * grassCellSize);
        Vector2 viewerPosition = new Vector2(viewer.position.x, viewer.position.z);
        float cellRadius = grassCellSize * 0.707107f;
        float allowedDistance = Mathf.Max(0f, distance) + cellRadius;
        return (cellCenter - viewerPosition).sqrMagnitude <= allowedDistance * allowedDistance;
    }

    private int CalculateGrassInstanceCapacity(float grassCellSize)
    {
        GrassSettingsSO settings = GetGrassSettings();
        if (settings == null || !settings.EnableGrass || settings.MaxInstancesPerGrassCell <= 0 || settings.DensityPerSquareMeter <= 0f)
        {
            return 0;
        }

        int cellsPerAxis = Mathf.Max(1, Mathf.CeilToInt(grassCellSize / settings.DetailCellSize));
        long maxByCells = (long)cellsPerAxis * cellsPerAxis * settings.MaxInstancesPerCell;
        return (int)Mathf.Min(settings.MaxInstancesPerGrassCell, maxByCells);
    }

    private void RequestGrassBuild(Vector2Int coord)
    {
        if (!visibleGrassCellCoords.Contains(coord)
            || runningGrassTasks.ContainsKey(coord)
            || queuedGrassCells.Contains(coord))
        {
            return;
        }

        if (grassCells.TryGetValue(coord, out GrassCell cell)
            && (cell.HasGrassBuild || cell.HasPendingGrassUpload))
        {
            return;
        }

        grassBuildQueue.Enqueue(coord);
        queuedGrassCells.Add(coord);
    }

    private void StartQueuedGrassBuilds()
    {
        if (viewer == null || ShouldDeferGrassStreaming())
        {
            return;
        }

        GrassSettingsSO settings = GetGrassSettings();
        if (settings == null
            || !settings.EnableGrass
            || settings.DensityPerSquareMeter <= 0f
            || settings.MaxInstancesPerGrassCell <= 0)
        {
            return;
        }

        float grassCellSize = GetGrassStreamingCellSize(settings);
        int grassInstanceCapacity = CalculateGrassInstanceCapacity(grassCellSize);
        while (runningGrassTasks.Count < maxConcurrentGrassBuildTasks && grassBuildQueue.Count > 0)
        {
            Vector2Int coord = grassBuildQueue.Dequeue();
            queuedGrassCells.Remove(coord);

            if (!visibleGrassCellCoords.Contains(coord)
                || !grassCells.TryGetValue(coord, out GrassCell cell)
                || cell.HasGrassBuild
                || cell.HasPendingGrassUpload)
            {
                continue;
            }

            if (grassInstanceCapacity <= 0)
            {
                cell.ApplyEmpty();
                continue;
            }

            GrassBuildTask task = ScheduleGrassBuild(coord, grassCellSize, grassInstanceCapacity);
            runningGrassTasks.Add(coord, task);
        }
    }

    private GrassBuildTask ScheduleGrassBuild(Vector2Int coord, float grassCellSize, int grassInstanceCapacity)
    {
        int surfaceResolution = CalculateGrassSurfaceResolution(grassCellSize);
        int surfaceVertexCount = surfaceResolution * surfaceResolution;
        int heightLayerCount = GetTerrainHeightLayerCount();
        int heightSplineSampleCount = GetTerrainSplineSampleCount();
        int grassTerrainLayerCount = grassInstanceCapacity > 0 ? GetGrassTerrainLayerCount() : 0;

        GrassBuildTask task = new GrassBuildTask(
            coord,
            grassCellSize,
            surfaceVertexCount,
            surfaceResolution,
            heightLayerCount,
            heightSplineSampleCount,
            grassInstanceCapacity,
            grassTerrainLayerCount);
        CopyTerrainHeightLayers(task.HeightLayers, task.HeightSplineSamples);
        CopyGrassTerrainLayers(task.GrassTerrainLayers);

        TerrainSettings terrainSettings = CreateTerrainSettings();
        float2 cellOrigin = GrassCellOrigin(coord, grassCellSize);

        GenerateTerrainVerticesJob surfaceJob = new GenerateTerrainVerticesJob
        {
            Vertices = task.Vertices,
            Normals = task.Normals,
            Uvs = task.Uvs,
            Settings = terrainSettings,
            HeightLayers = task.HeightLayers,
            HeightSplineSamples = task.HeightSplineSamples,
            HeightLayerCount = task.HeightLayers.IsCreated ? task.HeightLayers.Length : 0,
            ChunkOrigin = cellOrigin,
            ChunkSize = grassCellSize,
            SkirtDepth = 0f,
            Resolution = surfaceResolution,
            BaseVertexCount = surfaceVertexCount,
            SegmentCount = surfaceResolution - 1,
            LodStep = 1,
            Stitching = default
        };

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
            ChunkOrigin = cellOrigin,
            ChunkSize = grassCellSize,
            Resolution = surfaceResolution,
            BaseVertexCount = surfaceVertexCount
        };

        JobHandle surfaceHandle = surfaceJob.ScheduleParallel(surfaceVertexCount, 64, default);
        task.Handle = grassJob.Schedule(surfaceHandle);
        return task;
    }

    private void CompleteFinishedGrassTasks()
    {
        if (runningGrassTasks.Count == 0)
        {
            return;
        }

        completedGrassTaskBuffer.Clear();
        foreach (KeyValuePair<Vector2Int, GrassBuildTask> pair in runningGrassTasks)
        {
            if (pair.Value.Handle.IsCompleted)
            {
                completedGrassTaskBuffer.Add(pair.Key);
            }
        }

        if (completedGrassTaskBuffer.Count == 0)
        {
            return;
        }

        float grassCellSize = GetGrassStreamingCellSize(GetGrassSettings());
        completedGrassTaskSortOrigin = viewer != null
            ? WorldToGrassCellCoord(viewer.position, grassCellSize)
            : default;
        completedGrassTaskBuffer.Sort(CompareCompletedGrassTaskDistance);

        int appliedCount = 0;
        foreach (Vector2Int coord in completedGrassTaskBuffer)
        {
            if (!runningGrassTasks.TryGetValue(coord, out GrassBuildTask task))
            {
                continue;
            }

            bool canApply = CanApplyCompletedGrassTask(coord, task, out GrassCell cell);
            if (canApply && appliedCount >= maxGrassBuildRequestsPerFrame)
            {
                continue;
            }

            runningGrassTasks.Remove(coord);
            task.Handle.Complete();

            if (canApply)
            {
                cell.Apply(task);
                appliedCount++;
            }
            else if (visibleGrassCellCoords.Contains(coord) && grassCells.ContainsKey(coord))
            {
                RequestGrassBuild(coord);
            }

            task.Dispose();
        }
    }

    private int CompareCompletedGrassTaskDistance(Vector2Int a, Vector2Int b)
    {
        int distanceComparison = GetGrassCellDistanceSqr(a, completedGrassTaskSortOrigin)
            .CompareTo(GetGrassCellDistanceSqr(b, completedGrassTaskSortOrigin));
        if (distanceComparison != 0)
        {
            return distanceComparison;
        }

        int xComparison = a.x.CompareTo(b.x);
        return xComparison != 0 ? xComparison : a.y.CompareTo(b.y);
    }

    private bool CanApplyCompletedGrassTask(Vector2Int coord, GrassBuildTask task, out GrassCell cell)
    {
        float currentCellSize = GetGrassStreamingCellSize(GetGrassSettings());
        return grassCells.TryGetValue(coord, out cell)
            && visibleGrassCellCoords.Contains(coord)
            && Mathf.Abs(task.CellSize - currentCellSize) <= 0.001f;
    }

    private void PruneQueuedGrassBuildsToVisibleCells()
    {
        if (grassBuildQueue.Count == 0)
        {
            return;
        }

        int queuedCount = grassBuildQueue.Count;
        queuedGrassCells.Clear();

        for (int i = 0; i < queuedCount; i++)
        {
            Vector2Int coord = grassBuildQueue.Dequeue();
            if (!visibleGrassCellCoords.Contains(coord)
                || runningGrassTasks.ContainsKey(coord)
                || !grassCells.ContainsKey(coord)
                || !queuedGrassCells.Add(coord))
            {
                continue;
            }

            grassBuildQueue.Enqueue(coord);
        }
    }

    private void CompleteAndDisposeAllGrassTasks()
    {
        foreach (KeyValuePair<Vector2Int, GrassBuildTask> pair in runningGrassTasks)
        {
            pair.Value.Handle.Complete();
            pair.Value.Dispose();
        }

        runningGrassTasks.Clear();
        grassBuildQueue.Clear();
        queuedGrassCells.Clear();
        completedGrassTaskBuffer.Clear();
    }

    private void RemoveGrassCell(Vector2Int coord)
    {
        queuedGrassCells.Remove(coord);

        if (!grassCells.TryGetValue(coord, out GrassCell cell))
        {
            return;
        }

        cell.Dispose();
        grassCells.Remove(coord);
    }

    private void ClearGrassRuntimeCells()
    {
        CompleteAndDisposeAllGrassTasks();

        foreach (GrassCell cell in grassCells.Values)
        {
            cell.Dispose();
        }

        grassCells.Clear();
        visibleGrassCellCoords.Clear();
        grassRemovalBuffer.Clear();
        grassVisibilityCandidateBuffer.Clear();
        grassBuildCandidateBuffer.Clear();
        grassUploadCandidateBuffer.Clear();
    }

    private int CalculateGrassSurfaceResolution(float grassCellSize)
    {
        float terrainSampleSpacing = chunkSize / Mathf.Max(1, GetEffectiveSegmentCount());
        int segmentCount = Mathf.CeilToInt(grassCellSize / Mathf.Max(0.25f, terrainSampleSpacing));
        segmentCount = Mathf.Clamp(segmentCount, 1, MaxGrassSurfaceResolution - 1);
        return segmentCount + 1;
    }

    private float GetGrassStreamingCellSize(GrassSettingsSO settings)
    {
        float size = settings != null
            ? settings.StreamingCellSize
            : GrassSettingsSO.DefaultStreamingCellSize;
        return Mathf.Max(8f, size);
    }

    private static Vector2Int WorldToGrassCellCoord(Vector3 worldPosition, float grassCellSize)
    {
        return new Vector2Int(
            Mathf.FloorToInt(worldPosition.x / grassCellSize),
            Mathf.FloorToInt(worldPosition.z / grassCellSize));
    }

    private static int GetGrassCellDistanceSqr(Vector2Int coord, Vector2Int origin)
    {
        int dx = coord.x - origin.x;
        int dy = coord.y - origin.y;
        return dx * dx + dy * dy;
    }

    private static float2 GrassCellOrigin(Vector2Int coord, float grassCellSize)
    {
        return new float2(coord.x * grassCellSize, coord.y * grassCellSize);
    }

    private GrassBuildSettings CreateGrassBuildSettings()
    {
        GrassSettingsSO settings = GetGrassSettings();
        if (settings == null)
        {
            return default;
        }

        float minHeight = settings.MinHeight;
        if (settings.AvoidWater && enableWater)
        {
            minHeight = Mathf.Max(minHeight, waterHeight + settings.WaterPadding);
        }

        return new GrassBuildSettings
        {
            TerrainSeed = GetTerrainSeed(),
            GrassSeed = GetTerrainSeed() * 17 + 97,
            Channel = Mathf.Clamp(settings.ChannelIndex, 0, MaxTerrainLayerCount - 1),
            DensityPerSquareMeter = settings.DensityPerSquareMeter,
            CellSize = settings.DetailCellSize,
            Jitter = settings.Jitter,
            MaxInstancesPerCell = settings.MaxInstancesPerCell,
            LayerThreshold = settings.LayerThreshold,
            MinHeight = minHeight,
            MaxHeight = Mathf.Max(minHeight + 0.01f, settings.MaxHeight),
            HeightFadeRange = settings.HeightFadeRange,
            MinSlopeAngle = settings.MinSlopeAngle,
            MaxSlopeAngle = settings.MaxSlopeAngle,
            SlopeFadeRange = settings.SlopeFadeRange,
            BladeHeight = settings.BladeHeight,
            BladeHeightVariation = settings.BladeHeightVariation,
            BladeWidth = settings.BladeWidth,
            BladeWidthVariation = settings.BladeWidthVariation,
            ColorVariation = settings.ColorVariation,
            NormalAlignment = settings.NormalAlignment,
            SurfaceOffset = settings.SurfaceOffset,
            CoverageNoiseFrequency = settings.CoverageNoiseFrequency,
            CoverageNoiseStrength = settings.CoverageNoiseStrength
        };
    }

    private GrassSettingsSO GetGrassSettings()
    {
        if (grassSettings != null)
        {
            return grassSettings;
        }

        if (runtimeDefaultGrassSettings == null)
        {
            runtimeDefaultGrassSettings = GrassSettingsSO.CreateRuntimeDefault();
        }

        return runtimeDefaultGrassSettings;
    }

    private int GetGrassTerrainLayerCount()
    {
        return terrainLayers != null ? Mathf.Min(terrainLayers.Length, MaxTerrainLayerCount) : 0;
    }

    private void CopyGrassTerrainLayers(NativeArray<GrassTerrainLayerData> destination)
    {
        if (!destination.IsCreated || destination.Length == 0 || terrainLayers == null)
        {
            return;
        }

        GrassTerrainLayerData[] sortedLayers = new GrassTerrainLayerData[destination.Length];
        for (int i = 0; i < sortedLayers.Length; i++)
        {
            TerrainHeightLayer layer = terrainLayers[i];
            sortedLayers[i] = new GrassTerrainLayerData
            {
                Channel = Mathf.Clamp((int)layer.channel, 0, MaxTerrainLayerCount - 1),
                StartHeight = layer.startHeight,
                BlendRange = Mathf.Max(0.0001f, layer.blendRange)
            };
        }

        Array.Sort(sortedLayers, (a, b) => a.StartHeight.CompareTo(b.StartHeight));

        for (int i = 0; i < sortedLayers.Length; i++)
        {
            destination[i] = sortedLayers[i];
        }
    }

    private Mesh GetGrassRenderMesh(GrassSettingsSO settings)
    {
        if (settings.Mesh != null)
        {
            return settings.Mesh;
        }

        if (runtimeGrassMesh != null)
        {
            return runtimeGrassMesh;
        }

        runtimeGrassMesh = CreateDefaultGrassMesh();
        return runtimeGrassMesh;
    }

    private Material GetGrassRenderMaterial(GrassSettingsSO settings)
    {
        if (settings.Material != null)
        {
            return settings.Material;
        }

        if (runtimeGrassMaterial != null)
        {
            return runtimeGrassMaterial;
        }

        Shader shader = Shader.Find(GrassShaderName);
        if (shader == null)
        {
            return null;
        }

        runtimeGrassMaterial = new Material(shader)
        {
            name = "Runtime Instanced Grass",
            hideFlags = HideFlags.DontSave
        };
        runtimeGrassMaterial.enableInstancing = true;
        return runtimeGrassMaterial;
    }

    private static Mesh CreateDefaultGrassMesh()
    {
        Mesh mesh = new Mesh
        {
            name = "Runtime Cross Grass Blade",
            hideFlags = HideFlags.DontSave
        };

        Vector3[] vertices =
        {
            new Vector3(-0.5f, 0f, 0f),
            new Vector3(-0.5f, 1f, 0f),
            new Vector3(0.5f, 1f, 0f),
            new Vector3(0.5f, 0f, 0f),
            new Vector3(0f, 0f, -0.5f),
            new Vector3(0f, 1f, -0.5f),
            new Vector3(0f, 1f, 0.5f),
            new Vector3(0f, 0f, 0.5f)
        };

        Vector2[] uvs =
        {
            new Vector2(0f, 0f),
            new Vector2(0f, 1f),
            new Vector2(1f, 1f),
            new Vector2(1f, 0f),
            new Vector2(0f, 0f),
            new Vector2(0f, 1f),
            new Vector2(1f, 1f),
            new Vector2(1f, 0f)
        };

        int[] indices =
        {
            0, 1, 2,
            0, 2, 3,
            2, 1, 0,
            3, 2, 0,
            4, 5, 6,
            4, 6, 7,
            6, 5, 4,
            7, 6, 4
        };

        mesh.SetVertices(vertices);
        mesh.SetUVs(0, uvs);
        mesh.SetIndices(indices, MeshTopology.Triangles, 0);
        mesh.RecalculateBounds();
        return mesh;
    }

    private void ClearGrassFromRuntimeChunks()
    {
        foreach (TerrainChunk chunk in chunks.Values)
        {
            chunk.ClearGrass();
        }
    }

    private void DestroyGrassRuntimeResources()
    {
        ClearGrassRuntimeCells();
        ClearGrassFromRuntimeChunks();

        if (runtimeGrassMesh != null)
        {
            if (Application.isPlaying)
            {
                Destroy(runtimeGrassMesh);
            }
            else
            {
                DestroyImmediate(runtimeGrassMesh);
            }

            runtimeGrassMesh = null;
        }

        if (runtimeGrassMaterial != null)
        {
            if (Application.isPlaying)
            {
                Destroy(runtimeGrassMaterial);
            }
            else
            {
                DestroyImmediate(runtimeGrassMaterial);
            }

            runtimeGrassMaterial = null;
        }

        if (runtimeDefaultGrassSettings != null)
        {
            if (Application.isPlaying)
            {
                Destroy(runtimeDefaultGrassSettings);
            }
            else
            {
                DestroyImmediate(runtimeDefaultGrassSettings);
            }

            runtimeDefaultGrassSettings = null;
        }
    }

    private sealed class GrassCell : IDisposable
    {
        private ComputeBuffer grassInstanceBuffer;
        private ComputeBuffer grassArgsBuffer;
        private MaterialPropertyBlock grassPropertyBlock;
        private Bounds grassBounds;
        private int grassInstanceCount;
        private int pendingGrassUploadCount;
        private int pendingGrassUploadOffset;
        private uint grassArgsIndexCount;
        private uint grassArgsStartIndex;
        private uint grassArgsBaseVertex;
        private bool grassArgsDirty = true;
        private bool grassBuilt;
        private NativeArray<GrassInstanceData> pendingGrassUpload;

        public GrassCell(Vector2Int coord)
        {
            Coord = coord;
        }

        public Vector2Int Coord { get; }
        public bool HasPendingGrassUpload => pendingGrassUpload.IsCreated;
        public bool HasGrassBuild => grassBuilt;
        public bool HasGrass => grassBuilt && grassInstanceBuffer != null && grassInstanceCount > 0;

        public void Apply(GrassBuildTask task)
        {
            DisposePendingGrassUpload();

            if (!task.HasGrassInstances)
            {
                ApplyEmpty();
                return;
            }

            int instanceCount = Mathf.Clamp(task.GrassInstanceCounter[0], 0, task.GrassInstances.Length);
            grassBuilt = true;
            grassInstanceCount = 0;
            grassArgsDirty = true;

            if (instanceCount == 0)
            {
                ReleaseGrassRenderData();
                return;
            }

            if (grassInstanceBuffer == null || grassInstanceBuffer.count < instanceCount)
            {
                ReleaseGrassInstanceBuffer();
                grassInstanceBuffer = new ComputeBuffer(instanceCount, GrassInstanceStride, ComputeBufferType.Structured);
            }

            pendingGrassUpload = new NativeArray<GrassInstanceData>(instanceCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            NativeArray<GrassInstanceData>.Copy(task.GrassInstances, pendingGrassUpload, instanceCount);
            pendingGrassUploadCount = instanceCount;
            pendingGrassUploadOffset = 0;
            grassBounds = ToBounds(task.GrassBounds[0]);
        }

        public void ApplyEmpty()
        {
            grassBuilt = true;
            ReleaseGrassRenderData();
        }

        public int UploadPendingGrass(int maxInstanceCount)
        {
            if (!pendingGrassUpload.IsCreated || grassInstanceBuffer == null || maxInstanceCount <= 0)
            {
                return 0;
            }

            int remainingCount = pendingGrassUploadCount - pendingGrassUploadOffset;
            int uploadCount = Mathf.Min(maxInstanceCount, remainingCount);
            if (uploadCount <= 0)
            {
                CompletePendingGrassUpload();
                return 0;
            }

            grassInstanceBuffer.SetData(pendingGrassUpload, pendingGrassUploadOffset, pendingGrassUploadOffset, uploadCount);
            pendingGrassUploadOffset += uploadCount;

            if (pendingGrassUploadOffset >= pendingGrassUploadCount)
            {
                CompletePendingGrassUpload();
            }

            return uploadCount;
        }

        public void DrawGrass(
            Mesh drawMesh,
            Material drawMaterial,
            Vector3 viewerPosition,
            Vector4 fadeDistances,
            Vector4 wind,
            Vector4 meshGrounding,
            ShadowCastingMode shadowCastingMode,
            bool receiveShadows,
            int layer)
        {
            if (!HasGrass || drawMesh == null || drawMaterial == null)
            {
                return;
            }

            EnsureGrassArgsBuffer(drawMesh);

            grassPropertyBlock ??= new MaterialPropertyBlock();
            grassPropertyBlock.Clear();
            grassPropertyBlock.SetBuffer(GrassInstancesPropertyId, grassInstanceBuffer);
            grassPropertyBlock.SetVector(GrassViewerPositionPropertyId, viewerPosition);
            grassPropertyBlock.SetVector(GrassFadeDistancesPropertyId, fadeDistances);
            grassPropertyBlock.SetVector(GrassWindPropertyId, wind);
            grassPropertyBlock.SetVector(GrassMeshGroundingPropertyId, meshGrounding);

            Graphics.DrawMeshInstancedIndirect(
                drawMesh,
                0,
                drawMaterial,
                grassBounds,
                grassArgsBuffer,
                0,
                grassPropertyBlock,
                shadowCastingMode,
                receiveShadows,
                layer);
        }

        public void ClearGrass()
        {
            grassBuilt = false;
            ReleaseGrassRenderData();
        }

        public void Dispose()
        {
            ClearGrass();
        }

        private void ReleaseGrassRenderData()
        {
            grassInstanceCount = 0;
            grassArgsDirty = true;
            DisposePendingGrassUpload();
            ReleaseGrassInstanceBuffer();
            ReleaseGrassArgsBuffer();
        }

        private void CompletePendingGrassUpload()
        {
            grassInstanceCount = pendingGrassUploadCount;
            grassArgsDirty = true;
            DisposePendingGrassUpload();
        }

        private void DisposePendingGrassUpload()
        {
            if (pendingGrassUpload.IsCreated)
            {
                pendingGrassUpload.Dispose();
            }

            pendingGrassUploadCount = 0;
            pendingGrassUploadOffset = 0;
        }

        private void EnsureGrassArgsBuffer(Mesh drawMesh)
        {
            uint indexCount = drawMesh.GetIndexCount(0);
            uint startIndex = drawMesh.GetIndexStart(0);
            uint baseVertex = (uint)drawMesh.GetBaseVertex(0);

            if (grassArgsBuffer == null)
            {
                grassArgsBuffer = new ComputeBuffer(1, sizeof(uint) * 5, ComputeBufferType.IndirectArguments);
                grassArgsDirty = true;
            }

            if (!grassArgsDirty
                && grassArgsIndexCount == indexCount
                && grassArgsStartIndex == startIndex
                && grassArgsBaseVertex == baseVertex)
            {
                return;
            }

            uint[] args =
            {
                indexCount,
                (uint)grassInstanceCount,
                startIndex,
                baseVertex,
                0u
            };

            grassArgsBuffer.SetData(args);
            grassArgsIndexCount = indexCount;
            grassArgsStartIndex = startIndex;
            grassArgsBaseVertex = baseVertex;
            grassArgsDirty = false;
        }

        private void ReleaseGrassInstanceBuffer()
        {
            if (grassInstanceBuffer == null)
            {
                return;
            }

            grassInstanceBuffer.Release();
            grassInstanceBuffer = null;
        }

        private void ReleaseGrassArgsBuffer()
        {
            if (grassArgsBuffer == null)
            {
                return;
            }

            grassArgsBuffer.Release();
            grassArgsBuffer = null;
        }

        private static Bounds ToBounds(GrassChunkBounds source)
        {
            Vector3 min = new Vector3(source.Min.x, source.Min.y, source.Min.z);
            Vector3 max = new Vector3(source.Max.x, source.Max.y, source.Max.z);
            Bounds bounds = new Bounds((min + max) * 0.5f, max - min);
            bounds.Expand(2f);
            return bounds;
        }
    }

    [BurstCompile(FloatPrecision.Standard, FloatMode.Fast, CompileSynchronously = false)]
    private struct GenerateGrassInstancesJob : IJob
    {
        [ReadOnly] public NativeArray<Vector3> Vertices;
        [ReadOnly] public NativeArray<Vector3> Normals;
        [ReadOnly] public NativeArray<GrassTerrainLayerData> TerrainLayers;
        [WriteOnly] public NativeArray<GrassInstanceData> GrassInstances;
        public NativeArray<int> GrassInstanceCounter;
        public NativeArray<GrassChunkBounds> GrassBounds;

        public GrassBuildSettings Settings;
        public SlopeTextureSettings SlopeTextureSettings;
        public int2 ChunkCoord;
        public float2 ChunkOrigin;
        public float ChunkSize;
        public int Resolution;
        public int BaseVertexCount;

        public void Execute()
        {
            int capacity = GrassInstances.Length;
            if (capacity == 0 || Resolution < 2 || BaseVertexCount < Resolution * Resolution)
            {
                WriteOutputs(0, default);
                return;
            }

            int cellsPerAxis = math.max(1, (int)math.ceil(ChunkSize / math.max(0.1f, Settings.CellSize)));
            float cellSize = ChunkSize / cellsPerAxis;
            float expectedPerCell = Settings.DensityPerSquareMeter * cellSize * cellSize;
            if (expectedPerCell <= 0f)
            {
                WriteOutputs(0, default);
                return;
            }

            int count = 0;
            GrassChunkBounds bounds = new GrassChunkBounds
            {
                Min = new float3(float.MaxValue, float.MaxValue, float.MaxValue),
                Max = new float3(float.MinValue, float.MinValue, float.MinValue)
            };

            for (int z = 0; z < cellsPerAxis && count < capacity; z++)
            {
                for (int x = 0; x < cellsPerAxis && count < capacity; x++)
                {
                    uint cellHash = Hash(ChunkCoord.x, ChunkCoord.y, x, z, Settings.TerrainSeed, Settings.GrassSeed);
                    int candidateCount = (int)math.floor(expectedPerCell);
                    float fractional = expectedPerCell - candidateCount;
                    if (Hash01(cellHash + 0x6d2b79f5u) < fractional)
                    {
                        candidateCount++;
                    }

                    candidateCount = math.min(candidateCount, Settings.MaxInstancesPerCell);
                    for (int candidate = 0; candidate < candidateCount && count < capacity; candidate++)
                    {
                        uint instanceHash = Hash((int)cellHash, candidate, x * 73856093, z * 19349663, Settings.GrassSeed, Settings.TerrainSeed);
                        float2 jitter = new float2(
                            Hash01(instanceHash + 0x9e3779b9u),
                            Hash01(instanceHash + 0xbb67ae85u));
                        float2 centeredJitter = (jitter - 0.5f) * Settings.Jitter + 0.5f;
                        float2 local = (new float2(x, z) + centeredJitter) * cellSize;
                        local = math.clamp(local, new float2(0.001f, 0.001f), new float2(ChunkSize - 0.001f, ChunkSize - 0.001f));

                        SampleSurface(local, out float3 positionWS, out float3 normalWS);
                        float slopeAngle = math.acos(math.clamp(normalWS.y, -1f, 1f)) * 57.29578f;
                        float coverage = EvaluateLayerCoverage(positionWS.y, normalWS, slopeAngle);
                        coverage *= EvaluateHeightGate(positionWS.y);
                        coverage *= EvaluateSlopeGate(slopeAngle);
                        coverage *= EvaluateCoverageNoise(positionWS.xz);
                        coverage = math.saturate(coverage);

                        if (coverage <= 0f || Hash01(instanceHash + 0x3c6ef372u) > coverage)
                        {
                            continue;
                        }

                        float bladeHeight = Settings.BladeHeight * math.max(
                            0.05f,
                            1f + (Hash01(instanceHash + 0xa54ff53au) * 2f - 1f) * Settings.BladeHeightVariation);
                        float bladeWidth = Settings.BladeWidth * math.max(
                            0.05f,
                            1f + (Hash01(instanceHash + 0x510e527fu) * 2f - 1f) * Settings.BladeWidthVariation);
                        float yaw = Hash01(instanceHash + 0x1f83d9abu) * 6.2831855f;
                        float colorScale = 1f + (Hash01(instanceHash + 0x5be0cd19u) * 2f - 1f) * Settings.ColorVariation;
                        float3 instanceNormal = math.normalize(math.lerp(new float3(0f, 1f, 0f), normalWS, Settings.NormalAlignment));
                        positionWS.y += Settings.SurfaceOffset;

                        GrassInstances[count] = new GrassInstanceData
                        {
                            PositionScale = new float4(positionWS, bladeHeight),
                            NormalYaw = new float4(instanceNormal, yaw),
                            ColorWidth = new float4(colorScale, colorScale, colorScale, bladeWidth)
                        };

                        float radius = math.max(bladeWidth, Settings.SurfaceOffset) * 1.5f;
                        bounds.Min = math.min(bounds.Min, positionWS + new float3(-radius, 0f, -radius));
                        bounds.Max = math.max(bounds.Max, positionWS + new float3(radius, bladeHeight, radius));
                        count++;
                    }
                }
            }

            WriteOutputs(count, bounds);
        }

        private void SampleSurface(float2 local, out float3 positionWS, out float3 normalWS)
        {
            float u = math.saturate(local.x / ChunkSize);
            float v = math.saturate(local.y / ChunkSize);
            float gridX = u * (Resolution - 1);
            float gridZ = v * (Resolution - 1);
            int x0 = math.clamp((int)math.floor(gridX), 0, Resolution - 1);
            int z0 = math.clamp((int)math.floor(gridZ), 0, Resolution - 1);
            int x1 = math.min(x0 + 1, Resolution - 1);
            int z1 = math.min(z0 + 1, Resolution - 1);
            float tx = gridX - x0;
            float tz = gridZ - z0;

            int i00 = z0 * Resolution + x0;
            int i10 = z0 * Resolution + x1;
            int i01 = z1 * Resolution + x0;
            int i11 = z1 * Resolution + x1;

            float h00 = Vertices[i00].y;
            float h10 = Vertices[i10].y;
            float h01 = Vertices[i01].y;
            float h11 = Vertices[i11].y;
            float height0 = math.lerp(h00, h10, tx);
            float height1 = math.lerp(h01, h11, tx);
            float surfaceHeight = math.lerp(height0, height1, tz);

            float3 n00 = ToFloat3(Normals[i00]);
            float3 n10 = ToFloat3(Normals[i10]);
            float3 n01 = ToFloat3(Normals[i01]);
            float3 n11 = ToFloat3(Normals[i11]);
            float3 normal0 = math.lerp(n00, n10, tx);
            float3 normal1 = math.lerp(n01, n11, tx);
            normalWS = math.normalize(math.lerp(normal0, normal1, tz));
            if (math.lengthsq(normalWS) < 0.0001f)
            {
                normalWS = new float3(0f, 1f, 0f);
            }

            positionWS = new float3(ChunkOrigin.x + local.x, surfaceHeight, ChunkOrigin.y + local.y);
        }

        private float EvaluateLayerCoverage(float height, float3 normal, float slopeAngle)
        {
            float weight = EvaluateSplatWeight(height, normal, slopeAngle, Settings.Channel);
            return math.saturate((weight - Settings.LayerThreshold) / math.max(1f - Settings.LayerThreshold, 0.0001f));
        }

        private float EvaluateSplatWeight(float height, float3 normal, float slopeAngle, int channel)
        {
            if (TerrainLayers.Length == 0)
            {
                return channel == 0 ? 1f : 0f;
            }

            float4 map0 = float4.zero;
            float4 map1 = float4.zero;
            AddWeight(ref map0, ref map1, TerrainLayers[0].Channel, 1f);

            for (int i = 1; i < TerrainLayers.Length; i++)
            {
                GrassTerrainLayerData layer = TerrainLayers[i];
                float blend = SmoothStep(layer.StartHeight, layer.StartHeight + math.max(0.0001f, layer.BlendRange), height);
                map0 *= 1f - blend;
                map1 *= 1f - blend;
                AddWeight(ref map0, ref map1, layer.Channel, blend);
            }

            NormalizeWeights(ref map0, ref map1);

            if (SlopeTextureSettings.Enabled)
            {
                float slopeBlend = SmoothStep(SlopeTextureSettings.StartAngle, SlopeTextureSettings.FullAngle, slopeAngle);
                float rockAmount = math.saturate(slopeBlend * SlopeTextureSettings.Strength);
                if (rockAmount > 0.0001f)
                {
                    map0 *= 1f - rockAmount;
                    map1 *= 1f - rockAmount;
                    AddWeight(ref map0, ref map1, (int)SlopeTextureSettings.Channel, rockAmount);
                    NormalizeWeights(ref map0, ref map1);
                }
            }

            return ReadWeight(map0, map1, channel);
        }

        private float EvaluateHeightGate(float height)
        {
            float lower = SmoothStep(Settings.MinHeight, Settings.MinHeight + Settings.HeightFadeRange, height);
            float upper = 1f - SmoothStep(Settings.MaxHeight - Settings.HeightFadeRange, Settings.MaxHeight, height);
            return math.saturate(lower * upper);
        }

        private float EvaluateSlopeGate(float slopeAngle)
        {
            float lower = SmoothStep(Settings.MinSlopeAngle - Settings.SlopeFadeRange, Settings.MinSlopeAngle, slopeAngle);
            float upper = 1f - SmoothStep(Settings.MaxSlopeAngle, Settings.MaxSlopeAngle + Settings.SlopeFadeRange, slopeAngle);
            return math.saturate(lower * upper);
        }

        private float EvaluateCoverageNoise(float2 worldXZ)
        {
            if (Settings.CoverageNoiseFrequency <= 0f || Settings.CoverageNoiseStrength <= 0f)
            {
                return 1f;
            }

            float2 seedOffset = new float2(Settings.GrassSeed * 11.17f, Settings.GrassSeed * -7.31f);
            float noiseValue = To01(noise.snoise((worldXZ + seedOffset) * Settings.CoverageNoiseFrequency));
            return math.lerp(1f, math.saturate(noiseValue * 1.8f), Settings.CoverageNoiseStrength);
        }

        private void WriteOutputs(int count, GrassChunkBounds bounds)
        {
            GrassInstanceCounter[0] = count;
            GrassBounds[0] = count > 0
                ? bounds
                : new GrassChunkBounds { Min = float3.zero, Max = float3.zero };
        }

        private static void AddWeight(ref float4 map0, ref float4 map1, int channel, float value)
        {
            switch (channel)
            {
                case 1:
                    map0.y += value;
                    break;
                case 2:
                    map0.z += value;
                    break;
                case 3:
                    map0.w += value;
                    break;
                case 4:
                    map1.x += value;
                    break;
                case 5:
                    map1.y += value;
                    break;
                case 6:
                    map1.z += value;
                    break;
                case 7:
                    map1.w += value;
                    break;
                default:
                    map0.x += value;
                    break;
            }
        }

        private static float ReadWeight(float4 map0, float4 map1, int channel)
        {
            switch (channel)
            {
                case 1:
                    return map0.y;
                case 2:
                    return map0.z;
                case 3:
                    return map0.w;
                case 4:
                    return map1.x;
                case 5:
                    return map1.y;
                case 6:
                    return map1.z;
                case 7:
                    return map1.w;
                default:
                    return map0.x;
            }
        }

        private static void NormalizeWeights(ref float4 map0, ref float4 map1)
        {
            float sum = math.csum(map0) + math.csum(map1);
            if (sum > 0.0001f)
            {
                map0 /= sum;
                map1 /= sum;
            }
        }

        private static float SmoothStep(float edge0, float edge1, float value)
        {
            float t = math.saturate((value - edge0) / math.max(edge1 - edge0, 0.0001f));
            return t * t * (3f - 2f * t);
        }

        private static float To01(float value)
        {
            return math.saturate(value * 0.5f + 0.5f);
        }

        private static float3 ToFloat3(Vector3 value)
        {
            return new float3(value.x, value.y, value.z);
        }

        private static uint Hash(int a, int b, int c, int d, int e, int f)
        {
            unchecked
            {
                uint hash = 2166136261u;
                hash = Mix(hash, (uint)a);
                hash = Mix(hash, (uint)b);
                hash = Mix(hash, (uint)c);
                hash = Mix(hash, (uint)d);
                hash = Mix(hash, (uint)e);
                hash = Mix(hash, (uint)f);
                return Hash(hash);
            }
        }

        private static uint Mix(uint hash, uint value)
        {
            unchecked
            {
                hash ^= value;
                hash *= 16777619u;
                return hash;
            }
        }

        private static uint Hash(uint value)
        {
            unchecked
            {
                value ^= value >> 16;
                value *= 0x7feb352du;
                value ^= value >> 15;
                value *= 0x846ca68bu;
                value ^= value >> 16;
                return value;
            }
        }

        private static float Hash01(uint value)
        {
            return (Hash(value) >> 8) * (1f / 16777216f);
        }
    }

    private sealed partial class TerrainChunk
    {
        private ComputeBuffer grassInstanceBuffer;
        private ComputeBuffer grassArgsBuffer;
        private MaterialPropertyBlock grassPropertyBlock;
        private Bounds grassBounds;
        private int grassInstanceCount;
        private int pendingGrassUploadCount;
        private int pendingGrassUploadOffset;
        private uint grassArgsIndexCount;
        private uint grassArgsStartIndex;
        private uint grassArgsBaseVertex;
        private bool grassArgsDirty = true;
        private bool grassBuilt;
        private NativeArray<GrassInstanceData> pendingGrassUpload;

        public bool HasPendingGrassUpload => pendingGrassUpload.IsCreated;
        public bool HasGrassBuild => grassBuilt;
        public bool HasGrass => grassBuilt && grassInstanceBuffer != null && grassInstanceCount > 0;

        public void ApplyGrass(TerrainBuildTask task)
        {
            DisposePendingGrassUpload();

            if (!task.HasGrassInstances)
            {
                ClearGrass();
                return;
            }

            int instanceCount = Mathf.Clamp(task.GrassInstanceCounter[0], 0, task.GrassInstances.Length);
            grassBuilt = true;
            grassInstanceCount = 0;
            grassArgsDirty = true;

            if (instanceCount == 0)
            {
                ReleaseGrassRenderData();
                return;
            }

            if (grassInstanceBuffer == null || grassInstanceBuffer.count < instanceCount)
            {
                ReleaseGrassInstanceBuffer();
                grassInstanceBuffer = new ComputeBuffer(instanceCount, GrassInstanceStride, ComputeBufferType.Structured);
            }

            pendingGrassUpload = new NativeArray<GrassInstanceData>(instanceCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            NativeArray<GrassInstanceData>.Copy(task.GrassInstances, pendingGrassUpload, instanceCount);
            pendingGrassUploadCount = instanceCount;
            pendingGrassUploadOffset = 0;
            grassBounds = ToBounds(task.GrassBounds[0]);
        }

        public int UploadPendingGrass(int maxInstanceCount)
        {
            if (!pendingGrassUpload.IsCreated || grassInstanceBuffer == null || maxInstanceCount <= 0)
            {
                return 0;
            }

            int remainingCount = pendingGrassUploadCount - pendingGrassUploadOffset;
            int uploadCount = Mathf.Min(maxInstanceCount, remainingCount);
            if (uploadCount <= 0)
            {
                CompletePendingGrassUpload();
                return 0;
            }

            grassInstanceBuffer.SetData(pendingGrassUpload, pendingGrassUploadOffset, pendingGrassUploadOffset, uploadCount);
            pendingGrassUploadOffset += uploadCount;

            if (pendingGrassUploadOffset >= pendingGrassUploadCount)
            {
                CompletePendingGrassUpload();
            }

            return uploadCount;
        }

        public void ClearGrass()
        {
            grassBuilt = false;
            ReleaseGrassRenderData();
        }

        private void ReleaseGrassRenderData()
        {
            grassInstanceCount = 0;
            grassArgsDirty = true;
            DisposePendingGrassUpload();
            ReleaseGrassInstanceBuffer();
            ReleaseGrassArgsBuffer();
        }

        private void CompletePendingGrassUpload()
        {
            grassInstanceCount = pendingGrassUploadCount;
            grassArgsDirty = true;
            DisposePendingGrassUpload();
        }

        private void DisposePendingGrassUpload()
        {
            if (pendingGrassUpload.IsCreated)
            {
                pendingGrassUpload.Dispose();
            }

            pendingGrassUploadCount = 0;
            pendingGrassUploadOffset = 0;
        }

        public void DrawGrass(
            Mesh drawMesh,
            Material drawMaterial,
            Vector3 viewerPosition,
            Vector4 fadeDistances,
            Vector4 wind,
            Vector4 meshGrounding,
            ShadowCastingMode shadowCastingMode,
            bool receiveShadows,
            int layer)
        {
            if (!HasGrass || drawMesh == null || drawMaterial == null)
            {
                return;
            }

            EnsureGrassArgsBuffer(drawMesh);

            grassPropertyBlock ??= new MaterialPropertyBlock();
            grassPropertyBlock.Clear();
            grassPropertyBlock.SetBuffer(GrassInstancesPropertyId, grassInstanceBuffer);
            grassPropertyBlock.SetVector(GrassViewerPositionPropertyId, viewerPosition);
            grassPropertyBlock.SetVector(GrassFadeDistancesPropertyId, fadeDistances);
            grassPropertyBlock.SetVector(GrassWindPropertyId, wind);
            grassPropertyBlock.SetVector(GrassMeshGroundingPropertyId, meshGrounding);

            Graphics.DrawMeshInstancedIndirect(
                drawMesh,
                0,
                drawMaterial,
                grassBounds,
                grassArgsBuffer,
                0,
                grassPropertyBlock,
                shadowCastingMode,
                receiveShadows,
                layer);
        }

        private void EnsureGrassArgsBuffer(Mesh drawMesh)
        {
            uint indexCount = drawMesh.GetIndexCount(0);
            uint startIndex = drawMesh.GetIndexStart(0);
            uint baseVertex = (uint)drawMesh.GetBaseVertex(0);

            if (grassArgsBuffer == null)
            {
                grassArgsBuffer = new ComputeBuffer(1, sizeof(uint) * 5, ComputeBufferType.IndirectArguments);
                grassArgsDirty = true;
            }

            if (!grassArgsDirty
                && grassArgsIndexCount == indexCount
                && grassArgsStartIndex == startIndex
                && grassArgsBaseVertex == baseVertex)
            {
                return;
            }

            uint[] args =
            {
                indexCount,
                (uint)grassInstanceCount,
                startIndex,
                baseVertex,
                0u
            };

            grassArgsBuffer.SetData(args);
            grassArgsIndexCount = indexCount;
            grassArgsStartIndex = startIndex;
            grassArgsBaseVertex = baseVertex;
            grassArgsDirty = false;
        }

        private void ReleaseGrassInstanceBuffer()
        {
            if (grassInstanceBuffer == null)
            {
                return;
            }

            grassInstanceBuffer.Release();
            grassInstanceBuffer = null;
        }

        private void ReleaseGrassArgsBuffer()
        {
            if (grassArgsBuffer == null)
            {
                return;
            }

            grassArgsBuffer.Release();
            grassArgsBuffer = null;
        }

        private static Bounds ToBounds(GrassChunkBounds source)
        {
            Vector3 min = new Vector3(source.Min.x, source.Min.y, source.Min.z);
            Vector3 max = new Vector3(source.Max.x, source.Max.y, source.Max.z);
            Bounds bounds = new Bounds((min + max) * 0.5f, max - min);
            bounds.Expand(2f);
            return bounds;
        }
    }
}
