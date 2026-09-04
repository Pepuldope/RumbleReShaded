using System;
using MelonLoader;
using UIFramework;
using UIFramework.UiExtensions;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.RenderGraphModule;
using Il2CppInterop.Runtime.Injection;
using RGUtils = UnityEngine.Rendering.RenderGraphModule.Util.RenderGraphUtils;

namespace RumbleReShaded
{
    // ── The "mask sandwich", STEP 1 ONLY ────────────────────────────────────────────────────
    //
    // Goal of the whole idea (docs/LAYERED_RENDERING_PLAN.md §§11-12 + the V3 zip's
    // READ_ME_FIRST.md): get a sampleable category mask WITHOUT ever writing a render callback,
    // by composing only primitives already proven safe here:
    //
    //   1. blit camera colour  -> saved texture        ← THIS FILE
    //   2. CategoryRecolor's RenderObjectsPasses draw every category flat over the camera
    //   3. blit camera colour  -> _RRSCategoryMask, published as a global
    //   4. blit saved texture  -> camera colour, restoring the real frame   ← THIS FILE
    //
    // ⚠ WHAT IS IMPLEMENTED HERE IS STEPS 1 AND 4 AND NOTHING ELSE. No category draws are
    // forced on, no mask texture is copied, no global is published. That is deliberate: the only
    // real unknown in the whole plan is whether a mod can control pass ORDER well enough to
    // interleave its own blits around URP passes it does not own. Proving that in isolation
    // costs one VR test and, if it fails, kills the approach cheaply.
    //
    // ── VR TEST RESULT, 2026-08-06: IT WORKS. THE "FAILURE" WAS CategoryRecolor. ─────────────
    //
    // The first run of this file looked catastrophic — sky WHITE, all opaque geometry BLACK,
    // transparents normal — and it was not this code at all.
    //
    // `CategoryRecolor` was still switched ON from the 2026-08-05 session. Its settings PERSIST
    // in MelonPreferences.cfg; the sandwich toggle is the one that self-clears on startup, and
    // that asymmetry is what made the misattribution so convincing. From the same log:
    //
    //     18:31:53.456  Category recolour: 'Map' pass built …        <- startup
    //     18:33:37.064  [diag] Mask sandwich step 1 recording …      <- 1m44s LATER
    //
    // `EnsureBuilt` is reached only for a pass whose Enabled toggle is true, so Map (black),
    // Players (grey) and Structures (white) were live and drawing before the sandwich ran a
    // single blit. Map-black explains the black world; the sky deliberately carries no category
    // bit, so it kept its real colour and read as white against black; the Effects pass never
    // appears in the log, which is why transparents were untouched. Every symptom accounted for.
    //
    // With the recolour menu parked, rungs 1 and 2 re-ran with NO visible change — the pass
    // criterion. Ordering was never the problem and the blits were never wrong.
    //
    // ⚠ RUNG 3 — the real split-across-two-passes sandwich — IS STILL OWED A RUN. The log for
    // that session shows rung 1 -> 2 -> 1 and contains no `rung 3` line at all. Do not mark
    // step 1 complete until a `rung 3 recorded RESTORE` line exists.
    //
    // LESSON, general: a persisted debug setting from a previous session is indistinguishable
    // from a bug in the thing you are testing now. Check what else is switched on before
    // trusting any visual VR result — the log already says.
    //
    // The ladder below stays: it is what isolated this, and it is how step 2 gets verified.
    //
    // PASS for any rung: the view looks COMPLETELY UNCHANGED. (Very slightly softer opaque edges
    // are the expected MSAA resolve, not a failure — see the caveat at the bottom of this block.)
    //
    // THE RULE, non-negotiable (a violation of it took Peter's entire PC down on 2026-08-05):
    //   never hand the render pipeline a callback of our own. Anything executable must be URP's
    //   already-compiled code.
    // This file honours it: every step is `RGUtils.AddBlitPass`, the exact call RRSRenderPass
    // already makes safely every frame. No SetRenderFunc, no PassData, no delegate.
    //
    // Known, expected cosmetic caveat: the saved texture is forced to single-sample, because you
    // cannot SAMPLE an MSAA texture and VR runs MSAA8x. The restore therefore writes an already
    // resolved image back into the MSAA target, so opaque edges lose their MSAA. If Peter reports
    // "identical except edges look very slightly softer", that is this, and it is expected — it
    // does NOT mean the ordering failed.
    internal static class MaskSandwich
    {
        // ── THE LADDER ───────────────────────────────────────────────────────────────────────
        //
        // Each rung adds exactly ONE thing to the rung below it, so whichever rung first goes
        // black names its own cause. Every rung is blits only — no callback, no PassData, no
        // delegate — so the worst outcome anywhere on this ladder stays visual.
        //
        //   0  off.
        //
        //   1  SAVE ONLY. Blit activeColor -> saved, and never write anything back. A save is a
        //      pure READ of the camera image, so rung 1 CANNOT legally change the view.
        //      If rung 1 is already black, the save itself is destroying the frame, and the
        //      cause is the one §11 rung 1 already taught us: RenderGraph ALIASES transient
        //      memory, so `saved` can be handed the very memory activeColor is living in. The
        //      fix then is to stop it aliasing (import a real RTHandle, or keep the texture
        //      alive across the frame) rather than anything to do with ordering.
        //
        //   2  SAVE + RESTORE, BOTH INSIDE THE SAVE PASS. One RecordRenderGraph call, so the
        //      TextureHandle never leaves the recording that created it. This is the rung that
        //      isolates the blit PAIR from the cross-pass handoff.
        //      Rung 1 clean + rung 2 black => the restore blit is at fault, and the prime
        //      suspect is writing a resolved single-sample image back into an MSAA8x target:
        //      RRSRenderPass only ever blits INTO activeColor through a material, never plain.
        //
        //   3  SAVE + RESTORE SPLIT ACROSS THE TWO PASSES (299 / 301) — the real sandwich.
        //
        //   4  SAVE + RESTORE BOTH INSIDE THE **RESTORE** PASS, i.e. both at 301.
        //      Added 2026-08-06 because rung 3 changes TWO things at once against rung 2 —
        //      which pass records the restore, AND which event it lands at — so on its own it
        //      isolates nothing. Rung 4 holds "one recording" fixed (like rung 2) and moves only
        //      the event (like rung 3), which splits those two variables apart:
        //
        //        rung 4 CLEAN => the event is innocent; the bug is carrying a TextureHandle
        //                        between two separate pass recordings. The sandwich then needs a
        //                        bracket that is not a baton passed through a static field.
        //        rung 4 BLACK => the pass boundary is innocent; something about touching
        //                        activeColor at 301 is the problem. Leading theory: URP draws
        //                        opaque geometry AT event 300, so a save at 299 captures the
        //                        frame BEFORE the world is drawn, and restoring that empty frame
        //                        at 301 paints the freshly-drawn world away. Rung 4 saves at 301,
        //                        after the geometry exists, so it would come out clean if the
        //                        early save is the fault — meaning the fix is simply to move both
        //                        events later, not to abandon the sandwich.
        //
        //      VERDICT 2026-08-06: rung 4 came back CLEAN. The event is innocent; the bug is the
        //      cross-pass handoff. Which gives rung 5.
        //
        //   5  THE FIX. Save at 299 and restore at 301 as in rung 3, but into a texture the MOD
        //      OWNS instead of a RenderGraph transient.
        //
        //      Why rung 3 fails: `renderGraph.CreateTexture` makes a TRANSIENT resource, and
        //      RenderGraph ends its lifetime when nothing further in that recording reads it.
        //      Our reader is in a DIFFERENT recording, so by the time the restore runs at 301 the
        //      memory has been aliased to something else and the blit copies whatever now lives
        //      there. This is precisely §11 rung 1's lesson — "RenderGraph ALIASES transient
        //      memory" — biting from the other end.
        //
        //      An IMPORTED texture is external: RenderGraph neither aliases nor culls its memory,
        //      it only tracks the read/write dependency. So both passes import the same RTHandle
        //      and the bytes survive the gap between them.
        //
        //      Still blits only — `RTHandles.Alloc` / `ImportTexture` / `AddBlitPass` are all
        //      resource calls. THE RULE is not touched: no callback, no PassData, no delegate.
        //
        //      This is not a detour. Step 3 of the V3 plan publishes `_RRSCategoryMask` as a
        //      global for packs to sample at event 500 — the same cross-pass lifetime problem,
        //      several hundred events wider. Rung 5 is that mechanism, proved early.
        //
        //      VERDICT 2026-08-06: rung 5 came back BLACK, and rung 3 came back CLEAN in the same
        //      session it had been black in before. Two things follow, and both matter:
        //
        //        * RUNG 3 IS NON-DETERMINISTIC. Its code path did not change; only the session
        //          did. That is exactly what reading aliased memory looks like — it returns
        //          whatever was reused, which varies. Never read rung 3 as a pass OR a fail.
        //        * Rung 5's black is DIAGNOSTIC, not random. An unwritten RenderTexture is black,
        //          and the sky (event 350) and transparents (450+) are drawn AFTER our restore,
        //          which is why they survive. Rung 4 already proved the blit pair round-trips a
        //          real image. So the save's write is simply not happening.
        //
        //      Why: rung 5 calls ImportTexture in BOTH recordings. Two imports of the same
        //      RTHandle appear to give RenderGraph two independent resource entries, so nothing
        //      expresses "the save's write must precede the restore's read" — and from the save
        //      recording's own point of view nothing reads its output, so it is culled.
        //
        //      ⚠ SUPERSEDED 2026-08-27 — the paragraph immediately above is WRONG. See the
        //      2026-08-27 block at the end of this ladder. Rung 5 is CORRECT and it is the
        //      arrangement to build on; the double import was never the problem.
        //
        //   6  THE SAME THING, IMPORTED ONCE. Import in the save pass, stash the TextureHandle,
        //      and REUSE it in the restore pass. Structurally this is rung 3 — a handle carried
        //      between recordings — except what it points at is imported and therefore stable
        //      rather than transient. One resource entry, one write-then-read dependency.
        //
        //      VERDICT 2026-08-27: BLACK, with the global culling flag ON — i.e. it fails for a
        //      reason that has nothing to do with culling. ⛔ RUNG 6 IS KNOWN BAD AND RETIRED.
        //      It is kept only so the rung numbers still line up with the docs and the old logs.
        //      Carrying a TextureHandle between two pass recordings does not work, imported or
        //      not; rung 5 achieves the same thing correctly by re-importing. Do not repair it.
        //
        // ── 2026-08-27: WHAT THE CULLING FLAG PROVED, AND WHY 7 AND 8 EXIST ──────────────────
        //
        // Ticking the global "Disable pass culling (debug)" toggle and re-running the ladder:
        //
        //     rung 3 -> CLEAN (was black)      rung 5 -> CLEAN (was black)      rung 6 -> BLACK
        //
        // So §14's headline — "three distinct mechanisms for the handoff have each failed, the
        // two-pass bracket does not work in this build" — was wrong. It was ONE mechanism, pass
        // culling, failing three times. The bracket works. Step 1 is not disproved.
        //
        // What that leaves is a sharper and much smaller problem: `AllowPassCulling(false)` in
        // BlitNoCull does NOT achieve what the global flag achieves. Rungs 3 and 5 were black
        // WITH that declaration already in place. The method is real — dumping URP confirmed
        // `AllowPassCulling(Boolean)` on IBaseRenderGraphBuilder with a live interop stub — so
        // "the call isn't there" is ruled out. Either it is not landing on the pass we think, or
        // the pass being culled is not ours at all.
        //
        // ⚠ Keep that second possibility alive. The global flag stops culling of EVERY pass in
        // the graph, not just ours. That our own per-pass declaration did not help is genuine
        // evidence that the culled pass may belong to URP, not to us. Assuming otherwise is
        // exactly the shape of the two wrong diagnoses already recorded in plan §13.
        //
        //   7  RUNG 5 + THE SAVE PUBLISHES ITSELF AS A GLOBAL (`_RRSSandwichSave`).
        //      A pass that binds a global other passes may sample has an observable side effect,
        //      which is a much stronger statement to the compiler than a request not to be culled.
        //      And it is step 3 of this file's own header verbatim, so a pass here is progress on
        //      the shipping design rather than a diagnostic that gets thrown away.
        //
        //   8  RUNG 7 + `AllowGlobalStateModification(true)`.
        //      The explicit "this pass changes global state" declaration. Separate rung so that
        //      if 7 fails and 8 passes we know which declaration carried it. One rung, one change.
        //
        //      ⚠ RUN 7 AND 8 WITH THE GLOBAL CULLING FLAG **OFF**. Their entire purpose is to work
        //      without it — we cannot ship a global debug flag that disables culling for the whole
        //      frame. A clean 7 or 8 with the flag left on proves nothing, so both rungs now print
        //      the flag's state into their own diag lines and say so in as many words.
        //
        //      rung 7 or 8 CLEAN with the flag off => step 1 is passed, we have a shippable
        //      mechanism, and step 3's global mask has its mechanism at the same time.
        //      BOTH BLACK with the flag off => the culled pass is very likely not ours, and the
        //      next move is the graph dump (plan §14 item 2), not another declaration.
        //
        // ── VERDICT 2026-08-27: RUNGS 7 AND 8 ARE BOTH BLACK. IT IS PROBABLY NOT CULLING. ────
        //
        // Both behave exactly like rung 6. So THREE different declarations — AllowPassCulling,
        // SetGlobalTextureAfterPass, AllowGlobalStateModification — each failed to change
        // anything, while a global debug flag fixes it. Before blaming interop, the AddBlitPass
        // signature was dumped and checked argument by argument against our call: all 16 land
        // correctly and `returnBuilder` really is the 14th. The builder IS our blit pass. The
        // declarations are reaching the right pass and doing nothing.
        //
        // Which means the pass was probably never being culled, and the flag has been fixing this
        // through a SIDE EFFECT the whole time. `RenderGraphDebugParams` exposes
        // `AreAnySettingsActive`, and RenderGraph compiles differently when any debug setting is
        // live — so "clean under disablePassCulling" never actually proved culling.
        //
        // LEADING HYPOTHESIS — NATIVE PASS MERGING. Every rung that PASSES keeps both blits on one
        // side of the opaque geometry draw at event 300 (rungs 1, 2, 4). Every rung that FAILS
        // brackets it (3, 5, 6, 7, 8). If URP merges save + geometry + restore into a single
        // native render pass, the restore is sampling a texture written earlier inside that same
        // native pass — the one thing a native pass cannot do — and it reads empty. That single
        // mechanism accounts for every result on this ladder AND for why no anti-culling
        // declaration helped.
        //
        // THE TEST: `disablePassMerging`, now its own menu toggle. Run rung 5 or 7 with ONLY that
        // ticked. Clean => merging (or the debug compile path) is the cause and culling never was;
        // black => culling is back in the frame and the question is why our per-pass declaration
        // does not do what the global one does. Either way one VR run splits it, which is cheaper
        // and sharper than reviving the graph dump.
        //
        // ── 2026-08-27, THE CONTROL RUN. IT IS NEITHER CULLING NOR MERGING. ──────────────────
        //
        // Rung 5, current game build, one variable at a time:
        //
        //     culling disabled   -> CLEAN
        //     merging disabled   -> CLEAN
        //     NOTHING disabled   -> BLACK      <- the control that had never been run
        //
        // Two flags with unrelated primary effects, each independently sufficient. That rules out
        // both primary effects at once: not culling (rung 5 survives with culling ON, merging off)
        // and not merging (it survives with merging ON, culling off). Every declaration-based fix
        // attempted on rungs 5/7/8 was therefore aimed at the wrong thing from the start.
        //
        // What the two flags share is `areAnySettingsActive`, and RenderGraph disables its
        // COMPILED-GRAPH CACHING whenever any debug setting is live. That is the remaining common
        // cause, and `m_EnableCompilationCaching` is settable, so it is now toggle #3.
        //
        // NEXT: rung 5 with ONLY "Disable graph compilation caching (debug)" ticked.
        //   clean => caching is the mechanism. The bracket is sound; what breaks it is URP reusing
        //            a compiled graph that does not account for our per-frame re-import. The
        //            shipping fix is then about graph stability (or about not re-importing every
        //            frame), NOT about culling declarations.
        //   black => the shared cause is something else `areAnySettingsActive` gates, and the
        //            graph dump finally becomes worth its cost.
        //
        // ── 2026-08-27, LATER: ⛔ STOP. EVERY FLAG CONCLUSION ABOVE IS CONTAMINATED. ─────────
        //
        // The caching run answered a different question than the one it was asked. One session,
        // ONE unchanged flag state throughout (`culling=on merging=on caching=OFF`), rungs visited
        // in the order 4 3 4 5 2 5 6 8 5:
        //
        //     rung 5, first visit  -> CLEAN
        //     rung 5, second visit -> BLACK
        //     rung 5, third visit  -> BLACK
        //     rung 4, second visit -> BLACK    (rung 4 had never gone black in any session)
        //
        // Same rung. Same flags. Opposite results. So the FLAGS NEVER DETERMINED THE OUTCOME AT
        // ALL — the rung's history within the session did. A rung tells the truth only the first
        // time its graph shape is compiled in a session; every later visit reads black.
        //
        // Consequences, and they are large:
        //
        //   * The caching hypothesis is dead on its own terms. Caching was disabled for that whole
        //     session and rung 5 still flipped clean -> black.
        //   * "culling disabled -> clean" and "merging disabled -> clean" were almost certainly
        //     FIRST VISITS, not flag effects. Two toggles and a diagnosis were built on that.
        //   * The 2026-08-06 "non-determinism" blamed on transient memory aliasing was never
        //     random either. It was order-dependence, with nobody restarting between rungs.
        //   * Rungs 6, 7 and 8 read black "on first visit" because they produce the same graph
        //     shape as rung 5, which had already been compiled. Their results say nothing about
        //     the declarations they were built to test.
        //
        // Something is reused across rung changes and our caching disable does not govern it.
        // That is now the whole question — but it cannot be investigated with contaminated data,
        // so the rung LOCK (see the Rung property) comes first.
        //
        // THE ONLY RUN THAT COUNTS NEXT: fresh launch, rung 5, no flags, nothing else touched.
        // That is the shipping configuration. If it is clean, step 1 passes and this entire flag
        // investigation was an artefact of switching rungs.
        //
        // ⚠ Also learned here: rung 7 is black even where rung 5 is clean, under identical flags.
        // They differ by one line, so `SetGlobalTextureAfterPass` on the save is independently
        // harmful. Consequence for step 3: publish the mask global from its OWN pass, never from
        // a pass whose output something else has to read back. And rung 8 never tested what it
        // claimed to — it is rung 7 plus a declaration, and rung 7 was already broken.
        //
        // Forced to 0 on every startup, never persisted. Same reasoning as maskDebugMode in
        // Main.cs: this is experimental render-order code, and if it ever went wrong badly
        // enough to make the view unreadable, a persisted "on" would mean Peter cannot find the
        // menu to switch it off again. Re-selecting a rung each session costs nothing.
        private static MelonPreferences_Entry<float> rungSetting;

        // Persisted, unlike rungSetting — this is the "run this rung on the next launch" arming,
        // and it is deliberately consumed the moment it is used. See BuildSettings.
        private static MelonPreferences_Entry<float> startupRungSetting;

        public const int MaxRung = 13;

        // ── rungs 12 / 13 — V3 STEP 3, a real sampleable category mask ───────────────────────
        //
        //     save @301  ->  BLANK the frame @301  ->  category draws @302 (id colours)
        //     ->  copy to _RRSCategoryMask @303  ->  restore the real frame @304
        //
        // The blank is the piece the plan never had, and it is what makes the mask CLEAN. Step 2
        // draws categories over the live world, so untagged pixels keep world colour and a shader
        // sampling that cannot tell "untagged" from "a wall that happens to be this colour". By
        // blitting a cleared texture over the frame first, every untagged pixel is exactly zero:
        //
        //     black = nothing here          R = map
        //     G = players                   B = structures        R+G = effects
        //
        // It costs one extra blit and needs no new capability — the save already holds the real
        // frame, and the restore already puts it back. This is only safe because step 1 proved the
        // bracket: we are deliberately destroying the camera colour for two events, which would be
        // unrecoverable if the restore did not work. It does (rung 10).
        //
        // Both blits stay inside the SAVE recording, so no extra pass is needed for the blank —
        // rungs 2 and 4 already proved two blits in one recording are fine.
        //
        //   rung 12 => full step 3. PASS = NO VISIBLE CHANGE, exactly like rung 10. The mask is
        //              built and published without the player ever seeing it.
        //   rung 13 => rung 12 with the restore suppressed, so THE MASK ITSELF IS ON SCREEN.
        //              Expect a black world with flat RED map, GREEN players, BLUE structures.
        //              Run 13 first: it is the only way to see whether the mask is correct, and a
        //              clean rung 12 says nothing about the mask's CONTENT, only that the frame
        //              was restored.
        public const RenderPassEvent Step3SaveEvent =
            (RenderPassEvent)((int)RenderPassEvent.AfterRenderingOpaques + 1);
        public const RenderPassEvent Step3DrawEvent =
            (RenderPassEvent)((int)RenderPassEvent.AfterRenderingOpaques + 2);
        public const RenderPassEvent Step3MaskEvent =
            (RenderPassEvent)((int)RenderPassEvent.AfterRenderingOpaques + 3);
        public const RenderPassEvent Step3RestoreEvent =
            (RenderPassEvent)((int)RenderPassEvent.AfterRenderingOpaques + 4);

        internal static bool IsStep3(int rung) => rung == 12 || rung == 13;

        // The published mask. Name is public API the moment a pack samples it — pack authors will
        // write this string into their shaders and it cannot be changed afterwards without
        // breaking them. Namespaced like everything else on the trigger API.
        private static int maskGlobalId;

        private static int MaskGlobalId => maskGlobalId != 0
            ? maskGlobalId
            : (maskGlobalId = Shader.PropertyToID("_RRSCategoryMask"));

        // Second owned target, for the mask itself. Separate from the save target because both are
        // live at once: the save holds the real frame while the mask holds the categories.
        private static RenderTexture maskRT;
        private static RTHandle maskHandle;
        private static int maskW, maskH, maskSlices;
        private static GraphicsFormat maskFormat;
        private static bool maskDiagLogged;

        // ── rung 11 — RUNG 10 WITH THE RESTORE REMOVED ───────────────────────────────────────
        //
        // Step 2's pass criterion is "no visible change", and so is the appearance of a step 2
        // that silently drew NOTHING — untagged renderers match no category, so the draws are
        // no-ops and the frame is unchanged for the wrong reason. Main.cs has warned about that
        // false pass since 2026-08-06; a rung that can only be read correctly if you already
        // trust an unrelated button is not a test.
        //
        // Rung 11 is rung 10 with the restore suppressed, so the category draws stay on screen:
        //
        //     RUN RUNG 11 FIRST. Press "Tag renderer categories". Expect FLAT COLOURS —
        //     map BLACK, players GREY, structures WHITE, effects MAGENTA. That proves tagging
        //     and the draws both work.
        //     THEN RUN RUNG 10. Expect NO VISIBLE CHANGE. Same draws, now undone by the restore.
        //
        // Two rungs, one claim each. A clean rung 10 only means something once rung 11 has shown
        // there was something to undo.
        public const int Rung11 = 11;

        // ── rung 10 — V3 STEP 2, the first arrangement that can actually produce a mask ──────
        //
        //     save @301  ->  CategoryRecolor draws every category flat @302  ->  restore @303
        //
        // Rung 9 proved the bracket half works at 301/302 on the shipping path. Rung 10 puts the
        // category draws inside it. PASS CRITERION IS STILL "NO VISIBLE CHANGE" — the categories
        // are drawn flat over the world and then the restore paints the real frame back, so a
        // correct result looks like nothing happened. Step 3 is what keeps a copy of the drawn
        // categories as a sampleable mask; this step only proves they can be drawn and undone.
        //
        // ⚠ NEEDS TAGGED RENDERERS. Press "Tag renderer categories" first, or the draws match
        // nothing, the frame is genuinely unchanged, and the rung reads as a FALSE PASS. That
        // warning has been in Main.cs since 2026-08-06 and it is the whole reason the Retag /
        // Clear buttons come back in the same change as this rung.
        //
        //   rung 10 CLEAN with tags applied => step 2 passed, step 3 (mask copy + global) is next.
        //   rung 10 shows FLAT CATEGORY COLOURS => the draws happen but the restore is not
        //           putting the frame back; the bracket is being broken by the draws themselves.
        //   rung 10 BLACK like rung 5 => the draws moved the active target and the restore is
        //           writing into something else; compare the two activeColor diag lines.
        public const RenderPassEvent Rung10SaveEvent =
            (RenderPassEvent)((int)RenderPassEvent.AfterRenderingOpaques + 1);
        public const RenderPassEvent Rung10RestoreEvent =
            (RenderPassEvent)((int)RenderPassEvent.AfterRenderingOpaques + 3);
        public const RenderPassEvent Rung10DrawEvent =
            (RenderPassEvent)((int)RenderPassEvent.AfterRenderingOpaques + 2);

        // ── rung 9's events ──────────────────────────────────────────────────────────────────
        //
        // 301 and 302: two separate pass recordings, adjacent events, with NOTHING of URP's
        // scheduled between them. Rung 5 is the same mechanism straddling 300.
        //
        // This is the test the ladder never had. Rungs 2 and 4 prove the blit pair round-trips a
        // real image inside ONE recording, at 299 and at 301 respectively. Rung 5 proves the split
        // version fails. But rung 5 changes TWO things against rung 4 — the split AND the fact
        // that event 300 now falls between the halves — so it cannot say which one is fatal.
        //
        //   rung 9 CLEAN => the cross-recording handoff is FINE. Rung 5 fails because of whatever
        //                   URP does at 300, and the sandwich needs to bracket somewhere else (or
        //                   the category draws need to move). The whole "a TextureHandle cannot
        //                   cross a recording" conclusion, carried since 2026-08-06, is wrong.
        //   rung 9 BLACK => the handoff itself is genuinely broken, independent of what sits
        //                   between the passes, and the bracket must be built from something
        //                   other than two passes sharing a texture.
        //
        // ✅ VERDICT 2026-08-27: RUNG 9 IS CLEAN, on the shipping path with no debug flags:
        //
        //     save@301 restore@302, two recordings, imported mod-owned texture -> NO CHANGE
        //     activeColor AT SAVE:    '_CameraTargetAttachment' … R8G8B8A8_SRGB msaa=MSAA8x
        //     activeColor AT RESTORE: '_CameraTargetAttachment' … R8G8B8A8_SRGB msaa=MSAA8x
        //
        // So §14's "every way of carrying an image between two pass recordings fails" is WRONG,
        // and V3 steps 2-5 were blocked for three weeks behind a conclusion that does not hold.
        // The bracket works. Carrying a TextureHandle between two recordings works. An imported
        // mod-owned texture survives the gap. What does NOT work is doing it ACROSS EVENT 300.
        //
        // The remaining question is therefore narrow and concrete: what does URP do at
        // AfterRenderingOpaques that invalidates our save? Prime suspects are its own copy-colour
        // and copy-depth passes, which land there and can switch the active colour attachment.
        // Rung 5 re-run with the identity diagnostic (added after rung 5 was last tested) prints
        // activeColor from BOTH sides of 300 and should settle it in one line.
        public const RenderPassEvent Rung9SaveEvent =
            (RenderPassEvent)((int)RenderPassEvent.AfterRenderingOpaques + 1);
        public const RenderPassEvent Rung9RestoreEvent =
            (RenderPassEvent)((int)RenderPassEvent.AfterRenderingOpaques + 2);

        // The events actually used this frame, so the diag lines never lie about where the passes
        // ran. Rung 9 moves them, and a log line hard-coded to the class constants would report
        // 299/301 for a run that happened at 301/302.
        private static int activeSaveEvent = (int)RRSSandwichSavePass.Event;
        private static int activeRestoreEvent = (int)RRSSandwichRestorePass.Event;

        // Shader property the save publishes itself under on rungs 7 and 8. Resolved lazily
        // rather than in a static initialiser: Shader.PropertyToID is a Unity call and this type
        // is touched from BuildSettings, which now runs in OnLateInitializeMelon.
        private static int sandwichGlobalId;

        private static int SandwichGlobalId => sandwichGlobalId != 0
            ? sandwichGlobalId
            : (sandwichGlobalId = Shader.PropertyToID("_RRSSandwichSave"));

        /// <summary>
        /// The state of BOTH global debug toggles, as a log-safe string.
        ///
        /// This goes into every rung's diag line on purpose, and it now names both flags because
        /// the whole point of the 2026-08-27 experiment is which ONE of them is responsible. A
        /// rung is only interpretable next to the flags it ran under, and the 2026-08-06 session
        /// proved that a debug setting nobody remembers switching on is indistinguishable from a
        /// bug in the thing being tested. Both on at once tells us nothing — the log says so.
        /// </summary>
        private static string DebugFlagState
        {
            get
            {
                bool culling = disableCullingSetting != null && disableCullingSetting.Value;
                bool merging = disableMergingSetting != null && disableMergingSetting.Value;
                bool caching = disableCachingSetting != null && disableCachingSetting.Value;

                int on = (culling ? 1 : 0) + (merging ? 1 : 0) + (caching ? 1 : 0);
                if (on == 0) return "nothing disabled — the shipping path";

                string which = $"culling={(culling ? "OFF" : "on")} " +
                               $"merging={(merging ? "OFF" : "on")} " +
                               $"caching={(caching ? "OFF" : "on")}";

                return on > 1
                    ? which + " — MORE THAN ONE DISABLED, THIS RUN ISOLATES NOTHING"
                    : which;
            }
        }

        // ── THE SESSION RUNG LOCK (2026-08-27) ───────────────────────────────────────────────
        //
        // The first non-zero rung selected in a session LATCHES. After that the only values
        // accepted are that rung and 0; anything else is refused, snapped back to 0, and logged.
        //
        // This exists because switching rungs mid-session silently invalidates the result, and it
        // did so for three sessions running before anyone noticed. Measured 2026-08-27, one game
        // session, ONE unchanged flag state, rungs visited in the order 4 3 4 5 2 5 6 8 5:
        //
        //     rung 5, first visit  -> CLEAN
        //     rung 5, second visit -> BLACK
        //     rung 5, third visit  -> BLACK
        //     rung 4, second visit -> BLACK   (rung 4 had never gone black before)
        //
        // Same rung, same flags, opposite results. Something is reused across rung changes, so a
        // rung is only telling the truth the FIRST time its graph shape is compiled in a session.
        // Every "flag X fixes it" conclusion drawn before this was discovered is suspect for the
        // same reason: those were first visits, not flag effects.
        //
        // 0 is ALWAYS honoured, deliberately — this is experimental render code, and if a rung
        // ever makes the view unreadable, Peter has to be able to switch it off without a lock
        // standing in the way. That is the same reasoning as the reset-to-0-on-startup rule.
        //
        // PROTOCOL, now enforced rather than merely written down: one rung per game session.
        private static int lockedRung = -1;
        private static int lastRefusedRung = -1;

        public static int Rung
        {
            get
            {
                if (rungSetting == null) return 0;

                int requested = Mathf.Clamp((int)rungSetting.Value, 0, MaxRung);

                if (lockedRung < 0)
                {
                    if (requested == 0) return 0;

                    lockedRung = requested;
                    MelonLogger.Msg($"[diag] Mask sandwich: rung {lockedRung} is now LOCKED for " +
                        "this session. To test a different rung, RESTART THE GAME — switching " +
                        "rungs in-session produces results that look real and are not.");
                    return lockedRung;
                }

                // Always allow switching the whole thing off.
                if (requested == 0) return 0;

                if (requested != lockedRung)
                {
                    if (lastRefusedRung != requested)
                    {
                        lastRefusedRung = requested;
                        MelonLogger.Warning($"Mask sandwich: REFUSED rung {requested} — this " +
                            $"session is locked to rung {lockedRung}. Restart the game to test " +
                            $"another rung. Switching in-session is what made rung {lockedRung} " +
                            "read clean once and black afterwards with nothing else changed.");
                    }

                    // Snap to 0 rather than back to the locked rung: the refusal has to be
                    // VISIBLE in the menu, and silently continuing to run a rung he just tried to
                    // change away from would be its own kind of lie.
                    rungSetting.Value = 0f;
                    return 0;
                }

                return lockedRung;
            }
        }

        public static bool Enabled => Rung > 0;

        // The saved frame, handed from the save pass to the restore pass.
        //
        // A TextureHandle is only meaningful inside ONE graph recording, so this is not a cache —
        // it is a within-frame baton. `savedFrame` is what makes that safe: if the restore pass
        // is not recorded in the same frame as the save (mod toggled off mid-frame, camera
        // filtered out, URP dropping a pass), the stale handle is refused rather than blitted.
        private static TextureHandle saved;
        private static int savedFrame = -1;

        // ── the mod-owned save target, shared by rungs 5-8 ───────────────────────────────────
        //
        // Allocated lazily, and ONLY when one of those rungs is actually selected — this is a
        // full-size VR colour target (2244x2352 x2 slices), so it is not something to hold for a
        // rung nobody is running. Reallocated if the camera's dimensions or format change under us.
        private static RenderTexture ownedRT;
        private static RTHandle ownedHandle;
        private static int ownedW, ownedH, ownedSlices;
        private static GraphicsFormat ownedFormat;

        // ── ground truth: RenderGraph's own debug facilities ─────────────────────────────────
        //
        // Added 2026-08-06 because the headset stopped being a usable oracle: the same rung came
        // back clean one launch and black the next, twice over. Guessing from a black screen is
        // what produced two wrong diagnoses already. These two read the pipeline directly.
        //
        // Both are data-only fields on `renderGraph.debugParams`, which is a plain public getter
        // on a class Unity ships. No callback, no injected type — THE RULE is untouched.
        // ⛔ HARD-DISABLED 2026-08-06 — the graph dump CRASHED THE GAME. Do not flip this back on
        // without doing the work described below first.
        //
        // Root cause, from Player.log, and it is entirely my own:
        //     NullReferenceException
        //       at RenderGraphModule.RenderGraphLogger.LogLine
        //       at RenderGraphModule.RenderGraph.LogFrameInformation
        //       at RenderGraphModule.RenderGraph.EndRecordingAndExecute
        //
        // Unity initialises `m_FrameInformationLogger` inside BeginRecording, gated on
        // `debugParams.enableLogging`. We set `logFrameInformation` from INSIDE RecordRenderGraph
        // — after that check has already run — so the logger was never initialised and threw at
        // end of frame. The exception handler then hit the SAME null logger in
        // CleanupResourcesAndGraph, so the graph could not reset and the game went down.
        //
        // To make it work, the flag has to be set BEFORE BeginRecording (i.e. from
        // OnBeginCameraRendering, which needs a RenderGraph reference we do not currently have),
        // or `m_FrameInformationLogger` has to be constructed and Initialize()d by hand.
        //
        // ⚠ Note for whoever reads this next to gauge risk: this is a MANAGED exception inside
        // Unity's own code, NOT the §11 class of failure. §11 was native memory corruption
        // reaching the GPU driver and it took the whole PC down. This only killed the game
        // process, and nothing about it persists across a restart.
        private const bool GraphDumpEnabled = false;

        private static bool dumpRequested;
        private static MelonPreferences_Entry<bool> disableCullingSetting;
        private static MelonPreferences_Entry<bool> disableMergingSetting;
        private static MelonPreferences_Entry<bool> disableCachingSetting;

        // The value RenderGraph itself shipped with, captured the first time we look. We only ever
        // AND our own "off" into it — we never fabricate a `true`. If some future URP build has
        // compilation caching off by default, this code must not quietly switch it on.
        private static bool cachingDefault;
        private static bool cachingDefaultKnown;
        private static bool cachingWriteFailed;

        private static bool failed;
        private static bool diagLogged;
        private static bool restoreDiagLogged;
        private static bool skipDiagLogged;
        private static int diagRung = -1;

        public static void BuildSettings(MelonPreferences_Category cat)
        {
            rungSetting = cat.CreateEntry("MaskSandwich", 0f, "Mask sandwich test (rung)",
                "Experimental, blits only. 0 = off, 1 = save only, 2 = one pass at 299, 3 = split " +
                "via a transient texture (UNRELIABLE — memory-pool dependent), 4 = one pass at " +
                "301, 5 = split, mod-owned texture, imported twice (works, but only with the " +
                "culling flag below), 6 = KNOWN BAD, kept for numbering only, 7 = rung 5 plus the " +
                "save published as a global, 8 = rung 7 plus a global-state-modification " +
                "declaration, 9 = rung 5 with both halves moved to 301/302 so nothing of URP's " +
                "sits between them, 10 = STEP 2 (save@301, category draws@302, restore@303 — " +
                "expect NO change), 11 = step 2 with the restore suppressed (expect VISIBLE flat " +
                "colours — RUN THIS ONE FIRST, and press 'Tag renderer categories' before judging " +
                "either), 12 = STEP 3 (blank + id-colour draws + mask copy published as " +
                "_RRSCategoryMask + restore — expect NO change), 13 = step 3 with the restore " +
                "suppressed so YOU SEE THE MASK (black world, RED map, GREEN players, BLUE " +
                "structures) — RUN 13 BEFORE 12. ⚠ THE FIRST RUNG YOU PICK LOCKS FOR THE SESSION — to test another " +
                "one, RESTART THE GAME. Switching rungs in-session gives results that look real " +
                "and are not. You can always go back to 0 to switch it off. Correct result on " +
                "every rung is NO visible change. Resets to 0 on restart.",
                false, false, new SliderDescriptor { Min = 0f, Max = MaxRung, DecimalPlaces = 0 });

            // ── ONE-SHOT STARTUP ARMING (2026-08-27, Peter's idea) ───────────────────────────
            //
            // With the session lock in place, testing a rung means restarting anyway — so having
            // to walk into the menu and set the slider every launch is pure friction. Arm the rung
            // you want, restart, and it is already running.
            //
            // The arming is CONSUMED on use: we read it, then immediately write it back to 0 and
            // save. That keeps the protection the old reset-to-0-on-startup rule provided without
            // the friction. If a rung ever makes the view unreadable — or worse, takes the game
            // down on load — the NEXT launch is already back to rung 0, so it cannot become a boot
            // loop that has to be fixed by hand-editing MelonPreferences.cfg. One arm, one run.
            startupRungSetting = cat.CreateEntry("StartupRung", 0f, "Rung at next startup",
                "Arms a rung to run automatically on the NEXT launch, so testing needs no menu " +
                "trip. Cleared as soon as it is used — each arm is good for exactly one run, " +
                "which is also exactly the one-rung-per-session protocol. 0 = don't arm anything.",
                false, false, new SliderDescriptor { Min = 0f, Max = MaxRung, DecimalPlaces = 0 });

            int armed = Mathf.Clamp((int)startupRungSetting.Value, 0, MaxRung);

            if (armed > 0)
            {
                // Consume it BEFORE anything else can fail. If the rung is going to bring the game
                // down, the arming must already be spent by the time it does.
                try
                {
                    startupRungSetting.Value = 0f;
                    MelonPreferences.Save();
                }
                catch (Exception e)
                {
                    MelonLogger.Warning("Mask sandwich: could not clear the startup arming " +
                        $"({e.GetType().Name}: {e.Message}). Set 'Rung at next startup' back to 0 " +
                        "by hand, or this rung will run again next launch.");
                }

                lockedRung = armed;
                rungSetting.Value = armed;

                MelonLogger.Msg($"[diag] Mask sandwich: rung {armed} ARMED AT STARTUP and locked " +
                    "for this session. The arming has been cleared — the next launch is rung 0 " +
                    "unless you arm another one.");
            }
            else
            {
                // Nothing armed: same rule as before, start off and latch on first manual pick.
                rungSetting.Value = 0f;
            }

            // Ground truth #1 — dump the COMPILED graph for a single frame. This is the one that
            // answers "did our save pass actually survive?" without anyone squinting at a wall.
            // Unity resets the flag itself after one frame, so this cannot be left on by accident.
            UI.CreateButtonEntry(cat, "DumpRenderGraph", "Dump render graph (1 frame)",
                "Writes RenderGraph's own compiled pass and resource list to Player.log for one " +
                "frame. Needs the rung slider above 0, since that is what puts our passes in the " +
                "graph. Look for 'RRS Sandwich' entries.",
                (Action)(() => RequestGraphDump()));

            // Ground truth #2 — turn culling off for the WHOLE graph. If every rung becomes stable
            // with this on, culling is conclusively the mechanism and no further argument is
            // needed. Global, so it costs performance; off by default and never persisted.
            disableCullingSetting = cat.CreateEntry("DisablePassCulling", false,
                "Disable pass culling (debug)",
                "Stops RenderGraph culling ANY pass, not just ours. If this makes every rung " +
                "stable, culling is proven to be the cause. Costs performance. Resets off on restart.");
            disableCullingSetting.Value = false;

            // Ground truth #3 — the OTHER debug flag, added 2026-08-27.
            //
            // This is the control the ladder never had. `disablePassCulling` was assumed to work
            // because it disables culling; but it also flips `AreAnySettingsActive`, and a debug
            // setting being active changes how RenderGraph compiles the whole frame. So a clean
            // rung under that flag does NOT prove culling was the mechanism — it proves only that
            // SOMETHING about the debug compilation path fixes it.
            //
            // `disablePassMerging` is a different flag with the same side effect on that property
            // and a completely different primary effect. Running rung 5 or 7 under THIS one alone
            // splits the two apart:
            //
            //   clean under merging-off too  => merging (or the debug path itself) is the cause,
            //                                   NOT culling, and the three declarations we added
            //                                   were never going to help.
            //   black under merging-off      => culling really is the mechanism after all, and the
            //                                   question goes back to why our own declaration on
            //                                   the pass does not achieve what the global does.
            //
            // Why merging is the leading suspect: rungs 1/2/4 all keep both blits on ONE side of
            // the opaque geometry draw at event 300 and all pass. Every failing rung BRACKETS that
            // draw. If URP merges save + geometry + restore into one native render pass, the
            // restore is reading a texture written earlier in the SAME native pass — which is
            // exactly the read-after-write that native passes do not allow, and it comes back
            // empty. That single mechanism explains every result on the ladder, including why
            // AllowPassCulling / SetGlobalTextureAfterPass / AllowGlobalStateModification all did
            // nothing: the pass was never culled in the first place.
            disableMergingSetting = cat.CreateEntry("DisablePassMerging", false,
                "Disable pass merging (debug)",
                "Stops RenderGraph merging adjacent passes into one native render pass. Run rung " +
                "5 or 7 with ONLY this ticked and the culling one OFF. Costs performance. " +
                "Resets off on restart.");
            disableMergingSetting.Value = false;

            // Ground truth #4 — the thing the other two flags have in common. Added 2026-08-27
            // after the control run.
            //
            // Measured on the current build, rung 5:
            //     culling disabled  -> CLEAN
            //     merging disabled  -> CLEAN
            //     nothing disabled  -> BLACK
            //
            // Two flags with completely different primary effects, each independently sufficient.
            // That rules out BOTH of their primary effects: it is not culling (or rung 5 would
            // still fail with only merging off) and it is not merging (or it would still fail with
            // only culling off). What they actually share is `areAnySettingsActive` — and
            // RenderGraph turns OFF its compiled-graph caching whenever any debug setting is live.
            //
            // So the suspect is compilation caching itself. RenderGraph compiles a frame's graph
            // once and reuses that compiled result on later frames; if the cached graph does not
            // account for our re-imported RTHandle, the restore reads a stale or unwritten
            // resource — black — and every anti-culling declaration in the world is irrelevant,
            // which is exactly what we measured across rungs 5/7/8.
            //
            // `m_EnableCompilationCaching` is settable through interop, so this is one bool write
            // and no new machinery. Data only — THE RULE is untouched.
            //
            // ⚠ Slightly more adventurous than the other two: those are read by the compiler each
            // frame, this one steers the cache. It is written inside RecordRenderGraph, which is
            // BEFORE the compile in EndRecordingAndExecute, so it should take effect for the same
            // frame. If it throws, the write latches off and says so once rather than retrying.
            disableCachingSetting = cat.CreateEntry("DisableCompilationCaching", false,
                "Disable graph compilation caching (debug)",
                "Stops RenderGraph reusing a previously compiled graph. This is what the other " +
                "two toggles disable as a SIDE EFFECT. Run rung 5 with ONLY this ticked. If that " +
                "is clean, caching is the real mechanism. Resets off on restart.");
            disableCachingSetting.Value = false;
        }

        /// <summary>Arm the one-frame graph dump. Currently hard-disabled — see GraphDumpEnabled.</summary>
        public static void RequestGraphDump()
        {
            if (!GraphDumpEnabled)
            {
                MelonLogger.Warning("Render graph dump is DISABLED — it crashed the game on " +
                    "2026-08-06 (uninitialised RenderGraphLogger; see MaskSandwich.GraphDumpEnabled). " +
                    "Nothing was armed.");
                return;
            }

            if (!Enabled)
            {
                MelonLogger.Warning("Mask sandwich: set the rung slider above 0 first — at rung 0 " +
                    "our passes are never enqueued, so a graph dump would not show them.");
                return;
            }

            dumpRequested = true;
            MelonLogger.Msg("[diag] Render graph dump armed for the next frame. Output goes to " +
                "Unity's Player.log (…/AppData/LocalLow/Buckethead Entertainment/RUMBLE/Player.log), " +
                "NOT the MelonLoader log — RenderGraph logs through Debug.Log.");
        }

        /// <summary>
        /// Push our debug flags into the live RenderGraph. Called at the top of BOTH pass
        /// recordings so it works whichever rung is selected — rung 4 records nothing in the save
        /// pass, rung 2 nothing in the restore pass, and a dump must work for either.
        /// </summary>
        private static void ApplyDebugParams(RenderGraph renderGraph)
        {
            try
            {
                RenderGraphDebugParams dp = renderGraph.debugParams;
                if (dp == null) return;

                // Safe: a plain bool the graph compiler reads every frame. It involves none of the
                // logger machinery that crashed the dump, and RUMBLE's own rendering exercises
                // this code path constantly.
                dp.disablePassCulling = disableCullingSetting != null && disableCullingSetting.Value;
                dp.disablePassMerging = disableMergingSetting != null && disableMergingSetting.Value;

                // Its own try/catch below, deliberately: this one lives on the RenderGraph rather
                // than on debugParams, and if it is unreachable the other two must keep working.
                ApplyCompilationCaching(renderGraph);

                if (GraphDumpEnabled && dumpRequested)
                {
                    dumpRequested = false;
                    dp.logFrameInformation = true;   // the compiled pass list, in execution order
                    dp.logResources = true;          // every resource, so an unwritten save shows
                }
            }
            catch (Exception e)
            {
                // Never let a diagnostic break rendering — that would be a self-inflicted repeat
                // of the exact class of failure this whole ladder exists to avoid.
                dumpRequested = false;
                MelonLogger.Warning("Mask sandwich: could not reach renderGraph.debugParams " +
                    $"({e.GetType().Name}: {e.Message}). Dump and culling toggles are inert.");
            }
        }

        /// <summary>
        /// Describe a texture handle for the log, so the SAME texture can be compared across two
        /// pass recordings.
        ///
        /// Added 2026-08-27 to answer a question the ladder never asked: are the save and the
        /// restore even looking at the same `activeColorTexture`? URP ping-pongs between colour
        /// attachments, and if the handle we save FROM at one event is a different attachment than
        /// the one we restore INTO at the next, then nothing is broken at all — we are simply
        /// writing the right image into the wrong target, and every mechanism theorised so far is
        /// beside the point. The desc's own name (URP names these, e.g. _CameraColorAttachmentA/B)
        /// settles it in one line, with no VR guesswork.
        /// </summary>
        private static string DescribeActive(RenderGraph renderGraph, TextureHandle handle)
        {
            try
            {
                TextureDesc d = renderGraph.GetTextureDesc(ref handle);
                return $"'{d.name}' {d.width}x{d.height} slices={d.slices} {d.format} msaa={d.msaaSamples}";
            }
            catch (Exception e)
            {
                return $"<desc unavailable: {e.GetType().Name}>";
            }
        }

        /// <summary>
        /// Push the compilation-caching flag. Separate from the debugParams pair because it lives
        /// on the RenderGraph itself, and because it is the one write here that is not already
        /// proven safe by a previous session.
        /// </summary>
        private static void ApplyCompilationCaching(RenderGraph renderGraph)
        {
            if (cachingWriteFailed || disableCachingSetting == null) return;

            try
            {
                if (!cachingDefaultKnown)
                {
                    cachingDefault = renderGraph.m_EnableCompilationCaching;
                    cachingDefaultKnown = true;
                    MelonLogger.Msg("[diag] Mask sandwich: RenderGraph compilation caching was " +
                        $"{(cachingDefault ? "ON" : "off")} by default on this build.");
                }

                // Never fabricate a `true` — only ever AND our own "off" into whatever URP shipped.
                renderGraph.m_EnableCompilationCaching = cachingDefault && !disableCachingSetting.Value;
            }
            catch (Exception e)
            {
                // Latch off rather than retry: this runs twice a frame, and a warning per frame
                // would bury the very diag lines this ladder exists to read.
                cachingWriteFailed = true;
                MelonLogger.Warning("Mask sandwich: could not reach " +
                    $"RenderGraph.m_EnableCompilationCaching ({e.GetType().Name}: {e.Message}). " +
                    "The caching toggle is inert for this session; the other two are unaffected.");
            }
        }

        /// <summary>
        /// Re-arm the one-shot diagnostics when the rung changes, so every rung logs its own
        /// evidence instead of only the first one tried in a session getting a line.
        /// </summary>
        private static void NoteRung(int rung)
        {
            if (diagRung == rung) return;
            diagRung = rung;
            diagLogged = false;
            restoreDiagLogged = false;
            skipDiagLogged = false;
        }

        /// <summary>
        /// Enqueue the save/restore pair. Both or neither — a save without its restore would
        /// simply waste a blit, but a restore without its save is the one that could show a
        /// stale frame, so the frame guard above backs this up rather than replacing it.
        /// Rungs 1 and 2 still enqueue both; the restore pass simply records nothing, which
        /// keeps the enqueue path identical across all three rungs so it cannot itself be the
        /// difference a rung is measuring.
        /// </summary>
        public static void Enqueue(ScriptableRenderer renderer, RRSSandwichSavePass save,
                                   RRSSandwichRestorePass restore, RRSSandwichMaskPass mask)
        {
            if (failed || !Enabled || renderer == null || save == null || restore == null) return;

            try
            {
                // Rung 9 relocates BOTH halves so nothing of URP's sits between them. Set every
                // frame rather than once: the rung is locked per session, but this keeps the pass
                // objects honest even if that ever changes, and it costs two int writes.
                int r = Rung;
                bool step2 = r == 10 || r == 11;
                bool step3 = IsStep3(r);

                save.renderPassEvent = step3 ? Step3SaveEvent
                                     : step2 ? Rung10SaveEvent
                                     : r == 9 ? Rung9SaveEvent
                                     : RRSSandwichSavePass.Event;
                restore.renderPassEvent = step3 ? Step3RestoreEvent
                                        : step2 ? Rung10RestoreEvent
                                        : r == 9 ? Rung9RestoreEvent
                                        : RRSSandwichRestorePass.Event;
                activeSaveEvent = (int)save.renderPassEvent;
                activeRestoreEvent = (int)restore.renderPassEvent;

                renderer.EnqueuePass(save);

                // Only step 3 has a third slice. Enqueueing it on every rung would put an extra
                // pass in the graph for every other rung and quietly change what those rungs are
                // measuring — the whole ladder depends on each rung differing by ONE thing.
                if (step3 && mask != null)
                {
                    mask.renderPassEvent = Step3MaskEvent;
                    renderer.EnqueuePass(mask);
                }

                renderer.EnqueuePass(restore);
            }
            catch (Exception e)
            {
                failed = true;
                MelonLogger.Error("Mask sandwich failed and is now disabled for this session: " +
                    $"{e.GetType().Name}: {e.Message}\n{e.StackTrace}");
            }
        }

        /// <summary>
        /// Arm (or disarm) the category draws that sandwich step 2 brackets.
        ///
        /// ⚠ MUST be called from OnBeginCameraRendering BEFORE CategoryRecolor.Enqueue, because
        /// that is what reads these two statics. Ordering is the entire point of this file, so
        /// getting it wrong here would be a poor joke.
        ///
        /// Only rung 10 turns them on. Every other rung explicitly turns them OFF rather than
        /// leaving them as they were: CategoryRecolor's own toggles persist in MelonPreferences,
        /// and a category pass left running from an earlier session is precisely the trap that
        /// produced the first wrong diagnosis of this whole feature on 2026-08-06.
        /// </summary>
        public static void PrepareCategoryDraws()
        {
            int rung = Rung;
            bool step2 = rung == 10 || rung == 11;
            bool step3 = IsStep3(rung);

            if (failed || (!step2 && !step3))
            {
                CategoryRecolor.ForceAllForSandwich = false;
                CategoryRecolor.MaskColours = false;
                return;
            }

            // Before BuildDebugPasses: RenderObjectsPass takes its event in the constructor and
            // the built pass is cached for the session.
            CategoryRecolor.PassEvent = step3 ? Step3DrawEvent : Rung10DrawEvent;

            // Step 2 looks at the draws, so it uses the aesthetic colours a human can name.
            // Step 3 feeds a shader, so it uses channel ids. Same passes, same geometry.
            CategoryRecolor.MaskColours = step3;

            CategoryRecolor.BuildDebugPasses();
            CategoryRecolor.ForceAllForSandwich = true;
        }

        // ── called from the two injected passes ──────────────────────────────────────────────

        internal static void RecordSave(RenderGraph renderGraph, ContextContainer frameData)
        {
            if (failed) return;
            int rung = Rung;
            if (rung == 0) return;
            NoteRung(rung);
            ApplyDebugParams(renderGraph);

            UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
            if (resourceData == null) return;

            // Rung 4 does BOTH blits over in the restore pass at 301 — nothing happens here.
            if (rung == 4) return;

            TextureHandle activeColor = resourceData.activeColorTexture;
            if (!activeColor.IsValid()) return;

            // Rungs 5 and 6 both save into the mod-owned, imported texture instead of a transient
            // one. They differ ONLY in how the restore pass gets hold of it:
            //   5 — the restore calls ImportTexture again (a SECOND import of the same RTHandle)
            //   6 — the restore reuses the handle stashed here (ONE import for the whole frame)
            if (rung >= 5)
            {
                TextureDesc src = renderGraph.GetTextureDesc(ref activeColor);
                if (!EnsureOwnedTarget(src.width, src.height, src.slices, src.format)) return;

                TextureHandle target = renderGraph.ImportTexture(ownedHandle);

                // Rungs 7 and 8 are rung 5 plus one extra declaration each — see BlitNoCull.
                // Rung 9 is rung 5 with different EVENTS and nothing else — so it must not pick up
                // rungs 7/8's extra declarations. Exact matches, not >=.
                BlitNoCull(renderGraph, activeColor, target, "RRS Sandwich Save (owned)",
                           (rung == 7 || rung == 8) ? SandwichGlobalId : 0,
                           rung == 8);

                // ── STEP 3: BLANK THE FRAME, in this same recording ──────────────────────────
                //
                // The real frame is now safely in the owned target, so the camera colour is free
                // to become a scratch surface for the next two events. Blitting a cleared texture
                // over it is what makes every untagged pixel exactly zero in the mask.
                //
                // Deliberately AFTER the save blit and in the SAME recording: rungs 2 and 4 proved
                // two blits in one recording are fine, and doing it here means step 3 needs no
                // extra pass just to clear.
                if (IsStep3(rung))
                {
                    TextureDesc blankDesc = renderGraph.GetTextureDesc(ref activeColor);
                    blankDesc.msaaSamples = MSAASamples.None;
                    blankDesc.bindTextureMS = false;
                    blankDesc.depthBufferBits = DepthBits.None;
                    blankDesc.clearBuffer = true;
                    blankDesc.clearColor = Color.black;
                    blankDesc.name = "RRS_MaskBlank";

                    TextureHandle blank = renderGraph.CreateTexture(ref blankDesc);
                    BlitNoCull(renderGraph, blank, activeColor, "RRS Sandwich Blank (step 3)");
                }

                // Rung 6 carries the handle across, exactly as rung 3 does — the difference is
                // that what it points at is imported and therefore stable, not transient.
                if (rung == 6) saved = target;
                savedFrame = Time.frameCount;

                if (!diagLogged)
                {
                    diagLogged = true;
                    string extra = rung == 11 ? "STEP 2 PROOF — draws at 302, restore SUPPRESSED; expect VISIBLE flat category colours"
                                 : rung == 10 ? "STEP 2 — category draws bracketed at 302; run rung 11 FIRST to prove the draws land"
                                 : rung == 9 ? "events moved so nothing of URP's sits between the halves"
                                 : rung == 8 ? "global published + global-state-modification declared"
                                 : rung == 7 ? $"global published as _RRSSandwichSave (id {SandwichGlobalId})"
                                 : rung == 6 ? "handle stashed for the restore pass"
                                 : "restore will re-import";
                    MelonLogger.Msg($"[diag] Mask sandwich rung {rung} recording SAVE into the owned target " +
                        $"({extra}). save@{activeSaveEvent} restore@{activeRestoreEvent}. " +
                        $"RenderGraph debug flags: {DebugFlagState}. " +
                        $"activeColor AT SAVE: {DescribeActive(renderGraph, activeColor)}. " +
                        "Expect NO visible change.");
                }
                return;
            }

            Save(renderGraph, activeColor, rung, "RRS Sandwich Save");

            // Rung 1 stops here: a pure read of the camera image, nothing written back. If the
            // view changes anyway, the save is aliasing over live memory (see the ladder above).
            if (rung == 1) return;

            // Rung 2 restores inside THIS recording, so the handle never crosses a pass boundary.
            if (rung == 2) Restore(renderGraph, activeColor, "RRS Sandwich Restore (same pass)");
        }

        /// <summary>
        /// A save blit that RenderGraph is forbidden to cull.
        ///
        /// This is the missing declaration, and very likely the whole bug. RenderGraph culls any
        /// pass whose output nothing reads — and every SAVE we do is read by a pass in a DIFFERENT
        /// recording, which its culling analysis cannot see. From inside the save's own recording
        /// the blit looks like dead work, so it is dropped, and the restore then faithfully paints
        /// an untouched (black) texture over the world. `AllowPassCulling(false)` says "keep it".
        ///
        /// It also explains the non-determinism: whether a cullable pass actually gets dropped
        /// depends on the rest of the graph, which differs per session — so the same rung can look
        /// clean one launch and black the next, which is exactly what happened to rungs 3 and 4.
        ///
        /// Data only — `returnBuilder` plus two setter calls. No SetRenderFunc, no PassData, no
        /// delegate. THE RULE is untouched.
        /// </summary>
        private static void BlitNoCull(RenderGraph renderGraph, TextureHandle source,
                                       TextureHandle destination, string passName,
                                       int publishGlobalId = 0,
                                       bool declareGlobalStateModification = false)
        {
            IBaseRenderGraphBuilder builder = RGUtils.AddBlitPass(renderGraph, source, destination,
                Vector2.one, Vector2.zero, 0, 0, -1, 0, 0, 1, RGUtils.BlitFilterMode.ClampBilinear,
                passName, true, "", 0);

            if (builder == null) return;

            builder.AllowPassCulling(false);

            // ── rung 7 ────────────────────────────────────────────────────────────────────────
            // Publish the destination as a GLOBAL texture after this pass.
            //
            // Two reasons, and the second is why this is not a detour:
            //
            //   1. It gives the pass an observable SIDE EFFECT. `AllowPassCulling(false)` above
            //      is a request the compiler is free to reason around; a pass that binds a global
            //      other passes may sample has produced something the compiler must account for.
            //   2. It is the actual step-3 mechanism from this file's own header — "blit camera
            //      colour -> _RRSCategoryMask, published as a global". So rung 7 tests the
            //      shipping design rather than a stand-in for it.
            if (publishGlobalId != 0)
                builder.SetGlobalTextureAfterPass(ref destination, publishGlobalId);

            // ── rung 8 ────────────────────────────────────────────────────────────────────────
            // Say it outright: this pass modifies global state. This is the declaration URP's own
            // passes use for exactly this situation, and it is the strongest "do not treat me as
            // dead work" statement the builder offers. Kept as its own rung so that if 7 fails and
            // 8 passes, we know which declaration did the work — one rung, one change.
            if (declareGlobalStateModification)
                builder.AllowGlobalStateModification(true);

            // Same disposal idiom RRSRenderPass already uses for its material blit builder.
            builder.Cast<Il2CppSystem.IDisposable>().Dispose();
        }

        /// <summary>
        /// Allocate (or re-allocate) rung 5's mod-owned save target. Returns false if the texture
        /// could not be created, in which case rung 5 simply does nothing rather than blitting
        /// into a null handle.
        /// </summary>
        /// <summary>
        /// Allocate the mask target. Deliberately a near-copy of EnsureOwnedTarget rather than a
        /// shared helper: the two targets have the same shape today but different jobs, and the
        /// mask's format is the one most likely to diverge first (a packed mask wants exact
        /// values, so an sRGB colour format is already a question mark — see the note below).
        /// </summary>
        private static bool EnsureMaskTarget(int width, int height, int slices, GraphicsFormat format)
        {
            slices = Mathf.Max(1, slices);

            if (maskRT != null && maskRT.IsCreated() && maskW == width && maskH == height
                && maskSlices == slices && maskFormat == format)
                return true;

            ReleaseMaskTarget();

            try
            {
                maskRT = new RenderTexture(width, height, 0, format);
                maskRT.name = "RRS_CategoryMask";
                maskRT.dimension = slices > 1 ? TextureDimension.Tex2DArray : TextureDimension.Tex2D;
                maskRT.volumeDepth = slices;
                maskRT.antiAliasing = 1;
                maskRT.useMipMap = false;
                maskRT.autoGenerateMips = false;

                if (!maskRT.Create())
                {
                    MelonLogger.Error("Mask sandwich step 3: mask RenderTexture.Create() returned " +
                        "false. Step 3 is unavailable; the other rungs are unaffected.");
                    ReleaseMaskTarget();
                    return false;
                }

                maskHandle = RTHandles.Alloc(maskRT, false);
            }
            catch (Exception e)
            {
                MelonLogger.Error("Mask sandwich step 3 could not allocate its mask target: " +
                    $"{e.GetType().Name}: {e.Message}");
                ReleaseMaskTarget();
                return false;
            }

            maskW = width;
            maskH = height;
            maskSlices = slices;
            maskFormat = format;

            // ⚠ Worth reading when the mask starts decoding oddly in a shader: this inherits the
            // camera colour's format, which on this build is R8G8B8A8_**SRGB**. A mask is data,
            // not colour, so an sRGB transfer curve on the way in and out is a real hazard for
            // exact comparisons. It round-trips consistently today, but if a pack ever finds that
            // "R == 1" does not hold exactly, this line is the first suspect.
            MelonLogger.Msg($"[diag] Mask sandwich step 3 allocated the mask target: " +
                $"{width}x{height} slices={slices} {format}, dimension={maskRT.dimension}, msaa=1.");
            return true;
        }

        private static void ReleaseMaskTarget()
        {
            try { if (maskHandle != null) RTHandles.Release(maskHandle); }
            catch (Exception e) { MelonLogger.Warning($"Mask sandwich: mask RTHandle release failed ({e.GetType().Name})."); }
            maskHandle = null;

            if (maskRT != null)
            {
                try { maskRT.Release(); } catch { }
                try { UnityEngine.Object.Destroy(maskRT); } catch { }
                maskRT = null;
            }

            maskW = maskH = maskSlices = 0;
        }

        private static bool EnsureOwnedTarget(int width, int height, int slices, GraphicsFormat format)
        {
            slices = Mathf.Max(1, slices);

            if (ownedRT != null && ownedRT.IsCreated() && ownedW == width && ownedH == height
                && ownedSlices == slices && ownedFormat == format)
                return true;

            ReleaseOwnedTarget();

            try
            {
                // depth 0: this is a colour copy, and a depth buffer on it would only invite the
                // same MSAA/resolve trouble the transient path already avoids.
                ownedRT = new RenderTexture(width, height, 0, format);
                ownedRT.name = "RRS_SandwichSaveOwned";
                // VR renders both eyes into a Tex2DArray; a plain Tex2D here would silently save
                // one eye and restore it to both.
                ownedRT.dimension = slices > 1 ? TextureDimension.Tex2DArray : TextureDimension.Tex2D;
                ownedRT.volumeDepth = slices;
                ownedRT.antiAliasing = 1;   // you cannot SAMPLE an MSAA texture; VR runs MSAA8x
                ownedRT.useMipMap = false;
                ownedRT.autoGenerateMips = false;

                if (!ownedRT.Create())
                {
                    MelonLogger.Error("Mask sandwich rung 5: RenderTexture.Create() returned false. " +
                        "Rung 5 is unavailable; the other rungs are unaffected.");
                    ReleaseOwnedTarget();
                    return false;
                }

                ownedHandle = RTHandles.Alloc(ownedRT, false);   // false = we keep ownership
            }
            catch (Exception e)
            {
                MelonLogger.Error("Mask sandwich rung 5 could not allocate its owned target: " +
                    $"{e.GetType().Name}: {e.Message}");
                ReleaseOwnedTarget();
                return false;
            }

            ownedW = width;
            ownedH = height;
            ownedSlices = slices;
            ownedFormat = format;

            // Was hard-coded to "rung 5" and printed "rung 5" during a rung 9 run (2026-08-27).
            // Cosmetic, but a log line that names the wrong rung is exactly the kind of thing that
            // gets believed later, and this file has been burned by that more than once.
            MelonLogger.Msg($"[diag] Mask sandwich rung {Rung} allocated an owned save target: " +
                $"{width}x{height} slices={slices} {format}, dimension={ownedRT.dimension}, msaa=1.");
            return true;
        }

        private static void ReleaseOwnedTarget()
        {
            try { if (ownedHandle != null) RTHandles.Release(ownedHandle); }
            catch (Exception e) { MelonLogger.Warning($"Mask sandwich: RTHandle release failed ({e.GetType().Name})."); }
            ownedHandle = null;

            if (ownedRT != null)
            {
                try { ownedRT.Release(); UnityEngine.Object.Destroy(ownedRT); }
                catch (Exception e) { MelonLogger.Warning($"Mask sandwich: RenderTexture release failed ({e.GetType().Name})."); }
                ownedRT = null;
            }

            ownedW = ownedH = ownedSlices = 0;
        }

        /// <summary>
        /// The save blit, shared by the rungs that save at 299 and by rung 4, which saves at 301.
        /// Keeping it in one place is what lets rung 4 differ from rung 2 by the EVENT alone.
        /// </summary>
        private static void Save(RenderGraph renderGraph, TextureHandle activeColor, int rung, string passName)
        {
            // Same descriptor surgery RRSRenderPass does for its per-pack copy: single-sample
            // and colour-only, so the result is something a blit can read back out of.
            TextureDesc desc = renderGraph.GetTextureDesc(ref activeColor);
            desc.msaaSamples = MSAASamples.None;
            desc.bindTextureMS = false;
            desc.depthBufferBits = DepthBits.None;
            desc.clearBuffer = false;
            desc.name = "RRS_SandwichSave";

            saved = renderGraph.CreateTexture(ref desc);
            BlitNoCull(renderGraph, activeColor, saved, passName);
            savedFrame = Time.frameCount;

            if (!diagLogged)
            {
                diagLogged = true;
                MelonLogger.Msg($"[diag] Mask sandwich rung {rung} recording SAVE ('{passName}'). " +
                    $"save@{(int)RRSSandwichSavePass.Event} restore@{(int)RRSSandwichRestorePass.Event} " +
                    $"(AfterRenderingOpaques={(int)RenderPassEvent.AfterRenderingOpaques}); " +
                    $"saved {desc.width}x{desc.height} slices={desc.slices} msaa={desc.msaaSamples}. " +
                    "Expect NO visible change on every rung.");
            }
        }

        /// <summary>
        /// STEP 3, event 303: copy the drawn categories into the mask target and publish it as the
        /// `_RRSCategoryMask` global so packs can sample it at event 500.
        ///
        /// At this point the camera colour holds the blanked frame with the category draws on top —
        /// i.e. the mask itself. One blit takes a sampleable, single-sample copy of it.
        ///
        /// The global is published from THIS pass, never from the save. Rung 7 suggested
        /// SetGlobalTextureAfterPass on the save pass is actively harmful; that comparison was
        /// contaminated and is owed a clean re-test, but there was never a reason to put it on the
        /// save — the save's output is read back by the restore, and this one is not.
        /// </summary>
        internal static void RecordMask(RenderGraph renderGraph, ContextContainer frameData)
        {
            if (failed) return;
            int rung = Rung;
            if (!IsStep3(rung)) return;

            ApplyDebugParams(renderGraph);

            UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
            if (resourceData == null) return;

            TextureHandle activeColor = resourceData.activeColorTexture;
            if (!activeColor.IsValid()) return;

            TextureDesc src = renderGraph.GetTextureDesc(ref activeColor);
            if (!EnsureMaskTarget(src.width, src.height, src.slices, src.format)) return;

            TextureHandle maskTarget = renderGraph.ImportTexture(maskHandle);

            // Publish as a global AFTER this pass: the consumers (packs at event 500) are later in
            // the same recording, and RRSRenderPass already calls UseAllGlobalTextures(true), so
            // it will see it without any change on that side.
            BlitNoCull(renderGraph, activeColor, maskTarget, "RRS Sandwich Mask Copy",
                       MaskGlobalId);

            if (!maskDiagLogged)
            {
                maskDiagLogged = true;
                MelonLogger.Msg($"[diag] Mask sandwich rung {rung} recorded MASK COPY at " +
                    $"{(int)Step3MaskEvent} and published it as _RRSCategoryMask " +
                    $"(id {MaskGlobalId}). activeColor AT MASK: {DescribeActive(renderGraph, activeColor)}. " +
                    "Encoding: R=map G=players B=structures R+G=effects, black=untagged.");
            }
        }

        internal static void RecordRestore(RenderGraph renderGraph, ContextContainer frameData)
        {
            if (failed) return;
            int rung = Rung;
            if (rung == 0) return;
            // The dump has to be armable from this pass too: at rung 4 the save pass records
            // nothing, so arming only there would silently never fire.
            ApplyDebugParams(renderGraph);

            // Rungs 1 and 2 do not use the second pass for work — 1 never restores, 2 already
            // restored inside the save recording.
            //
            // ⚠ 2026-08-06: rung 6 was MISSING from this list when it was first tested. It fell
            // out here before reaching its branch below, so the restore never recorded and rung 6
            // silently behaved as rung 1 (save only) — which is exactly why it "passed" three
            // times. Its clean results were void.
            //
            // 2026-08-27: that guard is now INVERTED so the bug cannot recur. It lists only the
            // rungs that deliberately record nothing here; every other rung — including any added
            // later — falls through and reaches its branch. A new rung can no longer be silently
            // demoted to save-only by being forgotten in a whitelist.
            // Rung 11 is rung 10 with the restore deliberately suppressed, so the category draws
            // stay on screen and can be SEEN. See the rung 11 block at the top of this file.
            if (rung == 1 || rung == 2 || rung == 11 || rung == 13)
            {
                if (rung == 11 && !restoreDiagLogged)
                {
                    restoreDiagLogged = true;
                    MelonLogger.Msg("[diag] Mask sandwich rung 11: restore deliberately SKIPPED. " +
                        "The category draws should be VISIBLE — map black, players grey, " +
                        "structures white, effects magenta. If the view is unchanged, the " +
                        "renderers are not tagged: press 'Tag renderer categories'.");
                }
                else if (rung == 13 && !restoreDiagLogged)
                {
                    restoreDiagLogged = true;
                    MelonLogger.Msg("[diag] Mask sandwich rung 13: restore deliberately SKIPPED — " +
                        "YOU ARE LOOKING AT THE MASK ITSELF. Expect a BLACK world with flat RED " +
                        "map, GREEN players and BLUE structures, and nothing else. Any real " +
                        "scene detail still visible means the blank blit did not land; any " +
                        "colour outside pure R/G/B means the mask is not decodable.");
                }
                return;
            }
            NoteRung(rung);

            UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
            if (resourceData == null) return;

            TextureHandle activeColor = resourceData.activeColorTexture;
            if (!activeColor.IsValid()) return;

            // Rung 4: save AND restore right here, so this recording differs from rung 2 by the
            // EVENT only (301 instead of 299) and by nothing else. See the ladder above.
            if (rung == 4)
            {
                Save(renderGraph, activeColor, rung, "RRS Sandwich Save (restore pass)");
                Restore(renderGraph, activeColor, "RRS Sandwich Restore (same pass, @301)");
                return;
            }

            // Rungs 5 and 6: read back out of the mod-owned texture. The same-frame guard matters
            // MORE here than for rung 3 — the owned target persists across frames, so a dropped
            // save would restore LAST frame's image, a far more confusing artefact than black.
            if (rung >= 5)
            {
                if (ownedHandle == null || savedFrame != Time.frameCount)
                {
                    if (!skipDiagLogged)
                    {
                        skipDiagLogged = true;
                        MelonLogger.Warning($"[diag] Mask sandwich rung {rung} SKIPPED the restore: " +
                            $"ownedHandle={(ownedHandle == null ? "null" : "ok")} " +
                            $"savedFrame={savedFrame} Time.frameCount={Time.frameCount}.");
                    }
                    return;
                }

                // THE one difference between the rungs: where the restore gets its source handle.
                //
                // ⚠ THE PARAGRAPH THAT USED TO SIT HERE WAS WRONG, AND IT WAS WRONG CONFIDENTLY.
                // It argued that rung 5's second ImportTexture creates two independent resource
                // entries with no write-before-read dependency, so rung 5 would be culled while
                // rung 6's single stashed handle would be honoured. Measured 2026-08-27, with the
                // global pass-culling flag off then on:
                //
                //     rung 5 -> CLEAN      rung 6 -> BLACK
                //
                // Exactly backwards. Re-importing the same RTHandle is fine; carrying a
                // TextureHandle between two pass recordings is not, imported or transient. Rung 6
                // is retained ONLY so the rung numbers keep matching the docs and the logs — it is
                // a KNOWN-BAD arrangement, not a candidate. Do not spend time repairing it.
                //
                // What the black actually looked like is worth keeping, because it is a signature
                // worth recognising: the world black, the sky and the particles untouched. That is
                // a restore at 301 faithfully painting an EMPTY texture — the skybox (350) and the
                // transparents (450+) are drawn after us and so survive. A black world with a
                // correct sky means "the save never wrote", never "the restore never ran".
                TextureHandle source = rung == 6 ? saved : renderGraph.ImportTexture(ownedHandle);
                if (rung == 6 && !source.IsValid())
                {
                    if (!skipDiagLogged)
                    {
                        skipDiagLogged = true;
                        MelonLogger.Warning("[diag] Mask sandwich rung 6: the stashed handle is invalid; " +
                            "a TextureHandle does not survive between recordings even when imported.");
                    }
                    return;
                }

                RGUtils.AddBlitPass(renderGraph, source, activeColor, Vector2.one, Vector2.zero,
                    0, 0, -1, 0, 0, 1, RGUtils.BlitFilterMode.ClampBilinear,
                    "RRS Sandwich Restore (owned)", false, "", 0);
                savedFrame = -1;

                if (!restoreDiagLogged)
                {
                    restoreDiagLogged = true;
                    MelonLogger.Msg($"[diag] Mask sandwich rung {rung} recorded RESTORE from the owned target " +
                        $"({(rung == 6 ? "stashed handle" : "re-imported")}). " +
                        $"RenderGraph debug flags: {DebugFlagState}. " +
                        $"activeColor AT RESTORE: {DescribeActive(renderGraph, activeColor)}. " +
                        "⚠ COMPARE THIS WITH THE 'activeColor AT SAVE' LINE ABOVE — if the name " +
                        "or format differs, the two passes are not looking at the same target and " +
                        "that alone explains the black.");
                }
                return;
            }

            // Refuse anything that did not come from this frame's save — see savedFrame above.
            // This is also the single most valuable diagnostic on the ladder: if rung 3 is
            // skipping here, the save and restore are not recording in the same frame and the
            // black view is a restore that never ran, not a restore that ran wrong.
            if (savedFrame != Time.frameCount || !saved.IsValid())
            {
                if (!skipDiagLogged)
                {
                    skipDiagLogged = true;
                    MelonLogger.Warning($"[diag] Mask sandwich rung 3 SKIPPED the restore: " +
                        $"savedFrame={savedFrame} Time.frameCount={Time.frameCount} " +
                        $"savedValid={saved.IsValid()}. The save and restore passes are not " +
                        "recording in the same frame — that, not the blit, is the bug.");
                }
                return;
            }

            Restore(renderGraph, activeColor, "RRS Sandwich Restore");
        }

        /// <summary>
        /// The restore blit, shared by rung 2 (same recording) and rung 3 (second pass) so the
        /// two rungs differ by exactly one thing — where it is recorded — and nothing else.
        /// </summary>
        private static void Restore(RenderGraph renderGraph, TextureHandle activeColor, string passName)
        {
            RGUtils.AddBlitPass(renderGraph, saved, activeColor, Vector2.one, Vector2.zero,
                0, 0, -1, 0, 0, 1, RGUtils.BlitFilterMode.ClampBilinear, passName, false, "", 0);

            // Consumed. Belt-and-braces on top of the frame check: a handle is used exactly once.
            savedFrame = -1;

            if (!restoreDiagLogged)
            {
                restoreDiagLogged = true;
                MelonLogger.Msg($"[diag] Mask sandwich rung {Rung} recorded RESTORE ('{passName}').");
            }
        }
    }

    // Two separate injected types rather than one type with a mode field, on purpose: instance
    // fields on an Il2Cpp-injected class are their own source of interop trouble, and the passes
    // need different renderPassEvents anyway. All state lives in the managed static above, the
    // same shape RRSRenderPass already uses to reach RumbleReShadedMod.Packs.
    public class RRSSandwichSavePass : ScriptableRenderPass
    {
        // AfterRenderingOpaques - 1. URP orders its queue by the raw renderPassEvent int, so an
        // offset is how a mod expresses "just before/after" a stock injection point. Landing
        // either side of CategoryRecolor's passes (which sit exactly on AfterRenderingOpaques) is
        // the entire thing this step exists to verify.
        public const RenderPassEvent Event = (RenderPassEvent)((int)RenderPassEvent.AfterRenderingOpaques - 1);

        public RRSSandwichSavePass(IntPtr ptr) : base(ptr) { }
        public RRSSandwichSavePass() : base(ClassInjector.DerivedConstructorPointer<RRSSandwichSavePass>())
            => ClassInjector.DerivedConstructorBody(this);

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            => MaskSandwich.RecordSave(renderGraph, frameData);
    }

    /// <summary>
    /// STEP 3's third slice: copies the drawn categories into the mask target and publishes the
    /// `_RRSCategoryMask` global. Only enqueued on rungs 12 and 13 — see MaskSandwich.Enqueue.
    /// </summary>
    public class RRSSandwichMaskPass : ScriptableRenderPass
    {
        // AfterRenderingOpaques + 3 — between the category draws (302) and the restore (304).
        public const RenderPassEvent Event = (RenderPassEvent)((int)RenderPassEvent.AfterRenderingOpaques + 3);

        public RRSSandwichMaskPass(IntPtr ptr) : base(ptr) { }
        public RRSSandwichMaskPass() : base(ClassInjector.DerivedConstructorPointer<RRSSandwichMaskPass>())
            => ClassInjector.DerivedConstructorBody(this);

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            => MaskSandwich.RecordMask(renderGraph, frameData);
    }

    public class RRSSandwichRestorePass : ScriptableRenderPass
    {
        // AfterRenderingOpaques + 1 — the far slice of the sandwich.
        public const RenderPassEvent Event = (RenderPassEvent)((int)RenderPassEvent.AfterRenderingOpaques + 1);

        public RRSSandwichRestorePass(IntPtr ptr) : base(ptr) { }
        public RRSSandwichRestorePass() : base(ClassInjector.DerivedConstructorPointer<RRSSandwichRestorePass>())
            => ClassInjector.DerivedConstructorBody(this);

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            => MaskSandwich.RecordRestore(renderGraph, frameData);
    }
}
