using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.Terminal.Rendering
{
    internal struct ColoredRect
    {
        public Rect Rect;
        public Color32 Color;

        public ColoredRect(Rect rect, Color32 color)
        {
            Rect = rect;
            Color = color;
        }
    }

    /// <summary>
    /// Draws box-drawing (U+2500-257F), block elements (U+2580-259F) and braille (U+2800-28FF) as
    /// geometry instead of font glyphs, like Windows Terminal and iTerm do. Glyphs from a font rarely
    /// fill the cell exactly, which leaves gaps between rows of borders and in block-art logos.
    /// </summary>
    internal static class BoxDrawing
    {
        // Arm weights per code point from U+2500, as "LRUD": 0 none, 1 light, 2 heavy, 3 double.
        // Dashed variants are drawn solid; diagonals (U+2571-2573) are left to the font.
        private static readonly string[] Lines =
        {
            "1100", "2200", "0011", "0022", "1100", "2200", "0011", "0022", // 2500
            "1100", "2200", "0011", "0022", "0101", "0201", "0102", "0202", // 2508
            "1001", "2001", "1002", "2002", "0110", "0210", "0120", "0220", // 2510
            "1010", "2010", "1020", "2020", "0111", "0211", "0121", "0112", // 2518
            "0122", "0221", "0212", "0222", "1011", "2011", "1021", "1012", // 2520
            "1022", "2021", "2012", "2022", "1101", "2101", "1201", "2201", // 2528
            "1102", "2102", "1202", "2202", "1110", "2110", "1210", "2210", // 2530
            "1120", "2120", "1220", "2220", "1111", "2111", "1211", "2211", // 2538
            "1121", "1112", "1122", "2121", "1221", "2112", "1212", "2221", // 2540
            "2212", "2122", "1222", "2222", "1100", "2200", "0011", "0022", // 2548
            "3300", "0033", "0301", "0103", "0303", "3001", "1003", "3003", // 2550
            "0310", "0130", "0330", "3010", "1030", "3030", "0311", "0133", // 2558
            "0333", "3011", "1033", "3033", "3301", "1103", "3303", "3310", // 2560
            "1130", "3330", "3311", "1133", "3333", "0101", "1001", "1010", // 2568
            "0110", null,   null,   null,   "1000", "0010", "0100", "0001", // 2570
            "2000", "0020", "0200", "0002", "1200", "0012", "2100", "0021", // 2578
        };

        public static bool IsProcedural(int code)
        {
            if (code >= 0x2500 && code <= 0x257F) return Lines[code - 0x2500] != null;
            return (code >= 0x2580 && code <= 0x259F) || (code >= 0x2800 && code <= 0x28FF);
        }

        public static bool IsRoundedCorner(int code) => code >= 0x256D && code <= 0x2570;

        /// <summary>Light line thickness for a font size, in pixels.</summary>
        public static float LineWidth(int fontSize) => Mathf.Max(1f, Mathf.Round(fontSize / 12f));

        public static void AppendRects(int code, Rect cell, Color32 color, float lineWidth, List<ColoredRect> output)
        {
            if (code >= 0x2500 && code <= 0x257F)
            {
                if (!IsRoundedCorner(code)) AppendLines(Lines[code - 0x2500], cell, color, lineWidth, output);
            }
            else if (code >= 0x2580 && code <= 0x259F)
            {
                AppendBlock(code, cell, color, output);
            }
            else if (code >= 0x2800 && code <= 0x28FF)
            {
                AppendBraille(code - 0x2800, cell, color, output);
            }
        }

        /// <summary>Rounded corners (U+256D-2570) are arcs, drawn with the painter after the quads.</summary>
        public static void DrawRoundedCorner(Painter2D painter, int code, Rect cell, Color32 color, float lineWidth)
        {
            float mx = Mathf.Round(cell.x + cell.width / 2f - lineWidth / 2f) + lineWidth / 2f;
            float my = Mathf.Round(cell.y + cell.height / 2f - lineWidth / 2f) + lineWidth / 2f;
            float radius = Mathf.Max(1f, Mathf.Min(cell.width, cell.height) / 2f);
            bool right = code == 0x256D || code == 0x2570;
            bool down = code == 0x256D || code == 0x256E;

            var horizontalEnd = new Vector2(right ? cell.xMax : cell.xMin, my);
            var verticalEnd = new Vector2(mx, down ? cell.yMax : cell.yMin);

            painter.strokeColor = color;
            painter.lineWidth = lineWidth;
            painter.lineCap = LineCap.Butt;
            painter.BeginPath();
            painter.MoveTo(horizontalEnd);
            painter.ArcTo(new Vector2(mx, my), verticalEnd, radius);
            painter.LineTo(verticalEnd);
            painter.Stroke();
        }

        private static float Thickness(int weight, float t) => weight == 1 ? t : weight == 2 ? t * 2f : weight == 3 ? t * 3f : 0f;

        private static void AppendLines(string arms, Rect cell, Color32 color, float t, List<ColoredRect> output)
        {
            int left = arms[0] - '0', right = arms[1] - '0', up = arms[2] - '0', down = arms[3] - '0';
            float mx = cell.x + cell.width / 2f;
            float my = cell.y + cell.height / 2f;

            // Arms reach past the center by half of the crossing arm so joints are filled.
            float verticalSpan = Mathf.Max(Thickness(up, t), Thickness(down, t));
            float horizontalSpan = Mathf.Max(Thickness(left, t), Thickness(right, t));

            if (left > 0)
            {
                float ext = verticalSpan > 0 ? verticalSpan / 2f : Thickness(left, t) / 2f;
                AppendHorizontal(left, cell.xMin, mx + ext, my, t, color, output);
            }
            if (right > 0)
            {
                float ext = verticalSpan > 0 ? verticalSpan / 2f : Thickness(right, t) / 2f;
                AppendHorizontal(right, mx - ext, cell.xMax, my, t, color, output);
            }
            if (up > 0)
            {
                float ext = horizontalSpan > 0 ? horizontalSpan / 2f : Thickness(up, t) / 2f;
                AppendVertical(up, cell.yMin, my + ext, mx, t, color, output);
            }
            if (down > 0)
            {
                float ext = horizontalSpan > 0 ? horizontalSpan / 2f : Thickness(down, t) / 2f;
                AppendVertical(down, my - ext, cell.yMax, mx, t, color, output);
            }
        }

        private static void AppendHorizontal(int weight, float x0, float x1, float my, float t, Color32 color, List<ColoredRect> output)
        {
            if (weight == 3)
            {
                float top = Mathf.Round(my - t * 1.5f);
                output.Add(new ColoredRect(Rect.MinMaxRect(x0, top, x1, top + t), color));
                output.Add(new ColoredRect(Rect.MinMaxRect(x0, top + 2f * t, x1, top + 3f * t), color));
                return;
            }
            float w = Thickness(weight, t);
            float y = Mathf.Round(my - w / 2f);
            output.Add(new ColoredRect(Rect.MinMaxRect(x0, y, x1, y + w), color));
        }

        private static void AppendVertical(int weight, float y0, float y1, float mx, float t, Color32 color, List<ColoredRect> output)
        {
            if (weight == 3)
            {
                float leftEdge = Mathf.Round(mx - t * 1.5f);
                output.Add(new ColoredRect(Rect.MinMaxRect(leftEdge, y0, leftEdge + t, y1), color));
                output.Add(new ColoredRect(Rect.MinMaxRect(leftEdge + 2f * t, y0, leftEdge + 3f * t, y1), color));
                return;
            }
            float w = Thickness(weight, t);
            float x = Mathf.Round(mx - w / 2f);
            output.Add(new ColoredRect(Rect.MinMaxRect(x, y0, x + w, y1), color));
        }

        private static void AppendBlock(int code, Rect cell, Color32 color, List<ColoredRect> output)
        {
            float x0 = cell.xMin, x1 = cell.xMax, y0 = cell.yMin, y1 = cell.yMax;
            float w = cell.width, h = cell.height;

            if (code == 0x2580) { Add(x0, y0, x1, y0 + Mathf.Round(h / 2f)); return; }          // ▀
            if (code >= 0x2581 && code <= 0x2588)                                                  // ▁..█ lower n/8
            {
                float fraction = (code - 0x2580) / 8f;
                Add(x0, y1 - Mathf.Round(h * fraction), x1, y1);
                return;
            }
            if (code >= 0x2589 && code <= 0x258F)                                                  // ▉..▏ left n/8
            {
                float fraction = (0x2590 - code) / 8f;
                Add(x0, y0, x0 + Mathf.Max(1f, Mathf.Round(w * fraction)), y1);
                return;
            }
            if (code == 0x2590) { Add(x0 + Mathf.Round(w / 2f), y0, x1, y1); return; }          // ▐
            if (code >= 0x2591 && code <= 0x2593)                                                  // ░▒▓
            {
                byte alpha = (byte)(color.a * (code - 0x2590) / 4);
                output.Add(new ColoredRect(Rect.MinMaxRect(x0, y0, x1, y1), new Color32(color.r, color.g, color.b, alpha)));
                return;
            }
            if (code == 0x2594) { Add(x0, y0, x1, y0 + Mathf.Max(1f, Mathf.Round(h / 8f))); return; } // ▔
            if (code == 0x2595) { Add(x1 - Mathf.Max(1f, Mathf.Round(w / 8f)), y0, x1, y1); return; } // ▕

            // Quadrants U+2596-259F; bits: 1 upper-left, 2 upper-right, 4 lower-left, 8 lower-right.
            int[] quadrants = { 4, 8, 1, 13, 9, 7, 11, 2, 6, 14 };
            int mask = quadrants[code - 0x2596];
            float xm = x0 + Mathf.Round(w / 2f), ym = y0 + Mathf.Round(h / 2f);
            if ((mask & 1) != 0) Add(x0, y0, xm, ym);
            if ((mask & 2) != 0) Add(xm, y0, x1, ym);
            if ((mask & 4) != 0) Add(x0, ym, xm, y1);
            if ((mask & 8) != 0) Add(xm, ym, x1, y1);

            void Add(float ax0, float ay0, float ax1, float ay1)
            {
                output.Add(new ColoredRect(Rect.MinMaxRect(ax0, ay0, ax1, ay1), color));
            }
        }

        private static void AppendBraille(int dots, Rect cell, Color32 color, List<ColoredRect> output)
        {
            float size = Mathf.Max(1f, Mathf.Round(Mathf.Min(cell.width * 0.28f, cell.height * 0.14f)));
            float[] columns = { 0.3f, 0.7f };
            float[] rows = { 0.15f, 0.38f, 0.62f, 0.85f };
            // Dot numbering: bits 0-2 left column rows 0-2, bits 3-5 right column rows 0-2, bit 6 left row 3, bit 7 right row 3.
            for (int bit = 0; bit < 8; bit++)
            {
                if ((dots & (1 << bit)) == 0) continue;
                int column = bit < 3 || bit == 6 ? 0 : 1;
                int row = bit == 6 || bit == 7 ? 3 : bit % 3;
                float cx = Mathf.Round(cell.x + cell.width * columns[column] - size / 2f);
                float cy = Mathf.Round(cell.y + cell.height * rows[row] - size / 2f);
                output.Add(new ColoredRect(new Rect(cx, cy, size, size), color));
            }
        }
    }
}
