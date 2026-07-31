using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

[CreateAssetMenu(fileName = "TreeSettings", menuName = "Procedural Terrain/Tree Settings")]
public sealed class TreeSettingsSO : ScriptableObject
{
    private static readonly TreePrototypeSettings[] EmptyPrototypes = Array.Empty<TreePrototypeSettings>();

    public const float DefaultTreeDistance = 1536f;
    public const float DefaultRenderCellSize = 256f;
    public const float DefaultCellSize = 28f;
    public const int DefaultMaxInstancesPerChunk = 192;
    public const int DefaultMaxInstancesPerCell = 2;
    public const float DefaultJitter = 0.85f;
    public const int DefaultSeedOffset = 27183;
    public const float DefaultInteractiveDistance = 96f;
    public const float DefaultInteractiveReleaseDistance = 128f;
    public const int DefaultMaxInteractiveInstances = 96;
    public const int DefaultMaxInteractiveSpawnsPerFrame = 8;
    public const int MaxInstancedMeshLodIndex = 7;

    private static readonly TreeMeshLodDistance[] EmptyInstancedMeshLodDistances = Array.Empty<TreeMeshLodDistance>();

    public event Action Changed;

    [Header("Rendering")]
    [SerializeField] private bool enableTrees = true;
    [SerializeField, Min(1f)] private float treeDistance = DefaultTreeDistance;
    [SerializeField] private bool unloadOutsideTreeDistance = true;
    [SerializeField] private ShadowCastingMode shadowCastingMode = ShadowCastingMode.On;
    [SerializeField] private bool receiveShadows = true;
    [SerializeField, Min(16f)] private float renderCellSize = DefaultRenderCellSize;
    [SerializeField] private bool forceInstancedMeshLodByDistance = true;
    [SerializeField] private TreeMeshLodDistance[] instancedMeshLodDistances =
    {
        new TreeMeshLodDistance(0f, 96f, 0),
        new TreeMeshLodDistance(96f, 192f, 1),
        new TreeMeshLodDistance(192f, 384f, 2),
        new TreeMeshLodDistance(384f, 768f, 3)
    };

    [Header("Interaction Streaming")]
    [SerializeField] private bool enableInteractiveTrees = true;
    [SerializeField, Min(0f)] private float interactiveDistance = DefaultInteractiveDistance;
    [SerializeField, Min(0f)] private float interactiveReleaseDistance = DefaultInteractiveReleaseDistance;
    [SerializeField, Min(0)] private int maxInteractiveInstances = DefaultMaxInteractiveInstances;
    [SerializeField, Min(1)] private int maxInteractiveSpawnsPerFrame = DefaultMaxInteractiveSpawnsPerFrame;
    [SerializeField] private bool hideInstancedTreesWhenInteractive = true;

    [Header("Distribution")]
    [SerializeField, Min(0.1f)] private float cellSize = DefaultCellSize;
    [SerializeField, Min(0)] private int maxInstancesPerChunk = DefaultMaxInstancesPerChunk;
    [SerializeField, Range(1, 8)] private int maxInstancesPerCell = DefaultMaxInstancesPerCell;
    [SerializeField, Range(0f, 1f)] private float jitter = DefaultJitter;
    [SerializeField] private int seedOffset = DefaultSeedOffset;

    [Header("Prototypes")]
    [SerializeField] private List<TreePrototypeSettings> prototypes = new List<TreePrototypeSettings>();

    public bool EnableTrees => enableTrees;
    public float TreeDistance => Mathf.Max(1f, treeDistance);
    public bool UnloadOutsideTreeDistance => unloadOutsideTreeDistance;
    public ShadowCastingMode ShadowCastingMode => shadowCastingMode;
    public bool ReceiveShadows => receiveShadows;
    public float RenderCellSize => Mathf.Max(16f, renderCellSize);
    public bool ForceInstancedMeshLodByDistance => forceInstancedMeshLodByDistance;
    public IReadOnlyList<TreeMeshLodDistance> InstancedMeshLodDistances =>
        instancedMeshLodDistances != null ? instancedMeshLodDistances : EmptyInstancedMeshLodDistances;
    public bool EnableInteractiveTrees => enableInteractiveTrees;
    public float InteractiveDistance => Mathf.Max(0f, interactiveDistance);
    public float InteractiveReleaseDistance => Mathf.Max(InteractiveDistance, interactiveReleaseDistance);
    public int MaxInteractiveInstances => Mathf.Max(0, maxInteractiveInstances);
    public int MaxInteractiveSpawnsPerFrame => Mathf.Max(1, maxInteractiveSpawnsPerFrame);
    public bool HideInstancedTreesWhenInteractive => hideInstancedTreesWhenInteractive;
    public float CellSize => Mathf.Max(0.1f, cellSize);
    public int MaxInstancesPerChunk => Mathf.Max(0, maxInstancesPerChunk);
    public int MaxInstancesPerCell => Mathf.Clamp(maxInstancesPerCell, 1, 8);
    public float Jitter => Mathf.Clamp01(jitter);
    public int SeedOffset => seedOffset;
    public IReadOnlyList<TreePrototypeSettings> Prototypes => prototypes != null ? prototypes : EmptyPrototypes;
    public bool HasSpawnablePrototypes => GetTotalDensityPerSquareMeter() > 0f;

    public float GetTotalDensityPerSquareMeter()
    {
        if (prototypes == null)
        {
            return 0f;
        }

        float total = 0f;
        for (int i = 0; i < prototypes.Count; i++)
        {
            TreePrototypeSettings prototype = prototypes[i];
            if (prototype != null && prototype.IsSpawnable)
            {
                total += prototype.DensityPerSquareMeter;
            }
        }

        return total;
    }

    public int SelectInstancedMeshLodByDistanceSqr(float distanceSqr, int maxAvailableLodIndex)
    {
        int clampedMaxLodIndex = Mathf.Clamp(maxAvailableLodIndex, 0, MaxInstancedMeshLodIndex);
        if (!forceInstancedMeshLodByDistance)
        {
            return 0;
        }

        IReadOnlyList<TreeMeshLodDistance> distances = InstancedMeshLodDistances;
        if (distances.Count == 0)
        {
            return 0;
        }

        int nearestLowerLod = Mathf.Clamp(distances[0].MeshLod, 0, clampedMaxLodIndex);
        float nearestLowerMinSqr = float.NegativeInfinity;
        int nearestUpperLod = nearestLowerLod;
        float nearestUpperMinSqr = float.PositiveInfinity;

        for (int i = 0; i < distances.Count; i++)
        {
            TreeMeshLodDistance range = distances[i];
            float minDistance = range.MinDistance;
            float maxDistance = range.MaxDistance;
            float minDistanceSqr = minDistance * minDistance;
            float maxDistanceSqr = maxDistance * maxDistance;

            if (distanceSqr >= minDistanceSqr && distanceSqr < maxDistanceSqr)
            {
                return Mathf.Clamp(range.MeshLod, 0, clampedMaxLodIndex);
            }

            if (distanceSqr >= minDistanceSqr && minDistanceSqr >= nearestLowerMinSqr)
            {
                nearestLowerMinSqr = minDistanceSqr;
                nearestLowerLod = Mathf.Clamp(range.MeshLod, 0, clampedMaxLodIndex);
            }

            if (distanceSqr < minDistanceSqr && minDistanceSqr < nearestUpperMinSqr)
            {
                nearestUpperMinSqr = minDistanceSqr;
                nearestUpperLod = Mathf.Clamp(range.MeshLod, 0, clampedMaxLodIndex);
            }
        }

        return nearestLowerMinSqr > float.NegativeInfinity ? nearestLowerLod : nearestUpperLod;
    }

    public void ValidateValues()
    {
        treeDistance = Mathf.Max(1f, treeDistance);
        renderCellSize = renderCellSize <= 0f
            ? DefaultRenderCellSize
            : Mathf.Max(16f, renderCellSize);
        interactiveDistance = Mathf.Max(0f, interactiveDistance);
        interactiveReleaseDistance = Mathf.Max(interactiveDistance, interactiveReleaseDistance);
        maxInteractiveInstances = Mathf.Max(0, maxInteractiveInstances);
        maxInteractiveSpawnsPerFrame = Mathf.Max(1, maxInteractiveSpawnsPerFrame);
        cellSize = Mathf.Max(0.1f, cellSize);
        maxInstancesPerChunk = Mathf.Max(0, maxInstancesPerChunk);
        maxInstancesPerCell = Mathf.Clamp(maxInstancesPerCell, 1, 8);
        jitter = Mathf.Clamp01(jitter);

        if (prototypes == null)
        {
            prototypes = new List<TreePrototypeSettings>();
        }

        if (instancedMeshLodDistances != null)
        {
            for (int i = 0; i < instancedMeshLodDistances.Length; i++)
            {
                instancedMeshLodDistances[i].ValidateValues();
            }

            Array.Sort(instancedMeshLodDistances, (a, b) => a.MinDistance.CompareTo(b.MinDistance));
        }

        for (int i = 0; i < prototypes.Count; i++)
        {
            prototypes[i]?.ValidateValues();
        }
    }

    private void OnValidate()
    {
        ValidateValues();
        Changed?.Invoke();
    }
}

[Serializable]
public struct TreeMeshLodDistance
{
    [SerializeField, Min(0f)] private float minDistance;
    [SerializeField, Min(0.01f)] private float maxDistance;
    [SerializeField, Range(0, TreeSettingsSO.MaxInstancedMeshLodIndex)] private int meshLod;

    public TreeMeshLodDistance(float minDistance, float maxDistance, int meshLod)
    {
        this.minDistance = Mathf.Max(0f, minDistance);
        this.maxDistance = Mathf.Max(this.minDistance + 0.01f, maxDistance);
        this.meshLod = Mathf.Clamp(meshLod, 0, TreeSettingsSO.MaxInstancedMeshLodIndex);
    }

    public float MinDistance => Mathf.Max(0f, minDistance);
    public float MaxDistance => Mathf.Max(MinDistance + 0.01f, maxDistance);
    public int MeshLod => Mathf.Clamp(meshLod, 0, TreeSettingsSO.MaxInstancedMeshLodIndex);

    public void ValidateValues()
    {
        minDistance = Mathf.Max(0f, minDistance);
        maxDistance = Mathf.Max(minDistance + 0.01f, maxDistance);
        meshLod = Mathf.Clamp(meshLod, 0, TreeSettingsSO.MaxInstancedMeshLodIndex);
    }
}

[Serializable]
public sealed class TreePrototypeSettings
{
    private static readonly GameObject[] EmptyPrefabVariations = Array.Empty<GameObject>();

    [Header("Prefab")]
    [SerializeField] private GameObject prefab;
    [SerializeField] private List<GameObject> prefabVariations = new List<GameObject>();
    [SerializeField, Min(0f), InspectorName("Density Per Hectare")] private float densityPerHectare = 4f;

    [Header("Rendering")]
    [SerializeField, Min(1f)] private float maxRenderDistance = TreeSettingsSO.DefaultTreeDistance;

    [Header("Interaction")]
    [SerializeField] private GameObject felledPrefab;
    [SerializeField] private GameObject stumpPrefab;
    [SerializeField] private GameObject resourceDropPrefab;
    [SerializeField, Min(0.01f)] private float maxHealth = 30f;
    [SerializeField, Min(0)] private int minResourceDrops = 1;
    [SerializeField, Min(0)] private int maxResourceDrops = 3;
    [SerializeField, Min(0f)] private float resourceDropScatterRadius = 1.25f;

    [Header("Placement")]
    [SerializeField] private bool useTerrainLayer = true;
    [SerializeField] private InfinitMeshTerrain.SplatChannel channel = InfinitMeshTerrain.SplatChannel.Map0G;
    [SerializeField, Range(0f, 1f)] private float layerThreshold = 0.25f;
    [SerializeField] private bool avoidWater = true;
    [SerializeField, Min(0f)] private float waterPadding = 2f;
    [SerializeField, Min(0f)] private float minHeight;
    [SerializeField, Min(0f)] private float maxHeight = 520f;
    [SerializeField, Min(0.01f)] private float heightFadeRange = 24f;
    [SerializeField, Range(0f, 90f)] private float minSlopeAngle;
    [SerializeField, Range(0f, 90f)] private float maxSlopeAngle = 32f;
    [SerializeField, Min(0.01f)] private float slopeFadeRange = 6f;
    [SerializeField, Min(0f)] private float surfaceOffset;

    [Header("Transform")]
    [SerializeField] private bool randomYaw = true;
    [SerializeField] private bool alignToNormal = true;
    [SerializeField, Range(0f, 1f)] private float normalAlignment = 0.7f;
    [SerializeField, Min(0.01f)] private float minScale = 0.85f;
    [SerializeField, Min(0.01f)] private float maxScale = 1.25f;

    [Header("Forest Noise")]
    [SerializeField, Min(0f)] private float coverageNoiseFrequency = 0.0035f;
    [SerializeField, Range(0f, 1f)] private float coverageNoiseStrength = 0.35f;
    [SerializeField, Min(0f)] private float forestNoiseFrequency = 0.0012f;
    [SerializeField, Range(0f, 1f)] private float forestThreshold = 0.28f;
    [SerializeField, Min(0.001f)] private float forestBlendRange = 0.22f;

    public GameObject Prefab => GetPrefabVariation(0);
    public IReadOnlyList<GameObject> PrefabVariations => prefabVariations != null ? prefabVariations : EmptyPrefabVariations;
    public int PrefabVariationCount => CountPrefabVariations();
    public GameObject FelledPrefab => felledPrefab;
    public GameObject StumpPrefab => stumpPrefab;
    public GameObject ResourceDropPrefab => resourceDropPrefab;
    public float MaxHealth => Mathf.Max(0.01f, maxHealth);
    public int MinResourceDrops => Mathf.Max(0, minResourceDrops);
    public int MaxResourceDrops => Mathf.Max(MinResourceDrops, maxResourceDrops);
    public float ResourceDropScatterRadius => Mathf.Max(0f, resourceDropScatterRadius);
    public float DensityPerSquareMeter => Mathf.Max(0f, densityPerHectare) * 0.0001f;
    public float MaxRenderDistance => maxRenderDistance > 0f
        ? Mathf.Max(1f, maxRenderDistance)
        : TreeSettingsSO.DefaultTreeDistance;
    public bool UseTerrainLayer => useTerrainLayer;
    public int ChannelIndex => Mathf.Clamp((int)channel, 0, 7);
    public float LayerThreshold => Mathf.Clamp01(layerThreshold);
    public bool AvoidWater => avoidWater;
    public float WaterPadding => Mathf.Max(0f, waterPadding);
    public float MinHeight => Mathf.Max(0f, minHeight);
    public float MaxHeight => Mathf.Max(MinHeight + 0.01f, maxHeight);
    public float HeightFadeRange => Mathf.Max(0.01f, heightFadeRange);
    public float MinSlopeAngle => Mathf.Clamp(minSlopeAngle, 0f, 90f);
    public float MaxSlopeAngle => Mathf.Clamp(maxSlopeAngle, MinSlopeAngle, 90f);
    public float SlopeFadeRange => Mathf.Max(0.01f, slopeFadeRange);
    public float SurfaceOffset => Mathf.Max(0f, surfaceOffset);
    public bool RandomYaw => randomYaw;
    public bool AlignToNormal => alignToNormal;
    public float NormalAlignment => Mathf.Clamp01(normalAlignment);
    public float MinScale => Mathf.Max(0.01f, minScale);
    public float MaxScale => Mathf.Max(MinScale, maxScale);
    public float CoverageNoiseFrequency => Mathf.Max(0f, coverageNoiseFrequency);
    public float CoverageNoiseStrength => Mathf.Clamp01(coverageNoiseStrength);
    public float ForestNoiseFrequency => Mathf.Max(0f, forestNoiseFrequency);
    public float ForestThreshold => Mathf.Clamp01(forestThreshold);
    public float ForestBlendRange => Mathf.Max(0.001f, forestBlendRange);
    public bool IsSpawnable => PrefabVariationCount > 0 && DensityPerSquareMeter > 0f;

    public GameObject GetPrefabVariation(int variationIndex)
    {
        int validVariationCount = CountValidPrefabVariations();
        if (validVariationCount <= 0)
        {
            return prefab;
        }

        int targetIndex = PositiveModulo(variationIndex, validVariationCount);
        int currentIndex = 0;
        for (int i = 0; i < prefabVariations.Count; i++)
        {
            GameObject variation = prefabVariations[i];
            if (variation == null)
            {
                continue;
            }

            if (currentIndex == targetIndex)
            {
                return variation;
            }

            currentIndex++;
        }

        return prefab;
    }

    public void ValidateValues()
    {
        densityPerHectare = Mathf.Max(0f, densityPerHectare);
        maxRenderDistance = maxRenderDistance <= 0f
            ? TreeSettingsSO.DefaultTreeDistance
            : Mathf.Max(1f, maxRenderDistance);
        maxHealth = Mathf.Max(0.01f, maxHealth);
        minResourceDrops = Mathf.Max(0, minResourceDrops);
        maxResourceDrops = Mathf.Max(minResourceDrops, maxResourceDrops);
        resourceDropScatterRadius = Mathf.Max(0f, resourceDropScatterRadius);
        layerThreshold = Mathf.Clamp01(layerThreshold);
        waterPadding = Mathf.Max(0f, waterPadding);
        minHeight = Mathf.Max(0f, minHeight);
        maxHeight = Mathf.Max(minHeight + 0.01f, maxHeight);
        heightFadeRange = Mathf.Max(0.01f, heightFadeRange);
        minSlopeAngle = Mathf.Clamp(minSlopeAngle, 0f, 90f);
        maxSlopeAngle = Mathf.Clamp(maxSlopeAngle, minSlopeAngle, 90f);
        slopeFadeRange = Mathf.Max(0.01f, slopeFadeRange);
        surfaceOffset = Mathf.Max(0f, surfaceOffset);
        normalAlignment = Mathf.Clamp01(normalAlignment);
        minScale = Mathf.Max(0.01f, minScale);
        maxScale = Mathf.Max(minScale, maxScale);
        coverageNoiseFrequency = Mathf.Max(0f, coverageNoiseFrequency);
        coverageNoiseStrength = Mathf.Clamp01(coverageNoiseStrength);
        forestNoiseFrequency = Mathf.Max(0f, forestNoiseFrequency);
        forestThreshold = Mathf.Clamp01(forestThreshold);
        forestBlendRange = Mathf.Max(0.001f, forestBlendRange);
        channel = (InfinitMeshTerrain.SplatChannel)Mathf.Clamp((int)channel, 0, 7);

        if (prefabVariations == null)
        {
            prefabVariations = new List<GameObject>();
        }
    }

    private int CountPrefabVariations()
    {
        int variationCount = CountValidPrefabVariations();
        return variationCount > 0 || prefab == null ? variationCount : 1;
    }

    private int CountValidPrefabVariations()
    {
        if (prefabVariations == null)
        {
            return 0;
        }

        int count = 0;
        for (int i = 0; i < prefabVariations.Count; i++)
        {
            if (prefabVariations[i] != null)
            {
                count++;
            }
        }

        return count;
    }

    private static int PositiveModulo(int value, int modulo)
    {
        if (modulo <= 0)
        {
            return 0;
        }

        int result = value % modulo;
        return result < 0 ? result + modulo : result;
    }
}
