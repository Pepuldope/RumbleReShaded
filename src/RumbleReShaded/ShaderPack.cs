using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MelonLoader;
using UnityEngine;

namespace RumbleReShaded
{
    public class PackParameter
    {
        public string Property;
        public string Label;
        public float Default;
        public float Min;
        public float Max;
        public MelonPreferences_Entry<float> Setting;
    }

    // One shader pack = a manifest plus an AssetBundle holding the pack's material.
    //
    // Those bytes arrive one of two ways, and the rule for which is OWNERSHIP:
    //
    //   • The USER owns it  → a folder under UserData/RumbleReShaded/. Content the
    //     player downloads, mixes and tweaks (UltraShade, Grayscale, someone's CRT
    //     look). Discovered by ScanPacks(). This is the original and only model.
    //
    //   • A MOD owns it     → embedded resources inside that mod's own DLL, handed
    //     over via RumbleReShaded.Api.RRS.RegisterPack(). The pack IS the mod's
    //     effect, not something a player installs, mixes or deletes separately.
    //     Never touches the disk: no path to get wrong, no separate download, and
    //     the bundle version is welded to the DLL version that shipped it.
    //
    // Nothing below this line cares which it was. Only the source of the bytes
    // differs (see EnsureLoaded); the manifest schema, menu, sliders, multi-pass
    // and event channels are identical either way.
    public class ShaderPack
    {
        // Unique key: the MelonPreferences category id and the duplicate test.
        // Disk packs use their name; mod-supplied packs are namespaced "modid.name"
        // so two mods shipping a pack called "Impact" can coexist — the same reason
        // channels are namespaced, and equally impossible to tighten later.
        public string Id;
        // Display name shown in the menu. Never namespaced.
        public string Name;
        public string Author = "";
        public string Version = "";
        public string BundleFile;
        public string MaterialName;
        public int Priority;
        public int Queue;
        // How many shader passes to chain. 1 = the classic single-blit pack. >1 runs the
        // material's passes 0..N-1 in order, each reading the previous one's output, so a
        // pack can do things one blit can't — e.g. ContactShade computing AO in pass 0
        // and blurring it in pass 1. Intermediates are RGBA_Half, so a pass can hand the
        // next one a spare channel alongside the colour.
        public int Passes = 1;
        // Set for disk packs only.
        public string Folder;
        // Set for mod-supplied packs only: the raw bundle bytes handed to us by the
        // owning mod. Deliberately KEPT after Unload() so "Reload packs" works for an
        // embedded pack too — there is no file to re-read, and asking the owning mod
        // to re-register would need a callback the API does not have.
        public byte[] EmbeddedBundle;
        // Melon id of the mod that registered this pack, for log lines that have to
        // point a user at the right mod. Null for disk packs.
        public string OwnerModId;

        public bool IsEmbedded => EmbeddedBundle != null;
        public string SourceLabel => IsEmbedded ? "embedded in " + OwnerModId : Folder;

        // MelonPreferences persists categories as TOML tables, and a '.' in a table name
        // means NESTING there — "[RRS_mymod.Impact]" would be read back as a table Impact
        // inside a table RRS_mymod, so the pack's settings would silently fail to round
        // trip. Namespaced ids contain a dot by construction, hence this.
        //
        // It also covers a latent hazard that predates mod-supplied packs: a disk pack's
        // name comes straight from its manifest and has never been constrained, so a pack
        // called "My.Pack" or "My Pack" had the same problem. Stock pack names contain none
        // of these characters, so no existing saved setting changes identity.
        public string SettingsCategoryId
        {
            get
            {
                char[] chars = ("RRS_" + Id).ToCharArray();
                for (int i = 0; i < chars.Length; i++)
                    if (!char.IsLetterOrDigit(chars[i]) && chars[i] != '_' && chars[i] != '-')
                        chars[i] = '_';
                return new string(chars);
            }
        }
        public List<PackParameter> Parameters = new List<PackParameter>();
        // Event channels this pack reacts to, in manifest order. The mod resolves each
        // to a hash and pushes it as _RRSChannel0, _RRSChannel1, … so pack authors
        // compare names, never hashes, and no hashing happens in HLSL.
        public List<string> Listen = new List<string>();

        public MelonPreferences_Entry<bool> EnabledSetting;
        public AssetBundle Bundle;
        public Material Material;
        public bool LoadFailed;

        // Every pass is a full-screen blit at VR resolution, per eye. This ceiling
        // exists so a typo in a manifest can't tank the frame rate.
        public const int MaxPasses = 4;

        // 2600 draws after the opaque-texture grab but before the game's
        // transparent effects, so particles and trails stay visible on top.
        public int RenderQueue => Queue != 0 ? Queue : 2600 + Priority;

        // User override of the manifest's order, live from the menu. Packs apply in
        // ascending order and each reads what the previous one wrote, so ORDER IS THE
        // WHOLE EFFECT when packs interact: Grayscale at 2601 desaturates, then
        // ImpactRings at 2604 paints a colour ring on top of the grey world. Swap them
        // and the ring goes grey too. Neither is wrong — it depends what you want.
        //
        // This exists because the manifest alone stopped being enough once packs could
        // ship inside another mod's DLL: a user can edit a folder pack's manifest to
        // reorder it, but cannot edit one embedded in someone else's assembly. Without
        // an override, a mod author would silently own the stacking order of everyone
        // else's packs.
        public MelonPreferences_Entry<float> OrderSetting;

        public int EffectiveQueue => OrderSetting != null ? 2600 + (int)OrderSetting.Value : RenderQueue;

        // Where the slider starts: whatever the manifest asked for, expressed as an
        // offset from the 2600 base and clamped into the slider's range.
        public float DefaultOrder => Mathf.Clamp(RenderQueue - 2600, 0f, MaxOrder);
        public const float MaxOrder = 20f;

        public bool IsEnabled => EnabledSetting == null || EnabledSetting.Value;

        public static ShaderPack FromFolder(string folder)
        {
            string manifestPath = Path.Combine(folder, "manifest.json");
            if (!File.Exists(manifestPath)) return null;

            string json;
            try { json = File.ReadAllText(manifestPath); }
            catch (Exception e)
            {
                MelonLogger.Warning($"Failed to read shader pack manifest in '{folder}': {e.Message}");
                return null;
            }

            ShaderPack pack = Parse(json, folder, Path.GetFileName(folder), true);
            if (pack == null) return null;

            pack.Folder = folder;
            pack.Id = pack.Name;
            return pack;
        }

        // A pack handed over by another mod as raw bytes (RRS.RegisterPack). The
        // manifest's "bundle" key is not required here — we already hold the bundle,
        // so there is no filename to resolve.
        public static ShaderPack FromMemory(string modId, string manifestJson, byte[] bundle)
        {
            ShaderPack pack = Parse(manifestJson, "embedded in " + modId, modId, false);
            if (pack == null) return null;

            pack.EmbeddedBundle = bundle;
            pack.OwnerModId = modId;
            pack.Id = modId + "." + pack.Name;
            return pack;
        }

        // `sourceLabel` only ever appears in log messages — it is whatever will help a
        // user find the thing that is broken, a folder path or a mod id.
        private static ShaderPack Parse(string json, string sourceLabel, string fallbackName, bool requireBundleFile)
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(json);
                JsonElement root = doc.RootElement;

                ShaderPack pack = new ShaderPack
                {
                    Name = GetString(root, "name", fallbackName),
                    Author = GetString(root, "author", ""),
                    Version = GetString(root, "version", ""),
                    BundleFile = GetString(root, "bundle", null),
                    MaterialName = GetString(root, "material", null),
                    Priority = GetInt(root, "priority", 0),
                    Queue = GetInt(root, "queue", 0),
                    Passes = GetInt(root, "passes", 1),
                };

                if (pack.Passes < 1) pack.Passes = 1;
                if (pack.Passes > MaxPasses)
                {
                    MelonLogger.Warning($"Shader pack '{pack.Name}': \"passes\": {pack.Passes} exceeds the " +
                        $"limit of {MaxPasses} — clamped. Each pass is a full-screen blit per eye.");
                    pack.Passes = MaxPasses;
                }

                if (pack.MaterialName == null || (requireBundleFile && pack.BundleFile == null))
                {
                    MelonLogger.Warning($"Shader pack '{pack.Name}' ({sourceLabel}): manifest must define " +
                        (requireBundleFile ? "\"bundle\" and \"material\"." : "\"material\"."));
                    return null;
                }

                if (root.TryGetProperty("parameters", out JsonElement parameters) && parameters.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement p in parameters.EnumerateArray())
                    {
                        string property = GetString(p, "property", null);
                        if (property == null) continue;
                        pack.Parameters.Add(new PackParameter
                        {
                            Property = property,
                            Label = GetString(p, "label", property.TrimStart('_')),
                            Default = GetFloat(p, "default", 0f),
                            Min = GetFloat(p, "min", 0f),
                            Max = GetFloat(p, "max", 1f),
                        });
                    }
                }

                if (root.TryGetProperty("listen", out JsonElement listen) && listen.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement c in listen.EnumerateArray())
                    {
                        if (c.ValueKind != JsonValueKind.String) continue;
                        string channel = c.GetString();
                        if (string.IsNullOrEmpty(channel)) continue;
                        pack.Listen.Add(channel);
                    }
                }

                return pack;
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"Failed to read shader pack manifest ({sourceLabel}): {e.Message}");
                return null;
            }
        }

        private static string GetString(JsonElement element, string key, string fallback)
        {
            if (element.TryGetProperty(key, out JsonElement value) && value.ValueKind == JsonValueKind.String)
                return value.GetString();
            return fallback;
        }

        private static int GetInt(JsonElement element, string key, int fallback)
        {
            if (element.TryGetProperty(key, out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int result))
                return result;
            return fallback;
        }

        private static float GetFloat(JsonElement element, string key, float fallback)
        {
            if (element.TryGetProperty(key, out JsonElement value) && value.ValueKind == JsonValueKind.Number)
                return (float)value.GetDouble();
            return fallback;
        }

        public bool EnsureLoaded()
        {
            if (Material != null) return true;
            if (LoadFailed) return false;

            string bundlePath = null;
            if (!IsEmbedded)
            {
                bundlePath = Path.Combine(Folder, BundleFile);
                if (!File.Exists(bundlePath))
                {
                    MelonLogger.Warning($"Shader pack '{Name}': bundle file not found: {bundlePath}");
                    LoadFailed = true;
                    return false;
                }
            }

            try
            {
                if (IsEmbedded)
                {
                    // Same Unity-version and compression rules as a file bundle — the
                    // bytes just never landed on disk. LoadFromMemory is tried first and
                    // the proven stream path is the fallback, mirroring the file branch.
                    try { Bundle = AssetBundle.LoadFromMemory(EmbeddedBundle); }
                    catch (Exception e)
                    {
                        MelonLogger.Warning($"Shader pack '{Name}': LoadFromMemory threw: {e.Message}");
                        Bundle = null;
                    }
                    if (Bundle == null)
                        Bundle = LoadViaStream(EmbeddedBundle);
                }
                else
                {
                    try { Bundle = AssetBundle.LoadFromFile(bundlePath); }
                    catch (Exception e)
                    {
                        MelonLogger.Warning($"Shader pack '{Name}': LoadFromFile threw: {e.Message}");
                        Bundle = null;
                    }
                    if (Bundle == null)
                        Bundle = LoadViaStream(File.ReadAllBytes(bundlePath));
                }

                if (Bundle == null)
                {
                    MelonLogger.Warning($"Shader pack '{Name}' ({SourceLabel}): Unity refused the bundle. This game runs Unity " +
                        $"{Application.unityVersion} — the pack must be built with that exact editor version " +
                        "(AssetBundles are not compatible across Unity's 2025 security-format change). " +
                        "Other causes: LZMA compression (rebuild with the template, which uses LZ4), or two packs sharing the same name." +
                        (IsEmbedded ? $" This pack ships inside '{OwnerModId}' — you cannot fix it yourself; report it to that mod's author." : ""));
                    LoadFailed = true;
                    return false;
                }

                Material = Bundle.LoadAsset<Material>(MaterialName);
                if (Material == null)
                {
                    MelonLogger.Warning($"Shader pack '{Name}': material '{MaterialName}' not found in bundle.");
                    Unload();
                    LoadFailed = true;
                    return false;
                }

                if (Material.shader == null || !Material.shader.isSupported)
                    MelonLogger.Warning($"Shader pack '{Name}': shader reports unsupported — it may render magenta. Rebuild the pack with Unity {Application.unityVersion}.");

                Material.renderQueue = EffectiveQueue;
                ApplyParameters();
                MelonLogger.Msg($"Shader pack '{Name}' loaded (queue {EffectiveQueue}, {SourceLabel}).");
                LogChannelBinding();
                return true;
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"Shader pack '{Name}': load failed: {e.Message}");
                Unload();
                LoadFailed = true;
                return false;
            }
        }

        private static AssetBundle LoadViaStream(byte[] managedBytes)
        {
            try
            {
                Il2CppStructArray<byte> bytes = managedBytes;
                Il2CppSystem.IO.MemoryStream stream = new Il2CppSystem.IO.MemoryStream(bytes);
                return AssetBundle.LoadFromStream(stream);
            }
            catch (Exception e)
            {
                MelonLogger.Warning("Stream bundle load failed: " + e.Message);
                return null;
            }
        }

        public void ApplyParameters()
        {
            if (Material == null) return;
            foreach (PackParameter p in Parameters)
            {
                float value = p.Setting != null ? Mathf.Clamp(p.Setting.Value, p.Min, p.Max) : p.Default;
                Material.SetFloat(p.Property, value);
            }
            // Resolve the manifest's channel names to ids. Set every frame alongside the
            // parameters rather than once at load, so "Reload packs" picks up a manifest
            // edit without a restart.
            for (int i = 0; i < Listen.Count; i++)
                Material.SetFloat("_RRSChannel" + i, EventBuffer.ChannelId(Listen[i]));
        }

        // Report, per listened channel, the resolved id AND whether the shader actually
        // declares the matching _RRSChannelN property.
        //
        // Material.SetFloat on a property the shader does not declare is a SILENT no-op.
        // That is the one failure that looks identical to "the event never fired" from
        // outside — and it happens whenever a manifest gains a channel but the bundle
        // wasn't rebuilt. Compare the printed id against the "[event] first '<channel>'"
        // line to confirm both sides agree.
        private void LogChannelBinding()
        {
            if (Listen.Count == 0) return;

            for (int i = 0; i < Listen.Count; i++)
            {
                string property = "_RRSChannel" + i;
                bool declared = Material.HasProperty(property);
                float id = EventBuffer.ChannelId(Listen[i]);

                if (declared)
                {
                    MelonLogger.Msg($"Shader pack '{Name}': {property} = '{Listen[i]}' (id {id}).");
                }
                else
                {
                    MelonLogger.Warning($"Shader pack '{Name}': manifest listens to '{Listen[i]}' but the " +
                        $"shader does not declare '{property}' — events on that channel will do NOTHING. " +
                        "Add the property to the shader and REBUILD the bundle (a manifest-only change " +
                        "is not enough).");
                }
            }
        }

        // Put every slider back to its manifest default. Writes through the
        // MelonPreferences entries rather than the material, so the change persists and
        // the UI redraws; ApplyParameters() picks it up on the next frame anyway.
        public void ResetParameters()
        {
            foreach (PackParameter p in Parameters)
                if (p.Setting != null) p.Setting.Value = p.Default;
            // Order is a manifest value like any other, so "Reset to defaults" puts the
            // stacking back to what the pack author intended too.
            if (OrderSetting != null) OrderSetting.Value = DefaultOrder;
            MelonPreferences.Save();
        }

        // Drops the loaded Unity objects so the next EnsureLoaded() rebuilds them.
        // EmbeddedBundle is NOT cleared — those bytes are this pack's only copy of
        // itself, so clearing them would make "Reload packs" permanently kill every
        // mod-supplied pack until the game restarts.
        public void Unload()
        {
            try { if (Bundle != null) Bundle.Unload(true); } catch { }
            Bundle = null;
            Material = null;
            LoadFailed = false;
        }
    }
}
