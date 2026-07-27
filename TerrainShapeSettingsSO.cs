using System;
using UnityEngine;

[CreateAssetMenu(fileName = "TerrainShapeSettings", menuName = "Procedural Terrain/Terrain Shape Settings")]
public sealed class TerrainShapeSettingsSO : ScriptableObject
{
    public const float DefaultHeightMultiplier = 800f;
    public const int DefaultTerrainSeed = 1337;
    public const float DefaultContinentFrequency = 0.00012f;
    public const float DefaultDomainWarpFrequency = 0.00035f;
    public const float DefaultDomainWarpStrength = 300f;
    public const float DefaultBiomeFrequency = 0.0003f;
    public const float DefaultRidgeFrequency = 0.00065f;
    public const float DefaultDetailFrequency = 0.0035f;
    public const float DefaultSeaCoverage = 0.3f;
    public const float DefaultMountainStart = 0.58f;
    public const float DefaultPlainsStrength = 0.2f;
    public const float DefaultHillsStrength = 0.18f;
    public const float DefaultMountainStrength = 0.28f;
    public const float DefaultCliffStrength = 0.06f;
    public const float DefaultDetailStrength = 0.06f;
    public const float DefaultTerraceStrength = 0.05f;
    public const int DefaultTerraceSteps = 7;
    public const float DefaultTerrainSplineInfluence = 1f;
    public const float DefaultNoiseLayerInfluence = 1f;

    public static readonly Vector2 DefaultNoiseOffset = new Vector2(-50000f, 50000f);

    public event Action Changed;

    [Header("Height")]
    [SerializeField, Min(1f)] private float heightMultiplier = DefaultHeightMultiplier;
    [SerializeField] private Vector2 noiseOffset = DefaultNoiseOffset;
    [SerializeField] private int terrainSeed = DefaultTerrainSeed;

    [Header("Frequencies")]
    [SerializeField, Min(0.000001f)] private float continentFrequency = DefaultContinentFrequency;
    [SerializeField, Min(0.000001f)] private float domainWarpFrequency = DefaultDomainWarpFrequency;
    [SerializeField, Min(0f)] private float domainWarpStrength = DefaultDomainWarpStrength;
    [SerializeField, Min(0.000001f)] private float biomeFrequency = DefaultBiomeFrequency;
    [SerializeField, Min(0.000001f)] private float ridgeFrequency = DefaultRidgeFrequency;
    [SerializeField, Min(0.000001f)] private float detailFrequency = DefaultDetailFrequency;

    [Header("Shape")]
    [SerializeField, Range(0f, 0.9f)] private float seaCoverage = DefaultSeaCoverage;
    [SerializeField, Range(0f, 1f)] private float mountainStart = DefaultMountainStart;
    [SerializeField, Min(0f)] private float plainsStrength = DefaultPlainsStrength;
    [SerializeField, Min(0f)] private float hillsStrength = DefaultHillsStrength;
    [SerializeField, Min(0f)] private float mountainStrength = DefaultMountainStrength;
    [SerializeField, Min(0f)] private float cliffStrength = DefaultCliffStrength;
    [SerializeField, Min(0f)] private float detailStrength = DefaultDetailStrength;
    [SerializeField, Range(0f, 1f)] private float terraceStrength = DefaultTerraceStrength;
    [SerializeField, Min(1)] private int terraceSteps = DefaultTerraceSteps;

    [Header("External Shape Data")]
    [SerializeField, Range(0f, 1f)] private float terrainSplineInfluence = DefaultTerrainSplineInfluence;
    [SerializeField] private TerrainSplinesSO terrainSplines;
    [SerializeField, Range(0f, 1f), InspectorName("Noise Layer Influence")] private float noiseLayerInfluence = DefaultNoiseLayerInfluence;
    [SerializeField] private NoiseLayersSO noiseSettings;

    public float HeightMultiplier => Mathf.Max(1f, heightMultiplier);
    public Vector2 NoiseOffset => noiseOffset;
    public int TerrainSeed => terrainSeed;
    public float ContinentFrequency => Mathf.Max(0.000001f, continentFrequency);
    public float DomainWarpFrequency => Mathf.Max(0.000001f, domainWarpFrequency);
    public float DomainWarpStrength => Mathf.Max(0f, domainWarpStrength);
    public float BiomeFrequency => Mathf.Max(0.000001f, biomeFrequency);
    public float RidgeFrequency => Mathf.Max(0.000001f, ridgeFrequency);
    public float DetailFrequency => Mathf.Max(0.000001f, detailFrequency);
    public float SeaCoverage => Mathf.Clamp(seaCoverage, 0f, 0.9f);
    public float MountainStart => Mathf.Clamp01(mountainStart);
    public float PlainsStrength => Mathf.Max(0f, plainsStrength);
    public float HillsStrength => Mathf.Max(0f, hillsStrength);
    public float MountainStrength => Mathf.Max(0f, mountainStrength);
    public float CliffStrength => Mathf.Max(0f, cliffStrength);
    public float DetailStrength => Mathf.Max(0f, detailStrength);
    public float TerraceStrength => Mathf.Clamp01(terraceStrength);
    public int TerraceSteps => Mathf.Max(1, terraceSteps);
    public float TerrainSplineInfluence => Mathf.Clamp01(terrainSplineInfluence);
    public TerrainSplinesSO TerrainSplines => terrainSplines;
    public float NoiseLayerInfluence => Mathf.Clamp01(noiseLayerInfluence);
    public NoiseLayersSO NoiseSettings => noiseSettings;

    public void ValidateValues()
    {
        heightMultiplier = Mathf.Max(1f, heightMultiplier);
        continentFrequency = Mathf.Max(0.000001f, continentFrequency);
        domainWarpFrequency = Mathf.Max(0.000001f, domainWarpFrequency);
        domainWarpStrength = Mathf.Max(0f, domainWarpStrength);
        biomeFrequency = Mathf.Max(0.000001f, biomeFrequency);
        ridgeFrequency = Mathf.Max(0.000001f, ridgeFrequency);
        detailFrequency = Mathf.Max(0.000001f, detailFrequency);
        seaCoverage = Mathf.Clamp(seaCoverage, 0f, 0.9f);
        mountainStart = Mathf.Clamp01(mountainStart);
        plainsStrength = Mathf.Max(0f, plainsStrength);
        hillsStrength = Mathf.Max(0f, hillsStrength);
        mountainStrength = Mathf.Max(0f, mountainStrength);
        cliffStrength = Mathf.Max(0f, cliffStrength);
        detailStrength = Mathf.Max(0f, detailStrength);
        terraceStrength = Mathf.Clamp01(terraceStrength);
        terraceSteps = Mathf.Max(1, terraceSteps);
        terrainSplineInfluence = Mathf.Clamp01(terrainSplineInfluence);
        noiseLayerInfluence = Mathf.Clamp01(noiseLayerInfluence);
    }

    private void OnValidate()
    {
        ValidateValues();
        Changed?.Invoke();
    }
}
