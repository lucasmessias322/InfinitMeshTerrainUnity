using System;
using System.Collections;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

public partial class InfinitMeshTerrain
{
    [Header("Initial Spawn")]
    [SerializeField] private bool placeViewerAtInitialSpawn = true;
    [SerializeField] private Transform initialSpawnTarget;
    [SerializeField, Min(0f)] private float initialSpawnSearchRadius = 3000f;
    [SerializeField, Min(1f)] private float initialSpawnSampleSpacing = 32f;
    [SerializeField, Min(0f)] private float initialSpawnHeightAboveWater = 3f;
    [SerializeField, Min(0f)] private float initialSpawnYOffset = 1f;
    [SerializeField] private Vector2 initialSpawnSearchCenter;
    [SerializeField] private bool holdInitialSpawnUntilTerrainCollider = true;
    [Tooltip("Disables enabled MonoBehaviour scripts on the spawn target while the spawn chunk collider is loading.")]
    [SerializeField] private bool autoDisableInitialSpawnTargetBehaviours = true;
    [Tooltip("Optional movement scripts disabled while the spawn chunk collider is still loading.")]
    [SerializeField] private Behaviour[] initialSpawnBehavioursToDisable = Array.Empty<Behaviour>();
    [Tooltip("Seconds before giving up. 0 keeps the player held until terrain collision is ready.")]
    [SerializeField, Min(0f)] private float initialSpawnTerrainReadyTimeout;

    private bool hasPlacedInitialSpawn;
    private Coroutine initialSpawnHoldCoroutine;
    private Transform initialSpawnHoldTarget;
    private CharacterController initialSpawnHoldCharacterController;
    private bool initialSpawnHoldCharacterControllerWasEnabled;
    private Rigidbody initialSpawnHoldRigidbody;
    private bool initialSpawnHoldRigidbodyWasKinematic;
    private bool initialSpawnHoldRigidbodyUseGravity;
    private readonly List<Behaviour> initialSpawnDisabledBehaviours = new List<Behaviour>();

    public bool TryFindInitialSpawnPosition(out Vector3 spawnPosition)
    {
        ValidateInitialSpawnSettings();
        return TryFindInitialSpawnPosition(
            initialSpawnSearchCenter,
            initialSpawnSearchRadius,
            initialSpawnSampleSpacing,
            waterHeight + initialSpawnHeightAboveWater,
            initialSpawnYOffset,
            out spawnPosition);
    }

    public bool TryFindInitialSpawnPosition(
        Vector2 searchCenter,
        float searchRadius,
        float sampleSpacing,
        float minimumTerrainHeight,
        float yOffset,
        out Vector3 spawnPosition)
    {
        spawnPosition = default;
        searchRadius = Mathf.Max(0f, searchRadius);
        sampleSpacing = Mathf.Max(1f, sampleSpacing);
        yOffset = Mathf.Max(0f, yOffset);

        TerrainHeightSampler heightSampler = CreateTerrainHeightSampler(Allocator.Temp);
        try
        {
            GrassBiomeData[] biomeData = CreateBiomeDataArray();
            BiomeSamplingSettings biomeSettings = CreateBiomeSamplingSettings();
            int biomeDataCount = Mathf.Min(Mathf.Max(0, biomeSettings.Count), biomeData.Length);
            int targetBiomeIndex = ResolveInitialSpawnTargetBiomeIndex(biomeData, biomeDataCount);
            bool needsBiomeMatch = biomeDataCount > 0;

            if (TryAcceptInitialSpawnCandidate(
                searchCenter,
                heightSampler,
                biomeData,
                biomeSettings,
                biomeDataCount,
                targetBiomeIndex,
                needsBiomeMatch,
                minimumTerrainHeight,
                yOffset,
                out spawnPosition))
            {
                return true;
            }

            float angleOffset = CalculateInitialSpawnAngleOffset();
            for (float radius = sampleSpacing; radius <= searchRadius + 0.001f; radius += sampleSpacing)
            {
                int sampleCount = Mathf.Max(8, Mathf.CeilToInt(2f * Mathf.PI * radius / sampleSpacing));
                float angleStep = 2f * Mathf.PI / sampleCount;

                for (int i = 0; i < sampleCount; i++)
                {
                    float angle = angleOffset + i * angleStep;
                    Vector2 candidate = searchCenter + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius;
                    if (TryAcceptInitialSpawnCandidate(
                        candidate,
                        heightSampler,
                        biomeData,
                        biomeSettings,
                        biomeDataCount,
                        targetBiomeIndex,
                        needsBiomeMatch,
                        minimumTerrainHeight,
                        yOffset,
                        out spawnPosition))
                    {
                        return true;
                    }
                }
            }
        }
        finally
        {
            heightSampler.Dispose();
        }

        return false;
    }

    public float SampleTerrainHeight(Vector2 worldXZ)
    {
        TerrainHeightSampler heightSampler = CreateTerrainHeightSampler(Allocator.Temp);
        try
        {
            return heightSampler.SampleHeight(new float2(worldXZ.x, worldXZ.y));
        }
        finally
        {
            heightSampler.Dispose();
        }
    }

    private void ValidateInitialSpawnSettings()
    {
        initialSpawnSearchRadius = Mathf.Max(0f, initialSpawnSearchRadius);
        initialSpawnSampleSpacing = Mathf.Max(1f, initialSpawnSampleSpacing);
        initialSpawnHeightAboveWater = Mathf.Max(0f, initialSpawnHeightAboveWater);
        initialSpawnYOffset = Mathf.Max(0f, initialSpawnYOffset);
        initialSpawnTerrainReadyTimeout = Mathf.Max(0f, initialSpawnTerrainReadyTimeout);
        initialSpawnBehavioursToDisable ??= Array.Empty<Behaviour>();
    }

    private void TryPlaceViewerAtInitialSpawn()
    {
        if (!Application.isPlaying || hasPlacedInitialSpawn || !placeViewerAtInitialSpawn)
        {
            return;
        }

        Transform spawnTarget = ResolveInitialSpawnTarget();
        if (spawnTarget == null)
        {
            return;
        }

        hasPlacedInitialSpawn = true;
        if (TryFindInitialSpawnPosition(out Vector3 spawnPosition))
        {
            MoveSpawnTarget(spawnTarget, spawnPosition);
            BeginInitialSpawnHold(spawnTarget, spawnPosition);
            return;
        }

        Debug.LogWarning(
            $"{nameof(InfinitMeshTerrain)} could not find an initial spawn within {initialSpawnSearchRadius:0.#} units " +
            $"above height {waterHeight + initialSpawnHeightAboveWater:0.##} in the first biome.",
            this);
    }

    private Transform ResolveInitialSpawnTarget()
    {
        if (initialSpawnTarget != null)
        {
            return initialSpawnTarget;
        }

        if (viewer == null)
        {
            return null;
        }

        CharacterController characterController = viewer.GetComponentInParent<CharacterController>();
        if (characterController != null)
        {
            return characterController.transform;
        }

        Rigidbody body = viewer.GetComponentInParent<Rigidbody>();
        return body != null ? body.transform : viewer;
    }

    private void DisableInitialSpawnTargetBehaviours(Transform spawnTarget)
    {
        if (spawnTarget == null)
        {
            return;
        }

        MonoBehaviour[] behaviours = spawnTarget.GetComponents<MonoBehaviour>();
        for (int i = 0; i < behaviours.Length; i++)
        {
            DisableInitialSpawnBehaviour(behaviours[i]);
        }

        if (viewer == null || viewer == spawnTarget || !viewer.IsChildOf(spawnTarget))
        {
            return;
        }

        MonoBehaviour[] viewerBehaviours = viewer.GetComponents<MonoBehaviour>();
        for (int i = 0; i < viewerBehaviours.Length; i++)
        {
            DisableInitialSpawnBehaviour(viewerBehaviours[i]);
        }
    }

    private void DisableInitialSpawnBehaviour(Behaviour behaviour)
    {
        if (behaviour == null
            || behaviour == this
            || !behaviour.enabled
            || initialSpawnDisabledBehaviours.Contains(behaviour))
        {
            return;
        }

        behaviour.enabled = false;
        initialSpawnDisabledBehaviours.Add(behaviour);
    }

    private void BeginInitialSpawnHold(Transform spawnTarget, Vector3 spawnPosition)
    {
        ReleaseInitialSpawnHold();
        if (!holdInitialSpawnUntilTerrainCollider)
        {
            return;
        }

        initialSpawnHoldTarget = spawnTarget;
        initialSpawnHoldCharacterController = spawnTarget.GetComponent<CharacterController>();
        initialSpawnHoldCharacterControllerWasEnabled = initialSpawnHoldCharacterController != null
            && initialSpawnHoldCharacterController.enabled;
        if (initialSpawnHoldCharacterControllerWasEnabled)
        {
            initialSpawnHoldCharacterController.enabled = false;
        }

        initialSpawnHoldRigidbody = spawnTarget.GetComponent<Rigidbody>();
        if (initialSpawnHoldRigidbody != null)
        {
            initialSpawnHoldRigidbodyWasKinematic = initialSpawnHoldRigidbody.isKinematic;
            initialSpawnHoldRigidbodyUseGravity = initialSpawnHoldRigidbody.useGravity;
            initialSpawnHoldRigidbody.linearVelocity = Vector3.zero;
            initialSpawnHoldRigidbody.angularVelocity = Vector3.zero;
            initialSpawnHoldRigidbody.useGravity = false;
            initialSpawnHoldRigidbody.isKinematic = true;
        }

        initialSpawnDisabledBehaviours.Clear();
        if (autoDisableInitialSpawnTargetBehaviours)
        {
            DisableInitialSpawnTargetBehaviours(spawnTarget);
        }

        if (initialSpawnBehavioursToDisable != null)
        {
            for (int i = 0; i < initialSpawnBehavioursToDisable.Length; i++)
            {
                DisableInitialSpawnBehaviour(initialSpawnBehavioursToDisable[i]);
            }
        }

        MoveSpawnTarget(spawnTarget, spawnPosition);
        initialSpawnHoldCoroutine = StartCoroutine(WaitForInitialSpawnTerrainCollider(spawnPosition));
    }

    private IEnumerator WaitForInitialSpawnTerrainCollider(Vector3 spawnPosition)
    {
        ForceRefresh();
        float startTime = Time.realtimeSinceStartup;
        bool timedOut = false;

        while (!IsInitialSpawnTerrainColliderReady(spawnPosition))
        {
            MoveInitialSpawnHoldTarget(spawnPosition);

            if (initialSpawnTerrainReadyTimeout > 0f
                && Time.realtimeSinceStartup - startTime >= initialSpawnTerrainReadyTimeout)
            {
                timedOut = true;
                break;
            }

            yield return null;
        }

        MoveInitialSpawnHoldTarget(spawnPosition);
        Physics.SyncTransforms();
        yield return new WaitForFixedUpdate();
        MoveInitialSpawnHoldTarget(spawnPosition);

        if (timedOut)
        {
            Debug.LogWarning(
                $"{nameof(InfinitMeshTerrain)} released the initial spawn hold before terrain collision was ready " +
                $"after {initialSpawnTerrainReadyTimeout:0.##} seconds.",
                this);
        }

        initialSpawnHoldCoroutine = null;
        ReleaseInitialSpawnHold();
    }

    private bool IsInitialSpawnTerrainColliderReady(Vector3 spawnPosition)
    {
        Vector2Int coord = WorldToChunkCoord(spawnPosition);
        if (!chunks.TryGetValue(coord, out TerrainChunk chunk) || !chunk.HasMesh)
        {
            return false;
        }

        if (!chunk.HasActiveCollider)
        {
            QueueColliderUpdate(coord);
            chunk.SetColliderEnabled(true);
        }

        return chunk.HasActiveCollider;
    }

    private void MoveInitialSpawnHoldTarget(Vector3 spawnPosition)
    {
        if (initialSpawnHoldTarget != null)
        {
            MoveSpawnTarget(initialSpawnHoldTarget, spawnPosition);
        }
    }

    private void ReleaseInitialSpawnHold()
    {
        if (initialSpawnHoldCoroutine != null)
        {
            StopCoroutine(initialSpawnHoldCoroutine);
            initialSpawnHoldCoroutine = null;
        }

        for (int i = 0; i < initialSpawnDisabledBehaviours.Count; i++)
        {
            Behaviour behaviour = initialSpawnDisabledBehaviours[i];
            if (behaviour != null)
            {
                behaviour.enabled = true;
            }
        }

        initialSpawnDisabledBehaviours.Clear();

        if (initialSpawnHoldRigidbody != null)
        {
            initialSpawnHoldRigidbody.linearVelocity = Vector3.zero;
            initialSpawnHoldRigidbody.angularVelocity = Vector3.zero;
            initialSpawnHoldRigidbody.useGravity = initialSpawnHoldRigidbodyUseGravity;
            initialSpawnHoldRigidbody.isKinematic = initialSpawnHoldRigidbodyWasKinematic;
        }

        if (initialSpawnHoldCharacterController != null && initialSpawnHoldCharacterControllerWasEnabled)
        {
            initialSpawnHoldCharacterController.enabled = true;
        }

        initialSpawnHoldTarget = null;
        initialSpawnHoldCharacterController = null;
        initialSpawnHoldCharacterControllerWasEnabled = false;
        initialSpawnHoldRigidbody = null;
        initialSpawnHoldRigidbodyWasKinematic = false;
        initialSpawnHoldRigidbodyUseGravity = false;
    }

    private static void MoveSpawnTarget(Transform spawnTarget, Vector3 spawnPosition)
    {
        CharacterController characterController = spawnTarget.GetComponent<CharacterController>();
        bool restoreCharacterController = characterController != null && characterController.enabled;
        if (restoreCharacterController)
        {
            characterController.enabled = false;
        }

        Rigidbody body = spawnTarget.GetComponent<Rigidbody>();
        if (body != null)
        {
            body.position = spawnPosition;
            body.rotation = spawnTarget.rotation;
            body.linearVelocity = Vector3.zero;
            body.angularVelocity = Vector3.zero;
        }

        spawnTarget.position = spawnPosition;

        if (restoreCharacterController)
        {
            characterController.enabled = true;
        }
    }

    private bool TryAcceptInitialSpawnCandidate(
        Vector2 worldXZ,
        TerrainHeightSampler heightSampler,
        GrassBiomeData[] biomeData,
        BiomeSamplingSettings biomeSettings,
        int biomeDataCount,
        int targetBiomeIndex,
        bool needsBiomeMatch,
        float minimumTerrainHeight,
        float yOffset,
        out Vector3 spawnPosition)
    {
        spawnPosition = default;
        float2 world = new float2(worldXZ.x, worldXZ.y);
        float height = heightSampler.SampleHeight(world);
        if (height < minimumTerrainHeight)
        {
            return false;
        }

        if (!IsInitialSpawnBiomeMatch(world, biomeData, biomeSettings, biomeDataCount, targetBiomeIndex, needsBiomeMatch))
        {
            return false;
        }

        spawnPosition = new Vector3(worldXZ.x, height + yOffset, worldXZ.y);
        return true;
    }

    private static bool IsInitialSpawnBiomeMatch(
        float2 worldXZ,
        GrassBiomeData[] biomeData,
        BiomeSamplingSettings biomeSettings,
        int biomeDataCount,
        int targetBiomeIndex,
        bool needsBiomeMatch)
    {
        if (!needsBiomeMatch)
        {
            return true;
        }

        if (targetBiomeIndex < 0)
        {
            return false;
        }

        float biomeDistance = EvaluateBiomeDistance(worldXZ, biomeSettings);
        int biomeIndex = ResolveBiomeIndex(worldXZ, biomeDistance, biomeData, biomeSettings, biomeDataCount);
        return biomeIndex == targetBiomeIndex;
    }

    private static int ResolveInitialSpawnTargetBiomeIndex(GrassBiomeData[] biomeData, int biomeDataCount)
    {
        for (int i = 0; i < biomeDataCount; i++)
        {
            GrassBiomeData biome = biomeData[i];
            if (GetBiomeSelectionWeight(biome.DistanceRange) <= 0f)
            {
                continue;
            }

            if (Mathf.RoundToInt(biome.DistanceRange.w) == 0)
            {
                return i;
            }
        }

        return -1;
    }

    private float CalculateInitialSpawnAngleOffset()
    {
        float normalizedSeed = Mathf.Repeat(GetTerrainSeed() * 0.61803398875f, 1f);
        return normalizedSeed * Mathf.PI * 2f;
    }

    private TerrainHeightSampler CreateTerrainHeightSampler(Allocator allocator)
    {
        int heightLayerCount = GetTerrainHeightLayerCount();
        int heightSplineSampleCount = GetTerrainSplineSampleCount();
        NativeArray<TerrainHeightNoiseLayerData> heightLayers = heightLayerCount > 0
            ? new NativeArray<TerrainHeightNoiseLayerData>(heightLayerCount, allocator, NativeArrayOptions.UninitializedMemory)
            : default;
        NativeArray<float> heightSplineSamples = heightSplineSampleCount > 0
            ? new NativeArray<float>(heightSplineSampleCount, allocator, NativeArrayOptions.UninitializedMemory)
            : default;

        CopyTerrainHeightLayers(heightLayers, heightSplineSamples);
        return new TerrainHeightSampler(
            CreateTerrainSettings(),
            heightLayers,
            heightSplineSamples,
            heightLayerCount);
    }

    private struct TerrainHeightSampler : IDisposable
    {
        private TerrainSettings settings;
        private NativeArray<TerrainHeightNoiseLayerData> heightLayers;
        private NativeArray<float> heightSplineSamples;
        private int heightLayerCount;

        public TerrainHeightSampler(
            TerrainSettings settings,
            NativeArray<TerrainHeightNoiseLayerData> heightLayers,
            NativeArray<float> heightSplineSamples,
            int heightLayerCount)
        {
            this.settings = settings;
            this.heightLayers = heightLayers;
            this.heightSplineSamples = heightSplineSamples;
            this.heightLayerCount = heightLayerCount;
        }

        public void Dispose()
        {
            if (heightLayers.IsCreated)
            {
                heightLayers.Dispose();
            }

            if (heightSplineSamples.IsCreated)
            {
                heightSplineSamples.Dispose();
            }
        }

        public float SampleHeight(float2 world)
        {
            if (heightLayerCount > 0 && heightLayers.IsCreated)
            {
                float height = 0f;
                int layerCount = math.min(heightLayerCount, heightLayers.Length);
                for (int i = 0; i < layerCount; i++)
                {
                    TerrainHeightNoiseLayerData layer = heightLayers[i];
                    float contribution = SampleLayerContribution(world, layer, i);
                    height = ApplyLayerOperation(height, contribution, layer.Operation);
                }

                return math.clamp(height, settings.MinHeight, settings.MaxHeight);
            }

            return settings.MinHeight;
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
            float2 seedOffset = settings.NoiseOffset
                + layer.Offset
                + new float2(
                    settings.TerrainSeed * 37.17f + layerIndex * 101.31f,
                    settings.TerrainSeed * -19.91f + layerIndex * -73.57f);
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
            if (layer.SplineInfluence <= 0f
                || layer.SplineSampleOffset < 0
                || layer.SplineSampleCount <= 1
                || !heightSplineSamples.IsCreated
                || heightSplineSamples.Length == 0)
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
                || layer.MaskSplineInputLayerIndex >= heightLayerCount
                || !heightLayers.IsCreated
                || layer.MaskSplineInputLayerIndex >= heightLayers.Length
                || !heightSplineSamples.IsCreated
                || heightSplineSamples.Length == 0)
            {
                return 1f;
            }

            TerrainHeightNoiseLayerData inputLayer = heightLayers[layer.MaskSplineInputLayerIndex];
            float inputValue = SampleLayerValue(world, inputLayer, layer.MaskSplineInputLayerIndex);
            float input = NormalizeSplineInput(inputValue, inputLayer.NoiseShape);
            float splineMask = SampleSpline(input, layer.MaskSplineSampleOffset, layer.MaskSplineSampleCount);
            return math.max(0f, math.lerp(1f, splineMask, math.saturate(layer.MaskSplineInfluence)));
        }

        private float SampleSpline(float input, int sampleOffset, int sampleCount)
        {
            int availableCount = heightSplineSamples.IsCreated
                ? math.min(sampleCount, heightSplineSamples.Length - sampleOffset)
                : 0;
            if (sampleOffset < 0 || availableCount <= 0)
            {
                return input;
            }

            if (availableCount == 1)
            {
                return heightSplineSamples[sampleOffset];
            }

            float samplePosition = math.saturate(input) * (availableCount - 1);
            int index0 = (int)math.floor(samplePosition);
            int index1 = math.min(index0 + 1, availableCount - 1);
            float t = samplePosition - index0;
            float value0 = heightSplineSamples[sampleOffset + index0];
            float value1 = heightSplineSamples[sampleOffset + index1];
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
}
