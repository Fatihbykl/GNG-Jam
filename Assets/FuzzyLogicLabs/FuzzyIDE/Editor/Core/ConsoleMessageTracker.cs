using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.Core
{
    public enum ConsoleSeverity { Log, Warning, Error }

    public sealed class ConsoleLineEntry
    {
        public string AssetPath;
        public int Line;        // 0-based
        public string LineText; // trimmed source line when the message arrived, used to follow edits that move it
        public string Message;  // first line of the latest message
        public string FullMessage;
        public string StackTrace;
        public ConsoleSeverity Severity;
        public int Count;
    }

    /// <summary>
    /// Collects Console messages by the project source line that produced them (first project frame of the stack
    /// trace), so the editor can show errors, warnings and logs at the end of that line. Entries reset when Play Mode
    /// starts and when the Console is cleared.
    /// </summary>
    [InitializeOnLoad]
    public static class ConsoleMessageTracker
    {
        private const int MaxEntriesPerFile = 500;

        private static readonly Dictionary<string, Dictionary<int, ConsoleLineEntry>> s_ByFile =
            new Dictionary<string, Dictionary<int, ConsoleLineEntry>>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, (DateTime time, string[] lines)> s_SourceCache =
            new Dictionary<string, (DateTime, string[])>(StringComparer.OrdinalIgnoreCase);

        private static MethodInfo s_GetConsoleCount;
        private static double s_NextConsolePoll;

        /// <summary>Incremented on every change, so views can skip work when nothing happened.</summary>
        public static int Version { get; private set; }

        static ConsoleMessageTracker()
        {
            Application.logMessageReceived += OnLogMessage;
            EditorApplication.playModeStateChanged += state =>
            {
                if (state == PlayModeStateChange.ExitingEditMode) Clear();
            };
            EditorApplication.update += PollConsoleCleared;
            var logEntries = typeof(EditorApplication).Assembly.GetType("UnityEditor.LogEntries");
            s_GetConsoleCount = logEntries?.GetMethod("GetCount", BindingFlags.Public | BindingFlags.Static);
        }

        public static IEnumerable<ConsoleLineEntry> GetEntries(string assetPath)
        {
            if (assetPath != null && s_ByFile.TryGetValue(assetPath, out var entries)) return entries.Values;
            return Array.Empty<ConsoleLineEntry>();
        }

        public static void Clear()
        {
            if (s_ByFile.Count == 0) return;
            s_ByFile.Clear();
            s_SourceCache.Clear();
            Version++;
        }

        public static ConsoleSeverity ToSeverity(LogType type)
        {
            switch (type)
            {
                case LogType.Error:
                case LogType.Exception:
                case LogType.Assert:
                    return ConsoleSeverity.Error;
                case LogType.Warning:
                    return ConsoleSeverity.Warning;
                default:
                    return ConsoleSeverity.Log;
            }
        }

        private static void OnLogMessage(string condition, string stackTrace, LogType type)
        {
            try
            {
                Record(condition, stackTrace, ToSeverity(type));
            }
            catch (Exception)
            {
                // Never log from a log callback.
            }
        }

        private static void Record(string condition, string stackTrace, ConsoleSeverity severity)
        {
            if (!ConsoleSourceLocator.TryGetSourceLocation(stackTrace, out string assetPath, out int line)) return;

            if (!s_ByFile.TryGetValue(assetPath, out var entries))
            {
                entries = new Dictionary<int, ConsoleLineEntry>();
                s_ByFile[assetPath] = entries;
            }

            int index = line - 1;
            if (!entries.TryGetValue(index, out var entry))
            {
                if (entries.Count >= MaxEntriesPerFile) return;
                entry = new ConsoleLineEntry { AssetPath = assetPath, Line = index, LineText = ReadSourceLine(assetPath, index) };
                entries[index] = entry;
            }

            entry.Count++;
            // The most severe message wins the line; among equals the latest (e.g. a changing Debug.Log value) is shown.
            if (entry.Count == 1 || severity >= entry.Severity)
            {
                string message = condition ?? "";
                int newLine = message.IndexOf('\n');
                entry.Message = (newLine >= 0 ? message.Substring(0, newLine) : message).TrimEnd('\r');
                entry.FullMessage = message;
                entry.StackTrace = stackTrace;
                entry.Severity = severity;
            }
            Version++;
        }

        private static string ReadSourceLine(string assetPath, int index)
        {
            try
            {
                string fullPath = Path.GetFullPath(assetPath);
                var time = File.GetLastWriteTimeUtc(fullPath);
                if (!s_SourceCache.TryGetValue(assetPath, out var cached) || cached.time != time)
                {
                    cached = (time, File.ReadAllLines(fullPath));
                    s_SourceCache[assetPath] = cached;
                }
                return index < cached.lines.Length ? cached.lines[index].Trim() : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        // Unity has no "console cleared" event; an empty console with entries still recorded means it was cleared.
        private static void PollConsoleCleared()
        {
            if (s_ByFile.Count == 0 || s_GetConsoleCount == null) return;
            double now = EditorApplication.timeSinceStartup;
            if (now < s_NextConsolePoll) return;
            s_NextConsolePoll = now + 1.0;
            try
            {
                if ((int)s_GetConsoleCount.Invoke(null, null) == 0) Clear();
            }
            catch (Exception)
            {
                s_GetConsoleCount = null;
            }
        }
    }

    /// <summary>Maps Console messages to source lines; free of Unity state so it can be unit tested.</summary>
    public static class ConsoleSourceLocator
    {
        private static readonly Regex s_Frame = new Regex(@"\(at (?<path>[^()]+?):(?<line>\d+)\)", RegexOptions.Compiled);

        /// <summary>First stack frame that points at a project file ("(at Assets/Foo.cs:12)"); line is 1-based.</summary>
        public static bool TryGetSourceLocation(string stackTrace, out string assetPath, out int line)
        {
            assetPath = null;
            line = 0;
            if (string.IsNullOrEmpty(stackTrace)) return false;
            foreach (Match match in s_Frame.Matches(stackTrace))
            {
                string path = match.Groups["path"].Value.Replace('\\', '/');
                if (!path.StartsWith("Assets/", StringComparison.Ordinal) && !path.StartsWith("Packages/", StringComparison.Ordinal)) continue;
                if (!int.TryParse(match.Groups["line"].Value, out line) || line <= 0) continue;
                assetPath = path;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Line where <paramref name="entry"/> belongs in <paramref name="lines"/>: its recorded line if the text still
        /// matches, else the nearest line with the same text (edits moved it), else -1.
        /// </summary>
        public static int LocateInBuffer(ConsoleLineEntry entry, IList<string> lines, int searchRadius = 40)
        {
            if (lines == null || entry.Line < 0) return -1;
            if (string.IsNullOrEmpty(entry.LineText)) return entry.Line < lines.Count ? entry.Line : -1;
            if (entry.Line < lines.Count && lines[entry.Line].Trim() == entry.LineText) return entry.Line;
            for (int delta = 1; delta <= searchRadius; delta++)
            {
                int up = entry.Line - delta, down = entry.Line + delta;
                if (down < lines.Count && down >= 0 && lines[down].Trim() == entry.LineText) return down;
                if (up >= 0 && up < lines.Count && lines[up].Trim() == entry.LineText) return up;
            }
            return -1;
        }
    }
}
