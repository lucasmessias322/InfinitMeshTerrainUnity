using System;
using UnityEngine;

[Obsolete("Legacy terrain splines are no longer used by InfinitMeshTerrain.")]
public sealed class TerrainSplinesSO : ScriptableObject
{
    public const int SampleCount = 128;

    [Header("Height Mapping")]
    [SerializeField] private AnimationCurve continentalnessHeight = DefaultContinentalnessHeight();
    [SerializeField] private AnimationCurve erosionMultiplier = DefaultErosionMultiplier();

    public float Evaluate(TerrainSplineChannel channel, float input)
    {
        AnimationCurve curve = GetCurve(channel);
        return curve != null && curve.length > 0 ? curve.Evaluate(Mathf.Clamp01(input)) : 0f;
    }

    private void OnValidate()
    {
        EnsureCurve(ref continentalnessHeight, DefaultContinentalnessHeight());
        EnsureCurve(ref erosionMultiplier, DefaultErosionMultiplier());
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
    Count
}
