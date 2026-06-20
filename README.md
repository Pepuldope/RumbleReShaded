# RumbleReShaded

**Shaders for RUMBLE.** A MelonLoader mod that loads community-made **shader packs** and
applies them as full-screen post-processing on your view — color grading, bloom, film
grain, fog, CRT looks, grayscale, and whatever else people dream up. Think *Minecraft
shaders, but for RUMBLE*: drop a pack in a folder, toggle it on, and the whole game gets
a new look.

Every effect is applied **after the game finishes drawing the frame**, so it covers
*everything* you see — the arena, your hands, particles, trails and world-space UI — in
both eyes, with full VR support.

## Requirements

- **RUMBLE** (PC / Steam)
- **MelonLoader**
- **ModUI** (`Baumritter-RumbleModUI`) — RumbleReShaded registers all its toggles and
  sliders there. The mod manager installs it automatically as a dependency.

## Install

Install with your mod manager (r2modman / Thunderstore Mod Manager) and it drops
everything in the right place. Manual install:

- `Mods/RumbleReShaded.dll` → `RUMBLE/Mods/`
- `UserData/*` → `RUMBLE/UserData/` (the example pack folders)

Launch the game, open **ModUI → RumbleReShaded**, and toggle a pack on. Every pack gets
its own on/off switch plus a live slider for each parameter it exposes. Changes apply as
soon as you save.

## Included packs

Two example packs ship in `UserData/` — both genuinely usable, not just demos:

- **Grayscale** — one *Strength* slider, desaturates the whole view. The "hello world" of
  RumbleShade.
- **UltraShade** — a full cinematic grading stack: ACES tonemapping, exposure, white
  balance, saturation/contrast, soft bloom, sharpening, chromatic aberration, film grain,
  vignette, and depth-based atmospheric fog. Every stage has its own slider.

You can run several packs at once — they apply in order (lowest render queue first), each
processing the result of the previous, so an overlay pack (grain, vignette) layers cleanly
on top of a grading pack.

## Make your own packs

RumbleReShaded is really just a **host** for community packs — the fun part is making
them. A pack is a small fragment shader built into a Unity AssetBundle, dropped into a
folder. You don't even need to know HLSL: you can describe the look you want in plain
English and have an AI write the shader, then build it.

See the **[`ShaderCreation/`](ShaderCreation/)** folder included here:

- **[MAKING_SHADERS_WITH_CLAUDE.md](ShaderCreation/MAKING_SHADERS_WITH_CLAUDE.md)** — the
  AI workflow + a ready-to-paste prompt that already encodes every rule a pack shader
  must follow.
- **[AUTHORING_GUIDE.md](ShaderCreation/AUTHORING_GUIDE.md)** — the full authoring guide:
  what screen-space shaders can and can't do, the shader convention, the build process,
  and a troubleshooting table.

The packs are built from the companion **RumbleShade** Unity template project.

## Troubleshooting

| Symptom | Likely cause |
|---|---|
| Pack doesn't appear in ModUI | `manifest.json` missing or has a JSON typo; or two packs share the same `name`. |
| Pack toggles on but nothing changes | The shader output equals the input at current slider values, or the bundle was built wrong — see the authoring guide. |
| Magenta screen | Shader failed to compile/load — a pack must be built with the exact Unity version the game runs. |
| Nothing works at all | Make sure **Enabled** is on, and that MelonLoader + ModUI loaded (`MelonLoader/Latest.log`). |

## Credits

Mod by **Pepuldo**. Built on MelonLoader, Il2CppInterop and ModUI.
