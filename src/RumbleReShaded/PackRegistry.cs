using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using MelonLoader;

namespace RumbleReShaded
{
    // Holding pen for packs handed over by OTHER mods via RumbleReShaded.Api.RRS.RegisterPack.
    //
    // WHY A HOLDING PEN AND NOT A DIRECT ADD — the load-order problem:
    //
    // A consumer mod declares [MelonAdditionalDependencies("RumbleReShaded")], which makes
    // MelonLoader load US FIRST. That is correct for the API (RRS.Fire must exist before
    // they call it) but backwards for registration: our OnInitializeMelon would finish —
    // menu and all — before the consumer had a chance to register anything.
    //
    // So the sequence is deliberately split across MelonLoader's two init phases:
    //
    //   OnInitializeMelon      us: open registration (ApiReady = true)
    //   OnInitializeMelon      consumers: RRS.RegisterPack(...)  → lands here
    //   OnLateInitializeMelon  us: Close(), merge into the pack list, build the menu
    //
    // MelonLoader runs EVERY mod's OnInitializeMelon before ANY OnLateInitializeMelon, so
    // this catches every consumer regardless of load order — including one that loads
    // before us and registers into a static list that has not been touched yet.
    //
    // This class is deliberately usable before RumbleReShadedMod has initialised: nothing
    // here reads Unity or Melon state.
    internal static class PackRegistry
    {
        private static readonly List<ShaderPack> pending = new List<ShaderPack>();
        private static readonly object gate = new object();

        // Set once the menu has been built. Anything arriving after this can never get a
        // UI category (UIFramework layout is built exactly once), so it is rejected loudly
        // rather than silently half-working.
        private static bool closed;

        public static bool Register(string modId, string manifestJson, byte[] bundle)
        {
            if (!ValidateModId(modId)) return false;

            if (string.IsNullOrEmpty(manifestJson))
            {
                MelonLogger.Warning($"RumbleReShaded: '{modId}' registered a pack with an empty manifest — ignored.");
                return false;
            }
            if (bundle == null || bundle.Length == 0)
            {
                MelonLogger.Warning($"RumbleReShaded: '{modId}' registered a pack with no bundle bytes — ignored. " +
                    "If you are loading it from an embedded resource, check the resource name " +
                    "(Assembly.GetManifestResourceNames() lists what actually got compiled in).");
                return false;
            }

            lock (gate)
            {
                if (closed)
                {
                    MelonLogger.Warning($"RumbleReShaded: '{modId}' tried to register a shader pack too late — ignored. " +
                        "Registration closes once the settings menu is built. Call RRS.RegisterPack from your mod's " +
                        "OnInitializeMelon (not OnLateInitializeMelon, not from a scene load).");
                    return false;
                }
            }

            ShaderPack pack = ShaderPack.FromMemory(modId, manifestJson, bundle);
            if (pack == null) return false;   // Parse already logged why

            lock (gate)
            {
                if (pending.Exists(p => p.Id == pack.Id))
                {
                    MelonLogger.Warning($"RumbleReShaded: '{modId}' registered two packs named '{pack.Name}' — " +
                        "keeping the first. Pack names must be unique within a mod.");
                    return false;
                }
                pending.Add(pack);
            }

            MelonLogger.Msg($"Shader pack '{pack.Name}' registered by '{modId}' ({bundle.Length / 1024} KB, in memory).");
            return true;
        }

        // Convenience path: pull both files straight out of the calling assembly.
        // Resource names are matched by SUFFIX, because the compiler prefixes them with
        // the default namespace and folder path ("MyMod.Resources.manifest.json") and
        // people get that wrong far more often than they get the filename wrong.
        public static bool RegisterFromAssembly(string modId, Assembly asm, string manifestResource, string bundleResource)
        {
            if (asm == null)
            {
                MelonLogger.Warning($"RumbleReShaded: '{modId}' passed a null assembly to RegisterPack — ignored.");
                return false;
            }

            byte[] manifest = ReadResource(modId, asm, manifestResource, "manifest");
            byte[] bundle = ReadResource(modId, asm, bundleResource, "bundle");
            if (manifest == null || bundle == null) return false;

            // The manifest is authored text; BOM-tolerant decode so a file saved from
            // Notepad or Visual Studio does not blow up JsonDocument.Parse on char 0.
            string json = new System.Text.UTF8Encoding(false, false).GetString(manifest).TrimStart('﻿');
            return Register(modId, json, bundle);
        }

        private static byte[] ReadResource(string modId, Assembly asm, string nameOrSuffix, string what)
        {
            string[] all = asm.GetManifestResourceNames();
            string match = null;
            int matches = 0;

            foreach (string n in all)
            {
                if (n == nameOrSuffix || n.EndsWith("." + nameOrSuffix, StringComparison.OrdinalIgnoreCase) ||
                    n.EndsWith(nameOrSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    if (n == nameOrSuffix) { match = n; matches = 1; break; }
                    match = n;
                    matches++;
                }
            }

            if (matches == 0)
            {
                MelonLogger.Warning($"RumbleReShaded: '{modId}' has no embedded {what} resource matching " +
                    $"'{nameOrSuffix}'. Embedded resources in that DLL: " +
                    (all.Length == 0 ? "(none — is Build Action set to EmbeddedResource?)" : string.Join(", ", all)));
                return null;
            }
            if (matches > 1)
            {
                MelonLogger.Warning($"RumbleReShaded: '{modId}' has {matches} embedded resources ending in " +
                    $"'{nameOrSuffix}' — ambiguous, ignored. Pass the full resource name instead.");
                return null;
            }

            try
            {
                using Stream s = asm.GetManifestResourceStream(match);
                if (s == null) return null;
                using MemoryStream ms = new MemoryStream();
                s.CopyTo(ms);
                return ms.ToArray();
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"RumbleReShaded: '{modId}' — failed to read embedded {what} '{match}': {e.Message}");
                return null;
            }
        }

        // Called once, from OnLateInitializeMelon, immediately before the menu is built.
        public static List<ShaderPack> Close()
        {
            lock (gate)
            {
                closed = true;
                List<ShaderPack> result = new List<ShaderPack>(pending);
                pending.Clear();
                return result;
            }
        }

        // A mod id becomes part of a MelonPreferences category identifier, so keep it to
        // characters that cannot surprise a config file or a log line. Same spirit as the
        // channel namespacing rule in EventBuffer — and equally impossible to tighten
        // once the first consumer has shipped against it.
        private static bool ValidateModId(string modId)
        {
            if (string.IsNullOrEmpty(modId))
            {
                MelonLogger.Warning("RumbleReShaded: RegisterPack called with an empty mod id — ignored. " +
                    "Pass a short stable id for your mod (e.g. \"smudgemod\").");
                return false;
            }

            foreach (char c in modId)
            {
                if (char.IsLetterOrDigit(c) || c == '_' || c == '-') continue;
                MelonLogger.Warning($"RumbleReShaded: mod id '{modId}' contains '{c}' — ignored. " +
                    "Use letters, digits, '_' or '-' only (it becomes part of a settings category id).");
                return false;
            }

            if (modId.StartsWith("rrs", StringComparison.OrdinalIgnoreCase) && modId.Length == 3)
            {
                MelonLogger.Warning("RumbleReShaded: mod id 'rrs' is reserved for built-in packs — ignored.");
                return false;
            }

            return true;
        }
    }
}
