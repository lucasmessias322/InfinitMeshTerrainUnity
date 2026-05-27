using UnityEngine;

[CreateAssetMenu(fileName = "TerrainSplines", menuName = "Procedural Terrain/Terrain Splines")]
public sealed class TerrainSplinesSO : ScriptableObject
{
    public const int SampleCount = 128;

    [Header("Height Mapping")]
    [SerializeField] private AnimationCurve continentalnessHeight = DefaultContinentalnessHeight();
    [SerializeField] private AnimationCurve erosionMultiplier = DefaultErosionMultiplier();

    [Header("Feature Mapping")]
    [SerializeField] private AnimationCurve hillsStrength = DefaultHillsStrength();
    [SerializeField] private AnimationCurve peaksValleysOffset = DefaultPeaksValleysOffset();
    [SerializeField] private AnimationCurve mountainStrength = DefaultMountainStrength();
    [SerializeField] private AnimationCurve detailStrength = DefaultDetailStrength();

    public float Evaluate(TerrainSplineChannel channel, float input)
    {
        AnimationCurve curve = GetCurve(channel);
        return curve != null && curve.length > 0 ? curve.Evaluate(Mathf.Clamp01(input)) : 0f;
    }

    private void OnValidate()
    {
        EnsureCurve(ref continentalnessHeight, DefaultContinentalnessHeight());
        EnsureCurve(ref erosionMultiplier, DefaultErosionMultiplier());
        EnsureCurve(ref hillsStrength, DefaultHillsStrength());
        EnsureCurve(ref peaksValleysOffset, DefaultPeaksValleysOffset());
        EnsureCurve(ref mountainStrength, DefaultMountainStrength());
        EnsureCurve(ref detailStrength, DefaultDetailStrength());
    }

    private static void EnsureCurve(ref AnimationCurve curve, AnimationCurve fallback)
    {
        if (curve == null || curve.length == 0)
        {
            curve = fallback;
        }

        Keyframe[] keys = curve.keys;
        for (int i = 0; i < keys.Length; i++)
        {
            keys[i].time = Mathf.Clamp01(keys[i].time);
        }

        curve.keys = keys;
        curve.preWrapMode = WrapMode.ClampForever;
        curve.postWrapMode = WrapMode.ClampForever;
    }

    private AnimationCurve GetCurve(TerrainSplineChannel channel)
    {
        switch (channel)
        {
            case TerrainSplineChannel.ContinentalnessHeight:
                return continentalnessHeight;
            case TerrainSplineChannel.ErosionMultiplier:
                return erosionMultiplier;
            case TerrainSplineChannel.HillsStrength:
                return hillsStrength;
            case TerrainSplineChannel.PeaksValleysOffset:
                return peaksValleysOffset;
            case TerrainSplineChannel.MountainStrength:
                return mountainStrength;
            case TerrainSplineChannel.DetailStrength:
                return detailStrength;
            default:
                return continentalnessHeight;
        }
    }

    private static AnimationCurve DefaultContinentalnessHeight()
    {
        return CreateCurve(
            new Keyframe(0f, 0.025f),
            new Keyframe(0.18f, 0.055f),
            new Keyframe(0.3f, 0.14f),
            new Keyframe(0.42f, 0.2f),
            new Keyframe(0.68f, 0.26f),
            new Keyframe(1f, 0.34f));
    }

    private static AnimationCurve DefaultErosionMultiplier()
    {
        return CreateCurve(
            new Keyframe(0f, 1.35f),
            new Keyframe(0.35f, 1f),
            new Keyframe(0.65f, 0.55f),
            new Keyframe(1f, 0.18f));
    }

    private static AnimationCurve DefaultHillsStrength()
    {
        return CreateCurve(
            new Keyframe(0f, 0.15f),
            new Keyframe(0.35f, 0.45f),
            new Keyframe(0.7f, 1f),
            new Keyframe(1f, 0.7f));
    }

    private static AnimationCurve DefaultPeaksValleysOffset()
    {
        return CreateCurve(
            new Keyframe(0f, -0.06f),
            new Keyframe(0.32f, -0.02f),
            new Keyframe(0.5f, 0f),
            new Keyframe(0.76f, 0.035f),
            new Keyframe(1f, 0.08f));
    }

    private static AnimationCurve DefaultMountainStrength()
    {
        return CreateCurve(
            new Keyframe(0f, 0f),
            new Keyframe(0.45f, 0f),
            new Keyframe(0.65f, 0.22f),
            new Keyframe(0.85f, 0.62f),
            new Keyframe(1f, 0.88f));
    }

    private static AnimationCurve DefaultDetailStrength()
    {
        return CreateCurve(
            new Keyframe(0f, 0.25f),
            new Keyframe(0.35f, 0.4f),
            new Keyframe(0.65f, 0.8f),
            new Keyframe(1f, 1f));
    }

    private static AnimationCurve CreateCurve(params Keyframe[] keys)
    {
        AnimationCurve curve = new AnimationCurve(keys)
        {
            preWrapMode = WrapMode.ClampForever,
            postWrapMode = WrapMode.ClampForever
        };

        return curve;
    }
}

public enum TerrainSplineChannel
{
    ContinentalnessHeight,
    ErosionMultiplier,
    HillsStrength,
    PeaksValleysOffset,
    MountainStrength,
    DetailStrength,
    Count
}
