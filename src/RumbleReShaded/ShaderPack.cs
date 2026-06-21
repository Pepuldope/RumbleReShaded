using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MelonLoader;
using RumbleModUI;
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
        public ModSetting<float> Setting;
    }

    // One shader pack = a folder under UserData containing a
    // manifest.json and an AssetBundle with the pack's material.
    public class ShaderPack
    {
        public string Name;
        public string Author = "";
        public string Version = "";
        public string BundleFile;
        public string MaterialName;
        public int Priority;
        public int Queue;
        public string Folder;
        public List<PackParameter> Parameters = new List<PackParameter>();

        public ModSetting<bool> EnabledSetting;
        public AssetBundle Bundle;
        public Material Material;
        public bool LoadFailed;

        // 2600 draws after the opaque-texture grab but before the game's
        // transparent effects, so particles and trails stay visible on top.
        public int RenderQueue => Queue != 0 ? Queue : 2600 + Priority;

        public bool IsEnabled => EnabledSetting == null || (bool)EnabledSetting.Value;

        public static ShaderPack FromFolder(string folder)
        {
            string manifestPath = Path.Combine(folder, "manifest.json");
            if (!File.Exists(manifestPath)) return null;

            try
            {
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
                JsonElement root = doc.RootElement;

                ShaderPack pack = new ShaderPack
                {
                    Folder = folder,
                    Name = GetString(root, "name", Path.GetFileName(folder)),
                    Author = GetString(root, "author", ""),
                    Version = GetString(root, "version", ""),
                    BundleFile = GetString(root, "bundle", null),
                    MaterialName = GetString(root, "material", null),
                    Priority = GetInt(root, "priority", 0),
                    Queue = GetInt(root, "queue", 0),
                };

                if (pack.BundleFile == null || pack.MaterialName == null)
                {
                    MelonLogger.Warning($"Shader pack '{pack.Name}': manifest.json must define \"bundle\" and \"material\".");
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

                return pack;
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"Failed to read shader pack manifest in '{folder}': {e.Message}");
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

            string bundlePath = Path.Combine(Folder, BundleFile);
            if (!File.Exists(bundlePath))
            {
                MelonLogger.Warning($"Shader pack '{Name}': bundle file not found: {bundlePath}");
                LoadFailed = true;
                return false;
            }

            try
            {
                try { Bundle = AssetBundle.LoadFromFile(bundlePath); }
                catch (Exception e)
                {
                    MelonLogger.Warning($"Shader pack '{Name}': LoadFromFile threw: {e.Message}");
                    Bundle = null;
                }
                if (Bundle == null)
                    Bundle = LoadViaStream(bundlePath);

                if (Bundle == null)
                {
                    MelonLogger.Warning($"Shader pack '{Name}': Unity refused the bundle. This game runs Unity " +
                        $"{Application.unityVersion} — the pack must be built with that exact editor version " +
                        "(AssetBundles are not compatible across Unity's 2025 security-format change). " +
                        "Other causes: LZMA compression (rebuild with the template, which uses LZ4), or two packs sharing the same name.");
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

                Material.renderQueue = RenderQueue;
                ApplyParameters();
                MelonLogger.Msg($"Shader pack '{Name}' loaded (queue {RenderQueue}).");
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

        private static AssetBundle LoadViaStream(string path)
        {
            try
            {
                Il2CppStructArray<byte> bytes = File.ReadAllBytes(path);
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
                float value = p.Setting != null ? Mathf.Clamp((float)p.Setting.Value, p.Min, p.Max) : p.Default;
                Material.SetFloat(p.Property, value);
            }
        }

        public void Unload()
        {
            try { if (Bundle != null) Bundle.Unload(true); } catch { }
            Bundle = null;
            Material = null;
            LoadFailed = false;
        }
    }
}
