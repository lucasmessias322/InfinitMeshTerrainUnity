using System;
using System.Collections.Generic;
using UnityEngine;

[CreateAssetMenu(fileName = "NoiseLayers", menuName = "Procedural Terrain/Noise Layers")]
public sealed class NoiseLayersSO : ScriptableObject
{
    [SerializeField] private List<NoiseLayer> noiseLayers = new List<NoiseLayer>();

    public IReadOnlyList<NoiseLayer> NoiseLayers => noiseLayers;

    private void OnValidate()
    {
        for (int i = 0; i < noiseLayers.Count; i++)
        {
            NoiseLayer layer = noiseLayers[i];
            layer.octaves = Mathf.Clamp(layer.octaves <= 0 ? 1 : layer.octaves, 1, 12);
            layer.lacunarity = Mathf.Max(1f, layer.lacunarity <= 0f ? 2f : layer.lacunarity);
            layer.persistence = Mathf.Clamp01(layer.persistence <= 0f ? 0.5f : layer.persistence);
            noiseLayers[i] = layer;
        }
    }
}

[Serializable]
public enum TerrainNoiseRole
{
    Continentalness,
    Erosion,
    HillsNoise,
    PeaksValleys,
    MountainNoise
}

[Serializable]
public struct NoiseLayer
{
    public TerrainNoiseRole role;
    public float scaleX;
    public float scaleY;
    public float amplitude;
    [Range(1, 12)] public int octaves;
    [Min(1f)] public float lacunarity;
    [Range(0f, 1f)] public float persistence;
    public float heightThreshold;
    public Vector2 offset;
    public float blendRange;
}
