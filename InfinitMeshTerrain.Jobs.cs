using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

public partial class InfinitMeshTerrain
{
    private static int GetHeightMapResolution(int resolution)
    {
        return Mathf.Max(1, resolution) + HeightMapBorder * 2;
    }

    private static int GetHeightMapVertexCount(int resolution)
    {
        int heightMapResolution = GetHeightMapResolution(resolution);
        return heightMapResolution * heightMapResolution;
    }

    [BurstCompile(FloatPrecision.Standard, FloatMode.Fast, CompileSynchronously = false)]
    private struct GenerateTerrainHeightMapJob : IJobFor
    {
        [WriteOnly] public NativeArray<float> Heights;
        [ReadOnly] public NativeArray<TerrainHeightNoiseLayerData> HeightLayers;
        [ReadOnly] public NativeArray<float> HeightSplineSamples;

        public TerrainSettings Settings;
        public int HeightLayerCount;
        public float2 ChunkOrigin;
        public float ChunkSize;
        public int Resolution;
        public int HeightMapResolution;
        public int SegmentCount;
        public int LodStep;
        public EdgeStitching Stitching;

        public void Execute(int index)
        {
            int2 mapGrid = new int2(index % HeightMapResolution, index / HeightMapResolution);
            int2 grid = mapGrid - HeightMapBorder;
            float2 uv = Resolution > 1
                ? new float2(grid.x, grid.y) / (Resolution - 1)
                : float2.zero;

            float2 world = ChunkOrigin + uv * ChunkSize;
            float height = SampleHeight(world);
            if (IsInteriorGrid(grid))
            {
                height = StitchEdgeHeight(height, grid, world);
            }

            Heights[index] = height;
        }

        private bool IsInteriorGrid(int2 grid)
        {
            return grid.x >= 0
                && grid.x < Resolution
                && grid.y >= 0
                && grid.y < Resolution;
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
                return SampleHeight(EdgeWorldPosition(anchor0, horizontalEdge, fixedWorldAxis));
            }

            float t = (axisOriginal - anchor0) / (float)(anchor1 - anchor0);
            float h0 = SampleHeight(EdgeWorldPosition(anchor0, horizontalEdge, fixedWorldAxis));
            float h1 = SampleHeight(EdgeWorldPosition(anchor1, horizontalEdge, fixedWorldAxis));
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

        private float SampleHeight(float2 world)
        {
            if (HeightLayerCount > 0)
            {
                float height = 0f;
                int layerCount = math.min(HeightLayerCount, HeightLayers.Length);
                for (int i = 0; i < layerCount; i++)
                {
                    TerrainHeightNoiseLayerData layer = HeightLayers[i];
                    float contribution = SampleLayerContribution(world, layer, i);
                    height = ApplyLayerOperation(height, contribution, layer.Operation);
                }

                height = math.clamp(height, Settings.MinHeight, Settings.MaxHeight);
                return ApplyTerrainTerraces(height, Settings);
            }

            return Settings.MinHeight;
        }

        private float SampleLayerContribution(float2 world, TerrainHeightNoiseLayerData layer, int layerIndex)
        {
            float value = SampleLayerValue(world, layer, layerIndex);
            float mask = EvaluateLayerMask(value, layer);
            mask *= EvaluateMaskSpline(world, layer);
            return value * mask * layer.Amplitude;
        }

        private float SampleLayerValue(float2 world, TerrainHeightNoiseLayerData layer, int layerIndex)
        {
            float value = SampleLayerNoise(world, layer, layerIndex);
            return ApplySpline(value, layer);
        }

        private float SampleLayerNoise(float2 world, TerrainHeightNoiseLayerData layer, int layerIndex)
        {
            float2 seedOffset = Settings.NoiseOffset
                + layer.Offset
                + new float2(
                    Settings.TerrainSeed * 37.17f + layerIndex * 101.31f,
                    Settings.TerrainSeed * -19.91f + layerIndex * -73.57f);
            float frequency = math.max(0.000001f, layer.Frequency);
            float octaveAmplitude = 1f;
            float total = 0f;
            float weightSum = 0f;
            int octaveCount = math.clamp(layer.Octaves, 1, 12);

            for (int octave = 0; octave < octaveCount; octave++)
            {
                float raw = noise.snoise((world + seedOffset) * frequency);
                total += ShapeNoise(raw, layer.NoiseShape) * octaveAmplitude;
                weightSum += octaveAmplitude;
                octaveAmplitude *= math.saturate(layer.Persistence);
                frequency *= math.max(1f, layer.Lacunarity);
            }

            return weightSum > 0f ? total / weightSum : 0f;
        }

        private float ApplySpline(float value, TerrainHeightNoiseLayerData layer)
        {
            if (layer.SplineInfluence <= 0f || layer.SplineSampleOffset < 0 || layer.SplineSampleCount <= 1 || HeightSplineSamples.Length == 0)
            {
                return value;
            }

            float input = NormalizeSplineInput(value, layer.NoiseShape);
            float splineValue = SampleSpline(input, layer.SplineSampleOffset, layer.SplineSampleCount);
            return math.lerp(value, splineValue, math.saturate(layer.SplineInfluence));
        }

        private float EvaluateMaskSpline(float2 world, TerrainHeightNoiseLayerData layer)
        {
            if (layer.MaskSplineInfluence <= 0f
                || layer.MaskSplineSampleOffset < 0
                || layer.MaskSplineSampleCount <= 1
                || layer.MaskSplineInputLayerIndex < 0
                || layer.MaskSplineInputLayerIndex >= HeightLayerCount
                || layer.MaskSplineInputLayerIndex >= HeightLayers.Length
                || HeightSplineSamples.Length == 0)
            {
                return 1f;
            }

            TerrainHeightNoiseLayerData inputLayer = HeightLayers[layer.MaskSplineInputLayerIndex];
            float inputValue = SampleLayerValue(world, inputLayer, layer.MaskSplineInputLayerIndex);
            float input = NormalizeSplineInput(inputValue, inputLayer.NoiseShape);
            float splineMask = SampleSpline(input, layer.MaskSplineSampleOffset, layer.MaskSplineSampleCount);
            return math.max(0f, math.lerp(1f, splineMask, math.saturate(layer.MaskSplineInfluence)));
        }

        private float SampleSpline(float input, int sampleOffset, int sampleCount)
        {
            int availableCount = math.min(sampleCount, HeightSplineSamples.Length - sampleOffset);
            if (sampleOffset < 0 || availableCount <= 0)
            {
                return input;
            }

            if (availableCount == 1)
            {
                return HeightSplineSamples[sampleOffset];
            }

            float samplePosition = math.saturate(input) * (availableCount - 1);
            int index0 = (int)math.floor(samplePosition);
            int index1 = math.min(index0 + 1, availableCount - 1);
            float t = samplePosition - index0;
            float value0 = HeightSplineSamples[sampleOffset + index0];
            float value1 = HeightSplineSamples[sampleOffset + index1];
            return math.lerp(value0, value1, t);
        }

        private static float NormalizeSplineInput(float value, int noiseShape)
        {
            return noiseShape == (int)TerrainHeightNoiseShape.Signed
                ? To01(value)
                : math.saturate(value);
        }

        private static float ShapeNoise(float value, int noiseShape)
        {
            if (noiseShape == (int)TerrainHeightNoiseShape.Signed)
            {
                return value;
            }

            if (noiseShape == (int)TerrainHeightNoiseShape.Ridged)
            {
                return math.saturate(1f - math.abs(value));
            }

            if (noiseShape == (int)TerrainHeightNoiseShape.Billow)
            {
                return math.saturate(math.abs(value));
            }

            return To01(value);
        }

        private static float EvaluateLayerMask(float value, TerrainHeightNoiseLayerData layer)
        {
            if (layer.Threshold <= 0f && layer.BlendRange <= 0f)
            {
                return 1f;
            }

            float compareValue = layer.NoiseShape == (int)TerrainHeightNoiseShape.Signed
                ? To01(value)
                : math.saturate(value);

            if (layer.BlendRange <= 0.000001f)
            {
                return compareValue >= layer.Threshold ? 1f : 0f;
            }

            float blendEnd = math.min(1f, layer.Threshold + layer.BlendRange);
            if (blendEnd <= layer.Threshold)
            {
                return compareValue >= layer.Threshold ? 1f : 0f;
            }

            float t = math.saturate((compareValue - layer.Threshold) / (blendEnd - layer.Threshold));
            return t * t * (3f - 2f * t);
        }

        private static float ApplyLayerOperation(float height, float contribution, int operation)
        {
            if (operation == (int)TerrainHeightLayerOperation.Subtract)
            {
                return height - contribution;
            }

            if (operation == (int)TerrainHeightLayerOperation.Max)
            {
                return math.max(height, contribution);
            }

            if (operation == (int)TerrainHeightLayerOperation.Min)
            {
                return math.min(height, contribution);
            }

            return height + contribution;
        }

        private static float To01(float value)
        {
            return math.saturate(value * 0.5f + 0.5f);
        }
    }

    [BurstCompile(FloatPrecision.Standard, FloatMode.Fast, CompileSynchronously = false)]
    private struct GenerateTerrainVerticesJob : IJobFor
    {
        [ReadOnly] public NativeArray<float> Heights;
        [WriteOnly] public NativeArray<Vector3> Vertices;
        [WriteOnly] public NativeArray<Vector3> Normals;
        [WriteOnly] public NativeArray<Vector2> Uvs;

        public float ChunkSize;
        public float SkirtDepth;
        public int Resolution;
        public int HeightMapResolution;
        public int BaseVertexCount;
        public int WriteNormals;
        public int WriteUvs;

        public void Execute(int index)
        {
            int2 grid = ResolveGrid(index);
            float2 uv = Resolution > 1
                ? new float2(grid.x, grid.y) / (Resolution - 1)
                : float2.zero;
            float height = SampleHeight(grid);

            if (index >= BaseVertexCount)
            {
                height -= SkirtDepth;
            }

            Vertices[index] = new Vector3(uv.x * ChunkSize, height, uv.y * ChunkSize);

            if (WriteNormals != 0 && Normals.IsCreated)
            {
                Normals[index] = CalculateNormal(grid);
            }

            if (WriteUvs != 0 && Uvs.IsCreated)
            {
                Uvs[index] = new Vector2(uv.x, uv.y);
            }
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

        private Vector3 CalculateNormal(int2 grid)
        {
            int last = Resolution - 1;
            if (last <= 0 || Heights.Length == 0)
            {
                return new Vector3(0f, 1f, 0f);
            }

            int leftX = grid.x - 1;
            int rightX = grid.x + 1;
            int backZ = grid.y - 1;
            int forwardZ = grid.y + 1;
            float spacing = ChunkSize / math.max(1, last);
            float dxDistance = math.max(0.0001f, (rightX - leftX) * spacing);
            float dzDistance = math.max(0.0001f, (forwardZ - backZ) * spacing);
            float dHdx = (SampleHeight(rightX, grid.y) - SampleHeight(leftX, grid.y)) / dxDistance;
            float dHdz = (SampleHeight(grid.x, forwardZ) - SampleHeight(grid.x, backZ)) / dzDistance;
            float3 normal = new float3(-dHdx, 1f, -dHdz);

            if (math.lengthsq(normal) <= 0.000001f)
            {
                return new Vector3(0f, 1f, 0f);
            }

            normal = math.normalize(normal);
            return new Vector3(normal.x, normal.y, normal.z);
        }

        private float SampleHeight(int2 grid)
        {
            return SampleHeight(grid.x, grid.y);
        }

        private float SampleHeight(int x, int z)
        {
            int mapX = math.clamp(x + HeightMapBorder, 0, HeightMapResolution - 1);
            int mapZ = math.clamp(z + HeightMapBorder, 0, HeightMapResolution - 1);
            int index = mapZ * HeightMapResolution + mapX;
            return index >= 0 && index < Heights.Length ? Heights[index] : 0f;
        }
    }

    [BurstCompile(FloatPrecision.Standard, FloatMode.Fast, CompileSynchronously = false)]
    private struct BuildTerrainSplatMapsJob : IJobFor
    {
        [ReadOnly] public NativeArray<Vector3> Vertices;
        [ReadOnly] public NativeArray<Vector3> Normals;
        [WriteOnly] public NativeArray<Color32> SplatMap0Pixels;
        [WriteOnly] public NativeArray<Color32> SplatMap1Pixels;
        [WriteOnly, NativeDisableParallelForRestriction] public NativeArray<Color32> BiomeLayerColorPixels;
        [ReadOnly] public NativeArray<TerrainSplatLayerData> SplatLayers;
        [ReadOnly] public NativeArray<float4> TerrainLayerBaseColors;
        [ReadOnly] public NativeArray<TerrainBiomeLayerColorJobData> TerrainBiomeLayerColorData;
        [ReadOnly] public NativeArray<int> ActiveBiomeLayerColorChannels;

        public SlopeTextureSettings SlopeTextureSettings;
        public BiomeSamplingSettings BiomeSettings;
        public float2 ChunkOrigin;
        public int TerrainLayerCount;
        public int BiomeDataCount;
        public int ActiveBiomeLayerColorCount;
        public int BiomeLayerColorPixelCount;

        public void Execute(int index)
        {
            Vector3 vertex = Vertices[index];
            SplatWeights weights = EvaluateSplatWeights(vertex.y);
            weights = ApplySlopeTexture(weights, Normals[index]);
            SplatMap0Pixels[index] = ToColor32(weights.Map0);
            SplatMap1Pixels[index] = ToColor32(weights.Map1);

            if (ActiveBiomeLayerColorCount <= 0 || BiomeDataCount <= 0 || BiomeLayerColorPixelCount <= 0)
            {
                return;
            }

            float2 worldXZ = ChunkOrigin + new float2(vertex.x, vertex.z);
            float biomeDistance = EvaluateBiomeDistance(worldXZ, BiomeSettings);
            TerrainBiomeLayerColorBlend biomeBlend = ResolveTerrainBiomeLayerColorBlend(
                worldXZ,
                biomeDistance,
                TerrainBiomeLayerColorData,
                BiomeSettings,
                BiomeDataCount);

            for (int activeIndex = 0; activeIndex < ActiveBiomeLayerColorCount; activeIndex++)
            {
                int channelIndex = ActiveBiomeLayerColorChannels[activeIndex];
                float3 baseColor = TerrainLayerBaseColors[channelIndex].xyz;
                float3 layerColor = biomeBlend.PrimaryIndex >= 0
                    ? ResolveTerrainBiomeLayerColor(
                        TerrainBiomeLayerColorData[biomeBlend.PrimaryIndex],
                        channelIndex,
                        baseColor)
                    : baseColor;

                if (biomeBlend.SecondaryIndex >= 0 && biomeBlend.SecondaryWeight > 0.0001f)
                {
                    float3 secondaryColor = ResolveTerrainBiomeLayerColor(
                        TerrainBiomeLayerColorData[biomeBlend.SecondaryIndex],
                        channelIndex,
                        baseColor);
                    layerColor = math.lerp(layerColor, secondaryColor, biomeBlend.SecondaryWeight);
                }

                BiomeLayerColorPixels[activeIndex * BiomeLayerColorPixelCount + index] = ToColor32(layerColor);
            }
        }

        private SplatWeights EvaluateSplatWeights(float height)
        {
            SplatWeights weights = default;
            int layerCount = TerrainLayerCount;

            if (layerCount <= 0)
            {
                weights.Map0.x = 1f;
                return weights;
            }

            layerCount = math.min(layerCount, SplatLayers.Length);
            AddWeight(ref weights, SplatLayers[0].Channel, 1f);

            for (int i = 1; i < layerCount; i++)
            {
                TerrainSplatLayerData layer = SplatLayers[i];
                float blend = SmoothStep(layer.StartHeight, layer.StartHeight + math.max(0.0001f, layer.BlendRange), height);

                weights.Map0 *= 1f - blend;
                weights.Map1 *= 1f - blend;
                AddWeight(ref weights, layer.Channel, blend);
            }

            return NormalizeWeights(weights);
        }

        private SplatWeights ApplySlopeTexture(SplatWeights weights, Vector3 normal)
        {
            if (!SlopeTextureSettings.Enabled)
            {
                return weights;
            }

            float normalY = math.clamp(normal.y, -1f, 1f);
            float slopeAngle = math.acos(normalY) * 57.29578f;
            float slopeBlend = SmoothStep(SlopeTextureSettings.StartAngle, SlopeTextureSettings.FullAngle, slopeAngle);
            float rockAmount = math.saturate(slopeBlend * SlopeTextureSettings.Strength);

            if (rockAmount <= 0.0001f)
            {
                return weights;
            }

            weights.Map0 *= 1f - rockAmount;
            weights.Map1 *= 1f - rockAmount;
            AddWeight(ref weights, (int)SlopeTextureSettings.Channel, rockAmount);
            return NormalizeWeights(weights);
        }

        private static SplatWeights NormalizeWeights(SplatWeights weights)
        {
            float sum = math.csum(weights.Map0) + math.csum(weights.Map1);
            if (sum > 0.0001f)
            {
                weights.Map0 /= sum;
                weights.Map1 /= sum;
            }

            return weights;
        }

        private static void AddWeight(ref SplatWeights weights, int channel, float value)
        {
            switch (channel)
            {
                case 1:
                    weights.Map0.y += value;
                    break;
                case 2:
                    weights.Map0.z += value;
                    break;
                case 3:
                    weights.Map0.w += value;
                    break;
                case 4:
                    weights.Map1.x += value;
                    break;
                case 5:
                    weights.Map1.y += value;
                    break;
                case 6:
                    weights.Map1.z += value;
                    break;
                case 7:
                    weights.Map1.w += value;
                    break;
                default:
                    weights.Map0.x += value;
                    break;
            }
        }

        private static float SmoothStep(float edge0, float edge1, float value)
        {
            float t = math.saturate((value - edge0) / math.max(edge1 - edge0, 0.0001f));
            return t * t * (3f - 2f * t);
        }

        private static Color32 ToColor32(float4 weights)
        {
            return new Color32(
                ToByte(weights.x),
                ToByte(weights.y),
                ToByte(weights.z),
                ToByte(weights.w));
        }

        private static Color32 ToColor32(float3 color)
        {
            return new Color32(
                ToByte(color.x),
                ToByte(color.y),
                ToByte(color.z),
                255);
        }

        private static byte ToByte(float value)
        {
            return (byte)(int)math.round(math.saturate(value) * 255f);
        }

        private struct SplatWeights
        {
            public float4 Map0;
            public float4 Map1;
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
