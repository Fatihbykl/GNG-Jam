using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.TextCore.Text;
using UnityEngine.UIElements;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.Terminal.Rendering
{
    /// <summary>One terminal cell as copied out of the emulator buffer.</summary>
    internal struct TerminalCell : IEquatable<TerminalCell>
    {
        public int Code;
        public int Attribute;
        public int Width;

        public bool Equals(TerminalCell other) => Code == other.Code && Attribute == other.Attribute && Width == other.Width;
    }

    /// <summary>What the row should show besides its cells.</summary>
    internal struct RowOverlay : IEquatable<RowOverlay>
    {
        public int SelectionStart;  // inclusive column, -1 when nothing is selected
        public int SelectionEnd;    // inclusive column
        public int CursorColumn;    // -1 when the cursor is not on this row or hidden
        public bool CursorFilled;   // filled block when focused and in the visible blink phase, outline otherwise

        public static readonly RowOverlay None = new RowOverlay { SelectionStart = -1, SelectionEnd = -1, CursorColumn = -1 };

        public bool Equals(RowOverlay other) =>
            SelectionStart == other.SelectionStart && SelectionEnd == other.SelectionEnd &&
            CursorColumn == other.CursorColumn && CursorFilled == other.CursorFilled;
    }

    /// <summary>
    /// Cell-grid renderer for the terminal. Every glyph is placed at column * cellWidth, so fallback
    /// glyphs with a different advance cannot shift the rest of the line, and backgrounds, selection
    /// and cursor are exact cell rectangles. Rows are separate elements and only repaint when their
    /// content changed.
    /// </summary>
    internal sealed class TerminalGridView : VisualElement
    {
        private readonly List<TerminalRowView> _rows = new List<TerminalRowView>();

        public TerminalFonts Fonts { get; private set; }
        public float CellWidth => Fonts != null ? Fonts.CellWidth : 8f;
        public float CellHeight => Fonts != null ? Fonts.CellHeight : 18f;
        public int RowCount => _rows.Count;

        public TerminalGridView()
        {
            name = "terminal-grid";
            AddToClassList("terminal-grid");
            pickingMode = PickingMode.Position;
            style.overflow = Overflow.Hidden;
            style.flexGrow = 1;
            // Crisp text snaps to device pixels based on the row's position, which a layout change can move.
            RegisterCallback<GeometryChangedEvent>(_ => MarkAllDirty());
        }

        public void SetFonts(TerminalFonts fonts)
        {
            Fonts = fonts;
            for (int i = 0; i < _rows.Count; i++) PlaceRow(_rows[i], i);
            MarkAllDirty();
        }

        public void SetRowCount(int count)
        {
            while (_rows.Count < count)
            {
                var row = new TerminalRowView(this);
                PlaceRow(row, _rows.Count);
                _rows.Add(row);
                Add(row);
            }
            while (_rows.Count > count)
            {
                var row = _rows[_rows.Count - 1];
                _rows.RemoveAt(_rows.Count - 1);
                row.RemoveFromHierarchy();
            }
        }

        public TerminalRowView GetRow(int index) => _rows[index];

        public void MarkAllDirty()
        {
            foreach (var row in _rows) row.Invalidate();
        }

        public void Clear(int fromRow = 0)
        {
            for (int i = fromRow; i < _rows.Count; i++) _rows[i].SetEmpty();
        }

        private void PlaceRow(TerminalRowView row, int index)
        {
            row.style.top = index * CellHeight;
            row.style.height = CellHeight;
        }
    }

    internal sealed class TerminalRowView : VisualElement
    {
        private const int FlagBold = 1, FlagUnderline = 2, FlagInverse = 8, FlagInvisible = 16,
                          FlagDim = 32, FlagItalic = 64, FlagStrike = 128;

        private static readonly List<ColoredRect> s_rects = new List<ColoredRect>(256);
        private static readonly StringBuilder s_run = new StringBuilder(256);

        private readonly TerminalGridView _grid;
        private TerminalCell[] _cells = new TerminalCell[0];
        private int _count;
        private RowOverlay _overlay = RowOverlay.None;

        public TerminalRowView(TerminalGridView grid)
        {
            _grid = grid;
            pickingMode = PickingMode.Ignore;
            style.position = Position.Absolute;
            style.left = 0;
            style.right = 0;
            generateVisualContent += Draw;
        }

        public void Invalidate() => MarkDirtyRepaint();

        public void SetEmpty()
        {
            if (_count == 0 && _overlay.Equals(RowOverlay.None)) return;
            _count = 0;
            _overlay = RowOverlay.None;
            MarkDirtyRepaint();
        }

        /// <summary>Copies a buffer line; repaints only when cells or overlay differ. Call with the terminal locked.</summary>
        public void Load(XtermSharp.BufferLine line, int cols, RowOverlay overlay)
        {
            if (_cells.Length < cols) Array.Resize(ref _cells, cols);

            bool changed = _count != cols || !_overlay.Equals(overlay);
            int length = line != null ? Math.Min(line.Length, cols) : 0;
            for (int x = 0; x < cols; x++)
            {
                TerminalCell cell;
                if (x < length)
                {
                    var data = line[x];
                    cell.Code = data.Code;
                    cell.Attribute = data.Attribute;
                    cell.Width = data.Width;
                }
                else
                {
                    cell = default;
                    cell.Attribute = XtermSharp.CharData.DefaultAttr;
                    cell.Width = 1;
                }
                if (!changed && !_cells[x].Equals(cell)) changed = true;
                _cells[x] = cell;
            }

            _count = cols;
            _overlay = overlay;
            if (changed) MarkDirtyRepaint();
        }

        private void Draw(MeshGenerationContext mgc)
        {
            var fonts = _grid.Fonts;
            // Destroyed font materials are rebuilt by the window on its next render tick; skip drawing until then.
            if (fonts == null || fonts.Regular == null || fonts.Regular.material == null) return;

            float cw = fonts.CellWidth;
            float ch = fonts.CellHeight;
            float lineWidth = BoxDrawing.LineWidth(fonts.FontSize);
            var palette = TerminalPalette.Colors;

            s_rects.Clear();

            // Pass 1: backgrounds (merged into runs of equal color), selection and cursor.
            int runStart = -1;
            Color32 runColor = default;
            for (int x = 0; x <= _count; x++)
            {
                bool has = false;
                Color32 bg = default;
                if (x < _count) has = TryGetBackground(x, palette, out bg);
                if (runStart >= 0 && (!has || !SameColor(bg, runColor)))
                {
                    s_rects.Add(new ColoredRect(Rect.MinMaxRect(runStart * cw, 0, x * cw, ch), runColor));
                    runStart = -1;
                }
                if (has && runStart < 0)
                {
                    runStart = x;
                    runColor = bg;
                }
            }

            int cursor = _overlay.CursorColumn;
            if (cursor >= 0 && cursor < Math.Max(_count, 1) && !_overlay.CursorFilled)
            {
                float x0 = cursor * cw, x1 = x0 + cw * CellSpan(cursor);
                var c = TerminalPalette.CursorBackground;
                s_rects.Add(new ColoredRect(Rect.MinMaxRect(x0, 0, x1, 1), c));
                s_rects.Add(new ColoredRect(Rect.MinMaxRect(x0, ch - 1, x1, ch), c));
                s_rects.Add(new ColoredRect(Rect.MinMaxRect(x0, 0, x0 + 1, ch), c));
                s_rects.Add(new ColoredRect(Rect.MinMaxRect(x1 - 1, 0, x1, ch), c));
            }

            // Pass 2: procedural glyphs and text decorations.
            for (int x = 0; x < _count; x++)
            {
                var cell = _cells[x];
                if (IsWidePlaceholder(x)) continue;
                int flags = cell.Attribute >> 18;
                if ((flags & FlagInvisible) != 0) continue;

                Color32 fg = GetForeground(x, palette);
                var cellRect = Rect.MinMaxRect(x * cw, 0, (x + CellSpan(x)) * cw, ch);

                if (BoxDrawing.IsProcedural(cell.Code))
                    BoxDrawing.AppendRects(cell.Code, cellRect, fg, lineWidth, s_rects);

                bool visible = cell.Code > 32;
                if ((flags & FlagUnderline) != 0 && visible)
                {
                    float y = Mathf.Round(ch - Mathf.Max(2f, ch * 0.12f));
                    s_rects.Add(new ColoredRect(Rect.MinMaxRect(cellRect.xMin, y, cellRect.xMax, y + lineWidth), fg));
                }
                if ((flags & FlagStrike) != 0 && visible)
                {
                    float y = Mathf.Round(ch * 0.52f);
                    s_rects.Add(new ColoredRect(Rect.MinMaxRect(cellRect.xMin, y, cellRect.xMax, y + lineWidth), fg));
                }
            }

            WriteQuads(mgc, s_rects);

            // Rounded corners need arcs.
            for (int x = 0; x < _count; x++)
            {
                if (!BoxDrawing.IsRoundedCorner(_cells[x].Code) || IsInvisible(x)) continue;
                var cellRect = Rect.MinMaxRect(x * cw, 0, (x + 1) * cw, ch);
                BoxDrawing.DrawRoundedCorner(mgc.painter2D, _cells[x].Code, cellRect, GetForeground(x, palette), lineWidth);
            }

            // Pass 3: text. ASCII cells with identical style are drawn as one run anchored at the run's
            // first column (monospace advance equals the cell width). Everything else is drawn per cell.
            // Hinted bitmap glyphs only stay sharp on whole device pixels; the row's own position may be
            // fractional (padding, layout), so the text origin is shifted onto the pixel grid.
            Vector2 snap = Vector2.zero;
            if (fonts.Crisp)
            {
                float ppp = fonts.PixelsPerPoint;
                Vector2 origin = this.LocalToWorld(Vector2.zero) * ppp;
                snap = new Vector2(Mathf.Round(origin.x) - origin.x, Mathf.Round(origin.y) - origin.y) / ppp;
            }
            float drawSize = fonts.DrawSize;
            int start = -1;
            Color32 color = default;
            FontAsset font = null;
            s_run.Clear();
            for (int x = 0; x <= _count; x++)
            {
                bool plain = false;
                Color32 fg = default;
                FontAsset cellFont = null;
                string single = null;

                // Blank cells extend an open run (their color is irrelevant), so a line is a few runs, not one per word.
                if (start >= 0 && x < _count && x == start + s_run.Length && IsBlank(x))
                {
                    s_run.Append(' ');
                    continue;
                }

                if (x < _count && !IsWidePlaceholder(x) && !IsInvisible(x))
                {
                    var cell = _cells[x];
                    int code = cell.Code;
                    if (code > 32 && !BoxDrawing.IsProcedural(code))
                    {
                        int flags = cell.Attribute >> 18;
                        fg = GetForeground(x, palette);
                        cellFont = fonts.Select((flags & FlagBold) != 0, (flags & FlagItalic) != 0);
                        // '<' is drawn alone so it can never start a rich text tag.
                        if (code < 127 && code != '<') plain = true;
                        else single = ToText(code);
                    }
                }

                bool continues = plain && start >= 0 && font == cellFont && SameColor(fg, color) && x == start + s_run.Length;
                if (start >= 0 && !continues)
                {
                    mgc.DrawText(s_run.ToString(), new Vector2(start * cw, 0) + snap, drawSize, color, font);
                    start = -1;
                    s_run.Clear();
                }
                if (plain)
                {
                    if (start < 0)
                    {
                        start = x;
                        color = fg;
                        font = cellFont;
                    }
                    s_run.Append((char)_cells[x].Code);
                }
                else if (single != null)
                {
                    mgc.DrawText(single, new Vector2(x * cw, 0) + snap, drawSize, fg, cellFont);
                }
            }
        }

        private bool TryGetBackground(int x, Color32[] palette, out Color32 color)
        {
            if (x >= _overlay.SelectionStart && x <= _overlay.SelectionEnd && _overlay.SelectionStart >= 0)
            {
                color = TerminalPalette.SelectionBackground;
                return true;
            }
            if (IsFilledCursor(x))
            {
                color = TerminalPalette.CursorBackground;
                return true;
            }

            int attr = _cells[x].Attribute;
            int bg = attr & 0x1ff;
            int fg = (attr >> 9) & 0x1ff;
            if (((attr >> 18) & FlagInverse) != 0)
            {
                color = fg < 256 ? palette[fg] : TerminalPalette.DefaultForeground;
                return true;
            }
            if (bg < 256)
            {
                color = palette[bg];
                return true;
            }
            color = default;
            return false;
        }

        private Color32 GetForeground(int x, Color32[] palette)
        {
            if (x >= _overlay.SelectionStart && x <= _overlay.SelectionEnd && _overlay.SelectionStart >= 0)
                return TerminalPalette.SelectionForeground;
            if (IsFilledCursor(x)) return TerminalPalette.CursorForeground;

            int attr = _cells[x].Attribute;
            int bg = attr & 0x1ff;
            int fg = (attr >> 9) & 0x1ff;
            int flags = attr >> 18;

            Color32 fgColor = fg < 256 ? palette[fg] : TerminalPalette.DefaultForeground;
            Color32 bgColor = bg < 256 ? palette[bg] : TerminalPalette.DefaultBackground;
            if ((flags & FlagInverse) != 0)
            {
                var t = fgColor;
                fgColor = bg < 256 ? bgColor : TerminalPalette.DefaultBackground;
                bgColor = t;
            }
            if ((flags & FlagDim) != 0) fgColor = Color32.Lerp(fgColor, bgColor, 0.4f);
            return fgColor;
        }

        private bool IsFilledCursor(int x)
        {
            if (!_overlay.CursorFilled || _overlay.CursorColumn < 0) return false;
            return x >= _overlay.CursorColumn && x < _overlay.CursorColumn + CellSpan(_overlay.CursorColumn);
        }

        private int CellSpan(int x) => x < _count && _cells[x].Width == 2 ? 2 : 1;

        private bool IsWidePlaceholder(int x) => _cells[x].Width == 0 && x > 0 && _cells[x - 1].Width == 2;

        private bool IsInvisible(int x) => ((_cells[x].Attribute >> 18) & FlagInvisible) != 0;

        private bool IsBlank(int x) => (_cells[x].Code == 0 || _cells[x].Code == ' ') && _cells[x].Width == 1;

        private static bool SameColor(Color32 a, Color32 b) => a.r == b.r && a.g == b.g && a.b == b.b && a.a == b.a;

        private static string ToText(int code)
        {
            if (code > 0x10FFFF || (code >= 0xD800 && code <= 0xDFFF)) return "�";
            return char.ConvertFromUtf32(code);
        }

        private static void WriteQuads(MeshGenerationContext mgc, List<ColoredRect> rects)
        {
            const int maxQuadsPerMesh = 16000; // 4 vertices each, below the 16-bit index limit
            for (int offset = 0; offset < rects.Count; offset += maxQuadsPerMesh)
            {
                int quads = Math.Min(maxQuadsPerMesh, rects.Count - offset);
                var mesh = mgc.Allocate(quads * 4, quads * 6);
                for (int i = 0; i < quads; i++)
                {
                    var r = rects[offset + i];
                    var rect = r.Rect;
                    mesh.SetNextVertex(new Vertex { position = new Vector3(rect.xMin, rect.yMin, Vertex.nearZ), tint = r.Color });
                    mesh.SetNextVertex(new Vertex { position = new Vector3(rect.xMax, rect.yMin, Vertex.nearZ), tint = r.Color });
                    mesh.SetNextVertex(new Vertex { position = new Vector3(rect.xMax, rect.yMax, Vertex.nearZ), tint = r.Color });
                    mesh.SetNextVertex(new Vertex { position = new Vector3(rect.xMin, rect.yMax, Vertex.nearZ), tint = r.Color });
                    ushort b = (ushort)(i * 4);
                    mesh.SetNextIndex(b);
                    mesh.SetNextIndex((ushort)(b + 1));
                    mesh.SetNextIndex((ushort)(b + 2));
                    mesh.SetNextIndex(b);
                    mesh.SetNextIndex((ushort)(b + 2));
                    mesh.SetNextIndex((ushort)(b + 3));
                }
            }
        }
    }
}
