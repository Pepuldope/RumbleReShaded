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
