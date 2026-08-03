using System;
using UnityEngine;

[DisallowMultipleComponent]
[AddComponentMenu("Procedural Terrain/Procedural Terrain Ambient Audio")]
public sealed class ProceduralTerrainAmbientAudio : MonoBehaviour
{
    private const float SilentVolume = 0.001f;

    [Header("References")]
    [SerializeField] private InfinitMeshTerrain terrain;
    [SerializeField] private Transform listener;
    [SerializeField] private WaveManager waveManager;
    [SerializeField] private AudioSource seaSource;
    [SerializeField] private AudioSource birdsSource;
    [SerializeField] private AudioSource underwaterSource;

    [Header("Sampling")]
    [SerializeField, Min(0.02f)] private float sampleInterval = 0.25f;
    [SerializeField, Min(0f)] private float volumeFadeSpeed = 1.5f;
    [SerializeField] private bool autoFindReferences = true;
    [SerializeField] private bool forceLoopSources = true;
    [SerializeField] private bool pauseWhenSilent = true;

    [Header("Water")]
    [SerializeField] private bool enableSeaAudio = true;
    [SerializeField, Range(0f, 1f)] private float seaMaxVolume = 1f;
    [SerializeField, Min(1f)] private float seaAudibleDistance = 180f;
    [SerializeField, Min(0f)] private float seaFullVolumeDistance = 32f;
    [SerializeField, Min(0f)] private float waterHeightPadding = 2f;
    [SerializeField, Min(0f)] private float shorelineHeightFade = 24f;
    [SerializeField, Range(4, 32)] private int seaSamplesPerRing = 12;
    [SerializeField, Range(1, 5)] private int seaSampleRings = 3;

    [Header("Underwater")]
    [SerializeField] private bool enableUnderwaterAudio = true;
    [Tooltip("Optional. When set, this clip is assigned to the underwater source at runtime.")]
    [SerializeField] private AudioClip underwaterClip;
    [SerializeField, Range(0f, 1f)] private float underwaterMaxVolume = 1f;
    [SerializeField, Min(0f)] private float underwaterFullVolumeDepth = 1f;
    [SerializeField, Min(0f)] private float underwaterSurfaceHysteresis = 0.05f;
    [Tooltip("Uses WaveManager when available so underwater audio follows the rendered waves.")]
    [SerializeField] private bool useWaveManagerWaterHeight = true;
    [SerializeField, Range(0f, 1f)] private float seaDuckingUnderwater = 0.65f;
    [SerializeField, Range(0f, 1f)] private float birdsDuckingUnderwater = 1f;

    [Header("Forest")]
    [SerializeField] private bool enableBirdsAudio = true;
    [SerializeField, Range(0f, 1f)] private float birdsMaxVolume = 0.85f;
    [SerializeField, Min(1f)] private float forestAudibleDistance = 160f;
    [SerializeField, Min(0f)] private float forestFullVolumeDistance = 48f;
    [SerializeField, Range(4, 32)] private int forestSamplesPerRing = 10;
    [SerializeField, Range(1, 4)] private int forestSampleRings = 2;
    [SerializeField, Min(0f)] private float minimumForestTreeDensity = 0.005f;
    [SerializeField, Min(0.0001f)] private float fullForestTreeDensity = 0.08f;
    [SerializeField] private TerrainBiomeSO[] forestBiomes = Array.Empty<TerrainBiomeSO>();
    [SerializeField, Range(0f, 1f)] private float birdsDuckingNearSea = 0.15f;

    private float nextSampleTime;
    private float seaTargetVolume;
    private float birdsTargetVolume;
    private float underwaterTargetVolume;
    private float seaCurrentVolume;
    private float birdsCurrentVolume;
    private float underwaterCurrentVolume;

    public bool IsListenerUnderwater { get; private set; }
    public float WaterSurfaceHeight { get; private set; }

    private void Reset()
    {
        ResolveMissingReferences();

        AudioSource[] sources = GetComponentsInChildren<AudioSource>();
        if (sources.Length > 0)
        {
            seaSource = sources[0];
        }

        if (sources.Length > 1)
        {
            birdsSource = sources[1];
        }

        if (sources.Length > 2)
        {
            underwaterSource = sources[2];
        }
    }

    private void Awake()
    {
        ResolveMissingReferences();
        PrepareSource(seaSource);
        PrepareSource(birdsSource);
        PrepareUnderwaterSource();
    }

    private void OnEnable()
    {
        nextSampleTime = 0f;
        PrepareUnderwaterSource();
        SampleAudioTargets();
        ApplySourceVolume(seaSource, seaTargetVolume, ref seaCurrentVolume, true);
        ApplySourceVolume(birdsSource, birdsTargetVolume, ref birdsCurrentVolume, true);
        ApplySourceVolume(underwaterSource, underwaterTargetVolume, ref underwaterCurrentVolume, true);
    }

    private void Update()
    {
        bool needsReferences = terrain == null
            || listener == null
            || (useWaveManagerWaterHeight && waveManager == null);

        if (autoFindReferences && needsReferences)
        {
            ResolveMissingReferences();
        }

        if (Time.time >= nextSampleTime)
        {
            nextSampleTime = Time.time + sampleInterval;
            SampleAudioTargets();
        }

        ApplySourceVolume(seaSource, seaTargetVolume, ref seaCurrentVolume, false);
        ApplySourceVolume(birdsSource, birdsTargetVolume, ref birdsCurrentVolume, false);
        ApplySourceVolume(underwaterSource, underwaterTargetVolume, ref underwaterCurrentVolume, false);
    }

    private void OnDisable()
    {
        SetSourceVolume(seaSource, 0f);
        SetSourceVolume(birdsSource, 0f);
        SetSourceVolume(underwaterSource, 0f);
        seaCurrentVolume = 0f;
        birdsCurrentVolume = 0f;
        underwaterCurrentVolume = 0f;
    }

    private void OnValidate()
    {
        sampleInterval = Mathf.Max(0.02f, sampleInterval);
        volumeFadeSpeed = Mathf.Max(0f, volumeFadeSpeed);
        seaAudibleDistance = Mathf.Max(1f, seaAudibleDistance);
        seaFullVolumeDistance = Mathf.Clamp(seaFullVolumeDistance, 0f, seaAudibleDistance);
        waterHeightPadding = Mathf.Max(0f, waterHeightPadding);
        shorelineHeightFade = Mathf.Max(0f, shorelineHeightFade);
        underwaterMaxVolume = Mathf.Clamp01(underwaterMaxVolume);
        underwaterFullVolumeDepth = Mathf.Max(0f, underwaterFullVolumeDepth);
        underwaterSurfaceHysteresis = Mathf.Max(0f, underwaterSurfaceHysteresis);
        seaDuckingUnderwater = Mathf.Clamp01(seaDuckingUnderwater);
        birdsDuckingUnderwater = Mathf.Clamp01(birdsDuckingUnderwater);
        forestAudibleDistance = Mathf.Max(1f, forestAudibleDistance);
        forestFullVolumeDistance = Mathf.Clamp(forestFullVolumeDistance, 0f, forestAudibleDistance);
        minimumForestTreeDensity = Mathf.Max(0f, minimumForestTreeDensity);
        fullForestTreeDensity = Mathf.Max(minimumForestTreeDensity + 0.0001f, fullForestTreeDensity);
        forestBiomes ??= Array.Empty<TerrainBiomeSO>();
    }

    private void ResolveMissingReferences()
    {
        if (terrain == null)
        {
            terrain = FindAnyObjectByType<InfinitMeshTerrain>();
        }

        if (listener == null && Camera.main != null)
        {
            listener = Camera.main.transform;
        }

        if (waveManager == null)
        {
            waveManager = WaveManager.Instance != null
                ? WaveManager.Instance
                : FindAnyObjectByType<WaveManager>();
        }
    }

    private void PrepareSource(AudioSource source)
    {
        if (source == null)
        {
            return;
        }

        if (forceLoopSources)
        {
            source.loop = true;
        }

        source.playOnAwake = false;
        source.volume = 0f;

        if (!pauseWhenSilent && Application.isPlaying && source.clip != null && !source.isPlaying)
        {
            source.Play();
        }
    }

    private void SampleAudioTargets()
    {
        if (terrain == null || listener == null)
        {
            seaTargetVolume = 0f;
            birdsTargetVolume = 0f;
            underwaterTargetVolume = 0f;
            IsListenerUnderwater = false;
            return;
        }

        Vector3 position = listener.position;
        float underwaterPresence = 0f;
        if (enableUnderwaterAudio)
        {
            underwaterPresence = SampleUnderwaterPresence(position);
        }
        else
        {
            IsListenerUnderwater = false;
        }

        float seaPresence = enableSeaAudio ? SampleSeaPresence(position) : 0f;
        float forestPresence = enableBirdsAudio ? SampleForestPresence(position) : 0f;

        seaTargetVolume = Mathf.Clamp01(seaPresence) * seaMaxVolume;
        birdsTargetVolume = Mathf.Clamp01(forestPresence) * birdsMaxVolume;
        underwaterTargetVolume = Mathf.Clamp01(underwaterPresence) * underwaterMaxVolume;
        if (birdsDuckingNearSea > 0f)
        {
            birdsTargetVolume *= 1f - Mathf.Clamp01(seaPresence) * birdsDuckingNearSea;
        }

        if (underwaterPresence > 0f)
        {
            float underwaterAmount = Mathf.Clamp01(underwaterPresence);
            seaTargetVolume *= 1f - underwaterAmount * seaDuckingUnderwater;
            birdsTargetVolume *= 1f - underwaterAmount * birdsDuckingUnderwater;
        }
    }

    private void PrepareUnderwaterSource()
    {
        if (underwaterSource == null && underwaterClip != null && Application.isPlaying)
        {
            underwaterSource = gameObject.AddComponent<AudioSource>();
            underwaterSource.spatialBlend = 0f;
        }

        if (underwaterSource != null && underwaterClip != null)
        {
            underwaterSource.clip = underwaterClip;
        }

        PrepareSource(underwaterSource);
    }

    private float SampleSeaPresence(Vector3 position)
    {
        if (terrain == null || !terrain.IsWaterEnabled)
        {
            return 0f;
        }

        Vector2 origin = new Vector2(position.x, position.z);
        float waterHeight = terrain.WaterHeight;
        float bestPresence = SampleCurrentSeaPresence(origin, waterHeight);

        int samplesPerRing = Mathf.Max(4, seaSamplesPerRing);
        int rings = Mathf.Max(1, seaSampleRings);
        for (int ring = 1; ring <= rings; ring++)
        {
            float ringT = ring / (float)rings;
            float radius = Mathf.Lerp(seaFullVolumeDistance, seaAudibleDistance, ringT);
            bestPresence = Mathf.Max(bestPresence, SampleSeaRing(origin, radius, samplesPerRing, waterHeight));
        }

        return Smooth01(bestPresence);
    }

    private float SampleUnderwaterPresence(Vector3 position)
    {
        if (terrain == null || !terrain.IsWaterEnabled)
        {
            IsListenerUnderwater = false;
            return 0f;
        }

        WaterSurfaceHeight = ResolveWaterSurfaceHeight(position);
        IsListenerUnderwater = ShouldUseUnderwaterAudio(position.y, WaterSurfaceHeight, IsListenerUnderwater);
        if (!IsListenerUnderwater)
        {
            return 0f;
        }

        if (underwaterFullVolumeDepth <= 0f)
        {
            return 1f;
        }

        float depth = WaterSurfaceHeight - position.y;
        return Mathf.InverseLerp(-underwaterSurfaceHysteresis, underwaterFullVolumeDepth, depth);
    }

    private float ResolveWaterSurfaceHeight(Vector3 position)
    {
        float flatWaterHeight = terrain.WaterHeight;
        if (useWaveManagerWaterHeight && waveManager != null)
        {
            return waveManager.GetHeight(position, flatWaterHeight);
        }

        return flatWaterHeight;
    }

    private bool ShouldUseUnderwaterAudio(float sampleY, float waterHeight, bool currentlyUnderwater)
    {
        if (currentlyUnderwater)
        {
            return sampleY <= waterHeight + underwaterSurfaceHysteresis;
        }

        return sampleY < waterHeight - underwaterSurfaceHysteresis;
    }

    private float SampleCurrentSeaPresence(Vector2 origin, float waterHeight)
    {
        float terrainHeight = terrain.SampleTerrainHeight(origin);
        if (terrainHeight <= waterHeight + waterHeightPadding)
        {
            return 1f;
        }

        if (shorelineHeightFade <= 0f)
        {
            return 0f;
        }

        float heightAboveWater = terrainHeight - waterHeight - waterHeightPadding;
        return 1f - Mathf.InverseLerp(0f, shorelineHeightFade, heightAboveWater);
    }

    private float SampleSeaRing(Vector2 origin, float radius, int samplesPerRing, float waterHeight)
    {
        float bestPresence = 0f;
        for (int i = 0; i < samplesPerRing; i++)
        {
            Vector2 samplePoint = origin + DirectionOnRing(i, samplesPerRing) * radius;
            float terrainHeight = terrain.SampleTerrainHeight(samplePoint);
            if (terrainHeight > waterHeight + waterHeightPadding)
            {
                continue;
            }

            bestPresence = Mathf.Max(bestPresence, DistancePresence(radius, seaFullVolumeDistance, seaAudibleDistance));
        }

        return bestPresence;
    }

    private float SampleForestPresence(Vector3 position)
    {
        Vector2 origin = new Vector2(position.x, position.z);
        float bestPresence = SampleForestPoint(origin, origin, 0f);

        int samplesPerRing = Mathf.Max(4, forestSamplesPerRing);
        int rings = Mathf.Max(1, forestSampleRings);
        for (int ring = 1; ring <= rings; ring++)
        {
            float ringT = ring / (float)rings;
            float radius = Mathf.Lerp(forestFullVolumeDistance, forestAudibleDistance, ringT);
            for (int i = 0; i < samplesPerRing; i++)
            {
                Vector2 samplePoint = origin + DirectionOnRing(i, samplesPerRing) * radius;
                bestPresence = Mathf.Max(bestPresence, SampleForestPoint(origin, samplePoint, radius));
            }
        }

        return Smooth01(bestPresence);
    }

    private float SampleForestPoint(Vector2 origin, Vector2 samplePoint, float distance)
    {
        if (terrain == null || !terrain.TrySampleTerrainBiome(samplePoint, out TerrainBiomeSO biome))
        {
            return 0f;
        }

        float biomeStrength = GetForestBiomeStrength(biome);
        if (biomeStrength <= 0f)
        {
            return 0f;
        }

        float distancePresence = distance <= 0f
            ? 1f
            : DistancePresence(Vector2.Distance(origin, samplePoint), forestFullVolumeDistance, forestAudibleDistance);
        return biomeStrength * distancePresence;
    }

    private float GetForestBiomeStrength(TerrainBiomeSO biome)
    {
        if (biome == null)
        {
            return 0f;
        }

        if (forestBiomes != null && forestBiomes.Length > 0)
        {
            for (int i = 0; i < forestBiomes.Length; i++)
            {
                if (forestBiomes[i] == biome)
                {
                    return 1f;
                }
            }

            return 0f;
        }

        float density = biome.GetTotalTreeDensityPerSquareMeter();
        return Mathf.InverseLerp(minimumForestTreeDensity, fullForestTreeDensity, density);
    }

    private static Vector2 DirectionOnRing(int index, int count)
    {
        float angle = count > 0 ? index * Mathf.PI * 2f / count : 0f;
        return new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
    }

    private static float DistancePresence(float distance, float fullDistance, float audibleDistance)
    {
        if (distance <= fullDistance)
        {
            return 1f;
        }

        return 1f - Mathf.InverseLerp(fullDistance, audibleDistance, distance);
    }

    private static float Smooth01(float value)
    {
        float t = Mathf.Clamp01(value);
        return t * t * (3f - 2f * t);
    }

    private void ApplySourceVolume(AudioSource source, float targetVolume, ref float currentVolume, bool instant)
    {
        float clampedTarget = Mathf.Clamp01(targetVolume);
        currentVolume = instant || volumeFadeSpeed <= 0f
            ? clampedTarget
            : Mathf.MoveTowards(currentVolume, clampedTarget, volumeFadeSpeed * Time.deltaTime);

        SetSourceVolume(source, currentVolume);

        if (source == null || source.clip == null)
        {
            return;
        }

        if (currentVolume > SilentVolume && !source.isPlaying)
        {
            source.UnPause();
            if (!source.isPlaying)
            {
                source.Play();
            }
        }
        else if (pauseWhenSilent && currentVolume <= SilentVolume && clampedTarget <= SilentVolume && source.isPlaying)
        {
            source.Pause();
        }
    }

    private static void SetSourceVolume(AudioSource source, float volume)
    {
        if (source != null)
        {
            source.volume = Mathf.Clamp01(volume);
        }
    }
}
