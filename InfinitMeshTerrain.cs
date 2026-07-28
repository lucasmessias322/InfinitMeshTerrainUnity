using System.Collections.Generic;
using UnityEngine;

[DisallowMultipleComponent]
public partial class InfinitMeshTerrain : MonoBehaviour
{
    private const int TerrainSplineSampleCount = TerrainSplinesSO.SampleCount;
    private const int TerrainSplineChannelCount = (int)TerrainSplineChannel.Count;
    private const int SplatMapCount = 2;
    private const int SplatMapChannelCount = 4;
    private const int MaxTerrainLayerCount = SplatMapCount * SplatMapChannelCount;
    private static readonly int[] SplatMapPropertyIds =
    {
        Shader.PropertyToID("_SplatMap"),
        Shader.PropertyToID("_SplatMap2")
    };

    private static readonly int[] LayerTexturePropertyIds =
    {
        Shader.PropertyToID("_Texture2D_R"),
        Shader.PropertyToID("_Texture2D_G"),
        Shader.PropertyToID("_Texture2D_B"),
        Shader.PropertyToID("_Texture2D_A"),
        Shader.PropertyToID("_Texture2D_R2"),
        Shader.PropertyToID("_Texture2D_G2"),
        Shader.PropertyToID("_Texture2D_B2"),
        Shader.PropertyToID("_Texture2D_A2")
    };

    private static readonly int[] LayerColorPropertyIds =
    {
        Shader.PropertyToID("_Layer1Color"),
        Shader.PropertyToID("_Layer2Color"),
        Shader.PropertyToID("_Layer3Color"),
        Shader.PropertyToID("_Layer4Color"),
        Shader.PropertyToID("_Layer5Color"),
        Shader.PropertyToID("_Layer6Color"),
        Shader.PropertyToID("_Layer7Color"),
        Shader.PropertyToID("_Layer8Color")
    };

    private const int SkirtNorth = 1 << 0;
    private const int SkirtEast = 1 << 1;
    private const int SkirtSouth = 1 << 2;
    private const int SkirtWest = 1 << 3;

    [Header("Viewer")]
    [SerializeField] private Transform viewer;
    [SerializeField, Min(1)] private int viewDistanceInChunks = 8;
    [SerializeField, Min(16f)] private float chunkSize = 1024f;
    [SerializeField, Min(5)] private int verticesPerLine = 33;
    [Tooltip("Multiplies only the source grid density used by LOD 0. Higher LODs keep skipping over this denser grid.")]
    [SerializeField, Range(1, 4)] private int lod0VertexMultiplier = 2;
    [SerializeField, Min(1f)] private float viewerMoveThreshold = 32f;

    [Header("Rendering")]
    [SerializeField] private Material chunkMaterial;
    [SerializeField] private TerrainHeightLayer[] terrainLayers =
    {
        new TerrainHeightLayer("Low", SplatChannel.Map0R, 0f, 80f),
        new TerrainHeightLayer("Grass", SplatChannel.Map0G, 170f, 80f),
        new TerrainHeightLayer("Rock", SplatChannel.Map0B, 420f, 110f),
        new TerrainHeightLayer("Snow", SplatChannel.Map0A, 620f, 90f),
        new TerrainHeightLayer("Layer 5", SplatChannel.Map1R, 900f, 80f),
        new TerrainHeightLayer("Layer 6", SplatChannel.Map1G, 1000f, 80f),
        new TerrainHeightLayer("Layer 7", SplatChannel.Map1B, 1100f, 80f),
        new TerrainHeightLayer("Layer 8", SplatChannel.Map1A, 1200f, 80f)
    };
    [Header("Slope Texturing")]
    [SerializeField] private bool enableSlopeRock = true;
    [SerializeField] private SplatChannel slopeRockChannel = SplatChannel.Map0B;
    [SerializeField, Range(0f, 90f)] private float slopeRockStartAngle = 35f;
    [SerializeField, Range(0f, 90f)] private float slopeRockFullAngle = 55f;
    [SerializeField, Range(0f, 1f)] private float slopeRockStrength = 0.9f;
    [SerializeField, Range(0, 5)] private int maxLod = 3;
    [SerializeField, Min(0f)] private float skirtDepth = 32f;

    [Header("Collision")]
    [SerializeField] private bool useCollider = true;
    [Tooltip("Square radius, in chunks, that keeps terrain collision alive around the viewer.")]
    [SerializeField, Min(0)] private int colliderDistanceInChunks = 2;
    [SerializeField, Range(0, 5)] private int colliderMaxLod;
    [SerializeField, Min(1)] private int maxColliderUpdatesPerFrame = 4;

    [Header("Streaming")]
    [SerializeField, Min(1)] private int maxConcurrentMeshTasks = 4;
    [SerializeField, Min(0)] private int cachedChunkPadding = 2;

    [Header("Terrain Shape")]
    [SerializeField] private TerrainShapeSettingsSO terrainShapeSettings;

    [Header("Water")]
    [SerializeField] private bool enableWater = true;
    [SerializeField] private GameObject waterObject;
    [SerializeField] private float waterHeight = 150f;
    [SerializeField] private float waterScale = 102.4f;

    private readonly Dictionary<Vector2Int, TerrainChunk> chunks = new Dictionary<Vector2Int, TerrainChunk>();
    private readonly Dictionary<Vector2Int, TerrainBuildTask> runningTasks = new Dictionary<Vector2Int, TerrainBuildTask>();
    private readonly Queue<Vector2Int> buildQueue = new Queue<Vector2Int>();
    private readonly HashSet<Vector2Int> queuedChunks = new HashSet<Vector2Int>();
    private readonly Queue<Vector2Int> colliderUpdateQueue = new Queue<Vector2Int>();
    private readonly HashSet<Vector2Int> queuedColliderChunks = new HashSet<Vector2Int>();
    private readonly HashSet<Vector2Int> visibleChunkCoords = new HashSet<Vector2Int>();
    private readonly List<Vector2Int> removalBuffer = new List<Vector2Int>();
    private readonly List<ChunkCandidate> candidateBuffer = new List<ChunkCandidate>();
    private readonly List<Vector2Int> completedTaskBuffer = new List<Vector2Int>();

    private GameObject waterInstance;
    private Vector2Int lastViewerChunk;
    private Vector3 lastViewerUpdatePosition;
    private bool hasBuiltInitialSet;
    private TerrainShapeSettingsSO subscribedTerrainShapeSettings;
    private GrassSettingsSO subscribedGrassSettings;
    private TreeSettingsSO subscribedTreeSettings;

    private void Awake()
    {
        if (viewer == null && Camera.main != null)
        {
            viewer = Camera.main.transform;
        }
    }

    private void OnEnable()
    {
        SyncTerrainShapeSettingsSubscription();
        SyncGrassSettingsSubscription();
        SyncTreeSettingsSubscription();
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

        ProcessQueuedColliderUpdates();
        UpdateGrassDetails();
        UpdateTreeDetails();
    }

    private void OnDisable()
    {
        UnsubscribeTerrainShapeSettings();
        UnsubscribeGrassSettings();
        UnsubscribeTreeSettings();
        CompleteAndDisposeAllTasks();
        ClearRuntimeChunks();
        DestroyGrassRuntimeResources();
        DestroyTreeRuntimeResources();
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

        lod0VertexMultiplier = Mathf.Clamp(lod0VertexMultiplier, 1, 4);
        maxConcurrentMeshTasks = Mathf.Max(1, maxConcurrentMeshTasks);
        cachedChunkPadding = Mathf.Max(0, cachedChunkPadding);
        maxLod = Mathf.Clamp(maxLod, 0, 5);
        colliderDistanceInChunks = Mathf.Clamp(colliderDistanceInChunks, 0, viewDistanceInChunks);
        colliderMaxLod = Mathf.Clamp(colliderMaxLod, 0, maxLod);
        maxColliderUpdatesPerFrame = Mathf.Max(1, maxColliderUpdatesPerFrame);
        slopeRockChannel = (SplatChannel)Mathf.Clamp((int)slopeRockChannel, 0, MaxTerrainLayerCount - 1);
        slopeRockStartAngle = Mathf.Clamp(slopeRockStartAngle, 0f, 89.9f);
        slopeRockFullAngle = Mathf.Clamp(slopeRockFullAngle, slopeRockStartAngle + 0.01f, 90f);
        slopeRockStrength = Mathf.Clamp01(slopeRockStrength);
        ValidateTerrainShapeSettings();
        SyncTerrainShapeSettingsSubscription();
        ValidateTerrainLayers();
        ValidateGrassSettings();
        SyncGrassSettingsSubscription();
        ValidateTreeSettings();
        SyncTreeSettingsSubscription();
        ApplyChunkMaterialToRuntimeChunks();
        RequestVisibleChunkRebuilds();
        if (useCollider)
        {
            QueueVisibleColliderUpdates();
        }
        else
        {
            DisableAllChunkColliders();
        }
    }

    public void ForceRefresh()
    {
        if (!isActiveAndEnabled || viewer == null)
        {
            return;
        }

        RefreshVisibleChunks(WorldToChunkCoord(viewer.position));
    }

    private void ValidateTerrainShapeSettings()
    {
        if (terrainShapeSettings != null)
        {
            terrainShapeSettings.ValidateValues();
        }
    }

    private void SyncTerrainShapeSettingsSubscription()
    {
        if (subscribedTerrainShapeSettings == terrainShapeSettings)
        {
            return;
        }

        UnsubscribeTerrainShapeSettings();
        subscribedTerrainShapeSettings = terrainShapeSettings;

        if (subscribedTerrainShapeSettings != null)
        {
            subscribedTerrainShapeSettings.Changed += OnTerrainShapeSettingsChanged;
        }
    }

    private void UnsubscribeTerrainShapeSettings()
    {
        if (subscribedTerrainShapeSettings == null)
        {
            return;
        }

        subscribedTerrainShapeSettings.Changed -= OnTerrainShapeSettingsChanged;
        subscribedTerrainShapeSettings = null;
    }

    private void OnTerrainShapeSettingsChanged()
    {
        RequestVisibleChunkRebuilds();
    }

    private void SyncGrassSettingsSubscription()
    {
        if (subscribedGrassSettings == grassSettings)
        {
            return;
        }

        UnsubscribeGrassSettings();
        subscribedGrassSettings = grassSettings;

        if (subscribedGrassSettings != null)
        {
            subscribedGrassSettings.Changed += OnGrassSettingsChanged;
        }
    }

    private void UnsubscribeGrassSettings()
    {
        if (subscribedGrassSettings == null)
        {
            return;
        }

        subscribedGrassSettings.Changed -= OnGrassSettingsChanged;
        subscribedGrassSettings = null;
    }

    private void OnGrassSettingsChanged()
    {
        ValidateGrassSettings();
        ClearGrassFromRuntimeChunks();
        RequestVisibleChunkRebuilds();
    }
}
