using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
using FuzzyLogicLabs.FuzzyIDE.Editor.Notes.Core;
using FuzzyLogicLabs.FuzzyIDE.Editor.Notes.UI;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.Notes.Integration
{
    /// <summary>
    /// Code ⇄ notes glue for the IDE: which notes point at a file/line (gutter markers, "Notes for this file")
    /// and adding code to a note with a symbol anchor that survives edits.
    /// </summary>
    public static class CodeNotes
    {
        /// <summary>"C:/Proj/Assets/X.cs" or "Assets/X.cs" → "Assets/X.cs".</summary>
        public static string ToAssetPath(string fullOrAssetPath)
        {
            if (string.IsNullOrEmpty(fullOrAssetPath)) return "";
            string p = fullOrAssetPath.Replace('\\', '/');
            if (NoteRefLinks.IsProjectPath(p)) return p;
            string root = Path.GetFullPath(Path.Combine(Application.dataPath, "..")).Replace('\\', '/').TrimEnd('/') + "/";
            string full = Path.GetFullPath(p).Replace('\\', '/');
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full.Substring(root.Length) : full;
        }

        /// <summary>Every note link pointing at the file (file, line and symbol links).</summary>
        public static List<NoteRefHit> GetRefsForFile(string fullOrAssetPath)
        {
            var store = NoteStore.Instance;
            store.EnsureLoaded();
            return store.Index.GetCodeRefs(ToAssetPath(fullOrAssetPath));
        }

        /// <summary>
        /// 0-based code line → links anchored there. Symbol links are resolved against <paramref name="lines"/>
        /// (the live editor buffer), so they follow the code as it moves. File-level links are left out.
        /// </summary>
        public static Dictionary<int, List<NoteRefHit>> GetLineMarkers(string fullOrAssetPath, IList<string> lines)
        {
            var markers = new Dictionary<int, List<NoteRefHit>>();
            if (lines == null || lines.Count == 0) return markers;
            var refs = GetRefsForFile(fullOrAssetPath);
            if (refs.Count == 0) return markers;

            var symbols = refs.Where(r => r.Link.Symbol != null).Select(r => r.Link.Symbol).Distinct().ToList();
            var symbolLines = symbols.Count > 0 && IsCSharp(fullOrAssetPath)
                ? CodeSymbolLocator.FindSymbolLines(string.Join("\n", lines), symbols)
                : new Dictionary<string, int>();

            foreach (var hit in refs)
            {
                int line = -1;
                if (hit.Link.Symbol != null) { if (symbolLines.TryGetValue(hit.Link.Symbol, out int l)) line = l; }
                else if (hit.Link.Line > 0) line = hit.Link.Line - 1;
                if (line < 0 || line >= lines.Count) continue;

                if (!markers.TryGetValue(line, out var list)) markers[line] = list = new List<NoteRefHit>();
                list.Add(hit);
            }
            return markers;
        }

        /// <summary>Link text for a code location: "File.cs#Symbol" for C# inside a declaration, else "File.cs:line".</summary>
        public static string BuildCodeLink(string fullOrAssetPath, int line0, IList<string> lines)
        {
            string fileName = Path.GetFileName(ToAssetPath(fullOrAssetPath));
            string symbol = null;
            if (IsCSharp(fullOrAssetPath) && lines != null && lines.Count > 0)
            {
                try { symbol = CodeSymbolLocator.GetEnclosingSymbol(string.Join("\n", lines), line0); }
                catch (Exception ex) { Debug.LogWarning($"[FuzzyNotes] Symbol lookup failed: {ex.Message}"); }
            }
            return NoteRefLinks.FormatCodeLink(fileName, symbol, line0 + 1);
        }

        public static void OpenHit(NoteRefHit hit) => FuzzyNotesWindow.OpenNote(hit.Note, hit.Link.NoteLine);

        /// <summary>Opens the single note or a picker when several notes are anchored at the same place.</summary>
        public static void OpenHits(IReadOnlyList<NoteRefHit> hits, string title)
        {
            if (hits == null || hits.Count == 0) return;
            if (hits.Count == 1) { OpenHit(hits[0]); return; }
            NotePickerWindow.Open(title, "Enter open · Esc close", query => HitItems(hits, query));
        }

        /// <summary>Picker with every note linking to the file, plus "new note about this file".</summary>
        public static void ShowNotesForFile(string fullOrAssetPath, int line0 = -1, IList<string> lines = null)
        {
            string fileName = Path.GetFileName(ToAssetPath(fullOrAssetPath));
            var hits = GetRefsForFile(fullOrAssetPath);
            NotePickerWindow.Open($"Notes for {fileName}", "Enter open · Esc close", query =>
            {
                var items = HitItems(hits, query);
                var create = new PaletteItem
                {
                    Icon = "file-plus",
                    Title = line0 >= 0 ? "Add this location to a note…" : $"New note about {fileName}",
                    Hint = "new",
                    OnChoose = () => UnityEditor.EditorApplication.delayCall += () =>
                    {
                        if (line0 >= 0) AddCodeToNote(fullOrAssetPath, line0, lines, null);
                        else
                        {
                            var note = NoteStore.Instance.CreateNote($"Notes on {fileName}", $"# Notes on {fileName}\n\nCode: [[{fileName}]]\n\n");
                            FuzzyNotesWindow.OpenNote(note, int.MaxValue);
                        }
                    }
                };
                items.Add(create);
                return items;
            });
        }

        private static List<PaletteItem> HitItems(IEnumerable<NoteRefHit> hits, string query)
        {
            var items = new List<PaletteItem>();
            foreach (var hit in hits)
            {
                var h = hit;
                var matches = new List<int>();
                int score = string.IsNullOrWhiteSpace(query) ? 0 : FuzzyMatcher.Score(query, hit.Note.Title + " " + hit.Link.Context, matches);
                if (score == FuzzyMatcher.NoMatch) continue;
                matches.RemoveAll(i => i >= hit.Note.Title.Length);
                items.Add(new PaletteItem
                {
                    Icon = "file-text",
                    Title = hit.Note.Title,
                    Subtitle = hit.Link.Context,
                    Hint = hit.Link.Symbol != null ? "#" + hit.Link.Symbol : hit.Link.Line > 0 ? "line " + hit.Link.Line : "file",
                    Score = score,
                    Matches = matches,
                    OnChoose = () => OpenHit(h)
                });
            }
            return string.IsNullOrWhiteSpace(query) ? items : items.OrderByDescending(i => i.Score).ToList();
        }

        /// <summary>
        /// Asks for a target note and appends a link to the code location (plus the selected code as a fenced block).
        /// </summary>
        /// <param name="line0">0-based first line of the selection or caret.</param>
        /// <param name="lines">Current editor buffer, used to find the enclosing symbol.</param>
        /// <param name="selection">Selected code, or null/empty to add only the link.</param>
        public static void AddCodeToNote(string fullOrAssetPath, int line0, IList<string> lines, string selection)
        {
            if (string.IsNullOrEmpty(fullOrAssetPath)) return;
            string fileName = Path.GetFileName(ToAssetPath(fullOrAssetPath));
            string link = BuildCodeLink(fullOrAssetPath, line0, lines);
            string markdown = BuildSnippetMarkdown(link, fullOrAssetPath, line0, lines, selection);

            NoteTargetPicker.Pick(string.IsNullOrWhiteSpace(selection) ? $"Link {link} in note…" : $"Add code from {link} to note…",
                $"Notes on {fileName}", note => AppendAndReveal(note, markdown, "[[" + link + "]]"));
        }

        /// <summary>Appends markdown to a note, then opens the note at the line holding <paramref name="revealText"/>.</summary>
        public static void AppendAndReveal(NoteItem note, string markdown, string revealText)
        {
            if (note == null) return;
            NoteStore.Instance.AppendToNote(note, markdown);
            FuzzyNotesWindow.OpenNote(note, FindLastLine(note.Body, revealText));
        }

        internal static string BuildSnippetMarkdown(string link, string path, int line0, IList<string> lines, string selection)
        {
            if (string.IsNullOrWhiteSpace(selection))
            {
                string codeLine = lines != null && line0 >= 0 && line0 < lines.Count ? lines[line0].Trim() : "";
                return codeLine.Length > 0 && codeLine.IndexOf('`') < 0
                    ? $"\n- 💻 [[{link}]] — `{(codeLine.Length > 80 ? codeLine.Substring(0, 77) + "…" : codeLine)}`\n"
                    : $"\n- 💻 [[{link}]]\n";
            }

            var sb = new StringBuilder();
            sb.Append("\n### 💻 [[").Append(link).Append("]]\n");
            sb.Append("```").Append(FenceLanguage(path)).Append('\n');
            sb.Append(Dedent(selection.Replace("\r\n", "\n").Trim('\n'))).Append('\n');
            sb.Append("```\n");
            return sb.ToString();
        }

        private static int FindLastLine(string body, string text)
        {
            var lines = MarkdownScan.NormalizeNewlines(body).Split('\n');
            for (int i = lines.Length - 1; i >= 0; i--)
                if (lines[i].IndexOf(text, StringComparison.Ordinal) >= 0) return i;
            return lines.Length - 1;
        }

        private static string Dedent(string code)
        {
            var lines = code.Split('\n');
            int indent = int.MaxValue;
            foreach (var l in lines)
            {
                if (l.Trim().Length == 0) continue;
                int i = 0;
                while (i < l.Length && (l[i] == ' ' || l[i] == '\t')) i++;
                indent = Math.Min(indent, i);
            }
            if (indent == int.MaxValue || indent == 0) return code;
            return string.Join("\n", lines.Select(l => l.Length >= indent ? l.Substring(indent) : l.TrimStart()));
        }

        private static bool IsCSharp(string path) => path != null && path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);

        private static string FenceLanguage(string path)
        {
            switch (Path.GetExtension(path ?? "").ToLowerInvariant())
            {
                case ".cs": return "csharp";
                case ".shader": case ".hlsl": case ".cginc": case ".compute": return "hlsl";
                case ".json": case ".asmdef": return "json";
                case ".uss": return "css";
                case ".uxml": case ".xml": return "xml";
                case ".yaml": return "yaml";
                default: return "";
            }
        }
    }
}
