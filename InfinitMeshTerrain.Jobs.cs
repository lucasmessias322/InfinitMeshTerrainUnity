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
            float height = SampleHeight(world);
            height = StitchEdgeHeight(height, grid, world);
            float3 normal = EstimateNormal(world);

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
            float2 seeded = world + Settings.NoiseOffset + new float2(Settings.TerrainSeed * 37.17f, Settings.TerrainSeed * -19.91f);
            float normalizedHeight = To01(noise.snoise(seeded * Settings.NoiseFrequency));
            return normalizedHeight * HeightMultiplier;
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
