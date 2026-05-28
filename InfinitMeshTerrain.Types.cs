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
    private enum SplatChannel
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
            this.channel = channel;
            this.startHeight = startHeight;
            this.blendRange = blendRange;
        }

        public string layerName;
        public Texture2D texture;
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
        public float ContinentFrequency;
        public float DomainWarpFrequency;
        public float DomainWarpStrength;
        public float BiomeFrequency;
        public float RidgeFrequency;
        public float DetailFrequency;
        public float SeaCoverage;
        public float MountainStart;
        public float PlainsStrength;
        public float HillsStrength;
        public float MountainStrength;
        public float CliffStrength;
        public float DetailStrength;
        public float TerraceStrength;
        public int TerraceSteps;
        public float TerrainSplineInfluence;
        public float NoiseLayerInfluence;
    }

    private struct NoiseLayerData
    {
        public float2 Scale;
        public float Amplitude;
        public int Role;
        public int Octaves;
        public float Lacunarity;
        public float Gain;
        public float HeightThreshold;
        public float2 Offset;
        public float BlendRange;
    }
}
