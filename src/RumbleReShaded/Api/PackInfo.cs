using System.Collections.Generic;

namespace RumbleReShaded.Api
{
    /// <summary>
    /// A read-only snapshot of one installed pack, for mods that want to offer or drive looks.
    /// Taken when <see cref="RRS.Packs"/> is read; read it again after "Reload packs".
    /// </summary>
    public sealed class PackInfo
    {
        internal PackInfo(ShaderPack pack)
        {
            Id = pack.Id;
            Name = pack.Name;
            Author = pack.Author;
            Version = pack.Version;
            IsEmbedded = pack.IsEmbedded;
            PlayerEnabled = pack.EnabledSetting == null || pack.EnabledSetting.Value;
            Enabled = pack.IsEnabled;
            Loaded = pack.Material != null;

            var parameters = new List<PackParameterInfo>();
            foreach (var p in pack.Parameters)
            {
                var playerValue = p.Setting != null ? p.Setting.Value : p.Default;
                parameters.Add(new PackParameterInfo(p.Property, p.Label, p.Min, p.Max, p.Default, playerValue, PackOverrides.EffectiveValue(pack, p, playerValue)));
            }
            Parameters = parameters;
        }

        /// <summary>Unique key - what <see cref="RRS.Override"/> and <see cref="RRS.Preload"/> accept (the name works too).</summary>
        public string Id { get; }

        /// <summary>Display name, as in the menu.</summary>
        public string Name { get; }

        public string Author { get; }

        public string Version { get; }

        /// <summary>True for a pack shipped inside another mod's DLL rather than a UserData folder.</summary>
        public bool IsEmbedded { get; }

        /// <summary>The player's own on/off, ignoring any override.</summary>
        public bool PlayerEnabled { get; }

        /// <summary>On/off as the frame sees it: the player's, unless an override says otherwise.</summary>
        public bool Enabled { get; }

        /// <summary>True once its bundle is loaded; see <see cref="RRS.Preload"/>.</summary>
        public bool Loaded { get; }

        public IReadOnlyList<PackParameterInfo> Parameters { get; }
    }

    /// <summary>One slider of a pack: its shader property, label, range, default and the player's value.</summary>
    public sealed class PackParameterInfo
    {
        internal PackParameterInfo(string property, string label, float min, float max, float defaultValue, float playerValue, float value)
        {
            Property = property;
            Label = label;
            Min = min;
            Max = max;
            Default = defaultValue;
            PlayerValue = playerValue;
            Value = value;
        }

        public string Property { get; }

        public string Label { get; }

        public float Min { get; }

        public float Max { get; }

        public float Default { get; }

        /// <summary>The player's own setting, ignoring any override.</summary>
        public float PlayerValue { get; }

        /// <summary>The value the frame uses: the player's with any overrides blended over it.</summary>
        public float Value { get; }
    }
}
