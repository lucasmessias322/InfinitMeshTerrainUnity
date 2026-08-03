using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

public partial class InfinitMeshTerrain
{
    private const int TreeDrawBatchSize = 1023;
    private static readonly TreeRenderPrototype[] EmptyTreeRenderPrototypes = Array.Empty<TreeRenderPrototype>();
    private static readonly TreeBiomeRenderData[] EmptyTreeBiomeRenderData = Array.Empty<TreeBiomeRenderData>();

    [Header("Trees")]
    [SerializeField] private TreeSettingsSO treeSettings;

    private readonly List<TreeRenderPrototype> treeRenderPrototypes = new List<TreeRenderPrototype>();
    private readonly List<TreeBiomeRenderData> treeBiomeRenderData = new List<TreeBiomeRenderData>();
    private readonly HashSet<ulong> removedTreeIds = new HashSet<ulong>();
    private readonly Dictionary<ulong, ActiveInteractiveTree> activeInteractiveTrees = new Dictionary<ulong, ActiveInteractiveTree>();
    private readonly Dictionary<GameObject, Stack<GameObject>> interactiveTreePools = new Dictionary<GameObject, Stack<GameObject>>();
    private readonly Dictionary<GameObject, Stack<GameObject>> prefabTreePools = new Dictionary<GameObject, Stack<GameObject>>();
    private readonly List<TreeInteractionCandidate> treeInteractionCandidates = new List<TreeInteractionCandidate>();
    private readonly List<ulong> activeInteractiveTreeRemovalBuffer = new List<ulong>();
    private readonly List<GameObject> pooledTreeDestroyBuffer = new List<GameObject>();
    private readonly Queue<Vector2Int> treeBuildQueue = new Queue<Vector2Int>();
    private readonly HashSet<Vector2Int> queuedTreeBuildChunks = new HashSet<Vector2Int>();
    private readonly Matrix4x4[] treeDrawScratch = new Matrix4x4[TreeDrawBatchSize];
    private TreeSettingsSO cachedTreeRenderSettings;
    private bool treeRenderCacheDirty = true;
    private bool cachedHasBiomeSpecificTreeSpawns;
    private float cachedGlobalTreeTotalDensity;
    private float cachedTreeMaxDensity;
    private float cachedTreeMaxRenderDistance;
    private Transform interactiveTreeRoot;
    private Transform prefabTreePoolRoot;
    private int pooledPrefabTreeCount;
    private int prefabTreeSpawnBudgetRemaining;
    private float nextInteractiveTreeUpdateTime;

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
            ClearQueuedTreeBuilds();
            ReleaseAllInteractiveTrees(false);
            ClearTreesFromRuntimeChunks();
            return;
        }

        UpdateInteractiveTrees(settings);
        prefabTreeSpawnBudgetRemaining = settings.MaxPrefabTreeSpawnsPerFrame;

        float maxRenderDistance = GetTreeMaxRenderDistance(settings);
        int layer = gameObject.layer;
        ShadowCastingMode shadowCastingMode = settings.ShadowCastingMode;
        bool receiveShadows = settings.ReceiveShadows;
        Dictionary<ulong, ActiveInteractiveTree> hiddenInteractiveTrees = settings.HideInstancedTreesWhenInteractive
            ? activeInteractiveTrees
            : null;

        foreach (Vector2Int coord in visibleChunkCoords)
        {
            if (!chunks.TryGetValue(coord, out TerrainChunk chunk) || !chunk.HasMesh)
            {
                continue;
            }

            if (!IsChunkInsideTreeDistance(coord, maxRenderDistance))
            {
                if (settings.UnloadOutsideTreeDistance)
                {
                    chunk.ClearTrees();
                }
                else
                {
                    chunk.SetPrefabTreeInstancesActive(false);
                }

                continue;
            }

            if (!chunk.HasTreeBuild)
            {
                if (chunk.HasTreeSurfaceData)
                {
                    RequestTreeBuild(coord);
                }
                else
                {
                    RequestBuild(coord);
                }

                continue;
            }

            chunk.DrawTrees(settings, viewer.position, layer, shadowCastingMode, receiveShadows, removedTreeIds, hiddenInteractiveTrees, treeDrawScratch);
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
            && IsChunkInsideTreeDistance(coord, GetTreeMaxRenderDistance(settings) + ChunkSize * 0.75f);
    }

    private void RequestTreeBuild(Vector2Int coord)
    {
        if (!queuedTreeBuildChunks.Add(coord))
        {
            return;
        }

        treeBuildQueue.Enqueue(coord);
    }

    private void ProcessQueuedTreeBuilds(TreeSettingsSO settings)
    {
        if (treeBuildQueue.Count == 0)
        {
            return;
        }

        if (settings == null || !settings.EnableTrees || viewer == null)
        {
            ClearQueuedTreeBuilds();
            return;
        }

        IReadOnlyList<TreeRenderPrototype> renderPrototypes = GetTreeRenderPrototypes(settings);
        if (renderPrototypes.Count == 0)
        {
            ClearQueuedTreeBuilds();
            return;
        }

        IReadOnlyList<TreeBiomeRenderData> biomeRenderData = GetTreeBiomeRenderData(settings);
        float globalTreeTotalDensity = GetGlobalTreeTotalDensity();
        float maxTreeDensity = GetTreeMaxDensity();
        bool useBiomeTreeSpawns = HasBiomeSpecificTreeSpawns(settings);
        int terrainSeed = GetTerrainSeed();
        float chunkSizeValue = ChunkSize;
        SlopeTextureSettings slopeTextureSettings = CreateSlopeTextureSettings();
        BiomeSamplingSettings biomeSettings = CreateBiomeSamplingSettings();
        int budget = settings.MaxTreeChunksBuiltPerFrame;
        int checkedQueuedBuilds = treeBuildQueue.Count;

        while (budget > 0 && treeBuildQueue.Count > 0 && checkedQueuedBuilds > 0)
        {
            checkedQueuedBuilds--;
            Vector2Int coord = treeBuildQueue.Dequeue();
            if (!queuedTreeBuildChunks.Remove(coord))
            {
                continue;
            }

            if (!chunks.TryGetValue(coord, out TerrainChunk chunk)
                || !visibleChunkCoords.Contains(coord)
                || !chunk.HasMesh)
            {
                continue;
            }

            if (runningTasks.ContainsKey(coord) || queuedChunks.Contains(coord))
            {
                RequestTreeBuild(coord);
                continue;
            }

            if (!ShouldBuildTreesForChunk(coord))
            {
                chunk.ClearTrees();
                continue;
            }

            if (!chunk.HasTreeSurfaceData)
            {
                RequestBuild(coord);
                continue;
            }

            chunk.ApplyTrees(
                settings,
                renderPrototypes,
                biomeRenderData,
                globalTreeTotalDensity,
                maxTreeDensity,
                useBiomeTreeSpawns,
                terrainSeed,
                chunkSizeValue,
                enableWater,
                waterHeight,
                terrainLayers,
                slopeTextureSettings,
                biomeSettings);
            budget--;
        }
    }

    private void RequestVisibleTreeRebuilds()
    {
        ClearQueuedTreeBuilds();

        foreach (Vector2Int coord in visibleChunkCoords)
        {
            if (!chunks.TryGetValue(coord, out TerrainChunk chunk))
            {
                continue;
            }

            chunk.ClearTrees();
            if (!chunk.HasMesh || !ShouldBuildTreesForChunk(coord))
            {
                continue;
            }

            if (chunk.HasTreeSurfaceData)
            {
                RequestTreeBuild(coord);
            }
            else
            {
                RequestBuild(coord);
            }
        }
    }

    private void PruneQueuedTreeBuildsToVisibleChunks()
    {
        if (treeBuildQueue.Count == 0)
        {
            return;
        }

        int queuedCount = treeBuildQueue.Count;
        queuedTreeBuildChunks.Clear();

        for (int i = 0; i < queuedCount; i++)
        {
            Vector2Int coord = treeBuildQueue.Dequeue();
            if (!visibleChunkCoords.Contains(coord)
                || !chunks.TryGetValue(coord, out TerrainChunk chunk)
                || !chunk.HasMesh
                || !queuedTreeBuildChunks.Add(coord))
            {
                continue;
            }

            treeBuildQueue.Enqueue(coord);
        }
    }

    private void ClearQueuedTreeBuilds()
    {
        treeBuildQueue.Clear();
        queuedTreeBuildChunks.Clear();
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
        treeBiomeRenderData.Clear();
        cachedHasBiomeSpecificTreeSpawns = false;
        cachedGlobalTreeTotalDensity = 0f;
        cachedTreeMaxDensity = 0f;
        cachedTreeMaxRenderDistance = 0f;

        IReadOnlyList<TreePrototypeSettings> prototypes = settings.Prototypes;
        int nextPrototypeIndex = prototypes.Count;
        for (int i = 0; i < prototypes.Count; i++)
        {
            if (!TryCreateTreeRenderPrototype(prototypes[i], i, out TreeRenderPrototype renderPrototype))
            {
                continue;
            }

            treeRenderPrototypes.Add(renderPrototype);
            cachedGlobalTreeTotalDensity += renderPrototype.DensityPerSquareMeter;
            cachedTreeMaxRenderDistance = Mathf.Max(cachedTreeMaxRenderDistance, renderPrototype.MaxRenderDistance);
        }

        BuildTreeBiomeRenderData(ref nextPrototypeIndex);
        if (!cachedHasBiomeSpecificTreeSpawns)
        {
            cachedTreeMaxDensity = cachedGlobalTreeTotalDensity;
        }

        return treeRenderPrototypes;
    }

    private IReadOnlyList<TreeBiomeRenderData> GetTreeBiomeRenderData(TreeSettingsSO settings)
    {
        if (settings == null)
        {
            return EmptyTreeBiomeRenderData;
        }

        GetTreeRenderPrototypes(settings);
        return treeBiomeRenderData;
    }

    private bool HasBiomeSpecificTreeSpawns(TreeSettingsSO settings)
    {
        if (settings == null)
        {
            return false;
        }

        GetTreeRenderPrototypes(settings);
        return cachedHasBiomeSpecificTreeSpawns;
    }

    private bool TryCreateTreeRenderPrototype(
        TreePrototypeSettings prototype,
        int prototypeIndex,
        out TreeRenderPrototype renderPrototype)
    {
        renderPrototype = null;
        if (prototype == null || !prototype.IsSpawnable)
        {
            return false;
        }

        renderPrototype = new TreeRenderPrototype(prototype, prototypeIndex, CollectTreeRenderVariations(prototype));
        if (renderPrototype.HasRenderItems || (!renderPrototype.UseGpuInstancing && renderPrototype.VariationCount > 0))
        {
            return true;
        }

        renderPrototype = null;
        return false;
    }

    private void BuildTreeBiomeRenderData(ref int nextPrototypeIndex)
    {
        int biomeCount = GetTerrainBiomeCount();
        if (biomeCount <= 0 || biomes == null)
        {
            return;
        }

        int scannedCount = 0;
        for (int i = 0; i < biomes.Length && scannedCount < MaxTerrainBiomeCount; i++)
        {
            TerrainBiomeSO biome = biomes[i];
            if (biome == null)
            {
                continue;
            }

            biome.ValidateValues();
            List<TreeRenderPrototype> biomePrototypes = new List<TreeRenderPrototype>();
            float totalDensity = 0f;
            IReadOnlyList<TreePrototypeSettings> prototypes = biome.TreePrototypes;
            for (int prototypeIndex = 0; prototypeIndex < prototypes.Count; prototypeIndex++)
            {
                if (!TryCreateTreeRenderPrototype(
                    prototypes[prototypeIndex],
                    nextPrototypeIndex,
                    out TreeRenderPrototype renderPrototype))
                {
                    continue;
                }

                nextPrototypeIndex++;
                treeRenderPrototypes.Add(renderPrototype);
                biomePrototypes.Add(renderPrototype);
                totalDensity += renderPrototype.DensityPerSquareMeter;
                cachedTreeMaxRenderDistance = Mathf.Max(cachedTreeMaxRenderDistance, renderPrototype.MaxRenderDistance);
            }

            if (totalDensity > 0f)
            {
                cachedHasBiomeSpecificTreeSpawns = true;
                cachedTreeMaxDensity = Mathf.Max(cachedTreeMaxDensity, totalDensity);
            }

            treeBiomeRenderData.Add(new TreeBiomeRenderData(
                new float4(
                    biome.MinDistanceFromCenter,
                    biome.MaxDistanceFromCenter,
                    biome.SelectionWeight,
                    scannedCount),
                biomePrototypes.ToArray(),
                totalDensity));
            scannedCount++;
        }

        treeBiomeRenderData.Sort(CompareTreeBiomeRenderData);
    }

    private static int CompareTreeBiomeRenderData(TreeBiomeRenderData a, TreeBiomeRenderData b)
    {
        int minComparison = a.DistanceRange.x.CompareTo(b.DistanceRange.x);
        if (minComparison != 0)
        {
            return minComparison;
        }

        int maxComparison = a.DistanceRange.y.CompareTo(b.DistanceRange.y);
        return maxComparison != 0 ? maxComparison : a.DistanceRange.w.CompareTo(b.DistanceRange.w);
    }

    private static TreeRenderVariation[] CollectTreeRenderVariations(TreePrototypeSettings prototype)
    {
        if (prototype == null || prototype.PrefabVariationCount <= 0)
        {
            return Array.Empty<TreeRenderVariation>();
        }

        List<TreeRenderVariation> variations = new List<TreeRenderVariation>();
        for (int variationIndex = 0; variationIndex < prototype.PrefabVariationCount; variationIndex++)
        {
            GameObject prefab = prototype.GetPrefabVariation(variationIndex);
            if (prefab == null)
            {
                continue;
            }

            TreeRenderLod[] renderLods = prototype.UseGpuInstancing
                ? CollectTreeRenderLods(prefab)
                : Array.Empty<TreeRenderLod>();
            TreeRenderVariation variation = new TreeRenderVariation(variationIndex, prefab, renderLods);
            if (variation.HasRenderItems || !prototype.UseGpuInstancing)
            {
                variations.Add(variation);
            }
        }

        return variations.ToArray();
    }

    public bool IsProceduralTreeRemoved(ulong treeId)
    {
        return removedTreeIds.Contains(treeId);
    }

    public ulong[] GetRemovedProceduralTreeIds()
    {
        ulong[] ids = new ulong[removedTreeIds.Count];
        removedTreeIds.CopyTo(ids);
        return ids;
    }

    public void SetRemovedProceduralTreeIds(IEnumerable<ulong> treeIds)
    {
        removedTreeIds.Clear();

        if (treeIds != null)
        {
            foreach (ulong treeId in treeIds)
            {
                removedTreeIds.Add(treeId);
            }
        }

        ReleaseRemovedInteractiveTrees();
    }

    public bool SetProceduralTreeRemoved(ulong treeId, bool removed)
    {
        bool changed = removed ? removedTreeIds.Add(treeId) : removedTreeIds.Remove(treeId);
        if (removed)
        {
            ReleaseInteractiveTree(treeId, false);
        }

        return changed;
    }

    public bool TryDamageProceduralTree(ulong treeId, float damage, Vector3 hitPoint, Vector3 impulse)
    {
        if (!activeInteractiveTrees.TryGetValue(treeId, out ActiveInteractiveTree activeTree)
            || activeTree.Component == null)
        {
            return false;
        }

        activeTree.Component.ApplyDamage(damage, hitPoint, impulse);
        return true;
    }

    internal void NotifyProceduralTreeDestroyed(ProceduralTreeInstance tree, Vector3 hitPoint, Vector3 impulse)
    {
        if (tree == null)
        {
            return;
        }

        if (!activeInteractiveTrees.TryGetValue(tree.TreeId, out ActiveInteractiveTree activeTree))
        {
            removedTreeIds.Add(tree.TreeId);
            return;
        }

        removedTreeIds.Add(tree.TreeId);
        SpawnTreeAftermath(activeTree, hitPoint, impulse);
        ReleaseInteractiveTree(tree.TreeId, false);
    }

    private void UpdateInteractiveTrees(TreeSettingsSO settings)
    {
        if (settings == null
            || !settings.EnableInteractiveTrees
            || settings.MaxInteractiveInstances <= 0
            || settings.InteractiveDistance <= 0f
            || viewer == null)
        {
            nextInteractiveTreeUpdateTime = 0f;
            ReleaseAllInteractiveTrees(false);
            return;
        }

        float updateInterval = settings.InteractiveUpdateInterval;
        if (updateInterval > 0f)
        {
            float now = Time.time;
            if (now < nextInteractiveTreeUpdateTime)
            {
                return;
            }

            nextInteractiveTreeUpdateTime = now + updateInterval;
        }
        else
        {
            nextInteractiveTreeUpdateTime = 0f;
        }

        EnsureInteractiveTreeRoot();
        Vector3 viewerPosition = viewer.position;
        float releaseDistance = settings.InteractiveReleaseDistance;
        float releaseDistanceSqr = releaseDistance * releaseDistance;
        activeInteractiveTreeRemovalBuffer.Clear();

        foreach (KeyValuePair<ulong, ActiveInteractiveTree> pair in activeInteractiveTrees)
        {
            ActiveInteractiveTree activeTree = pair.Value;
            bool shouldRelease = activeTree == null
                || activeTree.Instance == null
                || removedTreeIds.Contains(pair.Key)
                || !visibleChunkCoords.Contains(activeTree.Coord)
                || !chunks.TryGetValue(activeTree.Coord, out TerrainChunk chunk)
                || !chunk.HasTreeInstance(pair.Key)
                || !activeTree.Data.IsInsideRenderDistance(viewerPosition)
                || (activeTree.Instance.transform.position - viewerPosition).sqrMagnitude > releaseDistanceSqr;

            if (shouldRelease)
            {
                activeInteractiveTreeRemovalBuffer.Add(pair.Key);
            }
        }

        for (int i = 0; i < activeInteractiveTreeRemovalBuffer.Count; i++)
        {
            ReleaseInteractiveTree(activeInteractiveTreeRemovalBuffer[i], false);
        }

        TrimInteractiveTreeBudget(settings.MaxInteractiveInstances, viewerPosition);
        if (activeInteractiveTrees.Count >= settings.MaxInteractiveInstances)
        {
            return;
        }

        float interactiveDistance = settings.InteractiveDistance;
        float interactiveDistanceSqr = interactiveDistance * interactiveDistance;
        treeInteractionCandidates.Clear();

        foreach (Vector2Int coord in visibleChunkCoords)
        {
            if (!chunks.TryGetValue(coord, out TerrainChunk chunk) || !chunk.HasTreeBuild)
            {
                continue;
            }

            IReadOnlyList<TreeInstanceData> instances = chunk.TreeInstances;
            for (int i = 0; i < instances.Count; i++)
            {
                TreeInstanceData instance = instances[i];
                if (removedTreeIds.Contains(instance.Id)
                    || activeInteractiveTrees.ContainsKey(instance.Id)
                    || instance.PrototypeSettings == null
                    || instance.PrototypeSettings.GetPrefabVariation(instance.VariationIndex) == null)
                {
                    continue;
                }

                float distanceSqr = (instance.Position - viewerPosition).sqrMagnitude;
                if (distanceSqr <= interactiveDistanceSqr && instance.IsInsideRenderDistance(viewerPosition))
                {
                    treeInteractionCandidates.Add(new TreeInteractionCandidate(instance, distanceSqr));
                }
            }
        }

        treeInteractionCandidates.Sort((a, b) => a.DistanceSqr.CompareTo(b.DistanceSqr));

        int spawnBudget = Mathf.Min(
            settings.MaxInteractiveSpawnsPerFrame,
            settings.MaxInteractiveInstances - activeInteractiveTrees.Count);

        for (int i = 0; i < treeInteractionCandidates.Count && spawnBudget > 0; i++)
        {
            if (TrySpawnInteractiveTree(treeInteractionCandidates[i].Instance))
            {
                spawnBudget--;
            }
        }
    }

    private void TrimInteractiveTreeBudget(int maxInteractiveInstances, Vector3 viewerPosition)
    {
        while (activeInteractiveTrees.Count > maxInteractiveInstances)
        {
            ulong farthestId = 0UL;
            float farthestDistanceSqr = float.MinValue;

            foreach (KeyValuePair<ulong, ActiveInteractiveTree> pair in activeInteractiveTrees)
            {
                ActiveInteractiveTree activeTree = pair.Value;
                if (activeTree == null || activeTree.Instance == null)
                {
                    farthestId = pair.Key;
                    farthestDistanceSqr = float.MaxValue;
                    break;
                }

                float distanceSqr = (activeTree.Instance.transform.position - viewerPosition).sqrMagnitude;
                if (distanceSqr > farthestDistanceSqr)
                {
                    farthestDistanceSqr = distanceSqr;
                    farthestId = pair.Key;
                }
            }

            if (farthestDistanceSqr == float.MinValue)
            {
                break;
            }

            ReleaseInteractiveTree(farthestId, false);
        }
    }

    private bool TrySpawnInteractiveTree(TreeInstanceData data)
    {
        TreePrototypeSettings prototypeSettings = data.PrototypeSettings;
        GameObject prefab = prototypeSettings != null
            ? prototypeSettings.GetPrefabVariation(data.VariationIndex)
            : null;
        if (prefab == null || activeInteractiveTrees.ContainsKey(data.Id))
        {
            return false;
        }

        GameObject instance = RentInteractiveTree(prefab);
        if (instance == null)
        {
            return false;
        }

        Transform instanceTransform = instance.transform;
        instanceTransform.SetParent(interactiveTreeRoot, true);
        instanceTransform.SetPositionAndRotation(data.Position, data.Rotation);
        instanceTransform.localScale = data.Scale;
        instance.SetActive(true);

        ProceduralTreeInstance treeInstance = instance.GetComponent<ProceduralTreeInstance>();
        if (treeInstance == null)
        {
            treeInstance = instance.AddComponent<ProceduralTreeInstance>();
        }

        treeInstance.Initialize(
            this,
            data.Id,
            data.Coord,
            data.PrototypeIndex,
            prototypeSettings.MaxHealth);

        activeInteractiveTrees.Add(data.Id, new ActiveInteractiveTree(data, prefab, instance, treeInstance));
        return true;
    }

    private GameObject RentInteractiveTree(GameObject prefab)
    {
        if (prefab == null)
        {
            return null;
        }

        if (interactiveTreePools.TryGetValue(prefab, out Stack<GameObject> pool))
        {
            while (pool.Count > 0)
            {
                GameObject pooled = pool.Pop();
                if (pooled != null)
                {
                    return pooled;
                }
            }
        }

        return Instantiate(prefab);
    }

    private void ReleaseInteractiveTree(ulong treeId, bool destroyInstance)
    {
        if (!activeInteractiveTrees.TryGetValue(treeId, out ActiveInteractiveTree activeTree))
        {
            return;
        }

        activeInteractiveTrees.Remove(treeId);

        if (activeTree == null || activeTree.Instance == null)
        {
            return;
        }

        if (destroyInstance)
        {
            DestroyRuntimeObject(activeTree.Instance);
            return;
        }

        activeTree.Component?.ResetRuntimeState();
        activeTree.Instance.SetActive(false);
        if (interactiveTreeRoot != null)
        {
            activeTree.Instance.transform.SetParent(interactiveTreeRoot, true);
        }

        if (!interactiveTreePools.TryGetValue(activeTree.Prefab, out Stack<GameObject> pool))
        {
            pool = new Stack<GameObject>();
            interactiveTreePools.Add(activeTree.Prefab, pool);
        }

        pool.Push(activeTree.Instance);
    }

    private void ReleaseRemovedInteractiveTrees()
    {
        activeInteractiveTreeRemovalBuffer.Clear();
        foreach (ulong treeId in activeInteractiveTrees.Keys)
        {
            if (removedTreeIds.Contains(treeId))
            {
                activeInteractiveTreeRemovalBuffer.Add(treeId);
            }
        }

        for (int i = 0; i < activeInteractiveTreeRemovalBuffer.Count; i++)
        {
            ReleaseInteractiveTree(activeInteractiveTreeRemovalBuffer[i], false);
        }
    }

    private void ReleaseInteractiveTreesForChunk(Vector2Int coord, bool destroyInstances)
    {
        activeInteractiveTreeRemovalBuffer.Clear();
        foreach (KeyValuePair<ulong, ActiveInteractiveTree> pair in activeInteractiveTrees)
        {
            if (pair.Value != null && pair.Value.Coord == coord)
            {
                activeInteractiveTreeRemovalBuffer.Add(pair.Key);
            }
        }

        for (int i = 0; i < activeInteractiveTreeRemovalBuffer.Count; i++)
        {
            ReleaseInteractiveTree(activeInteractiveTreeRemovalBuffer[i], destroyInstances);
        }
    }

    private void ReleaseAllInteractiveTrees(bool destroyInstances)
    {
        activeInteractiveTreeRemovalBuffer.Clear();
        foreach (ulong treeId in activeInteractiveTrees.Keys)
        {
            activeInteractiveTreeRemovalBuffer.Add(treeId);
        }

        for (int i = 0; i < activeInteractiveTreeRemovalBuffer.Count; i++)
        {
            ReleaseInteractiveTree(activeInteractiveTreeRemovalBuffer[i], destroyInstances);
        }

        if (!destroyInstances)
        {
            return;
        }

        pooledTreeDestroyBuffer.Clear();
        foreach (Stack<GameObject> pool in interactiveTreePools.Values)
        {
            while (pool.Count > 0)
            {
                GameObject instance = pool.Pop();
                if (instance != null)
                {
                    pooledTreeDestroyBuffer.Add(instance);
                }
            }
        }

        interactiveTreePools.Clear();
        for (int i = 0; i < pooledTreeDestroyBuffer.Count; i++)
        {
            DestroyRuntimeObject(pooledTreeDestroyBuffer[i]);
        }

        pooledTreeDestroyBuffer.Clear();

        if (interactiveTreeRoot != null)
        {
            DestroyRuntimeObject(interactiveTreeRoot.gameObject);
            interactiveTreeRoot = null;
        }
    }

    private void EnsureInteractiveTreeRoot()
    {
        if (interactiveTreeRoot != null)
        {
            return;
        }

        GameObject root = new GameObject("Interactive Tree Runtime");
        root.transform.SetParent(transform, false);
        interactiveTreeRoot = root.transform;
    }

    private GameObject RentPrefabTree(GameObject prefab)
    {
        if (prefab == null || prefabTreeSpawnBudgetRemaining <= 0)
        {
            return null;
        }

        prefabTreeSpawnBudgetRemaining--;
        if (prefabTreePools.TryGetValue(prefab, out Stack<GameObject> pool))
        {
            while (pool.Count > 0)
            {
                GameObject pooled = pool.Pop();
                pooledPrefabTreeCount = Mathf.Max(0, pooledPrefabTreeCount - 1);
                if (pooled != null)
                {
                    return pooled;
                }
            }
        }

        return Instantiate(prefab);
    }

    private void ReleasePrefabTree(GameObject prefab, GameObject instance, bool destroyInstance)
    {
        if (instance == null)
        {
            return;
        }

        if (prefab == null
            || destroyInstance
            || !Application.isPlaying
            || pooledPrefabTreeCount >= GetMaxPooledPrefabTreeCount())
        {
            DestroyRuntimeObject(instance);
            return;
        }

        EnsurePrefabTreePoolRoot();
        instance.SetActive(false);
        instance.transform.SetParent(prefabTreePoolRoot, false);

        if (!prefabTreePools.TryGetValue(prefab, out Stack<GameObject> pool))
        {
            pool = new Stack<GameObject>();
            prefabTreePools.Add(prefab, pool);
        }

        pool.Push(instance);
        pooledPrefabTreeCount++;
    }

    private int GetMaxPooledPrefabTreeCount()
    {
        return treeSettings != null
            ? treeSettings.MaxPooledPrefabTreeInstances
            : TreeSettingsSO.DefaultMaxPooledPrefabTreeInstances;
    }

    private void EnsurePrefabTreePoolRoot()
    {
        if (prefabTreePoolRoot != null)
        {
            return;
        }

        GameObject root = new GameObject("Prefab Tree Pool");
        root.transform.SetParent(transform, false);
        root.SetActive(false);
        prefabTreePoolRoot = root.transform;
    }

    private void DestroyPrefabTreePools()
    {
        pooledTreeDestroyBuffer.Clear();
        foreach (Stack<GameObject> pool in prefabTreePools.Values)
        {
            while (pool.Count > 0)
            {
                GameObject instance = pool.Pop();
                if (instance != null)
                {
                    pooledTreeDestroyBuffer.Add(instance);
                }
            }
        }

        prefabTreePools.Clear();
        pooledPrefabTreeCount = 0;
        for (int i = 0; i < pooledTreeDestroyBuffer.Count; i++)
        {
            DestroyRuntimeObject(pooledTreeDestroyBuffer[i]);
        }

        pooledTreeDestroyBuffer.Clear();

        if (prefabTreePoolRoot != null)
        {
            DestroyRuntimeObject(prefabTreePoolRoot.gameObject);
            prefabTreePoolRoot = null;
        }
    }

    private void SpawnTreeAftermath(ActiveInteractiveTree activeTree, Vector3 hitPoint, Vector3 impulse)
    {
        TreePrototypeSettings prototypeSettings = activeTree.Data.PrototypeSettings;
        if (prototypeSettings == null)
        {
            return;
        }

        TreeInstanceData data = activeTree.Data;
        SpawnConfiguredTreePrefab(prototypeSettings.StumpPrefab, data.Position, data.Rotation, data.Scale, Vector3.zero, Vector3.zero);

        Vector3 fallImpulse = impulse.sqrMagnitude > 0.0001f
            ? impulse
            : data.Rotation * Vector3.forward * 2.5f;
        SpawnConfiguredTreePrefab(prototypeSettings.FelledPrefab, data.Position, data.Rotation, data.Scale, hitPoint, fallImpulse);
        SpawnResourceDrops(prototypeSettings, data, fallImpulse);
    }

    private void SpawnConfiguredTreePrefab(
        GameObject prefab,
        Vector3 position,
        Quaternion rotation,
        Vector3 scale,
        Vector3 hitPoint,
        Vector3 impulse)
    {
        if (prefab == null)
        {
            return;
        }

        GameObject instance = Instantiate(prefab, position, rotation);
        instance.transform.localScale = scale;

        if (interactiveTreeRoot != null)
        {
            instance.transform.SetParent(interactiveTreeRoot, true);
        }

        Rigidbody body = instance.GetComponent<Rigidbody>();
        if (body != null && impulse.sqrMagnitude > 0.0001f)
        {
            Vector3 forcePoint = hitPoint.sqrMagnitude > 0.0001f ? hitPoint : position + Vector3.up;
            body.AddForceAtPosition(impulse, forcePoint, ForceMode.Impulse);
        }
    }

    private void SpawnResourceDrops(TreePrototypeSettings prototypeSettings, TreeInstanceData data, Vector3 impulse)
    {
        GameObject prefab = prototypeSettings.ResourceDropPrefab;
        if (prefab == null || prototypeSettings.MaxResourceDrops <= 0)
        {
            return;
        }

        int dropRange = prototypeSettings.MaxResourceDrops - prototypeSettings.MinResourceDrops + 1;
        int dropCount = prototypeSettings.MinResourceDrops + Mathf.FloorToInt(HashTree01(data.Id, 0x31415926u) * dropRange);
        dropCount = Mathf.Clamp(dropCount, prototypeSettings.MinResourceDrops, prototypeSettings.MaxResourceDrops);
        float scatterRadius = prototypeSettings.ResourceDropScatterRadius;

        for (int i = 0; i < dropCount; i++)
        {
            float angle = HashTree01(data.Id, (uint)(0x9e3779b9u + i * 97u)) * Mathf.PI * 2f;
            float radius = scatterRadius * Mathf.Sqrt(HashTree01(data.Id, (uint)(0x7f4a7c15u + i * 131u)));
            Vector3 offset = new Vector3(Mathf.Cos(angle) * radius, 0.65f, Mathf.Sin(angle) * radius);
            GameObject drop = Instantiate(prefab, data.Position + offset, Quaternion.identity);

            Rigidbody body = drop.GetComponent<Rigidbody>();
            if (body != null)
            {
                Vector3 scatterImpulse = offset.normalized * 0.8f + Vector3.up * 1.2f;
                if (impulse.sqrMagnitude > 0.0001f)
                {
                    scatterImpulse += impulse.normalized * 0.6f;
                }

                body.AddForce(scatterImpulse, ForceMode.Impulse);
            }
        }
    }

    private static TreeRenderLod[] CollectTreeRenderLods(GameObject prefab)
    {
        if (prefab == null)
        {
            return Array.Empty<TreeRenderLod>();
        }

        LODGroup[] lodGroups = prefab.GetComponentsInChildren<LODGroup>(true);
        if (lodGroups != null && lodGroups.Length > 0)
        {
            int maxLodCount = 0;
            LOD[][] lodGroupsData = new LOD[lodGroups.Length][];
            for (int i = 0; i < lodGroups.Length; i++)
            {
                if (lodGroups[i] == null)
                {
                    lodGroupsData[i] = Array.Empty<LOD>();
                    continue;
                }

                LOD[] lods = lodGroups[i].GetLODs();
                lodGroupsData[i] = lods != null ? lods : Array.Empty<LOD>();
                maxLodCount = Mathf.Max(maxLodCount, lodGroupsData[i].Length);
            }

            if (maxLodCount > 0)
            {
                TreeRenderLod[] renderLods = new TreeRenderLod[maxLodCount];
                List<Renderer> lodRenderers = new List<Renderer>();
                bool hasAnyRenderItems = false;

                for (int lodIndex = 0; lodIndex < maxLodCount; lodIndex++)
                {
                    lodRenderers.Clear();
                    for (int groupIndex = 0; groupIndex < lodGroupsData.Length; groupIndex++)
                    {
                        LOD[] lods = lodGroupsData[groupIndex];
                        if (lods.Length == 0)
                        {
                            continue;
                        }

                        int sourceLodIndex = Mathf.Min(lodIndex, lods.Length - 1);
                        Renderer[] renderers = lods[sourceLodIndex].renderers;
                        if (renderers == null)
                        {
                            continue;
                        }

                        for (int rendererIndex = 0; rendererIndex < renderers.Length; rendererIndex++)
                        {
                            Renderer renderer = renderers[rendererIndex];
                            if (renderer != null && !lodRenderers.Contains(renderer))
                            {
                                lodRenderers.Add(renderer);
                            }
                        }
                    }

                    TreeRenderItem[] renderItems = CollectTreeRenderItems(prefab, lodRenderers);
                    renderLods[lodIndex] = new TreeRenderLod(lodIndex, renderItems);
                    hasAnyRenderItems |= renderItems.Length > 0;
                }

                if (hasAnyRenderItems)
                {
                    return renderLods;
                }
            }
        }

        MeshRenderer[] meshRenderers = prefab.GetComponentsInChildren<MeshRenderer>(true);
        TreeRenderItem[] fallbackItems = CollectTreeRenderItems(prefab, meshRenderers);
        if (fallbackItems.Length == 0)
        {
            return Array.Empty<TreeRenderLod>();
        }

        return new[] { new TreeRenderLod(0, fallbackItems) };
    }

    private static TreeRenderItem[] CollectTreeRenderItems(GameObject prefab, IEnumerable<Renderer> renderers)
    {
        List<TreeRenderItem> items = new List<TreeRenderItem>();
        Matrix4x4 rootToLocal = prefab.transform.worldToLocalMatrix;

        foreach (Renderer renderer in renderers)
        {
            MeshRenderer meshRenderer = renderer as MeshRenderer;
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

    private float GetGlobalTreeTotalDensity()
    {
        return cachedGlobalTreeTotalDensity;
    }

    private float GetTreeMaxDensity()
    {
        return cachedTreeMaxDensity;
    }

    private float GetTreeMaxRenderDistance(TreeSettingsSO settings)
    {
        if (settings == null)
        {
            return 0f;
        }

        GetTreeRenderPrototypes(settings);
        return cachedTreeMaxRenderDistance > 0f
            ? cachedTreeMaxRenderDistance
            : settings.TreeDistance;
    }

    private bool IsChunkInsideTreeDistance(Vector2Int coord, float distance)
    {
        if (viewer == null)
        {
            return false;
        }

        Vector2 chunkCenter = new Vector2((coord.x + 0.5f) * ChunkSize, (coord.y + 0.5f) * ChunkSize);
        Vector2 viewerPosition = new Vector2(viewer.position.x, viewer.position.z);
        float chunkRadius = ChunkSize * 0.707107f;
        float allowedDistance = Mathf.Max(0f, distance) + chunkRadius;
        return (chunkCenter - viewerPosition).sqrMagnitude <= allowedDistance * allowedDistance;
    }

    private void ClearTreesFromRuntimeChunks()
    {
        ClearQueuedTreeBuilds();
        ReleaseAllInteractiveTrees(false);
        foreach (TerrainChunk chunk in chunks.Values)
        {
            chunk.ClearTrees();
        }
    }

    private void DestroyTreeRuntimeResources()
    {
        ReleaseAllInteractiveTrees(true);
        ClearTreesFromRuntimeChunks();
        DestroyPrefabTreePools();
        treeRenderPrototypes.Clear();
        treeBiomeRenderData.Clear();
        cachedTreeRenderSettings = null;
        cachedHasBiomeSpecificTreeSpawns = false;
        cachedGlobalTreeTotalDensity = 0f;
        cachedTreeMaxDensity = 0f;
        cachedTreeMaxRenderDistance = 0f;
        prefabTreeSpawnBudgetRemaining = 0;
        nextInteractiveTreeUpdateTime = 0f;
        treeInteractionCandidates.Clear();
        ClearQueuedTreeBuilds();
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
        nextInteractiveTreeUpdateTime = 0f;
        ReleaseAllInteractiveTrees(true);
        RequestVisibleTreeRebuilds();
    }

    private static void DestroyRuntimeObject(UnityEngine.Object target)
    {
        if (target == null)
        {
            return;
        }

        if (Application.isPlaying)
        {
            Destroy(target);
        }
        else
        {
            DestroyImmediate(target);
        }
    }

    private static float HashTree01(ulong treeId, uint salt)
    {
        unchecked
        {
            uint low = (uint)treeId;
            uint high = (uint)(treeId >> 32);
            uint value = low ^ (high * 0x9e3779b9u) ^ salt;
            value ^= value >> 16;
            value *= 0x7feb352du;
            value ^= value >> 15;
            value *= 0x846ca68bu;
            value ^= value >> 16;
            return (value >> 8) * (1f / 16777216f);
        }
    }

    private sealed class TreeRenderPrototype
    {
        public TreeRenderPrototype(TreePrototypeSettings settings, int prototypeIndex, TreeRenderVariation[] variations)
        {
            Settings = settings;
            PrototypeIndex = prototypeIndex;
            Variations = variations != null ? variations : Array.Empty<TreeRenderVariation>();
        }

        public TreePrototypeSettings Settings { get; }
        public int PrototypeIndex { get; }
        public TreeRenderVariation[] Variations { get; }
        public int VariationCount => Variations.Length;
        public int LodCount
        {
            get
            {
                int lodCount = 1;
                for (int i = 0; i < Variations.Length; i++)
                {
                    lodCount = Mathf.Max(lodCount, Variations[i].LodCount);
                }

                return lodCount;
            }
        }
        public float DensityPerSquareMeter => Settings.DensityPerSquareMeter;
        public bool UseGpuInstancing => Settings.UseGpuInstancing;
        public float MaxRenderDistance => Settings.MaxRenderDistance;

        public bool HasRenderItems
        {
            get
            {
                for (int i = 0; i < Variations.Length; i++)
                {
                    if (Variations[i].HasRenderItems)
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        public TreeRenderVariation PickVariation(uint instanceHash)
        {
            if (Variations.Length == 0)
            {
                return default;
            }

            int variationIndex = Mathf.FloorToInt(Hash01(instanceHash + 0x243f6a88u) * Variations.Length);
            variationIndex = Mathf.Clamp(variationIndex, 0, Variations.Length - 1);
            return Variations[variationIndex];
        }

        private static float Hash01(uint value)
        {
            unchecked
            {
                value ^= value >> 16;
                value *= 0x7feb352du;
                value ^= value >> 15;
                value *= 0x846ca68bu;
                value ^= value >> 16;
                return (value >> 8) * (1f / 16777216f);
            }
        }
    }

    private sealed class TreeBiomeRenderData
    {
        public TreeBiomeRenderData(float4 distanceRange, TreeRenderPrototype[] prototypes, float totalDensity)
        {
            DistanceRange = distanceRange;
            Prototypes = prototypes != null ? prototypes : Array.Empty<TreeRenderPrototype>();
            TotalDensity = Mathf.Max(0f, totalDensity);
        }

        public float4 DistanceRange { get; }
        public TreeRenderPrototype[] Prototypes { get; }
        public float TotalDensity { get; }
        public bool HasPrototypes => TotalDensity > 0f && Prototypes.Length > 0;
    }

    private readonly struct TreeRenderVariation
    {
        public TreeRenderVariation(int sourceVariationIndex, GameObject prefab, TreeRenderLod[] renderLods)
        {
            SourceVariationIndex = sourceVariationIndex;
            Prefab = prefab;
            RenderLods = renderLods != null ? renderLods : Array.Empty<TreeRenderLod>();
        }

        public int SourceVariationIndex { get; }
        public GameObject Prefab { get; }
        public TreeRenderLod[] RenderLods { get; }
        public int LodCount => RenderLods != null ? RenderLods.Length : 0;

        public bool HasRenderItems
        {
            get
            {
                if (RenderLods == null)
                {
                    return false;
                }

                for (int i = 0; i < RenderLods.Length; i++)
                {
                    if (RenderLods[i].HasRenderItems)
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        public TreeRenderItem[] GetRenderItems(int lodIndex)
        {
            if (RenderLods == null || RenderLods.Length == 0)
            {
                return Array.Empty<TreeRenderItem>();
            }

            int clampedLodIndex = Mathf.Clamp(lodIndex, 0, RenderLods.Length - 1);
            for (int i = clampedLodIndex; i < RenderLods.Length; i++)
            {
                if (RenderLods[i].HasRenderItems)
                {
                    return RenderLods[i].RenderItems;
                }
            }

            for (int i = clampedLodIndex - 1; i >= 0; i--)
            {
                if (RenderLods[i].HasRenderItems)
                {
                    return RenderLods[i].RenderItems;
                }
            }

            return Array.Empty<TreeRenderItem>();
        }
    }

    private readonly struct TreeInstanceData
    {
        public TreeInstanceData(
            ulong id,
            Vector2Int coord,
            int prototypeIndex,
            int variationIndex,
            TreePrototypeSettings prototypeSettings,
            Vector3 position,
            Quaternion rotation,
            Vector3 scale,
            Vector3 normal)
        {
            Id = id;
            Coord = coord;
            PrototypeIndex = prototypeIndex;
            VariationIndex = variationIndex;
            PrototypeSettings = prototypeSettings;
            Position = position;
            Rotation = rotation;
            Scale = scale;
            Normal = normal;
        }

        public ulong Id { get; }
        public Vector2Int Coord { get; }
        public int PrototypeIndex { get; }
        public int VariationIndex { get; }
        public TreePrototypeSettings PrototypeSettings { get; }
        public Vector3 Position { get; }
        public Quaternion Rotation { get; }
        public Vector3 Scale { get; }
        public Vector3 Normal { get; }

        public bool IsInsideRenderDistance(Vector3 viewerPosition)
        {
            float maxRenderDistance = PrototypeSettings != null
                ? PrototypeSettings.MaxRenderDistance
                : TreeSettingsSO.DefaultTreeDistance;
            float dx = Position.x - viewerPosition.x;
            float dz = Position.z - viewerPosition.z;
            return dx * dx + dz * dz <= maxRenderDistance * maxRenderDistance;
        }
    }

    private readonly struct TreeInteractionCandidate
    {
        public TreeInteractionCandidate(TreeInstanceData instance, float distanceSqr)
        {
            Instance = instance;
            DistanceSqr = distanceSqr;
        }

        public TreeInstanceData Instance { get; }
        public float DistanceSqr { get; }
    }

    private sealed class ActiveInteractiveTree
    {
        public ActiveInteractiveTree(
            TreeInstanceData data,
            GameObject prefab,
            GameObject instance,
            ProceduralTreeInstance component)
        {
            Data = data;
            Prefab = prefab;
            Instance = instance;
            Component = component;
        }

        public TreeInstanceData Data { get; }
        public GameObject Prefab { get; }
        public GameObject Instance { get; }
        public ProceduralTreeInstance Component { get; }
        public Vector2Int Coord => Data.Coord;
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

    private readonly struct TreeRenderLod
    {
        public TreeRenderLod(int lodIndex, TreeRenderItem[] renderItems)
        {
            LodIndex = lodIndex;
            RenderItems = renderItems != null ? renderItems : Array.Empty<TreeRenderItem>();
        }

        public int LodIndex { get; }
        public TreeRenderItem[] RenderItems { get; }
        public bool HasRenderItems => RenderItems.Length > 0;
    }

    private sealed partial class TerrainChunk
    {
        private readonly List<TreeRenderCell> treeRenderCells = new List<TreeRenderCell>();
        private readonly List<TreeInstanceData> treeInstances = new List<TreeInstanceData>();
        private readonly List<PrefabTreeInstance> prefabTreeInstances = new List<PrefabTreeInstance>();
        private Vector3[] treeSurfaceVertices = Array.Empty<Vector3>();
        private Vector3[] treeSurfaceNormals = Array.Empty<Vector3>();
        private int treeSurfaceVertexCount;
        private int treeSurfaceResolution;
        private int treeLodCount = 1;
        private int treeRenderCellsPerAxis = 1;
        private float treeRenderCellSize = 1f;
        private bool treesBuilt;

        public bool HasTreeBuild => treesBuilt;
        public bool HasTreeSurfaceData => treeSurfaceVertexCount > 0
            && treeSurfaceResolution >= 2
            && treeSurfaceVertices != null
            && treeSurfaceNormals != null
            && treeSurfaceVertices.Length >= treeSurfaceVertexCount
            && treeSurfaceNormals.Length >= treeSurfaceVertexCount;
        public IReadOnlyList<TreeInstanceData> TreeInstances => treeInstances;

        private int MaxTreeLodIndex => Mathf.Max(0, treeLodCount - 1);

        private void CaptureTreeSurfaceData(TerrainBuildTask task)
        {
            if (task == null || task.BaseVertexCount <= 0 || task.Resolution < 2)
            {
                ClearTreeSurfaceData();
                return;
            }

            int baseVertexCount = Mathf.Min(task.BaseVertexCount, Mathf.Min(task.Vertices.Length, task.Normals.Length));
            if (baseVertexCount <= 0)
            {
                ClearTreeSurfaceData();
                return;
            }

            if (treeSurfaceVertices == null || treeSurfaceVertices.Length < baseVertexCount)
            {
                treeSurfaceVertices = new Vector3[baseVertexCount];
            }

            if (treeSurfaceNormals == null || treeSurfaceNormals.Length < baseVertexCount)
            {
                treeSurfaceNormals = new Vector3[baseVertexCount];
            }

            for (int i = 0; i < baseVertexCount; i++)
            {
                treeSurfaceVertices[i] = task.Vertices[i];
                treeSurfaceNormals[i] = task.Normals[i];
            }

            treeSurfaceVertexCount = baseVertexCount;
            treeSurfaceResolution = task.Resolution;
        }

        private void ClearTreeSurfaceData()
        {
            treeSurfaceVertexCount = 0;
            treeSurfaceResolution = 0;
        }

        private static int GetTreeLodCount(IReadOnlyList<TreeRenderPrototype> renderPrototypes)
        {
            int lodCount = 1;
            if (renderPrototypes == null)
            {
                return lodCount;
            }

            for (int i = 0; i < renderPrototypes.Count; i++)
            {
                TreeRenderPrototype prototype = renderPrototypes[i];
                if (prototype != null)
                {
                    lodCount = Mathf.Max(lodCount, prototype.LodCount);
                }
            }

            return Mathf.Max(1, lodCount);
        }

        private void RebuildTreeRenderCells(int lodCount, float chunkSize, float requestedCellSize)
        {
            treeLodCount = Mathf.Max(1, lodCount);
            float safeChunkSize = Mathf.Max(1f, chunkSize);
            float targetCellSize = Mathf.Clamp(requestedCellSize, 16f, safeChunkSize);
            treeRenderCellsPerAxis = Mathf.Max(1, Mathf.CeilToInt(safeChunkSize / targetCellSize));
            treeRenderCellSize = safeChunkSize / treeRenderCellsPerAxis;

            Vector2 worldOrigin = new Vector2(Coord.x * safeChunkSize, Coord.y * safeChunkSize);
            for (int z = 0; z < treeRenderCellsPerAxis; z++)
            {
                for (int x = 0; x < treeRenderCellsPerAxis; x++)
                {
                    Vector2 min = worldOrigin + new Vector2(x * treeRenderCellSize, z * treeRenderCellSize);
                    Vector2 max = worldOrigin + new Vector2((x + 1) * treeRenderCellSize, (z + 1) * treeRenderCellSize);
                    treeRenderCells.Add(new TreeRenderCell(treeLodCount, min, max));
                }
            }
        }

        private TreeRenderCell GetTreeRenderCell(Vector3 chunkLocalPosition)
        {
            if (treeRenderCells.Count == 0)
            {
                return null;
            }

            int cellX = Mathf.Clamp(Mathf.FloorToInt(chunkLocalPosition.x / treeRenderCellSize), 0, treeRenderCellsPerAxis - 1);
            int cellZ = Mathf.Clamp(Mathf.FloorToInt(chunkLocalPosition.z / treeRenderCellSize), 0, treeRenderCellsPerAxis - 1);
            return treeRenderCells[cellZ * treeRenderCellsPerAxis + cellX];
        }

        public void ApplyTrees(
            TreeSettingsSO settings,
            IReadOnlyList<TreeRenderPrototype> renderPrototypes,
            IReadOnlyList<TreeBiomeRenderData> treeBiomeData,
            float globalTreeTotalDensity,
            float maxTreeDensity,
            bool useBiomeTreeSpawns,
            int terrainSeed,
            float chunkSize,
            bool enableWater,
            float waterHeight,
            TerrainHeightLayer[] terrainLayers,
            SlopeTextureSettings slopeTextureSettings,
            BiomeSamplingSettings biomeSettings)
        {
            ClearTrees();

            float candidateDensity = useBiomeTreeSpawns
                ? Mathf.Max(0f, maxTreeDensity)
                : Mathf.Max(0f, globalTreeTotalDensity);
            if (settings == null
                || !settings.EnableTrees
                || renderPrototypes == null
                || renderPrototypes.Count == 0
                || candidateDensity <= 0f
                || settings.MaxInstancesPerChunk <= 0
                || !HasTreeSurfaceData)
            {
                return;
            }

            RebuildTreeRenderCells(GetTreeLodCount(renderPrototypes), chunkSize, settings.RenderCellSize);
            treesBuilt = true;
            TerrainHeightLayer[] sortedLayers = CopySortedLayers(terrainLayers);
            int cellsPerAxis = Mathf.Max(1, Mathf.CeilToInt(chunkSize / settings.CellSize));
            float cellSize = chunkSize / cellsPerAxis;
            float expectedPerCell = candidateDensity * cellSize * cellSize;
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
                        Vector2 jitter = new Vector2(
                            Hash01(instanceHash + 0xbb67ae85u),
                            Hash01(instanceHash + 0x3c6ef372u));
                        Vector2 centeredJitter = (jitter - Vector2.one * 0.5f) * settings.Jitter + Vector2.one * 0.5f;
                        Vector2 local = (new Vector2(x, z) + centeredJitter) * cellSize;
                        local.x = Mathf.Clamp(local.x, 0.001f, chunkSize - 0.001f);
                        local.y = Mathf.Clamp(local.y, 0.001f, chunkSize - 0.001f);

                        if (!TrySampleSurface(local, chunkSize, out Vector3 position, out Vector3 normal))
                        {
                            continue;
                        }

                        Vector2 worldXZ = new Vector2(Coord.x * chunkSize + local.x, Coord.y * chunkSize + local.y);
                        IReadOnlyList<TreeRenderPrototype> spawnPrototypes = renderPrototypes;
                        float spawnTotalDensity = globalTreeTotalDensity;
                        if (useBiomeTreeSpawns)
                        {
                            TreeBiomeRenderData biomeTrees = ResolveTreeBiomeRenderData(worldXZ, treeBiomeData, biomeSettings);
                            if (biomeTrees == null || !biomeTrees.HasPrototypes)
                            {
                                continue;
                            }

                            spawnPrototypes = biomeTrees.Prototypes;
                            spawnTotalDensity = biomeTrees.TotalDensity;
                        }

                        TreeRenderPrototype prototype = PickPrototype(
                            spawnPrototypes,
                            Hash01(instanceHash + 0x9e3779b9u) * spawnTotalDensity);
                        if (prototype == null)
                        {
                            continue;
                        }

                        TreePrototypeSettings prototypeSettings = prototype.Settings;
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

                        ulong treeId = CreateTreeInstanceId(
                            Coord,
                            x,
                            z,
                            candidate,
                            prototype.PrototypeIndex,
                            instanceHash);
                        AddTreeRenderInstance(treeId, prototype, position, normal, chunkLocalToWorld, instanceHash);
                        spawnedCount++;
                    }
                }
            }

            FinalizeTreeBatches();
        }

        public void ClearTrees()
        {
            treesBuilt = false;
            treeInstances.Clear();
            for (int i = 0; i < prefabTreeInstances.Count; i++)
            {
                prefabTreeInstances[i].Release(owner, false);
            }

            prefabTreeInstances.Clear();
            for (int i = 0; i < treeRenderCells.Count; i++)
            {
                treeRenderCells[i].Clear();
            }

            treeRenderCells.Clear();
            treeLodCount = 1;
            treeRenderCellsPerAxis = 1;
            treeRenderCellSize = 1f;
        }

        public bool HasTreeInstance(ulong treeId)
        {
            for (int i = 0; i < treeInstances.Count; i++)
            {
                if (treeInstances[i].Id == treeId)
                {
                    return true;
                }
            }

            return false;
        }

        public void DrawTrees(
            TreeSettingsSO settings,
            Vector3 viewerPosition,
            int layer,
            ShadowCastingMode shadowCastingMode,
            bool receiveShadows,
            HashSet<ulong> removedTreeIds,
            Dictionary<ulong, ActiveInteractiveTree> hiddenInteractiveTrees,
            Matrix4x4[] scratchMatrices)
        {
            if (!treesBuilt)
            {
                return;
            }

            UpdatePrefabTreeInstances(viewerPosition, removedTreeIds, hiddenInteractiveTrees);

            for (int i = 0; i < treeRenderCells.Count; i++)
            {
                treeRenderCells[i].Draw(
                    settings,
                    viewerPosition,
                    MaxTreeLodIndex,
                    layer,
                    shadowCastingMode,
                    receiveShadows,
                    removedTreeIds,
                    hiddenInteractiveTrees,
                    scratchMatrices);
            }
        }

        public void SetPrefabTreeInstancesActive(bool active)
        {
            for (int i = 0; i < prefabTreeInstances.Count; i++)
            {
                prefabTreeInstances[i].SetActive(active);
            }
        }

        private void UpdatePrefabTreeInstances(
            Vector3 viewerPosition,
            HashSet<ulong> removedTreeIds,
            Dictionary<ulong, ActiveInteractiveTree> hiddenInteractiveTrees)
        {
            for (int i = 0; i < prefabTreeInstances.Count; i++)
            {
                prefabTreeInstances[i].UpdateVisibility(owner, viewerPosition, removedTreeIds, hiddenInteractiveTrees);
            }
        }

        private void AddTreeRenderInstance(
            ulong treeId,
            TreeRenderPrototype prototype,
            Vector3 position,
            Vector3 normal,
            Matrix4x4 chunkLocalToWorld,
            uint instanceHash)
        {
            TreePrototypeSettings settings = prototype.Settings;
            TreeRenderVariation variation = prototype.PickVariation(instanceHash);
            if (!variation.HasRenderItems && prototype.UseGpuInstancing)
            {
                return;
            }

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
            Matrix4x4 rootLocalToWorld = chunkLocalToWorld * rootLocalToChunk;
            Vector3 rootPosition = ExtractPosition(rootLocalToWorld);
            Quaternion rootRotation = ExtractRotation(rootLocalToWorld);
            Vector3 rootScale = ExtractScale(rootLocalToWorld);

            treeInstances.Add(new TreeInstanceData(
                treeId,
                Coord,
                prototype.PrototypeIndex,
                variation.SourceVariationIndex,
                settings,
                rootPosition,
                rootRotation,
                rootScale,
                chunkLocalToWorld.MultiplyVector(alignedNormal).normalized));

            if (!prototype.UseGpuInstancing)
            {
                AddPrefabTreeInstance(treeId, variation.Prefab, rootPosition, rootRotation, rootScale, prototype.MaxRenderDistance);
                return;
            }

            TreeRenderCell renderCell = GetTreeRenderCell(position);
            if (renderCell == null)
            {
                return;
            }

            for (int lodIndex = 0; lodIndex < treeLodCount; lodIndex++)
            {
                TreeRenderItem[] renderItems = variation.GetRenderItems(lodIndex);
                for (int i = 0; i < renderItems.Length; i++)
                {
                    TreeRenderItem renderItem = renderItems[i];
                    Matrix4x4 matrix = rootLocalToWorld * renderItem.LocalToPrefab;
                    renderCell.GetTreeRenderBatch(lodIndex, renderItem, prototype.MaxRenderDistance).Add(treeId, matrix, rootPosition);
                }
            }
        }

        private void AddPrefabTreeInstance(
            ulong treeId,
            GameObject prefab,
            Vector3 position,
            Quaternion rotation,
            Vector3 scale,
            float maxRenderDistance)
        {
            if (prefab == null)
            {
                return;
            }

            prefabTreeInstances.Add(new PrefabTreeInstance(
                treeId,
                prefab,
                gameObject.transform,
                position,
                rotation,
                scale,
                maxRenderDistance));
        }

        private static Vector3 ExtractPosition(Matrix4x4 matrix)
        {
            Vector4 column = matrix.GetColumn(3);
            return new Vector3(column.x, column.y, column.z);
        }

        private static Quaternion ExtractRotation(Matrix4x4 matrix)
        {
            Vector3 forward = matrix.GetColumn(2);
            Vector3 up = matrix.GetColumn(1);

            if (forward.sqrMagnitude < 0.0001f || up.sqrMagnitude < 0.0001f)
            {
                return Quaternion.identity;
            }

            return Quaternion.LookRotation(forward, up);
        }

        private static Vector3 ExtractScale(Matrix4x4 matrix)
        {
            return new Vector3(
                matrix.GetColumn(0).magnitude,
                matrix.GetColumn(1).magnitude,
                matrix.GetColumn(2).magnitude);
        }

        private void FinalizeTreeBatches()
        {
            for (int i = treeRenderCells.Count - 1; i >= 0; i--)
            {
                TreeRenderCell renderCell = treeRenderCells[i];
                renderCell.FinalizeBatches();
                if (!renderCell.HasMatrices)
                {
                    renderCell.Clear();
                    treeRenderCells.RemoveAt(i);
                }
            }
        }

        private static TreeBiomeRenderData ResolveTreeBiomeRenderData(
            Vector2 worldXZ,
            IReadOnlyList<TreeBiomeRenderData> biomes,
            BiomeSamplingSettings settings)
        {
            int count = biomes != null ? math.min(math.max(0, settings.Count), biomes.Count) : 0;
            if (count <= 0)
            {
                return null;
            }

            float2 sampleWorldXZ = new float2(worldXZ.x, worldXZ.y);
            float biomeDistance = EvaluateBiomeDistance(sampleWorldXZ, settings);
            int biomeIndex = -1;
            float bestScore = float.MinValue;
            float nearestDistance = float.MaxValue;
            int nearestIndex = -1;

            for (int i = 0; i < count; i++)
            {
                TreeBiomeRenderData biome = biomes[i];
                if (biome == null)
                {
                    continue;
                }

                float4 distanceRange = biome.DistanceRange;
                float minDistance = math.max(0f, distanceRange.x);
                float maxDistance = math.max(minDistance, distanceRange.y);
                float selectionWeight = GetBiomeSelectionWeight(distanceRange);
                if (selectionWeight <= 0f)
                {
                    continue;
                }

                float distanceToRange = DistanceToRange(biomeDistance, minDistance, maxDistance);
                if (distanceToRange < nearestDistance)
                {
                    nearestDistance = distanceToRange;
                    nearestIndex = i;
                }

                if (distanceToRange <= 0.0001f)
                {
                    int biomeSeedIndex = GetBiomeSeedIndex(distanceRange, i);
                    float score = EvaluateBiomeSelectionScore(sampleWorldXZ, settings, biomeSeedIndex, selectionWeight);
                    if (score > bestScore || (math.abs(score - bestScore) <= 0.0001f && i > biomeIndex))
                    {
                        biomeIndex = i;
                        bestScore = score;
                    }
                }
            }

            int resolvedIndex = biomeIndex >= 0 ? biomeIndex : nearestIndex;
            return resolvedIndex >= 0 ? biomes[resolvedIndex] : null;
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

        private bool TrySampleSurface(
            Vector2 local,
            float chunkSize,
            out Vector3 position,
            out Vector3 normal)
        {
            if (!HasTreeSurfaceData)
            {
                position = default;
                normal = Vector3.up;
                return false;
            }

            float u = Mathf.Clamp01(local.x / chunkSize);
            float v = Mathf.Clamp01(local.y / chunkSize);
            float gridX = u * (treeSurfaceResolution - 1);
            float gridZ = v * (treeSurfaceResolution - 1);
            int x0 = Mathf.Clamp(Mathf.FloorToInt(gridX), 0, treeSurfaceResolution - 1);
            int z0 = Mathf.Clamp(Mathf.FloorToInt(gridZ), 0, treeSurfaceResolution - 1);
            int x1 = Mathf.Min(x0 + 1, treeSurfaceResolution - 1);
            int z1 = Mathf.Min(z0 + 1, treeSurfaceResolution - 1);
            float tx = gridX - x0;
            float tz = gridZ - z0;

            int i00 = z0 * treeSurfaceResolution + x0;
            int i10 = z0 * treeSurfaceResolution + x1;
            int i01 = z1 * treeSurfaceResolution + x0;
            int i11 = z1 * treeSurfaceResolution + x1;

            if (i11 >= treeSurfaceVertexCount)
            {
                position = default;
                normal = Vector3.up;
                return false;
            }

            float h00 = treeSurfaceVertices[i00].y;
            float h10 = treeSurfaceVertices[i10].y;
            float h01 = treeSurfaceVertices[i01].y;
            float h11 = treeSurfaceVertices[i11].y;
            float height0 = Mathf.Lerp(h00, h10, tx);
            float height1 = Mathf.Lerp(h01, h11, tx);
            float surfaceHeight = Mathf.Lerp(height0, height1, tz);

            Vector3 n00 = treeSurfaceNormals[i00];
            Vector3 n10 = treeSurfaceNormals[i10];
            Vector3 n01 = treeSurfaceNormals[i01];
            Vector3 n11 = treeSurfaceNormals[i11];
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

        private static ulong CreateTreeInstanceId(
            Vector2Int coord,
            int cellX,
            int cellZ,
            int candidateIndex,
            int prototypeIndex,
            uint instanceHash)
        {
            uint upper = Hash(coord.x, coord.y, cellX, cellZ, candidateIndex, prototypeIndex);
            return ((ulong)upper << 32) | instanceHash;
        }

        private sealed class PrefabTreeInstance
        {
            private readonly ulong id;
            private readonly GameObject prefab;
            private readonly Transform parent;
            private readonly Vector3 position;
            private readonly Quaternion rotation;
            private readonly Vector3 scale;
            private readonly float maxRenderDistanceSqr;
            private GameObject instance;

            public PrefabTreeInstance(
                ulong id,
                GameObject prefab,
                Transform parent,
                Vector3 position,
                Quaternion rotation,
                Vector3 scale,
                float maxRenderDistance)
            {
                this.id = id;
                this.prefab = prefab;
                this.parent = parent;
                this.position = position;
                this.rotation = rotation;
                this.scale = scale;
                float safeMaxRenderDistance = Mathf.Max(1f, maxRenderDistance);
                maxRenderDistanceSqr = safeMaxRenderDistance * safeMaxRenderDistance;
            }

            public void UpdateVisibility(
                InfinitMeshTerrain owner,
                Vector3 viewerPosition,
                HashSet<ulong> removedTreeIds,
                Dictionary<ulong, ActiveInteractiveTree> hiddenInteractiveTrees)
            {
                bool visible = !IsRemoved(removedTreeIds)
                    && !IsHiddenInteractive(hiddenInteractiveTrees)
                    && IsInsideRenderDistance(viewerPosition);

                if (visible && !EnsureInstance(owner))
                {
                    return;
                }

                SetActive(visible);
            }

            public void SetActive(bool active)
            {
                if (instance != null && instance.activeSelf != active)
                {
                    instance.SetActive(active);
                }
            }

            public void Release(InfinitMeshTerrain owner, bool destroyInstance)
            {
                if (instance == null)
                {
                    return;
                }

                owner.ReleasePrefabTree(prefab, instance, destroyInstance);
                instance = null;
            }

            private bool EnsureInstance(InfinitMeshTerrain owner)
            {
                if (instance != null)
                {
                    return true;
                }

                if (owner == null || prefab == null || parent == null)
                {
                    return false;
                }

                instance = owner.RentPrefabTree(prefab);
                if (instance == null)
                {
                    return false;
                }

                Transform instanceTransform = instance.transform;
                instanceTransform.SetPositionAndRotation(position, rotation);
                instanceTransform.localScale = scale;
                instanceTransform.SetParent(parent, true);
                instance.SetActive(false);
                return true;
            }

            private bool IsRemoved(HashSet<ulong> removedTreeIds)
            {
                return removedTreeIds != null && removedTreeIds.Contains(id);
            }

            private bool IsHiddenInteractive(Dictionary<ulong, ActiveInteractiveTree> hiddenInteractiveTrees)
            {
                return hiddenInteractiveTrees != null && hiddenInteractiveTrees.ContainsKey(id);
            }

            private bool IsInsideRenderDistance(Vector3 viewerPosition)
            {
                float dx = position.x - viewerPosition.x;
                float dz = position.z - viewerPosition.z;
                return dx * dx + dz * dz <= maxRenderDistanceSqr;
            }
        }

        private sealed class TreeRenderCell
        {
            private readonly List<TreeRenderBatch>[] lodBatches;
            private readonly Vector2 min;
            private readonly Vector2 max;

            public TreeRenderCell(int lodCount, Vector2 min, Vector2 max)
            {
                int safeLodCount = Mathf.Max(1, lodCount);
                lodBatches = new List<TreeRenderBatch>[safeLodCount];
                for (int i = 0; i < lodBatches.Length; i++)
                {
                    lodBatches[i] = new List<TreeRenderBatch>();
                }

                this.min = min;
                this.max = max;
            }

            public bool HasMatrices
            {
                get
                {
                    for (int lodIndex = 0; lodIndex < lodBatches.Length; lodIndex++)
                    {
                        List<TreeRenderBatch> batches = lodBatches[lodIndex];
                        if (batches != null && batches.Count > 0)
                        {
                            return true;
                        }
                    }

                    return false;
                }
            }

            public TreeRenderBatch GetTreeRenderBatch(int lodIndex, TreeRenderItem item, float maxRenderDistance)
            {
                int clampedLodIndex = Mathf.Clamp(lodIndex, 0, lodBatches.Length - 1);
                List<TreeRenderBatch> batches = lodBatches[clampedLodIndex];
                for (int i = 0; i < batches.Count; i++)
                {
                    TreeRenderBatch batch = batches[i];
                    if (batch.Matches(item, maxRenderDistance))
                    {
                        return batch;
                    }
                }

                TreeRenderBatch newBatch = new TreeRenderBatch(item.Mesh, item.Material, item.SubMeshIndex, maxRenderDistance);
                batches.Add(newBatch);
                return newBatch;
            }

            public void FinalizeBatches()
            {
                for (int lodIndex = 0; lodIndex < lodBatches.Length; lodIndex++)
                {
                    List<TreeRenderBatch> batches = lodBatches[lodIndex];
                    if (batches == null)
                    {
                        continue;
                    }

                    for (int i = batches.Count - 1; i >= 0; i--)
                    {
                        TreeRenderBatch batch = batches[i];
                        batch.FinalizeBatches();
                        if (!batch.HasMatrices)
                        {
                            batches.RemoveAt(i);
                        }
                    }
                }
            }

            public void Draw(
                TreeSettingsSO settings,
                Vector3 viewerPosition,
                int maxTreeLodIndex,
                int layer,
                ShadowCastingMode shadowCastingMode,
                bool receiveShadows,
                HashSet<ulong> removedTreeIds,
                Dictionary<ulong, ActiveInteractiveTree> hiddenInteractiveTrees,
                Matrix4x4[] scratchMatrices)
            {
                float closestDistanceSqr = GetClosestDistanceSqr(viewerPosition);
                int lodIndex = SelectLod(settings, closestDistanceSqr, maxTreeLodIndex);
                if (lodIndex < 0 || lodIndex >= lodBatches.Length)
                {
                    return;
                }

                List<TreeRenderBatch> batches = lodBatches[lodIndex];
                if (batches == null)
                {
                    return;
                }

                float farthestDistanceSqr = GetFarthestDistanceSqr(viewerPosition);
                for (int i = 0; i < batches.Count; i++)
                {
                    batches[i].Draw(
                        viewerPosition,
                        closestDistanceSqr,
                        farthestDistanceSqr,
                        layer,
                        shadowCastingMode,
                        receiveShadows,
                        removedTreeIds,
                        hiddenInteractiveTrees,
                        scratchMatrices);
                }
            }

            public void Clear()
            {
                for (int lodIndex = 0; lodIndex < lodBatches.Length; lodIndex++)
                {
                    List<TreeRenderBatch> batches = lodBatches[lodIndex];
                    if (batches == null)
                    {
                        continue;
                    }

                    for (int i = 0; i < batches.Count; i++)
                    {
                        batches[i].Clear();
                    }

                    batches.Clear();
                }
            }

            private int SelectLod(TreeSettingsSO settings, float distanceSqr, int maxTreeLodIndex)
            {
                if (settings == null
                    || !settings.ForceInstancedMeshLodByDistance
                    || lodBatches.Length <= 1)
                {
                    return 0;
                }

                return settings.SelectInstancedMeshLodByDistanceSqr(distanceSqr, maxTreeLodIndex);
            }

            private float GetClosestDistanceSqr(Vector3 viewerPosition)
            {
                float closestX = Mathf.Clamp(viewerPosition.x, min.x, max.x);
                float closestZ = Mathf.Clamp(viewerPosition.z, min.y, max.y);
                float dx = viewerPosition.x - closestX;
                float dz = viewerPosition.z - closestZ;
                return dx * dx + dz * dz;
            }

            private float GetFarthestDistanceSqr(Vector3 viewerPosition)
            {
                float dx = Mathf.Max(Mathf.Abs(viewerPosition.x - min.x), Mathf.Abs(viewerPosition.x - max.x));
                float dz = Mathf.Max(Mathf.Abs(viewerPosition.z - min.y), Mathf.Abs(viewerPosition.z - max.y));
                return dx * dx + dz * dz;
            }
        }

        private sealed class TreeRenderBatch
        {
            private readonly Mesh mesh;
            private readonly Material material;
            private readonly int subMeshIndex;
            private readonly float maxRenderDistance;
            private readonly float maxRenderDistanceSqr;
            private readonly List<Matrix4x4> matrices = new List<Matrix4x4>();
            private readonly List<Vector3> positions = new List<Vector3>();
            private readonly List<ulong> ids = new List<ulong>();
            private readonly List<Matrix4x4[]> drawBatches = new List<Matrix4x4[]>();
            private readonly List<Vector3[]> drawBatchPositions = new List<Vector3[]>();
            private readonly List<ulong[]> drawBatchIds = new List<ulong[]>();

            public TreeRenderBatch(Mesh mesh, Material material, int subMeshIndex, float maxRenderDistance)
            {
                this.mesh = mesh;
                this.material = material;
                this.subMeshIndex = subMeshIndex;
                this.maxRenderDistance = Mathf.Max(1f, maxRenderDistance);
                maxRenderDistanceSqr = this.maxRenderDistance * this.maxRenderDistance;
            }

            public bool HasMatrices => drawBatches.Count > 0;

            public bool Matches(TreeRenderItem item, float maxRenderDistance)
            {
                return mesh == item.Mesh
                    && material == item.Material
                    && subMeshIndex == item.SubMeshIndex
                    && Mathf.Approximately(this.maxRenderDistance, Mathf.Max(1f, maxRenderDistance));
            }

            public void Add(ulong treeId, Matrix4x4 matrix, Vector3 position)
            {
                ids.Add(treeId);
                matrices.Add(matrix);
                positions.Add(position);
            }

            public void FinalizeBatches()
            {
                drawBatches.Clear();
                drawBatchPositions.Clear();
                drawBatchIds.Clear();
                for (int start = 0; start < matrices.Count; start += TreeDrawBatchSize)
                {
                    int count = Mathf.Min(TreeDrawBatchSize, matrices.Count - start);
                    Matrix4x4[] drawBatch = new Matrix4x4[count];
                    Vector3[] positionBatch = new Vector3[count];
                    ulong[] idBatch = new ulong[count];
                    matrices.CopyTo(start, drawBatch, 0, count);
                    positions.CopyTo(start, positionBatch, 0, count);
                    ids.CopyTo(start, idBatch, 0, count);
                    drawBatches.Add(drawBatch);
                    drawBatchPositions.Add(positionBatch);
                    drawBatchIds.Add(idBatch);
                }

                ids.Clear();
                matrices.Clear();
                positions.Clear();
            }

            public void Draw(
                Vector3 viewerPosition,
                float closestCellDistanceSqr,
                float farthestCellDistanceSqr,
                int layer,
                ShadowCastingMode shadowCastingMode,
                bool receiveShadows,
                HashSet<ulong> removedTreeIds,
                Dictionary<ulong, ActiveInteractiveTree> hiddenInteractiveTrees,
                Matrix4x4[] scratchMatrices)
            {
                if (mesh == null || material == null)
                {
                    return;
                }

                if (closestCellDistanceSqr > maxRenderDistanceSqr)
                {
                    return;
                }

                bool hasRemovedTrees = removedTreeIds != null && removedTreeIds.Count > 0;
                bool hasHiddenInteractiveTrees = hiddenInteractiveTrees != null && hiddenInteractiveTrees.Count > 0;
                bool needsFiltering = hasRemovedTrees
                    || hasHiddenInteractiveTrees
                    || farthestCellDistanceSqr > maxRenderDistanceSqr;

                for (int i = 0; i < drawBatches.Count; i++)
                {
                    Matrix4x4[] batch = drawBatches[i];
                    if (batch == null || batch.Length == 0)
                    {
                        continue;
                    }

                    if (needsFiltering)
                    {
                        DrawFilteredBatch(
                            batch,
                            drawBatchPositions[i],
                            drawBatchIds[i],
                            viewerPosition,
                            layer,
                            shadowCastingMode,
                            receiveShadows,
                            removedTreeIds,
                            hiddenInteractiveTrees,
                            scratchMatrices);
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

            private void DrawFilteredBatch(
                Matrix4x4[] batch,
                Vector3[] batchPositions,
                ulong[] batchIds,
                Vector3 viewerPosition,
                int layer,
                ShadowCastingMode shadowCastingMode,
                bool receiveShadows,
                HashSet<ulong> removedTreeIds,
                Dictionary<ulong, ActiveInteractiveTree> hiddenInteractiveTrees,
                Matrix4x4[] scratchMatrices)
            {
                if (batchIds == null
                    || batchIds.Length != batch.Length
                    || batchPositions == null
                    || batchPositions.Length != batch.Length)
                {
                    return;
                }

                if (scratchMatrices == null || scratchMatrices.Length < TreeDrawBatchSize)
                {
                    scratchMatrices = new Matrix4x4[TreeDrawBatchSize];
                }

                int visibleCount = 0;
                for (int i = 0; i < batch.Length; i++)
                {
                    ulong treeId = batchIds[i];
                    if ((removedTreeIds != null && removedTreeIds.Contains(treeId))
                        || (hiddenInteractiveTrees != null && hiddenInteractiveTrees.ContainsKey(treeId))
                        || !IsInsideRenderDistance(batchPositions[i], viewerPosition))
                    {
                        continue;
                    }

                    scratchMatrices[visibleCount] = batch[i];
                    visibleCount++;
                }

                if (visibleCount == 0)
                {
                    return;
                }

                Graphics.DrawMeshInstanced(
                    mesh,
                    subMeshIndex,
                    material,
                    scratchMatrices,
                    visibleCount,
                    null,
                    shadowCastingMode,
                    receiveShadows,
                    layer);
            }

            private bool IsInsideRenderDistance(Vector3 position, Vector3 viewerPosition)
            {
                float dx = position.x - viewerPosition.x;
                float dz = position.z - viewerPosition.z;
                return dx * dx + dz * dz <= maxRenderDistanceSqr;
            }

            public void Clear()
            {
                ids.Clear();
                matrices.Clear();
                positions.Clear();
                drawBatches.Clear();
                drawBatchPositions.Clear();
                drawBatchIds.Clear();
            }
        }
    }
}
