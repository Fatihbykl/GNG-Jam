using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.Notes.LiveEditor
{
    public enum CodeTokenKind
    {
        Keyword,
        Type,
        String,
        Comment,
        Number,
        Variable
    }

    public struct CodeToken
    {
        public int Start;
        public int Length;
        public CodeTokenKind Kind;

        public CodeToken(int start, int length, CodeTokenKind kind)
        {
            Start = start;
            Length = length;
            Kind = kind;
        }
    }

    /// <summary>
    /// Lightweight, line-local syntax highlighter for fenced code blocks in notes.
    /// Not a parser: it colours keywords, strings, comments and numbers well enough for reading snippets.
    /// Returned tokens never overlap and are sorted by start.
    /// </summary>
    public static class CodeHighlighter
    {
        private sealed class LanguageRules
        {
            public Regex Keywords;
            public Regex Types;
            public string LineComment;        // e.g. "//" or "#"
            public bool HashComment;          // '#' starts a comment only at line start / after whitespace
            public bool ShellVariables;
        }

        private static readonly Regex StringRx = new Regex(@"@?""(?:\\.|""""|[^""\\])*""?|'(?:\\.|[^'\\])*'?|`[^`]*`?", RegexOptions.Compiled);
        private static readonly Regex NumberRx = new Regex(@"\b(?:0x[0-9a-fA-F]+|\d+(?:\.\d+)?[fFdDmMlLuU]?)\b", RegexOptions.Compiled);
        private static readonly Regex ShellVarRx = new Regex(@"\$\{?[A-Za-z_][A-Za-z0-9_]*\}?|\$env:[A-Za-z_][A-Za-z0-9_]*", RegexOptions.Compiled);

        private static readonly Dictionary<string, LanguageRules> Languages = new Dictionary<string, LanguageRules>(StringComparer.OrdinalIgnoreCase);

        static CodeHighlighter()
        {
            var csharp = new LanguageRules
            {
                Keywords = Words("abstract as async await base bool break byte case catch char checked class const continue decimal default delegate do double else enum event explicit extern false finally fixed float for foreach get goto if implicit in int interface internal is lock long namespace new null object operator out override params private protected public readonly ref return sbyte sealed set short sizeof stackalloc static string struct switch this throw true try typeof uint ulong unchecked unsafe ushort using var virtual void volatile when where while yield"),
                Types = Words("GameObject Transform MonoBehaviour ScriptableObject Vector2 Vector3 Vector4 Quaternion Color Rigidbody Rigidbody2D Collider Collider2D Debug Mathf Time Input List Dictionary HashSet IEnumerator Coroutine Action Func Task UnityEngine UnityEditor EditorWindow Component Object Animator AudioSource Camera Material Mesh Texture2D Sprite"),
                LineComment = "//"
            };
            Register(csharp, "csharp", "cs", "c#");

            var js = new LanguageRules
            {
                Keywords = Words("async await break case catch class const continue debugger default delete do else export extends false finally for from function if import in instanceof let new null return static super switch this throw true try typeof undefined var void while yield interface type enum implements"),
                LineComment = "//"
            };
            Register(js, "js", "javascript", "ts", "typescript", "jsx", "tsx");

            var shell = new LanguageRules
            {
                Keywords = Words("if then else elif fi for while until do done case esac in function return exit export local echo cd ls cat grep git dotnet npm unity Write-Host Get-ChildItem Set-Location"),
                HashComment = true,
                ShellVariables = true
            };
            Register(shell, "sh", "bash", "shell", "zsh", "console", "terminal", "ps", "ps1", "powershell", "pwsh");

            Register(new LanguageRules
            {
                Keywords = Words("and as assert async await break class continue def del elif else except False finally for from global if import in is lambda None nonlocal not or pass raise return True try while with yield self"),
                HashComment = true
            }, "python", "py");

            Register(new LanguageRules
            {
                Keywords = Words("Shader Properties SubShader Pass Tags CGPROGRAM ENDCG HLSLPROGRAM ENDHLSL CGINCLUDE HLSLINCLUDE Fallback CustomEditor struct return if else for while in out inout uniform void discard"),
                Types = Words("float float2 float3 float4 float4x4 half half2 half3 half4 fixed fixed2 fixed3 fixed4 int int2 int3 int4 bool sampler2D samplerCUBE Texture2D SamplerState vec2 vec3 vec4 mat4"),
                LineComment = "//"
            }, "shader", "hlsl", "glsl", "cg", "shaderlab");

            Register(new LanguageRules { Keywords = Words("true false null"), LineComment = null }, "json");
            Register(new LanguageRules { Keywords = Words("true false null yes no"), HashComment = true }, "yaml", "yml");
        }

        public static List<CodeToken> Tokenize(string line, string language)
        {
            var tokens = new List<CodeToken>();
            if (string.IsNullOrEmpty(line)) return tokens;

            Languages.TryGetValue((language ?? "").Trim(), out var rules);
            var taken = new bool[line.Length];

            // 1. Comments first: everything after the marker (unless the marker is inside a string).
            int commentAt = FindCommentStart(line, rules);
            if (commentAt >= 0)
            {
                Add(tokens, taken, commentAt, line.Length - commentAt, CodeTokenKind.Comment);
            }

            int limit = commentAt >= 0 ? commentAt : line.Length;
            string code = line.Substring(0, limit);

            // 2. Strings, 3. shell variables, 4. numbers, 5. keywords/types (first come, first served).
            foreach (Match m in StringRx.Matches(code)) Add(tokens, taken, m.Index, m.Length, CodeTokenKind.String);
            if (rules != null && rules.ShellVariables)
                foreach (Match m in ShellVarRx.Matches(code)) Add(tokens, taken, m.Index, m.Length, CodeTokenKind.Variable);
            foreach (Match m in NumberRx.Matches(code)) Add(tokens, taken, m.Index, m.Length, CodeTokenKind.Number);
            if (rules?.Keywords != null)
                foreach (Match m in rules.Keywords.Matches(code)) Add(tokens, taken, m.Index, m.Length, CodeTokenKind.Keyword);
            if (rules?.Types != null)
                foreach (Match m in rules.Types.Matches(code)) Add(tokens, taken, m.Index, m.Length, CodeTokenKind.Type);

            tokens.Sort((a, b) => a.Start.CompareTo(b.Start));
            return tokens;
        }

        private static int FindCommentStart(string line, LanguageRules rules)
        {
            string marker = rules != null ? rules.LineComment : "//";
            bool hash = rules != null && rules.HashComment;
            char quote = '\0';

            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (quote != '\0')
                {
                    if (c == '\\') { i++; continue; }
                    if (c == quote) quote = '\0';
                    continue;
                }
                if (c == '"' || c == '\'') { quote = c; continue; }

                if (marker != null && string.CompareOrdinal(line, i, marker, 0, marker.Length) == 0) return i;
                if (hash && c == '#' && (i == 0 || char.IsWhiteSpace(line[i - 1]))) return i;
            }
            return -1;
        }

        private static void Add(List<CodeToken> tokens, bool[] taken, int start, int length, CodeTokenKind kind)
        {
            if (length <= 0) return;
            for (int i = start; i < start + length; i++)
            {
                if (taken[i]) return;
            }
            for (int i = start; i < start + length; i++) taken[i] = true;
            tokens.Add(new CodeToken(start, length, kind));
        }

        private static Regex Words(string words) =>
            new Regex(@"(?<![\w$-])(?:" + string.Join("|", Array.ConvertAll(words.Split(' '), Regex.Escape)) + @")(?![\w-])", RegexOptions.Compiled);

        private static void Register(LanguageRules rules, params string[] names)
        {
            foreach (var n in names) Languages[n] = rules;
        }
    }
}
