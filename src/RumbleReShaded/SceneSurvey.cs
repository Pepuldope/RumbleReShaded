using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Il2CppRUMBLE.MoveSystem;
using MelonLoader;
using MelonLoader.Utils;
using UnityEngine;

namespace RumbleReShaded
{
    // Scene survey — the prerequisite for Stage 3 (docs/LAYERED_RENDERING_PLAN.md §5, §9 step 3).
    //
    // The mask pass is useless without knowing WHICH renderer is a player, a structure or map
    // geometry. The plan calls that the single biggest unknown in the whole per-category goal, and
    // says to answer it before committing to a design. This does exactly that and nothing else:
    // it reads the scene and writes a report. It never modifies anything.
    //
    // Output goes to a FILE, not the log. A VR session cannot be read while it happens, the
    // renderer count is in the hundreds, and MelonLogger would truncate and interleave it. The
    // file can be read at leisure afterwards.
    //
    // TWO QUESTIONS IT MUST ANSWER, in priority order:
    //
    //   1. Is `Renderer.renderingLayerMask` even USABLE in this Il2Cpp build?
    //      Stage 3 depends on it completely — the plan's non-negotiable rule is to set
    //      renderingLayerMask and NEVER gameObject.layer, because layers drive RUMBLE's physics.
    //      But Il2Cpp strips managed members the game never calls, and RUMBLE has no reason to
    //      touch renderingLayerMask. If it is stripped, Stage 3's filtering approach is dead on
    //      arrival and we need to know NOW, not after writing a render pass. Probed explicitly
    //      below, get and set, on a throwaway value that is restored immediately.
    //
    //   2. Are players/structures/map separable WITHOUT brittle name matching?
    //      The best possible answer is "every structure renderer has a Structure component in its
    //      parent chain" — that is a clean, dynamic, name-free classifier. So that specific
    //      question is asked directly rather than left to be inferred from a name dump.
    internal static class SceneSurvey
    {
        // Guard against a second press while one is running — FindObjectsOfType over a full
        // scene is not free, and two overlapping walks would just produce two truncated files.
        private static bool running;

        public static void Run()
        {
            if (running) { MelonLogger.Warning("Scene survey already running."); return; }
            running = true;
            try { Survey(); }
            catch (Exception e)
            {
                MelonLogger.Error($"Scene survey failed: {e.GetType().Name}: {e.Message}\n{e.StackTrace}");
            }
            finally { running = false; }
        }

        private static void Survey()
        {
            StringBuilder sb = new StringBuilder(1 << 16);
            DateTime now = DateTime.Now;

            sb.AppendLine("RumbleReShaded scene survey");
            sb.AppendLine("Taken: " + now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("Unity: " + Application.unityVersion);
            sb.AppendLine("Scene: " + UnityEngine.SceneManagement.SceneManager.GetActiveScene().name);
            sb.AppendLine(new string('=', 78));
            sb.AppendLine();

            ProbeRenderingLayerMask(sb);

            Renderer[] renderers;
            try
            {
                // FindObjectsOfType, not Resources.FindObjectsOfTypeAll: we want what is actually
                // in the live scene, not every loaded asset and prefab.
                renderers = UnityEngine.Object.FindObjectsOfType<Renderer>();
            }
            catch (Exception e)
            {
                sb.AppendLine($"FATAL: FindObjectsOfType<Renderer>() threw — {e.GetType().Name}: {e.Message}");
                Write(sb, now);
                return;
            }

            sb.AppendLine($"## Renderers found: {renderers.Length}");
            sb.AppendLine();

            // Aggregates. The per-renderer dump below is the evidence; these are the answer.
            Dictionary<string, int> byLayer = new Dictionary<string, int>();
            Dictionary<string, int> byRoot = new Dictionary<string, int>();
            Dictionary<string, int> byShader = new Dictionary<string, int>();
            Dictionary<string, int> byRendererType = new Dictionary<string, int>();
            int structureTagged = 0, structureUntagged = 0, structureLive = 0;
            int taggedStructures = 0, taggedPlayers = 0, taggedMap = 0, taggedNone = 0;

            List<string> rows = new List<string>(renderers.Length);

            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer r = renderers[i];
                if (r == null) continue;

                string name = Safe(() => r.name, "?");
                GameObject go = Safe(() => r.gameObject, null);
                if (go == null) continue;

                int layer = Safe(() => go.layer, -1);
                string layerName = layer >= 0 ? Safe(() => LayerMask.LayerToName(layer), "?") : "?";
                string layerKey = $"{layer} ({layerName})";
                string path = HierarchyPath(go);
                string root = path.Split('/')[0];
                string rtype = Safe(() => r.GetIl2CppType().Name, "Renderer");
                string shader = Safe(() => r.sharedMaterial != null && r.sharedMaterial.shader != null
                    ? r.sharedMaterial.shader.name : "(none)", "(unreadable)");
                uint rlm = Safe(() => r.renderingLayerMask, uint.MaxValue);

                // Added after the first survey (2026-08-05). RUMBLE pools its structures and
                // never reparents them — a spawned rock stays under PoolManager and is simply
                // activated in place. Without these three columns there is no way to tell a live
                // structure from a parked pool instance, and that distinction decides whether
                // classification can be done ONCE at load (pool instances persist and are reused)
                // or has to re-run on every spawn.
                bool active = Safe(() => go.activeInHierarchy, false);
                bool enabled = Safe(() => r.enabled, false);
                Vector3 pos = Safe(() => r.bounds.center, new Vector3(-9999f, -9999f, -9999f));
                string state = (active ? "A" : "-") + (enabled ? "E" : "-");

                // THE decisive classification question: is this renderer part of a Structure?
                // If this is reliably true for structures and false for everything else, Stage 3
                // needs no name heuristics at all — and the classifier can run on spawn.
                // GetComponentInParent skips inactive objects by default in some Unity versions;
                // pass includeInactive so a parked pool instance is still recognised as a
                // structure. Falls back to the no-arg overload if that signature is stripped.
                bool isStructure = Safe(() => go.GetComponentInParent<Structure>(true) != null,
                                   Safe(() => go.GetComponentInParent<Structure>() != null, false));
                if (isStructure)
                {
                    structureTagged++;
                    if (active && enabled) structureLive++;
                }
                else structureUntagged++;

                if (rlm != uint.MaxValue)
                {
                    if ((rlm & CategoryTagger.Structures) != 0) taggedStructures++;
                    else if ((rlm & CategoryTagger.Players) != 0) taggedPlayers++;
                    else if ((rlm & CategoryTagger.Map) != 0) taggedMap++;
                    else taggedNone++;
                }

                Bump(byLayer, layerKey);
                Bump(byRoot, root);
                Bump(byShader, shader);
                Bump(byRendererType, rtype);

                rows.Add($"{(isStructure ? "STRUCT" : "      ")} | {state} | ({pos.x,8:F2},{pos.y,7:F2},{pos.z,8:F2}) | L{layer,-3} {layerName,-24} | rlm {(rlm == uint.MaxValue ? "n/a" : rlm.ToString()),-10} | {rtype,-18} | {shader,-46} | {path}");
            }

            // ---- summary first: this is what gets read ----------------------------------

            sb.AppendLine("## Renderers by Unity layer");
            sb.AppendLine("   (physics layers — NEVER retag these; Stage 3 uses renderingLayerMask)");
            AppendSorted(sb, byLayer);

            sb.AppendLine("## Renderers by hierarchy root");
            sb.AppendLine("   (a clean split here means classification can key off the root object)");
            AppendSorted(sb, byRoot);

            sb.AppendLine("## Renderers by renderer type");
            AppendSorted(sb, byRendererType);

            sb.AppendLine("## Renderers by shader");
            sb.AppendLine("   (a player-only or structure-only shader would be another free classifier)");
            AppendSorted(sb, byShader);

            sb.AppendLine("## Structure-component test — THE key result");
            sb.AppendLine($"   renderers with an Il2CppRUMBLE.MoveSystem.Structure in their parent chain: {structureTagged}");
            sb.AppendLine($"     ...of those, ACTIVE and ENABLED (i.e. actually drawing):                {structureLive}");
            sb.AppendLine($"   renderers without one:                                                     {structureUntagged}");
            sb.AppendLine("   RUMBLE pools structures and never reparents them, so the first number counts");
            sb.AppendLine("   parked pool instances too. The SECOND number is the one to compare against");
            sb.AppendLine("   how many structures are really on the field.");
            sb.AppendLine("   If pool instances persist and are reused, classification can be done ONCE at");
            sb.AppendLine("   load instead of on every spawn.");
            sb.AppendLine();

            // Tag verification. The survey is the only way to check the tagger did what it
            // claimed, so it reports the same categories back — read out of renderingLayerMask
            // rather than recomputed, so a disagreement between what we MEANT to tag and what
            // is actually ON the renderer shows up instead of being papered over.
            sb.AppendLine("## Category tags currently on renderers (RumbleReShaded bits 3+)");
            sb.AppendLine($"   structures (bit 3, {CategoryTagger.Structures}): {taggedStructures}");
            sb.AppendLine($"   players    (bit 4, {CategoryTagger.Players}): {taggedPlayers}");
            sb.AppendLine($"   map        (bit 5, {CategoryTagger.Map}): {taggedMap}");
            sb.AppendLine($"   untagged:  {taggedNone}");
            sb.AppendLine("   All zero = the tagger has not been run this session ('Tag renderer categories').");
            sb.AppendLine("   Compare 'structures' against the Structure-component count above: they should");
            sb.AppendLine("   differ by exactly the number of StructureTargets, which are deliberately excluded.");
            sb.AppendLine();

            sb.AppendLine("## Legend");
            sb.AppendLine("   state column: A = GameObject.activeInHierarchy, E = Renderer.enabled");
            sb.AppendLine("                 'AE' = actually drawing, '--' = parked in the pool");
            sb.AppendLine("   position: Renderer.bounds.center in world space");
            sb.AppendLine();

            sb.AppendLine(new string('=', 78));
            sb.AppendLine("## Full renderer list");
            sb.AppendLine("STRUCT | AE | bounds centre | layer | renderingLayerMask | type | shader | hierarchy path");
            sb.AppendLine(new string('-', 78));
            rows.Sort(StringComparer.OrdinalIgnoreCase);
            foreach (string row in rows) sb.AppendLine(row);

            Write(sb, now);
        }

        // Stage 3's whole filtering approach rests on this member existing at runtime. Il2Cpp
        // strips managed members nothing in the game calls, and the failure mode is an exception
        // deep inside a render pass — far more expensive to diagnose there than here.
        //
        // Set is probed too, not just get: they are separate methods and can be stripped
        // independently. The value is restored immediately, and it is render-only either way.
        private static void ProbeRenderingLayerMask(StringBuilder sb)
        {
            sb.AppendLine("## Renderer.renderingLayerMask availability  [GATES STAGE 3]");

            Renderer probe = null;
            try { probe = UnityEngine.Object.FindObjectOfType<Renderer>(); }
            catch (Exception e) { sb.AppendLine($"   could not find a renderer to probe: {e.Message}"); }

            if (probe == null)
            {
                sb.AppendLine("   INCONCLUSIVE — no renderer in the scene to probe. Re-run in a match.");
                sb.AppendLine();
                return;
            }

            uint original;
            try
            {
                original = probe.renderingLayerMask;
                sb.AppendLine($"   get: OK (probe '{Safe(() => probe.name, "?")}' reads {original})");
            }
            catch (Exception e)
            {
                sb.AppendLine($"   get: FAILED — {e.GetType().Name}: {e.Message}");
                sb.AppendLine("   >> Stage 3 CANNOT use renderingLayerMask filtering. Rethink before writing the mask pass.");
                sb.AppendLine();
                return;
            }

            try
            {
                probe.renderingLayerMask = original;   // no-op write: proves the setter exists
                sb.AppendLine("   set: OK (wrote the same value back — no scene change)");
                sb.AppendLine("   >> Stage 3's classification approach is viable.");
            }
            catch (Exception e)
            {
                sb.AppendLine($"   set: FAILED — {e.GetType().Name}: {e.Message}");
                sb.AppendLine("   >> Reading works but tagging does not. Stage 3 needs a different classifier.");
            }

            sb.AppendLine();
        }

        private static void Write(StringBuilder sb, DateTime now)
        {
            string dir = Path.Combine(MelonEnvironment.UserDataDirectory, "RumbleReShaded");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, $"scene-survey-{now:yyyyMMdd-HHmmss}.txt");
            File.WriteAllText(file, sb.ToString());
            MelonLogger.Msg($"Scene survey written to {file}");
        }

        private static string HierarchyPath(GameObject go)
        {
            try
            {
                StringBuilder p = new StringBuilder(go.name);
                Transform t = go.transform.parent;
                int guard = 0;
                while (t != null && guard++ < 32)
                {
                    p.Insert(0, t.name + "/");
                    t = t.parent;
                }
                return p.ToString();
            }
            catch { return go.name; }
        }

        // Every Unity member read here is a potential "Method unstripping failed" (see the
        // permanent gotcha in CLAUDE.md). A survey that dies halfway through is worth much less
        // than one that reports "unreadable" for a column and finishes.
        private static T Safe<T>(Func<T> get, T fallback)
        {
            try { return get(); }
            catch { return fallback; }
        }

        private static void Bump(Dictionary<string, int> d, string key)
        {
            d.TryGetValue(key, out int n);
            d[key] = n + 1;
        }

        private static void AppendSorted(StringBuilder sb, Dictionary<string, int> d)
        {
            List<KeyValuePair<string, int>> list = new List<KeyValuePair<string, int>>(d);
            list.Sort((a, b) => b.Value.CompareTo(a.Value));
            foreach (KeyValuePair<string, int> kv in list)
                sb.AppendLine($"   {kv.Value,5}  {kv.Key}");
            sb.AppendLine();
        }
    }
}
