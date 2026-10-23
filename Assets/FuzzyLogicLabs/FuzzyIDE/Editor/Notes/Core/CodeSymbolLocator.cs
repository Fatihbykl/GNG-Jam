using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.Notes.Core
{
    /// <summary>
    /// Resolves symbol anchors in C# code ("Awake", "PlayerMotor.Awake") to lines and back, using only
    /// Roslyn's syntax tree (no compilation). This is what keeps [[File.cs#Method]] links pointing at the
    /// right place while the code around them changes.
    /// </summary>
    public static class CodeSymbolLocator
    {
        /// <summary>0-based line of the first declaration named <paramref name="symbol"/>, or -1.</summary>
        public static int FindSymbolLine(string code, string symbol)
        {
            var lines = FindSymbolLines(code, new[] { symbol });
            return lines.TryGetValue(symbol ?? "", out int line) ? line : -1;
        }

        /// <summary>Resolves several symbols with a single parse. Missing symbols are left out of the result.</summary>
        public static Dictionary<string, int> FindSymbolLines(string code, IEnumerable<string> symbols)
        {
            var result = new Dictionary<string, int>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(code) || symbols == null) return result;

            var wanted = new List<(string symbol, string name, string container)>();
            foreach (var s in symbols)
            {
                if (string.IsNullOrWhiteSpace(s) || result.ContainsKey(s)) continue;
                string trimmed = s.Trim();
                int dot = trimmed.LastIndexOf('.');
                string name = dot >= 0 ? trimmed.Substring(dot + 1) : trimmed;
                string container = null;
                if (dot > 0)
                {
                    string prefix = trimmed.Substring(0, dot);
                    int prev = prefix.LastIndexOf('.');
                    container = prev >= 0 ? prefix.Substring(prev + 1) : prefix;
                }
                wanted.Add((s, name, container));
            }
            if (wanted.Count == 0) return result;

            var root = CSharpSyntaxTree.ParseText(code).GetRoot();
            foreach (var node in root.DescendantNodes())
            {
                var id = GetIdentifier(node);
                if (id.RawKind == 0) continue;
                string text = id.ValueText;

                for (int i = 0; i < wanted.Count; i++)
                {
                    var w = wanted[i];
                    if (result.ContainsKey(w.symbol) || w.name != text) continue;
                    if (w.container != null && ContainingTypeName(node) != w.container) continue;
                    result[w.symbol] = id.GetLocation().GetLineSpan().StartLinePosition.Line;
                }
                if (result.Count == wanted.Count) break;
            }
            return result;
        }

        /// <summary>
        /// Anchor for code at <paramref name="line"/> (0-based): the enclosing method/property/field name,
        /// or the type name between members. Qualified as "Type.Member" when the member name is declared
        /// more than once in the file. Null when the line is outside any type.
        /// </summary>
        public static string GetEnclosingSymbol(string code, int line)
        {
            if (string.IsNullOrEmpty(code) || line < 0) return null;
            var tree = CSharpSyntaxTree.ParseText(code);
            var text = tree.GetText();
            if (line >= text.Lines.Count) return null;

            var textLine = text.Lines[line];
            int pos = textLine.Start;
            string lineText = textLine.ToString();
            int indent = 0;
            while (indent < lineText.Length && char.IsWhiteSpace(lineText[indent])) indent++;
            pos += Math.Min(indent, Math.Max(0, lineText.Length - 1));

            var root = tree.GetRoot();
            var token = root.FindToken(Math.Min(pos, Math.Max(0, root.FullSpan.End - 1)));

            SyntaxNode member = null;
            SyntaxNode type = null;
            for (var n = token.Parent; n != null; n = n.Parent)
            {
                if (member == null && IsMember(n)) member = n;
                if (n is BaseTypeDeclarationSyntax || n is DelegateDeclarationSyntax) { type = n; break; }
            }

            // Code between a type's braces but outside members belongs to the type itself.
            // A token in a member's leading trivia (the comment above it) counts as part of the member.
            SyntaxNode anchor = member ?? type;
            if (anchor == null) return null;

            var anchorId = GetIdentifier(anchor is FieldDeclarationSyntax f ? (SyntaxNode)f.Declaration.Variables[0] :
                                         anchor is EventFieldDeclarationSyntax ef ? ef.Declaration.Variables[0] : anchor);
            if (anchorId.RawKind == 0 && type != null && anchor != type)
            {
                anchor = type; // operators and indexers have no name of their own
                anchorId = GetIdentifier(type);
            }
            if (anchorId.RawKind == 0) return null;
            string name = anchorId.ValueText;

            if (anchor != type && type != null)
            {
                int declarations = 0;
                foreach (var node in root.DescendantNodes())
                {
                    var id = GetIdentifier(node);
                    if (id.RawKind != 0 && id.ValueText == name && !(node is ConstructorDeclarationSyntax)) declarations++;
                }
                if (declarations > 1) name = GetIdentifier(type).ValueText + "." + name;
            }
            return name;
        }

        private static bool IsMember(SyntaxNode n) =>
            n is MethodDeclarationSyntax || n is ConstructorDeclarationSyntax || n is PropertyDeclarationSyntax ||
            n is EventDeclarationSyntax || n is FieldDeclarationSyntax || n is EventFieldDeclarationSyntax ||
            n is EnumMemberDeclarationSyntax || n is OperatorDeclarationSyntax || n is IndexerDeclarationSyntax;

        private static SyntaxToken GetIdentifier(SyntaxNode node)
        {
            switch (node)
            {
                case BaseTypeDeclarationSyntax t: return t.Identifier;
                case DelegateDeclarationSyntax d: return d.Identifier;
                case MethodDeclarationSyntax m: return m.Identifier;
                case ConstructorDeclarationSyntax c: return c.Identifier;
                case PropertyDeclarationSyntax p: return p.Identifier;
                case EventDeclarationSyntax e: return e.Identifier;
                case EnumMemberDeclarationSyntax em: return em.Identifier;
                case VariableDeclaratorSyntax v when v.Parent?.Parent is FieldDeclarationSyntax || v.Parent?.Parent is EventFieldDeclarationSyntax:
                    return v.Identifier;
                default: return default;
            }
        }

        private static string ContainingTypeName(SyntaxNode node)
        {
            for (var n = node.Parent; n != null; n = n.Parent)
            {
                if (n is BaseTypeDeclarationSyntax t) return t.Identifier.ValueText;
            }
            return null;
        }
    }
}
