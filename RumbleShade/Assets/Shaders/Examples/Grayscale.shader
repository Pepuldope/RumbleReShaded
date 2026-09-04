// Grayscale — the "hello world" of RumbleShade packs.
// Copy this file, rename the shader, and start experimenting.
//
// RumbleReShaded applies packs as a real URP render pass (a fullscreen blit) that
// runs AFTER the camera has drawn everything — opaque, transparents (particles),
// and world-space UI. The pass binds the current camera image as `_BlitTexture`
// (URP's standard blit source); your fragment reads it and returns the new colour.
Shader "RumbleShade/Examples/Grayscale"
{
    Properties
    {
        // Every property you want adjustable in-game must also be listed in your
        // pack's manifest.json under "parameters".
        _Strength ("Strength", Range(0, 1)) = 1
    }

    SubShader
    {
        // No "RenderPipeline" = "UniversalPipeline" tag — with it, URP's scriptable
        // shader stripper deletes every variant from the AssetBundle (the pack isn't
        // referenced by any built scene), leaving an empty shader that renders
        // nothing. Without the tag the variants survive and the blit still works.
        Tags { "RenderType" = "Opaque" }
        ZWrite Off
        ZTest Always
        Cull Off
        Blend Off

        Pass
        {
            Name "Grayscale"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            // XR (single-pass-instanced) blit variants — keep all four so the eye
            // texture array is sampled correctly in VR.
            #pragma multi_compile_instancing
            #pragma multi_compile _ STEREO_INSTANCING_ON
            #pragma multi_compile _ DISABLE_TEXTURE2D_X_ARRAY
            #pragma multi_compile _ BLIT_SINGLE_SLICE

            // URP Core MUST come first: it sets up the XR texture macros (TEXTURE2D_X
            // lives in TextureXR.hlsl) and the single-pass-instanced stereo state
            // (unity_StereoEyeIndex) that Blit.hlsl relies on.
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            // Blit.hlsl provides the fullscreen-triangle vertex shader `Vert`, the
            // `Varyings` struct (with .texcoord and stereo output), `_BlitTexture`
            // (TEXTURE2D_X — stereo-array aware) and `sampler_LinearClamp`.
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            float _Strength;

            half4 Frag(Varyings input) : SV_Target
            {
                // Must be the first line in VR fragment shaders.
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                // The full rendered frame (opaque + particles + world UI) at this pixel.
                float3 scene = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, input.texcoord).rgb;

                float gray = dot(scene, float3(0.2126, 0.7152, 0.0722));
                float3 color = lerp(scene, gray.xxx, _Strength);

                return half4(color, 1.0);
            }
            ENDHLSL
        }
    }
}
