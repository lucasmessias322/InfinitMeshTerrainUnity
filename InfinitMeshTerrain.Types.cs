using System;
using Unity.Mathematics;
using UnityEngine;

public partial class InfinitMeshTerrain
{
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

    [Serializable]
    public enum SplatChannel
    {
        [InspectorName("Splat Map 0 / R")] Map0R,
        [InspectorName("Splat Map 0 / G")] Map0G,
        [InspectorName("Splat Map 0 / B")] Map0B,
        [InspectorName("Splat Map 0 / A")] Map0A,
        [InspectorName("Splat Map 1 / R")] Map1R,
        [InspectorName("Splat Map 1 / G")] Map1G,
        [InspectorName("Splat Map 1 / B")] Map1B,
        [InspectorName("Splat Map 1 / A")] Map1A
    }

    [Serializable]
    private struct TerrainHeightLayer
    {
        public TerrainHeightLayer(string layerName, SplatChannel channel, float startHeight, float blendRange)
        {
            this.layerName = layerName;
            texture = null;
            textureScale = Vector2.one;
            normalTexture = null;
            normalScale = 1f;
            smoothness = 0f;
            color = Color.white;
            this.channel = channel;
            this.startHeight = startHeight;
            this.blendRange = blendRange;
        }

        public string layerName;
        public Texture2D texture;
        public Vector2 textureScale;
        public Texture2D normalTexture;
        [Range(0f, 4f)] public float normalScale;
        [Range(0f, 1f)] public float smoothness;
        public Color color;
        public SplatChannel channel;
        public float startHeight;
        [Min(0f)] public float blendRange;
    }

    private struct SlopeTextureSettings
    {
        public bool Enabled;
        public SplatChannel Channel;
        public float StartAngle;
        public float FullAngle;
        public float Strength;
    }

    private struct TerrainSettings
    {
        public float2 NoiseOffset;
        public int TerrainSeed;
        public float MinHeight;
        public float MaxHeight;
        public int UseTerraces;
        public float TerraceStepHeight;
        public float TerraceBlendRange;
        public float TerraceStrength;
    }

    private struct TerrainHeightNoiseLayerData
    {
        public int Operation;
        public int NoiseShape;
        public float Frequency;
        public float Amplitude;
        public int Octaves;
        public float Lacunarity;
        public float Persistence;
        public float2 Offset;
        public float Threshold;
        public float BlendRange;
        public int SplineSampleOffset;
        public int SplineSampleCount;
        public float SplineInfluence;
        public int MaskSplineSampleOffset;
        public int MaskSplineSampleCount;
        public int MaskSplineInputLayerIndex;
        public float MaskSplineInfluence;
    }

    private struct GrassInstanceData
    {
        private const float TwoPi = 6.2831855f;

        public float4 PositionScale;
        public uint4 Packed;

        public static GrassInstanceData Create(
            float3 position,
            float height,
            float3 normal,
            float yaw,
            float3 baseColor,
            float3 tipColor,
            float width,
            float packedWidthScale)
        {
            return new GrassInstanceData
            {
                PositionScale = new float4(position, height),
                Packed = new uint4(
                    PackNormalYaw(normal, yaw),
                    PackColorWidth(baseColor, width, packedWidthScale),
                    PackColor(tipColor),
                    0u)
            };
        }

        private static uint PackNormalYaw(float3 normal, float yaw)
        {
            float3 normalized = math.normalizesafe(normal, new float3(0f, 1f, 0f));
            uint normalX = PackUnorm8(normalized.x * 0.5f + 0.5f);
            uint normalZ = PackUnorm8(normalized.z * 0.5f + 0.5f);
            float yaw01 = yaw / TwoPi;
            yaw01 -= math.floor(yaw01);
            uint packedYaw = (uint)math.round(math.saturate(yaw01) * 65535f);
            return normalX | (normalZ << 8) | (packedYaw << 16);
        }

        private static uint PackColorWidth(float3 color, float width, float packedWidthScale)
        {
            float widthScale = math.max(0.0001f, packedWidthScale);
            return PackColor(color) | (PackUnorm8(width / widthScale) << 24);
        }

        private static uint PackColor(float3 color)
        {
            return PackUnorm8(color.x)
                | (PackUnorm8(color.y) << 8)
                | (PackUnorm8(color.z) << 16);
        }

        private static uint PackUnorm8(float value)
        {
            return (uint)math.round(math.saturate(value) * 255f);
        }
    }

    private struct GrassTerrainLayerData
    {
        public int Channel;
        public float StartHeight;
        public float BlendRange;
    }

    private struct TerrainSplatLayerData
    {
        public int Channel;
        public float StartHeight;
        public float BlendRange;
    }

    private struct TerrainBiomeLayerColorJobData
    {
        public float4 DistanceRange;
        public int HasLayerColorMask;
        public float4 LayerColor0;
        public float4 LayerColor1;
        public float4 LayerColor2;
        public float4 LayerColor3;
        public float4 LayerColor4;
        public float4 LayerColor5;
        public float4 LayerColor6;
        public float4 LayerColor7;
    }

    private struct GrassBiomeData
    {
        public float4 DistanceRange;
        public float4 GrassBaseColor;
        public float4 GrassTipColor;
        public float4 GrassSettings;
    }

    private struct GrassBiomeSample
    {
        public float3 GrassBaseColor;
        public float3 GrassTipColor;
        public float DensityMultiplier;
        public float BladeHeightMultiplier;
        public float BladeWidthMultiplier;
        public float ColorVariation;
    }

    private struct BiomeSamplingSettings
    {
        public int Count;
        public int Seed;
        public int UseNoise;
        public float BlendDistance;
        public float NoiseAmplitude;
        public float NoiseFrequency;
        public int NoiseOctaves;
        public float NoiseLacunarity;
        public float NoisePersistence;
        public float2 NoiseOffset;
        public float TerrainChunkSize;
        public float BiomeSampleSpacing;
    }

    private sealed class TerrainBiomeLayerColorData
    {
        public float4 DistanceRange;
        public bool[] HasLayerColor;
        public Color[] LayerColors;
    }

    private struct TerrainBiomeLayerColorBlend
    {
        public int PrimaryIndex;
        public int SecondaryIndex;
        public float SecondaryWeight;
    }

    private struct GrassBuildSettings
    {
        public int TerrainSeed;
        public int GrassSeed;
        public int Channel;
        public float DensityPerSquareMeter;
        public float CellSize;
        public float Jitter;
        public int MaxInstancesPerCell;
        public float LayerThreshold;
        public float MinHeight;
        public float MaxHeight;
        public float HeightFadeRange;
        public float MinSlopeAngle;
        public float MaxSlopeAngle;
        public float SlopeFadeRange;
        public float MinBladeHeight;
        public float MaxBladeHeight;
        public float MinBladeWidth;
        public float MaxBladeWidth;
        public float ColorVariation;
        public float MaxBiomeDensityMultiplier;
        public float MaxBiomeBladeHeightMultiplier;
        public float MaxBiomeBladeWidthMultiplier;
        public float NormalAlignment;
        public float SurfaceOffset;
        public float CoverageNoiseFrequency;
        public float CoverageNoiseStrength;
        public float PackedWidthScale;
    }

    private struct GrassChunkBounds
    {
        public float3 Min;
        public float3 Max;
    }
}
