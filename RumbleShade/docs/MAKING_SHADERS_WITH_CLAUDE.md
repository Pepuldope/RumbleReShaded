# Making RUMBLE shaders with Claude

You don't have to know HLSL to make a shader pack. You can describe the look you want
in plain English and have an AI (Claude, ChatGPT, etc.) write the shader for you, then
just build it. This guide gives you a **copy-paste prompt** that already contains all
the rules RumbleShade shaders must follow, so what you get back actually compiles and
runs in VR.

## The workflow

1. **Describe the look** you want (see the prompt below).
2. **Paste the prompt** into Claude, filling in the two blanks: your desired style, and
   the sliders you want.
3. Claude gives you a `.shader` file and a matching `manifest.json`.
4. **Drop them in** the RumbleShade project:
   - shader → `Assets/Shaders/<Name>.shader`
   - create a material from it → `Assets/Materials/M_<Name>.mat`
   - new folder `Assets/PackSource/<Name>/` with the `manifest.json`
5. **RumbleShade → Build Shader Packs**, copy `Build/<Name>/` into
   `UserData/RumbleReShaded/`, and flip **Reload packs** in the UIFramework menu.
6. **Iterate:** if something looks off, tell Claude what you see ("too dark", "fog is
   too strong", "I want the vignette tighter") and rebuild.

> Tip: paste the existing `Grayscale.shader` and `UltraShade.shader` into the chat too,
> as concrete examples. The more reference Claude has, the better the result.

---

## The prompt (copy this)

Replace the **two bracketed sections** with what you want, then send the whole thing.

```
You are writing a post-processing shader for "RumbleShade", a system that applies
full-screen shaders to the VR game RUMBLE. I will build your shader into a Unity
AssetBundle. Follow these rules EXACTLY or it will not compile / will break VR.

THE LOOK I WANT:
[ Describe the visual style in plain language. Be specific about mood, colors,
  references. Examples: "a warm cinematic film look with gentle bloom and a soft
  vignette", "a cold horror feel — desaturated, heavy grain, dark edges", "a retro
  CRT screen with scanlines and slight curvature", "vaporwave: push magenta/cyan,
  high contrast, subtle chromatic aberration". ]

THE SLIDERS I WANT (adjustable in-game):
[ List the parameters you want to tweak live, e.g. "Strength 0..1 default 1",
  "Bloom 0..2 default 0.4", "Grain 0..0.2 default 0.05". If unsure, just say
  "pick sensible sliders for this look". ]

HARD REQUIREMENTS — the shader MUST:
- Be a single Shader with one SubShader, one Pass.
- NOT include a "RenderPipeline" = "UniversalPipeline" SubShader tag (it makes Unity
  strip the shader to nothing). Use Tags { "RenderType" = "Opaque" } and
  ZWrite Off, ZTest Always, Cull Off, Blend Off.
- Use these pragmas:
    #pragma vertex Vert
    #pragma fragment Frag
    #pragma multi_compile_instancing
    #pragma multi_compile _ STEREO_INSTANCING_ON
    #pragma multi_compile _ DISABLE_TEXTURE2D_X_ARRAY
    #pragma multi_compile _ BLIT_SINGLE_SLICE
- Include, IN THIS ORDER (order is critical):
    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
    // add the next line ONLY if you sample depth:
    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
    #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
- Read the current frame from _BlitTexture, which is already declared by Blit.hlsl:
    float3 scene = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, input.texcoord).rgb;
  Do NOT use _MainTex or _CameraOpaqueTexture.
- Use Blit.hlsl's provided `Vert` and `Varyings` (with input.texcoord). Do NOT write
  your own vertex shader.
- Make UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input); the FIRST line of Frag.
- For neighbour taps, use _BlitTexture_TexelSize.xy (already declared by Blit.hlsl —
  do NOT redeclare it).
- For depth (only if needed): float raw = SampleSceneDepth(input.texcoord);
  float dist = LinearEyeDepth(raw, _ZBufferParams);  // meters
- Declare each adjustable property in Properties { } AND as a plain `float _Name;`
  uniform, and gate expensive effects with [branch] if (_Param > 0.001) { } for perf
  (runs per pixel, twice for VR, at 90fps).
- Return half4(saturate(color), 1.0);

THEN ALSO GIVE ME a matching manifest.json in this exact schema, with one entry in
"parameters" for every slider (property must match the shader property name):
{
  "name": "<PackName>",
  "author": "<me>",
  "version": "1.0.0",
  "bundle": "<packname>.bundle",
  "material": "M_<PackName>",
  "priority": 0,
  "queue": 0,
  "parameters": [
    { "property": "_Strength", "label": "Strength", "default": 1.0, "min": 0.0, "max": 1.0 }
  ]
}

Output two code blocks: the .shader file, then the manifest.json. Keep the shader
commented so I can tweak it by hand later.
```

---

## A worked example (what to put in the blanks)

> **THE LOOK I WANT:** A cozy "golden hour" film look — warm highlights, slightly
> crushed blacks, gentle bloom on bright spots, and a soft dark vignette around the
> edges so the center pops. Not too strong; it should feel natural, not like an
> Instagram filter.
>
> **THE SLIDERS I WANT:** Warmth 0..1 default 0.5, Bloom 0..2 default 0.3,
> Vignette 0..1 default 0.35, Overall Strength 0..1 default 1.

Send that with the prompt and you'll get a ready-to-build warm cinematic pack.

## Tips for good results

- **Reference real things.** "Like the orange/teal look in action movies", "like an
  old VHS tape", "like Minecraft's BSL shaders at sunset" — concrete references beat
  adjectives.
- **One idea per pack.** A focused pack (just CRT, just fog) is easier to get right and
  stacks nicely with others. Ask for a kitchen-sink shader only if you want an
  UltraShade-style all-in-one.
- **Iterate by feel.** You don't need to read the code. Build it, look at it in the
  headset, then tell the AI "warmer", "less grain", "tighter vignette", "the fog
  starts too close" and rebuild.
- **If it won't compile,** paste the `Shader error ...` line from the Unity Console (or
  `Editor.log`) back to the AI — the errors are usually a missing include or a stray
  redeclaration, and it'll fix them. The main project README has a troubleshooting
  table for the common ones.

## What it can't do

No AI can give you ray tracing, real reflections, or new geometry — RUMBLE shaders only
have the rendered color image and a depth buffer to work with. Within that, though, you
can do a *lot*: grading, bloom, fog, distortion, CRT, grain, outlines, stylized color.
See the "What screen-space shaders can / cannot do" section in the
[main README](../README.md).
