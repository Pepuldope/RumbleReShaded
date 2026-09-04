// UltraShade — RumbleShade showcase pack.
// A full "cinematic realism" grading stack: ACES filmic tonemapping, exposure,
// white balance, saturation/contrast, soft bloom, sharpening, chromatic
// aberration, film grain, vignette and depth-based atmospheric fog.
// This is the realistic ceiling of what a screen-space shader can do — see the
// RumbleShade README for why true raytracing is not possible from a mod.
//
// Runs as a real URP fullscreen blit AFTER transparents, so the image it grades
// includes particles and world-space UI. The camera image arrives as
// `_BlitTexture`; the depth buffer is still available as `_CameraDepthTexture`
// (RumbleReShaded requests it via ConfigureInput(Depth)).
Shader "RumbleShade/UltraShade"
{
    Properties
    {
        _Exposure("Exposure", Range(0, 3)) = 1
        _Temperature("Temperature (cold/warm)", Range(-1, 1)) = 0.08
        _Saturation("Saturation", Range(0, 2)) = 1.12
        _Contrast("Contrast", Range(0.5, 2)) = 1.06
        _Tonemap("ACES Tonemap (0=off 1=on)", Range(0, 1)) = 1
        _BloomIntensity("Bloom Intensity", Range(0, 2)) = 0.4
        _BloomThreshold("Bloom Threshold", Range(0, 2)) = 0.85
        _BloomRadius("Bloom Radius (px)", Range(0, 12)) = 4
        _Sharpen("Sharpen", Range(0, 1)) = 0.2
        _ChromAb("Chromatic Aberration", Range(0, 5)) = 0.6
        _GrainStrength("Film Grain", Range(0, 0.2)) = 0.025
        _VignetteStrength("Vignette", Range(0, 1)) = 0.3
        _FogDensity("Fog Density", Range(0, 0.2)) = 0.01
        _FogColor("Fog Color", Color) = (0.72, 0.78, 0.9, 1)
    }

    SubShader
    {
        // No "RenderPipeline" = "UniversalPipeline" tag — see Grayscale.shader for why:
        // it makes URP's stripper delete every variant from the AssetBundle.
        Tags { "RenderType" = "Opaque" }
        ZWrite Off
        ZTest Always
        Cull Off
        Blend Off

        Pass
        {
            Name "UltraShade"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            // XR (single-pass-instanced) blit variants. See Grayscale.shader.
            #pragma multi_compile_instancing
            #pragma multi_compile _ STEREO_INSTANCING_ON
            #pragma multi_compile _ DISABLE_TEXTURE2D_X_ARRAY
            #pragma multi_compile _ BLIT_SINGLE_SLICE

            // URP Core MUST come first: it sets up the XR texture macros (TEXTURE2D_X /
            // TextureXR.hlsl) and stereo state (unity_StereoEyeIndex) that Blit.hlsl needs.
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            // Depth declaration gives SampleSceneDepth / _CameraDepthTexture / _ZBufferParams.
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            // Blit.hlsl: fullscreen Vert + Varyings + _BlitTexture + sampler_LinearClamp
            // (and LinearEyeDepth, via core Common.hlsl).
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            // _BlitTexture_TexelSize (1/w, 1/h, w, h) is already declared by Blit.hlsl —
            // do NOT redeclare it (redefinition error). Just use it.

            float _Exposure;
            float _Temperature;
            float _Saturation;
            float _Contrast;
            float _Tonemap;
            float _BloomIntensity;
            float _BloomThreshold;
            float _BloomRadius;
            float _Sharpen;
            float _ChromAb;
            float _GrainStrength;
            float _VignetteStrength;
            float _FogDensity;
            half4 _FogColor;

            // The current camera image (opaque + particles + world UI) at a UV.
            float3 SampleScene(float2 uv)
            {
                return SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv).rgb;
            }

            // Narkowicz ACES filmic approximation
            float3 ACES(float3 x)
            {
                return saturate((x * (2.51 * x + 0.03)) / (x * (2.43 * x + 0.59) + 0.14));
            }

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float2 uv = input.texcoord;
                float2 texel = _BlitTexture_TexelSize.xy;
                float2 centered = uv - 0.5;

                // --- scene color, with chromatic aberration at the edges ---
                float3 color;
                [branch]
                if (_ChromAb > 0.001)
                {
                    float2 caOffset = centered * _ChromAb * 0.002;
                    color.r = SampleScene(uv + caOffset).r;
                    color.g = SampleScene(uv).g;
                    color.b = SampleScene(uv - caOffset).b;
                }
                else
                {
                    color = SampleScene(uv);
                }

                // --- sharpen (unsharp mask against the 4 neighbours) ---
                [branch]
                if (_Sharpen > 0.001)
                {
                    float3 around = SampleScene(uv + float2(texel.x, 0))
                                  + SampleScene(uv - float2(texel.x, 0))
                                  + SampleScene(uv + float2(0, texel.y))
                                  + SampleScene(uv - float2(0, texel.y));
                    color += _Sharpen * (color - around * 0.25);
                }

                // --- soft bloom (8-tap thresholded glow) ---
                [branch]
                if (_BloomIntensity > 0.001)
                {
                    float2 r = texel * _BloomRadius;
                    float3 glow = SampleScene(uv + float2( r.x,  0))
                                + SampleScene(uv + float2(-r.x,  0))
                                + SampleScene(uv + float2( 0,  r.y))
                                + SampleScene(uv + float2( 0, -r.y))
                                + SampleScene(uv + r * float2( 0.7,  0.7))
                                + SampleScene(uv + r * float2(-0.7,  0.7))
                                + SampleScene(uv + r * float2( 0.7, -0.7))
                                + SampleScene(uv + r * float2(-0.7, -0.7));
                    glow *= 0.125;
                    glow = max(glow - _BloomThreshold, 0.0);
                    color += glow * _BloomIntensity;
                }

                // --- depth-based atmospheric fog (the depth buffer is the one
                //     piece of real 3D data a screen-space shader gets) ---
                [branch]
                if (_FogDensity > 0.0001)
                {
                    float raw = SampleSceneDepth(uv);
                #if UNITY_REVERSED_Z
                    bool isSky = raw < 0.0001;
                #else
                    bool isSky = raw > 0.9999;
                #endif
                    if (!isSky)
                    {
                        float depth = LinearEyeDepth(raw, _ZBufferParams);
                        float fog = 1.0 - exp(-_FogDensity * depth);
                        color = lerp(color, _FogColor.rgb, fog);
                    }
                }

                // --- grading chain ---
                color *= _Exposure;
                color *= float3(1.0 + _Temperature * 0.1, 1.0, 1.0 - _Temperature * 0.1);

                if (_Tonemap > 0.5)
                    color = ACES(color);
                else
                    color = saturate(color);

                float luma = dot(color, float3(0.2126, 0.7152, 0.0722));
                color = lerp(luma.xxx, color, _Saturation);
                color = (color - 0.5) * _Contrast + 0.5;

                // --- vignette ---
                float dist = length(centered) * 2.0;
                color *= 1.0 - _VignetteStrength * smoothstep(0.6, 1.4, dist);

                // --- film grain ---
                float noise = frac(sin(dot(uv * (frac(_Time.y) + 1.0), float2(12.9898, 78.233))) * 43758.5453) - 0.5;
                color += noise * _GrainStrength;

                return half4(saturate(color), 1.0);
            }
            ENDHLSL
        }
    }
}
