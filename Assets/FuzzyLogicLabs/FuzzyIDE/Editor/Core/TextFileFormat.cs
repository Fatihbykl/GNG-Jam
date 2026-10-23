using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.Core
{
    /// <summary>
    /// Line-ending conventions of a text file, so saving an edited buffer (kept as a list of lines) writes the file back
    /// the way it was: same newline sequence and the same trailing newline, instead of normalising every file.
    /// </summary>
    public struct TextFileFormat
    {
        public string NewLine;
        public bool EndsWithNewLine;
        /// <summary>The file starts with a UTF-8 byte order mark; saving writes it back.</summary>
        public bool HasBom;

        public Encoding Encoding => new UTF8Encoding(HasBom);

        public static TextFileFormat Default => new TextFileFormat { NewLine = Environment.NewLine, EndsWithNewLine = false };

        /// <summary>Format of <paramref name="content"/>: the first newline decides CRLF vs LF.</summary>
        public static TextFileFormat Detect(string content)
        {
            var format = Default;
            if (string.IsNullOrEmpty(content)) return format;

            int lf = content.IndexOf('\n');
            if (lf >= 0) format.NewLine = lf > 0 && content[lf - 1] == '\r' ? "\r\n" : "\n";
            format.EndsWithNewLine = content[content.Length - 1] == '\n';
            return format;
        }

        /// <summary>Format of the file on disk, or <see cref="Default"/> when it does not exist yet.</summary>
        public static TextFileFormat DetectFile(string path)
        {
            try
            {
                if (!File.Exists(path)) return Default;
                var bytes = File.ReadAllBytes(path);
                var format = Detect(File.ReadAllText(path));
                format.HasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
                return format;
            }
            catch
            {
                return Default;
            }
        }

        public string Join(IList<string> lines, bool forceFinalNewLine = false)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < lines.Count; i++)
            {
                if (i > 0) sb.Append(NewLine);
                sb.Append(lines[i]);
            }

            // The buffer comes from File.ReadAllLines, which strips exactly one trailing newline; put it back.
            if (EndsWithNewLine || forceFinalNewLine) sb.Append(NewLine);
            return sb.ToString();
        }
    }
}
