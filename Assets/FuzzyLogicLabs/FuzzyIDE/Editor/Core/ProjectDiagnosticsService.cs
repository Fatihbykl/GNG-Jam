using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.Core
{
    public struct FileDiagnosticItem
    {
        public string Message;
        public bool IsError;
        public int Line;
        public int Column;

        public FileDiagnosticItem(string message, bool isError, int line = 0, int column = 0)
        {
            Message = message;
            IsError = isError;
            Line = line;
            Column = column;
        }
    }

    public class FileDiagnosticSummary
    {
        public int ErrorCount;
        public int WarningCount;
        public List<FileDiagnosticItem> Items = new List<FileDiagnosticItem>();
    }

    [InitializeOnLoad]
    public static class ProjectDiagnosticsService
    {
        public static event Action OnDiagnosticsUpdated;

        // Compiler diagnostics from Unity CompilationPipeline (all files in project)
        private static readonly Dictionary<string, FileDiagnosticSummary> s_CompilerDiagnostics = 
            new Dictionary<string, FileDiagnosticSummary>(StringComparer.OrdinalIgnoreCase);

        // Live editor diagnostics from Roslyn (actively open and edited files in FuzzyIDE)
        private static readonly Dictionary<string, FileDiagnosticSummary> s_LiveDiagnostics = 
            new Dictionary<string, FileDiagnosticSummary>(StringComparer.OrdinalIgnoreCase);

        // Cumulative folder totals
        private static readonly Dictionary<string, (int errors, int warnings)> s_FolderDiagnostics = 
            new Dictionary<string, (int errors, int warnings)>(StringComparer.OrdinalIgnoreCase);

        static ProjectDiagnosticsService()
        {
            CompilationPipeline.assemblyCompilationFinished -= OnAssemblyCompilationFinished;
            CompilationPipeline.assemblyCompilationFinished += OnAssemblyCompilationFinished;
        }

        // Messages per assembly: each assembly reports separately, and one assembly's (re)build must not wipe the
        // diagnostics of the others.
        private static readonly Dictionary<string, CompilerMessage[]> s_MessagesByAssembly = new Dictionary<string, CompilerMessage[]>(StringComparer.OrdinalIgnoreCase);

        private static void OnAssemblyCompilationFinished(string assemblyPath, CompilerMessage[] messages)
        {
            EditorApplication.delayCall += () =>
            {
                ProcessCompilerMessages(messages, assemblyPath);
            };
        }

        /// <summary>Replaces the compiler diagnostics reported for <paramref name="assemblyPath"/> (all of them when null).</summary>
        public static void ProcessCompilerMessages(CompilerMessage[] messages, string assemblyPath = null)
        {
            if (assemblyPath == null) s_MessagesByAssembly.Clear();
            s_MessagesByAssembly[assemblyPath ?? ""] = messages ?? new CompilerMessage[0];

            s_CompilerDiagnostics.Clear();
            foreach (var assemblyMessages in s_MessagesByAssembly.Values)
            {
                foreach (var msg in assemblyMessages)
                {
                    if (string.IsNullOrEmpty(msg.file)) continue;

                    string normalizedPath = NormalizeAssetPath(msg.file);
                    if (string.IsNullOrEmpty(normalizedPath)) continue;

                    if (!s_CompilerDiagnostics.TryGetValue(normalizedPath, out var summary))
                    {
                        summary = new FileDiagnosticSummary();
                        s_CompilerDiagnostics[normalizedPath] = summary;
                    }

                    bool isError = msg.type == CompilerMessageType.Error;
                    if (isError) summary.ErrorCount++;
                    else summary.WarningCount++;

                    summary.Items.Add(new FileDiagnosticItem(msg.message, isError, msg.line, msg.column));
                }
            }

            RecalculateFolderDiagnostics();
            OnDiagnosticsUpdated?.Invoke();
        }

        public static void SetRoslynDiagnostics(string assetPath, IEnumerable<FileDiagnosticItem> items)
        {
            if (string.IsNullOrEmpty(assetPath)) return;
            string normalized = NormalizeAssetPath(assetPath);

            var summary = new FileDiagnosticSummary();
            if (items != null)
            {
                foreach (var it in items)
                {
                    if (it.IsError) summary.ErrorCount++;
                    else summary.WarningCount++;
                    summary.Items.Add(it);
                }
            }

            // Live Roslyn diagnostics override compiler messages for actively edited files
            s_LiveDiagnostics[normalized] = summary;

            RecalculateFolderDiagnostics();
            OnDiagnosticsUpdated?.Invoke();
        }

        public static void ClearRoslynDiagnostics(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return;
            string normalized = NormalizeAssetPath(assetPath);
            if (s_LiveDiagnostics.Remove(normalized))
            {
                RecalculateFolderDiagnostics();
                OnDiagnosticsUpdated?.Invoke();
            }
        }

        private static void RecalculateFolderDiagnostics()
        {
            s_FolderDiagnostics.Clear();

            // Collect all unique file paths known to either live or compiler diagnostics
            var allFilePaths = new HashSet<string>(s_CompilerDiagnostics.Keys, StringComparer.OrdinalIgnoreCase);
            foreach (var key in s_LiveDiagnostics.Keys)
            {
                allFilePaths.Add(key);
            }

            foreach (var filePath in allFilePaths)
            {
                var summary = GetFileDiagnostics(filePath);
                if (summary == null || (summary.ErrorCount == 0 && summary.WarningCount == 0)) continue;

                string current = GetParentFolderPath(filePath);
                while (!string.IsNullOrEmpty(current))
                {
                    if (s_FolderDiagnostics.TryGetValue(current, out var currentTotals))
                    {
                        s_FolderDiagnostics[current] = (currentTotals.errors + summary.ErrorCount, currentTotals.warnings + summary.WarningCount);
                    }
                    else
                    {
                        s_FolderDiagnostics[current] = (summary.ErrorCount, summary.WarningCount);
                    }

                    current = GetParentFolderPath(current);
                }
            }
        }

        public static FileDiagnosticSummary GetFileDiagnostics(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return null;
            string normalized = NormalizeAssetPath(assetPath);

            // Active in-editor Roslyn diagnostics take precedence over disk compiler messages
            if (s_LiveDiagnostics.TryGetValue(normalized, out var liveSummary))
            {
                return liveSummary;
            }

            if (s_CompilerDiagnostics.TryGetValue(normalized, out var compSummary))
            {
                return compSummary;
            }

            return null;
        }

        /// <summary>Every file with diagnostics; live Roslyn results replace compiler messages for the files they cover.</summary>
        public static Dictionary<string, FileDiagnosticSummary> GetAllDiagnostics()
        {
            var result = new Dictionary<string, FileDiagnosticSummary>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in s_CompilerDiagnostics) result[kvp.Key] = kvp.Value;
            foreach (var kvp in s_LiveDiagnostics) result[kvp.Key] = kvp.Value;
            return result;
        }

        public static (int totalErrors, int totalWarnings) GetTotalDiagnostics()
        {
            var allFilePaths = new HashSet<string>(s_CompilerDiagnostics.Keys, StringComparer.OrdinalIgnoreCase);
            foreach (var key in s_LiveDiagnostics.Keys)
            {
                allFilePaths.Add(key);
            }

            int errs = 0;
            int warns = 0;
            foreach (var path in allFilePaths)
            {
                var summary = GetFileDiagnostics(path);
                if (summary != null)
                {
                    errs += summary.ErrorCount;
                    warns += summary.WarningCount;
                }
            }
            return (errs, warns);
        }

        public static (int errorCount, int warningCount) GetFolderDiagnostics(string folderPath)
        {
            if (string.IsNullOrEmpty(folderPath)) return (0, 0);
            string normalized = NormalizeAssetPath(folderPath).TrimEnd('/');
            if (s_FolderDiagnostics.TryGetValue(normalized, out var counts))
            {
                return counts;
            }
            return (0, 0);
        }

        public static string NormalizeAssetPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            string normalized = path.Replace('\\', '/');

            // Handle absolute paths pointing into this project
            string projectRoot = Path.GetFullPath(Directory.GetCurrentDirectory()).Replace('\\', '/').TrimEnd('/') + "/";
            if (normalized.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized.Substring(projectRoot.Length);
            }

            return normalized.TrimStart('/');
        }

        public static string GetParentFolderPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            string normalized = path.Replace('\\', '/').TrimEnd('/');
            int lastSlash = normalized.LastIndexOf('/');
            if (lastSlash <= 0) return "";
            return normalized.Substring(0, lastSlash);
        }
    }
}
