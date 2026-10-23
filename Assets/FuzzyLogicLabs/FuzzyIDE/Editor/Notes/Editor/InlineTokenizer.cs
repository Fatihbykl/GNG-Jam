using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.Notes.LiveEditor
{
    [Flags]
    public enum InlineStyle
    {
        None = 0,
        Bold = 1 << 0,
        Italic = 1 << 1,
        Strikethrough = 1 << 2,
        Code = 1 << 3,
        Link = 1 << 4,
        Tag = 1 << 5
    }

    /// <summary>
    /// A single styled segment of text within a line.
    /// Tracks both its raw character range and its display range in passive mode.
    /// </summary>
    public sealed class InlineSegment
    {
        public InlineStyle Style;
        public int RawStart;          // start index in raw line (inclusive)
        public int RawEnd;            // end index in raw line (exclusive)
        public int RawContentStart;   // start index of displayed text in raw line
        public int RawContentEnd;     // end index of displayed text in raw line
        public string DisplayText;    // text shown in passive mode
        public string LinkTarget;     // target if Style has Link (e.g. note name, file:line, URL)
        public int DisplayStart;      // start index in line's Display string
        public int DisplayEnd;        // end index in line's Display string
    }

    /// <summary>
    /// Parses inline markdown elements (bold, italic, strikethrough, code, wiki links, markdown links)
    /// into a continuous stream of InlineSegments covering the entire line body.
    /// </summary>
    public static class InlineTokenizer
    {
        private static readonly Regex MasterInlineRx = new Regex(
            @"(?<code>(?<!`)`(?<cval>[^`\r\n]+)`(?!`))" +
            @"|(?<wiki>\[\[(?<wtarget>[^\]|\r\n]+)(?:\|(?<walias>[^\]\r\n]+))?\]\])" +
            @"|(?<mdlink>\[(?<mlabel>[^\]\r\n]+)\]\((?<murl>[^)\r\n]+)\))" +
            @"|(?<tag>(?<![^\s])#(?=[\p{L}\p{N}_\-/]*[\p{L}_\-/])[\p{L}\p{N}_\-/]+)" +
            @"|(?<strike>~~(?<sval>[^~\r\n]+)~~)" +
            @"|(?<bolditalic>\*\*\*(?<bival>[^*\r\n]+)\*\*\*)" +
            @"|(?<bold>\*\*(?<bval1>[^*\r\n]+)\*\*|(?<![a-zA-Z0-9])__(?<bval2>[^_]+)__(?![a-zA-Z0-9]))" +
            @"|(?<italic>(?<!\*)\*(?<ival1>[^*\r\n]+)\*(?!\*)|(?<![a-zA-Z0-9])_(?<ival2>[^_]+)_(?![a-zA-Z0-9]))",
            RegexOptions.Compiled
        );

        /// <summary>
        /// Tokenizes the body of a markdown line into segments.
        /// </summary>
        /// <param name="body">Line text after any block prefix.</param>
        /// <param name="baseOffset">Offset of body in the full raw line.</param>
        public static List<InlineSegment> Tokenize(string body, int baseOffset = 0)
        {
            var segments = new List<InlineSegment>();
            if (string.IsNullOrEmpty(body))
            {
                segments.Add(new InlineSegment
                {
                    Style = InlineStyle.None,
                    RawStart = baseOffset,
                    RawEnd = baseOffset,
                    RawContentStart = baseOffset,
                    RawContentEnd = baseOffset,
                    DisplayText = ""
                });
                return segments;
            }

            int lastIdx = 0;
            var matches = MasterInlineRx.Matches(body);

            foreach (Match m in matches)
            {
                if (m.Index > lastIdx)
                {
                    // Plain text between previous match and this one
                    string plain = body.Substring(lastIdx, m.Index - lastIdx);
                    segments.Add(new InlineSegment
                    {
                        Style = InlineStyle.None,
                        RawStart = baseOffset + lastIdx,
                        RawEnd = baseOffset + m.Index,
                        RawContentStart = baseOffset + lastIdx,
                        RawContentEnd = baseOffset + m.Index,
                        DisplayText = plain
                    });
                }

                var seg = CreateSegmentFromMatch(m, baseOffset);
                segments.Add(seg);
                lastIdx = m.Index + m.Length;
            }

            if (lastIdx < body.Length)
            {
                string trailing = body.Substring(lastIdx);
                segments.Add(new InlineSegment
                {
                    Style = InlineStyle.None,
                    RawStart = baseOffset + lastIdx,
                    RawEnd = baseOffset + body.Length,
                    RawContentStart = baseOffset + lastIdx,
                    RawContentEnd = baseOffset + body.Length,
                    DisplayText = trailing
                });
            }

            return segments;
        }

        private static InlineSegment CreateSegmentFromMatch(Match m, int baseOffset)
        {
            int rawStart = baseOffset + m.Index;
            int rawEnd = baseOffset + m.Index + m.Length;

            if (m.Groups["code"].Success)
            {
                var val = m.Groups["cval"];
                return new InlineSegment
                {
                    Style = InlineStyle.Code,
                    RawStart = rawStart,
                    RawEnd = rawEnd,
                    RawContentStart = baseOffset + val.Index,
                    RawContentEnd = baseOffset + val.Index + val.Length,
                    DisplayText = val.Value
                };
            }

            if (m.Groups["wiki"].Success)
            {
                var target = m.Groups["wtarget"].Value.Trim();
                var aliasGrp = m.Groups["walias"];
                string display = aliasGrp.Success && !string.IsNullOrWhiteSpace(aliasGrp.Value)
                    ? aliasGrp.Value.Trim()
                    : target;

                int contentStart = aliasGrp.Success ? baseOffset + aliasGrp.Index : baseOffset + m.Groups["wtarget"].Index;
                int contentEnd = aliasGrp.Success ? baseOffset + aliasGrp.Index + aliasGrp.Length : baseOffset + m.Groups["wtarget"].Index + m.Groups["wtarget"].Length;

                return new InlineSegment
                {
                    Style = InlineStyle.Link,
                    RawStart = rawStart,
                    RawEnd = rawEnd,
                    RawContentStart = contentStart,
                    RawContentEnd = contentEnd,
                    DisplayText = display,
                    LinkTarget = target
                };
            }

            if (m.Groups["mdlink"].Success)
            {
                var label = m.Groups["mlabel"];
                var url = m.Groups["murl"].Value.Trim();
                return new InlineSegment
                {
                    Style = InlineStyle.Link,
                    RawStart = rawStart,
                    RawEnd = rawEnd,
                    RawContentStart = baseOffset + label.Index,
                    RawContentEnd = baseOffset + label.Index + label.Length,
                    DisplayText = label.Value,
                    LinkTarget = url
                };
            }

            if (m.Groups["tag"].Success)
            {
                return new InlineSegment
                {
                    Style = InlineStyle.Tag,
                    RawStart = rawStart,
                    RawEnd = rawEnd,
                    RawContentStart = rawStart,
                    RawContentEnd = rawEnd,
                    DisplayText = m.Value,
                    LinkTarget = m.Value.Substring(1)
                };
            }

            if (m.Groups["strike"].Success)
            {
                var val = m.Groups["sval"];
                return new InlineSegment
                {
                    Style = InlineStyle.Strikethrough,
                    RawStart = rawStart,
                    RawEnd = rawEnd,
                    RawContentStart = baseOffset + val.Index,
                    RawContentEnd = baseOffset + val.Index + val.Length,
                    DisplayText = val.Value
                };
            }

            if (m.Groups["bolditalic"].Success)
            {
                var val = m.Groups["bival"];
                return new InlineSegment
                {
                    Style = InlineStyle.Bold | InlineStyle.Italic,
                    RawStart = rawStart,
                    RawEnd = rawEnd,
                    RawContentStart = baseOffset + val.Index,
                    RawContentEnd = baseOffset + val.Index + val.Length,
                    DisplayText = val.Value
                };
            }

            if (m.Groups["bold"].Success)
            {
                var val = m.Groups["bval1"].Success ? m.Groups["bval1"] : m.Groups["bval2"];
                return new InlineSegment
                {
                    Style = InlineStyle.Bold,
                    RawStart = rawStart,
                    RawEnd = rawEnd,
                    RawContentStart = baseOffset + val.Index,
                    RawContentEnd = baseOffset + val.Index + val.Length,
                    DisplayText = val.Value
                };
            }

            if (m.Groups["italic"].Success)
            {
                var val = m.Groups["ival1"].Success ? m.Groups["ival1"] : m.Groups["ival2"];
                return new InlineSegment
                {
                    Style = InlineStyle.Italic,
                    RawStart = rawStart,
                    RawEnd = rawEnd,
                    RawContentStart = baseOffset + val.Index,
                    RawContentEnd = baseOffset + val.Index + val.Length,
                    DisplayText = val.Value
                };
            }

            // Fallback
            return new InlineSegment
            {
                Style = InlineStyle.None,
                RawStart = rawStart,
                RawEnd = rawEnd,
                RawContentStart = rawStart,
                RawContentEnd = rawEnd,
                DisplayText = m.Value
            };
        }
    }
}
