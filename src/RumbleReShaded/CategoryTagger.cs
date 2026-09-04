using System;
using System.Collections.Generic;
using Il2CppRUMBLE.MoveSystem;
using MelonLoader;
using UnityEngine;

namespace RumbleReShaded
{
    // Stage 3, half 1 of 2: decide what every renderer IS, and record that decision where a
    // render pass can filter on it. Half 2 is the mask pass that turns these tags into a texture
    // pack shaders can sample — this file does not draw anything.
    //
    // Everything here is measured, not assumed: see docs/LAYERED_RENDERING_PLAN.md §10 for the
    // scene survey (2026-08-05) that produced the rules below.
    //
    // ⚠ THE RULE THAT MATTERS: set Renderer.renderingLayerMask, NEVER GameObject.layer.
    // Unity layers drive RUMBLE's physics and collision matrix; retagging them breaks the game.
    //
    // ⚠ AND THE ONE THE PLAN GOT WRONG: renderingLayerMask is NOT free to overwrite either.
    // The survey found RUMBLE already using bits 0-2 (values 1, 2, 4 and combinations, across
    // ~1500 renderers). Assigning over them would break the game's own render features. So we
    // own bits 3+ ONLY, and every write is clear-our-bits-then-OR — never a plain assignment.
    internal static class CategoryTagger
    {
        // Category slots. Deliberately a table of slots rather than four hardcoded categories:
        // per-object identity is deferred but not walled off (plan §7), and a slot allocator is
        // what it will want. Adding a category is a line here plus a name in Names.
        //
        // Bit 0-2 belong to the GAME. Ours start at bit 3.
        public const int FirstBit = 3;

        public const uint Structures = 1u << (FirstBit + 0);   // 8
        public const uint Players    = 1u << (FirstBit + 1);   // 16
        public const uint Map        = 1u << (FirstBit + 2);   // 32
        // Particles, dust, impact VFX. Its own category at Peter's request (2026-08-05) rather
        // than being skipped: effects were inheriting the tag of whatever they were parented
        // under, so structure dust was recoloured as a structure. A category they can be
        // deliberately included in or left out of is the honest answer.
        public const uint Effects    = 1u << (FirstBit + 3);   // 64
        // Sky needs no bit: "no geometry drew here" is already a unique identity from depth
        // alone (plan §1). Spending a bit on it would be spending it twice.

        // Every bit this mod owns. Used to clear before re-tagging, so a re-scan can never
        // leave a stale category behind, and to restore the scene on unload.
        public const uint OwnedBits = Structures | Players | Map | Effects;

        private static readonly (uint Bit, string Name)[] Names =
        {
            (Structures, "structures"),
            (Players,    "players"),
            (Map,        "map"),
            (Effects,    "effects"),
        };

        private static bool warnedNoAccess;

        // ---------------------------------------------------------------------------------

        /// <summary>
        /// Classify and tag every renderer in the scene, including INACTIVE ones.
        /// </summary>
        /// <remarks>
        /// Inactive matters more than it sounds. RUMBLE pools its structures and never
        /// reparents them: a spawned rock is a parked pool instance switched on in place. The
        /// survey found FindObjectsOfType returns active objects ONLY — so tagging with it would
        /// miss every future spawn, because the instances they come from are parked and unseen.
        /// Resources.FindObjectsOfTypeAll sees them, and because pooling REUSES the same
        /// GameObjects, tagging them once covers every spawn for the rest of the session.
        /// </remarks>
        public static void Retag()
        {
            // Logged BEFORE the walk, not just after. Scanning every renderer in the scene takes
            // long enough to look like a dead button — Peter pressed it repeatedly on 2026-08-05
            // thinking it had not registered.
            MelonLogger.Msg("Category tagging: scanning scene…");

            Renderer[] all;
            try
            {
                all = Resources.FindObjectsOfTypeAll<Renderer>();
            }
            catch (Exception e)
            {
                MelonLogger.Error($"Category tagging failed to enumerate renderers: {e.GetType().Name}: {e.Message}");
                return;
            }

            int tagged = 0, skipped = 0, failed = 0;
            Dictionary<uint, int> counts = new Dictionary<uint, int>();

            for (int i = 0; i < all.Length; i++)
            {
                Renderer r = all[i];
                if (r == null) continue;

                GameObject go;
                try { go = r.gameObject; } catch { failed++; continue; }
                if (go == null) continue;

                // FindObjectsOfTypeAll also returns assets and prefabs that were never
                // instantiated into a scene. Tagging those is meaningless at best; skip anything
                // without a valid scene.
                if (!InScene(go)) { skipped++; continue; }

                // Effects are tested FIRST and win over everything else. They are parented under
                // whatever spawned them — structure dust lives under a structure's pool object —
                // so any parent-chain test would otherwise claim them. Peter saw exactly that:
                // structure smoke recoloured as a structure (2026-08-05).
                //
                // Two independent signals, because VFX Graph emits through renderer types that
                // vary by output and its materials are not always readable:
                //   - renderer type (VFXRenderer / ParticleSystemRenderer)
                //   - a "Hidden/" shader (Hidden/VFX/Dust_..., engine internals)
                uint category = (IsParticleRenderer(r) || IsHiddenShader(r)) ? Effects : Classify(go);
                if (category == 0) { skipped++; continue; }

                if (!ApplyBits(r, category)) { failed++; continue; }

                tagged++;
                counts.TryGetValue(category, out int n);
                counts[category] = n + 1;
            }

            System.Text.StringBuilder breakdown = new System.Text.StringBuilder();
            foreach ((uint bit, string name) in Names)
            {
                counts.TryGetValue(bit, out int n);
                breakdown.Append($"{name} {n}, ");
            }

            MelonLogger.Msg($"Category tagging: {tagged} renderer(s) tagged ({breakdown}untagged {skipped}" +
                (failed > 0 ? $", {failed} unreadable" : "") + $") out of {all.Length} scanned.");
        }

        /// <summary>Clear every bit this mod owns, leaving the game's own bits untouched.</summary>
        public static void ClearAll()
        {
            Renderer[] all;
            try { all = Resources.FindObjectsOfTypeAll<Renderer>(); }
            catch { return; }

            int cleared = 0;
            for (int i = 0; i < all.Length; i++)
            {
                Renderer r = all[i];
                if (r == null) continue;
                try
                {
                    uint current = r.renderingLayerMask;
                    uint stripped = current & ~OwnedBits;
                    if (stripped != current) { r.renderingLayerMask = stripped; cleared++; }
                }
                catch { }
            }
            MelonLogger.Msg($"Category tagging cleared from {cleared} renderer(s).");
        }

        // ---------------------------------------------------------------------------------

        /// <summary>Which category a renderer belongs to, or 0 for "leave it alone".</summary>
        private static uint Classify(GameObject go)
        {
            // STRUCTURES — the survey's cleanest result. Spawning 5 types x grounded/ungrounded
            // moved the count by exactly +10, one renderer each.
            //
            // includeInactive is essential: parked pool instances are the whole point of tagging
            // here, and GetComponentInParent skips inactive objects without it.
            Structure structure = Safe(() => go.GetComponentInParent<Structure>(true),
                                  Safe(() => go.GetComponentInParent<Structure>(), null));
            if (structure != null)
            {
                // StructureTarget DERIVES from Structure (verified by dumping
                // Il2CppRUMBLE.Runtime.dll: "TYPE StructureTarget / BASE Structure").
                //
                // They were EXCLUDED at first, which left target markers — including the dark
                // patch on a grounded structure — at their original colour while the rest of the
                // rock recoloured. Peter chose 2026-08-05 to have them follow the structure
                // colour so a rock reads as one solid object, so they are now simply included.
                //
                // The type check is kept, unused but ready: if targets ever need to be their own
                // category, this is the single line that splits them, and it is a type test
                // rather than name matching.
                return Structures;
            }

            // PLAYERS — PROVISIONAL. The 2026-08-05 survey was a solo Gym session, so this is a
            // best guess from the layers that exist there, NOT a measured result like structures.
            // A survey taken mid-match with an opponent visible is queued; expect to rewrite this.
            //
            // Deliberately layer-based READ (never a write): PlayerController and
            // PlayerPhysicsBone are physics layers the game already maintains, so reading them
            // costs nothing and breaks nothing.
            int layer = Safe(() => go.layer, -1);
            if (layer == 23 || layer == 18) return Players;

            // MAP — the level geometry, identified by its hierarchy ROOT.
            //
            // The first attempt used Unity layers 10 (Environment) and 8 (Floor) and was WRONG:
            // it recoloured the gear market tags, the telephone, the match console and the
            // tutorial carvings — props sitting on the Environment layer — while leaving the
            // actual arena untouched (Peter, 2026-08-05: "it just recolors some weird rocks").
            //
            // The survey shows the real arena is the "SCENE" root and nothing else lives there:
            // 31 GYM_Patch_* meshes (Shader Graphs/MobileEnvironmentUV0), 2 URP/Lit, plus
            // GYM_Water, GYM_Vista and GYMMoss. 36 renderers, all of them map.
            //
            // ⚠ Verified in the GYM only. If a match arena uses a different root name this needs
            // revisiting — check a match survey before assuming it generalises.
            if (RootName(go) == "SCENE") return Map;

            return 0;
        }

        // Clear our bits, then OR the new one. Never a plain assignment: bits 0-2 carry the
        // game's own rendering layers (measured: values 1/2/4 in use across ~1500 renderers) and
        // clobbering them would break RUMBLE's render features, not ours.
        private static bool ApplyBits(Renderer r, uint category)
        {
            try
            {
                uint current = r.renderingLayerMask;
                r.renderingLayerMask = (current & ~OwnedBits) | category;
                return true;
            }
            catch (Exception e)
            {
                if (!warnedNoAccess)
                {
                    warnedNoAccess = true;
                    MelonLogger.Warning($"RumbleReShaded: Renderer.renderingLayerMask is not writable in this " +
                        $"build ({e.GetType().Name}) — category masking cannot work. This was probed OK on " +
                        "2026-08-05, so suspect a game update.");
                }
                return false;
            }
        }

        // Matched on the Il2Cpp type name rather than a managed `is` check: the interop wrappers
        // for VFXRenderer/ParticleSystemRenderer are not guaranteed to be generated in every
        // build, and a missing type would be a compile error rather than a graceful skip.
        private static string RootName(GameObject go)
        {
            return Safe(() => go.transform.root.name, "");
        }

        private static bool IsHiddenShader(Renderer r)
        {
            string name = Safe(() =>
                r.sharedMaterial != null && r.sharedMaterial.shader != null ? r.sharedMaterial.shader.name : "", "");
            return name.StartsWith("Hidden/", StringComparison.Ordinal);
        }

        private static bool IsParticleRenderer(Renderer r)
        {
            string type = Safe(() => r.GetIl2CppType().Name, "");
            return type == "VFXRenderer" || type == "ParticleSystemRenderer";
        }

        // Resources.FindObjectsOfTypeAll returns assets and prefabs alongside scene objects.
        // A GameObject that was never instantiated has an invalid scene handle.
        private static bool InScene(GameObject go)
        {
            try { return go.scene.IsValid(); }
            catch { return true; }   // can't tell — tag it rather than silently skip everything
        }

        private static T Safe<T>(Func<T> get, T fallback)
        {
            try { return get(); }
            catch { return fallback; }
        }
    }
}
