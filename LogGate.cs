using System.Runtime.CompilerServices;
using System.Text;
using BepInEx.Logging;

namespace AdvancedMedic
{
    /// <summary>
    /// Release logging gate.
    ///
    /// About 25 "Patched X" lines plus the per-event chatter used to print on every launch whatever
    /// the config said. Rather than touch every call site, the log SOURCE is wrapped: Info, Message,
    /// Debug and Warning only reach the console while <see cref="Verbose"/> is on (General/DebugLogging),
    /// and errors always do, so a real failure is still reportable from a shipped build.
    ///
    /// Call sites keep the exact `Plugin.L.LogInfo(...)` shape. With the switch off, only errors print -
    /// nothing goes through <see cref="Raw"/> except errors.
    ///
    /// An interpolated argument ($"...") binds to the <see cref="GatedString"/> overloads: the
    /// compiler asks the handler first, and with logging off it answers "don't append", so the
    /// string is never built and no hole is ever formatted. A plain concatenation ("a" + b) still
    /// binds to the object overload and IS built - keep those off hot paths or gate them.
    /// </summary>
    public sealed class LogGate
    {
        private readonly ManualLogSource _s;

        internal LogGate(ManualLogSource s) { _s = s; }

        /// <summary>Driven by General/DebugLogging (live). Off in a shipped build.</summary>
        internal static bool Verbose;

        /// <summary>The underlying source - for ERRORS only. With the verbose switch off nothing but errors
        /// may print (user rule, 2026-09-30): never send Info/Warning through it.</summary>
        internal ManualLogSource Raw { get { return _s; } }

        public void LogInfo(object data)    { if (Verbose) _s.LogInfo(data); }
        public void LogWarning(object data) { if (Verbose) _s.LogWarning(data); }

        // Interpolated-string overloads: preferred by the compiler for $"..." arguments, and built
        // only when Verbose is on.
        public void LogInfo(ref GatedString msg)    { if (msg.Enabled) _s.LogInfo(msg.ToStringAndClear()); }
        public void LogWarning(ref GatedString msg) { if (msg.Enabled) _s.LogWarning(msg.ToStringAndClear()); }

        // Always printed: a shipped build still has to be able to report a real failure.
        public void LogError(object data)   { _s.LogError(data); }
    }

    /// <summary>
    /// One error line per failing place, not one per frame. Every Harmony hook, tick subsystem and
    /// OnGUI draw pass catches through this: a real failure (above all a game member removed by an
    /// update, which fails the whole JIT-compiled method at its call site) is always printed, even
    /// with DebugLogging off, but only the first time that place fails in the session.
    /// </summary>
    internal static class Guard
    {
        private static readonly System.Collections.Generic.HashSet<string> _failed
            = new System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal);

        public static void Once(string where, System.Exception e)
        {
            if (_failed.Add(where))
                Plugin.L.LogError($"[{where}] failed (logged once; the rest of the mod keeps running): {e.GetType().Name} {e.Message}");
        }
    }

    /// <summary>Interpolated-string handler that only builds its string while LogGate.Verbose is on.</summary>
    [InterpolatedStringHandler]
    public ref struct GatedString
    {
        private StringBuilder _sb;
        internal bool Enabled => _sb != null;

        public GatedString(int literalLength, int formattedCount, out bool shouldAppend)
        {
            shouldAppend = LogGate.Verbose;
            _sb = shouldAppend ? new StringBuilder(literalLength + formattedCount * 8) : null;
        }

        public void AppendLiteral(string s) { _sb?.Append(s); }
        public void AppendFormatted<T>(T value) { _sb?.Append(value); }
        public void AppendFormatted<T>(T value, string format)
        {
            if (_sb == null) return;
            if (value is System.IFormattable f) _sb.Append(f.ToString(format, null));
            else _sb.Append(value);
        }
        public void AppendFormatted(string value) { _sb?.Append(value); }

        internal string ToStringAndClear() { var s = _sb?.ToString() ?? ""; _sb = null; return s; }
    }
}
