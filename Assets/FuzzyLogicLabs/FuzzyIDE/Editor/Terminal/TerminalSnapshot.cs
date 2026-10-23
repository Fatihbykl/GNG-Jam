using System;
using System.Globalization;
using System.IO;
using System.Text;
using XtermSharp;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.Terminal
{
    /// <summary>
    /// Serializes the visible state of an XtermSharp terminal (scrollback, screen, cursor, a few modes)
    /// as a self-contained VT stream that rebuilds the same grid when fed into a fresh terminal of the
    /// same size. Used across domain reloads instead of replaying the raw PTY log, which replays TUI
    /// cursor choreography at the wrong size and from the middle of escape sequences.
    /// </summary>
    internal static class TerminalSnapshot
    {
        private const string Header = "FTSNAP1";

        // Attribute layout used by XtermSharp: bg = bits 0..8, fg = bits 9..17, flags from bit 18.
        private const int FlagBold = 1, FlagUnderline = 2, FlagBlink = 4, FlagInverse = 8,
                          FlagInvisible = 16, FlagDim = 32, FlagItalic = 64, FlagStrike = 128;

        public static string Capture(XtermSharp.Terminal term)
        {
            var sb = new StringBuilder(64 * 1024);
            sb.Append(Header).Append(' ')
              .Append(term.Cols.ToString(CultureInfo.InvariantCulture)).Append(' ')
              .Append(term.Rows.ToString(CultureInfo.InvariantCulture)).Append('\n');

            var buffers = term.Buffers;
            AppendBuffer(sb, buffers.Normal, term.Cols, term.Rows);

            if (buffers.IsAlternateBuffer)
            {
                // 1049h keeps the cursor where it was; the alt grid must be written from the top.
                sb.Append("\x1b[?1049h\x1b[H");
                AppendBuffer(sb, buffers.Alt, term.Cols, term.Rows);
            }

            if (term.ApplicationCursor) sb.Append("\x1b[?1h");
            if (term.BracketedPasteMode) sb.Append("\x1b[?2004h");
            if (term.CursorHidden) sb.Append("\x1b[?25l");
            return sb.ToString();
        }

        /// <summary>Parses a snapshot produced by <see cref="Capture"/>. Returns false when the text is not a snapshot.</summary>
        public static bool TryParse(string text, out int cols, out int rows, out string payload)
        {
            cols = rows = 0;
            payload = null;
            if (string.IsNullOrEmpty(text) || !text.StartsWith(Header, StringComparison.Ordinal)) return false;
            int nl = text.IndexOf('\n');
            if (nl < 0) return false;
            var parts = text.Substring(0, nl).Split(' ');
            if (parts.Length != 3 ||
                !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out cols) ||
                !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out rows) ||
                cols <= 0 || rows <= 0)
            {
                return false;
            }
            payload = text.Substring(nl + 1);
            return true;
        }

        /// <summary>Resizes <paramref name="term"/> to the snapshot size and feeds the snapshot into it.</summary>
        public static bool Restore(XtermSharp.Terminal term, string text)
        {
            if (!TryParse(text, out int cols, out int rows, out string payload)) return false;
            if (term.Cols != cols || term.Rows != rows) term.Resize(cols, rows);
            term.Feed(Encoding.UTF8.GetBytes(payload));
            return true;
        }

        public static void Save(string path, XtermSharp.Terminal term)
        {
            File.WriteAllText(path, Capture(term), new UTF8Encoding(false));
        }

        private static void AppendBuffer(StringBuilder sb, XtermSharp.Buffer buffer, int cols, int rows)
        {
            var lines = buffer.Lines;
            int count = lines.Length;
            int lastAttr = CharData.DefaultAttr;
            sb.Append("\x1b[0m");

            for (int i = 0; i < count; i++)
            {
                if (i > 0) sb.Append("\r\n");
                AppendLine(sb, lines[i], cols, ref lastAttr);
            }

            if (lastAttr != CharData.DefaultAttr) sb.Append("\x1b[0m");

            // Cursor is relative to the screen, which is the last `rows` lines fed above.
            int cursorRow = Math.Max(0, Math.Min(rows - 1, buffer.Y));
            int cursorCol = Math.Max(0, buffer.X);
            sb.Append("\x1b[").Append(cursorRow + 1).Append(';').Append(cursorCol + 1).Append('H');
        }

        private static void AppendLine(StringBuilder sb, BufferLine line, int cols, ref int lastAttr)
        {
            // Cells past the width would wrap onto an extra line when the snapshot is fed back.
            int length = Math.Min(line.Length, cols);
            int end = 0;
            for (int x = length - 1; x >= 0; x--)
            {
                var cell = line[x];
                int bg = cell.Attribute & 0x1ff;
                if ((cell.Code != 0 && cell.Code != ' ') || bg < 256)
                {
                    end = x + 1;
                    break;
                }
            }

            for (int x = 0; x < end; x++)
            {
                var cell = line[x];
                // Second half of a wide character: the glyph before it already advanced two columns.
                if (cell.Width == 0 && x > 0 && line[x - 1].Width == 2) continue;

                if (cell.Attribute != lastAttr)
                {
                    AppendSgr(sb, cell.Attribute);
                    lastAttr = cell.Attribute;
                }

                int code = cell.Code;
                if (code <= 0 || code == 0x7f || (code < 32)) sb.Append(' ');
                else if (code > 0x10FFFF || (code >= 0xD800 && code <= 0xDFFF)) sb.Append('?');
                else sb.Append(char.ConvertFromUtf32(code));
            }
        }

        private static void AppendSgr(StringBuilder sb, int attr)
        {
            int bg = attr & 0x1ff;
            int fg = (attr >> 9) & 0x1ff;
            int flags = attr >> 18;

            sb.Append("\x1b[0");
            if ((flags & FlagBold) != 0) sb.Append(";1");
            if ((flags & FlagDim) != 0) sb.Append(";2");
            if ((flags & FlagItalic) != 0) sb.Append(";3");
            if ((flags & FlagUnderline) != 0) sb.Append(";4");
            if ((flags & FlagBlink) != 0) sb.Append(";5");
            if ((flags & FlagInverse) != 0) sb.Append(";7");
            if ((flags & FlagInvisible) != 0) sb.Append(";8");
            if ((flags & FlagStrike) != 0) sb.Append(";9");
            if (fg < 256) sb.Append(";38;5;").Append(fg);
            if (bg < 256) sb.Append(";48;5;").Append(bg);
            sb.Append('m');
        }
    }
}
