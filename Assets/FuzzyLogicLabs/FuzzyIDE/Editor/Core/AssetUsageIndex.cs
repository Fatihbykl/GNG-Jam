using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using UnityEditor;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.Core
{
    /// <summary>What one text-serialized asset (scene, prefab, ScriptableObject) references.</summary>
    public sealed class AssetFileReferences
    {
        /// <summary>GUIDs of MonoScripts attached to objects in the file (m_Script).</summary>
        public readonly HashSet<string> ScriptGuids = new HashSet<string>();
        /// <summary>UnityEvent persistent calls: "Namespace.Type" to method names.</summary>
        public readonly Dictionary<string, HashSet<string>> EventCalls = new Dictionary<string, HashSet<string>>();
    }

    /// <summary>
    /// References Roslyn can't see: which scenes/prefabs/assets use a script, and which methods are wired to
    /// UnityEvents in the Inspector. Built by scanning text-serialized assets on a background thread, re-scanning only
    /// files whose timestamp changed after the project changes.
    /// </summary>
    [InitializeOnLoad]
    public static class AssetUsageIndex
    {
        private static readonly Regex s_ScriptRef = new Regex(@"m_Script: \{fileID: \d+, guid: (?<guid>[0-9a-f]{32})", RegexOptions.Compiled);
        private static readonly Regex s_TargetType = new Regex(@"^\s*m_TargetAssemblyTypeName: (?<type>[^,\r\n]+)", RegexOptions.Compiled);
        private static readonly Regex s_MethodName = new Regex(@"^\s*m_MethodName: (?<method>[A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Compiled);

        private static readonly object s_Gate = new object();
        private static Dictionary<string, (DateTime time, AssetFileReferences refs)> s_Files = new Dictionary<string, (DateTime, AssetFileReferences)>();
        private static Dictionary<string, List<string>> s_ScriptUsers = new Dictionary<string, List<string>>();
        private static Dictionary<string, Dictionary<string, List<string>>> s_EventTargets = new Dictionary<string, Dictionary<string, List<string>>>();
        private static bool s_Scanning;
        private static bool s_Dirty = true;
        // Finished scan handed from the worker thread to the main thread (EditorApplication.update).
        private static volatile Task<Dictionary<string, (DateTime, AssetFileReferences)>> s_PendingScan;

        /// <summary>Incremented whenever a scan finishes with new results.</summary>
        public static int Version { get; private set; }

        static AssetUsageIndex()
        {
            EditorApplication.projectChanged += () => s_Dirty = true;
            EditorApplication.update += CompletePendingScan;
        }

        private static void CompletePendingScan()
        {
            var task = s_PendingScan;
            if (task == null || !task.IsCompleted) return;
            s_PendingScan = null;
            s_Scanning = false;
            if (task.Status == TaskStatus.RanToCompletion && task.Result != null)
            {
                Publish(task.Result);
                Version++;
            }
        }

        /// <summary>Assets that attach the script with this GUID. Starts a (re)scan when needed.</summary>
        public static IReadOnlyList<string> GetScriptUsers(string scriptGuid)
        {
            EnsureScanned();
            lock (s_Gate) return scriptGuid != null && s_ScriptUsers.TryGetValue(scriptGuid, out var users) ? users : (IReadOnlyList<string>)Array.Empty<string>();
        }

        /// <summary>Assets whose UnityEvents call <paramref name="method"/> on <paramref name="typeName"/> (reflection name).</summary>
        public static IReadOnlyList<string> GetEventCallers(string typeName, string method)
        {
            EnsureScanned();
            lock (s_Gate)
            {
                return typeName != null && s_EventTargets.TryGetValue(typeName, out var methods) && methods.TryGetValue(method, out var assets)
                    ? assets : (IReadOnlyList<string>)Array.Empty<string>();
            }
        }

        public static bool HasEventCallers(string typeName)
        {
            lock (s_Gate) return typeName != null && s_EventTargets.ContainsKey(typeName);
        }

        private static void EnsureScanned()
        {
            if (!s_Dirty || s_Scanning) return;
            s_Dirty = false;
            s_Scanning = true;

            // Asset paths come from the AssetDatabase (main thread); reading and parsing happens in the background.
            var paths = AssetDatabase.GetAllAssetPaths()
                .Where(p => p.StartsWith("Assets/", StringComparison.Ordinal) &&
                            (p.EndsWith(".unity", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase) ||
                             p.EndsWith(".asset", StringComparison.OrdinalIgnoreCase)))
                .ToArray();
            Dictionary<string, (DateTime, AssetFileReferences)> previous;
            lock (s_Gate) previous = s_Files;

            s_PendingScan = Task.Run(() => Scan(paths, previous));
        }

        private static Dictionary<string, (DateTime, AssetFileReferences)> Scan(string[] paths, Dictionary<string, (DateTime time, AssetFileReferences refs)> previous)
        {
            var files = new Dictionary<string, (DateTime, AssetFileReferences)>(paths.Length);
            foreach (var path in paths)
            {
                try
                {
                    var time = File.GetLastWriteTimeUtc(path);
                    if (previous.TryGetValue(path, out var old) && old.time == time)
                    {
                        files[path] = old;
                        continue;
                    }
                    var refs = ParseFile(path);
                    if (refs != null) files[path] = (time, refs);
                }
                catch (Exception)
                {
                    // Locked or deleted while scanning: skip, the next project change rescans it.
                }
            }
            return files;
        }

        private static AssetFileReferences ParseFile(string path)
        {
            using (var reader = new StreamReader(path))
            {
                string first = reader.ReadLine();
                if (first == null || !first.StartsWith("%YAML", StringComparison.Ordinal)) return null; // binary serialization
                return Parse(ReadLines(reader));
            }
        }

        private static IEnumerable<string> ReadLines(StreamReader reader)
        {
            string line;
            while ((line = reader.ReadLine()) != null) yield return line;
        }

        /// <summary>Script GUIDs and UnityEvent calls of one YAML asset.</summary>
        public static AssetFileReferences Parse(IEnumerable<string> lines)
        {
            var refs = new AssetFileReferences();
            string pendingType = null;
            foreach (var line in lines)
            {
                if (line.IndexOf("m_Script:", StringComparison.Ordinal) >= 0)
                {
                    var m = s_ScriptRef.Match(line);
                    if (m.Success) refs.ScriptGuids.Add(m.Groups["guid"].Value);
                }
                else if (line.IndexOf("m_TargetAssemblyTypeName:", StringComparison.Ordinal) >= 0)
                {
                    var m = s_TargetType.Match(line);
                    pendingType = m.Success ? m.Groups["type"].Value.Trim() : null;
                }
                else if (pendingType != null && line.IndexOf("m_MethodName:", StringComparison.Ordinal) >= 0)
                {
                    var m = s_MethodName.Match(line);
                    if (m.Success)
                    {
                        if (!refs.EventCalls.TryGetValue(pendingType, out var methods))
                            refs.EventCalls[pendingType] = methods = new HashSet<string>();
                        methods.Add(m.Groups["method"].Value);
                    }
                    pendingType = null;
                }
            }
            return refs;
        }

        private static void Publish(Dictionary<string, (DateTime time, AssetFileReferences refs)> files)
        {
            var scriptUsers = new Dictionary<string, List<string>>();
            var eventTargets = new Dictionary<string, Dictionary<string, List<string>>>();
            foreach (var kvp in files.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                foreach (var guid in kvp.Value.refs.ScriptGuids)
                {
                    if (!scriptUsers.TryGetValue(guid, out var users)) scriptUsers[guid] = users = new List<string>();
                    users.Add(kvp.Key);
                }
                foreach (var call in kvp.Value.refs.EventCalls)
                {
                    if (!eventTargets.TryGetValue(call.Key, out var methods)) eventTargets[call.Key] = methods = new Dictionary<string, List<string>>();
                    foreach (var method in call.Value)
                    {
                        if (!methods.TryGetValue(method, out var assets)) methods[method] = assets = new List<string>();
                        assets.Add(kvp.Key);
                    }
                }
            }
            lock (s_Gate)
            {
                s_Files = files;
                s_ScriptUsers = scriptUsers;
                s_EventTargets = eventTargets;
            }
        }

        /// <summary>"2 prefabs, 1 scene" style summary.</summary>
        public static string Summarize(IReadOnlyList<string> assets)
        {
            int scenes = assets.Count(a => a.EndsWith(".unity", StringComparison.OrdinalIgnoreCase));
            int prefabs = assets.Count(a => a.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase));
            int others = assets.Count - scenes - prefabs;
            var parts = new List<string>();
            if (prefabs > 0) parts.Add(prefabs + (prefabs == 1 ? " prefab" : " prefabs"));
            if (scenes > 0) parts.Add(scenes + (scenes == 1 ? " scene" : " scenes"));
            if (others > 0) parts.Add(others + (others == 1 ? " asset" : " assets"));
            return string.Join(", ", parts);
        }
    }
}
