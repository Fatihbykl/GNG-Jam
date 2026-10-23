using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.Notes.Core
{
    public enum NoteRefKind
    {
        /// <summary>[[PlayerMotor.cs]], [[PlayerMotor.cs:42]], [[PlayerMotor.cs#Awake]], [[Assets/Scripts/PlayerMotor.cs:12]]</summary>
        Code,
        /// <summary>[[asset:GUID]], [[asset:Assets/Prefabs/Player.prefab]], [[Assets/Prefabs/Player.prefab]]</summary>
        Asset,
        /// <summary>[[go:GlobalObjectId_V1-2-…]] — a scene object.</summary>
        GameObject,
        /// <summary>[[commit:abc1234]]</summary>
        Commit
    }

    /// <summary>A link from a note to something outside the vault (code, asset, scene object, commit).</summary>
    public sealed class NoteRefLink
    {
        public NoteRefKind Kind;
        /// <summary>Link text without the alias ("PlayerMotor.cs#Awake").</summary>
        public string Raw;
        /// <summary>File name/path, asset GUID/path, GlobalObjectId string or commit hash.</summary>
        public string Target;
        /// <summary>1-based line for "File.cs:42" links, 0 otherwise.</summary>
        public int Line;
        /// <summary>Symbol for "File.cs#Awake" / "File.cs#PlayerMotor.Awake" links, null otherwise.</summary>
        public string Symbol;
        public string Alias = "";
        /// <summary>0-based body line of the note holding the link.</summary>
        public int NoteLine;
        /// <summary>The note line, trimmed, for tooltips and lists.</summary>
        public string Context = "";

        /// <summary>Index key used to look the link up from the referenced object.</summary>
        public string Key
        {
            get
            {
                switch (Kind)
                {
                    case NoteRefKind.Code: return NoteRefLinks.CodeKey(Target);
                    case NoteRefKind.Asset: return NoteRefLinks.IsGuid(Target) ? NoteRefLinks.GuidKey(Target) : NoteRefLinks.PathKey(Target);
                    case NoteRefKind.GameObject: return NoteRefLinks.GameObjectKey(Target);
                    default: return NoteRefLinks.CommitKey(Target);
                }
            }
        }
    }

    /// <summary>Classifies and extracts code / asset / scene-object / commit links. Pure text logic.</summary>
    public static class NoteRefLinks
    {
        public const string AssetPrefix = "asset:";
        public const string GameObjectPrefix = "go:";
        public const string CommitPrefix = "commit:";

        private static readonly Regex CodeLinkRx = new Regex(
            @"^([a-zA-Z0-9_\-\.\/\\ ]+\.(?:cs|shader|hlsl|cginc|compute|asmdef|json|txt|uss|uxml|xml|yaml|md))(?::(\d+)|#([a-zA-Z_][a-zA-Z0-9_\.]*))?$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex GuidRx = new Regex(@"^[a-fA-F0-9]{32}$", RegexOptions.Compiled);
        private static readonly Regex CommitRx = new Regex(@"^[a-fA-F0-9]{4,40}$", RegexOptions.Compiled);
        private static readonly Regex WikiLinkRx = new Regex(@"!?\[\[([^\[\]\r\n]+?)\]\]", RegexOptions.Compiled);
        private static readonly Regex InlineCodeRx = new Regex(@"(`+)[\s\S]*?\1", RegexOptions.Compiled);

        public static string CodeKey(string fileOrPath) => "code:" + Path.GetFileName((fileOrPath ?? "").Replace('\\', '/')).ToLowerInvariant();
        public static string GuidKey(string guid) => "guid:" + (guid ?? "").ToLowerInvariant();
        public static string PathKey(string assetPath) => "path:" + (assetPath ?? "").Replace('\\', '/').Trim().ToLowerInvariant();
        public static string GameObjectKey(string globalObjectId) => "go:" + (globalObjectId ?? "").Trim();
        public static string CommitKey(string hash) => "commit:" + (hash ?? "").ToLowerInvariant();

        public static bool IsGuid(string s) => !string.IsNullOrEmpty(s) && GuidRx.IsMatch(s);

        /// <summary>Parses a link's inner text ("PlayerMotor.cs#Awake|motor") into a reference, or null for note/URL links.</summary>
        public static NoteRefLink Parse(string inner)
        {
            if (string.IsNullOrWhiteSpace(inner)) return null;
            string text = inner.Trim();
            string alias = "";
            int pipe = text.IndexOf('|');
            if (pipe >= 0)
            {
                alias = text.Substring(pipe + 1).Trim();
                text = text.Substring(0, pipe).Trim();
            }
            if (text.Length == 0) return null;

            if (text.StartsWith(GameObjectPrefix, StringComparison.OrdinalIgnoreCase))
            {
                string id = text.Substring(GameObjectPrefix.Length).Trim();
                return id.Length == 0 ? null : new NoteRefLink { Kind = NoteRefKind.GameObject, Raw = text, Target = id, Alias = alias };
            }

            if (text.StartsWith(CommitPrefix, StringComparison.OrdinalIgnoreCase))
            {
                string hash = text.Substring(CommitPrefix.Length).Trim();
                return CommitRx.IsMatch(hash) ? new NoteRefLink { Kind = NoteRefKind.Commit, Raw = text, Target = hash, Alias = alias } : null;
            }

            if (text.StartsWith(AssetPrefix, StringComparison.OrdinalIgnoreCase))
            {
                string target = text.Substring(AssetPrefix.Length).Trim().Replace('\\', '/');
                return target.Length == 0 ? null : new NoteRefLink { Kind = NoteRefKind.Asset, Raw = text, Target = target, Alias = alias };
            }

            var m = CodeLinkRx.Match(text);
            if (m.Success)
            {
                // A note called "Readme.md" is a note link, not a code link.
                string file = m.Groups[1].Value.Replace('\\', '/');
                bool projectPath = IsProjectPath(file);
                if (file.EndsWith(".md", StringComparison.OrdinalIgnoreCase) && !projectPath) return null;

                var link = new NoteRefLink { Kind = NoteRefKind.Code, Raw = text, Target = file, Alias = alias };
                if (m.Groups[2].Success && int.TryParse(m.Groups[2].Value, out int line)) link.Line = Math.Max(1, line);
                if (m.Groups[3].Success) link.Symbol = m.Groups[3].Value;
                return link;
            }

            if (IsProjectPath(text))
                return new NoteRefLink { Kind = NoteRefKind.Asset, Raw = text, Target = text.Replace('\\', '/'), Alias = alias };

            return null;
        }

        public static bool IsProjectPath(string path) =>
            path != null && (path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) || path.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase));

        /// <summary>All code/asset/scene-object/commit links outside code blocks, with their body line.</summary>
        public static List<NoteRefLink> Extract(string body)
        {
            var result = new List<NoteRefLink>();
            MarkdownScan.ForEachProseLine(body, (line, index) =>
            {
                if (line.IndexOf("[[", StringComparison.Ordinal) < 0) return;
                string masked = line.IndexOf('`') < 0 ? line : InlineCodeRx.Replace(line, x => new string(' ', x.Length));
                foreach (Match m in WikiLinkRx.Matches(masked))
                {
                    var link = Parse(m.Groups[1].Value);
                    if (link == null) continue;
                    link.NoteLine = index;
                    link.Context = CleanContext(line);
                    result.Add(link);
                }
            });
            return result;
        }

        /// <summary>
        /// True when a code link's file part ("PlayerMotor.cs" or "Scripts/PlayerMotor.cs") can refer to
        /// <paramref name="assetPath"/> ("Assets/Scripts/PlayerMotor.cs").
        /// </summary>
        public static bool CodeTargetMatches(string target, string assetPath)
        {
            if (string.IsNullOrEmpty(target) || string.IsNullOrEmpty(assetPath)) return false;
            string t = target.Replace('\\', '/').TrimStart('/');
            string p = assetPath.Replace('\\', '/');
            if (p.Equals(t, StringComparison.OrdinalIgnoreCase)) return true;
            return p.EndsWith("/" + t, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Builds the link text for a code location: "File.cs#Symbol" when a symbol is known, else "File.cs:line".</summary>
        public static string FormatCodeLink(string fileName, string symbol, int line1)
        {
            if (!string.IsNullOrEmpty(symbol)) return fileName + "#" + symbol;
            return line1 > 0 ? fileName + ":" + line1 : fileName;
        }

        private static string CleanContext(string line)
        {
            string s = line.Trim();
            int i = 0;
            while (i < s.Length && (s[i] == '#' || s[i] == '>' || s[i] == '-' || s[i] == '*' || s[i] == ' ')) i++;
            s = s.Substring(i);
            if (s.StartsWith("[ ] ") || s.StartsWith("[x] ") || s.StartsWith("[X] ") || s.StartsWith("[/] ")) s = s.Substring(4);
            return s.Length > 160 ? s.Substring(0, 157) + "…" : s;
        }
    }
}
