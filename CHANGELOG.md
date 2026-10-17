## v1.3.0-dev.1
For mods that drive packs (additive API, `ApiVersion` unchanged):
- **`RRS.Override(modId, pack)`** - take temporary control of a pack's on/off and sliders without touching the player's settings. Returns a `PackOverride`: set `Enabled`, `Set(parameter, value)` by label or shader property, blend with `Weight` (0..1, so a fade is just animating it), and `Dispose()` to hand the pack back. Nothing is saved, so the menu keeps showing the player's own choices and a crash can't leave someone else's look in their config. Overrides from several mods stack in creation order.
- **`RRS.Packs`** - a snapshot of installed packs: id, name, author, and each parameter's range and default, with both the player's value and the value in use once overrides are blended in.
- **`RRS.Preload(pack)`** - load a pack's bundle now (during a countdown or loading screen) instead of hitching the first frame it's switched on.
- **`RRS.Enabled`** - the player's master switch.
- **Fix: a pack loaded before it was first drawn could fail for the whole session** ("Unity refused the bundle"). Unity freed the unused material, and reloading the still-loaded bundle file was refused. The material is now kept from being freed, and if it is lost anyway it is taken from the loaded bundle again.
- **`RRS.SetGlobal` names are now readable from shaders**: `"mymod.charge"` is published as `_RRS_mymod_charge` (a `.` can't appear in a shader property name). The dotted name is still set too.

## v1.2.0-dev.1
- **A failure in the render pass no longer takes the game down.** If the pass throws, the mod reports it once, switches its effects off and lets the game carry on — previously an exception inside URP's render-graph recording killed the process, and because packs default to on it could do so before you ever reached the menu.
- **It retries by itself.** After a failure the pass tries again on the next scene load, up to three times, then stays off until you restart. A one-off glitch heals with nothing to do; a real fault stops after three log entries instead of spamming.
- Each attempt logs the camera target it saw (`[diag] RRSRenderPass: activeColor …`), which is the line to include if you report a problem.
- **Packs now live in `UserData/RumbleReShaded/`** (one folder per pack) instead of loose at the top of `UserData/` — the mod has its own folder you drop packs into, like AdditionalSounds. Move your existing pack folders into `UserData/RumbleReShaded/`.
- Menu trimmed to what a player needs: **Enabled** and **Reload packs**, plus each pack's own section. The render-graph debug entries were development instruments and are gone.


## v1.1.0
- Switched the settings UI from ModUI to **UIFramework** (the current standard; ModUI is deprecated). Dependency changed accordingly.
- Each pack now gets its own collapsible section with an on/off toggle and a real min/max slider per parameter; **Reload packs** is now a proper button.
- Slider changes apply **live** while you drag (no save step). Settings now persist via MelonPreferences.

## v1.0.0
- Initial release.
- Loads community shader packs from `UserData/` and applies them as full-screen VR post-processing (runs after transparents, so it covers particles, trails and world UI in both eyes).
- Per-pack on/off toggle and live parameter sliders in ModUI.
- Ships two example packs: **Grayscale** and **UltraShade** (full cinematic stack — ACES tonemap, exposure, white balance, saturation/contrast, bloom, sharpening, chromatic aberration, film grain, vignette, depth fog).
- Includes pack-authoring guides in `ShaderCreation/` for making your own looks.
