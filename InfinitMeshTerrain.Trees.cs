using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

public partial class InfinitMeshTerrain
{
    private static readonly TreeRenderPrototype[] EmptyTreeRenderPrototypes = Array.Empty<TreeRenderPrototype>();

    [Header("Trees")]
    [SerializeField] private TreeSettingsSO treeSettings;

    private readonly List<TreeRenderPrototype> treeRenderPrototypes = new List<TreeRenderPrototype>();
    private TreeSettingsSO cachedTreeRenderSettings;
    private bool treeRenderCacheDirty = true;
    private float cachedTreeTotalDensity;

    private void ValidateTreeSettings()
    {
        if (treeSettings != null)
        {
            treeSettings.ValidateValues();
        }

        treeRenderCacheDirty = true;
    }

    private void UpdateTreeDetails()
    {
        TreeSettingsSO settings = treeSettings;
        IReadOnlyList<TreeRenderPrototype> renderPrototypes = GetTreeRenderPrototypes(settings);
        if (settings == null || !settings.EnableTrees || renderPrototypes.Count == 0 || viewer == null)
        {
            ClearTreesFromRuntimeChunks();
            return;
        }

        int layer = gameObject.layer;
        ShadowCastingMode shadowCastingMode = settings.ShadowCastingMode;
        bool receiveShadows = settings.ReceiveShadows;

        foreach (Vector2Int coord in visibleChunkCoords)
        {
            if (!chunks.TryGetValue(coord, out TerrainChunk chunk) || !chunk.HasMesh)
            {
                continue;
            }

            if (!IsChunkInsideTreeDistance(coord, settings.TreeDistance))
            {
                if (settings.UnloadOutsideTreeDistance)
                {
                    chunk.ClearTrees();
                }

                continue;
            }

            if (!chunk.HasTreeBuild)
            {
                RequestBuild(coord);
                continue;
            }

            chunk.DrawTrees(layer, shadowCastingMode, receiveShadows);
        }
    }

    private bool ShouldBuildTreesForChunk(Vector2Int coord)
    {
        TreeSettingsSO settings = treeSettings;
        if (settings == null || !settings.EnableTrees || settings.MaxInstancesPerChunk <= 0 || viewer == null)
        {
            return false;
        }

        return GetTreeRenderPrototypes(settings).Count > 0
            && IsChunkInsideTreeDistance(coord, settings.TreeDistance + chunkSize * 0.75f);
    }

    private IReadOnlyList<TreeRenderPrototype> GetTreeRenderPrototypes(TreeSettingsSO settings)
    {
        if (settings == null)
        {
            return EmptyTreeRenderPrototypes;
        }

        if (!treeRenderCacheDirty && cachedTreeRenderSettings == settings)
        {
            return treeRenderPrototypes;
        }

        cachedTreeRenderSettings = settings;
        treeRenderCacheDirty = false;
        treeRenderPrototypes.Clear();
        cachedTreeTotalDensity = 0f;

        IReadOnlyList<TreePrototypeSettings> prototypes = settings.Prototypes;
        for (int i = 0; i < prototypes.Count; i++)
        {
            TreePrototypeSettings prototype = prototypes[i];
            if (prototype == null || !prototype.IsSpawnable)
            {
                continue;
            }

            TreeRenderItem[] renderItems = CollectTreeRenderItems(prototype.Prefab);
            if (renderItems.Length == 0)
            {
                continue;
            }

            treeRenderPrototypes.Add(new TreeRenderPrototype(prototype, renderItems));
            cachedTreeTotalDensity += prototype.DensityPerSquareMeter;
        }

        return treeRenderPrototypes;
    }

    private static TreeRenderItem[] CollectTreeRenderItems(GameObject prefab)
    {
        if (prefab == null)
        {
            return Array.Empty<TreeRenderItem>();
        }

        MeshRenderer[] renderers = prefab.GetComponentsInChildren<MeshRenderer>(true);
        if (renderers == null || renderers.Length == 0)
        {
            return Array.Empty<TreeRenderItem>();
        }

        List<TreeRenderItem> items = new List<TreeRenderItem>();
        Matrix4x4 rootToLocal = prefab.transform.worldToLocalMatrix;

        for (int rendererIndex = 0; rendererIndex < renderers.Length; rendererIndex++)
        {
            MeshRenderer meshRenderer = renderers[rendererIndex];
            if (meshRenderer == null || !meshRenderer.enabled)
            {
                continue;
            }

            MeshFilter meshFilter = meshRenderer.GetComponent<MeshFilter>();
            Mesh mesh = meshFilter != null ? meshFilter.sharedMesh : null;
            if (mesh == null)
            {
                continue;
            }

            Material[] materials = meshRenderer.sharedMaterials;
            if (materials == null || materials.Length == 0)
            {
                continue;
            }

            Matrix4x4 localToPrefab = rootToLocal * meshRenderer.transform.localToWorldMatrix;
            int subMeshCount = Mathf.Max(1, mesh.subMeshCount);
            for (int subMeshIndex = 0; subMeshIndex < subMeshCount; subMeshIndex++)
            {
                Material material = materials[Mathf.Min(subMeshIndex, materials.Length - 1)];
                if (material == null)
                {
                    continue;
                }

                material.enableInstancing = true;
                items.Add(new TreeRenderItem(mesh, material, subMeshIndex, localToPrefab));
            }
        }

        return items.ToArray();
    }

    private float GetTreeTotalDensity()
    {
        return cachedTreeTotalDensity;
    }

    private bool IsChunkInsideTreeDistance(Vector2Int coord, float distance)
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

    private void ClearTreesFromRuntimeChunks()
    {
        foreach (TerrainChunk chunk in chunks.Values)
        {
            chunk.ClearTrees();
        }
    }

    private void DestroyTreeRuntimeResources()
    {
        ClearTreesFromRuntimeChunks();
        treeRenderPrototypes.Clear();
        cachedTreeRenderSettings = null;
        cachedTreeTotalDensity = 0f;
        treeRenderCacheDirty = true;
    }

    private void SyncTreeSettingsSubscription()
    {
        if (subscribedTreeSettings == treeSettings)
        {
            return;
        }

        UnsubscribeTreeSettings();
        subscribedTreeSettings = treeSettings;
        treeRenderCacheDirty = true;

        if (subscribedTreeSettings != null)
        {
            subscribedTreeSettings.Changed += OnTreeSettingsChanged;
        }
    }

    private void UnsubscribeTreeSettings()
    {
        if (subscribedTreeSettings == null)
        {
            return;
        }

        subscribedTreeSettings.Changed -= OnTreeSettingsChanged;
        subscribedTreeSettings = null;
    }

    private void OnTreeSettingsChanged()
    {
        ValidateTreeSettings();
        treeRenderCacheDirty = true;
        ClearTreesFromRuntimeChunks();
        RequestVisibleChunkRebuilds();
    }

    private sealed class TreeRenderPrototype
    {
        public TreeRenderPrototype(TreePrototypeSettings settings, TreeRenderItem[] renderItems)
        {
            Settings = settings;
            RenderItems = renderItems;
        }

        public TreePrototypeSettings Settings { get; }
        public TreeRenderItem[] RenderItems { get; }
        public float DensityPerSquareMeter => Settings.DensityPerSquareMeter;
    }

    private readonly struct TreeRenderItem
    {
        public TreeRenderItem(Mesh mesh, Material material, int subMeshIndex, Matrix4x4 localToPrefab)
        {
            Mesh = mesh;
            Material = material;
            SubMeshIndex = subMeshIndex;
            LocalToPrefab = localToPrefab;
        }

        public Mesh Mesh { get; }
        public Material Material { get; }
        public int SubMeshIndex { get; }
        public Matrix4x4 LocalToPrefab { get; }
    }

    private sealed partial class TerrainChunk
    {
        private const int TreeDrawBatchSize = 1023;

        private readonly List<TreeRenderBatch> treeBatches = new List<TreeRenderBatch>();
        private bool treesBuilt;

        public bool HasTreeBuild => treesBuilt;

        public void ApplyTrees(
            TerrainBuildTask task,
            TreeSettingsSO settings,
            IReadOnlyList<TreeRenderPrototype> renderPrototypes,
            float totalDensity,
            bool shouldBuildTrees,
            int terrainSeed,
            float chunkSize,
            bool enableWater,
            float waterHeight,
            TerrainHeightLayer[] terrainLayers,
            SlopeTextureSettings slopeTextureSettings)
        {
            ClearTrees();

            if (!shouldBuildTrees
                || settings == null
                || !settings.EnableTrees
                || renderPrototypes == null
                || renderPrototypes.Count == 0
                || totalDensity <= 0f
                || settings.MaxInstancesPerChunk <= 0
                || task.BaseVertexCount <= 0
                || task.Resolution < 2)
            {
                return;
            }

            treesBuilt = true;
            TerrainHeightLayer[] sortedLayers = CopySortedLayers(terrainLayers);
            int cellsPerAxis = Mathf.Max(1, Mathf.CeilToInt(chunkSize / settings.CellSize));
            float cellSize = chunkSize / cellsPerAxis;
            float expectedPerCell = totalDensity * cellSize * cellSize;
            int maxPerCell = settings.MaxInstancesPerCell;
            int spawnedCount = 0;
            Matrix4x4 chunkLocalToWorld = gameObject.transform.localToWorldMatrix;

            for (int z = 0; z < cellsPerAxis && spawnedCount < settings.MaxInstancesPerChunk; z++)
            {
                for (int x = 0; x < cellsPerAxis && spawnedCount < settings.MaxInstancesPerChunk; x++)
                {
                    uint cellHash = Hash(Coord.x, Coord.y, x, z, terrainSeed, settings.SeedOffset);
                    int candidateCount = Mathf.FloorToInt(expectedPerCell);
                    float fractional = expectedPerCell - candidateCount;
                    if (Hash01(cellHash + 0x6d2b79f5u) < fractional)
                    {
                        candidateCount++;
                    }

                    candidateCount = Mathf.Min(candidateCount, maxPerCell);
                    for (int candidate = 0; candidate < candidateCount && spawnedCount < settings.MaxInstancesPerChunk; candidate++)
                    {
                        uint instanceHash = Hash((int)cellHash, candidate, x * 73856093, z * 19349663, terrainSeed, settings.SeedOffset);
                        TreeRenderPrototype prototype = PickPrototype(renderPrototypes, Hash01(instanceHash + 0x9e3779b9u) * totalDensity);
                        if (prototype == null)
                        {
                            continue;
                        }

                        Vector2 jitter = new Vector2(
                            Hash01(instanceHash + 0xbb67ae85u),
                            Hash01(instanceHash + 0x3c6ef372u));
                        Vector2 centeredJitter = (jitter - Vector2.one * 0.5f) * settings.Jitter + Vector2.one * 0.5f;
                        Vector2 local = (new Vector2(x, z) + centeredJitter) * cellSize;
                        local.x = Mathf.Clamp(local.x, 0.001f, chunkSize - 0.001f);
                        local.y = Mathf.Clamp(local.y, 0.001f, chunkSize - 0.001f);

                        if (!TrySampleSurface(task, local, chunkSize, out Vector3 position, out Vector3 normal))
                        {
                            continue;
                        }

                        TreePrototypeSettings prototypeSettings = prototype.Settings;
                        Vector2 worldXZ = new Vector2(Coord.x * chunkSize + local.x, Coord.y * chunkSize + local.y);
                        float coverage = EvaluateCoverage(
                            prototypeSettings,
                            sortedLayers,
                            slopeTextureSettings,
                            position.y,
                            normal,
                            worldXZ,
                            terrainSeed,
                            enableWater,
                            waterHeight);

                        if (coverage <= 0f || Hash01(instanceHash + 0xa54ff53au) > coverage)
                        {
                            continue;
                        }

                        AddTreeRenderInstance(prototype, position, normal, chunkLocalToWorld, instanceHash);
                        spawnedCount++;
                    }
                }
            }

            FinalizeTreeBatches();
        }

        public void ClearTrees()
        {
            treesBuilt = false;
            for (int i = 0; i < treeBatches.Count; i++)
            {
                treeBatches[i].Clear();
            }

            treeBatches.Clear();
        }

        public void DrawTrees(int layer, ShadowCastingMode shadowCastingMode, bool receiveShadows)
        {
            if (!treesBuilt)
            {
                return;
            }

            for (int i = 0; i < treeBatches.Count; i++)
            {
                treeBatches[i].Draw(layer, shadowCastingMode, receiveShadows);
            }
        }

        private void AddTreeRenderInstance(
            TreeRenderPrototype prototype,
            Vector3 position,
            Vector3 normal,
            Matrix4x4 chunkLocalToWorld,
            uint instanceHash)
        {
            TreePrototypeSettings settings = prototype.Settings;
            Vector3 alignedNormal = settings.AlignToNormal
                ? Vector3.Lerp(Vector3.up, normal, settings.NormalAlignment).normalized
                : Vector3.up;
            if (alignedNormal.sqrMagnitude < 0.0001f)
            {
                alignedNormal = Vector3.up;
            }

            float yaw = settings.RandomYaw ? Hash01(instanceHash + 0x510e527fu) * 360f : 0f;
            Quaternion rotation = Quaternion.FromToRotation(Vector3.up, alignedNormal) * Quaternion.Euler(0f, yaw, 0f);
            float scale = Mathf.Lerp(settings.MinScale, settings.MaxScale, Hash01(instanceHash + 0x1f83d9abu));
            Matrix4x4 rootLocalToChunk = Matrix4x4.TRS(position + alignedNormal * settings.SurfaceOffset, rotation, Vector3.one * scale);

            TreeRenderItem[] renderItems = prototype.RenderItems;
            for (int i = 0; i < renderItems.Length; i++)
            {
                TreeRenderItem renderItem = renderItems[i];
                Matrix4x4 matrix = chunkLocalToWorld * rootLocalToChunk * renderItem.LocalToPrefab;
                GetTreeRenderBatch(renderItem).Add(matrix);
            }
        }

        private TreeRenderBatch GetTreeRenderBatch(TreeRenderItem item)
        {
            for (int i = 0; i < treeBatches.Count; i++)
            {
                TreeRenderBatch batch = treeBatches[i];
                if (batch.Matches(item))
                {
                    return batch;
                }
            }

            TreeRenderBatch newBatch = new TreeRenderBatch(item.Mesh, item.Material, item.SubMeshIndex);
            treeBatches.Add(newBatch);
            return newBatch;
        }

        private void FinalizeTreeBatches()
        {
            for (int i = treeBatches.Count - 1; i >= 0; i--)
            {
                TreeRenderBatch batch = treeBatches[i];
                batch.FinalizeBatches();
                if (!batch.HasMatrices)
                {
                    treeBatches.RemoveAt(i);
                }
            }
        }

        private static TreeRenderPrototype PickPrototype(IReadOnlyList<TreeRenderPrototype> prototypes, float densityPick)
        {
            float cumulative = 0f;
            for (int i = 0; i < prototypes.Count; i++)
            {
                TreeRenderPrototype prototype = prototypes[i];
                if (prototype == null || prototype.DensityPerSquareMeter <= 0f)
                {
                    continue;
                }

                cumulative += prototype.DensityPerSquareMeter;
                if (densityPick <= cumulative)
                {
                    return prototype;
                }
            }

            return null;
        }

        private static bool TrySampleSurface(
            TerrainBuildTask task,
            Vector2 local,
            float chunkSize,
            out Vector3 position,
            out Vector3 normal)
        {
            float u = Mathf.Clamp01(local.x / chunkSize);
            float v = Mathf.Clamp01(local.y / chunkSize);
            float gridX = u * (task.Resolution - 1);
            float gridZ = v * (task.Resolution - 1);
            int x0 = Mathf.Clamp(Mathf.FloorToInt(gridX), 0, task.Resolution - 1);
            int z0 = Mathf.Clamp(Mathf.FloorToInt(gridZ), 0, task.Resolution - 1);
            int x1 = Mathf.Min(x0 + 1, task.Resolution - 1);
            int z1 = Mathf.Min(z0 + 1, task.Resolution - 1);
            float tx = gridX - x0;
            float tz = gridZ - z0;

            int i00 = z0 * task.Resolution + x0;
            int i10 = z0 * task.Resolution + x1;
            int i01 = z1 * task.Resolution + x0;
            int i11 = z1 * task.Resolution + x1;

            if (i11 >= task.BaseVertexCount)
            {
                position = default;
                normal = Vector3.up;
                return false;
            }

            float h00 = task.Vertices[i00].y;
            float h10 = task.Vertices[i10].y;
            float h01 = task.Vertices[i01].y;
            float h11 = task.Vertices[i11].y;
            float height0 = Mathf.Lerp(h00, h10, tx);
            float height1 = Mathf.Lerp(h01, h11, tx);
            float surfaceHeight = Mathf.Lerp(height0, height1, tz);

            Vector3 n00 = task.Normals[i00];
            Vector3 n10 = task.Normals[i10];
            Vector3 n01 = task.Normals[i01];
            Vector3 n11 = task.Normals[i11];
            Vector3 normal0 = Vector3.Lerp(n00, n10, tx);
            Vector3 normal1 = Vector3.Lerp(n01, n11, tx);
            normal = Vector3.Lerp(normal0, normal1, tz).normalized;
            if (normal.sqrMagnitude < 0.0001f)
            {
                normal = Vector3.up;
            }

            position = new Vector3(local.x, surfaceHeight, local.y);
            return true;
        }

        private static float EvaluateCoverage(
            TreePrototypeSettings prototype,
            TerrainHeightLayer[] sortedLayers,
            SlopeTextureSettings slopeTextureSettings,
            float height,
            Vector3 normal,
            Vector2 worldXZ,
            int terrainSeed,
            bool enableWater,
            float waterHeight)
        {
            if (prototype.AvoidWater && enableWater && height < waterHeight + prototype.WaterPadding)
            {
                return 0f;
            }

            float slopeAngle = Mathf.Acos(Mathf.Clamp(normal.y, -1f, 1f)) * Mathf.Rad2Deg;
            float lowerHeight = SmoothStep(prototype.MinHeight, prototype.MinHeight + prototype.HeightFadeRange, height);
            float upperHeight = 1f - SmoothStep(prototype.MaxHeight - prototype.HeightFadeRange, prototype.MaxHeight, height);
            float lowerSlope = SmoothStep(prototype.MinSlopeAngle - prototype.SlopeFadeRange, prototype.MinSlopeAngle, slopeAngle);
            float upperSlope = 1f - SmoothStep(prototype.MaxSlopeAngle, prototype.MaxSlopeAngle + prototype.SlopeFadeRange, slopeAngle);
            float coverage = Mathf.Clamp01(lowerHeight * upperHeight * lowerSlope * upperSlope);

            if (prototype.UseTerrainLayer)
            {
                SplatWeights weights = EvaluateSplatWeights(height, sortedLayers);
                weights = ApplySlopeTexture(weights, normal, slopeTextureSettings);
                float layerWeight = ReadWeight(weights, prototype.ChannelIndex);
                float layerCoverage = Mathf.Clamp01((layerWeight - prototype.LayerThreshold) / Mathf.Max(1f - prototype.LayerThreshold, 0.0001f));
                coverage *= layerCoverage;
            }

            if (prototype.CoverageNoiseFrequency > 0f && prototype.CoverageNoiseStrength > 0f)
            {
                float noiseValue = Mathf.PerlinNoise(
                    (worldXZ.x + terrainSeed * 13.17f) * prototype.CoverageNoiseFrequency,
                    (worldXZ.y - terrainSeed * 7.91f) * prototype.CoverageNoiseFrequency);
                coverage *= Mathf.Lerp(1f, Mathf.Clamp01(noiseValue * 1.8f), prototype.CoverageNoiseStrength);
            }

            if (prototype.ForestNoiseFrequency > 0f)
            {
                float forestValue = Mathf.PerlinNoise(
                    (worldXZ.x - terrainSeed * 31.41f) * prototype.ForestNoiseFrequency,
                    (worldXZ.y + terrainSeed * 17.23f) * prototype.ForestNoiseFrequency);
                float forestGate = SmoothStep(prototype.ForestThreshold, prototype.ForestThreshold + prototype.ForestBlendRange, forestValue);
                coverage *= forestGate;
            }

            return Mathf.Clamp01(coverage);
        }

        private static float ReadWeight(SplatWeights weights, int channel)
        {
            switch (channel)
            {
                case 1:
                    return weights.Map0.y;
                case 2:
                    return weights.Map0.z;
                case 3:
                    return weights.Map0.w;
                case 4:
                    return weights.Map1.x;
                case 5:
                    return weights.Map1.y;
                case 6:
                    return weights.Map1.z;
                case 7:
                    return weights.Map1.w;
                default:
                    return weights.Map0.x;
            }
        }

        private static float SmoothStep(float edge0, float edge1, float value)
        {
            float t = Mathf.Clamp01((value - edge0) / Mathf.Max(edge1 - edge0, 0.0001f));
            return t * t * (3f - 2f * t);
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

        private sealed class TreeRenderBatch
        {
            private readonly Mesh mesh;
            private readonly Material material;
            private readonly int subMeshIndex;
            private readonly List<Matrix4x4> matrices = new List<Matrix4x4>();
            private readonly List<Matrix4x4[]> drawBatches = new List<Matrix4x4[]>();

            public TreeRenderBatch(Mesh mesh, Material material, int subMeshIndex)
            {
                this.mesh = mesh;
                this.material = material;
                this.subMeshIndex = subMeshIndex;
            }

            public bool HasMatrices => drawBatches.Count > 0;

            public bool Matches(TreeRenderItem item)
            {
                return mesh == item.Mesh
                    && material == item.Material
                    && subMeshIndex == item.SubMeshIndex;
            }

            public void Add(Matrix4x4 matrix)
            {
                matrices.Add(matrix);
            }

            public void FinalizeBatches()
            {
                drawBatches.Clear();
                for (int start = 0; start < matrices.Count; start += TreeDrawBatchSize)
                {
                    int count = Mathf.Min(TreeDrawBatchSize, matrices.Count - start);
                    Matrix4x4[] drawBatch = new Matrix4x4[count];
                    matrices.CopyTo(start, drawBatch, 0, count);
                    drawBatches.Add(drawBatch);
                }

                matrices.Clear();
            }

            public void Draw(int layer, ShadowCastingMode shadowCastingMode, bool receiveShadows)
            {
                if (mesh == null || material == null)
                {
                    return;
                }

                for (int i = 0; i < drawBatches.Count; i++)
                {
                    Matrix4x4[] batch = drawBatches[i];
                    if (batch == null || batch.Length == 0)
                    {
                        continue;
                    }

                    Graphics.DrawMeshInstanced(
                        mesh,
                        subMeshIndex,
                        material,
                        batch,
                        batch.Length,
                        null,
                        shadowCastingMode,
                        receiveShadows,
                        layer);
                }
            }

            public void Clear()
            {
                matrices.Clear();
                drawBatches.Clear();
            }
        }
    }
}
