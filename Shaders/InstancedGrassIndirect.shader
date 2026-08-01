Shader "InfinitMeshTerrain/Instanced Grass Indirect"
{
    Properties
    {
        _BaseColor ("Base Color", Color) = (0.16, 0.34, 0.08, 1)
        _TipColor ("Tip Color", Color) = (0.54, 0.76, 0.24, 1)
        _BaseMap ("Base Map", 2D) = "white" {}
        _UseBaseMap ("Use Base Map", Float) = 0
        _UseBiomeGrassColor ("Use Biome Grass Color", Float) = 0
        _ReceiveShadows ("Receive Shadows", Float) = 1
        _AmbientStrength ("Ambient Strength", Range(0, 1)) = 1
        _AdditionalLightsStrength ("Additional Lights Strength", Range(0, 32)) = 2
        _AdditionalLightsWrap ("Additional Lights Wrap", Range(0, 1)) = 0.55
        _AdditionalLightsAlbedoInfluence ("Additional Lights Albedo Influence", Range(0, 1)) = 0.55
        _WindColor ("Wind Color", Color) = (0.64, 0.86, 0.28, 1)
        _WindColorDirection ("Wind Wave Direction", Vector) = (1, 0.25, 0, 0)
        _WindColorStrength ("Wind Color Strength", Range(0, 1)) = 0.22
        _WindColorScale ("Wind Color Scale", Range(0.001, 0.2)) = 0.045
        _WindColorSpeed ("Wind Color Speed", Range(0, 4)) = 1
        _WindColorContrast ("Wind Color Contrast", Range(0.1, 8)) = 2.5
        _WindColorTipBias ("Wind Color Tip Bias", Range(0, 1)) = 0.65
        _WindWaveMovementStrength ("Wind Wave Movement Strength", Range(0, 3)) = 0.65
        _Cutoff ("Alpha Cutoff", Range(0, 1)) = 0.18
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "AlphaTest"
            "RenderType" = "TransparentCutout"
        }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            Cull Off
            ZWrite On
            ZTest LEqual

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing
            #pragma multi_compile_fog
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile_fragment _ _SHADOWS_SOFT

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct GrassInstance
            {
                float4 positionScale;
                float4 normalYaw;
                float4 colorWidth;
            };

            StructuredBuffer<GrassInstance> _GrassInstances;
            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);

            CBUFFER_START(UnityPerMaterial)
                half4 _BaseColor;
                half4 _TipColor;
                half _UseBaseMap;
                half _UseBiomeGrassColor;
                half _ReceiveShadows;
                half _AmbientStrength;
                half _AdditionalLightsStrength;
                half _AdditionalLightsWrap;
                half _AdditionalLightsAlbedoInfluence;
                half4 _WindColor;
                half4 _WindColorDirection;
                half _WindColorStrength;
                half _WindColorScale;
                half _WindColorSpeed;
                half _WindColorContrast;
                half _WindColorTipBias;
                half _WindWaveMovementStrength;
                half _Cutoff;
            CBUFFER_END

            float3 _ViewerPosition;
            float4 _FadeDistances;
            float4 _Wind;
            float4 _MeshGrounding;
            float4 _Trample;
            float3 _TramplePosition;

            half3 BiomeGrassColorToAlbedo(half3 color)
            {
#if defined(UNITY_COLORSPACE_GAMMA)
                return color;
#else
                color = saturate(color);
                half3 linearLow = color / half(12.92);
                half3 linearHigh = pow(max((color + half(0.055)) / half(1.055), half3(0.0, 0.0, 0.0)), half(2.4));
                return lerp(linearLow, linearHigh, step(half3(0.04045, 0.04045, 0.04045), color));
#endif
            }

            struct Attributes
            {
                float3 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                uint instanceID : SV_InstanceID;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                half3 normalWS : TEXCOORD1;
                half3 color : TEXCOORD2;
                half fade : TEXCOORD3;
                half fogFactor : TEXCOORD4;
                float3 positionWS : TEXCOORD5;
                half windColorMask : TEXCOORD6;
#if (defined(_ADDITIONAL_LIGHTS) || defined(_ADDITIONAL_LIGHTS_VERTEX)) && !USE_CLUSTER_LIGHT_LOOP
                half3 vertexLighting : TEXCOORD7;
#endif
            };

            float GrassWindHash(float2 lattice)
            {
                return frac(sin(dot(lattice, float2(127.1, 311.7))) * 43758.5453123);
            }

            float2 GrassWindGradient(float2 lattice)
            {
                float hash = GrassWindHash(lattice);
                float2 gradient = float2(hash - 0.5, frac(hash * 7.13) - 0.5);
                return normalize(gradient + float2(0.0001, 0.0001));
            }

            float GrassWindPerlin(float2 position)
            {
                float2 lattice = floor(position);
                float2 local = frac(position);
                float2 fade = local * local * local * (local * (local * 6.0 - 15.0) + 10.0);

                float corner00 = dot(GrassWindGradient(lattice + float2(0.0, 0.0)), local - float2(0.0, 0.0));
                float corner10 = dot(GrassWindGradient(lattice + float2(1.0, 0.0)), local - float2(1.0, 0.0));
                float corner01 = dot(GrassWindGradient(lattice + float2(0.0, 1.0)), local - float2(0.0, 1.0));
                float corner11 = dot(GrassWindGradient(lattice + float2(1.0, 1.0)), local - float2(1.0, 1.0));

                float lower = lerp(corner00, corner10, fade.x);
                float upper = lerp(corner01, corner11, fade.x);
                return lerp(lower, upper, fade.y) * 0.5 + 0.5;
            }

            float GrassWindFbm(float2 position)
            {
                float value = 0.0;
                float amplitude = 0.5;
                float amplitudeSum = 0.0;

                [unroll]
                for (int octave = 0; octave < 3; octave++)
                {
                    value += GrassWindPerlin(position) * amplitude;
                    amplitudeSum += amplitude;
                    position = position * 2.07 + float2(17.13, 9.21);
                    amplitude *= 0.5;
                }

                return value / max(amplitudeSum, 0.0001);
            }

            float2 GetGrassWindWaveDirection()
            {
                float2 colorDirection = (float2)_WindColorDirection.xy;
                return dot(colorDirection, colorDirection) > 0.000001
                    ? normalize(colorDirection)
                    : normalize(_Wind.xy + float2(0.0001, 0.0001));
            }

            half EvaluateGrassWindWaveMask(float2 positionXZ)
            {
                float scale = max((float)_WindColorScale, 0.0001);
                float2 windPlanar = GetGrassWindWaveDirection();
                float2 windTangent = float2(-windPlanar.y, windPlanar.x);
                float scroll = _Time.y * max(_Wind.w, 0.0) * max((float)_WindColorSpeed, 0.0);
                float2 advectedPosition = positionXZ - windPlanar * scroll;
                float2 noisePosition = float2(
                    dot(advectedPosition, windTangent) * 0.35,
                    dot(advectedPosition, windPlanar)) * scale;
                float noise = GrassWindFbm(noisePosition);
                float contrast = max((float)_WindColorContrast, 0.1);
                float mask = saturate((noise - 0.5) * contrast + 0.5);
                return half(smoothstep(0.35, 0.85, mask));
            }

            half EvaluateGrassWindColorMask(half windWaveMask)
            {
                return half(windWaveMask * saturate(_WindColorStrength));
            }

            float DitherNoise(float2 pixelPosition)
            {
                return frac(52.9829189 * frac(dot(pixelPosition, float2(0.06711056, 0.00583715))));
            }

            half3 EvaluateDirectLight(Light light, half3 normalWS, half receiveShadows)
            {
                half shadowAttenuation = lerp(half(1.0), light.shadowAttenuation, saturate(receiveShadows));
                half ndotl = saturate(dot(normalWS, light.direction));
                return light.color * (ndotl * light.distanceAttenuation * shadowAttenuation);
            }

            half EvaluateWrappedDiffuse(half3 normalWS, half3 lightDirectionWS, half wrap)
            {
                half wrappedNdotL = (dot(normalWS, lightDirectionWS) + wrap) / max(half(1.0) + wrap, half(0.0001));
                return saturate(wrappedNdotL);
            }

            half3 EvaluateAdditionalLightContribution(Light light, half3 normalWS)
            {
                half ndotl = EvaluateWrappedDiffuse(normalWS, light.direction, saturate(_AdditionalLightsWrap));
                return light.color * (ndotl * light.distanceAttenuation * light.shadowAttenuation * _AdditionalLightsStrength);
            }

            half3 EvaluateAdditionalVertexLights(float3 positionWS, half3 normalWS)
            {
                half3 lighting = half3(0.0, 0.0, 0.0);

#if (defined(_ADDITIONAL_LIGHTS) || defined(_ADDITIONAL_LIGHTS_VERTEX)) && !USE_CLUSTER_LIGHT_LOOP
                uint lightsCount = GetAdditionalLightsCount();
                for (uint lightIndex = 0u; lightIndex < lightsCount; lightIndex++)
                {
                    Light light = GetAdditionalLight(lightIndex, positionWS);
                    lighting += EvaluateAdditionalLightContribution(light, normalWS);
                }
#endif

                return lighting;
            }

            InputData InitializeGrassInputData(float3 positionWS, float4 positionHCS, half3 normalWS)
            {
                InputData inputData = (InputData)0;
                inputData.positionWS = positionWS;
                inputData.positionCS = positionHCS;
                inputData.normalWS = normalWS;
                inputData.viewDirectionWS = GetWorldSpaceNormalizeViewDir(positionWS);
                inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(positionHCS);
                inputData.shadowMask = half4(1.0, 1.0, 1.0, 1.0);
                return inputData;
            }

            half3 EvaluateAdditionalFragmentLights(InputData inputData, half3 normalWS)
            {
                half3 lighting = half3(0.0, 0.0, 0.0);

#if defined(_ADDITIONAL_LIGHTS) && USE_CLUSTER_LIGHT_LOOP
                uint pixelLightCount = GetAdditionalLightsCount();

                [loop] for (uint lightIndex = 0u; lightIndex < min(URP_FP_DIRECTIONAL_LIGHTS_COUNT, MAX_VISIBLE_LIGHTS); lightIndex++)
                {
                    CLUSTER_LIGHT_LOOP_SUBTRACTIVE_LIGHT_CHECK
                    Light light = GetAdditionalLight(lightIndex, inputData.positionWS);
                    lighting += EvaluateAdditionalLightContribution(light, normalWS);
                }

                LIGHT_LOOP_BEGIN(pixelLightCount)
                    Light light = GetAdditionalLight(lightIndex, inputData.positionWS);
                    lighting += EvaluateAdditionalLightContribution(light, normalWS);
                LIGHT_LOOP_END
#endif

                return lighting;
            }

            Varyings Vert(Attributes input)
            {
                GrassInstance instanceData = _GrassInstances[input.instanceID];

                float3 origin = instanceData.positionScale.xyz;
                float height = max(0.01, instanceData.positionScale.w);
                float width = max(0.01, instanceData.colorWidth.w);
                float3 terrainNormal = normalize(instanceData.normalYaw.xyz + float3(0.0001, 0.0001, 0.0001));

                float yawSin;
                float yawCos;
                sincos(instanceData.normalYaw.w, yawSin, yawCos);

                float3 yawForward = normalize(float3(yawSin, 0.0, yawCos));
                float3 tangent = normalize(cross(terrainNormal, yawForward) + float3(0.0001, 0.0, 0.0001));
                float3 bitangent = normalize(cross(tangent, terrainNormal));

                float swayMask = saturate(input.uv.y);
                float2 windPlanar = normalize(_Wind.xy + float2(0.0001, 0.0001));
                float phase = dot(origin.xz, float2(0.071, 0.047)) + _Time.y * _Wind.w + instanceData.normalYaw.w;
                float gust = sin(phase) * 0.65 + sin(phase * 2.17) * 0.35;
                half windWaveMask = EvaluateGrassWindWaveMask(origin.xz);
                float waveGust = (float)windWaveMask * max((float)_WindWaveMovementStrength, 0.0);
                float2 windWavePlanar = GetGrassWindWaveDirection();
                float3 windOffset = (
                    float3(windPlanar.x, 0.0, windPlanar.y) * gust +
                    float3(windWavePlanar.x, 0.0, windWavePlanar.y) * waveGust)
                    * _Wind.z * swayMask * swayMask;

                float3 localOffset =
                    tangent * (input.positionOS.x * width) +
                    bitangent * (input.positionOS.z * width) +
                    terrainNormal * ((input.positionOS.y - _MeshGrounding.x) * height + _MeshGrounding.y);

                float trampleRadius = max(_Trample.x, 0.0);
                float2 trampleDelta = origin.xz - _TramplePosition.xz;
                float trampleDistance = length(trampleDelta);
                float trampleMask = trampleRadius > 0.0001
                    ? 1.0 - smoothstep(trampleRadius * 0.35, trampleRadius, trampleDistance)
                    : 0.0;
                trampleMask *= saturate(_Trample.y);
                float2 trampleDirection = trampleDistance > 0.001
                    ? trampleDelta / trampleDistance
                    : yawForward.xz;
                float tipMask = swayMask * swayMask;
                float3 trampleOffset = float3(trampleDirection.x, -saturate(_Trample.z), trampleDirection.y)
                    * (trampleMask * tipMask * height);
                float3 bentNormal = normalize(lerp(
                    terrainNormal,
                    normalize(float3(trampleDirection.x, 0.35, trampleDirection.y)),
                    saturate(trampleMask * tipMask * 0.55)));

                float3 positionWS = origin + localOffset + windOffset + trampleOffset;
                float distanceToViewer = distance(_ViewerPosition.xz, positionWS.xz);
                float fade = 1.0 - smoothstep(_FadeDistances.x, _FadeDistances.y, distanceToViewer);

                Varyings output;
                output.positionHCS = TransformWorldToHClip(positionWS);
                output.uv = input.uv;
                output.normalWS = half3(bentNormal);
                output.color = half3(instanceData.colorWidth.rgb);
                output.fade = half(fade);
                output.fogFactor = half(ComputeFogFactor(output.positionHCS.z));
                output.positionWS = positionWS;
                output.windColorMask = EvaluateGrassWindColorMask(windWaveMask);
#if (defined(_ADDITIONAL_LIGHTS) || defined(_ADDITIONAL_LIGHTS_VERTEX)) && !USE_CLUSTER_LIGHT_LOOP
                output.vertexLighting = EvaluateAdditionalVertexLights(positionWS, output.normalWS);
#endif
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                half centeredUv = abs(input.uv.x * 2.0 - 1.0);
                half bladeWidth = lerp(1.0, 0.12, input.uv.y * input.uv.y);
                half bladeMask = saturate((bladeWidth - centeredUv) * 8.0);
                half4 baseMap = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv);
                half textured = saturate(_UseBaseMap);
                half alpha = lerp(bladeMask, baseMap.a, textured);
                clip(alpha - _Cutoff);
                clip(input.fade - DitherNoise(input.positionHCS.xy));

                half3 normalWS = normalize(input.normalWS);
                float4 shadowCoord = TransformWorldToShadowCoord(input.positionWS);
                Light mainLight = GetMainLight(shadowCoord);
                half3 ambient = SampleSH(normalWS);
                half3 lighting = ambient * _AmbientStrength + EvaluateDirectLight(mainLight, normalWS, _ReceiveShadows);
                half3 additionalLighting = half3(0.0, 0.0, 0.0);

#if (defined(_ADDITIONAL_LIGHTS) || defined(_ADDITIONAL_LIGHTS_VERTEX)) && !USE_CLUSTER_LIGHT_LOOP
                additionalLighting += input.vertexLighting;
#endif

#if defined(_ADDITIONAL_LIGHTS) && USE_CLUSTER_LIGHT_LOOP
                InputData inputData = InitializeGrassInputData(input.positionWS, input.positionHCS, normalWS);
                additionalLighting += EvaluateAdditionalFragmentLights(inputData, normalWS);
#endif

                half3 bladeGradient = lerp(_BaseColor.rgb, _TipColor.rgb, input.uv.y);
                half3 styledAlbedo = bladeGradient * input.color * lerp(half3(1.0, 1.0, 1.0), baseMap.rgb, textured);
                half3 biomeAlbedo = BiomeGrassColorToAlbedo(input.color);
                half3 biomeTexturedAlbedo = biomeAlbedo * lerp(half3(1.0, 1.0, 1.0), baseMap.rgb, textured);
                half3 albedo = lerp(styledAlbedo, biomeTexturedAlbedo, saturate(_UseBiomeGrassColor));
                half windTipMask = lerp(half(1.0), saturate(input.uv.y), saturate(_WindColorTipBias));
                albedo = lerp(albedo, _WindColor.rgb, input.windColorMask * windTipMask);
                half3 additionalAlbedo = lerp(half3(1.0, 1.0, 1.0), albedo, saturate(_AdditionalLightsAlbedoInfluence));
                half3 color = albedo * lighting + additionalAlbedo * additionalLighting;
                color = MixFog(color, input.fogFactor);
                return half4(color, 1.0);
            }
            ENDHLSL
        }
    }
}
