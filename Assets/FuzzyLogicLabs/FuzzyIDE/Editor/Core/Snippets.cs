using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.Core
{
    /// <summary>A tab stop inside expanded snippet text: offsets are relative to the snippet start.</summary>
    public struct SnippetTabStop
    {
        public int Index;
        public int Offset;
        public int Length;
    }

    public sealed class ExpandedSnippet
    {
        public string Text;
        /// <summary>Stops in visiting order ($1, $2, ... and finally $0, which is the end of the text when omitted).</summary>
        public List<SnippetTabStop> Stops = new List<SnippetTabStop>();
    }

    public sealed class SnippetDefinition
    {
        public string Prefix;
        public string Description;
        public string Body;
    }

    /// <summary>
    /// Expands VS Code style snippet bodies: <c>$1</c>, <c>${1:placeholder}</c>, <c>$0</c> and <c>\$</c> escapes.
    /// Variables (<c>$TM_SELECTED_TEXT</c>, ...) expand to their default or nothing. A repeated index is a plain copy
    /// of its first occurrence (no live mirroring), so built-in snippets avoid repeating stops.
    /// </summary>
    public static class SnippetParser
    {
        /// <param name="indent">Prefix for every line after the first (the indentation of the insertion line).</param>
        /// <param name="tabWidth">Tabs in the body become this many spaces, matching the editor's soft tabs.</param>
        public static ExpandedSnippet Expand(string body, string indent = "", int tabWidth = 4)
        {
            string prepared = (body ?? "").Replace("\r\n", "\n").Replace("\t", new string(' ', Math.Max(1, tabWidth)));
            if (!string.IsNullOrEmpty(indent)) prepared = prepared.Replace("\n", "\n" + indent);

            var result = new ExpandedSnippet();
            var sb = new StringBuilder();
            var stops = new List<SnippetTabStop>();
            Parse(prepared, 0, prepared.Length, sb, stops);
            result.Text = sb.ToString();

            // Visit $1..$n in order (first occurrence of each index), $0 last.
            var seen = new HashSet<int>();
            stops.Sort((a, b) => a.Index == b.Index ? a.Offset.CompareTo(b.Offset) : SortKey(a.Index).CompareTo(SortKey(b.Index)));
            foreach (var stop in stops)
            {
                if (seen.Add(stop.Index)) result.Stops.Add(stop);
            }
            if (!seen.Contains(0)) result.Stops.Add(new SnippetTabStop { Index = 0, Offset = result.Text.Length, Length = 0 });
            return result;
        }

        private static int SortKey(int index) => index == 0 ? int.MaxValue : index;

        private static void Parse(string s, int start, int end, StringBuilder sb, List<SnippetTabStop> stops)
        {
            int i = start;
            while (i < end)
            {
                char ch = s[i];
                if (ch == '\\' && i + 1 < end && (s[i + 1] == '$' || s[i + 1] == '}' || s[i + 1] == '\\'))
                {
                    sb.Append(s[i + 1]);
                    i += 2;
                    continue;
                }

                if (ch == '$' && i + 1 < end)
                {
                    char next = s[i + 1];
                    if (char.IsDigit(next))
                    {
                        int j = i + 1;
                        while (j < end && char.IsDigit(s[j])) j++;
                        stops.Add(new SnippetTabStop { Index = ParseInt(s, i + 1, j), Offset = sb.Length, Length = 0 });
                        i = j;
                        continue;
                    }

                    if (next == '{')
                    {
                        int close = FindClosingBrace(s, i + 2, end);
                        if (close > 0)
                        {
                            ParsePlaceholder(s, i + 2, close, sb, stops);
                            i = close + 1;
                            continue;
                        }
                    }
                    else if (char.IsLetter(next) || next == '_')
                    {
                        // Bare variable ($TM_FILENAME, ...): not supported, expands to nothing.
                        int j = i + 1;
                        while (j < end && (char.IsLetterOrDigit(s[j]) || s[j] == '_')) j++;
                        i = j;
                        continue;
                    }
                }

                sb.Append(ch);
                i++;
            }
        }

        // ${1}, ${1:default text with $2 nested}, ${1|a,b|} (first choice), ${VAR:default}
        private static void ParsePlaceholder(string s, int start, int end, StringBuilder sb, List<SnippetTabStop> stops)
        {
            int j = start;
            while (j < end && char.IsDigit(s[j])) j++;

            if (j == start)
            {
                // Variable with optional default.
                int colon = s.IndexOf(':', start, end - start);
                if (colon >= 0) Parse(s, colon + 1, end, sb, stops);
                return;
            }

            int index = ParseInt(s, start, j);
            int offset = sb.Length;
            if (j < end && s[j] == ':')
            {
                Parse(s, j + 1, end, sb, stops);
            }
            else if (j < end && s[j] == '|')
            {
                int bar = s.IndexOf('|', j + 1, end - j - 1);
                string choices = s.Substring(j + 1, (bar < 0 ? end : bar) - j - 1);
                int comma = choices.IndexOf(',');
                sb.Append(comma < 0 ? choices : choices.Substring(0, comma));
            }
            stops.Add(new SnippetTabStop { Index = index, Offset = offset, Length = sb.Length - offset });
        }

        private static int FindClosingBrace(string s, int start, int end)
        {
            int depth = 0;
            for (int i = start; i < end; i++)
            {
                if (s[i] == '\\') { i++; continue; }
                if (s[i] == '{') depth++;
                else if (s[i] == '}')
                {
                    if (depth == 0) return i;
                    depth--;
                }
            }
            return -1;
        }

        private static int ParseInt(string s, int start, int end) =>
            int.Parse(s.Substring(start, end - start), NumberStyles.Integer, CultureInfo.InvariantCulture);
    }

    /// <summary>Built-in C#/Unity snippets plus user snippets from <c>UserSettings/FuzzyIDE/snippets.json</c> (VS Code format).</summary>
    public static class SnippetLibrary
    {
        public const string UserSnippetsPath = "UserSettings/FuzzyIDE/snippets.json";

        private static List<SnippetDefinition> s_UserSnippets;
        private static DateTime s_UserSnippetsStamp;

        public static readonly SnippetDefinition[] BuiltIn =
        {
            new SnippetDefinition { Prefix = "for", Description = "for loop", Body = "for (int i = 0; i < ${1:length}; i++)\n{\n\t$0\n}" },
            new SnippetDefinition { Prefix = "forr", Description = "reverse for loop", Body = "for (int i = ${1:length} - 1; i >= 0; i--)\n{\n\t$0\n}" },
            new SnippetDefinition { Prefix = "foreach", Description = "foreach loop", Body = "foreach (var ${1:item} in ${2:collection})\n{\n\t$0\n}" },
            new SnippetDefinition { Prefix = "if", Description = "if statement", Body = "if (${1:condition})\n{\n\t$0\n}" },
            new SnippetDefinition { Prefix = "else", Description = "else block", Body = "else\n{\n\t$0\n}" },
            new SnippetDefinition { Prefix = "while", Description = "while loop", Body = "while (${1:condition})\n{\n\t$0\n}" },
            new SnippetDefinition { Prefix = "try", Description = "try/catch", Body = "try\n{\n\t$1\n}\ncatch (${2:System.Exception} ${3:ex})\n{\n\t$0\n}" },
            new SnippetDefinition { Prefix = "switch", Description = "switch statement", Body = "switch (${1:value})\n{\n\tcase ${2:0}:\n\t\t$0\n\t\tbreak;\n}" },
            new SnippetDefinition { Prefix = "prop", Description = "auto property", Body = "public ${1:int} ${2:Value} { get; set; }$0" },
            new SnippetDefinition { Prefix = "sf", Description = "serialized private field", Body = "[SerializeField] private ${1:float} ${2:value};$0" },
            new SnippetDefinition { Prefix = "header", Description = "inspector header", Body = "[Header(\"${1:Settings}\")]$0" },
            new SnippetDefinition { Prefix = "coroutine", Description = "coroutine method", Body = "private System.Collections.IEnumerator ${1:Routine}()\n{\n\t$0\n\tyield return null;\n}" },
            new SnippetDefinition { Prefix = "reqcomp", Description = "RequireComponent attribute", Body = "[RequireComponent(typeof(${1:Rigidbody}))]$0" },
            new SnippetDefinition { Prefix = "createmenu", Description = "CreateAssetMenu attribute", Body = "[CreateAssetMenu(fileName = \"${1:New Asset}\", menuName = \"${2:Game}/New Asset\")]$0" },
            new SnippetDefinition { Prefix = "menuitem", Description = "editor menu item", Body = "[UnityEditor.MenuItem(\"${1:Tools}/${2:Do Something}\")]\nprivate static void ${3:DoSomething}()\n{\n\t$0\n}" },
            new SnippetDefinition { Prefix = "log", Description = "Debug.Log", Body = "Debug.Log($\"${1:message}\");$0" },
        };

        // Unity messages with their real signatures, so the inserted method actually gets called.
        private static readonly string[][] s_UnityMessages =
        {
            new[] { "Awake", "" }, new[] { "Start", "" }, new[] { "Update", "" }, new[] { "FixedUpdate", "" }, new[] { "LateUpdate", "" },
            new[] { "OnEnable", "" }, new[] { "OnDisable", "" }, new[] { "OnDestroy", "" }, new[] { "OnValidate", "" }, new[] { "Reset", "" },
            new[] { "OnGUI", "" }, new[] { "OnDrawGizmos", "" }, new[] { "OnDrawGizmosSelected", "" },
            new[] { "OnTriggerEnter", "Collider other" }, new[] { "OnTriggerStay", "Collider other" }, new[] { "OnTriggerExit", "Collider other" },
            new[] { "OnCollisionEnter", "Collision collision" }, new[] { "OnCollisionStay", "Collision collision" }, new[] { "OnCollisionExit", "Collision collision" },
            new[] { "OnTriggerEnter2D", "Collider2D other" }, new[] { "OnTriggerStay2D", "Collider2D other" }, new[] { "OnTriggerExit2D", "Collider2D other" },
            new[] { "OnCollisionEnter2D", "Collision2D collision" }, new[] { "OnCollisionStay2D", "Collision2D collision" }, new[] { "OnCollisionExit2D", "Collision2D collision" },
            new[] { "OnMouseDown", "" }, new[] { "OnMouseUp", "" }, new[] { "OnMouseEnter", "" }, new[] { "OnMouseExit", "" },
            new[] { "OnBecameVisible", "" }, new[] { "OnBecameInvisible", "" },
            new[] { "OnApplicationPause", "bool pauseStatus" }, new[] { "OnApplicationFocus", "bool hasFocus" }, new[] { "OnApplicationQuit", "" },
        };

        /// <summary>Unity message names with their parameter lists ("Collider other"; empty when none).</summary>
        public static IReadOnlyList<string[]> UnityMessageSignatures => s_UnityMessages;

        public static IEnumerable<SnippetDefinition> UnityMessages()
        {
            foreach (var m in s_UnityMessages)
            {
                yield return new SnippetDefinition
                {
                    Prefix = m[0],
                    Description = "Unity message",
                    Body = "private void " + m[0] + "(" + m[1] + ")\n{\n\t$0\n}"
                };
            }
        }

        /// <summary>Built-ins, Unity messages and user snippets; a user snippet with the same prefix wins.</summary>
        public static List<SnippetDefinition> All()
        {
            var byPrefix = new Dictionary<string, SnippetDefinition>(StringComparer.Ordinal);
            foreach (var s in BuiltIn) byPrefix[s.Prefix] = s;
            foreach (var s in UnityMessages()) byPrefix[s.Prefix] = s;
            foreach (var s in GetUserSnippets()) byPrefix[s.Prefix] = s;
            return new List<SnippetDefinition>(byPrefix.Values);
        }

        public static List<SnippetDefinition> GetUserSnippets()
        {
            try
            {
                if (!File.Exists(UserSnippetsPath)) return s_UserSnippets = new List<SnippetDefinition>();
                var stamp = File.GetLastWriteTimeUtc(UserSnippetsPath);
                if (s_UserSnippets != null && stamp == s_UserSnippetsStamp) return s_UserSnippets;
                s_UserSnippetsStamp = stamp;
                s_UserSnippets = ParseUserSnippets(File.ReadAllText(UserSnippetsPath));
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning($"[FuzzyIDE] Could not read {UserSnippetsPath}: {ex.Message}");
                s_UserSnippets = new List<SnippetDefinition>();
            }
            return s_UserSnippets;
        }

        /// <summary>
        /// VS Code snippet file: <c>{ "Name": { "prefix": "x" | ["x","y"], "body": "..." | ["line", ...], "description": "..." } }</c>.
        /// </summary>
        public static List<SnippetDefinition> ParseUserSnippets(string json)
        {
            var result = new List<SnippetDefinition>();
            if (!(MiniJson.Parse(json) is Dictionary<string, object> root)) return result;

            foreach (var entry in root)
            {
                if (!(entry.Value is Dictionary<string, object> def)) continue;
                def.TryGetValue("body", out var bodyValue);
                string body = bodyValue is List<object> bodyLines ? string.Join("\n", bodyLines) : bodyValue as string;
                if (body == null) continue;
                def.TryGetValue("description", out var description);

                def.TryGetValue("prefix", out var prefixValue);
                var prefixes = prefixValue is List<object> list ? list : new List<object> { prefixValue ?? entry.Key };
                foreach (var prefix in prefixes)
                {
                    if (!(prefix is string p) || p.Length == 0) continue;
                    result.Add(new SnippetDefinition { Prefix = p, Body = body, Description = description as string ?? entry.Key });
                }
            }
            return result;
        }
    }

    /// <summary>Small JSON reader (objects, arrays, strings, numbers, true/false/null) for settings-like files.</summary>
    public static class MiniJson
    {
        public static object Parse(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            int i = 0;
            var value = ParseValue(json, ref i);
            return value;
        }

        private static object ParseValue(string s, ref int i)
        {
            SkipWhitespace(s, ref i);
            if (i >= s.Length) throw new FormatException("Unexpected end of JSON");
            char ch = s[i];
            switch (ch)
            {
                case '{': return ParseObject(s, ref i);
                case '[': return ParseArray(s, ref i);
                case '"': return ParseString(s, ref i);
                case 't': Expect(s, ref i, "true"); return true;
                case 'f': Expect(s, ref i, "false"); return false;
                case 'n': Expect(s, ref i, "null"); return null;
                default: return ParseNumber(s, ref i);
            }
        }

        private static Dictionary<string, object> ParseObject(string s, ref int i)
        {
            var obj = new Dictionary<string, object>();
            i++; // {
            while (true)
            {
                SkipWhitespace(s, ref i);
                if (i < s.Length && s[i] == '}') { i++; return obj; }
                string key = ParseString(s, ref i);
                SkipWhitespace(s, ref i);
                if (i >= s.Length || s[i] != ':') throw new FormatException("Expected ':' at " + i);
                i++;
                obj[key] = ParseValue(s, ref i);
                SkipWhitespace(s, ref i);
                if (i < s.Length && s[i] == ',') { i++; continue; }
                if (i < s.Length && s[i] == '}') { i++; return obj; }
                throw new FormatException("Expected ',' or '}' at " + i);
            }
        }

        private static List<object> ParseArray(string s, ref int i)
        {
            var list = new List<object>();
            i++; // [
            while (true)
            {
                SkipWhitespace(s, ref i);
                if (i < s.Length && s[i] == ']') { i++; return list; }
                list.Add(ParseValue(s, ref i));
                SkipWhitespace(s, ref i);
                if (i < s.Length && s[i] == ',') { i++; continue; }
                if (i < s.Length && s[i] == ']') { i++; return list; }
                throw new FormatException("Expected ',' or ']' at " + i);
            }
        }

        private static string ParseString(string s, ref int i)
        {
            if (i >= s.Length || s[i] != '"') throw new FormatException("Expected string at " + i);
            i++;
            var sb = new StringBuilder();
            while (i < s.Length)
            {
                char ch = s[i++];
                if (ch == '"') return sb.ToString();
                if (ch != '\\') { sb.Append(ch); continue; }
                if (i >= s.Length) break;
                char esc = s[i++];
                switch (esc)
                {
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'r': sb.Append('\r'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u':
                        sb.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        i += 4;
                        break;
                    default: sb.Append(esc); break;
                }
            }
            throw new FormatException("Unterminated string");
        }

        private static object ParseNumber(string s, ref int i)
        {
            int start = i;
            while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
            if (start == i) throw new FormatException("Unexpected character '" + s[i] + "' at " + i);
            return double.Parse(s.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        private static void Expect(string s, ref int i, string word)
        {
            if (string.CompareOrdinal(s, i, word, 0, word.Length) != 0) throw new FormatException("Expected " + word + " at " + i);
            i += word.Length;
        }

        // Whitespace plus // and /* */ comments, which VS Code snippet files allow.
        private static void SkipWhitespace(string s, ref int i)
        {
            while (i < s.Length)
            {
                if (char.IsWhiteSpace(s[i])) { i++; continue; }
                if (s[i] == '/' && i + 1 < s.Length && s[i + 1] == '/')
                {
                    while (i < s.Length && s[i] != '\n') i++;
                    continue;
                }
                if (s[i] == '/' && i + 1 < s.Length && s[i + 1] == '*')
                {
                    int end = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    i = end < 0 ? s.Length : end + 2;
                    continue;
                }
                break;
            }
        }
    }
}
