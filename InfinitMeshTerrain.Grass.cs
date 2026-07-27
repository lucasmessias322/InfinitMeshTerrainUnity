using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

public partial class InfinitMeshTerrain
{
    private const string GrassShaderName = "Shader Graphs/GrassShaderGraph";
    private const int GrassInstanceStride = 48;
    private static readonly int GrassInstancesPropertyId = Shader.PropertyToID("_GrassInstances");
    private static readonly int GrassViewerPositionPropertyId = Shader.PropertyToID("_ViewerPosition");
    private static readonly int GrassFadeDistancesPropertyId = Shader.PropertyToID("_FadeDistances");
    private static readonly int GrassWindPropertyId = Shader.PropertyToID("_Wind");
    private static readonly int GrassMeshGroundingPropertyId = Shader.PropertyToID("_MeshGrounding");

    [Header("Detail Grass")]
    [SerializeField] private GrassSettingsSO grassSettings;

    private Mesh runtimeGrassMesh;
    private Material runtimeGrassMaterial;
    private GrassSettingsSO runtimeDefaultGrassSettings;

    private void ValidateGrassSettings()
    {
        if (grassSettings != null)
        {
            grassSettings.ValidateValues();
        }
    }

    private void UpdateGrassDetails()
    {
        GrassSettingsSO settings = GetGrassSettings();
        if (settings == null || !settings.EnableGrass || viewer == null || settings.DensityPerSquareMeter <= 0f || settings.DetailDistance <= 0f)
        {
            ClearGrassFromRuntimeChunks();
            return;
        }

        Mesh drawMesh = GetGrassRenderMesh(settings);
        Material drawMaterial = GetGrassRenderMaterial(settings);
        if (drawMesh == null || drawMaterial == null)
        {
            return;
        }

        drawMaterial.enableInstancing = true;

        Vector2 windDirection = settings.WindDirection.sqrMagnitude > 0.0001f
            ? settings.WindDirection.normalized
            : Vector2.right;
        Vector4 wind = new Vector4(windDirection.x, windDirection.y, settings.WindStrength, settings.WindSpeed);
        Vector4 fadeDistances = new Vector4(settings.FadeStartDistance, settings.DetailDistance, 0f, 0f);
        Vector4 meshGrounding = new Vector4(0f, settings.SurfaceOffset, 0f, 0f);
        int layer = gameObject.layer;

        foreach (Vector2Int coord in visibleChunkCoords)
        {
            if (!chunks.TryGetValue(coord, out TerrainChunk chunk) || !chunk.HasMesh)
            {
                continue;
            }

            if (!IsChunkInsideGrassDistance(coord, settings.DetailDistance))
            {
                if (settings.UnloadOutsideDetailDistance)
                {
                    chunk.ClearGrass();
                }

                continue;
            }

            if (!chunk.HasGrass)
            {
                RequestBuild(coord);
                continue;
            }

            chunk.DrawGrass(
                drawMesh,
                drawMaterial,
                viewer.position,
                fadeDistances,
                wind,
                meshGrounding,
                settings.ShadowCastingMode,
                settings.ReceiveShadows,
                layer);
        }
    }

    private bool ShouldBuildGrassForChunk(Vector2Int coord)
    {
        GrassSettingsSO settings = GetGrassSettings();
        if (settings == null || !settings.EnableGrass || viewer == null || settings.DensityPerSquareMeter <= 0f || settings.MaxInstancesPerChunk <= 0)
        {
            return false;
        }

        return IsChunkInsideGrassDistance(coord, settings.DetailDistance + chunkSize * 0.75f);
    }

    private bool IsChunkInsideGrassDistance(Vector2Int coord, float distance)
    {
        if (viewer == null)
        {
            return false;
        }

        Vector2 chunkCenter = new Vector2((coord.x + 0.5f) * chunkSize, (coord.y + 0.5f) * chunkSize);
        Vector2 viewerPosition = new Vector2(viewer.position.x, viewer.position.z);
        float chunkRadius = chunkSize * 0.707107f;
        float allowedDistance = Mathf.Max(0f, distance) + chunkRadius;
        return (chunkCenter - viewerPosition).sqrMagnitude <= allowedDistance * allowedDistance;
    }

    private int CalculateGrassInstanceCapacity()
    {
        GrassSettingsSO settings = GetGrassSettings();
        if (settings == null || !settings.EnableGrass || settings.MaxInstancesPerChunk <= 0 || settings.DensityPerSquareMeter <= 0f)
        {
            return 0;
        }

        int cellsPerAxis = Mathf.Max(1, Mathf.CeilToInt(chunkSize / settings.DetailCellSize));
        long maxByCells = (long)cellsPerAxis * cellsPerAxis * settings.MaxInstancesPerCell;
        return (int)Mathf.Min(settings.MaxInstancesPerChunk, maxByCells);
    }

    private GrassBuildSettings CreateGrassBuildSettings()
    {
        GrassSettingsSO settings = GetGrassSettings();
        if (settings == null)
        {
            return default;
        }

        float minHeight = settings.MinHeight;
        if (settings.AvoidWater && enableWater)
        {
            minHeight = Mathf.Max(minHeight, waterHeight + settings.WaterPadding);
        }

        return new GrassBuildSettings
        {
            TerrainSeed = GetTerrainSeed(),
            GrassSeed = GetTerrainSeed() * 17 + 97,
            Channel = Mathf.Clamp(settings.ChannelIndex, 0, MaxTerrainLayerCount - 1),
            DensityPerSquareMeter = settings.DensityPerSquareMeter,
            CellSize = settings.DetailCellSize,
            Jitter = settings.Jitter,
            MaxInstancesPerCell = settings.MaxInstancesPerCell,
            LayerThreshold = settings.LayerThreshold,
            MinHeight = minHeight,
            MaxHeight = Mathf.Max(minHeight + 0.01f, settings.MaxHeight),
            HeightFadeRange = settings.HeightFadeRange,
            MinSlopeAngle = settings.MinSlopeAngle,
            MaxSlopeAngle = settings.MaxSlopeAngle,
            SlopeFadeRange = settings.SlopeFadeRange,
            BladeHeight = settings.BladeHeight,
            BladeHeightVariation = settings.BladeHeightVariation,
            BladeWidth = settings.BladeWidth,
            BladeWidthVariation = settings.BladeWidthVariation,
            ColorVariation = settings.ColorVariation,
            NormalAlignment = settings.NormalAlignment,
            SurfaceOffset = settings.SurfaceOffset,
            CoverageNoiseFrequency = settings.CoverageNoiseFrequency,
            CoverageNoiseStrength = settings.CoverageNoiseStrength
        };
    }

    private GrassSettingsSO GetGrassSettings()
    {
        if (grassSettings != null)
        {
            return grassSettings;
        }

        if (runtimeDefaultGrassSettings == null)
        {
            runtimeDefaultGrassSettings = GrassSettingsSO.CreateRuntimeDefault();
        }

        return runtimeDefaultGrassSettings;
    }

    private int GetGrassTerrainLayerCount()
    {
        return terrainLayers != null ? Mathf.Min(terrainLayers.Length, MaxTerrainLayerCount) : 0;
    }

    private void CopyGrassTerrainLayers(NativeArray<GrassTerrainLayerData> destination)
    {
        if (!destination.IsCreated || destination.Length == 0 || terrainLayers == null)
        {
            return;
        }

        GrassTerrainLayerData[] sortedLayers = new GrassTerrainLayerData[destination.Length];
        for (int i = 0; i < sortedLayers.Length; i++)
        {
            TerrainHeightLayer layer = terrainLayers[i];
            sortedLayers[i] = new GrassTerrainLayerData
            {
                Channel = Mathf.Clamp((int)layer.channel, 0, MaxTerrainLayerCount - 1),
                StartHeight = layer.startHeight,
                BlendRange = Mathf.Max(0.0001f, layer.blendRange)
            };
        }

        Array.Sort(sortedLayers, (a, b) => a.StartHeight.CompareTo(b.StartHeight));

        for (int i = 0; i < sortedLayers.Length; i++)
        {
            destination[i] = sortedLayers[i];
        }
    }

    private Mesh GetGrassRenderMesh(GrassSettingsSO settings)
    {
        if (settings.Mesh != null)
        {
            return settings.Mesh;
        }

        if (runtimeGrassMesh != null)
        {
            return runtimeGrassMesh;
        }

        runtimeGrassMesh = CreateDefaultGrassMesh();
        return runtimeGrassMesh;
    }

    private Material GetGrassRenderMaterial(GrassSettingsSO settings)
    {
        if (settings.Material != null)
        {
            return settings.Material;
        }

        if (runtimeGrassMaterial != null)
        {
            return runtimeGrassMaterial;
        }

        Shader shader = Shader.Find(GrassShaderName);
        if (shader == null)
        {
            return null;
        }

        runtimeGrassMaterial = new Material(shader)
        {
            name = "Runtime Instanced Grass",
            hideFlags = HideFlags.DontSave
        };
        runtimeGrassMaterial.enableInstancing = true;
        return runtimeGrassMaterial;
    }

    private static Mesh CreateDefaultGrassMesh()
    {
        Mesh mesh = new Mesh
        {
            name = "Runtime Cross Grass Blade",
            hideFlags = HideFlags.DontSave
        };

        Vector3[] vertices =
        {
            new Vector3(-0.5f, 0f, 0f),
            new Vector3(-0.5f, 1f, 0f),
            new Vector3(0.5f, 1f, 0f),
            new Vector3(0.5f, 0f, 0f),
            new Vector3(0f, 0f, -0.5f),
            new Vector3(0f, 1f, -0.5f),
            new Vector3(0f, 1f, 0.5f),
            new Vector3(0f, 0f, 0.5f)
        };

        Vector2[] uvs =
        {
            new Vector2(0f, 0f),
            new Vector2(0f, 1f),
            new Vector2(1f, 1f),
            new Vector2(1f, 0f),
            new Vector2(0f, 0f),
            new Vector2(0f, 1f),
            new Vector2(1f, 1f),
            new Vector2(1f, 0f)
        };

        int[] indices =
        {
            0, 1, 2,
            0, 2, 3,
            2, 1, 0,
            3, 2, 0,
            4, 5, 6,
            4, 6, 7,
            6, 5, 4,
            7, 6, 4
        };

        mesh.SetVertices(vertices);
        mesh.SetUVs(0, uvs);
        mesh.SetIndices(indices, MeshTopology.Triangles, 0);
        mesh.RecalculateBounds();
        return mesh;
    }

    private void ClearGrassFromRuntimeChunks()
    {
        foreach (TerrainChunk chunk in chunks.Values)
        {
            chunk.ClearGrass();
        }
    }

    private void DestroyGrassRuntimeResources()
    {
        ClearGrassFromRuntimeChunks();

        if (runtimeGrassMesh != null)
        {
            if (Application.isPlaying)
            {
                Destroy(runtimeGrassMesh);
            }
            else
            {
                DestroyImmediate(runtimeGrassMesh);
            }

            runtimeGrassMesh = null;
        }

        if (runtimeGrassMaterial != null)
        {
            if (Application.isPlaying)
            {
                Destroy(runtimeGrassMaterial);
            }
            else
            {
                DestroyImmediate(runtimeGrassMaterial);
            }

            runtimeGrassMaterial = null;
        }

        if (runtimeDefaultGrassSettings != null)
        {
            if (Application.isPlaying)
            {
                Destroy(runtimeDefaultGrassSettings);
            }
            else
            {
                DestroyImmediate(runtimeDefaultGrassSettings);
            }

            runtimeDefaultGrassSettings = null;
        }
    }

    [BurstCompile(FloatPrecision.Standard, FloatMode.Fast, CompileSynchronously = false)]
    private struct GenerateGrassInstancesJob : IJob
    {
        [ReadOnly] public NativeArray<Vector3> Vertices;
        [ReadOnly] public NativeArray<Vector3> Normals;
        [ReadOnly] public NativeArray<GrassTerrainLayerData> TerrainLayers;
        [WriteOnly] public NativeArray<GrassInstanceData> GrassInstances;
        public NativeArray<int> GrassInstanceCounter;
        public NativeArray<GrassChunkBounds> GrassBounds;

        public GrassBuildSettings Settings;
        public SlopeTextureSettings SlopeTextureSettings;
        public int2 ChunkCoord;
        public float2 ChunkOrigin;
        public float ChunkSize;
        public int Resolution;
        public int BaseVertexCount;

        public void Execute()
        {
            int capacity = GrassInstances.Length;
            if (capacity == 0 || Resolution < 2 || BaseVertexCount < Resolution * Resolution)
            {
                WriteOutputs(0, default);
                return;
            }

            int cellsPerAxis = math.max(1, (int)math.ceil(ChunkSize / math.max(0.1f, Settings.CellSize)));
            float cellSize = ChunkSize / cellsPerAxis;
            float expectedPerCell = Settings.DensityPerSquareMeter * cellSize * cellSize;
            if (expectedPerCell <= 0f)
            {
                WriteOutputs(0, default);
                return;
            }

            int count = 0;
            GrassChunkBounds bounds = new GrassChunkBounds
            {
                Min = new float3(float.MaxValue, float.MaxValue, float.MaxValue),
                Max = new float3(float.MinValue, float.MinValue, float.MinValue)
            };

            for (int z = 0; z < cellsPerAxis && count < capacity; z++)
            {
                for (int x = 0; x < cellsPerAxis && count < capacity; x++)
                {
                    uint cellHash = Hash(ChunkCoord.x, ChunkCoord.y, x, z, Settings.TerrainSeed, Settings.GrassSeed);
                    int candidateCount = (int)math.floor(expectedPerCell);
                    float fractional = expectedPerCell - candidateCount;
                    if (Hash01(cellHash + 0x6d2b79f5u) < fractional)
                    {
                        candidateCount++;
                    }

                    candidateCount = math.min(candidateCount, Settings.MaxInstancesPerCell);
                    for (int candidate = 0; candidate < candidateCount && count < capacity; candidate++)
                    {
                        uint instanceHash = Hash((int)cellHash, candidate, x * 73856093, z * 19349663, Settings.GrassSeed, Settings.TerrainSeed);
                        float2 jitter = new float2(
                            Hash01(instanceHash + 0x9e3779b9u),
                            Hash01(instanceHash + 0xbb67ae85u));
                        float2 centeredJitter = (jitter - 0.5f) * Settings.Jitter + 0.5f;
                        float2 local = (new float2(x, z) + centeredJitter) * cellSize;
                        local = math.clamp(local, new float2(0.001f, 0.001f), new float2(ChunkSize - 0.001f, ChunkSize - 0.001f));

                        SampleSurface(local, out float3 positionWS, out float3 normalWS);
                        float slopeAngle = math.acos(math.clamp(normalWS.y, -1f, 1f)) * 57.29578f;
                        float coverage = EvaluateLayerCoverage(positionWS.y, normalWS, slopeAngle);
                        coverage *= EvaluateHeightGate(positionWS.y);
                        coverage *= EvaluateSlopeGate(slopeAngle);
                        coverage *= EvaluateCoverageNoise(positionWS.xz);
                        coverage = math.saturate(coverage);

                        if (coverage <= 0f || Hash01(instanceHash + 0x3c6ef372u) > coverage)
                        {
                            continue;
                        }

                        float bladeHeight = Settings.BladeHeight * math.max(
                            0.05f,
                            1f + (Hash01(instanceHash + 0xa54ff53au) * 2f - 1f) * Settings.BladeHeightVariation);
                        float bladeWidth = Settings.BladeWidth * math.max(
                            0.05f,
                            1f + (Hash01(instanceHash + 0x510e527fu) * 2f - 1f) * Settings.BladeWidthVariation);
                        float yaw = Hash01(instanceHash + 0x1f83d9abu) * 6.2831855f;
                        float colorScale = 1f + (Hash01(instanceHash + 0x5be0cd19u) * 2f - 1f) * Settings.ColorVariation;
                        float3 instanceNormal = math.normalize(math.lerp(new float3(0f, 1f, 0f), normalWS, Settings.NormalAlignment));
                        positionWS.y += Settings.SurfaceOffset;

                        GrassInstances[count] = new GrassInstanceData
                        {
                            PositionScale = new float4(positionWS, bladeHeight),
                            NormalYaw = new float4(instanceNormal, yaw),
                            ColorWidth = new float4(colorScale, colorScale, colorScale, bladeWidth)
                        };

                        float radius = math.max(bladeWidth, Settings.SurfaceOffset) * 1.5f;
                        bounds.Min = math.min(bounds.Min, positionWS + new float3(-radius, 0f, -radius));
                        bounds.Max = math.max(bounds.Max, positionWS + new float3(radius, bladeHeight, radius));
                        count++;
                    }
                }
            }

            WriteOutputs(count, bounds);
        }

        private void SampleSurface(float2 local, out float3 positionWS, out float3 normalWS)
        {
            float u = math.saturate(local.x / ChunkSize);
            float v = math.saturate(local.y / ChunkSize);
            float gridX = u * (Resolution - 1);
            float gridZ = v * (Resolution - 1);
            int x0 = math.clamp((int)math.floor(gridX), 0, Resolution - 1);
            int z0 = math.clamp((int)math.floor(gridZ), 0, Resolution - 1);
            int x1 = math.min(x0 + 1, Resolution - 1);
            int z1 = math.min(z0 + 1, Resolution - 1);
            float tx = gridX - x0;
            float tz = gridZ - z0;

            int i00 = z0 * Resolution + x0;
            int i10 = z0 * Resolution + x1;
            int i01 = z1 * Resolution + x0;
            int i11 = z1 * Resolution + x1;

            float h00 = Vertices[i00].y;
            float h10 = Vertices[i10].y;
            float h01 = Vertices[i01].y;
            float h11 = Vertices[i11].y;
            float height0 = math.lerp(h00, h10, tx);
            float height1 = math.lerp(h01, h11, tx);
            float surfaceHeight = math.lerp(height0, height1, tz);

            float3 n00 = ToFloat3(Normals[i00]);
            float3 n10 = ToFloat3(Normals[i10]);
            float3 n01 = ToFloat3(Normals[i01]);
            float3 n11 = ToFloat3(Normals[i11]);
            float3 normal0 = math.lerp(n00, n10, tx);
            float3 normal1 = math.lerp(n01, n11, tx);
            normalWS = math.normalize(math.lerp(normal0, normal1, tz));
            if (math.lengthsq(normalWS) < 0.0001f)
            {
                normalWS = new float3(0f, 1f, 0f);
            }

            positionWS = new float3(ChunkOrigin.x + local.x, surfaceHeight, ChunkOrigin.y + local.y);
        }

        private float EvaluateLayerCoverage(float height, float3 normal, float slopeAngle)
        {
            float weight = EvaluateSplatWeight(height, normal, slopeAngle, Settings.Channel);
            return math.saturate((weight - Settings.LayerThreshold) / math.max(1f - Settings.LayerThreshold, 0.0001f));
        }

        private float EvaluateSplatWeight(float height, float3 normal, float slopeAngle, int channel)
        {
            if (TerrainLayers.Length == 0)
            {
                return channel == 0 ? 1f : 0f;
            }

            float4 map0 = float4.zero;
            float4 map1 = float4.zero;
            AddWeight(ref map0, ref map1, TerrainLayers[0].Channel, 1f);

            for (int i = 1; i < TerrainLayers.Length; i++)
            {
                GrassTerrainLayerData layer = TerrainLayers[i];
                float blend = SmoothStep(layer.StartHeight, layer.StartHeight + math.max(0.0001f, layer.BlendRange), height);
                map0 *= 1f - blend;
                map1 *= 1f - blend;
                AddWeight(ref map0, ref map1, layer.Channel, blend);
            }

            NormalizeWeights(ref map0, ref map1);

            if (SlopeTextureSettings.Enabled)
            {
                float slopeBlend = SmoothStep(SlopeTextureSettings.StartAngle, SlopeTextureSettings.FullAngle, slopeAngle);
                float rockAmount = math.saturate(slopeBlend * SlopeTextureSettings.Strength);
                if (rockAmount > 0.0001f)
                {
                    map0 *= 1f - rockAmount;
                    map1 *= 1f - rockAmount;
                    AddWeight(ref map0, ref map1, (int)SlopeTextureSettings.Channel, rockAmount);
                    NormalizeWeights(ref map0, ref map1);
                }
            }

            return ReadWeight(map0, map1, channel);
        }

        private float EvaluateHeightGate(float height)
        {
            float lower = SmoothStep(Settings.MinHeight, Settings.MinHeight + Settings.HeightFadeRange, height);
            float upper = 1f - SmoothStep(Settings.MaxHeight - Settings.HeightFadeRange, Settings.MaxHeight, height);
            return math.saturate(lower * upper);
        }

        private float EvaluateSlopeGate(float slopeAngle)
        {
            float lower = SmoothStep(Settings.MinSlopeAngle - Settings.SlopeFadeRange, Settings.MinSlopeAngle, slopeAngle);
            float upper = 1f - SmoothStep(Settings.MaxSlopeAngle, Settings.MaxSlopeAngle + Settings.SlopeFadeRange, slopeAngle);
            return math.saturate(lower * upper);
        }

        private float EvaluateCoverageNoise(float2 worldXZ)
        {
            if (Settings.CoverageNoiseFrequency <= 0f || Settings.CoverageNoiseStrength <= 0f)
            {
                return 1f;
            }

            float2 seedOffset = new float2(Settings.GrassSeed * 11.17f, Settings.GrassSeed * -7.31f);
            float noiseValue = To01(noise.snoise((worldXZ + seedOffset) * Settings.CoverageNoiseFrequency));
            return math.lerp(1f, math.saturate(noiseValue * 1.8f), Settings.CoverageNoiseStrength);
        }

        private void WriteOutputs(int count, GrassChunkBounds bounds)
        {
            GrassInstanceCounter[0] = count;
            GrassBounds[0] = count > 0
                ? bounds
                : new GrassChunkBounds { Min = float3.zero, Max = float3.zero };
        }

        private static void AddWeight(ref float4 map0, ref float4 map1, int channel, float value)
        {
            switch (channel)
            {
                case 1:
                    map0.y += value;
                    break;
                case 2:
                    map0.z += value;
                    break;
                case 3:
                    map0.w += value;
                    break;
                case 4:
                    map1.x += value;
                    break;
                case 5:
                    map1.y += value;
                    break;
                case 6:
                    map1.z += value;
                    break;
                case 7:
                    map1.w += value;
                    break;
                default:
                    map0.x += value;
                    break;
            }
        }

        private static float ReadWeight(float4 map0, float4 map1, int channel)
        {
            switch (channel)
            {
                case 1:
                    return map0.y;
                case 2:
                    return map0.z;
                case 3:
                    return map0.w;
                case 4:
                    return map1.x;
                case 5:
                    return map1.y;
                case 6:
                    return map1.z;
                case 7:
                    return map1.w;
                default:
                    return map0.x;
            }
        }

        private static void NormalizeWeights(ref float4 map0, ref float4 map1)
        {
            float sum = math.csum(map0) + math.csum(map1);
            if (sum > 0.0001f)
            {
                map0 /= sum;
                map1 /= sum;
            }
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

        private static float3 ToFloat3(Vector3 value)
        {
            return new float3(value.x, value.y, value.z);
        }

        private static uint Hash(int a, int b, int c, int d, int e, int f)
        {
            unchecked
            {
                uint hash = 2166136261u;
                hash = Mix(hash, (uint)a);
                hash = Mix(hash, (uint)b);
                hash = Mix(hash, (uint)c);
                hash = Mix(hash, (uint)d);
                hash = Mix(hash, (uint)e);
                hash = Mix(hash, (uint)f);
                return Hash(hash);
            }
        }

        private static uint Mix(uint hash, uint value)
        {
            unchecked
            {
                hash ^= value;
                hash *= 16777619u;
                return hash;
            }
        }

        private static uint Hash(uint value)
        {
            unchecked
            {
                value ^= value >> 16;
                value *= 0x7feb352du;
                value ^= value >> 15;
                value *= 0x846ca68bu;
                value ^= value >> 16;
                return value;
            }
        }

        private static float Hash01(uint value)
        {
            return (Hash(value) >> 8) * (1f / 16777216f);
        }
    }

    private sealed partial class TerrainChunk
    {
        private ComputeBuffer grassInstanceBuffer;
        private ComputeBuffer grassArgsBuffer;
        private MaterialPropertyBlock grassPropertyBlock;
        private Bounds grassBounds;
        private int grassInstanceCount;
        private uint grassArgsIndexCount;
        private uint grassArgsStartIndex;
        private uint grassArgsBaseVertex;
        private bool grassArgsDirty = true;

        public bool HasGrass => grassInstanceBuffer != null && grassInstanceCount > 0;

        public void ApplyGrass(TerrainBuildTask task)
        {
            if (!task.HasGrassInstances)
            {
                ClearGrass();
                return;
            }

            int instanceCount = Mathf.Clamp(task.GrassInstanceCounter[0], 0, task.GrassInstances.Length);
            if (instanceCount == 0)
            {
                ClearGrass();
                return;
            }

            if (grassInstanceBuffer == null || grassInstanceBuffer.count < instanceCount)
            {
                ReleaseGrassInstanceBuffer();
                grassInstanceBuffer = new ComputeBuffer(instanceCount, GrassInstanceStride, ComputeBufferType.Structured);
            }

            grassInstanceBuffer.SetData(task.GrassInstances, 0, 0, instanceCount);
            grassInstanceCount = instanceCount;
            grassBounds = ToBounds(task.GrassBounds[0]);
            grassArgsDirty = true;
        }

        public void ClearGrass()
        {
            grassInstanceCount = 0;
            grassArgsDirty = true;
            ReleaseGrassInstanceBuffer();
            ReleaseGrassArgsBuffer();
        }

        public void DrawGrass(
            Mesh drawMesh,
            Material drawMaterial,
            Vector3 viewerPosition,
            Vector4 fadeDistances,
            Vector4 wind,
            Vector4 meshGrounding,
            ShadowCastingMode shadowCastingMode,
            bool receiveShadows,
            int layer)
        {
            if (!HasGrass || drawMesh == null || drawMaterial == null)
            {
                return;
            }

            EnsureGrassArgsBuffer(drawMesh);

            grassPropertyBlock ??= new MaterialPropertyBlock();
            grassPropertyBlock.Clear();
            grassPropertyBlock.SetBuffer(GrassInstancesPropertyId, grassInstanceBuffer);
            grassPropertyBlock.SetVector(GrassViewerPositionPropertyId, viewerPosition);
            grassPropertyBlock.SetVector(GrassFadeDistancesPropertyId, fadeDistances);
            grassPropertyBlock.SetVector(GrassWindPropertyId, wind);
            grassPropertyBlock.SetVector(GrassMeshGroundingPropertyId, meshGrounding);

            Graphics.DrawMeshInstancedIndirect(
                drawMesh,
                0,
                drawMaterial,
                grassBounds,
                grassArgsBuffer,
                0,
                grassPropertyBlock,
                shadowCastingMode,
                receiveShadows,
                layer);
        }

        private void EnsureGrassArgsBuffer(Mesh drawMesh)
        {
            uint indexCount = drawMesh.GetIndexCount(0);
            uint startIndex = drawMesh.GetIndexStart(0);
            uint baseVertex = (uint)drawMesh.GetBaseVertex(0);

            if (grassArgsBuffer == null)
            {
                grassArgsBuffer = new ComputeBuffer(1, sizeof(uint) * 5, ComputeBufferType.IndirectArguments);
                grassArgsDirty = true;
            }

            if (!grassArgsDirty
                && grassArgsIndexCount == indexCount
                && grassArgsStartIndex == startIndex
                && grassArgsBaseVertex == baseVertex)
            {
                return;
            }

            uint[] args =
            {
                indexCount,
                (uint)grassInstanceCount,
                startIndex,
                baseVertex,
                0u
            };

            grassArgsBuffer.SetData(args);
            grassArgsIndexCount = indexCount;
            grassArgsStartIndex = startIndex;
            grassArgsBaseVertex = baseVertex;
            grassArgsDirty = false;
        }

        private void ReleaseGrassInstanceBuffer()
        {
            if (grassInstanceBuffer == null)
            {
                return;
            }

            grassInstanceBuffer.Release();
            grassInstanceBuffer = null;
        }

        private void ReleaseGrassArgsBuffer()
        {
            if (grassArgsBuffer == null)
            {
                return;
            }

            grassArgsBuffer.Release();
            grassArgsBuffer = null;
        }

        private static Bounds ToBounds(GrassChunkBounds source)
        {
            Vector3 min = new Vector3(source.Min.x, source.Min.y, source.Min.z);
            Vector3 max = new Vector3(source.Max.x, source.Max.y, source.Max.z);
            Bounds bounds = new Bounds((min + max) * 0.5f, max - min);
            bounds.Expand(2f);
            return bounds;
        }
    }
}
