using System;
using System.Collections.Generic;
using System.Text;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.Notes.LiveEditor
{
    /// <summary>A (line, column) position inside a <see cref="NoteTextBuffer"/>.</summary>
    public readonly struct TextPos : IEquatable<TextPos>, IComparable<TextPos>
    {
        public readonly int Line;
        public readonly int Col;

        public TextPos(int line, int col) { Line = line; Col = col; }

        public bool Equals(TextPos o) => Line == o.Line && Col == o.Col;
        public override bool Equals(object obj) => obj is TextPos p && Equals(p);
        public override int GetHashCode() => (Line * 397) ^ Col;
        public int CompareTo(TextPos o) => Line != o.Line ? Line.CompareTo(o.Line) : Col.CompareTo(o.Col);

        public static bool operator ==(TextPos a, TextPos b) => a.Equals(b);
        public static bool operator !=(TextPos a, TextPos b) => !a.Equals(b);
        public static bool operator <(TextPos a, TextPos b) => a.CompareTo(b) < 0;
        public static bool operator >(TextPos a, TextPos b) => a.CompareTo(b) > 0;
        public static bool operator <=(TextPos a, TextPos b) => a.CompareTo(b) <= 0;
        public static bool operator >=(TextPos a, TextPos b) => a.CompareTo(b) >= 0;

        public override string ToString() => $"({Line}:{Col})";
    }

    /// <summary>
    /// Single source of truth for note text: a list of raw markdown lines.
    /// Every mutation goes through <see cref="Replace"/>, which records undo history.
    /// </summary>
    public class NoteTextBuffer
    {
        private sealed class EditRecord
        {
            public TextPos Start;
            public string Removed;
            public string Inserted;
            public TextPos CaretBefore;
            public TextPos CaretAfter;
            public DateTime Time;
        }

        /// <summary>Raised after any edit: (firstLine, removedLineCount, insertedLineCount).</summary>
        public event Action<int, int, int> Changed;

        private readonly List<string> m_Lines = new List<string> { "" };
        private readonly List<EditRecord> m_Undo = new List<EditRecord>();
        private readonly List<EditRecord> m_Redo = new List<EditRecord>();

        public const double CoalesceSeconds = 1.0;
        public const int MaxHistory = 500;

        public int LineCount => m_Lines.Count;
        public string GetLine(int i) => m_Lines[i];
        public IReadOnlyList<string> Lines => m_Lines;

        public bool CanUndo => m_Undo.Count > 0;
        public bool CanRedo => m_Redo.Count > 0;

        // ---------------------------------------------------------------- text I/O

        public string GetText() => string.Join("\n", m_Lines);

        /// <summary>Replaces the whole document and clears history. Does not raise <see cref="Changed"/> granularly.</summary>
        public void SetText(string text)
        {
            m_Undo.Clear();
            m_Redo.Clear();
            int oldCount = m_Lines.Count;
            m_Lines.Clear();
            foreach (var l in Normalize(text).Split('\n')) m_Lines.Add(l);
            Changed?.Invoke(0, oldCount, m_Lines.Count);
        }

        public static string Normalize(string s) => string.IsNullOrEmpty(s) ? "" : s.Replace("\r\n", "\n").Replace('\r', '\n');

        // ---------------------------------------------------------------- queries

        public TextPos Clamp(TextPos p)
        {
            int line = Math.Max(0, Math.Min(p.Line, m_Lines.Count - 1));
            int col = Math.Max(0, Math.Min(p.Col, m_Lines[line].Length));
            return new TextPos(line, col);
        }

        public TextPos End => new TextPos(m_Lines.Count - 1, m_Lines[m_Lines.Count - 1].Length);

        public string GetRange(TextPos a, TextPos b)
        {
            Order(ref a, ref b);
            a = Clamp(a); b = Clamp(b);
            if (a.Line == b.Line) return m_Lines[a.Line].Substring(a.Col, b.Col - a.Col);

            var sb = new StringBuilder();
            sb.Append(m_Lines[a.Line], a.Col, m_Lines[a.Line].Length - a.Col);
            for (int i = a.Line + 1; i < b.Line; i++) sb.Append('\n').Append(m_Lines[i]);
            sb.Append('\n').Append(m_Lines[b.Line], 0, b.Col);
            return sb.ToString();
        }

        public static void Order(ref TextPos a, ref TextPos b)
        {
            if (a > b) { var t = a; a = b; b = t; }
        }

        /// <summary>Position reached after inserting <paramref name="text"/> at <paramref name="start"/>.</summary>
        public static TextPos EndOfInsert(TextPos start, string text)
        {
            if (string.IsNullOrEmpty(text)) return start;
            int lines = 0, lastBreak = -1;
            for (int i = 0; i < text.Length; i++)
                if (text[i] == '\n') { lines++; lastBreak = i; }
            if (lines == 0) return new TextPos(start.Line, start.Col + text.Length);
            return new TextPos(start.Line + lines, text.Length - lastBreak - 1);
        }

        // ---------------------------------------------------------------- editing

        /// <summary>
        /// Replaces the range [a,b) with <paramref name="text"/>, records history and returns the caret position after the insert.
        /// </summary>
        public TextPos Replace(TextPos a, TextPos b, string text, TextPos caretBefore, bool allowCoalesce = false)
        {
            Order(ref a, ref b);
            a = Clamp(a); b = Clamp(b);
            text = Normalize(text);

            string removed = GetRange(a, b);
            if (removed.Length == 0 && text.Length == 0) return a;

            TextPos caretAfter = ApplyRaw(a, b, text);

            var rec = new EditRecord
            {
                Start = a, Removed = removed, Inserted = text,
                CaretBefore = caretBefore, CaretAfter = caretAfter, Time = DateTime.UtcNow
            };

            m_Redo.Clear();
            if (allowCoalesce && TryCoalesce(rec)) { /* merged into previous */ }
            else
            {
                m_Undo.Add(rec);
                if (m_Undo.Count > MaxHistory) m_Undo.RemoveAt(0);
            }
            return caretAfter;
        }

        private bool TryCoalesce(EditRecord next)
        {
            if (m_Undo.Count == 0) return false;
            var prev = m_Undo[m_Undo.Count - 1];

            bool typing = prev.Removed.Length == 0 && next.Removed.Length == 0
                          && prev.Inserted.IndexOf('\n') < 0 && next.Inserted.IndexOf('\n') < 0;
            if (!typing) return false;
            if ((next.Time - prev.Time).TotalSeconds > CoalesceSeconds) return false;
            if (EndOfInsert(prev.Start, prev.Inserted) != next.Start) return false;
            // Word-ish boundary: don't merge a space following letters into one huge undo step
            if (next.Inserted.Length > 0 && next.Inserted[0] == ' ' && prev.Inserted.Length > 0 && prev.Inserted[prev.Inserted.Length - 1] != ' ') return false;

            prev.Inserted += next.Inserted;
            prev.CaretAfter = next.CaretAfter;
            prev.Time = next.Time;
            return true;
        }

        public bool Undo(out TextPos caret)
        {
            caret = default;
            if (m_Undo.Count == 0) return false;
            var rec = m_Undo[m_Undo.Count - 1];
            m_Undo.RemoveAt(m_Undo.Count - 1);

            ApplyRaw(rec.Start, EndOfInsert(rec.Start, rec.Inserted), rec.Removed);
            m_Redo.Add(rec);
            caret = Clamp(rec.CaretBefore);
            return true;
        }

        public bool Redo(out TextPos caret)
        {
            caret = default;
            if (m_Redo.Count == 0) return false;
            var rec = m_Redo[m_Redo.Count - 1];
            m_Redo.RemoveAt(m_Redo.Count - 1);

            ApplyRaw(rec.Start, EndOfInsert(rec.Start, rec.Removed), rec.Inserted);
            m_Undo.Add(rec);
            caret = Clamp(rec.CaretAfter);
            return true;
        }

        /// <summary>Breaks undo coalescing (e.g. after caret jumps).</summary>
        public void SealHistory()
        {
            if (m_Undo.Count > 0) m_Undo[m_Undo.Count - 1].Time = DateTime.MinValue;
        }

        /// <summary>Low-level splice without history. Returns end position of inserted text.</summary>
        private TextPos ApplyRaw(TextPos a, TextPos b, string text)
        {
            string head = m_Lines[a.Line].Substring(0, a.Col);
            string tail = m_Lines[b.Line].Substring(b.Col);

            int removedCount = b.Line - a.Line + 1;
            var pieces = text.Split('\n');

            m_Lines.RemoveRange(a.Line, removedCount);

            TextPos end;
            if (pieces.Length == 1)
            {
                m_Lines.Insert(a.Line, head + pieces[0] + tail);
                end = new TextPos(a.Line, head.Length + pieces[0].Length);
            }
            else
            {
                var list = new List<string>(pieces.Length) { head + pieces[0] };
                for (int i = 1; i < pieces.Length - 1; i++) list.Add(pieces[i]);
                list.Add(pieces[pieces.Length - 1] + tail);
                m_Lines.InsertRange(a.Line, list);
                end = new TextPos(a.Line + pieces.Length - 1, pieces[pieces.Length - 1].Length);
            }

            Changed?.Invoke(a.Line, removedCount, pieces.Length);
            return end;
        }
    }
}
