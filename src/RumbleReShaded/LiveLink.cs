using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text.Json;
using MelonLoader;
using RumbleReShaded.Api;

namespace RumbleReShaded
{
    // Lets a tool on this PC preview a look in the running game: its sliders and timeline
    // shown on the real shaders. Off unless the player turns it on, since a shader mod has no business
    // listening on a port by default. See docs/LIVE_LINK.md for the messages.
    //
    // Everything goes through RRS.Override, so this inherits what that promises: the player's
    // settings are never written, and looks stack with other mods' overrides. Created last, the
    // link's sits on top of them, so a look can be tried over a running battle. It can also set
    // shader globals, as a mod does during play, to preview a beat or a clock. When the client
    // leaves, for any reason, every override goes, every global it set goes back to 0, and the
    // packs are the player's again.
    internal static class LiveLink
    {
        private const string ModId = "rrs.livelink";

        private sealed class Held
        {
            public PackOverride Override;
            public HashSet<string> Names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        private static MelonPreferences_Entry<bool> setting;
        private static LiveLinkServer server;
        private static readonly Dictionary<string, Held> held = new Dictionary<string, Held>(StringComparer.OrdinalIgnoreCase);
        // The shader globals the link has set, so they go back to 0 when it leaves.
        private static readonly HashSet<string> globals = new HashSet<string>(StringComparer.Ordinal);

        public static void BuildSettings(MelonPreferences_Category category)
        {
            setting = category.CreateEntry("LiveLink", false, "Live link",
                "Lets a tool on this PC preview looks in the game. Your settings are never changed.");
        }

        /// <summary>Each frame: follows the setting, then applies what arrived.</summary>
        public static void Update()
        {
            if (setting == null) return;
            if (setting.Value != (server != null))
            {
                if (setting.Value) Start();
                else Stop();
            }
            if (server == null) return;

            while (server.TryRead(out var line))
            {
                if (line == null) ReleaseAll();
                else Handle(line);
            }
        }

        private static void Start()
        {
            try
            {
                server = new LiveLinkServer();
                Logger.Info($"live link listening on 127.0.0.1:{server.Port}.");
            }
            catch (SocketException e)
            {
                EventBuffer.WarnOnce("livelink:port", $"live link could not listen on its port: {e.Message}");
            }
        }

        public static void Stop()
        {
            ReleaseAll();
            server?.Dispose();
            server = null;
        }

        private static void Handle(string line)
        {
            try
            {
                using var message = JsonDocument.Parse(line);
                var root = message.RootElement;
                if (root.TryGetProperty("hello", out _)) Greet();
                else if (root.TryGetProperty("look", out var look)) Apply(look);
                else if (root.TryGetProperty("release", out var pack)) Release(pack.GetString());
                else if (root.TryGetProperty("globals", out var values)) SetGlobals(values);
            }
            catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
            {
                EventBuffer.WarnOnce("livelink:message", $"live link ignored a message it could not read: {e.Message}");
            }
        }

        private static void Greet()
        {
            server.Send(JsonSerializer.Serialize(new
            {
                hello = LiveLinkServer.Hello,
                version = LiveLinkServer.ProtocolVersion,
                // What this end can do beyond looks, so a client only sends what is understood.
                features = new[] { "globals" },
                packs =RRS.Packs.Select(pack => new
                {
                    id = pack.Id,
                    name = pack.Name,
                    parameters = pack.Parameters.Select(p => new { property = p.Property, label = p.Label, min = p.Min, max = p.Max, @default = p.Default, player = p.PlayerValue }),
                }),
            }));
        }

        // A look replaces what the link last set on that pack: a slider left out goes back to the
        // player's. Overrides are kept rather than remade, as these arrive many times a second.
        private static void Apply(JsonElement look)
        {
            var pack = look.GetProperty("pack").GetString();
            if (!held.TryGetValue(pack, out var mine))
            {
                var made = RRS.Override(ModId, pack);
                if (made == null) return;
                RRS.Preload(pack);
                held[pack] = mine = new Held { Override = made };
            }

            mine.Override.Enabled = look.GetProperty("on").GetBoolean();
            var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var value in look.GetProperty("values").EnumerateObject())
            {
                if (mine.Override.Set(value.Name, value.Value.GetSingle())) wanted.Add(value.Name);
            }
            foreach (var name in mine.Names)
            {
                if (!wanted.Contains(name)) mine.Override.Clear(name);
            }
            mine.Names = wanted;
        }

        private static void Release(string pack)
        {
            if (pack == null || !held.TryGetValue(pack, out var mine)) return;
            mine.Override.Dispose();
            held.Remove(pack);
        }

        // Shader globals by name, as RRS.SetGlobal sets them ("mymod.beat" is read as _RRS_mymod_beat):
        // what a mod would set during play, so a tool can preview it. A null puts one back to 0.
        private static void SetGlobals(JsonElement values)
        {
            foreach (var value in values.EnumerateObject())
            {
                if (value.Value.ValueKind == JsonValueKind.Null)
                {
                    RRS.SetGlobal(value.Name, 0f);
                    globals.Remove(value.Name);
                }
                else
                {
                    RRS.SetGlobal(value.Name, value.Value.GetSingle());
                    globals.Add(value.Name);
                }
            }
        }

        private static void ReleaseAll()
        {
            foreach (var mine in held.Values) mine.Override.Dispose();
            held.Clear();
            foreach (var name in globals) RRS.SetGlobal(name, 0f);
            globals.Clear();
        }
    }
}
