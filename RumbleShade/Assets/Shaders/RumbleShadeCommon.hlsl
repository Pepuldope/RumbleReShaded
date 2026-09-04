// RumbleShade shared boilerplate for fullscreen overlay shaders.
//
// Include this AFTER the URP includes:
//   #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
//   #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareOpaqueTexture.hlsl"   (for SampleSceneColor)
//   #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"    (for SampleSceneDepth)
//
// The UNITY_* macros below are what make the shader work in VR (the game renders
// once per eye, and the scene textures are texture ARRAYS in VR). If you write
// your own vertex/fragment structs, keep those macros — without them you get one
// working eye and one garbage eye.

#ifndef RUMBLESHADE_COMMON_INCLUDED
#define RUMBLESHADE_COMMON_INCLUDED

struct ShadeAttributes
{
    float4 positionOS : POSITION;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct ShadeVaryings
{
    float4 positionCS : SV_POSITION;
    UNITY_VERTEX_OUTPUT_STEREO
};

// Standard vertex shader for the overlay shell. You almost never need your own.
ShadeVaryings ShadeVert(ShadeAttributes input)
{
    ShadeVaryings output;
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
    output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
    return output;
}

// Screen UV (0..1) of the current fragment. Call inside the fragment shader,
// after UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input).
float2 ShadeScreenUV(float4 positionCS)
{
    return positionCS.xy / _ScaledScreenParams.xy;
}

// Size of one pixel in UV units — for neighbour taps (blur, sharpen, outlines).
float2 ShadeTexelSize()
{
    return 1.0 / _ScaledScreenParams.xy;
}

// Linear distance from the camera in meters at this screen UV, and whether the
// pixel is skybox (no geometry). Needs DeclareDepthTexture.hlsl.
#ifdef UNITY_DECLARE_DEPTH_TEXTURE_INCLUDED
float ShadeLinearDepth(float2 uv, out bool isSky)
{
    float raw = SampleSceneDepth(uv);
#if UNITY_REVERSED_Z
    isSky = raw < 0.0001;
#else
    isSky = raw > 0.9999;
#endif
    return LinearEyeDepth(raw, _ZBufferParams);
}
#endif

#endif // RUMBLESHADE_COMMON_INCLUDED
