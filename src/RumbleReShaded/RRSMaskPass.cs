using System;
using MelonLoader;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RendererUtils;
using UnityEngine.Rendering.Universal;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Injection;
using RGUtils = UnityEngine.Rendering.RenderGraphModule.Util.RenderGraphUtils;

namespace RumbleReShaded
{
    // Stage 3, half 2: turn CategoryTagger's renderingLayerMask bits into a texture that pack
    // shaders can sample. Plan §5.
    //
    // ⚠ STATUS: PROOF STEP. This deliberately does the SMALLEST thing that answers the one
    // question that can sink the whole approach, and nothing more.
    //
    // WHY A PROOF STEP. Every pass this mod has ever shipped uses RGUtils.AddBlitPass, which takes
    // no generic pass data and no render callback. Drawing a renderer list needs
    // AddRasterRenderPass<PassData> plus builder.SetRenderFunc<PassData, RasterGraphContext>(...),
    // which means injecting a custom PassData type into Il2Cpp AND marshalling a delegate across
    // the interop boundary. Neither has been done in this codebase. If either fails, it fails
    // inside a render graph recording, where diagnosis is expensive.
    //
    // This is the same tactic that de-risked the 2026-06-19 rework: DebugSolidFill turned the view
    // black to prove ClassInjector + EnqueuePass + RecordRenderGraph + write-to-HMD all worked
    // BEFORE any shader was involved. Prove the machinery, then build on it.
    //
    // The debug modes are ordered so each one adds exactly one new thing:
    //   1 = raster pass clears the mask to RED, mask blitted to screen.
    //       Whole view red  -> AddRasterRenderPass + Il2Cpp PassData + SetRenderFunc ALL WORK.
    //       Nothing/black   -> the interop is the problem, not the graphics. Stop here.
    //   2 = as above, but also draws the tagged renderer list into the mask.
    //       Red structures on black -> categories reach the GPU. Stage 3 is essentially done.
    //
    // Only after mode 2 passes does the RGBA channel packing and the global-texture publish
    // become worth writing.
    public class RRSMaskPass : ScriptableRenderPass
    {
        public RRSMaskPass(IntPtr ptr) : base(ptr) { }
        public RRSMaskPass() : base(ClassInjector.DerivedConstructorPointer<RRSMaskPass>())
            => ClassInjector.DerivedConstructorBody(this);

        // Pass data must be a class RenderGraph can allocate, and for Il2Cpp that means a type
        // registered with ClassInjector (done in Main.OnInitializeMelon alongside the pass types).
        // Kept to one field on purpose: every extra member is another thing that can fail to
        // marshal, and this exists to isolate a yes/no question.
        // Must derive from Il2CppSystem.Object, not System.Object: RenderGraph allocates and
        // stores this on the Il2Cpp side, so a purely managed class cannot cross the boundary.
        // (Found by compiler error, not by guessing — CS1503 on the ClassInjector call.)
        public class MaskPassData : Il2CppSystem.Object
        {
            public MaskPassData(IntPtr ptr) : base(ptr) { }
            public MaskPassData() : base(ClassInjector.DerivedConstructorPointer<MaskPassData>())
                => ClassInjector.DerivedConstructorBody(this);

            public RendererListHandle List;
        }

        // Debug ladder. REVISED 2026-08-05 after mode 1 hard-crashed the game.
        //
        // The first version jumped three unproven things at once — creating the mask texture,
        // adding a raster pass, and marshalling a render delegate — so the crash proved only
        // that ONE of them was broken, without saying which. That is a badly designed
        // experiment. Each rung now adds exactly one thing:
        //
        //   1 = create the mask texture (cleared red by its DESCRIPTOR) and blit it to screen.
        //       No raster pass, no delegate. Whole view red -> texture + blit are fine, and the
        //       fault is further up.
        //   2 = also add the raster pass, with NO render function attached.
        //       Survives -> AddRasterRenderPass + Il2Cpp PassData injection are fine, and the
        //       delegate is the culprit.
        //   3 = also attach the render function (the delegate marshalling).
        //       Survives -> the interop works and only the drawing is left.
        //   4 = also draw the tagged renderer list.
        //
        // Whichever rung crashes IS the answer. Climb one at a time.
        public static int DebugMode = 0;

        // ⛔ DISABLED 2026-08-05 — BLOCKED, not proven impossible. Read all of this before raising it.
        //
        // WHAT HAPPENED: rung 3 (SetRenderFunc) crashed instantly with an injected PassData, then —
        // after borrowing RenderGraphUtils.CopyPassData so the generic instantiation existed — took
        // down Peter's entire Windows machine after a failed ~838 KB write. That is native memory
        // corruption reaching the GPU driver.
        //
        // WHAT IS ACTUALLY PROVEN, and it is narrower than "RenderGraph is off limits":
        //   ✅ AddRasterRenderPass works (rung 2 ran fine).
        //   ✅ Managed code IS callable from the render pipeline — RRSRenderPass.RecordRenderGraph
        //      is our managed override, invoked natively every frame, and has always worked.
        //   ❌ Only supplying our own DELEGATE via SetRenderFunc failed.
        //
        // LEADING HYPOTHESIS (untested): RasterGraphContext is a STRUCT PASSED BY VALUE. A
        // ClassInjector vtable override (RecordRenderGraph) and a DelegateSupport trampoline are
        // different marshalling paths, and if the trampoline gets that struct's layout wrong then
        // context.cmd is a garbage pointer — which is precisely the failure observed. Nobody has
        // verified this; it is the thing to test first if this is ever revisited.
        //
        // IF YOU RETRY, DO IT UNDER THIS PROTOCOL (Peter, 2026-08-05, on easing the ban):
        //   1. Render to a small OFF-SCREEN texture, never the camera target.
        //   2. Execute for exactly ONE frame, then self-disable. Do not run per-frame.
        //   3. Confirm the callback's arguments are sane (log context validity) BEFORE writing
        //      anything through them.
        //   4. Warn Peter first and have him close anything he cares about.
        // The failure mode is machine-level, so the burden of proof sits on the retry, not on the
        // ban. The sandwich approach in plan §12 needs none of this and should be tried first.
        public const bool RenderGraphRouteEnabled = false;

        // Rungs 3+ are the dangerous ones. Even with the pass disabled above, this clamp means a
        // stray value can never select them again.
        public const int MaxSafeRung = 2;

        // Set once if recording throws, so a failure reports itself exactly once instead of
        // spamming the log at 90 fps — and so the mod keeps running rather than taking the frame
        // down with it.
        private static bool failed;
        private static bool loggedOnce;

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            if (DebugMode == 0 || failed) return;

            try
            {
                UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
                UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
                if (resourceData == null || cameraData == null) return;

                TextureHandle activeColor = resourceData.activeColorTexture;
                if (!activeColor.IsValid()) return;

                // The mask matches the camera colour's dimensions and slice count so it is
                // automatically a Tex2DArray with slices=2 in VR — the stereo requirement from
                // plan §5. Single-sample because you cannot SAMPLE an MSAA texture, the same
                // reason RRSRenderPass forces msaaSamples=None on its copy.
                TextureDesc maskDesc = renderGraph.GetTextureDesc(ref activeColor);
                maskDesc.msaaSamples = MSAASamples.None;
                maskDesc.bindTextureMS = false;
                maskDesc.depthBufferBits = DepthBits.None;
                maskDesc.format = GraphicsFormat.R8G8B8A8_UNorm;
                maskDesc.clearBuffer = true;
                // Rung 1 gets its red from the DESCRIPTOR, so the texture proves itself with no
                // raster pass and no delegate involved.
                maskDesc.clearColor = DebugMode == 1 ? Color.red : Color.black;
                maskDesc.name = "RRS_CategoryMask";

                TextureHandle mask = renderGraph.CreateTexture(ref maskDesc);

                if (DebugMode >= 2) RecordRasterPass(renderGraph, mask, frameData, cameraData);

                // Show the mask instead of the scene. Uses the existing material-less blit path,
                // so this is verifiable in VR with NO new AssetBundle and no Unity trip.
                RGUtils.AddBlitPass(renderGraph, mask, activeColor, Vector2.one, Vector2.zero,
                    0, 0, -1, 0, 0, 1, RGUtils.BlitFilterMode.ClampNearest, "RRS Mask Debug View", false, "", 0);

                if (!loggedOnce)
                {
                    loggedOnce = true;
                    MelonLogger.Msg($"[diag] RRSMaskPass recorded (rung {DebugMode}, {maskDesc.width}x{maskDesc.height} " +
                        $"slices={maskDesc.slices}). Reaching this line means recording did not crash.");
                }
            }
            catch (Exception e)
            {
                failed = true;
                MelonLogger.Error($"RRSMaskPass failed and is now DISABLED for this session: " +
                    $"{e.GetType().Name}: {e.Message}\n{e.StackTrace}");
            }
        }

        // Rungs 2-4. Split out so rung 1 cannot touch any of it.
        private static void RecordRasterPass(RenderGraph renderGraph, TextureHandle mask,
                                             ContextContainer frameData, UniversalCameraData cameraData)
        {
            {
                // ⚠ PassData is RenderGraphUtils.CopyPassData — a URP type — NOT our own class,
                // and that is the entire point.
                //
                // Rung 3 crashed natively with an injected MaskPassData (2026-08-05). Cause:
                // Il2Cpp is AOT, so every generic instantiation must exist in the compiled
                // binary, and RUMBLE was obviously never built with
                // BaseRenderFunc<MaskPassData, RasterGraphContext>. There was no code to call.
                //
                // CopyPassData is what URP's own AddCopyPass uses — the DLL dump shows
                // CopyRenderFunc(CopyPassData, RasterGraphContext) — so AddRasterRenderPass,
                // SetRenderFunc AND the BaseRenderFunc delegate are all already instantiated
                // over it. Borrowing the type borrows those instantiations.
                //
                // The cost is that we cannot carry our own fields in it, hence pendingList below.
                IRasterRenderGraphBuilder builder =
                    renderGraph.AddRasterRenderPass<RGUtils.CopyPassData>("RRS Category Mask", out RGUtils.CopyPassData data);

                // SetRenderAttachment and UseRendererList are declared on the CONCRETE
                // RenderGraphBuilders class, not on IRasterRenderGraphBuilder — Il2Cpp interop
                // does not surface them through the interface. Same Cast<> pattern the existing
                // blit path uses for IDisposable. Verified by dumping the real DLLs; the
                // five-argument overload is the only one that exists here.
                RenderGraphBuilders b = builder.Cast<RenderGraphBuilders>();
                b.SetRenderAttachment(mask, 0, AccessFlags.Write, 0, -1);

                if (DebugMode >= 4)
                {
                    // Draw only what CategoryTagger marked as a structure. renderingLayerMask on
                    // the desc is the filter — this is the payoff of the whole tagging exercise.
                    UniversalRenderingData renderingData = frameData.Get<UniversalRenderingData>();
                    RendererListDesc desc = new RendererListDesc(
                        new ShaderTagId("UniversalForward"), renderingData.cullResults, cameraData.camera)
                    {
                        renderQueueRange = RenderQueueRange.opaque,
                        sortingCriteria = SortingCriteria.CommonOpaque,
                        renderingLayerMask = CategoryTagger.Structures,
                        // URP/Unlit is present in this build (survey 2026-08-05: 57 renderers use
                        // it), which is why no mask bundle has to ship. Plan §5 option (a).
                        overrideMaterial = MaskMaterial,
                        overrideMaterialPassIndex = 0,
                    };
                    // Borrowed pass data has no field for our handle, so it rides in a static.
                    // Safe here: recording is single-threaded and there is exactly one mask pass
                    // per frame. If a second one is ever added, this must become real pass data.
                    pendingList = renderGraph.CreateRendererList(ref desc);
                    RendererListHandle handle = pendingList;   // takes a ref, so it needs a local
                    b.UseRendererList(ref handle);
                }

                // Rung 3: THE RISKY LINE. A managed lambda has to become an Il2Cpp delegate over
                // an injected generic type. Nothing in this codebase has done that before, and
                // it is the single most likely thing to be unsupported.
                if (DebugMode >= 3) builder.SetRenderFunc<RGUtils.CopyPassData>(RenderFunc);

                builder.Cast<Il2CppSystem.IDisposable>().Dispose();
            }
        }

        // Renderer list for the pass currently being recorded. See the CopyPassData note above.
        private static RendererListHandle pendingList;

        // ⚠ BUILT ONCE. DelegateSupport.ConvertDelegate is expensive one-time setup — it builds an
        // interop trampoline by reflection. The first version called it inside RecordRenderGraph,
        // i.e. ~90 times a second per eye, allocating a fresh Il2Cpp delegate every frame and
        // never releasing any of them. That is what turned rung 3 from a clean crash into
        // "application not responding" on 2026-08-05. Convert once, reuse forever.
        //
        // BOTH references are kept deliberately: `managedRenderFunc` holds the managed delegate
        // alive so the GC cannot collect the target out from under the native trampoline, which
        // would be an intermittent crash long after the fact — the worst kind to diagnose.
        private static Action<RGUtils.CopyPassData, RasterGraphContext> managedRenderFunc;
        private static BaseRenderFunc<RGUtils.CopyPassData, RasterGraphContext> convertedRenderFunc;

        private static BaseRenderFunc<RGUtils.CopyPassData, RasterGraphContext> RenderFunc
        {
            get
            {
                if (convertedRenderFunc == null)
                {
                    managedRenderFunc = Execute;
                    convertedRenderFunc = DelegateSupport
                        .ConvertDelegate<BaseRenderFunc<RGUtils.CopyPassData, RasterGraphContext>>(managedRenderFunc);
                    MelonLogger.Msg("[diag] Mask render callback converted to an Il2Cpp delegate (once).");
                }
                return convertedRenderFunc;
            }
        }

        // Proves the callback actually EXECUTES, which is the one thing neither a crash nor a hang
        // can tell us from outside. If this line never appears, the delegate is never invoked and
        // the problem is the pass, not the drawing.
        private static bool executedOnce;

        private static void Execute(RGUtils.CopyPassData data, RasterGraphContext context)
        {
            try
            {
                if (!executedOnce)
                {
                    executedOnce = true;
                    MelonLogger.Msg("[diag] Mask render callback EXECUTED — Il2Cpp delegate marshalling works.");
                }

                // Rung 3 paints the attachment GREEN. Deliberately not red: rungs 1-2 were shown
                // on 2026-08-05 to display uninitialised/aliased memory (nothing writes the mask
                // until this callback exists), and that garbage happened to look red. A colour
                // nothing else produces is the only trustworthy signal.
                if (DebugMode == 3)
                {
                    context.cmd.ClearRenderTarget(RTClearFlags.Color, Color.green, 1f, 0);
                    return;
                }

                context.cmd.ClearRenderTarget(RTClearFlags.Color, Color.black, 1f, 0);
                if (pendingList.IsValid())
                    context.cmd.DrawRendererList(pendingList);
            }
            catch (Exception e)
            {
                // Disables the pass rather than throwing back into the render graph, and logs
                // once — this runs per frame per eye, so an unguarded log here is its own hang.
                failed = true;
                MelonLogger.Error($"RRSMaskPass render callback threw: {e.GetType().Name}: {e.Message}");
            }
        }

        // Written by whatever the mask pass draws. Created lazily from a shader already in the
        // game — the survey confirmed URP/Unlit survived RUMBLE's build, so nothing ships.
        private static Material maskMaterial;
        private static Material MaskMaterial
        {
            get
            {
                if (maskMaterial != null) return maskMaterial;
                Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
                if (shader == null)
                {
                    MelonLogger.Warning("RRSMaskPass: 'Universal Render Pipeline/Unlit' not found at runtime, " +
                        "despite the scene survey seeing 57 renderers using it. Mask pass cannot draw.");
                    return null;
                }
                maskMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                maskMaterial.color = Color.red;
                return maskMaterial;
            }
        }
    }
}
