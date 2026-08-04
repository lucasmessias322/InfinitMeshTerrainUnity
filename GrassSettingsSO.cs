using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Serialization;

[CreateAssetMenu(fileName = "GrassSettings", menuName = "Procedural Terrain/Grass Settings")]
public sealed class GrassSettingsSO : ScriptableObject
{
    private const int MaxSplatChannelCount = 8;

    public const float DefaultDetailDistance = 1536f;
    public const float DefaultFadeStartDistance = 1152f;
    public const float DefaultStreamingCellSize = 64f;
    public const float DefaultDetailCellSize = 2f;
    public const float DefaultDensityPerSquareMeter = 0.25f;
    public const int DefaultMaxInstancesPerGrassCell = 65536;
    public const int DefaultMaxInstancesPerChunk = DefaultMaxInstancesPerGrassCell;
    public const int MaxAllowedInstancesPerGrassCell = 65536;
    public const int DefaultMaxInstancesPerCell = 2;
    public const float DefaultJitter = 0.92f;
    public const float DefaultLayerThreshold = 0.18f;
    public const float DefaultWaterPadding = 1f;
    public const float DefaultMinHeight = 0f;
    public const float DefaultMaxHeight = 520f;
    public const float DefaultHeightFadeRange = 32f;
    public const float DefaultMinSlopeAngle = 0f;
    public const float DefaultMaxSlopeAngle = 38f;
    public const float DefaultSlopeFadeRange = 8f;
    public const float DefaultBladeHeight = 1.6f;
    public const float DefaultBladeHeightVariation = 0.45f;
    public const float DefaultBladeWidth = 0.12f;
    public const float DefaultBladeWidthVariation = 0.35f;
    public const float DefaultMinBladeHeight = DefaultBladeHeight * (1f - DefaultBladeHeightVariation);
    public const float DefaultMaxBladeHeight = DefaultBladeHeight * (1f + DefaultBladeHeightVariation);
    public const float DefaultMinBladeWidth = DefaultBladeWidth * (1f - DefaultBladeWidthVariation);
    public const float DefaultMaxBladeWidth = DefaultBladeWidth * (1f + DefaultBladeWidthVariation);
    public const float DefaultColorVariation = 0.18f;
    public const float DefaultNormalAlignment = 0.85f;
    public const float DefaultSurfaceOffset = 0.03f;
    public const float DefaultCoverageNoiseFrequency = 0.0075f;
    public const float DefaultCoverageNoiseStrength = 0.35f;
    public const float DefaultWindStrength = 0.35f;
    public const float DefaultWindSpeed = 1.6f;
    public const float DefaultTrampleRadius = 2.4f;
    public const float DefaultTrampleStrength = 1.15f;
    public const float DefaultTrampleFlatten = 0.85f;

    public static readonly Vector2 DefaultWindDirection = new Vector2(1f, 0.25f);

    public event Action Changed;

    [Header("Rendering")]
    [SerializeField] private bool enableGrass = true;
    [SerializeField] private Material material;
    [SerializeField] private Mesh mesh;
    [SerializeField] private ShadowCastingMode shadowCastingMode = ShadowCastingMode.Off;
    [SerializeField] private bool receiveShadows = true;
    [SerializeField] private bool unloadOutsideDetailDistance = true;

    [Header("Distance")]
    [SerializeField, Min(1f)] private float detailDistance = DefaultDetailDistance;
    [SerializeField, Min(0f)] private float fadeStartDistance = DefaultFadeStartDistance;

    [Header("Placement")]
    [SerializeField, Min(8f)] private float streamingCellSize = DefaultStreamingCellSize;
    [SerializeField, Min(0.1f)] private float detailCellSize = DefaultDetailCellSize;
    [SerializeField, Min(0f)] private float densityPerSquareMeter = DefaultDensityPerSquareMeter;
    [SerializeField, FormerlySerializedAs("maxInstancesPerChunk"), Min(0)] private int maxInstancesPerGrassCell = DefaultMaxInstancesPerGrassCell;
    [SerializeField, Range(1, 8)] private int maxInstancesPerCell = DefaultMaxInstancesPerCell;
    [SerializeField, Range(0f, 1f)] private float jitter = DefaultJitter;
    [SerializeField] private InfinitMeshTerrain.SplatChannel channel = InfinitMeshTerrain.SplatChannel.Map0G;
    [SerializeField, Range(0f, 1f)] private float layerThreshold = DefaultLayerThreshold;
    [SerializeField] private bool avoidWater = true;
    [SerializeField, Min(0f)] private float waterPadding = DefaultWaterPadding;

    [Header("Height And Slope")]
    [SerializeField, Min(0f)] private float minHeight = DefaultMinHeight;
    [SerializeField, Min(0f)] private float maxHeight = DefaultMaxHeight;
    [SerializeField, Min(0.01f)] private float heightFadeRange = DefaultHeightFadeRange;
    [SerializeField, Range(0f, 90f)] private float minSlopeAngle = DefaultMinSlopeAngle;
    [SerializeField, Range(0f, 90f)] private float maxSlopeAngle = DefaultMaxSlopeAngle;
    [SerializeField, Min(0.01f)] private float slopeFadeRange = DefaultSlopeFadeRange;

    [Header("Blade")]
    [SerializeField, HideInInspector] private bool bladeSizeRangeInitialized;
    [SerializeField, HideInInspector] private float bladeHeight = DefaultBladeHeight;
    [SerializeField, HideInInspector] private float bladeHeightVariation = DefaultBladeHeightVariation;
    [SerializeField, HideInInspector] private float bladeWidth = DefaultBladeWidth;
    [SerializeField, HideInInspector] private float bladeWidthVariation = DefaultBladeWidthVariation;
    [SerializeField, Min(0.01f)] private float minBladeHeight = DefaultMinBladeHeight;
    [SerializeField, Min(0.01f)] private float maxBladeHeight = DefaultMaxBladeHeight;
    [SerializeField, Min(0.005f)] private float minBladeWidth = DefaultMinBladeWidth;
    [SerializeField, Min(0.005f)] private float maxBladeWidth = DefaultMaxBladeWidth;
    [SerializeField, Range(0f, 1f)] private float colorVariation = DefaultColorVariation;
    [SerializeField, Range(0f, 1f)] private float normalAlignment = DefaultNormalAlignment;
    [SerializeField, Min(0f)] private float surfaceOffset = DefaultSurfaceOffset;

    [Header("Coverage Noise")]
    [SerializeField, Min(0f)] private float coverageNoiseFrequency = DefaultCoverageNoiseFrequency;
    [SerializeField, Range(0f, 1f)] private float coverageNoiseStrength = DefaultCoverageNoiseStrength;

    [Header("Wind")]
    [SerializeField] private Vector2 windDirection = DefaultWindDirection;
    [SerializeField, Min(0f)] private float windStrength = DefaultWindStrength;
    [SerializeField, Min(0f)] private float windSpeed = DefaultWindSpeed;

    [Header("Interaction")]
    [SerializeField, Min(0f)] private float trampleRadius = DefaultTrampleRadius;
    [SerializeField, Min(0f)] private float trampleStrength = DefaultTrampleStrength;
    [SerializeField, Range(0f, 1f)] private float trampleFlatten = DefaultTrampleFlatten;

    public bool EnableGrass => enableGrass;
    public Material Material => material;
    public Mesh Mesh => mesh;
    public ShadowCastingMode ShadowCastingMode => shadowCastingMode;
    public bool ReceiveShadows => receiveShadows;
    public bool UnloadOutsideDetailDistance => unloadOutsideDetailDistance;
    public float DetailDistance => Mathf.Max(1f, detailDistance);
    public float FadeStartDistance => GetValidatedFadeStartDistance();
    public float StreamingCellSize => Mathf.Max(8f, streamingCellSize);
    public float DetailCellSize => Mathf.Max(0.1f, detailCellSize);
    public float DensityPerSquareMeter => Mathf.Max(0f, densityPerSquareMeter);
    public int MaxInstancesPerGrassCell => Mathf.Clamp(maxInstancesPerGrassCell, 0, MaxAllowedInstancesPerGrassCell);
    public int MaxInstancesPerChunk => MaxInstancesPerGrassCell;
    public int MaxInstancesPerCell => Mathf.Clamp(maxInstancesPerCell, 1, 8);
    public float Jitter => Mathf.Clamp01(jitter);
    public int ChannelIndex => Mathf.Clamp((int)channel, 0, MaxSplatChannelCount - 1);
    public float LayerThreshold => Mathf.Clamp01(layerThreshold);
    public bool AvoidWater => avoidWater;
    public float WaterPadding => Mathf.Max(0f, waterPadding);
    public float MinHeight => Mathf.Max(0f, minHeight);
    public float MaxHeight => Mathf.Max(MinHeight + 0.01f, maxHeight);
    public float HeightFadeRange => Mathf.Max(0.01f, heightFadeRange);
    public float MinSlopeAngle => Mathf.Clamp(minSlopeAngle, 0f, 90f);
    public float MaxSlopeAngle => Mathf.Clamp(maxSlopeAngle, MinSlopeAngle, 90f);
    public float SlopeFadeRange => Mathf.Max(0.01f, slopeFadeRange);
    public float MinBladeHeight => Mathf.Max(0.01f, Mathf.Min(minBladeHeight, maxBladeHeight));
    public float MaxBladeHeight => Mathf.Max(MinBladeHeight, maxBladeHeight);
    public float MinBladeWidth => Mathf.Max(0.005f, Mathf.Min(minBladeWidth, maxBladeWidth));
    public float MaxBladeWidth => Mathf.Max(MinBladeWidth, maxBladeWidth);
    public float BladeHeight => (MinBladeHeight + MaxBladeHeight) * 0.5f;
    public float BladeHeightVariation => CalculateLegacyVariation(MinBladeHeight, MaxBladeHeight);
    public float BladeWidth => (MinBladeWidth + MaxBladeWidth) * 0.5f;
    public float BladeWidthVariation => CalculateLegacyVariation(MinBladeWidth, MaxBladeWidth);
    public float ColorVariation => Mathf.Clamp01(colorVariation);
    public float NormalAlignment => Mathf.Clamp01(normalAlignment);
    public float SurfaceOffset => Mathf.Max(0f, surfaceOffset);
    public float CoverageNoiseFrequency => Mathf.Max(0f, coverageNoiseFrequency);
    public float CoverageNoiseStrength => Mathf.Clamp01(coverageNoiseStrength);
    public Vector2 WindDirection => windDirection;
    public float WindStrength => Mathf.Max(0f, windStrength);
    public float WindSpeed => Mathf.Max(0f, windSpeed);
    public float TrampleRadius => Mathf.Max(0f, trampleRadius);
    public float TrampleStrength => Mathf.Max(0f, trampleStrength);
    public float TrampleFlatten => Mathf.Clamp01(trampleFlatten);

    public static GrassSettingsSO CreateRuntimeDefault()
    {
        GrassSettingsSO settings = CreateInstance<GrassSettingsSO>();
        settings.hideFlags = HideFlags.HideAndDontSave;
        settings.ValidateValues();
        return settings;
    }

    public void ValidateValues()
    {
        detailDistance = Mathf.Max(1f, detailDistance);
        fadeStartDistance = GetValidatedFadeStartDistance();
        streamingCellSize = Mathf.Max(8f, streamingCellSize);
        detailCellSize = Mathf.Max(0.1f, detailCellSize);
        densityPerSquareMeter = Mathf.Max(0f, densityPerSquareMeter);
        maxInstancesPerGrassCell = Mathf.Clamp(maxInstancesPerGrassCell, 0, MaxAllowedInstancesPerGrassCell);
        maxInstancesPerCell = Mathf.Clamp(maxInstancesPerCell, 1, 8);
        jitter = Mathf.Clamp01(jitter);
        channel = (InfinitMeshTerrain.SplatChannel)Mathf.Clamp((int)channel, 0, MaxSplatChannelCount - 1);
        layerThreshold = Mathf.Clamp01(layerThreshold);
        waterPadding = Mathf.Max(0f, waterPadding);
        minHeight = Mathf.Max(0f, minHeight);
        maxHeight = Mathf.Max(minHeight + 0.01f, maxHeight);
        heightFadeRange = Mathf.Max(0.01f, heightFadeRange);
        minSlopeAngle = Mathf.Clamp(minSlopeAngle, 0f, 90f);
        maxSlopeAngle = Mathf.Clamp(maxSlopeAngle, minSlopeAngle, 90f);
        slopeFadeRange = Mathf.Max(0.01f, slopeFadeRange);
        MigrateBladeSizeRangeIfNeeded();
        minBladeHeight = Mathf.Max(0.01f, minBladeHeight);
        maxBladeHeight = Mathf.Max(minBladeHeight, maxBladeHeight);
        minBladeWidth = Mathf.Max(0.005f, minBladeWidth);
        maxBladeWidth = Mathf.Max(minBladeWidth, maxBladeWidth);
        SyncLegacyBladeSizeFields();
        colorVariation = Mathf.Clamp01(colorVariation);
        normalAlignment = Mathf.Clamp01(normalAlignment);
        surfaceOffset = Mathf.Max(0f, surfaceOffset);
        coverageNoiseFrequency = Mathf.Max(0f, coverageNoiseFrequency);
        coverageNoiseStrength = Mathf.Clamp01(coverageNoiseStrength);
        windStrength = Mathf.Max(0f, windStrength);
        windSpeed = Mathf.Max(0f, windSpeed);
        trampleRadius = Mathf.Max(0f, trampleRadius);
        trampleStrength = Mathf.Max(0f, trampleStrength);
        trampleFlatten = Mathf.Clamp01(trampleFlatten);

        if (material != null)
        {
            material.enableInstancing = true;
        }
    }

    private void MigrateBladeSizeRangeIfNeeded()
    {
        if (bladeSizeRangeInitialized)
        {
            return;
        }

        float validatedBladeHeight = Mathf.Max(0.01f, bladeHeight);
        float validatedBladeHeightVariation = Mathf.Clamp01(bladeHeightVariation);
        float minBladeHeightMultiplier = Mathf.Max(0.05f, 1f - validatedBladeHeightVariation);
        float maxBladeHeightMultiplier = Mathf.Max(0.05f, 1f + validatedBladeHeightVariation);
        minBladeHeight = validatedBladeHeight * minBladeHeightMultiplier;
        maxBladeHeight = validatedBladeHeight * maxBladeHeightMultiplier;

        float validatedBladeWidth = Mathf.Max(0.005f, bladeWidth);
        float validatedBladeWidthVariation = Mathf.Clamp01(bladeWidthVariation);
        float minBladeWidthMultiplier = Mathf.Max(0.05f, 1f - validatedBladeWidthVariation);
        float maxBladeWidthMultiplier = Mathf.Max(0.05f, 1f + validatedBladeWidthVariation);
        minBladeWidth = validatedBladeWidth * minBladeWidthMultiplier;
        maxBladeWidth = validatedBladeWidth * maxBladeWidthMultiplier;

        bladeSizeRangeInitialized = true;
    }

    private void SyncLegacyBladeSizeFields()
    {
        bladeHeight = BladeHeight;
        bladeHeightVariation = BladeHeightVariation;
        bladeWidth = BladeWidth;
        bladeWidthVariation = BladeWidthVariation;
    }

    private static float CalculateLegacyVariation(float minValue, float maxValue)
    {
        float center = (Mathf.Max(0f, minValue) + Mathf.Max(0f, maxValue)) * 0.5f;
        return center > 0.0001f
            ? Mathf.Clamp01((Mathf.Max(minValue, maxValue) - center) / center)
            : 0f;
    }

    private float GetValidatedFadeStartDistance()
    {
        float validatedDetailDistance = Mathf.Max(1f, detailDistance);
        float validatedFadeStartDistance = Mathf.Clamp(fadeStartDistance, 0f, validatedDetailDistance);
        return Mathf.Approximately(validatedFadeStartDistance, validatedDetailDistance)
            ? validatedDetailDistance * 0.8f
            : validatedFadeStartDistance;
    }

    private void OnValidate()
    {
        ValidateValues();
        Changed?.Invoke();
    }

    private void OnEnable()
    {
        ValidateValues();
    }
}
