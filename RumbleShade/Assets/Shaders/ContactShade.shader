// ContactShade — RumbleShade SSAO / contact-shadow pack.
//
// Screen-space ambient occlusion: darkens creases, corners and the contact
// points where objects meet, which is what makes a scene read as "grounded"
// and gives the impression of soft, realistic shadowing. This is NOT ray
// tracing — there is no scene geometry available to a post-process pass — but
// it is the realistic ceiling for grounded shadow contact from depth alone.
// (See RumbleReShaded CLAUDE.md "Track A" for the real-raytracing investigation.)
//
// How it works: the only true 3D data a fullscreen shader gets is the depth
// buffer (_CameraDepthTexture, requested by RumbleReShaded via ConfigureInput).
// We reconstruct each pixel's WORLD position and a WORLD normal from depth,
// then fire a hemisphere of samples around it; samples that land behind nearer
// geometry count as occlusion. The result multiplies the camera image
// (_BlitTexture), which already includes particles + world UI.
//
// TWO PASSES (needs RumbleReShaded 1.2.0+ and "passes": 2 in the manifest):
//   Pass 0 — compute AO. Outputs the scene colour in RGB and the raw AO in ALPHA.
//   Pass 1 — depth-aware blur of that alpha, then composite.
// A random-rotated hemisphere kernel is inherently noisy; every real SSAO
// implementation blurs it afterwards. Doing it in one pass is why this looked
// grainy before (Peter, 2026-08-04). The blur is depth-aware so it smooths the
// noise without bleeding occlusion across silhouette edges.
//
// Runs as a real URP fullscreen blit AFTER transparents (same convention as
// UltraShade). All reconstruction uses URP's convention-safe helpers
// (ComputeWorldSpacePosition / ComputeNormalizedDeviceCoordinates) and the
// stereo matrices, so it is correct per-eye in VR.
//
// KNOWN LIMIT, not a bug: AO only appears where the occluding geometry is
// on-screen AND in the depth buffer. Looking UP at an object against sky gives
// no occlusion, because sky pixels are skipped — there is nothing to occlude
// with. Looking DOWN, the ground behind the object supplies the occluders.
// No screen-space AO escapes this.
Shader "RumbleShade/ContactShade"
{
    Properties
    {
        _Intensity("AO Intensity", Range(0, 4)) = 1.6
        _Radius("Sample Radius (m)", Range(0.01, 3)) = 0.6
        _Power("AO Contrast", Range(0.5, 4)) = 1.5
        _Bias("Depth Bias (m)", Range(0, 0.2)) = 0.025
        _MaxDistance("Fade Distance (m)", Range(1, 100)) = 25
        // Blur width in pixels. 0 disables the blur and gives you the old grainy
        // look back, which is useful for judging whether the blur is doing its job.
        _BlurSize("Blur Size (px)", Range(0, 4)) = 2
        _DebugView("Debug: show AO (0/1)", Range(0, 1)) = 0
        // Not slider-exposed (Color) — tint applied in occluded areas. Black = plain darkening.
        _AOColor("AO Tint", Color) = (0, 0, 0, 1)
    }

    SubShader
    {
        // No "RenderPipeline" = "UniversalPipeline" tag — URP's stripper would
        // delete every variant from the AssetBundle (see RumbleShade README).
        Tags { "RenderType" = "Opaque" }
        ZWrite Off
        ZTest Always
        Cull Off
        Blend Off

        // ------------------------------------------------------------------
        // PASS 0 — compute AO. RGB = untouched scene colour, A = raw AO.
        // ------------------------------------------------------------------
        Pass
        {
            Name "ContactShadeAO"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            // XR (single-pass-instanced) blit variants. See UltraShade.shader.
            #pragma multi_compile_instancing
            #pragma multi_compile _ STEREO_INSTANCING_ON
            #pragma multi_compile _ DISABLE_TEXTURE2D_X_ARRAY
            #pragma multi_compile _ BLIT_SINGLE_SLICE

            // URP Core MUST come first (sets up TEXTURE2D_X / stereo state /
            // the matrices and reconstruction helpers we use below).
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            // Gives SampleSceneDepth / _CameraDepthTexture / _ZBufferParams.
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            // Blit.hlsl: fullscreen Vert + Varyings + _BlitTexture + sampler_LinearClamp
            // (+ _BlitTexture_TexelSize, already declared — do not redeclare).
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            #define AO_SAMPLES 24

            float _Intensity;
            float _Radius;
            float _Power;
            float _Bias;
            float _MaxDistance;

            float RawDepth(float2 uv)
            {
                return SampleSceneDepth(uv);
            }

            bool IsSky(float raw)
            {
            #if UNITY_REVERSED_Z
                return raw <= 0.0;
            #else
                return raw >= 1.0;
            #endif
            }

            // World-space position at a screen UV from its raw depth.
            // ComputeWorldSpacePosition handles the platform/Y conventions, and
            // UNITY_MATRIX_I_VP is the per-eye inverse VP under stereo instancing.
            float3 WorldPosAt(float2 uv, float raw)
            {
                return ComputeWorldSpacePosition(uv, raw, UNITY_MATRIX_I_VP);
            }

            // Cheap per-pixel hash for kernel rotation (static → no VR shimmer).
            float Hash(float2 p)
            {
                return frac(sin(dot(p, float2(12.9898, 78.233))) * 43758.5453);
            }

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float2 uv = input.texcoord;
                float3 color = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv).rgb;

                float centerRaw = RawDepth(uv);
                // Sky: no geometry, no AO. Alpha 0 so the blur pass leaves it alone.
                if (IsSky(centerRaw))
                    return half4(color, 0.0);

                float2 texel = _BlitTexture_TexelSize.xy;
                float cEye = LinearEyeDepth(centerRaw, _ZBufferParams);
                float3 P = WorldPosAt(uv, centerRaw);

                // --- reconstruct a world normal from the closer depth neighbours
                //     (closer pair → fewer fat edges on silhouettes) ---
                float2 uvR = uv + float2(texel.x, 0), uvL = uv - float2(texel.x, 0);
                float2 uvU = uv + float2(0, texel.y), uvD = uv - float2(0, texel.y);
                float rR = RawDepth(uvR), rL = RawDepth(uvL), rU = RawDepth(uvU), rD = RawDepth(uvD);
                float3 Pr = WorldPosAt(uvR, rR), Pl = WorldPosAt(uvL, rL);
                float3 Pu = WorldPosAt(uvU, rU), Pd = WorldPosAt(uvD, rD);

                float eR = LinearEyeDepth(rR, _ZBufferParams), eL = LinearEyeDepth(rL, _ZBufferParams);
                float eU = LinearEyeDepth(rU, _ZBufferParams), eD = LinearEyeDepth(rD, _ZBufferParams);
                float3 dpdx = (abs(eR - cEye) < abs(cEye - eL)) ? (Pr - P) : (P - Pl);
                float3 dpdy = (abs(eU - cEye) < abs(cEye - eD)) ? (Pu - P) : (P - Pd);

                float3 N = normalize(cross(dpdy, dpdx));
                float3 V = normalize(_WorldSpaceCameraPos - P);
                if (dot(N, V) < 0.0) N = -N;   // face the camera

                // --- TBN with a per-pixel random rotation ---
                float rnd = Hash(uv * _BlitTexture_TexelSize.zw);
                float a = rnd * 6.2831853;
                float3 randVec = float3(cos(a), sin(a), 0);
                float3 T = normalize(randVec - N * dot(randVec, N));
                float3 B = cross(N, T);

                // Cap how much of the SCREEN the kernel can cover.
                //
                // _Radius is in metres, so its screen footprint grows without bound as
                // geometry gets closer: standing over the ground, 0.6 m spans a huge
                // part of the view, the 24 samples spread thin, and the discrete radii
                // of the golden-spiral kernel become visible as concentric rings —
                // centred on the nearest ground, i.e. around your own feet, which is
                // exactly the "halo around your shadow" Peter reported (2026-08-04).
                // Tying the radius to depth keeps the screen footprint roughly fixed.
                float radius = min(_Radius, cEye * 0.15);

                // --- hemisphere occlusion sampling (golden-spiral kernel) ---
                float occlusion = 0.0;
                [loop]
                for (int i = 0; i < AO_SAMPLES; i++)
                {
                    float k = (i + 0.5) / AO_SAMPLES;
                    float phi = i * 2.3999632 + a;          // golden angle + rotation
                    float sinT = sqrt(k);
                    float cosT = sqrt(1.0 - k);
                    float3 dirTS = float3(cos(phi) * sinT, sin(phi) * sinT, cosT);
                    float scale = lerp(0.1, 1.0, k * k);     // cluster samples near the centre
                    float3 sampleWS = P + (T * dirTS.x + B * dirTS.y + N * dirTS.z) * radius * scale;

                    float2 sUV = ComputeNormalizedDeviceCoordinates(sampleWS, UNITY_MATRIX_VP);
                    if (sUV.x < 0.0 || sUV.x > 1.0 || sUV.y < 0.0 || sUV.y > 1.0) continue;

                    float sRaw = RawDepth(sUV);
                    if (IsSky(sRaw)) continue;

                    float sceneEye = LinearEyeDepth(sRaw, _ZBufferParams);
                    float sampleEye = -TransformWorldToView(sampleWS).z;   // +z away from camera

                    // Reject in WORLD space, not depth space.
                    //
                    // Two earlier attempts at this were both too weak. First
                    // smoothstep(0, 1, _Radius / |cEye - sceneEye|) — a reciprocal that
                    // never reaches zero, so a hand 0.3 m away against a 5 m wall still
                    // scored ~0.13. Then a depth-delta smoothstep, which STILL left a
                    // halo (Peter, 2026-08-04), because "close in depth" is not the same
                    // question as "close in space".
                    //
                    // What actually matters: where is the geometry we hit, relative to
                    // THIS pixel? Reconstruct its world position and ask two things — is
                    // it within the sampling radius, and is it above this surface's
                    // horizon. Geometry behind the surface plane cannot occlude it, and
                    // geometry metres away belongs to a different surface entirely.
                    // Together these are what kills the halo around a near object.
                    float3 occluderWS = WorldPosAt(sUV, sRaw);
                    float3 toOccluder = occluderWS - P;
                    float occluderDist = length(toOccluder);
                    if (occluderDist > radius * 2.0) continue;

                    // Below the hemisphere horizon → cannot occlude this surface.
                    float ndot = dot(toOccluder / max(occluderDist, 1e-5), N);
                    if (ndot < 0.15) continue;

                    float rangeCheck = 1.0 - smoothstep(radius, radius * 2.0, occluderDist);

                    occlusion += (sceneEye <= sampleEye - _Bias ? 1.0 : 0.0) * rangeCheck;
                }
                occlusion /= AO_SAMPLES;

                // fade AO out with distance so the far skybox/arena edges stay clean
                occlusion *= 1.0 - smoothstep(_MaxDistance * 0.5, _MaxDistance, cEye);

                float ao = saturate(pow(saturate(occlusion), _Power) * _Intensity);

                // Scene colour untouched in RGB; AO handed to pass 1 in alpha. The
                // mod's intermediate buffer is RGBA half specifically so this fits.
                return half4(color, ao);
            }
            ENDHLSL
        }

        // ------------------------------------------------------------------
        // PASS 1 — depth-aware blur of the AO in alpha, then composite.
        // ------------------------------------------------------------------
        Pass
        {
            Name "ContactShadeBlur"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing
            #pragma multi_compile _ STEREO_INSTANCING_ON
            #pragma multi_compile _ DISABLE_TEXTURE2D_X_ARRAY
            #pragma multi_compile _ BLIT_SINGLE_SLICE

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            float _BlurSize;
            float _DebugView;
            half4 _AOColor;

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float2 uv = input.texcoord;

                // _BlitTexture is now pass 0's output: RGB = scene, A = raw AO.
                half4 center = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv);
                float3 color = center.rgb;

                float ao = center.a;

                if (_BlurSize > 0.0)
                {
                    float centerEye = LinearEyeDepth(SampleSceneDepth(uv), _ZBufferParams);
                    float2 texel = _BlitTexture_TexelSize.xy * _BlurSize;

                    float sum = 0.0;
                    float weightSum = 0.0;

                    // 4x4 box, depth-weighted. Matches the kernel size the 24-sample
                    // rotated hemisphere needs to average out; a plain 3x3 leaves
                    // visible structure behind.
                    [unroll]
                    for (int y = -2; y <= 1; y++)
                    {
                        [unroll]
                        for (int x = -2; x <= 1; x++)
                        {
                            float2 sUV = uv + float2(x, y) * texel;
                            half4 s = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, sUV);

                            // Depth-aware: a neighbour on the far side of a silhouette
                            // must not bleed its occlusion across the edge. Without
                            // this, the blur turns a crisp contact shadow into a smear
                            // and reintroduces halos — the exact artifact we just fixed
                            // in pass 0.
                            float sEye = LinearEyeDepth(SampleSceneDepth(sUV), _ZBufferParams);
                            float w = saturate(1.0 - abs(sEye - centerEye) * 4.0);

                            sum += s.a * w;
                            weightSum += w;
                        }
                    }

                    // weightSum is never 0 — the centre tap always weighs 1.
                    ao = sum / max(weightSum, 1e-4);
                }

                if (_DebugView > 0.5)
                    return half4((1.0 - ao).xxx, 1.0);   // white = open, dark = occluded

                color = lerp(color, color * _AOColor.rgb, ao);
                return half4(color, 1.0);
            }
            ENDHLSL
        }
    }
}
