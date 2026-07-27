using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

[CreateAssetMenu(fileName = "TreeSettings", menuName = "Procedural Terrain/Tree Settings")]
public sealed class TreeSettingsSO : ScriptableObject
{
    private static readonly TreePrototypeSettings[] EmptyPrototypes = Array.Empty<TreePrototypeSettings>();

    public const float DefaultTreeDistance = 1536f;
    public const float DefaultCellSize = 28f;
    public const int DefaultMaxInstancesPerChunk = 192;
    public const int DefaultMaxInstancesPerCell = 2;
    public const float DefaultJitter = 0.85f;
    public const int DefaultSeedOffset = 27183;

    public event Action Changed;

    [Header("Rendering")]
    [SerializeField] private bool enableTrees = true;
    [SerializeField, Min(1f)] private float treeDistance = DefaultTreeDistance;
    [SerializeField] private bool unloadOutsideTreeDistance = true;
    [SerializeField] private ShadowCastingMode shadowCastingMode = ShadowCastingMode.On;
    [SerializeField] private bool receiveShadows = true;

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

    public void ValidateValues()
    {
        treeDistance = Mathf.Max(1f, treeDistance);
        cellSize = Mathf.Max(0.1f, cellSize);
        maxInstancesPerChunk = Mathf.Max(0, maxInstancesPerChunk);
        maxInstancesPerCell = Mathf.Clamp(maxInstancesPerCell, 1, 8);
        jitter = Mathf.Clamp01(jitter);

        if (prototypes == null)
        {
            prototypes = new List<TreePrototypeSettings>();
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
public sealed class TreePrototypeSettings
{
    [Header("Prefab")]
    [SerializeField] private GameObject prefab;
    [SerializeField, Min(0f), InspectorName("Density Per Hectare")] private float densityPerHectare = 4f;

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

    public GameObject Prefab => prefab;
    public float DensityPerSquareMeter => Mathf.Max(0f, densityPerHectare) * 0.0001f;
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
    public bool IsSpawnable => prefab != null && DensityPerSquareMeter > 0f;

    public void ValidateValues()
    {
        densityPerHectare = Mathf.Max(0f, densityPerHectare);
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
    }
}
