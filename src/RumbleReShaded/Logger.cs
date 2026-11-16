using MelonLoader;

namespace RumbleReShaded
{
    /// <summary>Severity, lowest first. <see cref="Logger.Level"/> hides anything below it.</summary>
    public enum LogLevel
    {
        Debug,
        Info,
        Warning,
        Error,
    }

    // One place every log line goes through, so the diagnostic lines this mod uses to explain
    // itself can be kept OUT of a player's log without deleting them from the code. They are
    // what a bug report is read from, so they have to survive; they just shouldn't be on by
    // default. "Debug logging" in the settings is what turns them back on.
    //
    // ⚠ Melon is THIS MOD'S OWN MelonLogger.Instance (handed over from MelonMod.LoggerInstance),
    // never the static MelonLogger. Both come out prefixed [RumbleReShaded] in practice —
    // MelonLoader infers the melon from the calling assembly, as Latest.log from a dev.2 build
    // shows — but the instance states it outright instead of depending on that inference.
    internal static class Logger
    {
        public static LogLevel Level { get; set; } = LogLevel.Info;

        public static MelonLogger.Instance Melon { get; set; }

        public static void Debug(string message) => Write(LogLevel.Debug, message);

        public static void Info(string message) => Write(LogLevel.Info, message);

        public static void Warning(string message) => Write(LogLevel.Warning, message);

        public static void Error(string message) => Write(LogLevel.Error, message);

        public static void Write(LogLevel level, string message)
        {
            if (level < Level) return;

            // Melon is null until OnInitializeMelon hands it over — and another mod calling our
            // API can get here before that. Fall back to the static logger rather than dropping
            // the line: the earliest lines are the ones that explain a failure to start. They
            // lose the [RumbleReShaded] prefix, which is why this is a fallback and not the plan.
            if (Melon == null)
            {
                switch (level)
                {
                    case LogLevel.Error: MelonLogger.Error(message); break;
                    case LogLevel.Warning: MelonLogger.Warning(message); break;
                    default: MelonLogger.Msg(message); break;
                }
                return;
            }

            switch (level)
            {
                case LogLevel.Error: Melon.Error(message); break;
                case LogLevel.Warning: Melon.Warning(message); break;
                default: Melon.Msg(message); break;
            }
        }
    }
}
