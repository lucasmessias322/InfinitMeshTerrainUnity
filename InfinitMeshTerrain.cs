using System;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Serialization;

[DisallowMultipleComponent]
public class InfinitMeshTerrain : MonoBehaviour
{
    private const int MaxTerrainLayers = 8;
    private const int ControlTextureCount = 2;

    [Header("Viewer")]
    [SerializeField] private Transform viewer;
    [SerializeField, Min(1)] private int viewDistanceInChunks = 8;
    [SerializeField, Min(16f)] private float chunkSize = 1024f;
    [SerializeField, Min(5)] private int verticesPerLine = 33;
    [SerializeField, Min(1f)] private float viewerMoveThreshold = 32f;

    [Header("Rendering")]
    [SerializeField] private Material terrainMaterial;
    [SerializeField, Range(0, 5)] private int maxLod = 3;
    [SerializeField, Min(0f)] private float skirtDepth = 32f;
    [SerializeField] private bool useCollider;
    [SerializeField, Range(0, 5)] private int colliderMaxLod;
    [SerializeField, Min(1)] private int maxConcurrentMeshTasks = 4;
    [SerializeField, Min(0)] private int cachedChunkPadding = 2;

    [Header("Terrain Shape")]
    [SerializeField, Min(1f)] private float heightMultiplier = 800f;
    [SerializeField] private Vector2 noiseOffset = new Vector2(-50000f, 50000f);
    [SerializeField] private int terrainSeed = 1337;
    [SerializeField, Min(0.000001f)] private float continentFrequency = 0.00012f;
    [SerializeField, Min(0.000001f)] private float domainWarpFrequency = 0.00035f;
    [SerializeField, Min(0f)] private float domainWarpStrength = 300f;
    [SerializeField, Min(0.000001f)] private float biomeFrequency = 0.0003f;
    [SerializeField, Min(0.000001f)] private float ridgeFrequency = 0.0012f;
    [SerializeField, Min(0.000001f)] private float detailFrequency = 0.0035f;
    [SerializeField, Range(0f, 0.9f)] private float seaCoverage = 0.3f;
    [SerializeField, Range(0f, 1f)] private float mountainStart = 0.58f;
    [SerializeField, Min(0f)] private float plainsStrength = 0.2f;
    [SerializeField, Min(0f)] private float hillsStrength = 0.18f;
    [SerializeField, Min(0f)] private float mountainStrength = 0.42f;
    [SerializeField, Min(0f)] private float cliffStrength = 0.18f;
    [SerializeField, Min(0f)] private float detailStrength = 0.06f;
    [SerializeField, Range(0f, 1f)] private float terraceStrength = 0.15f;
    [SerializeField, Min(1)] private int terraceSteps = 7;
    [FormerlySerializedAs("legacyNoiseInfluence")]
    [SerializeField, Range(0f, 1f), InspectorName("Noise Layer Influence")] private float noiseLayerInfluence = 1f;
    [SerializeField] private NoiseLayersSO noiseSettings;

    [Header("Surface Layers")]
    [SerializeField] private TerrainLayerRule[] layerRules =
    {
        new TerrainLayerRule
        {
            layerName = "Sand",
            splatColor = new Color(0f, 0f, 0f, 1f),
            minHeight = 0f,
            maxHeight = 0.4f,
            blend = 0.1f,
            minSlope = 0f,
            maxSlope = 1f,
            slopeBlend = 0.15f,
            minMoisture = 0f,
            maxMoisture = 1f,
            moistureBlend = 0.15f,
            variationScale = 0.0025f,
            variationStrength = 0.15f
        },
        new TerrainLayerRule
        {
            layerName = "Grass",
            splatColor = new Color(0f, 1f, 0f, 0f),
            minHeight = 0.1f,
            maxHeight = 1f,
            blend = 0.1f,
            minSlope = 0f,
            maxSlope = 0.55f,
            slopeBlend = 0.15f,
            minMoisture = 0f,
            maxMoisture = 1f,
            moistureBlend = 0.15f,
            variationScale = 0.0025f,
            variationStrength = 0.15f
        },
        new TerrainLayerRule
        {
            layerName = "Rock",
            splatColor = new Color(0f, 0f, 1f, 0f),
            minHeight = 0.5f,
            maxHeight = 1f,
            blend = 0.1f,
            minSlope = 0.35f,
            maxSlope = 1f,
            slopeBlend = 0.15f,
            minMoisture = 0f,
            maxMoisture = 1f,
            moistureBlend = 0.15f,
            variationScale = 0.0025f,
            variationStrength = 0.15f
        }
    };

    [Header("Water")]
    [SerializeField] private bool enableWater = true;
    [SerializeField] private GameObject waterObject;
    [SerializeField] private float waterHeight = 150f;
    [SerializeField] private float waterScale = 102.4f;

    private readonly Dictionary<Vector2Int, TerrainChunk> chunks = new Dictionary<Vector2Int, TerrainChunk>();
    private readonly Dictionary<Vector2Int, TerrainBuildTask> runningTasks = new Dictionary<Vector2Int, TerrainBuildTask>();
    private readonly Queue<Vector2Int> buildQueue = new Queue<Vector2Int>();
    private readonly HashSet<Vector2Int> queuedChunks = new HashSet<Vector2Int>();
    private readonly HashSet<Vector2Int> visibleChunkCoords = new HashSet<Vector2Int>();
    private readonly List<Vector2Int> removalBuffer = new List<Vector2Int>();
    private readonly List<ChunkCandidate> candidateBuffer = new List<ChunkCandidate>();
    private readonly List<Vector2Int> completedTaskBuffer = new List<Vector2Int>();

    private GameObject waterInstance;
    private Vector2Int lastViewerChunk;
    private Vector3 lastViewerUpdatePosition;
    private int control0TextureId;
    private int control1TextureId;
    private int controlAliasTextureId;
    private int numLayersCountId;
    private bool hasBuiltInitialSet;

    private void Awake()
    {
        CacheShaderPropertyIds();

        if (viewer == null && Camera.main != null)
        {
            viewer = Camera.main.transform;
        }
    }

    private void OnEnable()
    {
        ForceRefresh();
    }

    private void Start()
    {
        EnsureWaterInstance();
        ForceRefresh();
    }

    private void Update()
    {
        CompleteFinishedTasks();
        StartQueuedBuilds();
        UpdateWater();

        if (viewer == null)
        {
            return;
        }

        Vector2Int viewerChunk = WorldToChunkCoord(viewer.position);
        float moveThresholdSqr = viewerMoveThreshold * viewerMoveThreshold;
        bool movedFarEnough = (viewer.position - lastViewerUpdatePosition).sqrMagnitude >= moveThresholdSqr;

        if (!hasBuiltInitialSet || viewerChunk != lastViewerChunk || movedFarEnough)
        {
            RefreshVisibleChunks(viewerChunk);
        }
    }

    private void OnDisable()
    {
        CompleteAndDisposeAllTasks();
        ClearRuntimeChunks();
        DestroyWaterInstance();
        hasBuiltInitialSet = false;
    }

    private void OnValidate()
    {
        viewDistanceInChunks = Mathf.Max(1, viewDistanceInChunks);
        chunkSize = Mathf.Max(16f, chunkSize);
        verticesPerLine = Mathf.Max(5, verticesPerLine);
        if (verticesPerLine % 2 == 0)
        {
            verticesPerLine += 1;
        }

        heightMultiplier = Mathf.Max(1f, heightMultiplier);
        maxConcurrentMeshTasks = Mathf.Max(1, maxConcurrentMeshTasks);
        cachedChunkPadding = Mathf.Max(0, cachedChunkPadding);
        maxLod = Mathf.Clamp(maxLod, 0, 5);
        colliderMaxLod = Mathf.Clamp(colliderMaxLod, 0, maxLod);
        terraceSteps = Mathf.Max(1, terraceSteps);
        CacheShaderPropertyIds();
    }

    public void ForceRefresh()
    {
        if (!isActiveAndEnabled || viewer == null)
        {
            return;
        }

        RefreshVisibleChunks(WorldToChunkCoord(viewer.position));
    }

    private void RefreshVisibleChunks(Vector2Int viewerChunk)
    {
        visibleChunkCoords.Clear();
        candidateBuffer.Clear();

        int radius = Mathf.Max(1, viewDistanceInChunks);
        for (int z = -radius; z <= radius; z++)
        {
            for (int x = -radius; x <= radius; x++)
            {
                Vector2Int offset = new Vector2Int(x, z);
                int distanceSqr = x * x + z * z;

                if (distanceSqr > radius * radius)
                {
                    continue;
                }

                candidateBuffer.Add(new ChunkCandidate(viewerChunk + offset, distanceSqr));
            }
        }

        candidateBuffer.Sort((a, b) => a.DistanceSqr.CompareTo(b.DistanceSqr));

        foreach (ChunkCandidate candidate in candidateBuffer)
        {
            Vector2Int coord = candidate.Coord;
            visibleChunkCoords.Add(coord);

            int desiredLod = SelectLod(candidate.DistanceSqr);
            if (!chunks.TryGetValue(coord, out TerrainChunk chunk))
            {
                chunk = CreateChunk(coord);
                chunks.Add(coord, chunk);
            }

            chunk.SetVisible(true);
            chunk.DesiredLod = desiredLod;
        }

        foreach (Vector2Int coord in visibleChunkCoords)
        {
            TerrainChunk chunk = chunks[coord];
            chunk.DesiredStitching = CalculateDesiredStitching(coord, chunk.DesiredLod);

            if (!chunk.HasMesh || chunk.CurrentLod != chunk.DesiredLod || !chunk.CurrentStitching.Equals(chunk.DesiredStitching))
            {
                RequestBuild(coord);
            }
        }

        removalBuffer.Clear();
        int unloadRadius = viewDistanceInChunks + cachedChunkPadding;
        int unloadRadiusSqr = unloadRadius * unloadRadius;
        foreach (KeyValuePair<Vector2Int, TerrainChunk> pair in chunks)
        {
            if (visibleChunkCoords.Contains(pair.Key))
            {
                continue;
            }

            pair.Value.SetVisible(false);

            Vector2Int delta = pair.Key - viewerChunk;
            bool outsideCache = delta.x * delta.x + delta.y * delta.y > unloadRadiusSqr;

            if (outsideCache && !runningTasks.ContainsKey(pair.Key))
            {
                removalBuffer.Add(pair.Key);
            }
        }

        foreach (Vector2Int coord in removalBuffer)
        {
            if (!chunks.TryGetValue(coord, out TerrainChunk chunk))
            {
                continue;
            }

            chunk.Dispose();
            chunks.Remove(coord);
            queuedChunks.Remove(coord);
        }

        lastViewerChunk = viewerChunk;
        lastViewerUpdatePosition = viewer.position;
        hasBuiltInitialSet = true;
    }

    private TerrainChunk CreateChunk(Vector2Int coord)
    {
        GameObject chunkObject = new GameObject($"Terrain Chunk {coord.x}, {coord.y}");
        chunkObject.transform.SetParent(transform, false);
        chunkObject.transform.localPosition = new Vector3(coord.x * chunkSize, 0f, coord.y * chunkSize);

        MeshFilter meshFilter = chunkObject.AddComponent<MeshFilter>();
        MeshRenderer meshRenderer = chunkObject.AddComponent<MeshRenderer>();
        meshRenderer.sharedMaterial = terrainMaterial;

        MeshCollider meshCollider = null;
        if (useCollider)
        {
            meshCollider = chunkObject.AddComponent<MeshCollider>();
            meshCollider.enabled = false;
        }

        return new TerrainChunk(coord, chunkObject, meshFilter, meshRenderer, meshCollider);
    }

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
        int segmentCount = verticesPerLine - 1;
        int resolution = segmentCount / step + 1;
        int baseVertexCount = resolution * resolution;
        int skirtVertexCount = resolution * 4;
        int vertexCount = baseVertexCount + skirtVertexCount;
        int baseQuadCount = (resolution - 1) * (resolution - 1);
        int skirtQuadCount = (resolution - 1) * 4;
        int indexCount = (baseQuadCount + skirtQuadCount) * 6;

        TerrainBuildTask task = new TerrainBuildTask(
            coord,
            lod,
            stitching,
            resolution,
            vertexCount,
            indexCount,
            baseVertexCount,
            GetLayerRuleCount(),
            GetNoiseLayerCount());
        CopyLayerRules(task.LayerRules);
        CopyNoiseLayers(task.NoiseLayers);

        TerrainSettings settings = CreateTerrainSettings();

        GenerateTerrainVerticesJob verticesJob = new GenerateTerrainVerticesJob
        {
            Vertices = task.Vertices,
            Normals = task.Normals,
            Uvs = task.Uvs,
            SplatColors = task.SplatColors,
            ControlPixels0 = task.ControlPixels0,
            ControlPixels1 = task.ControlPixels1,
            LayerRules = task.LayerRules,
            NoiseLayers = task.NoiseLayers,
            Settings = settings,
            ChunkOrigin = new float2(coord.x * chunkSize, coord.y * chunkSize),
            ChunkSize = chunkSize,
            HeightMultiplier = heightMultiplier,
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
            TotalQuadCount = baseQuadCount + skirtQuadCount
        };

        JobHandle verticesHandle = verticesJob.ScheduleParallel(vertexCount, 64, default);
        JobHandle indicesHandle = indicesJob.Schedule();
        task.Handle = JobHandle.CombineDependencies(verticesHandle, indicesHandle);
        return task;
    }

    private TerrainSettings CreateTerrainSettings()
    {
        return new TerrainSettings
        {
            NoiseOffset = noiseOffset,
            TerrainSeed = terrainSeed,
            ContinentFrequency = continentFrequency,
            DomainWarpFrequency = domainWarpFrequency,
            DomainWarpStrength = domainWarpStrength,
            BiomeFrequency = biomeFrequency,
            RidgeFrequency = ridgeFrequency,
            DetailFrequency = detailFrequency,
            SeaCoverage = seaCoverage,
            MountainStart = mountainStart,
            PlainsStrength = plainsStrength,
            HillsStrength = hillsStrength,
            MountainStrength = mountainStrength,
            CliffStrength = cliffStrength,
            DetailStrength = detailStrength,
            TerraceStrength = terraceStrength,
            TerraceSteps = terraceSteps,
            NoiseLayerInfluence = noiseLayerInfluence
        };
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
                chunk.Apply(
                    task,
                    terrainMaterial,
                    control0TextureId,
                    control1TextureId,
                    controlAliasTextureId,
                    numLayersCountId,
                    GetLayerRuleCount(),
                    useCollider,
                    colliderMaxLod);
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

    private void CopyLayerRules(NativeArray<TerrainLayerRuleData> destination)
    {
        if (!destination.IsCreated || destination.Length == 0)
        {
            return;
        }

        for (int i = 0; i < destination.Length; i++)
        {
            TerrainLayerRule source = layerRules[i];
            Color color = source.splatColor;
            destination[i] = new TerrainLayerRuleData
            {
                DebugColor = new float4(color.r, color.g, color.b, color.a),
                MinHeight = source.minHeight,
                MaxHeight = source.maxHeight,
                Blend = Mathf.Max(0.0001f, source.blend),
                MinSlope = source.minSlope,
                MaxSlope = source.maxSlope,
                SlopeBlend = Mathf.Max(0.0001f, source.slopeBlend),
                MinMoisture = source.minMoisture,
                MaxMoisture = source.maxMoisture,
                MoistureBlend = Mathf.Max(0.0001f, source.moistureBlend),
                VariationScale = source.variationScale,
                VariationStrength = source.variationStrength
            };
        }
    }

    private int GetLayerRuleCount()
    {
        return layerRules != null ? Mathf.Min(layerRules.Length, MaxTerrainLayers) : 0;
    }

    private void CacheShaderPropertyIds()
    {
        control0TextureId = Shader.PropertyToID("_Control0");
        control1TextureId = Shader.PropertyToID("_Control1");
        controlAliasTextureId = Shader.PropertyToID("_Control");
        numLayersCountId = Shader.PropertyToID("_NumLayersCount");
    }

    private void CopyNoiseLayers(NativeArray<NoiseLayerData> destination)
    {
        if (!destination.IsCreated || destination.Length == 0 || noiseSettings == null || noiseSettings.NoiseLayers == null)
        {
            return;
        }

        IReadOnlyList<NoiseLayer> source = noiseSettings.NoiseLayers;
        for (int i = 0; i < destination.Length; i++)
        {
            NoiseLayer layer = source[i];
            destination[i] = new NoiseLayerData
            {
                Scale = new float2(
                    Mathf.Max(0.000001f, Mathf.Abs(layer.scaleX)),
                    Mathf.Max(0.000001f, Mathf.Abs(layer.scaleY))),
                Amplitude = layer.amplitude,
                Role = (int)layer.role,
                Octaves = Mathf.Clamp(layer.octaves <= 0 ? 1 : layer.octaves, 1, 12),
                Lacunarity = Mathf.Max(1f, layer.lacunarity <= 0f ? 2f : layer.lacunarity),
                Gain = Mathf.Clamp01(layer.persistence <= 0f ? 0.5f : layer.persistence),
                HeightThreshold = layer.heightThreshold,
                Offset = layer.offset,
                BlendRange = math.max(0.0001f, layer.blendRange)
            };
        }
    }

    private int GetNoiseLayerCount()
    {
        return noiseSettings != null && noiseSettings.NoiseLayers != null ? noiseSettings.NoiseLayers.Count : 0;
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
        int segmentCount = Mathf.Max(1, verticesPerLine - 1);
        int step = 1 << Mathf.Clamp(lod, 0, maxLod);

        while (step > 1 && segmentCount % step != 0)
        {
            step >>= 1;
        }

        return Mathf.Max(1, step);
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

    [Serializable]
    public struct TerrainLayerRule
    {
        public string layerName;
        public Color splatColor;
        [Range(0f, 1f)] public float minHeight;
        [Range(0f, 1f)] public float maxHeight;
        [Min(0f)] public float blend;
        [Range(0f, 1f)] public float minSlope;
        [Range(0f, 1f)] public float maxSlope;
        [Min(0f)] public float slopeBlend;
        [Range(0f, 1f)] public float minMoisture;
        [Range(0f, 1f)] public float maxMoisture;
        [Min(0f)] public float moistureBlend;
        [Min(0f)] public float variationScale;
        [Range(0f, 1f)] public float variationStrength;
    }

    private readonly struct ChunkCandidate
    {
        public ChunkCandidate(Vector2Int coord, int distanceSqr)
        {
            Coord = coord;
            DistanceSqr = distanceSqr;
        }

        public Vector2Int Coord { get; }
        public int DistanceSqr { get; }
    }

    private readonly struct EdgeStitching : IEquatable<EdgeStitching>
    {
        public EdgeStitching(int northStep, int eastStep, int southStep, int westStep)
        {
            NorthStep = northStep;
            EastStep = eastStep;
            SouthStep = southStep;
            WestStep = westStep;
        }

        public int NorthStep { get; }
        public int EastStep { get; }
        public int SouthStep { get; }
        public int WestStep { get; }

        public bool Equals(EdgeStitching other)
        {
            return NorthStep == other.NorthStep
                && EastStep == other.EastStep
                && SouthStep == other.SouthStep
                && WestStep == other.WestStep;
        }

        public override bool Equals(object obj)
        {
            return obj is EdgeStitching other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hashCode = NorthStep;
                hashCode = (hashCode * 397) ^ EastStep;
                hashCode = (hashCode * 397) ^ SouthStep;
                hashCode = (hashCode * 397) ^ WestStep;
                return hashCode;
            }
        }
    }

    private sealed class TerrainChunk : IDisposable
    {
        private readonly GameObject gameObject;
        private readonly MeshFilter meshFilter;
        private readonly MeshRenderer meshRenderer;
        private readonly MeshCollider meshCollider;
        private readonly MaterialPropertyBlock propertyBlock;
        private readonly Mesh mesh;
        private readonly Texture2D[] controlTextures = new Texture2D[ControlTextureCount];

        public TerrainChunk(Vector2Int coord, GameObject gameObject, MeshFilter meshFilter, MeshRenderer meshRenderer, MeshCollider meshCollider)
        {
            Coord = coord;
            this.gameObject = gameObject;
            this.meshFilter = meshFilter;
            this.meshRenderer = meshRenderer;
            this.meshCollider = meshCollider;
            propertyBlock = new MaterialPropertyBlock();
            mesh = new Mesh
            {
                name = $"Terrain Chunk Mesh {coord.x}, {coord.y}",
                indexFormat = IndexFormat.UInt32
            };
            mesh.MarkDynamic();
            meshFilter.sharedMesh = mesh;
        }

        public Vector2Int Coord { get; }
        public int CurrentLod { get; private set; } = -1;
        public int DesiredLod { get; set; }
        public EdgeStitching CurrentStitching { get; private set; }
        public EdgeStitching DesiredStitching { get; set; }
        public bool HasMesh { get; private set; }

        public void Apply(
            TerrainBuildTask task,
            Material material,
            int control0TextureId,
            int control1TextureId,
            int controlAliasTextureId,
            int numLayersCountId,
            int layerCount,
            bool useCollider,
            int colliderMaxLod)
        {
            mesh.Clear();
            mesh.indexFormat = task.Vertices.Length > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.SetVertices(task.Vertices);
            mesh.SetNormals(task.Normals);
            mesh.SetUVs(0, task.Uvs);
            mesh.SetColors(task.SplatColors);
            mesh.SetIndices(task.Indices, MeshTopology.Triangles, 0, true);
            mesh.RecalculateBounds();

            meshRenderer.sharedMaterial = material;
            ApplyControlTextures(task, control0TextureId, control1TextureId, controlAliasTextureId, numLayersCountId, layerCount);

            if (meshCollider != null)
            {
                bool colliderEnabled = useCollider && task.Lod <= colliderMaxLod;
                meshCollider.enabled = colliderEnabled;
                meshCollider.sharedMesh = null;
                if (colliderEnabled)
                {
                    meshCollider.sharedMesh = mesh;
                }
            }

            CurrentLod = task.Lod;
            CurrentStitching = task.Stitching;
            HasMesh = true;
        }

        public void SetVisible(bool visible)
        {
            if (gameObject.activeSelf != visible)
            {
                gameObject.SetActive(visible);
            }
        }

        public void Dispose()
        {
            for (int i = 0; i < controlTextures.Length; i++)
            {
                DestroyControlTexture(i);
            }

            if (Application.isPlaying)
            {
                Destroy(mesh);
                Destroy(gameObject);
            }
            else
            {
                DestroyImmediate(mesh);
                DestroyImmediate(gameObject);
            }
        }

        private void ApplyControlTextures(
            TerrainBuildTask task,
            int control0TextureId,
            int control1TextureId,
            int controlAliasTextureId,
            int numLayersCountId,
            int layerCount)
        {
            controlTextures[0] = CreateControlTexture(task.ControlPixels0, task.SplatResolution, 0);
            controlTextures[1] = CreateControlTexture(task.ControlPixels1, task.SplatResolution, 1);

            meshRenderer.GetPropertyBlock(propertyBlock);
            propertyBlock.SetTexture(control0TextureId, controlTextures[0]);
            propertyBlock.SetTexture(control1TextureId, controlTextures[1]);
            propertyBlock.SetTexture(controlAliasTextureId, controlTextures[0]);
            propertyBlock.SetFloat(numLayersCountId, layerCount);
            meshRenderer.SetPropertyBlock(propertyBlock);
        }

        private Texture2D CreateControlTexture(NativeArray<Color32> pixels, int resolution, int textureIndex)
        {
            DestroyControlTexture(textureIndex);

            Texture2D texture = new Texture2D(resolution, resolution, TextureFormat.RGBA32, false, true)
            {
                name = $"Control {textureIndex} {Coord.x}, {Coord.y}",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
            texture.SetPixelData(pixels, 0);
            texture.Apply(false, false);
            return texture;
        }

        private void DestroyControlTexture(int textureIndex)
        {
            Texture2D texture = controlTextures[textureIndex];
            if (texture == null)
            {
                return;
            }

            if (Application.isPlaying)
            {
                Destroy(texture);
            }
            else
            {
                DestroyImmediate(texture);
            }

            controlTextures[textureIndex] = null;
        }
    }

    private sealed class TerrainBuildTask : IDisposable
    {
        public TerrainBuildTask(
            Vector2Int coord,
            int lod,
            EdgeStitching stitching,
            int splatResolution,
            int vertexCount,
            int indexCount,
            int baseVertexCount,
            int layerRuleCount,
            int noiseLayerCount)
        {
            Coord = coord;
            Lod = lod;
            Stitching = stitching;
            SplatResolution = splatResolution;
            BaseVertexCount = baseVertexCount;
            Vertices = new NativeArray<Vector3>(vertexCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            Normals = new NativeArray<Vector3>(vertexCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            Uvs = new NativeArray<Vector2>(vertexCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            SplatColors = new NativeArray<Color32>(vertexCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            ControlPixels0 = new NativeArray<Color32>(splatResolution * splatResolution, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            ControlPixels1 = new NativeArray<Color32>(splatResolution * splatResolution, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            Indices = new NativeArray<int>(indexCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            LayerRules = new NativeArray<TerrainLayerRuleData>(Mathf.Max(0, layerRuleCount), Allocator.Persistent);
            NoiseLayers = new NativeArray<NoiseLayerData>(Mathf.Max(0, noiseLayerCount), Allocator.Persistent);
        }

        public Vector2Int Coord { get; }
        public int Lod { get; }
        public EdgeStitching Stitching { get; }
        public int SplatResolution { get; }
        public int BaseVertexCount { get; }
        public JobHandle Handle;
        public NativeArray<Vector3> Vertices;
        public NativeArray<Vector3> Normals;
        public NativeArray<Vector2> Uvs;
        public NativeArray<Color32> SplatColors;
        public NativeArray<Color32> ControlPixels0;
        public NativeArray<Color32> ControlPixels1;
        public NativeArray<int> Indices;
        public NativeArray<TerrainLayerRuleData> LayerRules;
        public NativeArray<NoiseLayerData> NoiseLayers;

        public void Dispose()
        {
            if (Vertices.IsCreated)
            {
                Vertices.Dispose();
            }

            if (Normals.IsCreated)
            {
                Normals.Dispose();
            }

            if (Uvs.IsCreated)
            {
                Uvs.Dispose();
            }

            if (SplatColors.IsCreated)
            {
                SplatColors.Dispose();
            }

            if (ControlPixels0.IsCreated)
            {
                ControlPixels0.Dispose();
            }

            if (ControlPixels1.IsCreated)
            {
                ControlPixels1.Dispose();
            }

            if (Indices.IsCreated)
            {
                Indices.Dispose();
            }

            if (LayerRules.IsCreated)
            {
                LayerRules.Dispose();
            }

            if (NoiseLayers.IsCreated)
            {
                NoiseLayers.Dispose();
            }
        }

    }

    private struct TerrainSettings
    {
        public float2 NoiseOffset;
        public int TerrainSeed;
        public float ContinentFrequency;
        public float DomainWarpFrequency;
        public float DomainWarpStrength;
        public float BiomeFrequency;
        public float RidgeFrequency;
        public float DetailFrequency;
        public float SeaCoverage;
        public float MountainStart;
        public float PlainsStrength;
        public float HillsStrength;
        public float MountainStrength;
        public float CliffStrength;
        public float DetailStrength;
        public float TerraceStrength;
        public int TerraceSteps;
        public float NoiseLayerInfluence;
    }

    private struct TerrainLayerRuleData
    {
        public float4 DebugColor;
        public float MinHeight;
        public float MaxHeight;
        public float Blend;
        public float MinSlope;
        public float MaxSlope;
        public float SlopeBlend;
        public float MinMoisture;
        public float MaxMoisture;
        public float MoistureBlend;
        public float VariationScale;
        public float VariationStrength;
    }

    private struct NoiseLayerData
    {
        public float2 Scale;
        public float Amplitude;
        public int Role;
        public int Octaves;
        public float Lacunarity;
        public float Gain;
        public float HeightThreshold;
        public float2 Offset;
        public float BlendRange;
    }

    [BurstCompile(FloatPrecision.Standard, FloatMode.Fast, CompileSynchronously = false)]
    private struct GenerateTerrainVerticesJob : IJobFor
    {
        [WriteOnly] public NativeArray<Vector3> Vertices;
        [WriteOnly] public NativeArray<Vector3> Normals;
        [WriteOnly] public NativeArray<Vector2> Uvs;
        [WriteOnly] public NativeArray<Color32> SplatColors;
        [WriteOnly] public NativeArray<Color32> ControlPixels0;
        [WriteOnly] public NativeArray<Color32> ControlPixels1;
        [ReadOnly] public NativeArray<TerrainLayerRuleData> LayerRules;
        [ReadOnly] public NativeArray<NoiseLayerData> NoiseLayers;

        public TerrainSettings Settings;
        public float2 ChunkOrigin;
        public float ChunkSize;
        public float HeightMultiplier;
        public float SkirtDepth;
        public int Resolution;
        public int BaseVertexCount;
        public int SegmentCount;
        public int LodStep;
        public EdgeStitching Stitching;

        public void Execute(int index)
        {
            int2 grid = ResolveGrid(index);
            float2 uv = Resolution > 1
                ? new float2(grid.x, grid.y) / (Resolution - 1)
                : float2.zero;

            float2 world = ChunkOrigin + uv * ChunkSize;
            HeightSample sample = SampleHeight(world);
            sample.WorldHeight = StitchEdgeHeight(sample.WorldHeight, grid, world);
            float3 normal = EstimateNormal(world);
            CalculateLayerControls(world, sample.NormalizedHeight, normal, out Color32 control0, out Color32 control1, out Color32 debugColor);
            float height = sample.WorldHeight;

            if (index >= BaseVertexCount)
            {
                height -= SkirtDepth;
            }
            else
            {
                ControlPixels0[index] = control0;
                ControlPixels1[index] = control1;
            }

            Vertices[index] = new Vector3(uv.x * ChunkSize, height, uv.y * ChunkSize);
            Normals[index] = new Vector3(normal.x, normal.y, normal.z);
            Uvs[index] = new Vector2(uv.x, uv.y);
            SplatColors[index] = debugColor;
        }

        private int2 ResolveGrid(int index)
        {
            if (index < BaseVertexCount)
            {
                return new int2(index % Resolution, index / Resolution);
            }

            int skirtIndex = index - BaseVertexCount;
            int side = skirtIndex / Resolution;
            int sideIndex = skirtIndex - side * Resolution;

            switch (side)
            {
                case 0:
                    return new int2(sideIndex, Resolution - 1);
                case 1:
                    return new int2(Resolution - 1, sideIndex);
                case 2:
                    return new int2(Resolution - 1 - sideIndex, 0);
                default:
                    return new int2(0, Resolution - 1 - sideIndex);
            }
        }

        private float StitchEdgeHeight(float height, int2 grid, float2 world)
        {
            int originalX = grid.x * LodStep;
            int originalZ = grid.y * LodStep;

            if (grid.y == Resolution - 1 && Stitching.NorthStep > LodStep)
            {
                return SampleStitchedEdgeHeight(originalX, Stitching.NorthStep, true, ChunkOrigin.y + ChunkSize);
            }

            if (grid.x == Resolution - 1 && Stitching.EastStep > LodStep)
            {
                return SampleStitchedEdgeHeight(originalZ, Stitching.EastStep, false, ChunkOrigin.x + ChunkSize);
            }

            if (grid.y == 0 && Stitching.SouthStep > LodStep)
            {
                return SampleStitchedEdgeHeight(originalX, Stitching.SouthStep, true, ChunkOrigin.y);
            }

            if (grid.x == 0 && Stitching.WestStep > LodStep)
            {
                return SampleStitchedEdgeHeight(originalZ, Stitching.WestStep, false, ChunkOrigin.x);
            }

            return height;
        }

        private float SampleStitchedEdgeHeight(int axisOriginal, int neighborStep, bool horizontalEdge, float fixedWorldAxis)
        {
            int anchor0 = (axisOriginal / neighborStep) * neighborStep;
            int anchor1 = math.min(anchor0 + neighborStep, SegmentCount);

            if (anchor0 == anchor1)
            {
                return SampleHeight(EdgeWorldPosition(anchor0, horizontalEdge, fixedWorldAxis)).WorldHeight;
            }

            float t = (axisOriginal - anchor0) / (float)(anchor1 - anchor0);
            float h0 = SampleHeight(EdgeWorldPosition(anchor0, horizontalEdge, fixedWorldAxis)).WorldHeight;
            float h1 = SampleHeight(EdgeWorldPosition(anchor1, horizontalEdge, fixedWorldAxis)).WorldHeight;
            return math.lerp(h0, h1, t);
        }

        private float2 EdgeWorldPosition(int axisOriginal, bool horizontalEdge, float fixedWorldAxis)
        {
            float offset = axisOriginal / (float)math.max(1, SegmentCount) * ChunkSize;

            if (horizontalEdge)
            {
                return new float2(ChunkOrigin.x + offset, fixedWorldAxis);
            }

            return new float2(fixedWorldAxis, ChunkOrigin.y + offset);
        }

        private HeightSample SampleHeight(float2 world)
        {
            float2 seeded = world + Settings.NoiseOffset + new float2(Settings.TerrainSeed * 37.17f, Settings.TerrainSeed * -19.91f);
            float2 warp = new float2(
                Fbm(seeded * Settings.DomainWarpFrequency, 3, 2.01f, 0.5f),
                Fbm((seeded + 73.41f) * Settings.DomainWarpFrequency, 3, 2.03f, 0.5f));
            float2 p = seeded + warp * Settings.DomainWarpStrength;

            float continent = To01(Fbm(p * Settings.ContinentFrequency, 5, 2.02f, 0.52f));
            float landMask = SmoothStep(Settings.SeaCoverage, 1f, continent);
            float biome = To01(Fbm((p + 911.73f) * Settings.BiomeFrequency, 3, 2.11f, 0.48f));
            float hills = To01(Fbm((p + 117.13f) * Settings.RidgeFrequency, 4, 2.05f, 0.5f));
            float ridges = RidgedFbm((p - 251.9f) * Settings.RidgeFrequency, 4, 2.08f, 0.55f);
            float detail = Fbm((p + 41.7f) * Settings.DetailFrequency, 3, 2.17f, 0.5f);

            float mountainMask = SmoothStep(Settings.MountainStart, 1f, biome);
            float plains = landMask * Settings.PlainsStrength;
            float rollingHills = landMask * hills * Settings.HillsStrength;
            float mountains = landMask * mountainMask * math.pow(math.saturate(ridges), 1.35f) * Settings.MountainStrength;
            float cliffs = landMask * mountainMask * math.saturate(ridges - 0.45f) * Settings.CliffStrength;
            float normalized = plains + rollingHills + mountains + cliffs + detail * Settings.DetailStrength * landMask;

            float terraced = ApplyTerraces(normalized, Settings.TerraceSteps);
            normalized = math.lerp(normalized, terraced, Settings.TerraceStrength * mountainMask);

            if (NoiseLayers.Length > 0 && Settings.NoiseLayerInfluence > 0f)
            {
                normalized = ApplyRoleNoiseLayers(world, normalized, landMask, mountainMask);
            }

            normalized = math.max(0f, normalized);
            float worldHeight = normalized * HeightMultiplier;
            return new HeightSample(worldHeight, math.saturate(normalized));
        }

        private float3 EstimateNormal(float2 world)
        {
            float sampleDistance = math.max(1f, ChunkSize / math.max(Resolution - 1, 1));
            float left = SampleHeight(world - new float2(sampleDistance, 0f)).WorldHeight;
            float right = SampleHeight(world + new float2(sampleDistance, 0f)).WorldHeight;
            float back = SampleHeight(world - new float2(0f, sampleDistance)).WorldHeight;
            float forward = SampleHeight(world + new float2(0f, sampleDistance)).WorldHeight;
            return math.normalize(new float3(left - right, sampleDistance * 2f, back - forward));
        }

        private void CalculateLayerControls(float2 world, float normalizedHeight, float3 normal, out Color32 control0, out Color32 control1, out Color32 debugColor)
        {
            if (LayerRules.Length == 0)
            {
                float slopeFallback = 1f - math.saturate(normal.y);
                float4 fallback = math.lerp(new float4(0f, 1f, 0f, 0f), new float4(0f, 0f, 1f, 0f), slopeFallback);
                control0 = ToColor32(fallback);
                control1 = new Color32(0, 0, 0, 0);
                debugColor = ToColor32(fallback);
                return;
            }

            float slope = 1f - math.saturate(normal.y);
            float moisture = To01(Fbm((world + Settings.NoiseOffset + 518.27f) * Settings.BiomeFrequency, 3, 2.07f, 0.5f));
            float4 weights0 = float4.zero;
            float4 weights1 = float4.zero;
            float4 debug = float4.zero;
            float totalWeight = 0f;

            for (int i = 0; i < LayerRules.Length; i++)
            {
                TerrainLayerRuleData rule = LayerRules[i];
                float heightWeight = RangeWeight(normalizedHeight, rule.MinHeight, rule.MaxHeight, rule.Blend);
                float slopeWeight = RangeWeight(slope, rule.MinSlope, rule.MaxSlope, rule.SlopeBlend);
                float moistureWeight = RangeWeight(moisture, rule.MinMoisture, rule.MaxMoisture, rule.MoistureBlend);
                float variation = To01(noise.snoise((world + Settings.NoiseOffset + i * 127.1f) * rule.VariationScale));
                float variationWeight = math.lerp(1f, variation, rule.VariationStrength);
                float weight = heightWeight * slopeWeight * moistureWeight * variationWeight;

                AddLayerWeight(ref weights0, ref weights1, i, weight);
                debug += rule.DebugColor * weight;
                totalWeight += weight;
            }

            if (totalWeight <= 0.0001f)
            {
                weights0 = new float4(1f, 0f, 0f, 0f);
                weights1 = float4.zero;
                debug = LayerRules[0].DebugColor;
                totalWeight = 1f;
            }

            control0 = ToColor32(weights0 / totalWeight);
            control1 = ToColor32(weights1 / totalWeight);
            debugColor = ToColor32(debug / totalWeight);
        }

        private static void AddLayerWeight(ref float4 weights0, ref float4 weights1, int layerIndex, float weight)
        {
            switch (layerIndex)
            {
                case 0:
                    weights0.x = weight;
                    break;
                case 1:
                    weights0.y = weight;
                    break;
                case 2:
                    weights0.z = weight;
                    break;
                case 3:
                    weights0.w = weight;
                    break;
                case 4:
                    weights1.x = weight;
                    break;
                case 5:
                    weights1.y = weight;
                    break;
                case 6:
                    weights1.z = weight;
                    break;
                case 7:
                    weights1.w = weight;
                    break;
            }
        }

        private float ApplyRoleNoiseLayers(float2 world, float normalized, float landMask, float mountainMask)
        {
            float result = normalized;

            for (int i = 0; i < NoiseLayers.Length; i++)
            {
                NoiseLayerData layer = NoiseLayers[i];
                float gate = HeightGate(result, layer);
                float amount = layer.Amplitude / math.max(HeightMultiplier, 1f) * Settings.NoiseLayerInfluence * gate;

                if (math.abs(amount) <= 0.000001f)
                {
                    continue;
                }

                switch ((TerrainNoiseRole)layer.Role)
                {
                    case TerrainNoiseRole.Continentalness:
                        result += SampleSignedLayer(world, layer) * amount;
                        break;
                    case TerrainNoiseRole.Erosion:
                        result -= SamplePositiveLayer(world, layer) * amount * landMask * math.lerp(0.35f, 1f, mountainMask);
                        break;
                    case TerrainNoiseRole.HillsNoise:
                        result += SamplePositiveLayer(world, layer) * amount * landMask * (1f - mountainMask * 0.35f);
                        break;
                    case TerrainNoiseRole.PeaksValleys:
                        result += SampleSignedLayer(world, layer) * amount * landMask * math.lerp(0.35f, 1.2f, mountainMask);
                        break;
                    case TerrainNoiseRole.MountainNoise:
                        result += SampleRidgedLayer(world, layer) * amount * landMask * mountainMask;
                        break;
                }
            }

            return math.max(0f, result);
        }

        private float HeightGate(float normalizedHeight, NoiseLayerData layer)
        {
            if (layer.HeightThreshold <= 0f)
            {
                return 1f;
            }

            float worldHeight = normalizedHeight * HeightMultiplier;
            return SmoothStep(layer.HeightThreshold - layer.BlendRange, layer.HeightThreshold + layer.BlendRange, worldHeight);
        }

        private float SampleSignedLayer(float2 world, NoiseLayerData layer)
        {
            return Fbm(LayerPoint(world, layer), layer.Octaves, layer.Lacunarity, layer.Gain);
        }

        private float SamplePositiveLayer(float2 world, NoiseLayerData layer)
        {
            return To01(SampleSignedLayer(world, layer));
        }

        private float SampleRidgedLayer(float2 world, NoiseLayerData layer)
        {
            return RidgedFbm(LayerPoint(world, layer), layer.Octaves, layer.Lacunarity, layer.Gain);
        }

        private float2 LayerPoint(float2 world, NoiseLayerData layer)
        {
            float roleOffset = (layer.Role + 1) * 101.37f;
            float2 seededOffset = new float2(
                Settings.TerrainSeed * 17.31f + roleOffset,
                Settings.TerrainSeed * -23.77f - roleOffset);
            return (world + layer.Offset + seededOffset) * layer.Scale;
        }

        private static float Fbm(float2 p, int octaves, float lacunarity, float gain)
        {
            float amplitude = 0.5f;
            float frequency = 1f;
            float sum = 0f;

            for (int i = 0; i < octaves; i++)
            {
                sum += noise.snoise(p * frequency) * amplitude;
                frequency *= lacunarity;
                amplitude *= gain;
            }

            return sum;
        }

        private static float RidgedFbm(float2 p, int octaves, float lacunarity, float gain)
        {
            float amplitude = 0.5f;
            float frequency = 1f;
            float sum = 0f;
            float total = 0f;

            for (int i = 0; i < octaves; i++)
            {
                float n = 1f - math.abs(noise.snoise(p * frequency));
                sum += n * n * amplitude;
                total += amplitude;
                frequency *= lacunarity;
                amplitude *= gain;
            }

            return total > 0f ? sum / total : 0f;
        }

        private static float ApplyTerraces(float value, int steps)
        {
            float stepCount = math.max(1, steps);
            return math.floor(value * stepCount) / stepCount;
        }

        private static float RangeWeight(float value, float min, float max, float blend)
        {
            float lower = SmoothStep(min - blend, min + blend, value);
            float upper = 1f - SmoothStep(max - blend, max + blend, value);
            return math.saturate(lower * upper);
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

        private static Color32 ToColor32(float4 value)
        {
            float4 c = math.saturate(value);
            return new Color32(
                (byte)math.round(c.x * 255f),
                (byte)math.round(c.y * 255f),
                (byte)math.round(c.z * 255f),
                (byte)math.round(c.w * 255f));
        }

        private struct HeightSample
        {
            public HeightSample(float worldHeight, float normalizedHeight)
            {
                WorldHeight = worldHeight;
                NormalizedHeight = normalizedHeight;
            }

            public float WorldHeight;
            public float NormalizedHeight;
        }
    }

    [BurstCompile(FloatPrecision.Standard, FloatMode.Fast, CompileSynchronously = false)]
    private struct BuildTerrainIndicesJob : IJob
    {
        [WriteOnly] public NativeArray<int> Indices;
        public int Resolution;
        public int BaseQuadCount;
        public int BaseVertexCount;
        public int TotalQuadCount;

        public void Execute()
        {
            for (int quadIndex = 0; quadIndex < TotalQuadCount; quadIndex++)
            {
                WriteQuad(quadIndex);
            }
        }

        private void WriteQuad(int quadIndex)
        {
            if (quadIndex < BaseQuadCount)
            {
                WriteSurfaceQuad(quadIndex);
                return;
            }

            WriteSkirtQuad(quadIndex - BaseQuadCount);
        }

        private void WriteSurfaceQuad(int quadIndex)
        {
            int quadsPerLine = Resolution - 1;
            int x = quadIndex % quadsPerLine;
            int y = quadIndex / quadsPerLine;
            int bottomLeft = y * Resolution + x;
            int bottomRight = bottomLeft + 1;
            int topLeft = bottomLeft + Resolution;
            int topRight = topLeft + 1;
            int index = quadIndex * 6;

            Indices[index] = bottomLeft;
            Indices[index + 1] = topLeft;
            Indices[index + 2] = topRight;
            Indices[index + 3] = bottomLeft;
            Indices[index + 4] = topRight;
            Indices[index + 5] = bottomRight;
        }

        private void WriteSkirtQuad(int skirtQuadIndex)
        {
            int segmentCount = Resolution - 1;
            int side = skirtQuadIndex / segmentCount;
            int i = skirtQuadIndex - side * segmentCount;
            int index = (BaseQuadCount + skirtQuadIndex) * 6;
            int skirtStart = BaseVertexCount + side * Resolution;

            int edgeA;
            int edgeB;

            switch (side)
            {
                case 0:
                    edgeA = (Resolution - 1) * Resolution + i;
                    edgeB = edgeA + 1;
                    WriteOutwardQuad(index, edgeA, edgeB, skirtStart + i, skirtStart + i + 1, 0);
                    break;
                case 1:
                    edgeA = i * Resolution + (Resolution - 1);
                    edgeB = edgeA + Resolution;
                    WriteOutwardQuad(index, edgeA, edgeB, skirtStart + i, skirtStart + i + 1, 1);
                    break;
                case 2:
                    edgeA = Resolution - 1 - i;
                    edgeB = edgeA - 1;
                    WriteOutwardQuad(index, edgeA, edgeB, skirtStart + i, skirtStart + i + 1, 2);
                    break;
                default:
                    edgeA = (Resolution - 1 - i) * Resolution;
                    edgeB = edgeA - Resolution;
                    WriteOutwardQuad(index, edgeA, edgeB, skirtStart + i, skirtStart + i + 1, 3);
                    break;
            }
        }

        private void WriteOutwardQuad(int index, int edgeA, int edgeB, int skirtA, int skirtB, int side)
        {
            if (side == 0 || side == 1)
            {
                Indices[index] = edgeA;
                Indices[index + 1] = skirtB;
                Indices[index + 2] = edgeB;
                Indices[index + 3] = edgeA;
                Indices[index + 4] = skirtA;
                Indices[index + 5] = skirtB;
                return;
            }

            Indices[index] = edgeA;
            Indices[index + 1] = edgeB;
            Indices[index + 2] = skirtB;
            Indices[index + 3] = edgeA;
            Indices[index + 4] = skirtB;
            Indices[index + 5] = skirtA;
        }
    }
}
