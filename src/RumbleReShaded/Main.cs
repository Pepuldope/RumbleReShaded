using System;
using System.Collections.Generic;
using System.IO;
using MelonLoader;
using MelonLoader.Utils;
using UIFramework;
using UIFramework.UiExtensions;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.RenderGraphModule;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Injection;
using RGUtils = UnityEngine.Rendering.RenderGraphModule.Util.RenderGraphUtils;

namespace RumbleReShaded
{
    public static class BuildInfo
    {
        public const string ModName = "RumbleReShaded";
        // CANONICAL version (flows into MelonInfo → what Lum reads to detect "outdated").
        // Dev/test builds shared on Discord/GitHub carry a "-dev" prerelease suffix; SemVer
        // sorts e.g. "1.2.0-dev" BELOW the clean "1.2.0", so once the tested stable release
        // ships, Lum flags every dev build as outdated. Stable release = drop the suffix.
        public const string ModVersion = "1.2.0-dev.1";
        public const string Description = "Loads community shader packs from UserData and applies them as VR-safe post effects. Make your own with the RumbleShade template!";
        public const string Author = "Pepuldo";
    }

    // A real URP render pass injected into the pipeline. It runs after the camera
    // has drawn opaque + transparents, reads the camera's active colour texture
    // (which therefore INCLUDES particles and world-space transparents), and blits
    // it back through each enabled pack's material. This is the only place a write
    // actually reaches the presented HMD image — a CommandBuffer in the global
    // endCameraRendering callback writes a backbuffer the XR plug-in never presents.
    public class RRSRenderPass : ScriptableRenderPass
    {
        public RRSRenderPass(IntPtr ptr) : base(ptr) { }
        public RRSRenderPass() : base(ClassInjector.DerivedConstructorPointer<RRSRenderPass>())
            => ClassInjector.DerivedConstructorBody(this);

        // Everything below runs inside URP's graph recording, which is called from native
        // code — an exception escaping here does not surface as a normal managed error, it
        // takes the game down. TacoSlayer hit exactly that on 2026-09-03 (ArgumentOutOfRange
        // out of TextureDesc.CalculateFinalDimensions, before the gym loaded), and because
        // both the master switch and every pack default to ON, the crash landed before he
        // could ever reach the menu to turn it off. So: catch, report once, and stand the
        // whole mod down for the session rather than letting a bad frame end the process.
        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            if (RumbleReShadedMod.RenderFailed) return;
            try
            {
                Record(renderGraph, frameData);
            }
            catch (Exception e)
            {
                RumbleReShadedMod.ReportRenderFailure("RecordRenderGraph threw while building the pack passes.", e);
            }
        }

        private void Record(RenderGraph renderGraph, ContextContainer frameData)
        {
            UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
            if (resourceData == null) return;

            TextureHandle activeColor = resourceData.activeColorTexture;
            if (!activeColor.IsValid()) return;

            if (RumbleReShadedMod.DebugSolidFill)
            {
                // No material/shader involved: create a cleared (black) texture with
                // the same descriptor and copy it over the active colour. If the view
                // goes black, injection + targeting + presentation all work, and the
                // only remaining work is the shader/blit convention.
                TextureHandle black = renderGraph.CreateTexture(activeColor, "RRS_DebugBlack", true);
                RGUtils.AddCopyPass(renderGraph, black, activeColor, "RRS Debug Black", false, "", 0);
                if (!RumbleReShadedMod.passDiagLogged)
                {
                    RumbleReShadedMod.passDiagLogged = true;
                    MelonLogger.Msg("[diag] RRSRenderPass.RecordRenderGraph ran (DebugSolidFill): copied black over activeColorTexture.");
                }
                return;
            }

            // A sampleable copy of the live image: same descriptor as the active
            // colour but forced to single-sample (you cannot SAMPLE an MSAA texture,
            // and VR commonly runs MSAA) and colour-only.
            TextureDesc desc = renderGraph.GetTextureDesc(ref activeColor);
            desc.msaaSamples = MSAASamples.None;
            desc.bindTextureMS = false;
            desc.depthBufferBits = DepthBits.None;
            desc.clearBuffer = false;
            desc.name = "RRS_Copy";

            // Intermediate buffer for multi-pass packs. Forced to a 4-channel half
            // format because the camera colour in VR may have no usable alpha
            // (R11G11B10 has none at all), and the whole point of an intermediate is
            // that a pass can stash a working value — an AO factor, a mask — in the
            // spare channel for the next pass to read. Half float also stops a
            // multi-pass chain from banding the way an 8-bit intermediate would.
            //
            // Fetched fresh rather than copied from `desc` on purpose: TextureDesc's
            // value-vs-reference semantics through Il2Cpp interop are not something to
            // assume. If it came through as a reference, `interDesc = desc` would alias
            // and setting .format here would silently retarget the MSAA-resolve copy
            // too — a bug that would only show up as a subtle format mismatch.
            TextureDesc interDesc = renderGraph.GetTextureDesc(ref activeColor);
            interDesc.msaaSamples = MSAASamples.None;
            interDesc.bindTextureMS = false;
            interDesc.depthBufferBits = DepthBits.None;
            interDesc.clearBuffer = false;
            interDesc.format = GraphicsFormat.R16G16B16A16_SFloat;
            interDesc.name = "RRS_Inter";

            if (!RumbleReShadedMod.passDiagLogged)
            {
                RumbleReShadedMod.passDiagLogged = true;
                MelonLogger.Msg($"[diag] RRSRenderPass: activeColor {desc.width}x{desc.height} dim={desc.dimension} " +
                    $"slices={desc.slices} srcMSAA={renderGraph.GetTextureDesc(ref activeColor).msaaSamples}.");
            }

            // See RumbleReShadedMod.ForceRenderFailure. Thrown here, after the diag line and
            // before the first CreateTexture, so it lands as close as possible to where the
            // real ArgumentOutOfRangeException came from.
            if (RumbleReShadedMod.ForceRenderFailure)
                throw new InvalidOperationException(
                    "Forced render failure — ForceRenderFailure is true in this build. " +
                    "This is a deliberate test of the self-disable, NOT a real fault.");

            // Sanity-check the descriptor we are about to hand RenderGraph. This is the
            // narrow, named form of the crash above: CalculateFinalDimensions is what
            // throws when a descriptor's width/height/slices are not usable, and it is
            // reached from AddBlitPass, not from anything we can see going wrong here.
            // Bailing out of the frame is invisible (no effect for one frame); throwing is
            // fatal — so check rather than find out. Not a guess at the root cause: the
            // numbers go into the log so the real reason is recoverable from a user report.
            if (desc.width <= 0 || desc.height <= 0 || desc.slices <= 0)
            {
                RumbleReShadedMod.ReportRenderFailure(
                    $"the camera colour target has an unusable descriptor: {desc.width}x{desc.height}, " +
                    $"slices={desc.slices}, dim={desc.dimension}. Nothing was drawn.", null);
                return;
            }

            int applied = 0;
            foreach (ShaderPack pack in RumbleReShadedMod.Packs)
            {
                if (!pack.IsEnabled || !pack.EnsureLoaded()) continue;
                // Push the current slider values into the material every frame, so
                // dragging a slider in the UIFramework menu updates the look live
                // (no settings-save callback needed).
                pack.ApplyParameters();

                // Can't sample the texture we render into, so copy (resolving MSAA via
                // a fragment blit) the live image to a temp, then blit the temp back
                // through the material into the active colour. Blitter binds the source
                // to "_BlitTexture" and draws a procedural fullscreen triangle, so pack
                // shaders must use the URP Blit convention (Blit.hlsl / _BlitTexture).
                TextureHandle source = renderGraph.CreateTexture(ref desc);
                RGUtils.AddBlitPass(renderGraph, activeColor, source, Vector2.one, Vector2.zero,
                    0, 0, -1, 0, 0, 1, RGUtils.BlitFilterMode.ClampBilinear, "RRS Copy " + pack.Name, false, "", 0);

                // Multi-pass packs chain material passes 0..N-1, each reading what the
                // previous one wrote. Only the LAST pass targets the camera colour;
                // everything before it lands in an intermediate we own. A single-pass
                // pack (the overwhelming majority) runs exactly one iteration and
                // behaves identically to how it always has.
                for (int p = 0; p < pack.Passes; p++)
                {
                    bool isLast = p == pack.Passes - 1;
                    // Intermediates keep alpha (see interDesc) so a pass can hand the
                    // next one a spare channel — that's how ContactShade passes its raw
                    // AO through to the blur without a second texture.
                    TextureHandle target = isLast ? activeColor : renderGraph.CreateTexture(ref interDesc);

                    RGUtils.BlitMaterialParameters bmp =
                        new RGUtils.BlitMaterialParameters(source, target, pack.Material, p);
                    // returnBuilder:true so we can grant the pass access to URP global
                    // textures (e.g. _CameraDepthTexture for depth-based effects); the
                    // builder must be disposed to finalise the pass.
                    string passName = pack.Passes > 1 ? $"RRS {pack.Name} pass {p}" : "RRS " + pack.Name;
                    IBaseRenderGraphBuilder builder = RGUtils.AddBlitPass(renderGraph, bmp, passName, true, "", 0);
                    builder.UseAllGlobalTextures(true);
                    builder.Cast<Il2CppSystem.IDisposable>().Dispose();

                    // The texture we just wrote becomes the next pass's input. No extra
                    // copy needed: intermediates are already single-sample and sampleable,
                    // unlike activeColor.
                    source = target;
                }
                applied++;
            }
        }
    }

    public class RumbleReShadedMod : MelonMod
    {
        private static MelonPreferences_Entry<bool> masterEnabled;

        private static readonly List<ShaderPack> packs = new List<ShaderPack>();
        public static IReadOnlyList<ShaderPack> Packs => packs;

        private static bool hooked;
        private static RRSRenderPass pass;
        private static RRSMaskPass maskPass;
        private static RRSSandwichSavePass sandwichSave;
        private static RRSSandwichRestorePass sandwichRestore;
        private static RRSSandwichMaskPass sandwichMask;
        // maskDebugMode (the Stage 3 RenderGraph ladder slider) was removed 2026-08-06 along with
        // its menu entry — the route it drove is hard-disabled and the field had no readers left.
        internal static bool passDiagLogged;
        private static bool enqueueDiagLogged;

        // Tripped when anything in our render path throws. While set, nothing is enqueued and
        // the pass records nothing, so the game runs exactly as if the mod were switched off.
        // Deliberately NOT persisted: a hardware/pipeline hiccup should not permanently write
        // itself into the user's config. Cleared automatically on a scene load, up to
        // MaxRenderAttempts times — see OnSceneWasInitialized.
        internal static bool RenderFailed { get; private set; }



        // How many times the pass is allowed to fail before it stays off for good. A failure can
        // be transient — an odd camera state for one frame, a resolution or graphics-setting
        // change mid-session — and those heal on their own, so standing down permanently on the
        // first one is too harsh. A genuinely broken build fails every attempt and gives up after
        // three, which bounds the log at three reports instead of one per eye per frame forever.
        private const int MaxRenderAttempts = 3;

        private static int renderFailureCount;

        // One report per failure, then silence — this can be reached once per eye per frame,
        // and a log spammed at that rate is worse than useless for diagnosing it afterwards.
        internal static void ReportRenderFailure(string what, Exception e)
        {
            if (RenderFailed) return;
            RenderFailed = true;
            renderFailureCount++;

            bool willRetry = renderFailureCount < MaxRenderAttempts;
            MelonLogger.Error($"RumbleReShaded has switched its render pass off ({renderFailureCount}/{MaxRenderAttempts}) — " + what);
            if (e != null) MelonLogger.Error(e.ToString());

            if (willRetry)
            {
                MelonLogger.Warning("The game keeps running with no shader effects, and the pass will retry by " +
                    "itself on the next scene load. Nothing to do.");
            }
            else
            {
                MelonLogger.Warning($"That was attempt {MaxRenderAttempts} of {MaxRenderAttempts}, so the pass now " +
                    "stays off until you restart the game. It keeps running with no shader effects. To stop it " +
                    "trying at all, set Enabled = false under [RumbleReShaded] in UserData/MelonPreferences.cfg. " +
                    "Please report this with your MelonLoader/Latest.log.");
            }
        }

        // The automatic half of recovery. A scene load is the natural retry point: it is when the
        // camera, the render targets and the XR state are all rebuilt, so it is exactly the moment
        // a transient fault is most likely to be gone. Deliberately NOT a per-frame or timer-based
        // retry — that would re-enter the failing path hundreds of times a second and turn one
        // logged fault into a stutter.
        public override void OnSceneWasInitialized(int buildIndex, string sceneName)
        {
            if (!RenderFailed || renderFailureCount >= MaxRenderAttempts) return;

            RenderFailed = false;
            // Let the next successful recording print its descriptor again. On a retry that line
            // is the whole diagnosis — if the numbers differ between attempts, the fault is a
            // changing camera target rather than anything in the pack path.
            passDiagLogged = false;
            MelonLogger.Msg($"RumbleReShaded: retrying the render pass after loading '{sceneName}' " +
                $"(attempt {renderFailureCount + 1} of {MaxRenderAttempts}).");
        }

        // Gate for the public RumbleReShaded.Api surface. A consumer mod with an unlucky
        // load order can call RRS.Fire() before we are set up; that must no-op, not NRE
        // inside someone else's mod.
        internal static bool ApiReady;

        // Last main-camera position seen, used only by the "Fire test event" debug
        // button so it has somewhere sensible to fire at.
        private static Vector3 lastCameraPos;

        // TEMP DIAGNOSTIC: see RRSRenderPass.RecordRenderGraph. While true the pass
        // copies a black texture over the camera colour instead of running packs, to
        // prove the injection reaches the HMD. Set false for the real effect.
        public const bool DebugSolidFill = false;

        // ⚠ TEMPORARY TEST SCAFFOLD (2026-09-04) — MUST be false in any published build.
        // While true, the pack pass throws on its first recording, at the same point in the
        // frame the real crash reached. It exists because the self-disable above cannot
        // otherwise be tested: it only runs on a failure that does not reproduce on this
        // machine, and an untested crash handler is exactly the thing that fails when it is
        // finally needed. Expected result: the game KEEPS RUNNING with no effects, the log
        // carries one error plus the recovery advice, the menu is reachable, and flicking
        // master Enabled off then on retries (and throws again — that is correct).
        public const bool ForceRenderFailure = false;

        // Packs live in this mod's own UserData folder, one folder per pack (each
        // with a manifest.json) — same "drop your stuff in here" model as
        // AdditionalSounds. The folder is created on startup if missing; folders
        // without a manifest are simply skipped.
        private static string PacksRoot => Path.Combine(MelonEnvironment.UserDataDirectory, "RumbleReShaded");

        public override void OnInitializeMelon()
        {
            // Register the injected Il2Cpp type before it is ever instantiated/used.
            try { ClassInjector.RegisterTypeInIl2Cpp<RRSRenderPass>(); }
            catch (Exception e) { MelonLogger.Warning("RRSRenderPass already registered or registration failed: " + e.Message); }

            // Stage 3 mask pass + its RenderGraph pass data. BOTH must be injected: RenderGraph
            // allocates the pass data on the Il2Cpp side, so an unregistered type would fail at
            // record time rather than here.
            try
            {
                ClassInjector.RegisterTypeInIl2Cpp<RRSMaskPass>();
                ClassInjector.RegisterTypeInIl2Cpp<RRSMaskPass.MaskPassData>();
            }
            catch (Exception e) { MelonLogger.Warning("RRSMaskPass registration failed: " + e.Message); }

            // Mask sandwich step 1 (see MaskSandwich.cs). Two passes, not one, because they sit
            // either side of CategoryRecolor's RenderObjectsPasses and so need different
            // renderPassEvents. No pass data type here — unlike RRSMaskPass these record nothing
            // but blits, which is exactly why the route is considered safe.
            try
            {
                ClassInjector.RegisterTypeInIl2Cpp<RRSSandwichSavePass>();
                ClassInjector.RegisterTypeInIl2Cpp<RRSSandwichRestorePass>();
                // Step 3's third slice, added 2026-08-27.
                ClassInjector.RegisterTypeInIl2Cpp<RRSSandwichMaskPass>();
            }
            catch (Exception e) { MelonLogger.Warning("Mask sandwich pass registration failed: " + e.Message); }

            // Open the API here, NOT after the packs are scanned. Consumer mods register
            // their own packs from their OnInitializeMelon, which MelonLoader may run
            // either side of ours — so the registration window has to be open for the
            // whole of that phase. Scanning and menu-building move to
            // OnLateInitializeMelon, by which point every consumer has had its turn.
            ApiReady = true;
            MelonLogger.Msg($"RumbleReShaded initialising. Trigger API v{Api.RRS.ApiVersion} ready " +
                $"({EventBuffer.MaxEvents} event slots); shader packs are collected after all mods have loaded.");
        }

        // Build the UIFramework menu from the discovered packs. One category holds the
        // master switch + a Reload button; each pack gets its own category with an
        // on/off toggle and a slider per parameter. Settings persist via
        // MelonPreferences (UserData/MelonPreferences.cfg).
        private void BuildSettings()
        {
            List<MelonPreferences_Category> categories = new List<MelonPreferences_Category>();

            MelonPreferences_Category mainCat = MelonPreferences.CreateCategory("RumbleReShaded", "RumbleReShaded");
            masterEnabled = mainCat.CreateEntry("Enabled", true, "Enabled", "Master switch for all shader packs.");
            // A real button: re-read every pack bundle from disk (handy while authoring).
            // Mod-supplied packs reload too — from their retained bytes rather than from
            // disk (ShaderPack.Unload keeps EmbeddedBundle for exactly this). Pointless for
            // them in practice, but a Reload that silently killed half the packs would be
            // far worse than one that redundantly reloads them.
            UI.CreateButtonEntry(mainCat, "Reload", "Reload packs",
                "Re-read all shader pack bundles (from disk, or from memory for packs supplied by other mods).",
                (Action)(() =>
                {
                    foreach (ShaderPack pack in packs) pack.Unload();
                    MelonLogger.Msg("Shader packs will reload.");
                }));
            // ── MENU PARKED 2026-08-06 (Peter) — entries removed, CODE UNTOUCHED ─────────────
            //
            // The main category had grown to ~23 entries, almost all of it dev scaffolding for
            // work that is either finished or blocked. While the mask sandwich ladder is the
            // only live experiment, the menu shows only what that needs. Peter's standing rule
            // applies to our own menu too, not just to pack authors: "too many options / things
            // to learn is gonna hurt us in the long run."
            //
            // NOTHING WAS DELETED except the CreateEntry/CreateButtonEntry calls. Every
            // implementation below is intact, compiles, and is one line from coming back:
            //
            //   Fire test event      -> Api.RRS.Fire("rrs.test.ping", lastCameraPos, 8f, 1f, 2f)
            //                           Trigger API is VR-verified (2026-08-04); the button had
            //                           done its job. See docs/TRIGGER_API.md §6.
            //   Dump scene renderers -> SceneSurvey.Run()
            //                           Survey is finished and written up in the plan §10.
            //   Tag renderer cats    -> CategoryTagger.Retag()
            //   Clear renderer cats  -> CategoryTagger.ClearAll()
            //                           ⚠ RE-ADD BOTH BEFORE SANDWICH STEP 2. The category draws
            //                           need tagged renderers; with no tags step 2 draws nothing
            //                           and reads as a false pass. Deliberately NOT made
            //                           automatic on scene load — that would add a moving part
            //                           to a test whose entire value is "nothing changed."
            //   Category recolour    -> CategoryRecolor.BuildSettings(mainCat)
            //                           16 entries (4 categories x toggle+R+G+B) — two thirds of
            //                           the old menu on its own. VR-verified for map and players
            //                           on 2026-08-05; re-add that one call to get it all back.
            //   Category mask debug  -> gone for good in practice: the RenderGraph route it drove
            //                           is hard-disabled behind RRSMaskPass.RenderGraphRouteEnabled
            //                           = false after it took the whole PC down (plan §11). Its
            //                           own description already read "has no effect".
            //
            // Stale keys left in UserData/MelonPreferences.cfg are harmless — an unregistered
            // entry is simply never read, and re-adding a call picks the saved value back up.

            // Belt-and-braces on the dead RenderGraph route: the enqueue is gated by
            // RenderGraphRouteEnabled (false), and the mode it would have read is now pinned at 0
            // with no way to raise it from the menu.
            RRSMaskPass.DebugMode = 0;

            // ── MENU PARKED 2026-09-04 (Peter) — SIX SANDWICH ENTRIES + TWO TAGGER BUTTONS ─────
            //
            // Same treatment, same reason as the 2026-08-06 block above, applied to the last of
            // the dev scaffolding ahead of publishing 1.2.0. What a downloader now sees in the
            // main category is exactly: Enabled, Reload packs. Everything else in the menu is a
            // pack the user installed.
            //
            // Removed here (implementations untouched, one line each to bring back):
            //
            //   MaskSandwich.BuildSettings(mainCat)  -> the 0–13 rung ladder, "Rung at next
            //                                           startup", "Dump render graph (1 frame)",
            //                                           and the three RenderGraph debug toggles
            //                                           (culling / merging / compilation caching).
            //                                           Six entries, all of them instruments for
            //                                           an experiment that CONCLUDED on
            //                                           2026-08-27: steps 1–3 passed and the
            //                                           category mask is live. The ladder's value
            //                                           now is re-running it after a game update,
            //                                           which is a dev act, not a player one.
            //   Tag / Clear renderer categories      -> CategoryTagger.Retag() / ClearAll().
            //                                           These exist to make the sandwich rungs
            //                                           readable; with the ladder parked they tag
            //                                           renderers nothing reads.
            //
            // ⚠ RE-ADD ALL THREE TOGETHER, and re-add the taggers BEFORE running any rung ≥10 —
            // untagged renderers make the category draws match nothing, and the rung then reads
            // as a FALSE PASS. That warning is why they were un-parked on 2026-08-27; it has not
            // stopped being true, only stopped being reachable.
            //
            // Safe by construction, not by hope: MaskSandwich reads `if (rungSetting == null)
            // return 0` and every debug flag getter is null-guarded, so with BuildSettings never
            // called the whole sandwich is inert at rung 0 — which is what it is forced to on
            // every startup anyway. `_RRSCategoryMask` is therefore NOT published in this build;
            // nothing samples it yet, and the encoding is explicitly still unsettled.

            categories.Add(mainCat);

            foreach (ShaderPack pack in packs)
            {
                // Identifier is derived from the namespaced Id (unique across mods) but
                // TOML-sanitised — see ShaderPack.SettingsCategoryId, a dot there would
                // nest the table and lose the pack's saved settings. Display name stays
                // the plain pack name.
                MelonPreferences_Category cat = MelonPreferences.CreateCategory(pack.SettingsCategoryId, pack.Name);
                string author = pack.Author.Length > 0 ? " by " + pack.Author
                    : pack.OwnerModId != null ? " from " + pack.OwnerModId : "";
                pack.EnabledSetting = cat.CreateEntry("Enabled", true, "Enabled", $"Enable the '{pack.Name}' shader pack{author}.");

                // Stacking order, live. Higher = applied later = draws on top of what the
                // earlier packs produced. Put Grayscale above ImpactRings and the rings go
                // grey too; leave it below and they stay coloured over a grey world.
                pack.OrderSetting = cat.CreateEntry("Order", pack.DefaultOrder, "Apply order",
                    "Higher = applied later, on top of other packs (default from the pack's manifest).",
                    false, false, new SliderDescriptor { Min = 0f, Max = ShaderPack.MaxOrder, DecimalPlaces = 0 });

                foreach (PackParameter p in pack.Parameters)
                    p.Setting = cat.CreateEntry(p.Property, p.Default, p.Label, $"{p.Label} ({p.Min} to {p.Max})",
                        false, false, new SliderDescriptor { Min = p.Min, Max = p.Max, DecimalPlaces = 2 });

                // Captured per iteration on purpose — `pack` is a fresh variable each
                // pass, so every button resets its own pack.
                ShaderPack target = pack;
                UI.CreateButtonEntry(cat, "Reset", "Reset to defaults",
                    $"Put every '{pack.Name}' slider back to its manifest default.",
                    (Action)(() =>
                    {
                        target.ResetParameters();
                        MelonLogger.Msg($"Shader pack '{target.Name}': parameters reset to defaults.");
                    }));

                categories.Add(cat);
            }

            UI.RegisterMelon(this, categories.ToArray());
            MelonLogger.Msg("RumbleReShaded settings registered with UIFramework.");
        }

        // Collect every pack from both sources. Disk first, so a user's own folder always
        // wins a name clash against a mod-supplied pack — the user can rename their folder,
        // and cannot rename what is baked into somebody else's DLL.
        private static void CollectPacks()
        {
            packs.Clear();
            Directory.CreateDirectory(PacksRoot);

            foreach (string dir in Directory.GetDirectories(PacksRoot))
            {
                ShaderPack pack = ShaderPack.FromFolder(dir);
                if (pack == null) continue;
                if (!TryAdd(pack, dir)) continue;
                MelonLogger.Msg($"Found shader pack '{pack.Name}'{(pack.Author.Length > 0 ? " by " + pack.Author : "")} {pack.Version}");
            }

            // Packs other mods handed us during OnInitializeMelon (RRS.RegisterPack).
            // Close() latches the registry shut: anything arriving later would be invisible
            // in the menu built two lines from now, so it is refused rather than half-added.
            int embedded = 0;
            foreach (ShaderPack pack in PackRegistry.Close())
                if (TryAdd(pack, pack.SourceLabel)) embedded++;

            SortPacks();

            MelonLogger.Msg($"{packs.Count} shader pack(s): {packs.Count - embedded} from UserData, {embedded} supplied by other mods.");
        }

        // Lower queue applies first; a scene-transforming pack wants to be LAST if it
        // should also affect what earlier packs drew.
        //
        // The Id tie-break is not decoration: List.Sort is unstable, and stock packs
        // genuinely tie (ContactShade and UltraShade are both 2600). Without it, two
        // packs at the same order could swap places between sessions and the frame would
        // quietly change — the worst kind of bug to chase, because nothing was edited.
        private static void SortPacks()
        {
            packs.Sort((a, b) =>
            {
                int byQueue = a.EffectiveQueue.CompareTo(b.EffectiveQueue);
                return byQueue != 0 ? byQueue : string.CompareOrdinal(a.Id, b.Id);
            });
        }

        // The order sliders are live, so the list has to be able to re-sort mid-session.
        // Done by comparing against last frame's values rather than by subscribing to
        // MelonPreferences change events: a handful of int compares per frame costs
        // nothing measurable, and it cannot miss a change that arrives by any other route
        // (a config reload, another mod writing the entry).
        private static readonly List<int> lastOrder = new List<int>();

        private static void ResortIfOrderChanged()
        {
            bool changed = lastOrder.Count != packs.Count;
            if (!changed)
                for (int i = 0; i < packs.Count; i++)
                    if (lastOrder[i] != packs[i].EffectiveQueue) { changed = true; break; }

            if (!changed) return;

            SortPacks();
            lastOrder.Clear();
            foreach (ShaderPack pack in packs)
            {
                lastOrder.Add(pack.EffectiveQueue);
                // Keep the material in step. Nothing in our own blit path reads it — we
                // apply packs in list order — but it is the value anything else would
                // inspect, and a material disagreeing with the list is a trap.
                if (pack.Material != null) pack.Material.renderQueue = pack.EffectiveQueue;
            }
        }

        private static bool TryAdd(ShaderPack pack, string source)
        {
            if (packs.Exists(p => p.Id == pack.Id))
            {
                MelonLogger.Warning($"Duplicate shader pack '{pack.Id}' — skipping {source}. Pack names must be unique.");
                return false;
            }
            packs.Add(pack);
            return true;
        }

        public override void OnLateInitializeMelon()
        {
            // MelonLoader runs EVERY mod's OnInitializeMelon before ANY OnLateInitializeMelon,
            // so by here every consumer mod has had its chance to call RRS.RegisterPack —
            // whatever the load order. That is the whole reason these two calls are not in
            // OnInitializeMelon any more: UIFramework builds its layout exactly once, and a
            // pack that misses that moment can never get a menu category this session.
            //
            // If UIFramework ever objects to being registered this late, this is the pair of
            // lines to move back — and mod-supplied packs would then need a game restart to
            // appear, which is the behaviour this change exists to avoid.
            CollectPacks();
            BuildSettings();

            if (!hooked)
            {
                hooked = true;
                pass = new RRSRenderPass();
                // Run after transparents so the colour we sample already contains
                // particles and world-space UI. requiresIntermediateTexture forces URP
                // to render into a real texture (not straight to the backbuffer), so
                // activeColorTexture is something we can copy and blit.
                pass.renderPassEvent = RenderPassEvent.AfterRenderingTransparents;
                pass.requiresIntermediateTexture = true;
                // Make _CameraDepthTexture available to packs that sample depth
                // (e.g. UltraShade's atmospheric fog).
                pass.ConfigureInput(ScriptableRenderPassInput.Depth);

                // Mask pass runs BEFORE the colour pass so the mask is finished by the time
                // anything samples it. AfterRenderingOpaques is also where the geometry it wants
                // to draw has just been rendered, so the culling results are current.
                maskPass = new RRSMaskPass();
                maskPass.renderPassEvent = RenderPassEvent.AfterRenderingOpaques;
                maskPass.requiresIntermediateTexture = true;

                // Mask sandwich step 1. requiresIntermediateTexture on BOTH: without it URP may
                // render straight to the backbuffer, and there would be no activeColorTexture to
                // copy out of or blit back into.
                sandwichSave = new RRSSandwichSavePass();
                sandwichSave.renderPassEvent = RRSSandwichSavePass.Event;
                sandwichSave.requiresIntermediateTexture = true;

                sandwichRestore = new RRSSandwichRestorePass();
                sandwichRestore.renderPassEvent = RRSSandwichRestorePass.Event;
                sandwichRestore.requiresIntermediateTexture = true;

                // Step 3's mask copy. Enqueued only on rungs 12/13 — see MaskSandwich.Enqueue —
                // but constructed here with the others so injection happens once.
                sandwichMask = new RRSSandwichMaskPass();
                sandwichMask.renderPassEvent = RRSSandwichMaskPass.Event;
                sandwichMask.requiresIntermediateTexture = true;

                RenderPipelineManager.beginCameraRendering += (Il2CppSystem.Action<ScriptableRenderContext, Camera>)OnBeginCameraRendering;
            }
        }

        private static bool AnyPackEnabled()
        {
            foreach (ShaderPack pack in packs)
                if (pack.IsEnabled) return true;
            return false;
        }

        // Enqueue our render pass on the main VR camera every frame. The pass itself
        // (RRSRenderPass.RecordRenderGraph) decides what to do once inside the graph.
        private static void OnBeginCameraRendering(ScriptableRenderContext context, Camera cam)
        {
            // Same reasoning as RecordRenderGraph: this is a callback from the pipeline, so a
            // throw here kills the game rather than logging. The enqueue path calls into the
            // mask sandwich and the recolour passes as well as our own, and none of it is worth
            // a crash — an unshaded frame is a far better failure than a dead process.
            try
            {
                BeginCameraRendering(cam);
            }
            catch (Exception e)
            {
                ReportRenderFailure("the per-camera setup threw before the pass was enqueued.", e);
            }
        }

        private static void BeginCameraRendering(Camera cam)
        {
            // Camera filter first, so the position we remember for the debug button is
            // the VR camera's and not some off-screen render-to-texture camera's.
            if (cam == null || cam.targetTexture != null || !cam.stereoEnabled) return;
            lastCameraPos = cam.transform.position;

            if (masterEnabled == null || !masterEnabled.Value) return;

            // ── REMOVED 2026-09-04: toggle-to-retry (master switch and per-pack) ─────────────
            //
            // A previous version cleared RenderFailed when the master switch or any pack was
            // flicked off→on, tracked frame-to-frame the same way the order slider is. It never
            // ran in a VR test — three sessions in a row happened to toggle BEFORE the failure
            // rather than after — and Peter's call was that shipping an unverified recovery
            // gesture, and describing it in release notes, is worse than not having one.
            //
            // Nothing was lost: the automatic retry in OnSceneWasInitialized is verified
            // (2026-09-04, three attempts and two scene loads on the forced-failure build) and it
            // covers the case that actually matters — a transient fault healing without the
            // player ever learning something failed. What goes away is only the ability to retry
            // a FOURTH time without restarting.
            //
            // If it comes back it needs its own VR run, and the test is: reach the give-up
            // message first, THEN toggle. Toggling before the failure proves nothing.

            if (RenderFailed) return;

            // Ageing and expiry happen here, once per camera, BEFORE the pass is
            // enqueued — globals set while the render graph is being recorded are
            // order-dependent, so setting them up front is the predictable option.
            EventBuffer.PushGlobals();

            UniversalAdditionalCameraData maskData = cam.GetComponent<UniversalAdditionalCameraData>();
            ScriptableRenderer maskRenderer = maskData != null ? maskData.scriptableRenderer : null;

            // ⛔ The Stage 3 RenderGraph debug ladder used to be enqueued here. Its menu slider was
            // parked on 2026-08-06 (see BuildSettings), and the route it drove is hard-disabled
            // behind RenderGraphRouteEnabled = false after rung 3 crashed the whole PC on
            // 2026-08-05 (see RRSMaskPass / plan §11). With no way to raise DebugMode above 0 the
            // enqueue could never fire, so it is gone rather than sitting here reading as live.
            // `maskPass` is still constructed and still injected — only the enqueue is removed.

            // Enqueued before the pack early-return: recolouring is independent of shader packs,
            // and the same mistake in the mask debug made a slider look broken (2026-08-05).
            // Its menu entries are parked, which leaves `passes` empty and makes this a no-op —
            // deliberately left in place so re-adding CategoryRecolor.BuildSettings is the single
            // line that brings the whole feature back.
            // Sandwich step 2 (rung 10) arms the category draws and pins them to event 302, so
            // they land between the save at 301 and the restore at 303. MUST run before
            // CategoryRecolor.Enqueue — that call is what reads the two statics this sets.
            MaskSandwich.PrepareCategoryDraws();

            if (maskRenderer != null) CategoryRecolor.Enqueue(maskRenderer);

            // Enqueued after the recolour passes but that says nothing about ORDER — URP sorts
            // its queue by renderPassEvent, and these two deliberately straddle
            // AfterRenderingOpaques where the recolour passes sit. Also outside the pack
            // early-return, for the same reason as the two above: it is independent of packs.
            if (maskRenderer != null) MaskSandwich.Enqueue(maskRenderer, sandwichSave, sandwichRestore, sandwichMask);

            if (!DebugSolidFill && !AnyPackEnabled()) return;

            // Before enqueueing, not inside RecordRenderGraph — reordering the list the
            // graph is about to walk, while it is walking it, is asking for trouble.
            ResortIfOrderChanged();

            UniversalAdditionalCameraData data = cam.GetComponent<UniversalAdditionalCameraData>();
            if (data == null) return;
            ScriptableRenderer renderer = data.scriptableRenderer;
            if (renderer == null) return;

            renderer.EnqueuePass(pass);

            if (!enqueueDiagLogged)
            {
                enqueueDiagLogged = true;
                MelonLogger.Msg($"[diag] EnqueuePass on '{cam.name}' (stereo={cam.stereoEnabled}, XR={UnityEngine.XR.XRSettings.enabled}). " +
                    "Pass will run inside URP's RenderGraph.");
            }
        }
    }
}
