using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MelonLoader;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace RumbleReShaded
{
    // Per-category shading (plan goal 1) WITHOUT a mask texture and WITHOUT any custom render
    // callback. This is the replacement for the RenderGraph mask route.
    //
    // ⚠ WHY THIS SHAPE — read before "improving" it.
    //
    // The mask route died on 2026-08-05 in the worst way available: SetRenderFunc with a
    // marshalled Il2Cpp delegate corrupted memory badly enough to take Peter's whole PC down.
    // Root cause: RenderGraph invokes a render callback from NATIVE execution code, and an
    // Il2Cpp interop trampoline does not honour that contract.
    //
    // The rule that follows, and it is not negotiable: **we never hand the render pipeline a
    // callback of our own.** Everything executable must be URP's already-compiled code.
    //
    // RenderObjectsPass satisfies that completely. It is URP's own public ScriptableRenderPass —
    // the one behind the "Render Objects" renderer feature. We only ever:
    //   1. construct it,
    //   2. set plain fields on it (filtering settings, override material),
    //   3. EnqueuePass it — the exact call this mod already makes safely every frame.
    // All drawing happens inside URP. No delegate, no injected type, no trampoline.
    //
    // The trade-off, stated plainly: this RECOLOURS geometry directly into the camera target
    // rather than producing a mask other packs can sample. It delivers the visual goal
    // (map black / players grey / structures untouched) but not the platform feature. There is
    // currently no known safe route to a sampleable category mask in this build.
    internal static class CategoryRecolor
    {
        // One pass per category we want to override. Built lazily and reused — constructing a
        // pass per frame would be pointless allocation in a VR frame loop.
        private sealed class CategoryPass
        {
            public uint Bit;
            public string Name;
            public MelonPreferences_Entry<bool> Enabled;
            public MelonPreferences_Entry<float> Red, Green, Blue;
            public RenderObjectsPass Pass;
            public Material Material;
            // Fallback colour, used when this pass has no menu entries behind it. Sandwich step 2
            // builds the category list WITHOUT the 16-entry recolour menu (parked 2026-08-06 and
            // staying parked), so the draws need a colour from somewhere.
            public float Dr, Dg, Db;
            // Effects draw in the transparent queue, not the opaque one. A pass filtered to the
            // opaque range simply never sees them.
            public RenderQueueType Queue = RenderQueueType.Opaque;
        }

        private static readonly List<CategoryPass> passes = new List<CategoryPass>();
        private static bool failed;

        // ── Sandwich step 2 hooks (2026-08-27) ───────────────────────────────────────────────
        //
        // The event the recolour passes are BUILT at. Was hard-coded to AfterRenderingOpaques
        // (300); it has to be settable because step 2 puts the category draws INSIDE the sandwich
        // bracket, and 2026-08-27 established that the bracket cannot start before 300 — there is
        // nothing in the colour target at 299 to save. So the whole arrangement shifts later:
        //
        //     save @301  ->  category draws @302  ->  restore @303
        //
        // Set this BEFORE the first EnsureBuilt: the RenderObjectsPass takes its event in the
        // constructor and the built pass is cached for the session.
        public static RenderPassEvent PassEvent =
            (RenderPassEvent)((int)RenderPassEvent.AfterRenderingOpaques + 2);

        // Draw every category regardless of its menu toggle, using the fallback colours. This is
        // how step 2 gets real category geometry on screen without un-parking 16 menu entries —
        // Peter's "too many options will hurt us" rule applies to our own menu too.
        public static bool ForceAllForSandwich;

        // ── Mask encoding (2026-08-27, step 3) ───────────────────────────────────────────────
        //
        // When true, categories draw in unambiguous CHANNEL-ID colours instead of the aesthetic
        // recolour colours. This is plan §7's "slot -> channel indirection", made real:
        //
        //     Map = R, Players = G, Structures = B, Effects = R+G
        //
        // Why it cannot reuse the recolour colours: those are chosen to look right (map black,
        // players grey, structures white), and a mask has to be READ, not looked at. Black is the
        // single worst possible id in a scene full of dark pixels — every unlit corner of the map
        // would decode as "map". A shader sampling this needs a value it can test exactly.
        //
        // Effects deliberately gets R+G rather than the alpha channel: the VR camera colour is
        // R8G8B8A8_SRGB here so alpha exists today, but the architecture notes already record that
        // a VR colour target may have no usable alpha at all (R11G11B10 has none). Encoding a
        // category in a channel that can vanish with a game update is a trap for later.
        public static bool MaskColours;

        private static Color MaskColourFor(uint bit)
        {
            if (bit == CategoryTagger.Map) return new Color(1f, 0f, 0f, 1f);
            if (bit == CategoryTagger.Players) return new Color(0f, 1f, 0f, 1f);
            if (bit == CategoryTagger.Structures) return new Color(0f, 0f, 1f, 1f);
            if (bit == CategoryTagger.Effects) return new Color(1f, 1f, 0f, 1f);
            return new Color(0f, 0f, 0f, 1f);
        }

        // Shader tags URP uses for its forward opaque geometry. All three are passed because a
        // renderer whose shader declares none of them simply will not be drawn — and silently
        // missing geometry is far harder to notice than an extra tag that matches nothing.
        private static readonly string[] ShaderTags =
            { "UniversalForward", "UniversalForwardOnly", "SRPDefaultUnlit", "LightweightForward" };

        public static void BuildSettings(MelonPreferences_Category cat)
        {
            Add(cat, CategoryTagger.Map, "Map", 0f, 0f, 0f);        // black
            Add(cat, CategoryTagger.Players, "Players", 0.5f, 0.5f, 0.5f);  // grey
            // Structures deliberately get a pass too, defaulting to OFF: Peter's target look
            // leaves them their normal colour, and "no pass" is how you say that. It exists so
            // the third category is available without a code change.
            Add(cat, CategoryTagger.Structures, "Structures", 1f, 1f, 1f);
            // Separate from structures on purpose — dust and impact VFX were being recoloured as
            // whatever they were parented under. Transparent queue: they are not opaque geometry.
            Add(cat, CategoryTagger.Effects, "Effects", 1f, 0f, 1f, RenderQueueType.Transparent);
        }

        private static void Add(MelonPreferences_Category cat, uint bit, string name, float r, float g, float b,
                                RenderQueueType queue = RenderQueueType.Opaque)
        {
            CategoryPass cp = new CategoryPass { Bit = bit, Name = name, Queue = queue, Dr = r, Dg = g, Db = b };
            cp.Enabled = cat.CreateEntry("Recolor" + name, false, "Recolour " + name,
                $"Draw all {name.ToLowerInvariant()} geometry in a flat colour. Requires 'Tag renderer categories' first.");
            cp.Red = cat.CreateEntry("Recolor" + name + "R", r, name + " red", "Red 0-1", false, false,
                new UIFramework.UiExtensions.SliderDescriptor { Min = 0f, Max = 1f, DecimalPlaces = 2 });
            cp.Green = cat.CreateEntry("Recolor" + name + "G", g, name + " green", "Green 0-1", false, false,
                new UIFramework.UiExtensions.SliderDescriptor { Min = 0f, Max = 1f, DecimalPlaces = 2 });
            cp.Blue = cat.CreateEntry("Recolor" + name + "B", b, name + " blue", "Blue 0-1", false, false,
                new UIFramework.UiExtensions.SliderDescriptor { Min = 0f, Max = 1f, DecimalPlaces = 2 });
            passes.Add(cp);
        }

        /// <summary>
        /// Populate the category list WITHOUT creating any menu entries — sandwich step 2.
        ///
        /// Same four categories and the same default colours BuildSettings uses, so what step 2
        /// draws is what the real feature would draw. Idempotent, and a no-op if the recolour menu
        /// was un-parked (in which case the passes already exist, with settings behind them).
        /// </summary>
        public static void BuildDebugPasses()
        {
            if (passes.Count > 0) return;

            AddDebug(CategoryTagger.Map, "Map", 0f, 0f, 0f);                 // black
            AddDebug(CategoryTagger.Players, "Players", 0.5f, 0.5f, 0.5f);   // grey
            AddDebug(CategoryTagger.Structures, "Structures", 1f, 1f, 1f);   // white
            AddDebug(CategoryTagger.Effects, "Effects", 1f, 0f, 1f, RenderQueueType.Transparent);

            MelonLogger.Msg($"[diag] Category recolour: built {passes.Count} menu-less category " +
                $"passes for sandwich step 2, at event {(int)PassEvent}.");
        }

        private static void AddDebug(uint bit, string name, float r, float g, float b,
                                     RenderQueueType queue = RenderQueueType.Opaque)
            => passes.Add(new CategoryPass { Bit = bit, Name = name, Queue = queue, Dr = r, Dg = g, Db = b });

        public static bool AnyEnabled()
        {
            foreach (CategoryPass cp in passes)
                if (cp.Enabled != null && cp.Enabled.Value) return true;
            return false;
        }

        /// <summary>Enqueue one URP RenderObjectsPass per enabled category.</summary>
        public static void Enqueue(ScriptableRenderer renderer)
        {
            if (failed || renderer == null) return;

            try
            {
                foreach (CategoryPass cp in passes)
                {
                    bool on = ForceAllForSandwich || (cp.Enabled != null && cp.Enabled.Value);
                    if (!on) continue;
                    if (!EnsureBuilt(cp)) continue;

                    // Colour is pushed every frame so the sliders are live, exactly like pack
                    // parameters. Cheap: one SetColor on a material nothing else touches.
                    // A menu-less pass (step 2) has no sliders, so it uses its fallback colour.
                    cp.Material.color = MaskColours
                        ? MaskColourFor(cp.Bit)
                        : cp.Enabled != null
                            ? new Color(cp.Red.Value, cp.Green.Value, cp.Blue.Value, 1f)
                            : new Color(cp.Dr, cp.Dg, cp.Db, 1f);
                    renderer.EnqueuePass(cp.Pass);
                }
            }
            catch (Exception e)
            {
                failed = true;
                MelonLogger.Error($"Category recolour failed and is now disabled for this session: " +
                    $"{e.GetType().Name}: {e.Message}\n{e.StackTrace}");
            }
        }

        private static bool EnsureBuilt(CategoryPass cp)
        {
            if (cp.Pass != null) return true;

            Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null)
            {
                MelonLogger.Warning("Category recolour: 'Universal Render Pipeline/Unlit' not found at runtime. " +
                    "The 2026-08-05 scene survey saw 57 renderers using it, so this is unexpected — a game " +
                    "update may have stripped it.");
                failed = true;
                return false;
            }

            cp.Material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };

            // PassEvent (default AfterRenderingOpaques + 2 = 302): the geometry we are overdrawing
            // has been rendered, and this still lands BEFORE RRSRenderPass
            // (AfterRenderingTransparents), so full-screen packs continue to see the final
            // composited image including our recolour.
            //
            // Moved off 300 on 2026-08-27. The sandwich has to bracket these draws, and a save at
            // 299 captures an EMPTY target — proven by rung 5 (299->301) black against rung 9
            // (301->302) clean, same mechanism. So the draws move to 302 and the bracket becomes
            // 301/303, which keeps every part of it on the side of 300 where the world exists.
            cp.Pass = new RenderObjectsPass(
                "RRS Recolor " + cp.Name,
                PassEvent,
                new Il2CppStringArray(ShaderTags),
                cp.Queue,
                -1,                                   // all Unity layers; we filter on rendering layers instead
                new RenderObjects.CustomCameraSettings());

            cp.Pass.overrideMaterial = cp.Material;
            cp.Pass.overrideMaterialPassIndex = 0;

            // THE POINT OF THE WHOLE TAGGING EXERCISE. renderingLayerMask filtering is what makes
            // this per-category rather than per-Unity-layer — and Unity layers were never usable
            // here, because they drive RUMBLE's physics (plan §5).
            //
            // FilteringSettings is a struct: read, modify, write back. Modifying the value
            // returned by the getter in place would update a copy and silently do nothing.
            FilteringSettings fs = cp.Pass.m_FilteringSettings;
            fs.renderingLayerMask = cp.Bit;
            fs.layerMask = -1;
            fs.renderQueueRange = cp.Queue == RenderQueueType.Transparent
                ? RenderQueueRange.transparent : RenderQueueRange.opaque;
            cp.Pass.m_FilteringSettings = fs;

            MelonLogger.Msg($"Category recolour: '{cp.Name}' pass built (renderingLayerMask bit {cp.Bit}).");
            return true;
        }
    }
}
