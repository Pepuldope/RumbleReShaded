# RumbleShade — make shaders for RUMBLE

RumbleShade is the Unity template project for building **shader packs** for RUMBLE.
You write a fragment shader, build it into an AssetBundle here, drop the output into
the game, and the [**RumbleReShaded**](../README.md) mod applies it as a
full-screen post effect on your view — in VR, covering everything including particles.

> In a hurry / not a shader programmer? You can describe the look you want in plain
> English and have Claude write the shader for you — see
> [MAKING_SHADERS_WITH_CLAUDE.md](MAKING_SHADERS_WITH_CLAUDE.md).

This guide is the hand-written reference: how the project is set up, how to author a
shader, and the gotchas that will otherwise eat an afternoon.

---

## Why a Unity project at all?

RUMBLE is an Il2Cpp build — it ships no shader compiler, so shaders can't be compiled
at runtime. Packs must ship as **pre-compiled AssetBundles**, and Unity builds those.
That's all this project does.

## Unity version — must match the game exactly

**Build with the *exact* Unity version the installed game runs — not "newer."**
AssetBundles aren't compatible across Unity's 2025 bundle-format change: a bundle built
by an editor on the wrong side of that change is refused at load (you'll see "Unity
refused the bundle" / a magenta or missing effect). Patch level matters.

Find the game's version:

```powershell
(Get-Item 'C:\Program Files (x86)\Steam\steamapps\common\RUMBLE\UnityPlayer.dll').VersionInfo.ProductVersion
```

Install that exact editor in Unity Hub. **At time of writing the game runs `6000.3.0f1`.**
A Steam update can change it — re-check `UnityPlayer.dll` whenever packs suddenly stop
loading.

## Project layout

```
Assets/
  Shaders/
    Examples/
      Grayscale.shader      Minimal reference — start here
    UltraShade.shader       Full-featured reference — everything at once
  Materials/                Pack materials (M_<Name>.mat)
  PackSource/               One folder per pack, each with a manifest.json
  Editor/
    ShaderPackBuilder.cs    The "Build Shader Packs" menu
Build/                      Output — copy each folder into the game's UserData/RumbleShade/
```

## One-time project setup (required for VR)

**The project must have VR/stereo enabled or your effect renders in ONE EYE only.**
The game uses single-pass-instanced stereo; without a VR target, Unity strips the
stereo shader variant at build time and you get a mono shader.

- Package Manager → install **`com.unity.xr.mock-hmd`**
- Project Settings → **XR Plug-in Management** → Standalone → enable **Unity Mock HMD**
- Set the Mock HMD **Render Mode = Single Pass Instanced**

(Mock HMD is the lightest option; OpenXR/Oculus also work.)

Also leave **Strip Unused Variants** off for safety: Project Settings → Graphics → URP
Global Settings → Shader Stripping → uncheck *Strip Unused Variants*
(`m_StripUnusedVariants: 0`).

## Build process

Menu bar → **RumbleShade → Build Shader Packs** → outputs to `Build/<PackName>/`.
Copy each output folder into `RUMBLE/UserData/RumbleShade/`. In-game: ModUI →
RumbleReShaded → flip **Reload packs** and save (no restart needed).

**Verify a good build** (this catches almost every "nothing renders" problem): open
`%LOCALAPPDATA%\Unity\Editor\Editor.log` after building and check that there are **no
`Shader error` lines** and that the shader reports **`total internal programs: > 0`**.
A stripped/failed shader shows `total internal programs: 0`.

---

## How a pack shader is structured (the blit convention)

RumbleReShaded applies your shader through Unity's **Blitter** as a full-screen pass.
That dictates the shape of the shader. The minimal **Grayscale** example is the
template — here it is, annotated:

```hlsl
Shader "RumbleShade/Examples/Grayscale"
{
    Properties
    {
        // Anything you want adjustable in-game must ALSO be listed in manifest.json.
        _Strength ("Strength", Range(0, 1)) = 1
    }

    SubShader
    {
        // NO "RenderPipeline" = "UniversalPipeline" tag — see gotchas below.
        Tags { "RenderType" = "Opaque" }
        ZWrite Off ZTest Always Cull Off Blend Off

        Pass
        {
            Name "Grayscale"

            HLSLPROGRAM
            #pragma vertex Vert      // provided by Blit.hlsl
            #pragma fragment Frag

            // XR (single-pass-instanced) blit variants — keep all four.
            #pragma multi_compile_instancing
            #pragma multi_compile _ STEREO_INSTANCING_ON
            #pragma multi_compile _ DISABLE_TEXTURE2D_X_ARRAY
            #pragma multi_compile _ BLIT_SINGLE_SLICE

            // ORDER MATTERS. URP Core first (sets up TEXTURE2D_X / stereo),
            // THEN core Blit.hlsl (gives Vert, Varyings, _BlitTexture, sampler_LinearClamp).
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            float _Strength;

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);   // MUST be first

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
```

The non-negotiable parts:

| Rule | Why |
|---|---|
| `#include` URP `Core.hlsl` **before** `Runtime/Utilities/Blit.hlsl` | Blit.hlsl uses `TEXTURE2D_X` / `unity_StereoEyeIndex`, which URP Core sets up. Wrong order → `unrecognized identifier 'TEXTURE2D_X'`. |
| Sample **`_BlitTexture`** with `SAMPLE_TEXTURE2D_X(..., sampler_LinearClamp, input.texcoord)` | That's where the mod binds the live frame. (`_MainTex` / `_CameraOpaqueTexture` are *not* used by this pipeline.) |
| `#pragma vertex Vert` | Use Blit.hlsl's fullscreen-triangle vertex shader — don't write your own. |
| `UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);` first line of `Frag` | Selects the correct eye slice. Omit it → one eye wrong. |
| Keep the four `#pragma multi_compile` lines | They emit the XR blit variants. |
| **No** `UniversalPipeline` SubShader tag | With it, URP's stripper deletes every variant → empty "unsupported" shader. |

## Reading depth (optional)

The depth buffer is the one piece of real 3D info a screen-space shader gets — good for
fog and outlines. Add the depth header **after** URP Core, then sample:

```hlsl
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
#include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
...
float raw = SampleSceneDepth(input.texcoord);
float dist = LinearEyeDepth(raw, _ZBufferParams);  // meters from camera
```

The mod automatically makes `_CameraDepthTexture` available, so this just works.
See **UltraShade** for a complete fog example.

## Neighbour taps (blur, sharpen, outlines)

`_BlitTexture_TexelSize` (`xy` = 1/width, 1/height) is **already declared by Blit.hlsl**
— use it, don't redeclare it (redeclaring is a compile error). Example:

```hlsl
float2 texel = _BlitTexture_TexelSize.xy;
float3 right = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, input.texcoord + float2(texel.x, 0)).rgb;
```

## Creating a new pack — step by step

1. **Duplicate** `Grayscale.shader`, rename the file and the `Shader "RumbleShade/..."`
   declaration line. Edit the `Frag` function to taste.
2. **Material:** right-click the shader → Create → Material → save as
   `Assets/Materials/M_<Name>.mat`.
3. **Pack folder:** duplicate a folder in `Assets/PackSource/`, rename it, edit its
   `manifest.json` (see schema below). Every adjustable shader property must be both a
   `Properties { }` entry in the shader *and* a `parameters` entry in the manifest.
4. **Build Shader Packs**, copy `Build/<Name>/` into `UserData/RumbleShade/`, reload.

## Manifest schema

```json
{
    "name": "PackName",
    "author": "YourName",
    "version": "1.0.0",
    "bundle": "packname.bundle",
    "material": "M_MaterialName",
    "priority": 0,
    "queue": 0,
    "parameters": [
        { "property": "_Strength", "label": "Strength", "default": 1.0, "min": 0.0, "max": 1.0 }
    ]
}
```

- `name` — must be unique across installed packs; shown as the ModUI toggle.
- `bundle` / `material` — must match the built bundle file and the material asset name.
- `property` — must match a `float` property in the shader; `label` is the slider name.
- `priority` / `queue` — draw order when stacking packs (lower applies first). Leave 0
  unless you're layering multiple packs.

## Performance

Your shader runs on **every pixel, twice (both eyes), at 90 fps**. Be frugal:

- Gate expensive features so a zeroed slider costs nothing:
  `[branch] if (_Bloom > 0.001) { ...taps... }`
- Minimise texture taps. UltraShade's worst case (~15 taps) is already heavy.

## What screen-space shaders can / cannot do

**Can:** color grading, tonemapping, bloom/glow, vignette, CRT/pixelation, chromatic
aberration, film grain, distortion, depth fog, depth-based outlines.

**Cannot:** true ray tracing (no scene geometry, no DXR, and VR framerate forbids it),
or anything needing 3D data beyond the color + depth buffers.

**Stacking:** two packs that both fully re-color the scene won't "blend" — the later
one wins. The clean pattern is one grading pack + any number of additive overlay packs
(grain, vignette, scanlines).

## Troubleshooting

| Symptom | Cause / fix |
|---|---|
| `Couldn't open include file '...Blit.hlsl'` | Wrong path. It's `Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl` (NOT under `ShaderLibrary/`). |
| `unrecognized identifier 'TEXTURE2D_X'` / `undeclared 'unity_StereoEyeIndex'` | Include URP `Core.hlsl` **before** `Blit.hlsl`. |
| `redefinition of '_BlitTexture_TexelSize'` | It's already declared by Blit.hlsl — don't redeclare it. |
| Pack loads but mod log says shader **"unsupported"**, nothing renders | Variants were stripped or compile failed → `total internal programs: 0` in Editor.log. Remove any `UniversalPipeline` tag, keep *Strip Unused Variants* off, fix any `Shader error`. |
| "Unity refused the bundle" / Player.log "not compatible with newer Unity runtime" | Editor version ≠ game runtime (check `UnityPlayer.dll`); or `com.unity.modules.assetbundle` missing from `Packages/manifest.json`; or duplicate pack `name`. |
| Magenta screen | Shader compile failure — check the Unity Console / Editor.log. |
| One eye wrong | A stereo macro was removed, or VR isn't set up in the project (see one-time setup). |
| Effect invisible | Pack toggled off in ModUI, or the shader returns the scene unchanged at current slider values. |

---

Made something cool? Share it — packs are just folders. See the
[RumbleReShaded README](../README.md) for how players install them.
