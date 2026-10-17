using System.Collections.Generic;
using MelonLoader;
using RumbleReShaded.Api;
using UnityEngine;

namespace RumbleReShaded
{
    // Temporary changes other mods layer over a pack (RRS.Override), never written to the
    // player's settings.
    //
    // WHY NOT JUST WRITE THE MELONPREFERENCES ENTRIES — which is what a consumer had to do
    // before this existed:
    //
    //   • A crash, a force-quit or a forgotten restore leaves the player's saved settings
    //     holding someone else's look. MelonLoader saves preferences on exit, so the damage
    //     outlives the session that caused it.
    //   • Two mods doing it at once each "restore" the other's values.
    //   • The menu shows the borrowed values as if the player had chosen them, and a slider
    //     drag mid-effect fights the mod every frame.
    //
    // An override lives only in memory. The player's entries are never touched, so the menu
    // keeps showing their own choices and there is nothing to restore if the game dies.
    //
    // Effective value = the player's value, then each live override on that pack applied in
    // the order they were created: value = lerp(value, override, weight). The same rule
    // gives a fade (animate the weight) and lets two mods stack predictably.
    internal static class PackOverrides
    {
        private static readonly List<PackOverride> live = [];
        private static readonly object gate = new();

        public static PackOverride Create(string modId, ShaderPack pack)
        {
            var created = new PackOverride(modId, pack.Id);
            lock (gate) live.Add(created);
            MelonLogger.Msg($"RumbleReShaded: '{modId}' is overriding pack '{pack.Name}' (temporary - the player's settings are untouched).");
            return created;
        }

        public static void Remove(PackOverride removed)
        {
            lock (gate) live.Remove(removed);
        }

        // On/off as the frame should see it. The last live override that says something about
        // on/off wins; one with weight 0 is treated as not there, so a fade-out ending at 0 hands
        // the pack back to the player.
        public static bool EffectiveEnabled(ShaderPack pack, bool playerEnabled)
        {
            var enabled = playerEnabled;
            lock (gate)
            {
                foreach (var o in live)
                {
                    if (o.PackId != pack.Id || o.Weight <= 0f || o.Enabled == null) continue;
                    enabled = o.Enabled.Value;
                }
            }
            return enabled;
        }

        public static float EffectiveValue(ShaderPack pack, PackParameter parameter, float playerValue)
        {
            var value = playerValue;
            lock (gate)
            {
                foreach (var o in live)
                {
                    if (o.PackId != pack.Id || o.Weight <= 0f) continue;
                    if (!o.TryGetValue(parameter.Property, out var wanted)) continue;
                    value = Mathf.Lerp(value, wanted, o.Weight);
                }
            }
            return Mathf.Clamp(value, parameter.Min, parameter.Max);
        }

        // An override names a parameter the way a person would: by the manifest's label or its
        // shader property, ignoring case, spaces, underscores and a leading '_'. Resolved once,
        // when set, so a typo is reported at the call site rather than silently doing nothing.
        public static PackParameter FindParameter(ShaderPack pack, string name)
        {
            var wanted = Squash(name);
            foreach (var p in pack.Parameters)
                if (Squash(p.Property) == wanted || Squash(p.Label) == wanted) return p;
            return null;
        }

        private static string Squash(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var chars = new char[text.Length];
            var n = 0;
            foreach (var c in text)
                if (char.IsLetterOrDigit(c)) chars[n++] = char.ToLowerInvariant(c);
            return new string(chars, 0, n);
        }

        public static int LiveCount { get { lock (gate) return live.Count; } }
    }
}
