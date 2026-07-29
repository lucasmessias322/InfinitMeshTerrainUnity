using System;
using UnityEngine;

[CreateAssetMenu(fileName = "TerrainShapeSettings", menuName = "Procedural Terrain/Terrain Shape Settings")]
public sealed class TerrainShapeSettingsSO : ScriptableObject
{
    public const float DefaultHeightMultiplier = 800f;
    public const int DefaultTerrainSeed = 1337;
    public const float DefaultNoiseFrequency = 0.001f;

    public static readonly Vector2 DefaultNoiseOffset = new Vector2(-50000f, 50000f);

    public event Action Changed;

    [Header("Height")]
    [SerializeField, Min(1f)] private float heightMultiplier = DefaultHeightMultiplier;

    [Header("Simple Noise")]
    [SerializeField, Min(0.000001f)] private float noiseFrequency = DefaultNoiseFrequency;
    [SerializeField] private Vector2 noiseOffset = DefaultNoiseOffset;
    [SerializeField] private int terrainSeed = DefaultTerrainSeed;

    public float HeightMultiplier => Mathf.Max(1f, heightMultiplier);
    public float NoiseFrequency => Mathf.Max(0.000001f, noiseFrequency);
    public Vector2 NoiseOffset => noiseOffset;
    public int TerrainSeed => terrainSeed;

    public void ValidateValues()
    {
        heightMultiplier = Mathf.Max(1f, heightMultiplier);
        noiseFrequency = Mathf.Max(0.000001f, noiseFrequency);
    }

    private void OnValidate()
    {
        ValidateValues();
        Changed?.Invoke();
    }
}
