using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

// Builds every pack under Assets/PackSource into a ready-to-install folder.
// Each pack folder needs a manifest.json naming its bundle file and material.
// Output goes to <project>/Build/<PackName>/ — copy that folder into the game's
// UserData/RumbleReShaded/ directory.
public static class ShaderPackBuilder
{
    [MenuItem("RumbleShade/Build Shader Packs")]
    public static void BuildAll()
    {
        const string packSourceRoot = "Assets/PackSource";
        string projectRoot = Path.GetDirectoryName(Application.dataPath);
        string outputRoot = Path.Combine(projectRoot, "Build");

        if (!Directory.Exists(packSourceRoot))
        {
            EditorUtility.DisplayDialog("RumbleShade", "No Assets/PackSource folder found.", "OK");
            return;
        }

        int built = 0;
        var failures = new List<string>();

        foreach (string dir in Directory.GetDirectories(packSourceRoot))
        {
            string packName = Path.GetFileName(dir);
            string manifestPath = Path.Combine(dir, "manifest.json");
            if (!File.Exists(manifestPath))
            {
                failures.Add($"{packName}: no manifest.json");
                continue;
            }

            string manifestText = File.ReadAllText(manifestPath);
            string materialName = ExtractJsonString(manifestText, "material");
            string bundleFile = ExtractJsonString(manifestText, "bundle");
            if (materialName == null || bundleFile == null)
            {
                failures.Add($"{packName}: manifest.json must define \"material\" and \"bundle\"");
                continue;
            }

            string materialPath = FindMaterialPath(materialName);
            if (materialPath == null)
            {
                failures.Add($"{packName}: material '{materialName}' not found in the project");
                continue;
            }

            string outDir = Path.Combine(outputRoot, packName);
            Directory.CreateDirectory(outDir);

            var build = new AssetBundleBuild
            {
                assetBundleName = bundleFile,
                assetNames = new[] { materialPath },
            };

            // LZ4 (ChunkBasedCompression) is required: the game refuses the default
            // LZMA bundles at runtime, and LoadFromStream never supports LZMA.
            var manifest = BuildPipeline.BuildAssetBundles(
                outDir,
                new[] { build },
                BuildAssetBundleOptions.ChunkBasedCompression,
                BuildTarget.StandaloneWindows64);

            if (manifest == null)
            {
                failures.Add($"{packName}: BuildAssetBundles failed (see Console)");
                continue;
            }

            File.Copy(manifestPath, Path.Combine(outDir, "manifest.json"), true);

            // remove the build-system side files Unity drops next to the bundle
            string folderBundle = Path.Combine(outDir, Path.GetFileName(outDir));
            foreach (string junk in new[] { folderBundle, folderBundle + ".manifest", Path.Combine(outDir, bundleFile + ".manifest") })
                if (File.Exists(junk)) File.Delete(junk);

            built++;
            Debug.Log($"RumbleShade: built pack '{packName}' -> {outDir}");
        }

        string summary = $"Built {built} pack(s) to:\n{outputRoot}";
        if (failures.Count > 0)
            summary += "\n\nProblems:\n- " + string.Join("\n- ", failures);
        EditorUtility.DisplayDialog("RumbleShade", summary, "OK");

        if (built > 0)
            EditorUtility.RevealInFinder(outputRoot);
    }

    private static string FindMaterialPath(string materialName)
    {
        foreach (string guid in AssetDatabase.FindAssets("t:Material " + materialName))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (Path.GetFileNameWithoutExtension(path) == materialName)
                return path;
        }
        return null;
    }

    private static string ExtractJsonString(string json, string key)
    {
        Match match = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"([^\"]+)\"");
        return match.Success ? match.Groups[1].Value : null;
    }
}
