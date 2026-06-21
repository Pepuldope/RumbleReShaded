using System;
using System.Collections.Generic;
using System.IO;
using MelonLoader;
using MelonLoader.Utils;
using RumbleModUI;
using UnityEngine;
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
        public const string ModVersion = "1.0.0";
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

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
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

            if (!RumbleReShadedMod.passDiagLogged)
            {
                RumbleReShadedMod.passDiagLogged = true;
                MelonLogger.Msg($"[diag] RRSRenderPass: activeColor {desc.width}x{desc.height} dim={desc.dimension} " +
                    $"slices={desc.slices} srcMSAA={renderGraph.GetTextureDesc(ref activeColor).msaaSamples}.");
            }

            int applied = 0;
            foreach (ShaderPack pack in RumbleReShadedMod.Packs)
            {
                if (!pack.IsEnabled || !pack.EnsureLoaded()) continue;

                // Can't sample the texture we render into, so copy (resolving MSAA via
                // a fragment blit) the live image to a temp, then blit the temp back
                // through the material into the active colour. Blitter binds the source
                // to "_BlitTexture" and draws a procedural fullscreen triangle, so pack
                // shaders must use the URP Blit convention (Blit.hlsl / _BlitTexture).
                TextureHandle copied = renderGraph.CreateTexture(ref desc);
                RGUtils.AddBlitPass(renderGraph, activeColor, copied, Vector2.one, Vector2.zero,
                    0, 0, -1, 0, 0, 1, RGUtils.BlitFilterMode.ClampBilinear, "RRS Copy " + pack.Name, false, "", 0);

                RGUtils.BlitMaterialParameters bmp =
                    new RGUtils.BlitMaterialParameters(copied, activeColor, pack.Material, 0);
                // returnBuilder:true so we can grant the pass access to URP global
                // textures (e.g. _CameraDepthTexture for depth-based effects); the
                // builder must be disposed to finalise the pass.
                IBaseRenderGraphBuilder builder = RGUtils.AddBlitPass(renderGraph, bmp, "RRS " + pack.Name, true, "", 0);
                builder.UseAllGlobalTextures(true);
                builder.Cast<Il2CppSystem.IDisposable>().Dispose();
                applied++;
            }
        }
    }

    public class RumbleReShadedMod : MelonMod
    {
        public static Mod Mod = new Mod();
        private static ModSetting<bool> masterEnabled;
        private static ModSetting<bool> reloadPacks;

        private static readonly List<ShaderPack> packs = new List<ShaderPack>();
        public static IReadOnlyList<ShaderPack> Packs => packs;

        private static bool hooked;
        private static RRSRenderPass pass;
        internal static bool passDiagLogged;
        private static bool enqueueDiagLogged;

        // TEMP DIAGNOSTIC: see RRSRenderPass.RecordRenderGraph. While true the pass
        // copies a black texture over the camera colour instead of running packs, to
        // prove the injection reaches the HMD. Set false for the real effect.
        public const bool DebugSolidFill = false;

        // Packs live directly under UserData, one folder per pack (each with a
        // manifest.json). Folders without a manifest are simply skipped, so this
        // coexists with other mods' UserData folders.
        private static string PacksRoot => MelonEnvironment.UserDataDirectory;

        public override void OnInitializeMelon()
        {
            // Register the injected Il2Cpp type before it is ever instantiated/used.
            try { ClassInjector.RegisterTypeInIl2Cpp<RRSRenderPass>(); }
            catch (Exception e) { MelonLogger.Warning("RRSRenderPass already registered or registration failed: " + e.Message); }

            ScanPacks();
            MelonLogger.Msg($"RumbleReShaded loaded, {packs.Count} shader pack(s) found in UserData.");
        }

        private static void ScanPacks()
        {
            packs.Clear();
            Directory.CreateDirectory(PacksRoot);

            foreach (string dir in Directory.GetDirectories(PacksRoot))
            {
                ShaderPack pack = ShaderPack.FromFolder(dir);
                if (pack == null) continue;
                if (packs.Exists(p => p.Name == pack.Name))
                {
                    MelonLogger.Warning($"Duplicate shader pack name '{pack.Name}' — skipping {dir}. Pack names must be unique.");
                    continue;
                }
                packs.Add(pack);
                MelonLogger.Msg($"Found shader pack '{pack.Name}'{(pack.Author.Length > 0 ? " by " + pack.Author : "")} {pack.Version}");
            }

            // lower queue applies first; a scene-transforming pack should be lowest
            packs.Sort((a, b) => a.RenderQueue.CompareTo(b.RenderQueue));
        }

        public override void OnLateInitializeMelon()
        {
            UI.instance.UI_Initialized += OnUIInit;

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
                RenderPipelineManager.beginCameraRendering += (Il2CppSystem.Action<ScriptableRenderContext, Camera>)OnBeginCameraRendering;
            }
        }

        public void OnUIInit()
        {
            Mod.ModName = BuildInfo.ModName;
            Mod.ModVersion = BuildInfo.ModVersion;
            Mod.SetFolder("RumbleReShaded");
            Mod.AddDescription("Description", "", BuildInfo.Description, new Tags { IsSummary = true });

            masterEnabled = Mod.AddToList("Enabled", true, 0, "Master switch for all shader packs.", new Tags());
            reloadPacks = Mod.AddToList("Reload packs", false, 0, "Turn on and save to re-read all shader pack bundles from disk. Useful while developing a shader.", new Tags());

            foreach (ShaderPack pack in packs)
            {
                string author = pack.Author.Length > 0 ? " by " + pack.Author : "";
                pack.EnabledSetting = Mod.AddToList(pack.Name, true, 0, $"Enable the '{pack.Name}' shader pack{author}.", new Tags());
                // Separator must NOT contain ": " — ModUI's Settings.txt is a
                // "Key: Value" format, so a ": " inside the setting name produces a
                // line like "Pack: Param: 1" that ModUI mis-parses ("File Read
                // Error") and the saved value is lost. Use " - " instead.
                foreach (PackParameter p in pack.Parameters)
                    p.Setting = Mod.AddToList($"{pack.Name} - {p.Label}", p.Default, $"{p.Label} ({p.Min} to {p.Max})", new Tags());
            }

            Mod.GetFromFile();
            Mod.ModSaved += OnModSaved;
            UI.instance.AddMod(Mod);
            MelonLogger.Msg("RumbleReShaded settings registered!");
        }

        private void OnModSaved()
        {
            bool corrected = false;
            foreach (ShaderPack pack in packs)
            {
                foreach (PackParameter p in pack.Parameters)
                {
                    if (p.Setting == null) continue;
                    float clamped = Mathf.Clamp((float)p.Setting.Value, p.Min, p.Max);
                    if ((float)p.Setting.Value != clamped)
                    {
                        p.Setting.Value = clamped;
                        p.Setting.SavedValue = clamped;
                        corrected = true;
                    }
                }
            }

            if (reloadPacks != null && (bool)reloadPacks.Value)
            {
                reloadPacks.Value = false;
                reloadPacks.SavedValue = false;
                corrected = true;
                foreach (ShaderPack pack in packs) pack.Unload();
                MelonLogger.Msg("Shader packs will reload from disk.");
            }

            foreach (ShaderPack pack in packs) pack.ApplyParameters();
            if (corrected) UI.instance.ForceRefresh();
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
            if (masterEnabled == null || !(bool)masterEnabled.Value) return;
            if (cam == null || cam.targetTexture != null || !cam.stereoEnabled) return;
            if (!DebugSolidFill && !AnyPackEnabled()) return;

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
