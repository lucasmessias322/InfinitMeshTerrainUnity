using System;
using System.Collections.Generic;
using UnityEngine;

[CreateAssetMenu(fileName = "TerrainShapeSettings", menuName = "Procedural Terrain/Terrain Shape Settings")]
public sealed class TerrainShapeSettingsSO : ScriptableObject
{
    public const int DefaultTerrainSeed = 1337;
    public const int MaxHeightLayerCount = 16;
    public const int SplineSampleCount = 128;
    public const float DefaultLayeredMaxHeight = 2000f;
    public const float DefaultTerraceStepHeight = 12f;
    public const float DefaultTerraceBlendRange = 0.28f;

    public static readonly Vector2 DefaultNoiseOffset = new Vector2(-50000f, 50000f);

    public event Action Changed;

    [Header("Layered Height")]
    [SerializeField] private bool useLayeredHeight = true;
    [SerializeField, Min(0f)] private float minHeight;
    [SerializeField, Min(1f)] private float maxHeight = DefaultLayeredMaxHeight;
    [SerializeField] private List<TerrainHeightLayerDefinition> heightLayers = CreateDefaultHeightLayers();

    [Header("Continental Spline")]
    [SerializeField] private bool useContinentalSpline = true;
    [SerializeField, Min(0)] private int continentalSplineLayerIndex;
    [SerializeField, Range(0f, 1f)] private float continentalSplineInfluence = 1f;
    [SerializeField] private AnimationCurve continentalSpline = DefaultContinentalSpline();

    [Header("Mountain Placement Spline")]
    [SerializeField] private bool useMountainSpline = true;
    [SerializeField, Min(0)] private int mountainSplineLayerIndex = 1;
    [SerializeField, Min(0)] private int mountainSplineInputLayerIndex;
    [SerializeField, Range(0f, 1f)] private float mountainSplineInfluence = 1f;
    [SerializeField] private AnimationCurve mountainSpline = DefaultMountainSpline();

    [Header("Terraces")]
    [Tooltip("Quantizes the final terrain height into broad, Valheim-like plateaus.")]
    [SerializeField] private bool useTerraces;
    [Tooltip("Vertical distance, in world units, between each terrace plateau.")]
    [SerializeField, Min(0.01f)] private float terraceStepHeight = DefaultTerraceStepHeight;
    [Tooltip("Fraction of each step used by the short smooth ramp into the next plateau.")]
    [SerializeField, Range(0f, 1f)] private float terraceBlendRange = DefaultTerraceBlendRange;
    [Tooltip("Blends between the original height and the terraced height.")]
    [SerializeField, Range(0f, 1f)] private float terraceStrength = 1f;

    [Header("Seed / Offset")]
    [SerializeField] private Vector2 noiseOffset = DefaultNoiseOffset;
    [SerializeField] private int terrainSeed = DefaultTerrainSeed;

    public bool UseLayeredHeight => useLayeredHeight && heightLayers != null && heightLayers.Count > 0;
    public bool UseContinentalSpline => useContinentalSpline && continentalSplineInfluence > 0f && continentalSpline != null && continentalSpline.length > 0;
    public bool UseMountainSpline => useMountainSpline && mountainSplineInfluence > 0f && mountainSpline != null && mountainSpline.length > 0;
    public float MinHeight => minHeight;
    public float MaxHeight => Mathf.Max(minHeight + 1f, maxHeight);
    public IReadOnlyList<TerrainHeightLayerDefinition> HeightLayers => heightLayers;
    public int ContinentalSplineLayerIndex => heightLayers == null || heightLayers.Count == 0
        ? 0
        : Mathf.Clamp(continentalSplineLayerIndex, 0, heightLayers.Count - 1);
    public float ContinentalSplineInfluence => Mathf.Clamp01(continentalSplineInfluence);
    public int MountainSplineLayerIndex => heightLayers == null || heightLayers.Count == 0
        ? 0
        : Mathf.Clamp(mountainSplineLayerIndex, 0, heightLayers.Count - 1);
    public int MountainSplineInputLayerIndex => heightLayers == null || heightLayers.Count == 0
        ? 0
        : Mathf.Clamp(mountainSplineInputLayerIndex, 0, heightLayers.Count - 1);
    public float MountainSplineInfluence => Mathf.Clamp01(mountainSplineInfluence);
    public bool UseTerraces => useTerraces && terraceStepHeight > 0.0001f && terraceStrength > 0f;
    public float TerraceStepHeight => Mathf.Max(0.01f, terraceStepHeight);
    public float TerraceBlendRange => Mathf.Clamp01(terraceBlendRange);
    public float TerraceStrength => UseTerraces ? Mathf.Clamp01(terraceStrength) : 0f;
    public Vector2 NoiseOffset => noiseOffset;
    public int TerrainSeed => terrainSeed;

    public void ValidateValues()
    {
        if (heightLayers == null)
        {
            heightLayers = CreateDefaultHeightLayers();
        }

        while (heightLayers.Count > MaxHeightLayerCount)
        {
            heightLayers.RemoveAt(heightLayers.Count - 1);
        }

        for (int i = 0; i < heightLayers.Count; i++)
        {
            TerrainHeightLayerDefinition layer = heightLayers[i];
            layer.Validate(i);
            heightLayers[i] = layer;
        }

        minHeight = Mathf.Max(0f, minHeight);
        maxHeight = Mathf.Max(minHeight + 1f, maxHeight);
        continentalSplineLayerIndex = heightLayers.Count > 0
            ? Mathf.Clamp(continentalSplineLayerIndex, 0, heightLayers.Count - 1)
            : 0;
        mountainSplineLayerIndex = heightLayers.Count > 0
            ? Mathf.Clamp(mountainSplineLayerIndex, 0, heightLayers.Count - 1)
            : 0;
        mountainSplineInputLayerIndex = heightLayers.Count > 0
            ? Mathf.Clamp(mountainSplineInputLayerIndex, 0, heightLayers.Count - 1)
            : 0;
        continentalSplineInfluence = Mathf.Clamp01(continentalSplineInfluence);
        mountainSplineInfluence = Mathf.Clamp01(mountainSplineInfluence);
        terraceStepHeight = Mathf.Max(0.01f, terraceStepHeight);
        terraceBlendRange = Mathf.Clamp01(terraceBlendRange);
        terraceStrength = Mathf.Clamp01(terraceStrength);
        EnsureCurve(ref continentalSpline, DefaultContinentalSpline());
        EnsureCurve(ref mountainSpline, DefaultMountainSpline());
    }

    [ContextMenu("Reset Height Layers To Minecraft Like Stack")]
    private void ResetHeightLayersToMinecraftLikeStack()
    {
        heightLayers = CreateDefaultHeightLayers();
        ValidateValues();
        Changed?.Invoke();
    }

    [ContextMenu("Enable Valheim Like Terraces")]
    private void EnableValheimLikeTerraces()
    {
        useTerraces = true;
        terraceStepHeight = DefaultTerraceStepHeight;
        terraceBlendRange = DefaultTerraceBlendRange;
        terraceStrength = 1f;
        ValidateValues();
        Changed?.Invoke();
    }

    public float EvaluateContinentalSpline(float input)
    {
        if (!UseContinentalSpline)
        {
            return Mathf.Clamp01(input);
        }

        return continentalSpline.Evaluate(Mathf.Clamp01(input));
    }

    public float EvaluateMountainSpline(float input)
    {
        if (!UseMountainSpline)
        {
            return 1f;
        }

        return mountainSpline.Evaluate(Mathf.Clamp01(input));
    }

    private void OnValidate()
    {
        ValidateValues();
        Changed?.Invoke();
    }

    private static List<TerrainHeightLayerDefinition> CreateDefaultHeightLayers()
    {
        return new List<TerrainHeightLayerDefinition>
        {
            new TerrainHeightLayerDefinition(
                "Base Continental",
                TerrainHeightLayerOperation.Add,
                TerrainHeightNoiseShape.Smooth,
                0.00012f,
                380f,
                5,
                2.02f,
                0.52f,
                Vector2.zero,
                0f,
                0f),
            new TerrainHeightLayerDefinition(
                "Mountains",
                TerrainHeightLayerOperation.Add,
                TerrainHeightNoiseShape.Ridged,
                0.00082f,
                460f,
                5,
                2.1f,
                0.48f,
                new Vector2(1170f, -913f),
                0.58f,
                0.22f),
            new TerrainHeightLayerDefinition(
                "Small Details",
                TerrainHeightLayerOperation.Add,
                TerrainHeightNoiseShape.Signed,
                0.009f,
                28f,
                3,
                2f,
                0.45f,
                new Vector2(-421f, 781f),
                0f,
                0f),
            new TerrainHeightLayerDefinition(
                "Erosion / Smoothing",
                TerrainHeightLayerOperation.Subtract,
                TerrainHeightNoiseShape.Smooth,
                0.0015f,
                130f,
                4,
                2f,
                0.5f,
                new Vector2(263f, 1497f),
                0.3f,
                0.45f)
        };
    }

    private static AnimationCurve DefaultContinentalSpline()
    {
        return CreateCurve(
            new Keyframe(0f, 0f),
            new Keyframe(0.18f, 0.02f),
            new Keyframe(0.32f, 0.08f),
            new Keyframe(0.48f, 0.28f),
            new Keyframe(0.68f, 0.62f),
            new Keyframe(1f, 1f));
    }

    private static AnimationCurve DefaultMountainSpline()
    {
        return CreateCurve(
            new Keyframe(0f, 0f),
            new Keyframe(0.35f, 0f),
            new Keyframe(0.52f, 0.18f),
            new Keyframe(0.68f, 0.72f),
            new Keyframe(1f, 1f));
    }

    private static AnimationCurve CreateCurve(params Keyframe[] keys)
    {
        AnimationCurve curve = new AnimationCurve(keys)
        {
            preWrapMode = WrapMode.ClampForever,
            postWrapMode = WrapMode.ClampForever
        };

        for (int i = 0; i < curve.length; i++)
        {
            curve.SmoothTangents(i, 0f);
        }

        return curve;
    }

    private static void EnsureCurve(ref AnimationCurve curve, AnimationCurve fallback)
    {
        if (curve == null || curve.length == 0)
        {
            curve = fallback;
        }

        Keyframe[] keys = curve.keys;
        for (int i = 0; i < keys.Length; i++)
        {
            keys[i].time = Mathf.Clamp01(keys[i].time);
        }

        Array.Sort(keys, (a, b) => a.time.CompareTo(b.time));
        curve.keys = keys;
        curve.preWrapMode = WrapMode.ClampForever;
        curve.postWrapMode = WrapMode.ClampForever;
    }
}

[Serializable]
public enum TerrainHeightLayerOperation
{
    Add,
    Subtract,
    Max,
    Min
}

[Serializable]
public enum TerrainHeightNoiseShape
{
    Smooth,
    Signed,
    Ridged,
    Billow
}

[Serializable]
public struct TerrainHeightLayerDefinition
{
    public TerrainHeightLayerDefinition(
        string layerName,
        TerrainHeightLayerOperation operation,
        TerrainHeightNoiseShape noiseShape,
        float frequency,
        float amplitude,
        int octaves,
        float lacunarity,
        float persistence,
        Vector2 offset,
        float threshold,
        float blendRange)
    {
        enabled = true;
        this.layerName = layerName;
        this.operation = operation;
        this.noiseShape = noiseShape;
        this.frequency = frequency;
        this.amplitude = amplitude;
        this.octaves = octaves;
        this.lacunarity = lacunarity;
        this.persistence = persistence;
        this.offset = offset;
        this.threshold = threshold;
        this.blendRange = blendRange;
    }

    public bool enabled;
    public string layerName;
    public TerrainHeightLayerOperation operation;
    public TerrainHeightNoiseShape noiseShape;
    [Min(0.000001f)] public float frequency;
    [Min(0f)] public float amplitude;
    [Range(1, 12)] public int octaves;
    [Min(1f)] public float lacunarity;
    [Range(0f, 1f)] public float persistence;
    public Vector2 offset;
    [Range(0f, 1f)] public float threshold;
    [Min(0f)] public float blendRange;

    public bool HasContribution => enabled && amplitude > 0f;

    public void Validate(int fallbackIndex)
    {
        if (string.IsNullOrWhiteSpace(layerName))
        {
            layerName = $"Height Layer {fallbackIndex + 1}";
        }

        operation = (TerrainHeightLayerOperation)Mathf.Clamp((int)operation, 0, 3);
        noiseShape = (TerrainHeightNoiseShape)Mathf.Clamp((int)noiseShape, 0, 3);
        frequency = Mathf.Max(0.000001f, frequency);
        amplitude = Mathf.Max(0f, amplitude);
        octaves = Mathf.Clamp(octaves <= 0 ? 1 : octaves, 1, 12);
        lacunarity = Mathf.Max(1f, lacunarity <= 0f ? 2f : lacunarity);
        persistence = Mathf.Clamp01(persistence <= 0f ? 0.5f : persistence);
        threshold = Mathf.Clamp01(threshold);
        blendRange = Mathf.Clamp(blendRange, 0f, 1f);
    }
}
