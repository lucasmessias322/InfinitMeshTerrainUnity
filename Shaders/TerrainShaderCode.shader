Shader "InfinitMeshTerrain/TerrainShaderCode"
{
    Properties
    {
        [NoScaleOffset] _SplatMap("Splat Map 0 (RGBA)", 2D) = "black" {}
        [NoScaleOffset] _SplatMap2("Splat Map 1 (RGBA)", 2D) = "black" {}

        [NoScaleOffset] _Texture2D_R("Layer 1 Albedo (Map0 R)", 2D) = "white" {}
        [NoScaleOffset] _Texture2D_G("Layer 2 Albedo (Map0 G)", 2D) = "white" {}
        [NoScaleOffset] _Texture2D_B("Layer 3 Albedo (Map0 B)", 2D) = "white" {}
        [NoScaleOffset] _Texture2D_A("Layer 4 Albedo (Map0 A)", 2D) = "white" {}
        [NoScaleOffset] _Texture2D_R2("Layer 5 Albedo (Map1 R)", 2D) = "white" {}
        [NoScaleOffset] _Texture2D_G2("Layer 6 Albedo (Map1 G)", 2D) = "white" {}
        [NoScaleOffset] _Texture2D_B2("Layer 7 Albedo (Map1 B)", 2D) = "white" {}
        [NoScaleOffset] _Texture2D_A2("Layer 8 Albedo (Map1 A)", 2D) = "white" {}

        _Layer1TextureScale("Layer 1 Texture Scale", Float) = 1
        _Layer2TextureScale("Layer 2 Texture Scale", Float) = 1
        _Layer3TextureScale("Layer 3 Texture Scale", Float) = 1
        _Layer4TextureScale("Layer 4 Texture Scale", Float) = 1
        _Layer5TextureScale("Layer 5 Texture Scale", Float) = 1
        _Layer6TextureScale("Layer 6 Texture Scale", Float) = 1
        _Layer7TextureScale("Layer 7 Texture Scale", Float) = 1
        _Layer8TextureScale("Layer 8 Texture Scale", Float) = 1

        _Layer1Color("Layer 1 Color", Color) = (1, 1, 1, 1)
        _Layer2Color("Layer 2 Color", Color) = (1, 1, 1, 1)
        _Layer3Color("Layer 3 Color", Color) = (1, 1, 1, 1)
        _Layer4Color("Layer 4 Color", Color) = (1, 1, 1, 1)
        _Layer5Color("Layer 5 Color", Color) = (1, 1, 1, 1)
        _Layer6Color("Layer 6 Color", Color) = (1, 1, 1, 1)
        _Layer7Color("Layer 7 Color", Color) = (1, 1, 1, 1)
        _Layer8Color("Layer 8 Color", Color) = (1, 1, 1, 1)

        [NoScaleOffset][Normal] _Layer1Normal("Layer 1 Normal", 2D) = "bump" {}
        [NoScaleOffset][Normal] _Layer2Normal("Layer 2 Normal", 2D) = "bump" {}
        [NoScaleOffset][Normal] _Layer3Normal("Layer 3 Normal", 2D) = "bump" {}
        [NoScaleOffset][Normal] _Layer4Normal("Layer 4 Normal", 2D) = "bump" {}
        [NoScaleOffset][Normal] _Layer5Normal("Layer 5 Normal", 2D) = "bump" {}
        [NoScaleOffset][Normal] _Layer6Normal("Layer 6 Normal", 2D) = "bump" {}
        [NoScaleOffset][Normal] _Layer7Normal("Layer 7 Normal", 2D) = "bump" {}
        [NoScaleOffset][Normal] _Layer8Normal("Layer 8 Normal", 2D) = "bump" {}

        _Layer1NormalScale("Layer 1 Normal Scale", Range(0, 4)) = 1
        _Layer2NormalScale("Layer 2 Normal Scale", Range(0, 4)) = 1
        _Layer3NormalScale("Layer 3 Normal Scale", Range(0, 4)) = 1
        _Layer4NormalScale("Layer 4 Normal Scale", Range(0, 4)) = 1
        _Layer5NormalScale("Layer 5 Normal Scale", Range(0, 4)) = 1
        _Layer6NormalScale("Layer 6 Normal Scale", Range(0, 4)) = 1
        _Layer7NormalScale("Layer 7 Normal Scale", Range(0, 4)) = 1
        _Layer8NormalScale("Layer 8 Normal Scale", Range(0, 4)) = 1

        _Metallic("Metallic", Range(0, 1)) = 0
        _Smoothness("Smoothness", Range(0, 1)) = 0
        _Occlusion("Ambient Occlusion", Range(0, 1)) = 1

        [HideInInspector] _Cull("__cull", Float) = 2
        [HideInInspector] _ZWrite("__zw", Float) = 1
        [HideInInspector] _QueueOffset("Queue Offset", Float) = 0
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
            "UniversalMaterialType" = "Lit"
            "IgnoreProjector" = "True"
        }
        LOD 300

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
        #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/GlobalSamplers.hlsl"
        #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Packing.hlsl"

        TEXTURE2D(_SplatMap);
        TEXTURE2D(_SplatMap2);

        TEXTURE2D(_Texture2D_R);
        TEXTURE2D(_Texture2D_G);
        TEXTURE2D(_Texture2D_B);
        TEXTURE2D(_Texture2D_A);
        TEXTURE2D(_Texture2D_R2);
        TEXTURE2D(_Texture2D_G2);
        TEXTURE2D(_Texture2D_B2);
        TEXTURE2D(_Texture2D_A2);

        TEXTURE2D(_Layer1Normal);
        TEXTURE2D(_Layer2Normal);
        TEXTURE2D(_Layer3Normal);
        TEXTURE2D(_Layer4Normal);
        TEXTURE2D(_Layer5Normal);
        TEXTURE2D(_Layer6Normal);
        TEXTURE2D(_Layer7Normal);
        TEXTURE2D(_Layer8Normal);

        CBUFFER_START(UnityPerMaterial)
            half4 _Layer1Color;
            half4 _Layer2Color;
            half4 _Layer3Color;
            half4 _Layer4Color;
            half4 _Layer5Color;
            half4 _Layer6Color;
            half4 _Layer7Color;
            half4 _Layer8Color;

            half _Layer1TextureScale;
            half _Layer2TextureScale;
            half _Layer3TextureScale;
            half _Layer4TextureScale;
            half _Layer5TextureScale;
            half _Layer6TextureScale;
            half _Layer7TextureScale;
            half _Layer8TextureScale;

            half _Layer1NormalScale;
            half _Layer2NormalScale;
            half _Layer3NormalScale;
            half _Layer4NormalScale;
            half _Layer5NormalScale;
            half _Layer6NormalScale;
            half _Layer7NormalScale;
            half _Layer8NormalScale;

            half _Metallic;
            half _Smoothness;
            half _Occlusion;
        CBUFFER_END

        struct TerrainWeights
        {
            half4 map0;
            half4 map1;
        };

        struct TerrainAttributes
        {
            float4 positionOS : POSITION;
            float3 normalOS : NORMAL;
            float4 tangentOS : TANGENT;
            float2 uv : TEXCOORD0;
            UNITY_VERTEX_INPUT_INSTANCE_ID
        };

        struct TerrainVaryings
        {
            float4 positionCS : SV_POSITION;
            float3 positionWS : TEXCOORD0;
            half3 normalWS : TEXCOORD1;
            half4 tangentWS : TEXCOORD2;
            float2 uv : TEXCOORD3;
            half fogFactor : TEXCOORD4;
            half3 vertexLighting : TEXCOORD5;
            UNITY_VERTEX_INPUT_INSTANCE_ID
            UNITY_VERTEX_OUTPUT_STEREO
        };

        float2 TerrainLayerUV(float2 uv, half textureScale)
        {
            return uv * max(textureScale, half(0.001));
        }

        half3 ApplyTerrainNormalStrength(half3 normalTS, half strength)
        {
            return half3(normalTS.xy * strength, lerp(half(1.0), normalTS.z, saturate(strength)));
        }

        TerrainWeights SampleTerrainWeights(float2 uv)
        {
            TerrainWeights weights;
            weights.map0 = SAMPLE_TEXTURE2D(_SplatMap, sampler_LinearClamp, uv);
            weights.map1 = SAMPLE_TEXTURE2D(_SplatMap2, sampler_LinearClamp, uv);

            half totalWeight = dot(weights.map0, half4(1.0, 1.0, 1.0, 1.0))
                + dot(weights.map1, half4(1.0, 1.0, 1.0, 1.0));

            if (totalWeight <= half(0.0001))
            {
                weights.map0 = half4(1.0, 0.0, 0.0, 0.0);
                weights.map1 = half4(0.0, 0.0, 0.0, 0.0);
            }

            return weights;
        }

        half3 SampleLayerAlbedo(TEXTURE2D_PARAM(layerTexture, layerSampler), float2 uv, half textureScale, half4 tint)
        {
            return SAMPLE_TEXTURE2D(layerTexture, layerSampler, TerrainLayerUV(uv, textureScale)).rgb * tint.rgb;
        }

        half3 SampleLayerNormal(TEXTURE2D_PARAM(normalTexture, normalSampler), float2 uv, half textureScale, half normalScale)
        {
            half3 normalTS = UnpackNormal(SAMPLE_TEXTURE2D(normalTexture, normalSampler, TerrainLayerUV(uv, textureScale)));
            return ApplyTerrainNormalStrength(normalTS, normalScale);
        }

        half3 BlendTerrainAlbedo(float2 uv, TerrainWeights weights)
        {
            half3 albedo = half3(0.0, 0.0, 0.0);
            albedo += SampleLayerAlbedo(TEXTURE2D_ARGS(_Texture2D_R, sampler_LinearRepeat), uv, _Layer1TextureScale, _Layer1Color) * weights.map0.r;
            albedo += SampleLayerAlbedo(TEXTURE2D_ARGS(_Texture2D_G, sampler_LinearRepeat), uv, _Layer2TextureScale, _Layer2Color) * weights.map0.g;
            albedo += SampleLayerAlbedo(TEXTURE2D_ARGS(_Texture2D_B, sampler_LinearRepeat), uv, _Layer3TextureScale, _Layer3Color) * weights.map0.b;
            albedo += SampleLayerAlbedo(TEXTURE2D_ARGS(_Texture2D_A, sampler_LinearRepeat), uv, _Layer4TextureScale, _Layer4Color) * weights.map0.a;
            albedo += SampleLayerAlbedo(TEXTURE2D_ARGS(_Texture2D_R2, sampler_LinearRepeat), uv, _Layer5TextureScale, _Layer5Color) * weights.map1.r;
            albedo += SampleLayerAlbedo(TEXTURE2D_ARGS(_Texture2D_G2, sampler_LinearRepeat), uv, _Layer6TextureScale, _Layer6Color) * weights.map1.g;
            albedo += SampleLayerAlbedo(TEXTURE2D_ARGS(_Texture2D_B2, sampler_LinearRepeat), uv, _Layer7TextureScale, _Layer7Color) * weights.map1.b;
            albedo += SampleLayerAlbedo(TEXTURE2D_ARGS(_Texture2D_A2, sampler_LinearRepeat), uv, _Layer8TextureScale, _Layer8Color) * weights.map1.a;
            return albedo;
        }

        half3 BlendTerrainNormals(float2 uv, TerrainWeights weights)
        {
            half3 normalTS = half3(0.0, 0.0, 0.0);
            normalTS += SampleLayerNormal(TEXTURE2D_ARGS(_Layer1Normal, sampler_LinearRepeat), uv, _Layer1TextureScale, _Layer1NormalScale) * weights.map0.r;
            normalTS += SampleLayerNormal(TEXTURE2D_ARGS(_Layer2Normal, sampler_LinearRepeat), uv, _Layer2TextureScale, _Layer2NormalScale) * weights.map0.g;
            normalTS += SampleLayerNormal(TEXTURE2D_ARGS(_Layer3Normal, sampler_LinearRepeat), uv, _Layer3TextureScale, _Layer3NormalScale) * weights.map0.b;
            normalTS += SampleLayerNormal(TEXTURE2D_ARGS(_Layer4Normal, sampler_LinearRepeat), uv, _Layer4TextureScale, _Layer4NormalScale) * weights.map0.a;
            normalTS += SampleLayerNormal(TEXTURE2D_ARGS(_Layer5Normal, sampler_LinearRepeat), uv, _Layer5TextureScale, _Layer5NormalScale) * weights.map1.r;
            normalTS += SampleLayerNormal(TEXTURE2D_ARGS(_Layer6Normal, sampler_LinearRepeat), uv, _Layer6TextureScale, _Layer6NormalScale) * weights.map1.g;
            normalTS += SampleLayerNormal(TEXTURE2D_ARGS(_Layer7Normal, sampler_LinearRepeat), uv, _Layer7TextureScale, _Layer7NormalScale) * weights.map1.b;
            normalTS += SampleLayerNormal(TEXTURE2D_ARGS(_Layer8Normal, sampler_LinearRepeat), uv, _Layer8TextureScale, _Layer8NormalScale) * weights.map1.a;

            return dot(normalTS, normalTS) > half(0.00001)
                ? normalize(normalTS)
                : half3(0.0, 0.0, 1.0);
        }

        SurfaceData BuildTerrainSurfaceData(float2 uv)
        {
            TerrainWeights weights = SampleTerrainWeights(uv);

            SurfaceData surfaceData = (SurfaceData)0;
            surfaceData.albedo = BlendTerrainAlbedo(uv, weights);
            surfaceData.specular = half3(0.0, 0.0, 0.0);
            surfaceData.metallic = _Metallic;
            surfaceData.smoothness = _Smoothness;
            surfaceData.normalTS = BlendTerrainNormals(uv, weights);
            surfaceData.emission = half3(0.0, 0.0, 0.0);
            surfaceData.occlusion = _Occlusion;
            surfaceData.alpha = half(1.0);
            surfaceData.clearCoatMask = half(0.0);
            surfaceData.clearCoatSmoothness = half(0.0);
            return surfaceData;
        }

        half3x3 BuildTerrainTangentToWorld(half3 normalWS, half4 tangentWS)
        {
            half3 tangent = tangentWS.xyz;
            half tangentSign = tangentWS.w;

            if (dot(tangent, tangent) <= half(0.00001))
            {
                tangent = half3(1.0, 0.0, 0.0) - normalWS * dot(normalWS, half3(1.0, 0.0, 0.0));

                if (dot(tangent, tangent) <= half(0.00001))
                {
                    tangent = half3(0.0, 0.0, 1.0) - normalWS * dot(normalWS, half3(0.0, 0.0, 1.0));
                }

                tangent = normalize(tangent);
                tangentSign = half(-1.0);
            }
            else
            {
                tangent = normalize(tangent);
                tangentSign = tangentSign == half(0.0) ? half(-1.0) : tangentSign;
            }

            half3 bitangent = tangentSign * cross(normalWS, tangent);
            return half3x3(tangent, normalize(bitangent), normalWS);
        }

        void InitializeTerrainInputData(TerrainVaryings input, half3 normalTS, out InputData inputData)
        {
            inputData = (InputData)0;
            inputData.positionWS = input.positionWS;
            inputData.positionCS = input.positionCS;
            inputData.tangentToWorld = BuildTerrainTangentToWorld(normalize(input.normalWS), input.tangentWS);
            inputData.normalWS = TransformTangentToWorld(normalTS, inputData.tangentToWorld);
            inputData.normalWS = NormalizeNormalPerPixel(inputData.normalWS);
            inputData.viewDirectionWS = SafeNormalize(GetWorldSpaceViewDir(input.positionWS));
            #if defined(MAIN_LIGHT_CALCULATE_SHADOWS)
                inputData.shadowCoord = TransformWorldToShadowCoord(inputData.positionWS);
            #else
                inputData.shadowCoord = float4(0.0, 0.0, 0.0, 0.0);
            #endif
            inputData.fogCoord = input.fogFactor;
            inputData.vertexLighting = input.vertexLighting;
            inputData.bakedGI = SampleSH(inputData.normalWS);
            inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);
            inputData.shadowMask = half4(1.0, 1.0, 1.0, 1.0);
        }

        TerrainVaryings TerrainForwardVertex(TerrainAttributes input)
        {
            TerrainVaryings output = (TerrainVaryings)0;
            UNITY_SETUP_INSTANCE_ID(input);
            UNITY_TRANSFER_INSTANCE_ID(input, output);
            UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

            VertexPositionInputs positionInput = GetVertexPositionInputs(input.positionOS.xyz);
            VertexNormalInputs normalInput = GetVertexNormalInputs(input.normalOS, input.tangentOS);

            output.positionCS = positionInput.positionCS;
            output.positionWS = positionInput.positionWS;
            output.normalWS = NormalizeNormalPerVertex(normalInput.normalWS);
            output.tangentWS = half4(normalInput.tangentWS, input.tangentOS.w * GetOddNegativeScale());
            output.uv = input.uv;
            output.fogFactor = ComputeFogFactor(positionInput.positionCS.z);
            output.vertexLighting = VertexLighting(positionInput.positionWS, output.normalWS);
            return output;
        }

        half4 TerrainForwardFragment(TerrainVaryings input) : SV_Target
        {
            UNITY_SETUP_INSTANCE_ID(input);
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

            SurfaceData surfaceData = BuildTerrainSurfaceData(input.uv);

            InputData inputData;
            InitializeTerrainInputData(input, surfaceData.normalTS, inputData);

            half4 color = UniversalFragmentPBR(inputData, surfaceData);
            color.rgb = MixFog(color.rgb, inputData.fogCoord);
            color.a = half(1.0);
            return color;
        }

        TerrainVaryings TerrainDepthNormalsVertex(TerrainAttributes input)
        {
            return TerrainForwardVertex(input);
        }

        void TerrainDepthNormalsFragment(TerrainVaryings input, out half4 outNormalWS : SV_Target0)
        {
            UNITY_SETUP_INSTANCE_ID(input);
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

            SurfaceData surfaceData = BuildTerrainSurfaceData(input.uv);
            half3x3 tangentToWorld = BuildTerrainTangentToWorld(normalize(input.normalWS), input.tangentWS);
            half3 normalWS = NormalizeNormalPerPixel(TransformTangentToWorld(surfaceData.normalTS, tangentToWorld));

            #if defined(_GBUFFER_NORMALS_OCT)
                float2 octNormalWS = PackNormalOctQuadEncode(normalWS);
                float2 remappedOctNormalWS = saturate(octNormalWS * 0.5 + 0.5);
                half3 packedNormalWS = PackFloat2To888(remappedOctNormalWS);
                outNormalWS = half4(packedNormalWS, 0.0);
            #else
                outNormalWS = half4(normalWS, 0.0);
            #endif
        }

        struct TerrainDepthVaryings
        {
            float4 positionCS : SV_POSITION;
            UNITY_VERTEX_INPUT_INSTANCE_ID
            UNITY_VERTEX_OUTPUT_STEREO
        };

        TerrainDepthVaryings TerrainDepthVertex(TerrainAttributes input)
        {
            TerrainDepthVaryings output = (TerrainDepthVaryings)0;
            UNITY_SETUP_INSTANCE_ID(input);
            UNITY_TRANSFER_INSTANCE_ID(input, output);
            UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
            output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
            return output;
        }

        half TerrainDepthFragment(TerrainDepthVaryings input) : SV_Target
        {
            UNITY_SETUP_INSTANCE_ID(input);
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
            return input.positionCS.z;
        }
        ENDHLSL

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            ZWrite[_ZWrite]
            Cull[_Cull]

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex TerrainForwardVertex
            #pragma fragment TerrainForwardFragment

            #pragma multi_compile_instancing
            #pragma multi_compile_fog
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile _ EVALUATE_SH_MIXED EVALUATE_SH_VERTEX
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BLENDING
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BOX_PROJECTION
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_ATLAS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile_fragment _ _LIGHT_COOKIES
            #pragma multi_compile _ _LIGHT_LAYERS
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            ZWrite On
            ZTest LEqual
            ColorMask 0
            Cull[_Cull]

            HLSLPROGRAM
            #pragma target 2.0
            #pragma vertex ShadowPassVertex
            #pragma fragment ShadowPassFragment
            #pragma multi_compile_instancing
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            float3 _LightDirection;
            float3 _LightPosition;

            struct ShadowAttributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct ShadowVaryings
            {
                float4 positionCS : SV_POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            ShadowVaryings ShadowPassVertex(ShadowAttributes input)
            {
                ShadowVaryings output = (ShadowVaryings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);

                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                float3 normalWS = TransformObjectToWorldNormal(input.normalOS);

                #if _CASTING_PUNCTUAL_LIGHT_SHADOW
                    float3 lightDirectionWS = normalize(_LightPosition - positionWS);
                #else
                    float3 lightDirectionWS = _LightDirection;
                #endif

                output.positionCS = TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, lightDirectionWS));
                output.positionCS = ApplyShadowClamping(output.positionCS);
                return output;
            }

            half4 ShadowPassFragment(ShadowVaryings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                return 0;
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }

            ZWrite On
            ColorMask R
            Cull[_Cull]

            HLSLPROGRAM
            #pragma target 2.0
            #pragma vertex TerrainDepthVertex
            #pragma fragment TerrainDepthFragment
            #pragma multi_compile_instancing
            ENDHLSL
        }

        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }

            ZWrite On
            Cull[_Cull]

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex TerrainDepthNormalsVertex
            #pragma fragment TerrainDepthNormalsFragment
            #pragma multi_compile_instancing
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT
            ENDHLSL
        }
    }

    FallBack "Hidden/Universal Render Pipeline/FallbackError"
}
