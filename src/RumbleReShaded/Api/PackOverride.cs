using System;
using System.Collections.Generic;
using MelonLoader;

namespace RumbleReShaded.Api
{
    /// <summary>
    /// A temporary change to one shader pack, made by your mod and never saved to the player's
    /// settings. Get one from <see cref="RRS.Override"/>; dispose it to hand the pack back.
    /// </summary>
    /// <remarks>
    /// <para>The player's own on/off and slider values are never written, so their menu keeps
    /// showing their choices, and a crash or force-quit cannot leave your look in their saved
    /// settings.</para>
    ///
    /// <para><see cref="Weight"/> blends each parameter between the value underneath and yours,
    /// so animating it from 0 to 1 is a fade. Overrides from several mods on the same pack apply in
    /// the order they were created, each blending over the last.</para>
    ///
    /// <para>Safe to keep across "Reload packs": an override follows its pack by id, and picks the
    /// pack up again once it is back.</para>
    /// </remarks>
    public sealed class PackOverride : IDisposable
    {
        private readonly Dictionary<string, float> values = new(StringComparer.Ordinal);
        private float weight = 1f;

        internal PackOverride(string modId, string packId)
        {
            ModId = modId;
            PackId = packId;
        }

        /// <summary>The mod that made this override.</summary>
        public string ModId { get; }

        /// <summary>The overridden pack's id (its name, or "modid.name" for a mod-supplied pack).</summary>
        public string PackId { get; }

        /// <summary>False once disposed; a disposed override does nothing.</summary>
        public bool Active { get; private set; } = true;

        /// <summary>
        /// How much of this override applies, 0..1 (clamped). 0 is as if it were not there - the
        /// pack's on/off goes back to the player's as well.
        /// </summary>
        public float Weight
        {
            get => weight;
            set => weight = value < 0f ? 0f : value > 1f ? 1f : value;
        }

        /// <summary>Turns the pack on or off while this override has weight; null leaves the player's choice.</summary>
        public bool? Enabled { get; set; }

        /// <summary>
        /// Sets a parameter, named by its manifest label or shader property (case, spaces and
        /// underscores ignored). Clamped to the parameter's range when applied.
        /// </summary>
        /// <returns>False, with a one-time warning, when the pack has no such parameter.</returns>
        public bool Set(string parameter, float value)
        {
            if (!Active) return false;

            var pack = RumbleReShadedMod.FindPack(PackId);
            var found = pack == null ? null : PackOverrides.FindParameter(pack, parameter);
            if (found == null)
            {
                EventBuffer.WarnOnce($"override:{PackId}:{parameter}",
                    $"RumbleReShaded: '{ModId}' tried to override '{parameter}' on pack '{PackId}', which has no such parameter" +
                    (pack == null ? " (the pack is not loaded)." : $". It has: {string.Join(", ", pack.Parameters.ConvertAll(p => p.Label))}."));
                return false;
            }

            values[found.Property] = value;
            return true;
        }

        /// <summary>Stops overriding one parameter; the value underneath shows again.</summary>
        public void Clear(string parameter)
        {
            var pack = RumbleReShadedMod.FindPack(PackId);
            var found = pack == null ? null : PackOverrides.FindParameter(pack, parameter);
            if (found != null) values.Remove(found.Property);
        }

        /// <summary>Hands the pack back to the player. Safe to call more than once.</summary>
        public void Dispose()
        {
            if (!Active) return;
            Active = false;
            PackOverrides.Remove(this);
            MelonLogger.Msg($"RumbleReShaded: '{ModId}' released its override of pack '{PackId}'.");
        }

        internal bool TryGetValue(string property, out float value) => values.TryGetValue(property, out value);
    }
}
