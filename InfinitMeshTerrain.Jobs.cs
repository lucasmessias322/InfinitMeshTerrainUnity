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
        [ReadOnly] public NativeArray<TerrainHeightNoiseLayerData> HeightLayers;
        [ReadOnly] public NativeArray<float> HeightSplineSamples;

        public TerrainSettings Settings;
        public int HeightLayerCount;
        public float2 ChunkOrigin;
        public float ChunkSize;
        public float SkirtDepth;
        public int Resolution;
        public int BaseVertexCount;
        public int SegmentCount;
        public int LodStep;
        public int WriteUvs;
        public EdgeStitching Stitching;

        public void Execute(int index)
        {
            int2 grid = ResolveGrid(index);
            float2 uv = Resolution > 1
                ? new float2(grid.x, grid.y) / (Resolution - 1)
                : float2.zero;

            float2 world = ChunkOrigin + uv * ChunkSize;
            float height = SampleHeight(world);
            height = StitchEdgeHeight(height, grid, world);
            float3 normal = EstimateNormal(world);

            if (index >= BaseVertexCount)
            {
                height -= SkirtDepth;
            }

            Vertices[index] = new Vector3(uv.x * ChunkSize, height, uv.y * ChunkSize);
            Normals[index] = new Vector3(normal.x, normal.y, normal.z);
            if (WriteUvs != 0)
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

                return math.clamp(height, Settings.MinHeight, Settings.MaxHeight);
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

        private float3 EstimateNormal(float2 world)
        {
            float sampleDistance = math.max(1f, ChunkSize / math.max(Resolution - 1, 1));
            float left = SampleHeight(world - new float2(sampleDistance, 0f));
            float right = SampleHeight(world + new float2(sampleDistance, 0f));
            float back = SampleHeight(world - new float2(0f, sampleDistance));
            float forward = SampleHeight(world + new float2(0f, sampleDistance));
            return math.normalize(new float3(left - right, sampleDistance * 2f, back - forward));
        }

        private static float To01(float value)
        {
            return math.saturate(value * 0.5f + 0.5f);
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
