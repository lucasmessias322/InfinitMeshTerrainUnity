using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

public partial class InfinitMeshTerrain
{
    [BurstCompile(FloatPrecision.Standard, FloatMode.Fast, CompileSynchronously = false)]
    private struct GenerateTerrainVerticesJob : IJobFor
    {
        [WriteOnly] public NativeArray<Vector3> Vertices;
        [WriteOnly] public NativeArray<Vector3> Normals;
        [WriteOnly] public NativeArray<Vector2> Uvs;
        [ReadOnly] public NativeArray<NoiseLayerData> NoiseLayers;
        [ReadOnly] public NativeArray<float> TerrainSplineSamples;

        public TerrainSettings Settings;
        public float2 ChunkOrigin;
        public float ChunkSize;
        public float HeightMultiplier;
        public float SkirtDepth;
        public int Resolution;
        public int BaseVertexCount;
        public int SegmentCount;
        public int LodStep;
        public EdgeStitching Stitching;

        public void Execute(int index)
        {
            int2 grid = ResolveGrid(index);
            float2 uv = Resolution > 1
                ? new float2(grid.x, grid.y) / (Resolution - 1)
                : float2.zero;

            float2 world = ChunkOrigin + uv * ChunkSize;
            HeightSample sample = SampleHeight(world);
            sample.WorldHeight = StitchEdgeHeight(sample.WorldHeight, grid, world);
            float3 normal = EstimateNormal(world);
            float height = sample.WorldHeight;

            if (index >= BaseVertexCount)
            {
                height -= SkirtDepth;
            }

            Vertices[index] = new Vector3(uv.x * ChunkSize, height, uv.y * ChunkSize);
            Normals[index] = new Vector3(normal.x, normal.y, normal.z);
            Uvs[index] = new Vector2(uv.x, uv.y);
        }

        private int2 ResolveGrid(int index)
        {
            if (index < BaseVertexCount)
            {
                return new int2(index % Resolution, index / Resolution);
            }

            int skirtIndex = index - BaseVertexCount;
            int side = skirtIndex / Resolution;
            int sideIndex = skirtIndex - side * Resolution;

            switch (side)
            {
                case 0:
                    return new int2(sideIndex, Resolution - 1);
                case 1:
                    return new int2(Resolution - 1, sideIndex);
                case 2:
                    return new int2(Resolution - 1 - sideIndex, 0);
                default:
                    return new int2(0, Resolution - 1 - sideIndex);
            }
        }

        private float StitchEdgeHeight(float height, int2 grid, float2 world)
        {
            int originalX = grid.x * LodStep;
            int originalZ = grid.y * LodStep;

            if (grid.y == Resolution - 1 && Stitching.NorthStep > LodStep)
            {
                return SampleStitchedEdgeHeight(originalX, Stitching.NorthStep, true, ChunkOrigin.y + ChunkSize);
            }

            if (grid.x == Resolution - 1 && Stitching.EastStep > LodStep)
            {
                return SampleStitchedEdgeHeight(originalZ, Stitching.EastStep, false, ChunkOrigin.x + ChunkSize);
            }

            if (grid.y == 0 && Stitching.SouthStep > LodStep)
            {
                return SampleStitchedEdgeHeight(originalX, Stitching.SouthStep, true, ChunkOrigin.y);
            }

            if (grid.x == 0 && Stitching.WestStep > LodStep)
            {
                return SampleStitchedEdgeHeight(originalZ, Stitching.WestStep, false, ChunkOrigin.x);
            }

            return height;
        }

        private float SampleStitchedEdgeHeight(int axisOriginal, int neighborStep, bool horizontalEdge, float fixedWorldAxis)
        {
            int anchor0 = (axisOriginal / neighborStep) * neighborStep;
            int anchor1 = math.min(anchor0 + neighborStep, SegmentCount);

            if (anchor0 == anchor1)
            {
                return SampleHeight(EdgeWorldPosition(anchor0, horizontalEdge, fixedWorldAxis)).WorldHeight;
            }

            float t = (axisOriginal - anchor0) / (float)(anchor1 - anchor0);
            float h0 = SampleHeight(EdgeWorldPosition(anchor0, horizontalEdge, fixedWorldAxis)).WorldHeight;
            float h1 = SampleHeight(EdgeWorldPosition(anchor1, horizontalEdge, fixedWorldAxis)).WorldHeight;
            return math.lerp(h0, h1, t);
        }

        private float2 EdgeWorldPosition(int axisOriginal, bool horizontalEdge, float fixedWorldAxis)
        {
            float offset = axisOriginal / (float)math.max(1, SegmentCount) * ChunkSize;

            if (horizontalEdge)
            {
                return new float2(ChunkOrigin.x + offset, fixedWorldAxis);
            }

            return new float2(fixedWorldAxis, ChunkOrigin.y + offset);
        }

        private HeightSample SampleHeight(float2 world)
        {
            float2 seeded = world + Settings.NoiseOffset + new float2(Settings.TerrainSeed * 37.17f, Settings.TerrainSeed * -19.91f);
            float2 warp = new float2(
                Fbm(seeded * Settings.DomainWarpFrequency, 3, 2.01f, 0.5f),
                Fbm((seeded + 73.41f) * Settings.DomainWarpFrequency, 3, 2.03f, 0.5f));
            float2 p = seeded + warp * Settings.DomainWarpStrength;

            float continent = To01(Fbm(p * Settings.ContinentFrequency, 5, 2.02f, 0.52f));
            float landMask = SmoothStep(Settings.SeaCoverage, 1f, continent);
            float biome = To01(Fbm((p + 911.73f) * Settings.BiomeFrequency, 3, 2.11f, 0.48f));
            float erosion = To01(Fbm((p + 331.59f) * Settings.BiomeFrequency * 1.65f, 4, 2.03f, 0.5f));
            float hills = To01(Fbm((p + 117.13f) * Settings.RidgeFrequency, 4, 2.05f, 0.5f));
            float peaksValleys = To01(Fbm((p - 631.17f) * Settings.RidgeFrequency * 0.55f, 3, 1.95f, 0.48f));
            float ridges = RoundedMountainNoise(p, Settings.RidgeFrequency);
            float detail = Fbm((p + 41.7f) * Settings.DetailFrequency, 3, 2.17f, 0.5f);

            float mountainMask = SmoothStep(Settings.MountainStart, 1f, biome);
            float normalized = CalculateDefaultTerrain(landMask, mountainMask, hills, ridges, detail);

            if (HasTerrainSplines() && Settings.TerrainSplineInfluence > 0f)
            {
                float splineNormalized = CalculateSplineTerrain(
                    continent,
                    erosion,
                    hills,
                    peaksValleys,
                    ridges,
                    detail,
                    out float splineLandMask,
                    out float splineMountainMask);
                float splineBlend = math.saturate(Settings.TerrainSplineInfluence);
                normalized = math.lerp(normalized, splineNormalized, splineBlend);
                landMask = math.lerp(landMask, splineLandMask, splineBlend);
                mountainMask = math.lerp(mountainMask, splineMountainMask, splineBlend);
            }

            float terraced = ApplyTerraces(normalized, Settings.TerraceSteps);
            normalized = math.lerp(normalized, terraced, Settings.TerraceStrength * mountainMask);

            if (NoiseLayers.Length > 0 && Settings.NoiseLayerInfluence > 0f)
            {
                normalized = ApplyRoleNoiseLayers(world, normalized, landMask, mountainMask);
            }

            normalized = math.max(0f, normalized);
            float worldHeight = normalized * HeightMultiplier;
            return new HeightSample(worldHeight, math.saturate(normalized));
        }

        private float CalculateDefaultTerrain(float landMask, float mountainMask, float hills, float ridges, float detail)
        {
            float plains = landMask * Settings.PlainsStrength;
            float rollingHills = landMask * hills * Settings.HillsStrength;
            float mountainShape = RoundPeak(ridges);
            float mountains = landMask * mountainMask * mountainShape * Settings.MountainStrength;
            float cliffs = landMask * mountainMask * SmoothStep(0.62f, 1f, ridges) * Settings.CliffStrength;
            return plains + rollingHills + mountains + cliffs + detail * Settings.DetailStrength * landMask;
        }

        private float CalculateSplineTerrain(
            float continentalness,
            float erosion,
            float hills,
            float peaksValleys,
            float mountainNoise,
            float detail,
            out float splineLandMask,
            out float splineMountainMask)
        {
            float baseHeight = SampleTerrainSpline(TerrainSplineChannel.ContinentalnessHeight, continentalness);
            float erosionMultiplier = SampleTerrainSpline(TerrainSplineChannel.ErosionMultiplier, erosion);
            float hillsMultiplier = SampleTerrainSpline(TerrainSplineChannel.HillsStrength, hills);
            float peaksValleysOffset = SampleTerrainSpline(TerrainSplineChannel.PeaksValleysOffset, peaksValleys);
            float mountainMultiplier = SampleTerrainSpline(TerrainSplineChannel.MountainStrength, mountainNoise);
            float detailMultiplier = SampleTerrainSpline(TerrainSplineChannel.DetailStrength, continentalness);

            splineLandMask = SmoothStep(Settings.SeaCoverage, 1f, continentalness);
            splineMountainMask = math.saturate(mountainMultiplier * erosionMultiplier);

            float plains = splineLandMask * Settings.PlainsStrength * (1f - math.saturate(mountainMultiplier)) * 0.35f;
            float rollingHills = splineLandMask * hills * Settings.HillsStrength * hillsMultiplier * erosionMultiplier;
            float mountainShape = RoundPeak(mountainNoise);
            float mountains = splineLandMask * mountainShape * Settings.MountainStrength * mountainMultiplier * erosionMultiplier;
            float cliffs = splineLandMask * SmoothStep(0.62f, 1f, mountainNoise) * Settings.CliffStrength * mountainMultiplier;
            float details = splineLandMask * detail * Settings.DetailStrength * detailMultiplier;
            return math.max(0f, baseHeight + plains + rollingHills + peaksValleysOffset * splineLandMask + mountains + cliffs + details);
        }

        private bool HasTerrainSplines()
        {
            return TerrainSplineSamples.Length >= TerrainSplineSampleCount * TerrainSplineChannelCount;
        }

        private float SampleTerrainSpline(TerrainSplineChannel channel, float input)
        {
            float scaled = math.saturate(input) * (TerrainSplineSampleCount - 1);
            int index0 = (int)math.floor(scaled);
            int index1 = math.min(index0 + 1, TerrainSplineSampleCount - 1);
            float t = scaled - index0;
            int baseIndex = (int)channel * TerrainSplineSampleCount;
            return math.lerp(TerrainSplineSamples[baseIndex + index0], TerrainSplineSamples[baseIndex + index1], t);
        }

        private float3 EstimateNormal(float2 world)
        {
            float sampleDistance = math.max(1f, ChunkSize / math.max(Resolution - 1, 1));
            float left = SampleHeight(world - new float2(sampleDistance, 0f)).WorldHeight;
            float right = SampleHeight(world + new float2(sampleDistance, 0f)).WorldHeight;
            float back = SampleHeight(world - new float2(0f, sampleDistance)).WorldHeight;
            float forward = SampleHeight(world + new float2(0f, sampleDistance)).WorldHeight;
            return math.normalize(new float3(left - right, sampleDistance * 2f, back - forward));
        }

        private float ApplyRoleNoiseLayers(float2 world, float normalized, float landMask, float mountainMask)
        {
            float result = normalized;

            for (int i = 0; i < NoiseLayers.Length; i++)
            {
                NoiseLayerData layer = NoiseLayers[i];
                float gate = HeightGate(result, layer);
                float amount = layer.Amplitude / math.max(HeightMultiplier, 1f) * Settings.NoiseLayerInfluence * gate;

                if (math.abs(amount) <= 0.000001f)
                {
                    continue;
                }

                switch ((TerrainNoiseRole)layer.Role)
                {
                    case TerrainNoiseRole.Continentalness:
                        result += SampleSignedLayer(world, layer) * amount;
                        break;
                    case TerrainNoiseRole.Erosion:
                        result -= SamplePositiveLayer(world, layer) * amount * landMask * math.lerp(0.35f, 1f, mountainMask);
                        break;
                    case TerrainNoiseRole.HillsNoise:
                        result += SamplePositiveLayer(world, layer) * amount * landMask * (1f - mountainMask * 0.35f);
                        break;
                    case TerrainNoiseRole.PeaksValleys:
                        result += SampleSignedLayer(world, layer) * amount * landMask * math.lerp(0.25f, 0.75f, mountainMask);
                        break;
                    case TerrainNoiseRole.MountainNoise:
                        result += SampleRoundedMountainLayer(world, layer) * amount * landMask * SmoothStep(0.15f, 1f, mountainMask);
                        break;
                }
            }

            return math.max(0f, result);
        }

        private float HeightGate(float normalizedHeight, NoiseLayerData layer)
        {
            if (layer.HeightThreshold <= 0f)
            {
                return 1f;
            }

            float worldHeight = normalizedHeight * HeightMultiplier;
            return SmoothStep(layer.HeightThreshold - layer.BlendRange, layer.HeightThreshold + layer.BlendRange, worldHeight);
        }

        private float SampleSignedLayer(float2 world, NoiseLayerData layer)
        {
            return Fbm(LayerPoint(world, layer), layer.Octaves, layer.Lacunarity, layer.Gain);
        }

        private float SamplePositiveLayer(float2 world, NoiseLayerData layer)
        {
            return To01(SampleSignedLayer(world, layer));
        }

        private float SampleRidgedLayer(float2 world, NoiseLayerData layer)
        {
            return RidgedFbm(LayerPoint(world, layer), layer.Octaves, layer.Lacunarity, layer.Gain);
        }

        private float SampleRoundedMountainLayer(float2 world, NoiseLayerData layer)
        {
            float2 p = LayerPoint(world, layer);
            int octaves = math.min(layer.Octaves, 3);
            float broadMass = To01(Fbm(p * 0.42f, octaves, 1.9f, math.min(layer.Gain, 0.45f)));
            float foldedDetail = RidgedFbm(p * 0.65f, octaves, 1.88f, math.min(layer.Gain, 0.42f));
            float rounded = RoundPeak(SmoothStep(0.18f, 0.92f, broadMass));
            return math.saturate(rounded * math.lerp(0.78f, 1.08f, foldedDetail));
        }

        private float2 LayerPoint(float2 world, NoiseLayerData layer)
        {
            float roleOffset = (layer.Role + 1) * 101.37f;
            float2 seededOffset = new float2(
                Settings.TerrainSeed * 17.31f + roleOffset,
                Settings.TerrainSeed * -23.77f - roleOffset);
            return (world + layer.Offset + seededOffset) * layer.Scale;
        }

        private static float RoundedMountainNoise(float2 p, float ridgeFrequency)
        {
            float broadMass = To01(Fbm((p - 251.9f) * ridgeFrequency * 0.38f, 3, 1.9f, 0.48f));
            float foldedDetail = RidgedFbm((p + 384.2f) * ridgeFrequency * 0.62f, 3, 1.88f, 0.42f);
            float roundedMass = SmoothStep(0.2f, 0.95f, broadMass);
            return math.saturate(RoundPeak(roundedMass) * math.lerp(0.8f, 1.08f, foldedDetail));
        }

        private static float RoundPeak(float value)
        {
            float v = math.saturate(value);
            return 1f - math.pow(1f - v, 2.15f);
        }

        private static float Fbm(float2 p, int octaves, float lacunarity, float gain)
        {
            float amplitude = 0.5f;
            float frequency = 1f;
            float sum = 0f;

            for (int i = 0; i < octaves; i++)
            {
                sum += noise.snoise(p * frequency) * amplitude;
                frequency *= lacunarity;
                amplitude *= gain;
            }

            return sum;
        }

        private static float RidgedFbm(float2 p, int octaves, float lacunarity, float gain)
        {
            float amplitude = 0.5f;
            float frequency = 1f;
            float sum = 0f;
            float total = 0f;

            for (int i = 0; i < octaves; i++)
            {
                float n = 1f - math.abs(noise.snoise(p * frequency));
                sum += n * n * amplitude;
                total += amplitude;
                frequency *= lacunarity;
                amplitude *= gain;
            }

            return total > 0f ? sum / total : 0f;
        }

        private static float ApplyTerraces(float value, int steps)
        {
            float stepCount = math.max(1, steps);
            return math.floor(value * stepCount) / stepCount;
        }

        private static float SmoothStep(float edge0, float edge1, float value)
        {
            float t = math.saturate((value - edge0) / math.max(edge1 - edge0, 0.0001f));
            return t * t * (3f - 2f * t);
        }

        private static float To01(float value)
        {
            return math.saturate(value * 0.5f + 0.5f);
        }

        private struct HeightSample
        {
            public HeightSample(float worldHeight, float normalizedHeight)
            {
                WorldHeight = worldHeight;
                NormalizedHeight = normalizedHeight;
            }

            public float WorldHeight;
            public float NormalizedHeight;
        }
    }

    [BurstCompile(FloatPrecision.Standard, FloatMode.Fast, CompileSynchronously = false)]
    private struct BuildTerrainIndicesJob : IJob
    {
        [WriteOnly] public NativeArray<int> Indices;
        public int Resolution;
        public int BaseQuadCount;
        public int BaseVertexCount;
        public int TotalQuadCount;
        public int SkirtSideMask;

        public void Execute()
        {
            for (int quadIndex = 0; quadIndex < TotalQuadCount; quadIndex++)
            {
                WriteQuad(quadIndex);
            }
        }

        private void WriteQuad(int quadIndex)
        {
            if (quadIndex < BaseQuadCount)
            {
                WriteSurfaceQuad(quadIndex);
                return;
            }

            WriteSkirtQuad(quadIndex - BaseQuadCount);
        }

        private void WriteSurfaceQuad(int quadIndex)
        {
            int quadsPerLine = Resolution - 1;
            int x = quadIndex % quadsPerLine;
            int y = quadIndex / quadsPerLine;
            int bottomLeft = y * Resolution + x;
            int bottomRight = bottomLeft + 1;
            int topLeft = bottomLeft + Resolution;
            int topRight = topLeft + 1;
            int index = quadIndex * 6;

            Indices[index] = bottomLeft;
            Indices[index + 1] = topLeft;
            Indices[index + 2] = topRight;
            Indices[index + 3] = bottomLeft;
            Indices[index + 4] = topRight;
            Indices[index + 5] = bottomRight;
        }

        private void WriteSkirtQuad(int skirtQuadIndex)
        {
            int segmentCount = Resolution - 1;
            int enabledSideIndex = skirtQuadIndex / segmentCount;
            int side = ResolveSkirtSide(enabledSideIndex);
            int i = skirtQuadIndex - enabledSideIndex * segmentCount;
            int index = (BaseQuadCount + skirtQuadIndex) * 6;
            int skirtStart = BaseVertexCount + side * Resolution;

            int edgeA;
            int edgeB;

            switch (side)
            {
                case 0:
                    edgeA = (Resolution - 1) * Resolution + i;
                    edgeB = edgeA + 1;
                    WriteOutwardQuad(index, edgeA, edgeB, skirtStart + i, skirtStart + i + 1, 0);
                    break;
                case 1:
                    edgeA = i * Resolution + (Resolution - 1);
                    edgeB = edgeA + Resolution;
                    WriteOutwardQuad(index, edgeA, edgeB, skirtStart + i, skirtStart + i + 1, 1);
                    break;
                case 2:
                    edgeA = Resolution - 1 - i;
                    edgeB = edgeA - 1;
                    WriteOutwardQuad(index, edgeA, edgeB, skirtStart + i, skirtStart + i + 1, 2);
                    break;
                default:
                    edgeA = (Resolution - 1 - i) * Resolution;
                    edgeB = edgeA - Resolution;
                    WriteOutwardQuad(index, edgeA, edgeB, skirtStart + i, skirtStart + i + 1, 3);
                    break;
            }
        }

        private int ResolveSkirtSide(int enabledSideIndex)
        {
            int current = 0;

            if ((SkirtSideMask & SkirtNorth) != 0)
            {
                if (current == enabledSideIndex)
                {
                    return 0;
                }

                current++;
            }

            if ((SkirtSideMask & SkirtEast) != 0)
            {
                if (current == enabledSideIndex)
                {
                    return 1;
                }

                current++;
            }

            if ((SkirtSideMask & SkirtSouth) != 0)
            {
                if (current == enabledSideIndex)
                {
                    return 2;
                }

                current++;
            }

            return 3;
        }

        private void WriteOutwardQuad(int index, int edgeA, int edgeB, int skirtA, int skirtB, int side)
        {
            if (side == 0 || side == 1)
            {
                Indices[index] = edgeA;
                Indices[index + 1] = skirtB;
                Indices[index + 2] = edgeB;
                Indices[index + 3] = edgeA;
                Indices[index + 4] = skirtA;
                Indices[index + 5] = skirtB;
                return;
            }

            Indices[index] = edgeA;
            Indices[index + 1] = edgeB;
            Indices[index + 2] = skirtB;
            Indices[index + 3] = edgeA;
            Indices[index + 4] = skirtB;
            Indices[index + 5] = skirtA;
        }
    }
}
