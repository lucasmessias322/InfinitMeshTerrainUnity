using System;
using System.Collections.Generic;
using UnityEngine;

[CreateAssetMenu(fileName = "TerrainBiome", menuName = "Procedural Terrain/Terrain Biome")]
public sealed class TerrainBiomeSO : ScriptableObject
{
    public const float DefaultMaxDistanceFromCenter = 2048f;
    public static readonly Color DefaultGrassColor = new Color(0.32f, 0.42f, 0.07f, 1f);

    public event Action Changed;

    [SerializeField] private string biomeName = "Biome";
    [SerializeField, Min(0f)] private float minDistanceFromCenter;
    [SerializeField, Min(0f)] private float maxDistanceFromCenter = DefaultMaxDistanceFromCenter;
    [SerializeField, Min(0f)] private float selectionWeight = 1f;
    [SerializeField] private Color grassColor = DefaultGrassColor;
    [SerializeField, HideInInspector] private bool grassSettingsInitialized;
    [SerializeField] private BiomeGrassSettings grass = BiomeGrassSettings.Default;
    [SerializeField, HideInInspector] private bool terrainLayerColorsInitialized;
    [SerializeField] private List<TerrainBiomeLayerColor> terrainLayerColors = new List<TerrainBiomeLayerColor>();

    public string BiomeName => string.IsNullOrWhiteSpace(biomeName) ? name : biomeName;
    public float MinDistanceFromCenter => Mathf.Max(0f, minDistanceFromCenter);
    public float MaxDistanceFromCenter => Mathf.Max(MinDistanceFromCenter, maxDistanceFromCenter);
    public float SelectionWeight => Mathf.Max(0f, selectionWeight);
    public Color GrassColor => grassColor;
    public BiomeGrassSettings Grass => grass;
    public IReadOnlyList<TerrainBiomeLayerColor> TerrainLayerColors => terrainLayerColors;

    public void ValidateValues()
    {
        if (string.IsNullOrWhiteSpace(biomeName))
        {
            biomeName = name;
        }

        minDistanceFromCenter = Mathf.Max(0f, minDistanceFromCenter);
        maxDistanceFromCenter = Mathf.Max(minDistanceFromCenter, maxDistanceFromCenter);
        selectionWeight = Mathf.Max(0f, selectionWeight);
        grassColor.a = Mathf.Clamp01(grassColor.a);

        if (!grassSettingsInitialized)
        {
            grass = BiomeGrassSettings.Default;
            grassSettingsInitialized = true;
        }

        grass.Validate();

        if (terrainLayerColors == null)
        {
            terrainLayerColors = new List<TerrainBiomeLayerColor>();
        }

        if (!terrainLayerColorsInitialized)
        {
            if (terrainLayerColors.Count == 0)
            {
                terrainLayerColors.Add(new TerrainBiomeLayerColor(InfinitMeshTerrain.SplatChannel.Map0G, grassColor));
            }

            terrainLayerColorsInitialized = true;
        }

        for (int i = 0; i < terrainLayerColors.Count; i++)
        {
            TerrainBiomeLayerColor layerColor = terrainLayerColors[i];
            layerColor.Validate();
            terrainLayerColors[i] = layerColor;
        }
    }

    private void OnValidate()
    {
        ValidateValues();
        Changed?.Invoke();
    }
}

[Serializable]
public struct BiomeGrassSettings
{
    public static readonly BiomeGrassSettings Default = new BiomeGrassSettings
    {
        enabled = true,
        densityMultiplier = 1f,
        bladeHeightMultiplier = 1f,
        bladeWidthMultiplier = 1f,
        colorVariation = 0f
    };

    [InspectorName("Enable Grass")] public bool enabled;
    [Min(0f)] public float densityMultiplier;
    [Min(0.01f)] public float bladeHeightMultiplier;
    [Min(0.01f)] public float bladeWidthMultiplier;
    [Range(0f, 1f)] public float colorVariation;

    public bool Enabled => enabled;
    public float DensityMultiplier => enabled ? Mathf.Max(0f, densityMultiplier) : 0f;
    public float BladeHeightMultiplier => Mathf.Max(0.01f, bladeHeightMultiplier);
    public float BladeWidthMultiplier => Mathf.Max(0.01f, bladeWidthMultiplier);
    public float ColorVariation => Mathf.Clamp01(colorVariation);

    public void Validate()
    {
        densityMultiplier = Mathf.Max(0f, densityMultiplier);
        bladeHeightMultiplier = Mathf.Max(0.01f, bladeHeightMultiplier);
        bladeWidthMultiplier = Mathf.Max(0.01f, bladeWidthMultiplier);
        colorVariation = Mathf.Clamp01(colorVariation);
    }
}

[Serializable]
public struct TerrainBiomeLayerColor
{
    public TerrainBiomeLayerColor(InfinitMeshTerrain.SplatChannel channel, Color color)
    {
        enabled = true;
        this.channel = channel;
        this.color = color;
    }

    public bool enabled;
    public InfinitMeshTerrain.SplatChannel channel;
    public Color color;

    public bool Enabled => enabled;
    public int ChannelIndex => Mathf.Clamp((int)channel, 0, 7);
    public Color Color => color;

    public void Validate()
    {
        channel = (InfinitMeshTerrain.SplatChannel)Mathf.Clamp((int)channel, 0, 7);
        color.a = Mathf.Clamp01(color.a);
    }
}
