// SkyShade — the LAYERING PROOF pack.
//
// Purpose: prove that a pack can shade ONE CATEGORY of the scene instead of the
// whole frame, using nothing but the depth buffer. Sky is the one category that
// is free to isolate: pixels with no geometry keep the depth-clear value, so
// "depth == far plane" IS the sky mask. No mod-side changes are needed for this —
// RumbleReShaded already calls ConfigureInput(Depth) and grants the blit pass
// access to _CameraDepthTexture (the same plumbing UltraShade's fog uses).
//
// If this reads correctly in both eyes, selective/layered shading is viable and
// the only thing missing for players/structures/map is a mask SOURCE for them
// (a mask render pass on the mod side) — the compositing half is proven here.
//
// FIRST TEST: set _DebugMask = 1. You should see a pure black/white image:
// WHITE where the mask is, BLACK everywhere else. Confirm the white region is
// actually the sky and looks identical in both eyes BEFORE judging the tint.
// Then flip _Invert to 1 — the mask must exactly swap (world shaded, sky clean).
// That pair of checks is the whole experiment.
//
// KNOWN LIMIT (matters for the wider layering plan): the depth buffer is
// OPAQUE-ONLY. Particles and trails drawn in front of the sky do not write
// depth, so they classify as sky and get shaded with it. Any depth-derived mask
// inherits this. A renderingLayerMask mask pass would not.
Shader "RumbleShade/SkyShade"
{
    Properties
    {
        _Strength("Strength", Range(0, 1)) = 1
        // 0 = shade the sky only. 1 = shade everything EXCEPT the sky.
        // Flipping this is what proves the mask is real and not a coincidence.
        _Invert("Invert mask (0=sky 1=world)", Range(0, 1)) = 0
        // 1 = output the raw mask (white = shaded region). Diagnostic view.
        _DebugMask("Debug: show mask", Range(0, 1)) = 0
        _Brightness("Brightness", Range(0, 3)) = 1
        _Saturation("Saturation", Range(0, 2)) = 0.2
        _TintAmount("Tint Amount", Range(0, 1)) = 1
        // Baked colour (not slider-exposed — same as UltraShade's _FogColor).
        // Default is a deliberately unnatural orange so a working mask is
        // unmistakable on the first run.
        _Tint("Tint Colour", Color) = (1.0, 0.35, 0.12, 1)
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
            Name "SkyShade"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            // XR (single-pass-instanced) blit variants. See Grayscale.shader.
            #pragma multi_compile_instancing
            #pragma multi_compile _ STEREO_INSTANCING_ON
            #pragma multi_compile _ DISABLE_TEXTURE2D_X_ARRAY
            #pragma multi_compile _ BLIT_SINGLE_SLICE

            // Include order is load-bearing: URP Core sets up TEXTURE2D_X and the
            // stereo state that Blit.hlsl assumes already exist.
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            // Depth declaration gives SampleSceneDepth / _CameraDepthTexture / _ZBufferParams.
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            // Blit.hlsl: fullscreen Vert + Varyings + _BlitTexture + sampler_LinearClamp.
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            float _Strength;
            float _Invert;
            float _DebugMask;
            float _Brightness;
            float _Saturation;
            float _TintAmount;
            half4 _Tint;

            half4 Frag(Varyings input) : SV_Target
            {
                // Must be the first line in VR fragment shaders.
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float2 uv = input.texcoord;
                float3 scene = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv).rgb;

                // --- the category mask ---
                // No geometry wrote here, so depth is still at the clear value.
                float raw = SampleSceneDepth(uv);
            #if UNITY_REVERSED_Z
                float sky = raw < 0.0001 ? 1.0 : 0.0;
            #else
                float sky = raw > 0.9999 ? 1.0 : 0.0;
            #endif
                // _Invert selects which side of the mask we shade.
                float mask = lerp(sky, 1.0 - sky, saturate(_Invert));

                if (_DebugMask > 0.5)
                    return half4(mask, mask, mask, 1.0);

                // --- the effect, applied only where mask == 1 ---
                float3 shaded = scene * _Brightness;
                float luma = dot(shaded, float3(0.2126, 0.7152, 0.0722));
                shaded = lerp(luma.xxx, shaded, _Saturation);
                // Re-hue while keeping the region's brightness, so the tint stays
                // visible even over a dark sky (a plain multiply would vanish).
                shaded = lerp(shaded, luma.xxx * _Tint.rgb * 2.0, saturate(_TintAmount));

                float3 color = lerp(scene, shaded, mask * saturate(_Strength));
                return half4(color, 1.0);
            }
            ENDHLSL
        }
    }
}
