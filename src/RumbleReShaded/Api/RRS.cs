using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace RumbleReShaded.Api
{
    // PUBLIC API — other people's mods are the intended callers, not just this one.
    //
    // Once a DLL ships with this surface, renaming anything here is a breaking change
    // for every consumer. Settle names BEFORE the first published build (see
    // docs/TRIGGER_API.md §2); after that, only additive changes.
    //
    // Consumers should add [assembly: MelonAdditionalDependencies("RumbleReShaded")]
    // for load order — the same pattern this mod uses for UIFramework.
    //
    // Every method here is safe to call before RumbleReShaded has finished
    // initialising and safe to call every frame. A consumer mod with an unlucky load
    // order must never NRE inside someone else's mod.
    //
    // Two different behaviours before init, on purpose:
    //   Fire/SetGlobal/Play  no-op (they describe a MOMENT — a dropped one is correct)
    //   RegisterPack         QUEUES (it describes a THING THAT EXISTS — dropping it
    //                        would make the pack's visibility depend on load order)
    public static class RRS
    {
        /// <summary>Incremented on any breaking change. Gate on it if you care.</summary>
        public static int ApiVersion => 1;

        /// <summary>True once RumbleReShaded is initialised. Calls made while false are ignored, not queued.</summary>
        public static bool Ready => RumbleReShadedMod.ApiReady;

        /// <summary>
        /// Maximum events that can be live at the same time across all mods. Provisional;
        /// exposed so a caller can reason about whether it is likely to flood the buffer.
        /// </summary>
        public static int MaxConcurrentEvents => EventBuffer.MaxEvents;

        // ---- events ---------------------------------------------------------

        /// <summary>
        /// Fire a one-shot world event that shader packs can react to. It lives for
        /// <paramref name="duration"/> seconds and then expires on its own — there is no
        /// cancel, by design.
        /// </summary>
        /// <param name="channel">
        /// MUST be namespaced "modid.name" (e.g. "mymod.hit"). Un-namespaced channels are
        /// rejected with a one-time warning. The "rrs." prefix is reserved for built-ins.
        /// </param>
        /// <param name="worldPos">World-space origin of the event.</param>
        /// <param name="strength">Arbitrary 0..1-ish intensity; the shader decides what it means.</param>
        /// <param name="duration">Lifetime in seconds. Longer events hold their slot longer.</param>
        public static void Fire(string channel, Vector3 worldPos, float strength = 1f, float duration = 1f)
            => Fire(channel, worldPos, 0f, strength, duration);

        /// <summary>
        /// As <see cref="Fire(string, Vector3, float, float)"/>, with an explicit radius for
        /// effects that need spatial extent (rings, blast falloff).
        /// </summary>
        public static void Fire(string channel, Vector3 worldPos, float radius, float strength, float duration)
        {
            if (!Ready) return;
            EventBuffer.Fire(channel, worldPos, radius, strength, duration);
        }

        // ---- continuous values ----------------------------------------------

        /// <summary>
        /// Push a named global float, readable in any shader as "_RRS_" + key. For
        /// sustained state (charge level, health, match phase) rather than moments.
        /// </summary>
        /// <param name="key">MUST be namespaced "modid.name", same rule as channels.</param>
        public static void SetGlobal(string key, float value)
        {
            if (!Ready) return;
            EventBuffer.SetGlobal(key, value);
        }

        // ---- shipping a shader pack inside your own mod ----------------------

        /// <summary>
        /// Register a shader pack that lives INSIDE your mod's DLL, so your users install
        /// one file and never touch UserData/.
        /// </summary>
        /// <remarks>
        /// <para>Use this when the pack IS your mod's effect. Packs a player is meant to
        /// browse, mix and tweak belong in UserData/RumbleReShaded/ as folders instead —
        /// registering those here would hide them from the people who want to swap them.</para>
        ///
        /// <para><b>Call this from your mod's OnInitializeMelon.</b> Registration closes
        /// when RumbleReShaded builds its settings menu in OnLateInitializeMelon; anything
        /// later is rejected with a warning, because UIFramework builds its layout exactly
        /// once and a pack registered after that could never get its sliders.</para>
        ///
        /// <para>Nothing is written to disk. The bundle is loaded straight from memory on
        /// first enable, so there is no path to get wrong and no way for the bundle to go
        /// stale relative to the DLL that shipped it.</para>
        ///
        /// <para>Requires RumbleReShaded 1.2.0+. On older builds this method does not exist,
        /// so guard the call site if you support them.</para>
        /// </remarks>
        /// <param name="modId">
        /// Short stable id for your mod (letters, digits, '_', '-'), e.g. "smudgemod". Your
        /// pack is keyed as "modid.packname" internally so two mods can ship a pack with the
        /// same display name. "rrs" is reserved.
        /// </param>
        /// <param name="manifestJson">The manifest text — same schema as a folder pack's
        /// manifest.json, except "bundle" is ignored (we already have the bytes).</param>
        /// <param name="bundle">Raw AssetBundle bytes, built with the game's exact Unity
        /// version by the RumbleShade template, same as any folder pack.</param>
        /// <returns>True if accepted. False (with a log warning saying why) if not.</returns>
        public static bool RegisterPack(string modId, string manifestJson, byte[] bundle)
            => PackRegistry.Register(modId, manifestJson, bundle);

        /// <summary>
        /// As <see cref="RegisterPack(string, string, byte[])"/>, reading both files out of
        /// your assembly's embedded resources. This is the one-liner most mods want:
        /// <code>
        /// RRS.RegisterPack("smudgemod", Assembly.GetExecutingAssembly(), "manifest.json", "smudge.bundle");
        /// </code>
        /// Set both files' Build Action to <c>EmbeddedResource</c> in your csproj.
        /// </summary>
        /// <param name="manifestResource">Resource name, or just the filename — matched by
        /// suffix, so you do not have to spell out the namespace prefix the compiler adds.</param>
        /// <param name="bundleResource">Same, for the bundle.</param>
        public static bool RegisterPack(string modId, Assembly assembly, string manifestResource, string bundleResource)
            => PackRegistry.RegisterFromAssembly(modId, assembly, manifestResource, bundleResource);

        // ---- built-in effects -----------------------------------------------

        /// <summary>
        /// Fire a built-in parameterised effect — no shader authoring, no Unity needed.
        /// An unknown effectId warns once and is ignored.
        /// </summary>
        /// <remarks>NOT IMPLEMENTED YET (docs/TRIGGER_API.md §5, build order step 4).
        /// The surface exists so consumers can compile against it; check
        /// <see cref="AvailableEffects"/> before relying on it.</remarks>
        public static void Play(string effectId, Vector3 worldPos, float strength = 1f, float duration = 1f)
        {
            if (!Ready) return;
            EventBuffer.PlayNotImplemented(effectId);
        }

        /// <summary>
        /// Which built-in effects this build actually supports, so a caller can degrade
        /// gracefully. Currently empty — see <see cref="Play"/>.
        /// </summary>
        public static IReadOnlyList<string> AvailableEffects => EventBuffer.AvailableEffects;
    }
}
