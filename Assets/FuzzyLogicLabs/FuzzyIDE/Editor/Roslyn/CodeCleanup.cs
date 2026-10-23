using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.Roslyn
{
    public static class CodeCleanup
    {
        /// <summary>
        /// Removes file-level using directives the compiler reports as unnecessary (CS8019) and sorts the rest:
        /// System namespaces first, then alphabetically, then <c>using static</c>, then aliases. Usings mixed with
        /// preprocessor directives are only pruned, never reordered.
        /// </summary>
        public static string OrganizeUsings(SyntaxNode root, SemanticModel model)
        {
            var text = root.SyntaxTree.GetText();
            if (!(root is CompilationUnitSyntax unit) || unit.Usings.Count == 0) return text.ToString();

            var unused = new HashSet<TextSpan>(model.GetDiagnostics()
                .Where(d => d.Id == "CS8019" && d.Location.SourceTree == root.SyntaxTree)
                .Select(d => d.Location.SourceSpan));
            var usings = unit.Usings;
            bool hasDirectives = usings.Any(u => u.GetLeadingTrivia().Any(t => t.IsDirective) || u.GetTrailingTrivia().Any(t => t.IsDirective));
            string newLine = text.ToString().Contains("\r\n") ? "\r\n" : "\n";

            if (hasDirectives)
            {
                var changes = usings.Where(u => unused.Contains(u.Span)).Select(u => new TextChange(FullLine(text, u.Span), "")).ToList();
                return text.WithChanges(changes).ToString();
            }

            var kept = usings.Where(u => !unused.Contains(u.Span))
                .OrderBy(u => u.Alias != null ? 2 : u.StaticKeyword.IsKind(SyntaxKind.StaticKeyword) ? 1 : 0)
                .ThenBy(u => IsSystem(u) ? 0 : 1)
                .ThenBy(u => u.Name.ToString(), StringComparer.Ordinal)
                .Select(u => u.WithoutTrivia().ToFullString())
                .ToList();

            var block = TextSpan.FromBounds(usings.First().SpanStart, usings.Last().Span.End);
            if (kept.Count == 0)
            {
                // Drop the whole block and the blank line that separated it from the code.
                int end = FullLine(text, block).End;
                if (end < text.Length)
                {
                    var nextLine = text.Lines.GetLineFromPosition(end);
                    if (nextLine.ToString().Trim().Length == 0) end = nextLine.EndIncludingLineBreak;
                }
                return text.WithChanges(new TextChange(TextSpan.FromBounds(text.Lines.GetLineFromPosition(block.Start).Start, end), "")).ToString();
            }
            return text.WithChanges(new TextChange(block, string.Join(newLine, kept))).ToString();
        }

        private static bool IsSystem(UsingDirectiveSyntax u)
        {
            string name = u.Name.ToString();
            return name == "System" || name.StartsWith("System.", StringComparison.Ordinal);
        }

        private static TextSpan FullLine(SourceText text, TextSpan span)
        {
            var first = text.Lines.GetLineFromPosition(span.Start);
            var last = text.Lines.GetLineFromPosition(span.End);
            return TextSpan.FromBounds(first.Start, last.EndIncludingLineBreak);
        }
    }
}
