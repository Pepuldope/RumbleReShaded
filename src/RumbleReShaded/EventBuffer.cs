using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MelonLoader;
using UnityEngine;

namespace RumbleReShaded
{
    // The wire between C# mods and shader packs. A mod calls RumbleReShaded.Api.RRS.Fire(),
    // which lands here; once per camera, every live event is packed into two global
    // Vector4 arrays that any pack shader can read. See docs/TRIGGER_API.md.
    //
    // Events are fire-and-forget: they live for their duration, then their slot frees.
    // Nothing is ever handed back out to C#.
    internal static class EventBuffer
    {
        // Concurrently-live events, NOT events per frame and NOT distinct channels
        // (channels are unlimited). Occupancy is rate x duration — a 2 s effect holds
        // its slot for ~180 frames at 90 fps, so this fills faster than it looks.
        //
        // PROVISIONAL (Peter, 2026-08-03) — revisit once per-pixel loop cost is measured
        // in VR. Deliberately a single constant: the shader loop is bounded by
        // _RRSEventCount rather than by array length, so live events cost the same
        // whatever this is set to, and changing it is a one-line edit here plus the
        // matching constant in RRSEvents.hlsl.
        //
        // Unity locks a global array's length on first set, so a change means the game
        // must be restarted, not just "Reload packs".
        public const int MaxEvents = 32;

        private sealed class LiveEvent
        {
            public float ChannelId;
            public Vector3 Position;
            public float Radius;
            public float Strength;
            public float Duration;
            public float StartTime;
        }

        // Allocated once. Per-frame Il2Cpp interop allocation in a VR frame loop is
        // exactly the wrong place to be sloppy.
        private static readonly Il2CppStructArray<Vector4> posArray = new Il2CppStructArray<Vector4>(MaxEvents);
        private static readonly Il2CppStructArray<Vector4> dataArray = new Il2CppStructArray<Vector4>(MaxEvents);

        private static readonly List<LiveEvent> live = new List<LiveEvent>(MaxEvents);
        // Recycled rather than reallocated when a slot expires.
        private static readonly Stack<LiveEvent> pool = new Stack<LiveEvent>(MaxEvents);

        // Fire() is documented as callable from any mod, and a coroutine or an async
        // callback is entirely plausible. A torn write here corrupts a frame rather than
        // crashing, which is the harder thing to diagnose — so guard it.
        private static readonly object gate = new object();

        // Keys/channels already warned about, so a mod firing an unnamespaced channel
        // every frame doesn't flood the log.
        private static readonly HashSet<string> warned = new HashSet<string>();

        private static bool pushedOnce;

        public static int LiveCount { get { lock (gate) return live.Count; } }

        // ---------------------------------------------------------------------

        public static void Fire(string channel, Vector3 worldPos, float radius, float strength, float duration)
        {
            if (!ValidateName(channel, "channel")) return;
            if (duration <= 0f) duration = 0.001f;

            float channelId = ChannelId(channel);
            float now = Time.time;

            // First event on each channel is logged once. Without this there is no way
            // to tell "the hook never fired" from "the hook fired but the pack didn't
            // react" — and those need completely different fixes. Cheap: one HashSet
            // probe per event, and it goes quiet after the first of each channel.
            LogFirstUse(channel, channelId, worldPos);

            lock (gate)
            {
                LiveEvent e;
                if (live.Count >= MaxEvents)
                {
                    // Full: recycle the oldest. This pops an effect out mid-animation,
                    // and it happens precisely during the busiest moment — which is the
                    // argument for keeping MaxEvents generous.
                    e = live[0];
                    live.RemoveAt(0);
                }
                else
                {
                    e = pool.Count > 0 ? pool.Pop() : new LiveEvent();
                }

                e.ChannelId = channelId;
                e.Position = worldPos;
                e.Radius = radius;
                e.Strength = strength;
                e.Duration = duration;
                e.StartTime = now;
                // Appended, so live[0] is always the oldest and recycling is O(1)-ish.
                live.Add(e);
            }
        }

        public static void SetGlobal(string key, float value)
        {
            if (!ValidateName(key, "global key")) return;
            Shader.SetGlobalFloat("_RRS_" + key, value);
        }

        // Built-in effects are build order step 4 (docs/TRIGGER_API.md §5) — they need a
        // pre-built bundle shipped inside the mod's own package. The public surface
        // exists now so consumers can compile against it; AvailableEffects being empty
        // is the documented way for them to find out it does nothing yet.
        private static readonly string[] availableEffects = new string[0];
        public static IReadOnlyList<string> AvailableEffects => availableEffects;

        public static void PlayNotImplemented(string effectId)
        {
            WarnOnce("play:" + effectId, $"RumbleReShaded: RRS.Play(\"{effectId}\") ignored — no built-in " +
                "effects ship in this build yet. Check RRS.AvailableEffects (currently empty) before calling.");
        }

        // Called once per camera from OnBeginCameraRendering, BEFORE the pass is
        // enqueued. Globals set while the render graph is being recorded are
        // order-dependent; setting them once up front is predictable.
        public static void PushGlobals()
        {
            float now = Time.time;
            int count = 0;

            lock (gate)
            {
                for (int i = live.Count - 1; i >= 0; i--)
                {
                    LiveEvent e = live[i];
                    float age = now - e.StartTime;
                    if (age >= e.Duration)
                    {
                        live.RemoveAt(i);
                        pool.Push(e);
                    }
                }

                count = live.Count;
                for (int i = 0; i < count; i++)
                {
                    LiveEvent e = live[i];
                    posArray[i] = new Vector4(e.Position.x, e.Position.y, e.Position.z, e.Radius);
                    // Age is computed here, in seconds, and pushed. Shaders must NOT
                    // derive it from _Time — that resets and wraps.
                    dataArray[i] = new Vector4(now - e.StartTime, e.Duration, e.ChannelId, e.Strength);
                }
            }

            // Always push the FULL array. Unity locks a global array's length on first
            // set; pushing a shorter one later silently truncates and is miserable to
            // debug. _RRSEventCount is what tells the shader how many entries are real.
            // (Stale entries past `count` are left as-is on purpose — never read.)
            Shader.SetGlobalVectorArray("_RRSEventPos", posArray);
            Shader.SetGlobalVectorArray("_RRSEventData", dataArray);
            Shader.SetGlobalFloat("_RRSEventCount", count);

            if (!pushedOnce)
            {
                pushedOnce = true;
                MelonLogger.Msg($"[diag] Event globals pushed for the first time ({MaxEvents} slots, {count} live).");
            }
        }

        // ---------------------------------------------------------------------

        // Channel ids must be stable across sessions AND across mod load orders, so a
        // registration-order counter is wrong. FNV-1a over the lowercased name is
        // deterministic and needs no registry.
        //
        // It is folded to 22 bits before becoming a float ON PURPOSE: these ids travel
        // in a float4 component, and a float's mantissa is 24 bits — a full 32-bit hash
        // would not survive the trip intact, so an == comparison in HLSL could fail or,
        // worse, collide unpredictably. 22 bits keeps every id an exactly-representable
        // integer. ~4M buckets is ample; collisions only matter between two channels a
        // single shader listens to.
        //
        // MUST stay identical to RRS_ChannelId() in RRSEvents.hlsl.
        public static float ChannelId(string channel)
        {
            uint hash = 2166136261u;
            string lower = channel.ToLowerInvariant();
            for (int i = 0; i < lower.Length; i++)
            {
                hash ^= lower[i];
                hash *= 16777619u;
            }
            return (hash & 0x3FFFFFu);
        }

        // Namespacing is enforced from the first published build, not bolted on later:
        // it cannot be tightened afterwards without breaking every consumer. Without it
        // two mods silently collide on "_RRS_charge" and the bug looks like a shader
        // problem in whichever mod loads second.
        private static bool ValidateName(string name, string what)
        {
            if (string.IsNullOrEmpty(name))
            {
                WarnOnce("<empty>", $"RumbleReShaded: empty {what} ignored.");
                return false;
            }

            int dot = name.IndexOf('.');
            // Must look like "modid.name" — a dot with something on both sides.
            if (dot <= 0 || dot >= name.Length - 1)
            {
                WarnOnce(name, $"RumbleReShaded: {what} '{name}' is not namespaced and was ignored. " +
                    "Use \"modid.name\" (e.g. \"mymod.hit\") so two mods can't collide. 'rrs.*' is reserved.");
                return false;
            }

            return true;
        }

        private static readonly HashSet<string> seenChannels = new HashSet<string>();

        private static void LogFirstUse(string channel, float channelId, Vector3 worldPos)
        {
            lock (gate)
            {
                if (!seenChannels.Add(channel)) return;
            }
            MelonLogger.Msg($"[event] first '{channel}' fired at {worldPos} (channel id {channelId}). " +
                "A pack reacts to it only if its manifest lists this exact name in \"listen\".");
        }

        private static void WarnOnce(string key, string message)
        {
            lock (gate)
            {
                if (!warned.Add(key)) return;
            }
            MelonLogger.Warning(message);
        }
    }
}
