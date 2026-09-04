using System;
using HarmonyLib;
using Il2CppRUMBLE.MoveSystem;
using MelonLoader;
using UnityEngine;

namespace RumbleReShaded
{
    // Built-in triggers: RumbleReShaded firing its own events off real gameplay, so
    // the system does something useful with no second mod installed.
    //
    // All built-ins live under the reserved "rrs." namespace — the same namespacing
    // rule third-party mods must follow, so a built-in can never collide with one.
    //
    // FOUND BY DUMPING Il2CppRUMBLE.Runtime.dll (2026-08-04), not by guessing. The
    // relevant surface on Il2CppRUMBLE.MoveSystem.Structure:
    //   void OnCollisionEnter(Collision collision)      <- what we use
    //   void OnPreInterpolatedDestroy()
    //   void OnPhysicsStateChanged(PhysicsState previous, PhysicsState now)
    //   enum PhysicsState { Free = 0, FreeGrounded = 1, StableGrounded = 2,
    //                       Frozen = 3, Floating = 4 }
    //   UnityEvent onStructureDestroyed;  bool IsGrounded;  Vector3 CurrentVelocity
    //
    // TRIED AND ABANDONED (2026-08-04): OnPhysicsStateChanged, airborne -> grounded.
    // It looked like the perfect hook and produced nothing visible in a match. Not
    // re-diagnosed — Peter asked for the simpler literal "started touching something"
    // instead, so that is what this now does. If you revisit it, first confirm whether
    // the patch was firing at all (the per-channel first-fire log added to
    // EventBuffer.Fire will tell you).
    // Re-dump with C:/temp/apidump2d (`dotnet run -c Release MoveSystem.Structure`)
    // if a game update moves any of this.
    internal static class Hooks
    {
        // Channels fired here. Public so the eventual API docs can list them, and so a
        // pack author has one place to copy the exact strings from.
        public const string StructureLanded = "rrs.structure.landed";
        public const string StructureDestroyed = "rrs.structure.destroyed";

        private static bool warned;

        // Time-sampled, NOT a first-N cap. The previous version logged the first 10
        // landings and burned all ten inside a 74 ms burst at match start — hiding the
        // only thing worth knowing, which is whether anything fires during gameplay.
        // A cap answers "did this ever happen"; sampling answers "is this still
        // happening", and that was the actual question.
        private const float LogInterval = 1f;
        private static readonly System.Collections.Generic.Dictionary<string, float> lastLogged
            = new System.Collections.Generic.Dictionary<string, float>();

        // Set once if reading the Collision object throws, so we try exactly once and
        // then leave it alone. The event itself is fired BEFORE this runs, so a
        // stripping failure here costs us information, never the effect.
        private static bool probeFailed;
        private static float lastProbeLog;

        // Layer of whatever the structure hit, or -1 if it can't be read. Same guarded
        // access as ProbeCollision — a stripping failure must degrade the filter, never
        // throw into the game's collision handler.
        private static int HitLayer(Collision collision)
        {
            if (probeFailed || collision == null) return -1;
            try
            {
                GameObject other = collision.gameObject;
                return other == null ? -1 : other.layer;
            }
            catch
            {
                probeFailed = true;
                return -1;
            }
        }

        // Report what a structure actually collided with. Deliberately a separate method
        // with its own try/catch: Collision's members are the ones Il2Cpp stripped last
        // time, and this must not be able to take the ring down with it.
        private static void ProbeCollision(Collision collision, float velocityY, float speed)
        {
            if (probeFailed || collision == null) return;

            float now = Time.time;
            if (now - lastProbeLog < 0.4f) return;
            lastProbeLog = now;

            try
            {
                GameObject other = collision.gameObject;
                if (other == null) return;

                int layer = other.layer;
                MelonLogger.Msg($"[probe] structure hit '{other.name}' " +
                    $"layer {layer} ('{LayerMask.LayerToName(layer)}'), " +
                    $"vel.y {velocityY:F1}, speed {speed:F1} m/s.");
            }
            catch (Exception e)
            {
                probeFailed = true;
                MelonLogger.Warning($"RumbleReShaded: collision probe unavailable " +
                    $"({e.GetType().Name}: {e.Message}) — Collision.gameObject is stripped in this " +
                    "build, so the floor-vs-player filter needs a different source. " +
                    "Events are unaffected.");
            }
        }

        private static void LogSampled(string hook, Vector3 pos, float speed, float strength)
        {
            float now = Time.time;
            if (lastLogged.TryGetValue(hook, out float last) && now - last < LogInterval) return;
            lastLogged[hook] = now;
            // Strength logged next to the speed that produced it, so the curve can be
            // judged from the log rather than from memory of how bright a ring looked —
            // "3.1 m/s -> 0.86" is checkable, "that felt about right" is not.
            //
            // Live count catches buffer flooding: if collisions fire fast enough to keep
            // all MaxEvents slots occupied, events are evicted before a frame renders
            // them, which looks exactly like "the channel is broken". Known to happen on
            // scene load — ~89 structures settle at once in the Gym.
            MelonLogger.Msg($"[event] {hook}: structure at {pos}, speed {speed:F1} m/s " +
                $"-> strength {strength:F2}, {EventBuffer.LiveCount}/{EventBuffer.MaxEvents} slots live.");
        }

        // Below this impact speed we don't fire. A structure at rest generates a
        // stream of OnCollisionEnter calls as it settles and jostles; this is what
        // stops a resting structure from strobing, and it needs no cooldown
        // bookkeeping to do it.
        private const float MinImpactSpeed = 1.5f;

        // Only used by the fallback path below, if the collision object can't be read.
        private const float MinDownwardSpeed = 0.5f;

        // Impact speed that counts as a full-strength landing. MEASURED, not guessed:
        // real floor landings probe at 1.6-3.6 m/s (2026-08-04). The original code used
        // `speed / 12f`, and that 12 m/s reference is why landings were invisible —
        // every real landing scored the 0.25 clamp floor.
        //
        // Mapping the measured range onto the visible range, rather than dividing by a
        // reference, keeps these numbers readable against the probe log: the slowest
        // landing that fires at all looks like MinLandingStrength, the hardest observed
        // looks like 1.0.
        private const float HardLandingSpeed = 3.6f;

        // Floor, not zero: a landing that passed MinImpactSpeed is a real event and
        // should be visible. Below roughly this it reads as "nothing happened" — which
        // is exactly how the old 0.25 clamp failed.
        private const float MinLandingStrength = 0.4f;

        // 8 m is kept from the diagnostic because it is the value proven readable in VR.
        //
        // Lifetime drops 2 s -> 1.2 s: occupancy is rate x duration
        // (docs/TRIGGER_API.md §3), so at 90 fps a 2 s ring holds its slot for ~180
        // frames, and a landing flurry is exactly when the 32 slots are scarcest.
        // Deliberately NOT scaled with strength — that would lengthen occupancy
        // precisely when the buffer is busiest.
        private const float LandingRadius = 8f;
        private const float LandingDuration = 1.2f;

        // RUMBLE's arena floor. Measured from live collision probes, 2026-08-04:
        //
        //   layer  9  CombatFloor              "Collission combat floor"   <- a landing
        //   layer 10  Environment              walls, slabs, "Button Rock"
        //   layer 14  Move                     "Fruit"
        //   layer 23  PlayerController         "Physics"
        //   layer 27  PlayerPhysicsTransform   "Left/RightPhysicsController"
        //
        // Layers 23/27 are why velocity direction could never work as a floor test:
        // a hand contact was observed at vel.y -3.6, indistinguishable from a fall
        // (Peter, 2026-08-04). Only the layer separates them.
        //
        // Re-measure with the [probe] logging if a game update renumbers layers.
        private const int CombatFloorLayer = 9;

        // "Structure not touching something -> structure touching it." Peter asked for
        // this explicitly (2026-08-04) over the PhysicsState transition, which produced
        // no visible rings in a match.
        //
        // Note this fires on ANY collision, not just ground — structure-on-structure and
        // structure-on-player included. That is arguably more useful for an impact
        // effect than a strict ground test, but it IS a behaviour difference worth
        // knowing if a pack starts ringing more than expected.
        [HarmonyPatch(typeof(Structure), nameof(Structure.OnCollisionEnter))]
        private static class StructureCollisionPatch
        {
            // Postfix, not prefix: let the game finish its own collision handling
            // first. Never throw out of here — an exception inside a patch on a hot
            // gameplay method is a far worse failure than a missing visual effect.
            //
            // ⚠ The `Collision collision` parameter is deliberately NOT taken.
            //
            // Taking it and reading collision.relativeVelocity / .contactCount /
            // .GetContact(0) threw "Method unstripping failed" on every impact
            // (2026-08-04, caught in Latest.log). Il2Cpp builds strip methods nothing in
            // the game calls, and RUMBLE's own code never touches those Collision
            // members — so they aren't in the build to call. Harmony lets a patch omit
            // parameters it doesn't need, and Structure exposes equivalents that the
            // game DOES use and therefore survive stripping.
            //
            // RULE for any future hook here: prefer members the game itself calls.
            // A Unity API existing in the editor says nothing about it existing in an
            // Il2Cpp build.
            // `Collision collision` IS taken here, unlike the earlier attempt — but
            // nothing reads it outside ProbeCollision, which guards itself. Taking the
            // parameter is not what failed before; reading Collision.relativeVelocity /
            // .contactCount / .GetContact(0) was.
            private static void Postfix(Structure __instance, Collision collision)
            {
                try
                {
                    if (__instance == null) return;

                    // Structure's own property, used by the game, so it survives
                    // stripping — unlike Collision.relativeVelocity.
                    Vector3 velocity = __instance.CurrentVelocity;
                    float speed = velocity.magnitude;
                    if (speed < MinImpactSpeed) return;

                    ProbeCollision(collision, velocity.y, speed);

                    // WHITELIST the floor layer rather than blacklisting the player.
                    // Anything unrecognised stays silent, which is the safer default —
                    // a new prop or another structure can't start ringing by accident.
                    int layer = HitLayer(collision);
                    if (layer >= 0)
                    {
                        if (layer != CombatFloorLayer) return;
                    }
                    else
                    {
                        // Collision.gameObject unavailable (stripped). Fall back to the
                        // old velocity heuristic — worse, but better than firing never.
                        if (velocity.y > -MinDownwardSpeed) return;
                    }

                    // Structure origin rather than the contact point. Slightly less
                    // precise than GetContact(0).point would have been, but that call
                    // does not exist in this build.
                    Vector3 pos = __instance.transform.position;

                    // Structures are POOLED (Structure : PooledMonoBehaviour). A
                    // recycled one reports transform.position == exactly (0,0,0) while
                    // it is being returned to the pool, and firing on that put a ring at
                    // the world origin every time something was destroyed (Peter,
                    // 2026-08-04; visible in the log as "structure at (0.00, 0.00, 0.00),
                    // speed 14.1 m/s").
                    //
                    // Exact-zero test on purpose: a real structure sitting precisely at
                    // the world origin is possible but vanishingly unlikely, and every
                    // observed bad sample was exactly zero, not near it.
                    if (pos == Vector3.zero) return;

                    // Speed-scaled strength, restored 2026-08-05 after the rebuilt bundle
                    // confirmed BOTH channels ring. The flat diagnostic that lived here
                    // fired (8f, 1f, 2f) — identical to the test button — so that position
                    // was the only variable left while proving the hook worked.
                    //
                    // A gentle touchdown and a slam should not look the same; this maps
                    // the measured 1.5-3.6 m/s band onto 0.4-1.0 brightness.
                    float t = Mathf.InverseLerp(MinImpactSpeed, HardLandingSpeed, speed);
                    float strength = Mathf.Lerp(MinLandingStrength, 1f, t);

                    LogSampled("OnCollisionEnter", pos, speed, strength);
                    Api.RRS.Fire(StructureLanded, pos, LandingRadius, strength, LandingDuration);
                }
                catch (Exception e)
                {
                    WarnOnce("OnCollisionEnter", e);
                }
            }
        }

        // TRIED AND REMOVED (2026-08-04): Structure.PlayCollisionPresence — the game's
        // own "this impact deserves an effect" call, gated by collisionPresenceTreshold.
        // It looked like the ideal hook and NEVER FIRED ONCE across a full match of
        // slams (patched, no exception logged, zero log lines). Don't re-add it without
        // first confirming it is called at all.
        [HarmonyPatch(typeof(Structure), nameof(Structure.OnPreInterpolatedDestroy))]
        private static class StructureDestroyPatch
        {
            private static void Postfix(Structure __instance)
            {
                try
                {
                    if (__instance == null) return;
                    // Same pooled-structure origin guard as above.
                    Vector3 pos = __instance.transform.position;
                    if (pos == Vector3.zero) return;
                    Api.RRS.Fire(StructureDestroyed, pos, 4f, 1f, 0.8f);
                }
                catch (Exception e)
                {
                    WarnOnce("OnPreInterpolatedDestroy", e);
                }
            }
        }

        // One warning total, not one per event — these sit on gameplay methods that
        // fire many times a second, and a failing patch would otherwise bury the log.
        private static void WarnOnce(string where, Exception e)
        {
            if (warned) return;
            warned = true;
            // Type and stack included, not just Message: the first version of this
            // logged "Method unstripping failed" with no indication of WHICH member
            // failed, which cost a round trip to work out (2026-08-04).
            MelonLogger.Warning($"RumbleReShaded: built-in trigger hook '{where}' threw and will be " +
                $"reported only once: {e.GetType().Name}: {e.Message}\n{e.StackTrace}\n" +
                "Events from it may be missing; the rest of the mod is unaffected.");
        }
    }
}
