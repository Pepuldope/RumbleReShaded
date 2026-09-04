# How RumbleReShaded works

A technical tour of the mod, for the curious and for anyone maintaining it. If you
just want to *use* the mod or *make* packs, see the [README](../README.md) and the
[authoring guide](../ShaderCreation/AUTHORING_GUIDE.md).

## The big picture

RumbleReShaded turns community shader packs into a **full-screen post-processing
effect** on the RUMBLE camera. The hard part is that RUMBLE is:

- **Il2Cpp** — the game's C# is ahead-of-time compiled to native code, so we work
  through MelonLoader + Il2CppInterop and can't just `new` up engine types normally.
- **URP 17 / Unity 6** — Universal Render Pipeline with **RenderGraph** on by default.
- **VR, single-pass-instanced** — the scene is rendered once into a 2-slice texture
  *array* (one slice per eye), not twice.

The effect has to land *after* everything is drawn (so it covers particles and
transparent UI), in *both eyes*, on the buffer that's actually presented to the
headset. That last requirement is what makes the naïve approaches fail.

## Why the obvious approaches don't work (and what does)

Two dead ends we hit, documented so nobody re-treads them:

1. **A camera-parented "sphere shell" sampling `_CameraOpaqueTexture`.** Works, but
   the opaque texture is captured *before* transparents — so particles and trails are
   never affected. This was the original architecture; the whole rework exists to get
   past it.

2. **A `CommandBuffer` blit in `RenderPipelineManager.endCameraRendering` writing to
   `BuiltinRenderTextureType.CameraTarget`.** The code runs, but in URP+VR the headset
   image is presented from URP's *internal* color buffer (read by the XR plugin), not
   from that builtin target. The write lands on a backbuffer the HMD never shows —
   invisible, even a raw red-clear.

**What works:** injecting a real URP **`ScriptableRenderPass`** into the pipeline,
which runs *inside* URP's render and writes URP's own active color texture.

## The injected render pass

`RRSRenderPass : ScriptableRenderPass` is a managed subclass of an Il2Cpp engine type,
registered at startup with `ClassInjector.RegisterTypeInIl2Cpp<RRSRenderPass>()`
(using the Il2Cpp constructor pattern: an `IntPtr` ctor plus
`DerivedConstructorPointer`/`DerivedConstructorBody`).

Each frame, in `RenderPipelineManager.beginCameraRendering`, the mod enqueues the pass
on the main VR camera:

```csharp
var data = cam.GetComponent<UniversalAdditionalCameraData>();
data.scriptableRenderer.EnqueuePass(pass);
```

The camera is filtered to the real stereo view (`stereoEnabled`, no `targetTexture`)
so we skip preview/RT/UI cameras.

The pass is configured:

- `renderPassEvent = AfterRenderingTransparents` — runs after opaque **and**
  transparents, so the color we read already contains particles and world UI.
- `requiresIntermediateTexture = true` — forces URP to render into a real texture (not
  straight to the backbuffer), so there's something we can read and write.
- `ConfigureInput(ScriptableRenderPassInput.Depth)` — makes `_CameraDepthTexture`
  available to packs that use depth (e.g. UltraShade's fog).

## Inside `RecordRenderGraph`

This is the RenderGraph (Unity 6) override. For each enabled pack it does a
read-modify-write of the camera color:

```
activeColor = frameData.Get<UniversalResourceData>().activeColorTexture

// Build a SAMPLEABLE copy descriptor: same as activeColor but single-sample.
desc = GetTextureDesc(activeColor)
desc.msaaSamples = None      // you can't SAMPLE an MSAA texture, and the
desc.bindTextureMS = false   // VR view is MSAA 8x (2244x2352 Tex2DArray, 2 slices)
desc.depthBufferBits = None

for each enabled pack:
    copied = CreateTexture(desc)
    AddBlitPass(activeColor -> copied)      // material-less: resolves MSAA, XR-aware
    AddBlitPass(copied -> activeColor, packMaterial)   // the actual effect
        + builder.UseAllGlobalTextures(true)  // lets the shader read _CameraDepthTexture
```

Key points:

- **MSAA resolve.** The VR color buffer is MSAA 8×. A shader can't sample an MSAA
  texture, so we first blit (fragment copy, which resolves) into a single-sample
  `copied` texture, then sample that. This mirrors what URP's own
  `FullScreenPassRendererFeature` does.
- **Can't read and write the same texture.** Hence the copy: the pack samples
  `copied` and writes back into `activeColor`.
- **Stereo is automatic.** RenderGraph + the XR system run the blit for both eye
  slices; we don't hand-roll per-eye logic.
- **Global textures.** `UseAllGlobalTextures(true)` (via the builder returned by
  `AddBlitPass`) grants the pass access to URP globals like `_CameraDepthTexture`,
  so depth-based effects work under RenderGraph's validity checks.

## The blit convention (why pack shaders look the way they do)

`AddBlitPass` drives the pack material through Unity's **Blitter**: it binds the source
to **`_BlitTexture`** and draws a *procedural fullscreen triangle* (vertices from
`SV_VertexID`, not a mesh). So pack shaders must follow URP's blit convention:

- `#include` URP `Core.hlsl` **first** (sets up the XR texture macros and stereo
  state), then `Runtime/Utilities/Blit.hlsl` (provides `Vert`, `Varyings`,
  `_BlitTexture`, `sampler_LinearClamp`).
- Sample the scene with `SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv)`.
- Keep `UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input)` as the first line of the
  fragment shader so the correct eye slice is sampled.

The RumbleShade template's example shaders are the canonical reference for this; the
[authoring guide](../ShaderCreation/AUTHORING_GUIDE.md) spells out the full convention.

## Pack discovery & UIFramework

- On startup the mod scans `UserData/RumbleReShaded/*/manifest.json`, builds a `ShaderPack`
  for each (name, bundle file, material name, parameters, render queue). Folders without a
  `manifest.json` are skipped, so it coexists with other mods' `UserData` folders.
- AssetBundles are loaded **lazily** on first enable (`LoadFromFile`, with a
  stream-based fallback), so disabled packs cost nothing.
- Settings are built dynamically as **MelonPreferences** categories and registered with
  **UIFramework** (`UI.RegisterMelon(this, categories)`): one category for the master
  toggle + a *Reload packs* button, then one category per pack holding its on/off toggle
  and a `SliderDescriptor`-backed slider (min/max from the manifest) per parameter.
- Parameter values are pushed into the material with `Material.SetFloat` **every frame**
  inside the render pass (clamped to each parameter's min/max), so dragging a slider
  updates the look live — no save callback needed. The **Reload packs** button unloads
  every bundle so they re-read from disk on the next frame — a live edit loop without
  restarting the game.

## Why AssetBundles (and the exact Unity version)

Il2Cpp Unity builds don't ship the shader compiler, so shaders can't be compiled at
runtime — packs must ship **pre-compiled AssetBundles**. AssetBundles are also not
compatible across Unity's 2025 bundle-format change, so a pack must be built with the
*exact* editor version the game runs (currently `6000.3.0f1`). The RumbleShade
template handles the build; see the [authoring guide](../ShaderCreation/AUTHORING_GUIDE.md)
for the details and pitfalls.

## No Harmony patches

There are zero Harmony patches. Scene/camera changes are handled by enqueuing the pass
every frame in `beginCameraRendering`; pack reloads are driven by the UIFramework button.
