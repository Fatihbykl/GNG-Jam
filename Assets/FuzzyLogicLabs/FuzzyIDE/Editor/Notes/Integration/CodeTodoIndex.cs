using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using FuzzyLogicLabs.FuzzyIDE.Editor.Core;
using FuzzyLogicLabs.FuzzyIDE.Editor.Notes.Core;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.Notes.Integration
{
    /// <summary>
    /// Live index of // TODO, FIXME, HACK and BUG comments in the project's C# files. The first request scans
    /// Assets/ on a background thread; afterwards only files reported by the file watcher, the asset
    /// pipeline or the IDE's own saves are re-read.
    /// </summary>
    public static class CodeTodoIndex
    {
        public static event Action OnChanged;

        private static readonly Dictionary<string, List<CodeTodo>> s_ByFile = new Dictionary<string, List<CodeTodo>>(StringComparer.OrdinalIgnoreCase);
        private static List<CodeTodo> s_All;
        private static bool s_Started;
        private static bool s_Ready;
        // Files updated while the initial scan was still running; its (older) results must not overwrite them.
        private static readonly HashSet<string> s_TouchedDuringScan = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public static bool IsReady => s_Ready;

        /// <summary>All TODO comments ordered by file and line. Starts the initial scan on first use (empty until it finishes).</summary>
        public static IReadOnlyList<CodeTodo> All
        {
            get
            {
                EnsureStarted();
                if (s_All == null)
                {
                    s_All = s_ByFile.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase).SelectMany(k => k.Value).ToList();
                }
                return s_All;
            }
        }

        public static void EnsureStarted()
        {
            if (s_Started) return;
            s_Started = true;

            FileWatcherService.OnFilesChangedBatch -= OnFilesChanged;
            FileWatcherService.OnFilesChangedBatch += OnFilesChanged;

            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string assetsRoot = Application.dataPath;
            Task.Run(() =>
            {
                var found = new Dictionary<string, List<CodeTodo>>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    foreach (var file in Directory.EnumerateFiles(assetsRoot, "*.cs", SearchOption.AllDirectories))
                    {
                        string assetPath = ToAssetPath(projectRoot, file);
                        var todos = ScanFile(file, assetPath);
                        if (todos.Count > 0) found[assetPath] = todos;
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[FuzzyNotes] Code TODO scan failed: {ex.Message}");
                }
                return found;
            }).ContinueWith(t =>
            {
                EditorApplication.delayCall += () =>
                {
                    if (t.Status == System.Threading.Tasks.TaskStatus.RanToCompletion)
                    {
                        // Files saved while the scan ran are already up to date.
                        foreach (var kv in t.Result)
                            if (!s_TouchedDuringScan.Contains(kv.Key)) s_ByFile[kv.Key] = kv.Value;
                    }
                    s_Ready = true;
                    Changed();
                };
            });
        }

        /// <summary>The IDE saved a file (its own saves are hidden from the file watcher).</summary>
        public static void NotifySaved(string assetPath, string text)
        {
            if (!s_Started || string.IsNullOrEmpty(assetPath) || !assetPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) return;
            if (Set(NormalizeAssetPath(assetPath), CodeTodoScanner.Scan(NormalizeAssetPath(assetPath), text))) Changed();
        }

        /// <summary>Re-reads files the asset pipeline imported, moved or deleted.</summary>
        internal static void NotifyAssetsChanged(IEnumerable<string> changed, IEnumerable<string> deleted)
        {
            if (!s_Started) return;
            bool any = false;
            foreach (var p in deleted)
            {
                if (p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) any |= Set(p, null);
            }
            foreach (var p in changed)
            {
                if (!p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) continue;
                any |= Set(p, File.Exists(p) ? ScanFile(Path.GetFullPath(p), p) : null);
            }
            if (any) Changed();
        }

        private static void OnFilesChanged(IReadOnlyList<FileWatcherEvent> events)
        {
            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            bool any = false;
            foreach (var e in events)
            {
                if (e.ChangeType == FileWatcherChangeType.Renamed && !string.IsNullOrEmpty(e.OldFullPath) &&
                    e.OldFullPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                {
                    any |= Set(ToAssetPath(projectRoot, e.OldFullPath), null);
                }

                if (string.IsNullOrEmpty(e.FullPath) || !e.FullPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) continue;
                string assetPath = ToAssetPath(projectRoot, e.FullPath);
                any |= e.ChangeType == FileWatcherChangeType.Deleted || !File.Exists(e.FullPath)
                    ? Set(assetPath, null)
                    : Set(assetPath, ScanFile(e.FullPath, assetPath));
            }
            if (any) Changed();
        }

        /// <returns>True when the file's TODO list changed.</returns>
        private static bool Set(string assetPath, List<CodeTodo> todos)
        {
            if (!s_Ready) s_TouchedDuringScan.Add(assetPath);
            s_ByFile.TryGetValue(assetPath, out var old);
            if (todos == null || todos.Count == 0)
            {
                if (old == null) return false;
                s_ByFile.Remove(assetPath);
                return true;
            }

            if (old != null && old.Count == todos.Count)
            {
                bool same = true;
                for (int i = 0; i < old.Count && same; i++)
                    same = old[i].Line == todos[i].Line && old[i].Kind == todos[i].Kind && old[i].Text == todos[i].Text;
                if (same) return false;
            }
            s_ByFile[assetPath] = todos;
            return true;
        }

        private static List<CodeTodo> ScanFile(string fullPath, string assetPath)
        {
            try { return CodeTodoScanner.Scan(assetPath, File.ReadAllText(fullPath)); }
            catch (IOException) { return new List<CodeTodo>(); }
            catch (UnauthorizedAccessException) { return new List<CodeTodo>(); }
        }

        private static void Changed()
        {
            s_All = null;
            OnChanged?.Invoke();
        }

        private static string NormalizeAssetPath(string path) => path.Replace('\\', '/');

        private static string ToAssetPath(string projectRoot, string fullPath)
        {
            string full = Path.GetFullPath(fullPath).Replace('\\', '/');
            string root = projectRoot.Replace('\\', '/').TrimEnd('/') + "/";
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full.Substring(root.Length) : full;
        }

        private sealed class Postprocessor : AssetPostprocessor
        {
            private static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
            {
                if (!s_Started) return;
                NotifyAssetsChanged(imported.Concat(moved), deleted.Concat(movedFrom));
            }
        }
    }
}
