using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.UIElements;
using FuzzyLogicLabs.FuzzyIDE.Editor.Core;
using FuzzyLogicLabs.FuzzyIDE.Editor.Notes.Core;
using FuzzyLogicLabs.FuzzyIDE.Editor.Notes.UI;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.Notes.LiveEditor
{
    /// <summary>Editing commands, folding, outline and search for <see cref="MarkdownLiveEditor"/>.</summary>
    public partial class MarkdownLiveEditor
    {
        public struct HeadingInfo
        {
            public int Line;
            public int Level;
            public string Text;
        }

        private static readonly Regex NumberPrefixRx = new Regex(@"^([ \t]*)(\d+)([.)])[ \t]", RegexOptions.Compiled);
        private static readonly Regex QuoteMarkerRx = new Regex(@"^[ \t]*>[ \t]?", RegexOptions.Compiled);

        private readonly HashSet<int> m_Folded = new HashSet<int>();
        private readonly List<(TextPos start, TextPos end)> m_SearchMatches = new List<(TextPos, TextPos)>();
        private int m_SearchCurrent = -1;

        // =====================================================================
        // Shortcuts (IDE keybinding system)
        // =====================================================================

        private bool HandleCommandShortcut(KeyDownEvent evt)
        {
            var kb = CodeIDESettings.Current.Keybindings;
            if (kb.CheckAction(IDEAction.NoteToggleTask, evt)) { ToggleTaskOnSelection(); return true; }
            if (kb.CheckAction(IDEAction.MoveLineUp, evt)) { MoveLines(-1); return true; }
            if (kb.CheckAction(IDEAction.MoveLineDown, evt)) { MoveLines(1); return true; }
            if (kb.CheckAction(IDEAction.DuplicateLine, evt)) { DuplicateLines(); return true; }
            if (kb.CheckAction(IDEAction.DeleteLine, evt)) { DeleteLines(); return true; }
            if (kb.CheckAction(IDEAction.NoteBold, evt)) { WrapSelection("**", "**"); return true; }
            if (kb.CheckAction(IDEAction.NoteItalic, evt)) { WrapSelection("*", "*"); return true; }
            if (kb.CheckAction(IDEAction.NoteInsertLink, evt)) { WrapSelection("[[", "]]"); return true; }
            return false;
        }

        // =====================================================================
        // Helpers
        // =====================================================================

        private void EnsureCodeStates()
        {
            if (!m_CodeDirty && m_CodeStates.Length == m_Buffer.LineCount) return;
            m_CodeStates = BlockParser.ComputeCodeStates(m_Buffer.Lines);
            m_CodeDirty = false;
            m_StructureDirty = true;
        }

        private byte CodeStateOf(int line)
        {
            EnsureCodeStates();
            return line >= 0 && line < m_CodeStates.Length ? m_CodeStates[line] : BlockParser.StateNone;
        }

        private BlockInfo InfoOf(int line) => BlockParser.Parse(m_Buffer.GetLine(line), CodeStateOf(line));

        private static string LeadingWhitespace(string line, int maxLen = int.MaxValue)
        {
            int n = 0;
            while (n < line.Length && n < maxLen && (line[n] == ' ' || line[n] == '\t')) n++;
            return line.Substring(0, n);
        }

        private static bool IsListKind(BlockKind k) => k == BlockKind.Bullet || k == BlockKind.Task || k == BlockKind.Numbered;

        private void GetSelectedLineRange(out int first, out int last)
        {
            GetSelection(out var s, out var e);
            first = s.Line;
            last = e.Line;
            if (last > first && e.Col == 0) last--;
        }

        // =====================================================================
        // Enter / Backspace / Tab
        // =====================================================================

        /// <summary>Enter with list/quote continuation, empty-item exit and auto-closed code fences.</summary>
        private void DoEnter()
        {
            if (HasSelection)
            {
                GetSelection(out var s, out var e);
                m_Caret = m_Anchor = m_Buffer.Replace(s, e, "", m_Caret);
            }

            int lineIdx = m_Caret.Line;
            string line = m_Buffer.GetLine(lineIdx);
            byte cs = CodeStateOf(lineIdx);
            string indent = LeadingWhitespace(line, m_Caret.Col);

            if (cs == BlockParser.StateCode)
            {
                InsertNewline("\n" + indent);
                return;
            }

            if (cs == BlockParser.StateFence)
            {
                if (m_Caret.Col == line.Length && IsUnclosedOpeningFence(lineIdx))
                {
                    string fenceIndent = LeadingWhitespace(line);
                    var eol = new TextPos(lineIdx, line.Length);
                    m_Buffer.Replace(eol, eol, "\n" + fenceIndent + "\n" + fenceIndent + "```", m_Caret);
                    m_Caret = m_Anchor = new TextPos(lineIdx + 1, fenceIndent.Length);
                    m_Buffer.SealHistory();
                    AfterEdit();
                    return;
                }
                InsertNewline("\n" + indent);
                return;
            }

            var info = BlockParser.Parse(line, BlockParser.StateNone);
            bool continues = IsListKind(info.Kind) || info.Kind == BlockKind.Quote;
            if (continues && m_Caret.Col >= info.PrefixLen)
            {
                if (line.Substring(info.PrefixLen).Trim().Length == 0)
                {
                    EndEmptyItem(lineIdx, info);
                    return;
                }
                InsertNewline("\n" + ContinuationPrefix(line, info));
                return;
            }

            InsertNewline("\n" + indent);
        }

        /// <summary>Shift+Enter: new line keeping indentation but without list continuation.</summary>
        private void DoSoftEnter() => InsertNewline("\n" + LeadingWhitespace(m_Buffer.GetLine(m_Caret.Line), m_Caret.Col));

        private void InsertNewline(string text)
        {
            ReplaceSelection(text);
            m_Buffer.SealHistory();
        }

        /// <summary>Prefix that Enter continues with on a list or quote line ("- [ ] ", "3. ", "> "), or null.</summary>
        public static string ListContinuation(string line)
        {
            var info = BlockParser.Parse(line ?? "", BlockParser.StateNone);
            return IsListKind(info.Kind) || info.Kind == BlockKind.Quote ? ContinuationPrefix(line, info) : null;
        }

        private static string ContinuationPrefix(string line, BlockInfo info)
        {
            string lead = LeadingWhitespace(line);
            switch (info.Kind)
            {
                case BlockKind.Bullet:
                    return lead + line[lead.Length] + " ";
                case BlockKind.Task:
                    return lead + line[lead.Length] + " [ ] ";
                case BlockKind.Numbered:
                    var m = NumberPrefixRx.Match(line);
                    return m.Success && int.TryParse(m.Groups[2].Value, out int n)
                        ? m.Groups[1].Value + (n + 1) + m.Groups[3].Value + " "
                        : lead;
                case BlockKind.Quote:
                    return QuoteMarkerRx.Match(line).Value;
                default:
                    return lead;
            }
        }

        /// <summary>Enter on an empty list item outdents it (nested) or removes the marker (top level).</summary>
        private void EndEmptyItem(int lineIdx, BlockInfo info)
        {
            string line = m_Buffer.GetLine(lineIdx);
            if (IsListKind(info.Kind) && info.IndentLevel > 0)
            {
                int remove = line.StartsWith("\t") ? 1 : Math.Min(2, LeadingWhitespace(line).Length);
                m_Buffer.Replace(new TextPos(lineIdx, 0), new TextPos(lineIdx, remove), "", m_Caret);
                m_Caret = m_Anchor = new TextPos(lineIdx, m_Buffer.GetLine(lineIdx).Length);
            }
            else
            {
                m_Buffer.Replace(new TextPos(lineIdx, 0), new TextPos(lineIdx, line.Length), "", m_Caret);
                m_Caret = m_Anchor = new TextPos(lineIdx, 0);
            }
            m_Buffer.SealHistory();
            AfterEdit();
        }

        private bool IsUnclosedOpeningFence(int line)
        {
            EnsureCodeStates();
            int fencesBefore = 0;
            for (int i = 0; i < line; i++) if (m_CodeStates[i] == BlockParser.StateFence) fencesBefore++;
            if (fencesBefore % 2 != 0) return false; // this fence closes a block
            for (int i = line + 1; i < m_CodeStates.Length; i++) if (m_CodeStates[i] == BlockParser.StateFence) return false;
            return true;
        }

        /// <summary>Backspace right after a block marker removes the marker (list, task, quote, heading).</summary>
        private void DoBackspace(bool word)
        {
            if (HasSelection) { ReplaceSelection(""); return; }

            if (!word && m_Caret.Col > 0 && CodeStateOf(m_Caret.Line) == BlockParser.StateNone)
            {
                string line = m_Buffer.GetLine(m_Caret.Line);
                var info = BlockParser.Parse(line, BlockParser.StateNone);
                bool marker = IsListKind(info.Kind) || info.Kind == BlockKind.Quote || info.Kind == BlockKind.Heading;
                if (marker && info.PrefixLen > 0 && m_Caret.Col == info.PrefixLen)
                {
                    int keep = info.Kind == BlockKind.Heading ? 0 : LeadingWhitespace(line).Length;
                    m_Caret = m_Anchor = m_Buffer.Replace(new TextPos(m_Caret.Line, keep), new TextPos(m_Caret.Line, info.PrefixLen), "", m_Caret);
                    m_Buffer.SealHistory();
                    AfterEdit();
                    return;
                }
            }

            TextPos from;
            if (m_Caret.Col > 0)
                from = new TextPos(m_Caret.Line, word ? WordLeft(m_Caret) : m_Caret.Col - 1);
            else if (m_Caret.Line > 0)
                from = new TextPos(m_Caret.Line - 1, m_Buffer.GetLine(m_Caret.Line - 1).Length);
            else return;

            m_Caret = m_Anchor = m_Buffer.Replace(from, m_Caret, "", m_Caret);
            AfterEdit();
        }

        /// <summary>Tab indents list items as a whole (and multi-line selections); elsewhere it inserts spaces.</summary>
        private void DoTab(bool outdent)
        {
            GetSelection(out var s, out var e);
            bool multiLine = s.Line != e.Line;
            byte cs = CodeStateOf(s.Line);
            bool listLine = !multiLine && cs == BlockParser.StateNone && IsListKind(BlockParser.Parse(m_Buffer.GetLine(s.Line), cs).Kind);

            if (!outdent && !multiLine && !listLine)
            {
                ReplaceSelection(cs == BlockParser.StateCode ? "    " : "  ");
                m_Buffer.SealHistory();
                return;
            }

            int lastLine = e.Line;
            if (multiLine && e.Col == 0) lastLine--;

            TextPos caret = m_Caret, anchor = m_Anchor;
            for (int l = s.Line; l <= lastLine; l++)
            {
                string text = m_Buffer.GetLine(l);
                if (!outdent)
                {
                    m_Buffer.Replace(new TextPos(l, 0), new TextPos(l, 0), "  ", m_Caret);
                    if (caret.Line == l) caret = new TextPos(l, caret.Col + 2);
                    if (anchor.Line == l) anchor = new TextPos(l, anchor.Col + 2);
                }
                else
                {
                    int remove = 0;
                    if (text.StartsWith("\t")) remove = 1;
                    else while (remove < 2 && remove < text.Length && text[remove] == ' ') remove++;
                    if (remove == 0) continue;
                    m_Buffer.Replace(new TextPos(l, 0), new TextPos(l, remove), "", m_Caret);
                    if (caret.Line == l) caret = new TextPos(l, Math.Max(0, caret.Col - remove));
                    if (anchor.Line == l) anchor = new TextPos(l, Math.Max(0, anchor.Col - remove));
                }
            }

            m_Caret = caret; m_Anchor = anchor;
            m_Buffer.SealHistory();
            AfterEdit();
        }

        // =====================================================================
        // Line commands
        // =====================================================================

        /// <summary>Ctrl+Enter: toggles tasks done/open, turns bullets and plain lines into tasks.</summary>
        public void ToggleTaskOnSelection()
        {
            if (m_ReadOnly) return;
            GetSelectedLineRange(out int first, out int last);
            var lines = new string[last - first + 1];
            int caretDelta = 0, anchorDelta = 0;
            bool changed = false;

            for (int l = first; l <= last; l++)
            {
                string raw = m_Buffer.GetLine(l);
                string result = raw;
                int insertAt = -1, delta = 0;

                if (CodeStateOf(l) == BlockParser.StateNone)
                {
                    var info = BlockParser.Parse(raw, BlockParser.StateNone);
                    switch (info.Kind)
                    {
                        case BlockKind.Task:
                            char mark = info.TaskMark == 'x' ? ' ' : 'x';
                            result = raw.Substring(0, info.TaskMarkIndex) + mark + raw.Substring(info.TaskMarkIndex + 1);
                            break;
                        case BlockKind.Bullet:
                            insertAt = info.PrefixLen;
                            result = raw.Insert(insertAt, "[ ] ");
                            delta = 4;
                            break;
                        case BlockKind.Numbered:
                            int lead = LeadingWhitespace(raw).Length;
                            result = raw.Substring(0, lead) + "- [ ] " + raw.Substring(info.PrefixLen);
                            insertAt = lead;
                            delta = 6 - (info.PrefixLen - lead);
                            break;
                        case BlockKind.Paragraph:
                            insertAt = LeadingWhitespace(raw).Length;
                            result = raw.Insert(insertAt, "- [ ] ");
                            delta = 6;
                            break;
                    }
                }

                if (result != raw) changed = true;
                lines[l - first] = result;
                if (insertAt >= 0)
                {
                    if (m_Caret.Line == l && m_Caret.Col >= insertAt) caretDelta = delta;
                    if (m_Anchor.Line == l && m_Anchor.Col >= insertAt) anchorDelta = delta;
                }
            }

            if (!changed) return;

            var caret = m_Caret;
            var anchor = m_Anchor;
            m_Buffer.Replace(new TextPos(first, 0), new TextPos(last, m_Buffer.GetLine(last).Length), string.Join("\n", lines), m_Caret);
            m_Caret = new TextPos(caret.Line, Math.Max(0, caret.Col + caretDelta));
            m_Anchor = new TextPos(anchor.Line, Math.Max(0, anchor.Col + anchorDelta));
            m_Buffer.SealHistory();
            AfterEdit();
        }

        public void MoveLines(int dir)
        {
            if (m_ReadOnly) return;
            GetSelectedLineRange(out int first, out int last);
            if (dir < 0 && first == 0) return;
            if (dir > 0 && last >= m_Buffer.LineCount - 1) return;

            var block = new List<string>();
            for (int l = first; l <= last; l++) block.Add(m_Buffer.GetLine(l));
            string blockText = string.Join("\n", block);
            var caret = m_Caret;
            var anchor = m_Anchor;

            if (dir < 0)
            {
                string above = m_Buffer.GetLine(first - 1);
                m_Buffer.Replace(new TextPos(first - 1, 0), new TextPos(last, m_Buffer.GetLine(last).Length), blockText + "\n" + above, m_Caret);
            }
            else
            {
                string below = m_Buffer.GetLine(last + 1);
                m_Buffer.Replace(new TextPos(first, 0), new TextPos(last + 1, below.Length), below + "\n" + blockText, m_Caret);
            }

            m_Caret = new TextPos(caret.Line + dir, caret.Col);
            m_Anchor = new TextPos(anchor.Line + dir, anchor.Col);
            m_Buffer.SealHistory();
            AfterEdit();
        }

        public void DuplicateLines()
        {
            if (m_ReadOnly) return;
            GetSelectedLineRange(out int first, out int last);
            var block = new List<string>();
            for (int l = first; l <= last; l++) block.Add(m_Buffer.GetLine(l));
            int count = last - first + 1;

            var eol = new TextPos(last, m_Buffer.GetLine(last).Length);
            var caret = m_Caret;
            var anchor = m_Anchor;
            m_Buffer.Replace(eol, eol, "\n" + string.Join("\n", block), m_Caret);
            m_Caret = new TextPos(caret.Line + count, caret.Col);
            m_Anchor = new TextPos(anchor.Line + count, anchor.Col);
            m_Buffer.SealHistory();
            AfterEdit();
        }

        public void DeleteLines()
        {
            if (m_ReadOnly) return;
            GetSelectedLineRange(out int first, out int last);
            if (last < m_Buffer.LineCount - 1)
                m_Buffer.Replace(new TextPos(first, 0), new TextPos(last + 1, 0), "", m_Caret);
            else if (first > 0)
                m_Buffer.Replace(new TextPos(first - 1, m_Buffer.GetLine(first - 1).Length), new TextPos(last, m_Buffer.GetLine(last).Length), "", m_Caret);
            else
                m_Buffer.Replace(new TextPos(0, 0), new TextPos(last, m_Buffer.GetLine(last).Length), "", m_Caret);

            int line = Math.Min(first, m_Buffer.LineCount - 1);
            m_Caret = m_Anchor = new TextPos(line, 0);
            m_Buffer.SealHistory();
            AfterEdit();
        }

        // =====================================================================
        // Folding
        // =====================================================================

        public bool IsFolded(int line) => m_Folded.Contains(line);

        private void ShiftFolds(int first, int removed, int inserted)
        {
            if (m_Folded.Count == 0) return;
            var shifted = new HashSet<int>();
            foreach (int f in m_Folded)
            {
                if (f < first) shifted.Add(f);
                else if (f >= first + removed) shifted.Add(f + inserted - removed);
                else if (f == first && inserted > 0) shifted.Add(f); // heading line edited in place
            }
            m_Folded.Clear();
            m_Folded.UnionWith(shifted);
        }

        private void ToggleFoldAt(LineView v) => ToggleFold(m_Views.IndexOf(v));

        public void ToggleFold(int line)
        {
            if (line < 0 || line >= m_Views.Count) return;
            EnsureCodeStates();
            if (InfoOf(line).Kind != BlockKind.Heading) return;

            if (!m_Folded.Remove(line))
            {
                m_Folded.Add(line);
                // keep the caret out of the collapsed section
                int end = SectionEnd(line);
                if (m_Caret.Line > line && m_Caret.Line <= end) m_Caret = m_Anchor = new TextPos(line, m_Buffer.GetLine(line).Length);
                if (m_Anchor.Line > line && m_Anchor.Line <= end) m_Anchor = m_Caret;
            }
            m_StructureDirty = true;
            RefreshVisuals(false);
        }

        public void SetAllFolded(bool folded)
        {
            m_Folded.Clear();
            if (folded)
            {
                foreach (var h in GetHeadings()) if (SectionEnd(h.Line) > h.Line) m_Folded.Add(h.Line);

                // move the caret to the heading that now hides it
                int owner = -1;
                foreach (var h in GetHeadings()) if (h.Line <= m_Caret.Line) owner = h.Line;
                if (owner >= 0 && owner != m_Caret.Line && m_Folded.Count > 0)
                    m_Caret = m_Anchor = new TextPos(owner, m_Buffer.GetLine(owner).Length);
            }
            m_StructureDirty = true;
            RefreshVisuals(false);
        }

        /// <summary>Last line of the section started by the heading at <paramref name="headingLine"/>.</summary>
        private int SectionEnd(int headingLine)
        {
            int level = InfoOf(headingLine).Level;
            for (int i = headingLine + 1; i < m_Buffer.LineCount; i++)
            {
                if (CodeStateOf(i) != BlockParser.StateNone) continue;
                var info = BlockParser.Parse(m_Buffer.GetLine(i), BlockParser.StateNone);
                if (info.Kind == BlockKind.Heading && info.Level <= level) return i - 1;
            }
            return m_Buffer.LineCount - 1;
        }

        private void UnfoldToReveal(int line)
        {
            int minLevel = 7;
            for (int j = line - 1; j >= 0 && minLevel > 1; j--)
            {
                var info = m_Views[j].Info;
                if (info.Kind != BlockKind.Heading || info.Level >= minLevel) continue;
                minLevel = info.Level;
                m_Folded.Remove(j);
            }
            m_StructureDirty = true;
            UpdateStructure();
        }

        // =====================================================================
        // Outline
        // =====================================================================

        public List<HeadingInfo> GetHeadings()
        {
            var result = new List<HeadingInfo>();
            EnsureCodeStates();
            for (int i = 0; i < m_Buffer.LineCount; i++)
            {
                if (m_CodeStates[i] != BlockParser.StateNone) continue;
                string raw = m_Buffer.GetLine(i);
                if (raw.Length == 0 || raw[0] != '#') continue;
                var info = BlockParser.Parse(raw, BlockParser.StateNone);
                if (info.Kind != BlockKind.Heading) continue;
                result.Add(new HeadingInfo { Line = i, Level = info.Level, Text = PlainInline(raw.Substring(info.PrefixLen)).Trim() });
            }
            return result;
        }

        /// <summary>Moves the caret to a line, unfolding it if needed, and scrolls it near the top.</summary>
        public void RevealLine(int line)
        {
            if (m_Views.Count == 0) return;
            line = Mathf.Clamp(line, 0, m_Views.Count - 1);
            if (m_Views[line].Hidden) UnfoldToReveal(line);

            m_Caret = m_Anchor = new TextPos(line, m_Buffer.GetLine(line).Length);
            m_DesiredX = -1f;
            m_Buffer.SealHistory();
            ScrollLineNearTop(line);
            Focus();
        }

        private void ScrollLineNearTop(int line)
        {
            CancelPendingScroll();
            RefreshVisuals(false);
            if (m_Scroll.contentViewport == null || m_LinesContainer.panel == null) return;
            float offset = m_LinesContainer.ChangeCoordinatesTo(m_Scroll.contentContainer, Vector2.zero).y;
            m_Scroll.scrollOffset = new Vector2(0f, Mathf.Max(0f, offset + m_Tops[line] - 40f));
            RefreshVisuals(false);
        }

        // =====================================================================
        // Search
        // =====================================================================

        public List<(TextPos start, TextPos end)> FindAll(string query, bool matchCase)
        {
            var result = new List<(TextPos, TextPos)>();
            if (string.IsNullOrEmpty(query) || query.IndexOf('\n') >= 0) return result;

            var cmp = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            for (int l = 0; l < m_Buffer.LineCount; l++)
            {
                string line = m_Buffer.GetLine(l);
                int idx = 0;
                while ((idx = line.IndexOf(query, idx, cmp)) >= 0)
                {
                    result.Add((new TextPos(l, idx), new TextPos(l, idx + query.Length)));
                    idx += Math.Max(1, query.Length);
                }
            }
            return result;
        }

        public void SetSearchMatches(List<(TextPos start, TextPos end)> matches, int current)
        {
            m_SearchMatches.Clear();
            if (matches != null) m_SearchMatches.AddRange(matches);
            m_SearchCurrent = current;
            RefreshVisuals(false);
        }

        public void ClearSearch() => SetSearchMatches(null, -1);

        private void UpdateSearchRects()
        {
            int used = 0;
            if (m_SearchMatches.Count > 0 && m_ShownFirst >= 0)
            {
                var other = NotesTheme.SearchMatch;
                var current = NotesTheme.SearchMatchCurrent;
                for (int i = 0; i < m_SearchMatches.Count; i++)
                {
                    var (a, b) = m_SearchMatches[i];
                    if (b.Line < m_ShownFirst || a.Line > m_ShownLast) continue;
                    used = AddRangeRects(m_SearchRects, used, a, b, i == m_SearchCurrent ? current : other, extendLineEnds: false);
                }
            }

            for (int i = used; i < m_SearchRects.Count; i++)
                m_SearchRects[i].style.display = DisplayStyle.None;
        }

        /// <summary>Replaces the whole document as a single undo step, keeping the caret where it was (clamped).</summary>
        public void ReplaceAllText(string newText)
        {
            if (m_ReadOnly) return;
            var caret = m_Caret;
            m_Buffer.Replace(new TextPos(0, 0), m_Buffer.End, MarkdownScan.NormalizeNewlines(newText), m_Caret);
            m_Caret = m_Anchor = m_Buffer.Clamp(caret);
            m_Buffer.SealHistory();
            AfterEdit();
        }
    }
}
