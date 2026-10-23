using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using FuzzyLogicLabs.FuzzyIDE.Editor.UI;
using FuzzyLogicLabs.FuzzyIDE.Editor.Notes.Integration;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.Notes.Core
{
    public static class NoteLinkResolver
    {
        /// <summary>A note link with a heading/block subpath was opened: (note, "Heading" or "^blockid").</summary>
        public static event Action<NoteItem, string> OnRevealSubpath;

        /// <summary>
        /// True when the link points at something that exists (or that we cannot cheaply check, like URLs and assets).
        /// Used to render unresolved note links differently.
        /// </summary>
        public static bool IsResolvable(string linkText, NoteItem context = null)
        {
            if (string.IsNullOrWhiteSpace(linkText)) return false;
            linkText = linkText.Trim();
            if (linkText.StartsWith("#") || linkText.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                linkText.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || NoteRefLinks.Parse(linkText) != null)
            {
                return true;
            }
            if (Uri.TryCreate(linkText, UriKind.Absolute, out var uri) && !uri.IsFile) return true;
            return NoteStore.Instance.FindNoteByLink(linkText, context) != null;
        }

        /// <param name="context">The note containing the link: same-vault notes win and "#Heading" links stay in it.</param>
        public static bool ResolveAndOpen(string linkText, NoteItem context = null)
        {
            if (string.IsNullOrWhiteSpace(linkText)) return false;

            linkText = linkText.Trim();

            // 0. Same-note heading/block link: [[#Heading]] / [[#^block]]
            if (linkText.StartsWith("#"))
            {
                var current = context ?? NoteStore.Instance.ActiveNote;
                if (current == null) return false;
                MarkdownScan.SplitLinkTarget(linkText, out _, out string ownSubpath, out _);
                OnRevealSubpath?.Invoke(current, ownSubpath);
                return true;
            }

            // 1. External URL (http / https)
            if (linkText.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                linkText.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                Application.OpenURL(linkText);
                return true;
            }

            // 2. Code, asset, scene object and commit links
            var reference = NoteRefLinks.Parse(linkText);
            if (reference != null)
            {
                // A note that happens to be named like a file ("Readme.txt") still wins over a code lookup.
                if (reference.Kind == NoteRefKind.Code && !NoteRefLinks.IsProjectPath(reference.Target))
                {
                    var note = NoteStore.Instance.FindNoteByLink(linkText, context);
                    if (note != null) { NoteStore.Instance.SetActiveNote(note); return true; }
                }
                return OpenReference(reference);
            }

            // 3. Inter-note Link: [[Note]], [[Folder/Note]], [[Note#Heading]], [[Note|Alias]]
            var targetNote = NoteStore.Instance.FindNoteByLink(linkText, context);
            if (targetNote != null)
            {
                NoteStore.Instance.SetActiveNote(targetNote);
                MarkdownScan.SplitLinkTarget(linkText, out _, out string subpath, out _);
                if (subpath.Length > 0) OnRevealSubpath?.Invoke(targetNote, subpath);
                return true;
            }

            return false;
        }

        public static bool OpenReference(NoteRefLink reference)
        {
            switch (reference.Kind)
            {
                case NoteRefKind.Code: return OpenCodeFile(reference.Target, reference.Line, reference.Symbol);
                case NoteRefKind.Asset: return OpenAsset(reference.Target);
                case NoteRefKind.GameObject: return UnityObjectLinks.OpenGameObject(reference.Target);
                case NoteRefKind.Commit: return GitLinks.OpenCommit(reference.Target);
                default: return false;
            }
        }

        public static bool OpenAsset(string target)
        {
            string assetPath = NoteRefLinks.IsGuid(target) ? AssetDatabase.GUIDToAssetPath(target) : target;
            if (string.IsNullOrEmpty(assetPath)) return false;

            // Code files open in the IDE rather than just being selected.
            if (NoteRefLinks.Parse(assetPath) is NoteRefLink code && code.Kind == NoteRefKind.Code) return OpenCodeFile(assetPath);

            var obj = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath);
            if (obj != null)
            {
                EditorGUIUtility.PingObject(obj);
                Selection.activeObject = obj;
                return true;
            }

            return false;
        }

        /// <summary>Parses "File.cs:42" / "File.cs#Method" / "Assets/Path/File.cs" style links.</summary>
        public static bool TryParseCodeLink(string linkText, out string file, out int line, out string symbol)
        {
            file = null;
            line = 1;
            symbol = null;
            var reference = NoteRefLinks.Parse(linkText);
            if (reference == null || reference.Kind != NoteRefKind.Code) return false;

            file = reference.Target;
            line = Math.Max(1, reference.Line);
            symbol = reference.Symbol;
            return true;
        }

        /// <param name="line">1-based line; ignored when <paramref name="symbol"/> is found.</param>
        public static bool OpenCodeFile(string fileNameOrPath, int line = 1, string symbol = null)
        {
            string assetPath = ResolveCodeAssetPath(fileNameOrPath);

            if (!string.IsNullOrEmpty(symbol))
            {
                int symbolLine = FindSymbolLine(assetPath, symbol);
                if (symbolLine >= 0) line = symbolLine + 1;
                else Debug.LogWarning($"[FuzzyNotes] '{symbol}' was not found in {assetPath}; it may have been renamed. Opening the file instead.");
            }

            // Try opening in FuzzyIDE VirtualIDEWindow first
            try
            {
                var ideWindow = EditorWindow.GetWindow<VirtualIDEWindow>();
                if (ideWindow != null)
                {
                    ideWindow.Show();
                    ideWindow.LoadFileAndJumpToLine(assetPath, Math.Max(1, line));
                    return true;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[FuzzyNotes] Could not open in VirtualIDEWindow: {ex.Message}. Falling back to default editor.");
            }

            // Fallback to Unity default script open
            var assetObj = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath);
            if (assetObj != null)
            {
                AssetDatabase.OpenAsset(assetObj, Math.Max(1, line));
                return true;
            }

            return false;
        }

        private static int FindSymbolLine(string assetPath, string symbol)
        {
            if (!assetPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) return -1;
            try
            {
                string full = Path.GetFullPath(assetPath);
                return File.Exists(full) ? CodeSymbolLocator.FindSymbolLine(File.ReadAllText(full), symbol) : -1;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[FuzzyNotes] Could not read {assetPath}: {ex.Message}");
                return -1;
            }
        }

        /// <summary>Project-relative asset path for a bare file name or path ("PlayerMotor.cs" → "Assets/Scripts/PlayerMotor.cs").</summary>
        public static string ResolveCodeAssetPath(string fileNameOrPath)
        {
            string assetPath = fileNameOrPath.Replace('\\', '/');
            if (NoteRefLinks.IsProjectPath(assetPath)) return assetPath;

            // "Scripts/PlayerMotor.cs" or "PlayerMotor.cs": search the project, preferring a path that ends with the link.
            string fileName = Path.GetFileName(assetPath);
            string bestMatch = null;
            foreach (var guid in AssetDatabase.FindAssets(Path.GetFileNameWithoutExtension(fileName)))
            {
                string p = AssetDatabase.GUIDToAssetPath(guid);
                if (!Path.GetFileName(p).Equals(fileName, StringComparison.OrdinalIgnoreCase)) continue;
                if (NoteRefLinks.CodeTargetMatches(assetPath, p)) return p;
                if (bestMatch == null) bestMatch = p;
            }

            return bestMatch ?? assetPath;
        }
    }
}
