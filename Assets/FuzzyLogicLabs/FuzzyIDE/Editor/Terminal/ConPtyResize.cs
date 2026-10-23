using System;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.Terminal
{
    /// <summary>
    /// Resizes an XtermSharp terminal for a ConPTY session.
    /// When the width shrinks XtermSharp keeps the cells past the new right edge. Erase operations only
    /// clear up to the current width, so those cells survive while the application redraws, and reappear as
    /// stale text when the terminal is widened again (seen with Claude Code's status line and separators).
    /// After narrowing, every line is cut to the new width. ConPTY repaints the viewport after a resize, so
    /// nothing visible is lost.
    /// XtermSharp also leaves the saved cursor (DECSC / ?1049h) at its old screen row while the buffer
    /// content moves between scrollback and screen. Resizing while a full-screen app runs then puts the
    /// shell prompt on the wrong row when the app exits, and ConPTY (which asks for the cursor position)
    /// continues from there over existing text. The saved cursor is kept on the same buffer line instead.
    /// </summary>
    internal static class ConPtyResize
    {
        /// <summary>Call with the terminal locked.</summary>
        public static void Resize(XtermSharp.Terminal term, int cols, int rows)
        {
            int previousCols = term.Cols;
            var normal = term.Buffers.Normal;
            var alt = term.Buffers.Alt;
            int normalSavedLine = normal != null ? normal.YBase + normal.SavedY : 0;
            int altSavedLine = alt != null ? alt.YBase + alt.SavedY : 0;

            bool altActive = term.Buffers.IsAlternateBuffer;
            term.Resize(cols, rows);

            if (altActive && normal != null)
            {
                // Nothing moves the normal buffer's cursor while the alternate screen is up, so it still marks
                // the position saved by ?1049h, and unlike SavedY it follows the resize (including reflow).
                normal.SavedY = Math.Max(0, Math.Min(rows - 1, normal.Y));
                normal.SavedX = Math.Max(0, Math.Min(cols - 1, normal.X));
            }
            else
            {
                RestoreSavedCursor(normal, normalSavedLine, cols, rows);
            }
            RestoreSavedCursor(alt, altSavedLine, cols, rows);

            if (cols >= previousCols) return;

            TrimLines(normal, cols);
            TrimLines(alt, cols);
        }

        private static void RestoreSavedCursor(XtermSharp.Buffer buffer, int savedLine, int cols, int rows)
        {
            if (buffer == null) return;
            buffer.SavedY = Math.Max(0, Math.Min(rows - 1, savedLine - buffer.YBase));
            buffer.SavedX = Math.Max(0, Math.Min(cols - 1, buffer.SavedX));
        }

        private static void TrimLines(XtermSharp.Buffer buffer, int cols)
        {
            if (buffer == null) return;
            var lines = buffer.Lines;
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (line != null && line.Length > cols) line.Resize(cols, XtermSharp.CharData.Null);
            }
        }
    }
}
