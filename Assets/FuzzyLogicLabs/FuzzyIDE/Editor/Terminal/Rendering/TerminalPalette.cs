using UnityEngine;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.Terminal.Rendering
{
    internal enum TerminalTheme
    {
        Campbell,     // Windows Terminal / conhost default
        OneHalfDark,
        Fuzzy         // the original FuzzyIDE theme
    }

    /// <summary>Colors used by the terminal grid: the xterm 256-color table plus UI colors.</summary>
    internal static class TerminalPalette
    {
        public static TerminalTheme Theme { get; private set; } = TerminalTheme.Campbell;
        public static Color32 DefaultForeground { get; private set; }
        public static Color32 DefaultBackground { get; private set; }
        public static Color32 SelectionBackground { get; private set; }
        public static Color32 SelectionForeground { get; private set; }
        public static Color32 CursorBackground { get; private set; }
        public static Color32 CursorForeground { get; private set; }

        /// <summary>256-color table; 0-15 come from the theme, 16-255 are the standard xterm cube and grays.</summary>
        public static Color32[] Colors { get; private set; } = new Color32[256];

        static TerminalPalette()
        {
            Apply(TerminalTheme.Campbell);
        }

        public static void Apply(TerminalTheme theme)
        {
            uint fg, bg, cursor;
            uint[] basic16;
            switch (theme)
            {
                case TerminalTheme.OneHalfDark:
                    fg = 0xdcdfe4; bg = 0x282c34; cursor = 0xa3b3cc;
                    basic16 = new uint[]
                    {
                        0x282c34, 0xe06c75, 0x98c379, 0xe5c07b, 0x61afef, 0xc678dd, 0x56b6c2, 0xdcdfe4,
                        0x5a6374, 0xe06c75, 0x98c379, 0xe5c07b, 0x61afef, 0xc678dd, 0x56b6c2, 0xdcdfe4
                    };
                    break;
                case TerminalTheme.Fuzzy:
                    fg = 0xcccccc; bg = 0x121218; cursor = 0x5294e2;
                    basic16 = new uint[]
                    {
                        0x181818, 0xe06c75, 0x98c379, 0xe5c07b, 0x61afef, 0xc678dd, 0x56b6c2, 0xd4d4d4,
                        0x7f848e, 0xf07178, 0xa6e22e, 0xffe66d, 0x82aaff, 0xc792ea, 0x7fdbca, 0xffffff
                    };
                    break;
                default:
                    theme = TerminalTheme.Campbell;
                    fg = 0xcccccc; bg = 0x0c0c0c; cursor = 0xcccccc;
                    basic16 = new uint[]
                    {
                        0x0c0c0c, 0xc50f1f, 0x13a10e, 0xc19c00, 0x0037da, 0x881798, 0x3a96dd, 0xcccccc,
                        0x767676, 0xe74856, 0x16c60c, 0xf9f1a5, 0x3b78ff, 0xb4009e, 0x61d6d6, 0xf2f2f2
                    };
                    break;
            }

            Theme = theme;
            DefaultForeground = FromRgb(fg);
            DefaultBackground = FromRgb(bg);
            CursorBackground = FromRgb(cursor);
            CursorForeground = DefaultBackground;
            SelectionBackground = new Color32(0x38, 0x70, 0xbb, 0xcc);
            SelectionForeground = new Color32(0xff, 0xff, 0xff, 0xff);
            Colors = Build(basic16);
        }

        private static Color32[] Build(uint[] basic16)
        {
            var colors = new Color32[256];
            for (int i = 0; i < 16; i++) colors[i] = FromRgb(basic16[i]);

            byte[] steps = { 0x00, 0x5f, 0x87, 0xaf, 0xd7, 0xff };
            int index = 16;
            for (int r = 0; r < 6; r++)
                for (int g = 0; g < 6; g++)
                    for (int b = 0; b < 6; b++)
                        colors[index++] = new Color32(steps[r], steps[g], steps[b], 0xff);

            for (int i = 0; i < 24; i++)
            {
                byte gray = (byte)(8 + i * 10);
                colors[232 + i] = new Color32(gray, gray, gray, 0xff);
            }
            return colors;
        }

        private static Color32 FromRgb(uint rgb)
        {
            return new Color32((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb, 0xff);
        }
    }
}
