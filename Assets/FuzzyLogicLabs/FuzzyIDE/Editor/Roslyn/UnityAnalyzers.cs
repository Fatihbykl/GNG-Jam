using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using FuzzyLogicLabs.FuzzyIDE.Editor.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.Roslyn
{
    public sealed class CodeFix
    {
        public string Title;
        public List<TextChange> Changes = new List<TextChange>();

        public string Apply(SourceText text) => text.WithChanges(Changes).ToString();
    }

    public sealed class CodeIssue
    {
        public string Id;
        public TextSpan Span;
        public string Message;
        public List<CodeFix> Fixes = new List<CodeFix>();
    }

    /// <summary>
    /// Unity-specific diagnostics in the spirit of Microsoft.Unity.Analyzers, written against the Roslyn APIs the
    /// editor already ships: empty or misspelled messages, wrong message signatures, tag comparisons, per-frame
    /// lookups and null propagation on UnityEngine.Object.
    /// </summary>
    public static class UnityAnalyzers
    {
        public const string EmptyMessage = "FZU010";
        public const string TagComparison = "FZU011";
        public const string ExpensiveCallInUpdate = "FZU012";
        public const string NullPropagation = "FZU013";
        public const string WrongMessageSignature = "FZU014";
        public const string MisspelledMessage = "FZU015";

        private static readonly HashSet<string> s_PerFrameMessages = new HashSet<string> { "Update", "FixedUpdate", "LateUpdate", "OnGUI" };

        private static readonly HashSet<string> s_ExpensiveCalls = new HashSet<string>
        {
            "GetComponent", "GetComponents", "GetComponentInChildren", "GetComponentsInChildren", "GetComponentInParent", "GetComponentsInParent",
            "FindObjectOfType", "FindObjectsOfType", "FindFirstObjectByType", "FindAnyObjectByType", "FindObjectsByType",
            "Find", "FindWithTag", "FindGameObjectWithTag", "FindGameObjectsWithTag",
        };

        private static Dictionary<string, string> s_Messages;

        /// <summary>Unity message name to its parameter list ("Collider other", or "" when it takes none).</summary>
        public static Dictionary<string, string> Messages
        {
            get
            {
                if (s_Messages == null)
                {
                    var messages = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var m in SnippetLibrary.UnityMessageSignatures) messages[m[0]] = m[1];
                    s_Messages = messages;
                }
                return s_Messages;
            }
        }

        public static List<CodeIssue> Analyze(SyntaxNode root, SemanticModel model, CancellationToken cancellationToken = default)
        {
            var issues = new List<CodeIssue>();
            if (root == null || model == null) return issues;
            var text = root.SyntaxTree.GetText(cancellationToken);

            foreach (var node in root.DescendantNodes())
            {
                if (cancellationToken.IsCancellationRequested) break;
                switch (node)
                {
                    case MethodDeclarationSyntax method:
                        AnalyzeMethod(method, model, text, issues);
                        break;
                    case BinaryExpressionSyntax binary:
                        AnalyzeTagComparison(binary, model, issues);
                        AnalyzeCoalesce(binary, model, issues);
                        break;
                    case InvocationExpressionSyntax invocation:
                        AnalyzePerFrameCall(invocation, model, issues);
                        break;
                    case ConditionalAccessExpressionSyntax conditional:
                        AnalyzeConditionalAccess(conditional, model, issues);
                        break;
                }
            }
            return issues;
        }

        // ---- Messages ---------------------------------------------------------------------------------------------

        private static void AnalyzeMethod(MethodDeclarationSyntax method, SemanticModel model, SourceText text, List<CodeIssue> issues)
        {
            if (method.Modifiers.Any(SyntaxKind.OverrideKeyword) || method.Modifiers.Any(SyntaxKind.AbstractKeyword) || method.Modifiers.Any(SyntaxKind.StaticKeyword)) return;
            if (!(model.GetDeclaredSymbol(method) is IMethodSymbol symbol) || !IsMonoBehaviour(symbol.ContainingType)) return;

            string name = method.Identifier.ValueText;
            if (Messages.TryGetValue(name, out string expected))
            {
                if (IsEmpty(method))
                {
                    var issue = new CodeIssue
                    {
                        Id = EmptyMessage,
                        Span = method.Identifier.Span,
                        Message = $"Empty Unity message '{name}' still costs a call from the engine; remove it."
                    };
                    issue.Fixes.Add(new CodeFix { Title = $"Remove empty {name}()", Changes = { new TextChange(WholeLines(text, method), "") } });
                    issues.Add(issue);
                }
                AnalyzeSignature(method, symbol, expected, issues);
                return;
            }

            // Private methods only: a public method with a similar name is more likely intentional API.
            if (symbol.DeclaredAccessibility == Accessibility.Public || method.ParameterList.Parameters.Count > 1) return;
            string suggestion = Messages.Keys.FirstOrDefault(m => string.Equals(m, name, StringComparison.OrdinalIgnoreCase))
                                ?? (name.Length >= 5 ? Messages.Keys.FirstOrDefault(m => Math.Abs(m.Length - name.Length) <= 1 && EditDistance(m, name) == 1) : null);
            if (suggestion == null) return;

            var misspelled = new CodeIssue
            {
                Id = MisspelledMessage,
                Span = method.Identifier.Span,
                Message = $"'{name}' is not a Unity message and will never be called by the engine. Did you mean '{suggestion}'?"
            };
            misspelled.Fixes.Add(new CodeFix { Title = $"Rename to {suggestion}", Changes = { new TextChange(method.Identifier.Span, suggestion) } });
            issues.Add(misspelled);
        }

        private static void AnalyzeSignature(MethodDeclarationSyntax method, IMethodSymbol symbol, string expected, List<CodeIssue> issues)
        {
            // The parameterless form is accepted for every message; only a different parameter list is a mistake.
            if (symbol.Parameters.Length == 0) return;
            var expectedParams = expected.Length == 0 ? new string[0] : expected.Split(',').Select(p => p.Trim()).ToArray();
            var expectedTypes = expectedParams.Select(p => p.Split(' ')[0]).ToArray();
            bool matches = expectedTypes.Length == symbol.Parameters.Length &&
                           expectedTypes.Select((t, i) => SameTypeName(symbol.Parameters[i].Type, t)).All(ok => ok);
            if (matches) return;

            string name = method.Identifier.ValueText;
            var issue = new CodeIssue
            {
                Id = WrongMessageSignature,
                Span = method.ParameterList.Span,
                Message = expected.Length == 0
                    ? $"Unity message '{name}' takes no parameters; Unity will not call this method."
                    : $"Unity message '{name}' expects ({expected}); Unity will not call this method."
            };
            string newParams = expectedParams.Length == 1 && symbol.Parameters.Length == 1
                ? expectedTypes[0] + " " + symbol.Parameters[0].Name // keep the user's parameter name
                : expected;
            issue.Fixes.Add(new CodeFix { Title = $"Change signature to {name}({newParams})", Changes = { new TextChange(method.ParameterList.Span, "(" + newParams + ")") } });
            issues.Add(issue);
        }

        private static bool SameTypeName(ITypeSymbol type, string expected)
        {
            switch (expected)
            {
                case "bool": return type.SpecialType == SpecialType.System_Boolean;
                case "int": return type.SpecialType == SpecialType.System_Int32;
                case "float": return type.SpecialType == SpecialType.System_Single;
                default: return type.Name == expected || type.TypeKind == TypeKind.Error;
            }
        }

        private static bool IsEmpty(MethodDeclarationSyntax method)
        {
            if (method.Body == null || method.Body.Statements.Count > 0) return false;
            // Comments or #if blocks inside mean the emptiness is deliberate (or the body is conditional).
            return !method.Body.CloseBraceToken.LeadingTrivia.Any(t => !t.IsKind(SyntaxKind.WhitespaceTrivia) && !t.IsKind(SyntaxKind.EndOfLineTrivia)) &&
                   !method.Body.OpenBraceToken.TrailingTrivia.Any(t => t.IsKind(SyntaxKind.SingleLineCommentTrivia) || t.IsKind(SyntaxKind.MultiLineCommentTrivia));
        }

        // Full lines covered by the member plus one preceding blank line, so removing it leaves no gap.
        private static TextSpan WholeLines(SourceText text, SyntaxNode node)
        {
            var span = node.Span;
            foreach (var trivia in node.GetLeadingTrivia())
            {
                if (trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) || trivia.IsKind(SyntaxKind.SingleLineCommentTrivia) || trivia.IsKind(SyntaxKind.MultiLineCommentTrivia))
                {
                    span = TextSpan.FromBounds(Math.Min(span.Start, trivia.SpanStart), span.End);
                }
            }
            int startLine = text.Lines.GetLineFromPosition(span.Start).LineNumber;
            int endLine = text.Lines.GetLineFromPosition(span.End).LineNumber;
            if (startLine > 0 && text.Lines[startLine - 1].ToString().Trim().Length == 0) startLine--;
            int end = endLine + 1 < text.Lines.Count ? text.Lines[endLine + 1].Start : text.Length;
            return TextSpan.FromBounds(text.Lines[startLine].Start, end);
        }

        // ---- Expressions ------------------------------------------------------------------------------------------

        private static void AnalyzeTagComparison(BinaryExpressionSyntax binary, SemanticModel model, List<CodeIssue> issues)
        {
            if (!binary.IsKind(SyntaxKind.EqualsExpression) && !binary.IsKind(SyntaxKind.NotEqualsExpression)) return;
            ExpressionSyntax tagSide = null, other = null;
            if (IsTagAccess(binary.Left, model)) { tagSide = binary.Left; other = binary.Right; }
            else if (IsTagAccess(binary.Right, model)) { tagSide = binary.Right; other = binary.Left; }
            if (tagSide == null || other.IsKind(SyntaxKind.NullLiteralExpression)) return;

            string receiver = tagSide is MemberAccessExpressionSyntax ma ? ma.Expression.ToString() + "." : "";
            string replacement = (binary.IsKind(SyntaxKind.NotEqualsExpression) ? "!" : "") + receiver + "CompareTag(" + other.ToString() + ")";
            var issue = new CodeIssue
            {
                Id = TagComparison,
                Span = binary.Span,
                Message = "Comparing 'tag' with a string allocates; use CompareTag."
            };
            issue.Fixes.Add(new CodeFix { Title = "Use CompareTag", Changes = { new TextChange(binary.Span, replacement) } });
            issues.Add(issue);
        }

        private static bool IsTagAccess(ExpressionSyntax expression, SemanticModel model)
        {
            string name = expression is MemberAccessExpressionSyntax ma ? ma.Name.Identifier.ValueText : (expression as IdentifierNameSyntax)?.Identifier.ValueText;
            if (name != "tag") return false;
            var symbol = model.GetSymbolInfo(expression).Symbol;
            return (symbol is IPropertySymbol || symbol is IFieldSymbol) && IsUnityEngineType(symbol.ContainingType);
        }

        private static void AnalyzePerFrameCall(InvocationExpressionSyntax invocation, SemanticModel model, List<CodeIssue> issues)
        {
            string name = invocation.Expression is MemberAccessExpressionSyntax ma ? ma.Name.Identifier.ValueText
                : invocation.Expression is GenericNameSyntax g ? g.Identifier.ValueText
                : invocation.Expression is IdentifierNameSyntax id ? id.Identifier.ValueText : null;
            if (name == null || !s_ExpensiveCalls.Contains(name)) return;

            var method = invocation.FirstAncestorOrSelf<MethodDeclarationSyntax>();
            if (method == null || !s_PerFrameMessages.Contains(method.Identifier.ValueText)) return;
            if (invocation.Ancestors().Any(a => a is LambdaExpressionSyntax || a is LocalFunctionStatementSyntax)) return;
            if (!(model.GetSymbolInfo(invocation).Symbol is IMethodSymbol called) || !IsUnityEngineType(called.ContainingType)) return;
            if (!(model.GetDeclaredSymbol(method) is IMethodSymbol container) || !IsMonoBehaviour(container.ContainingType)) return;

            issues.Add(new CodeIssue
            {
                Id = ExpensiveCallInUpdate,
                Span = invocation.Span,
                Message = $"{name}() in {method.Identifier.ValueText} runs every frame; cache the result in Awake or Start."
            });
        }

        private static void AnalyzeConditionalAccess(ConditionalAccessExpressionSyntax conditional, SemanticModel model, List<CodeIssue> issues)
        {
            if (!IsUnityObject(model.GetTypeInfo(conditional.Expression).Type)) return;
            var issue = new CodeIssue
            {
                Id = NullPropagation,
                Span = conditional.OperatorToken.Span,
                Message = "'?.' on a UnityEngine.Object skips Unity's destroyed-object check; compare with null explicitly."
            };
            // "target?.Do();" as a statement has a direct equivalent.
            if (conditional.Parent is ExpressionStatementSyntax statement && IsSimple(conditional.Expression))
            {
                string receiver = conditional.Expression.ToString();
                string replacement = $"if ({receiver} != null) {receiver}{conditional.WhenNotNull};";
                issue.Fixes.Add(new CodeFix { Title = "Use an explicit null check", Changes = { new TextChange(statement.Span, replacement) } });
            }
            issues.Add(issue);
        }

        private static void AnalyzeCoalesce(BinaryExpressionSyntax binary, SemanticModel model, List<CodeIssue> issues)
        {
            if (!binary.IsKind(SyntaxKind.CoalesceExpression) || !IsUnityObject(model.GetTypeInfo(binary.Left).Type)) return;
            var issue = new CodeIssue
            {
                Id = NullPropagation,
                Span = binary.OperatorToken.Span,
                Message = "'??' on a UnityEngine.Object skips Unity's destroyed-object check; compare with null explicitly."
            };
            if (IsSimple(binary.Left))
            {
                string left = binary.Left.ToString();
                issue.Fixes.Add(new CodeFix { Title = "Use an explicit null check", Changes = { new TextChange(binary.Span, $"{left} != null ? {left} : {binary.Right}") } });
            }
            issues.Add(issue);
        }

        // Identifiers and member access chains can be evaluated twice without side effects.
        private static bool IsSimple(ExpressionSyntax expression) =>
            expression is IdentifierNameSyntax || expression is ThisExpressionSyntax ||
            (expression is MemberAccessExpressionSyntax ma && IsSimple(ma.Expression));

        // ---- Type helpers -----------------------------------------------------------------------------------------

        private static bool IsUnityEngineType(INamedTypeSymbol type)
        {
            string ns = type?.ContainingNamespace?.ToDisplayString();
            return ns != null && (ns == "UnityEngine" || ns.StartsWith("UnityEngine.", StringComparison.Ordinal));
        }

        public static bool IsUnityObject(ITypeSymbol type)
        {
            for (var t = type; t != null; t = t.BaseType)
            {
                if (t.Name == "Object" && t.ContainingNamespace?.ToDisplayString() == "UnityEngine") return true;
            }
            return false;
        }

        public static bool IsMonoBehaviour(INamedTypeSymbol type)
        {
            for (var t = type?.BaseType; t != null; t = t.BaseType)
            {
                if (t.Name == "MonoBehaviour" && t.ContainingNamespace?.ToDisplayString() == "UnityEngine") return true;
            }
            return false;
        }

        private static int EditDistance(string a, string b)
        {
            var d = new int[a.Length + 1, b.Length + 1];
            for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
            for (int j = 0; j <= b.Length; j++) d[0, j] = j;
            for (int i = 1; i <= a.Length; i++)
                for (int j = 1; j <= b.Length; j++)
                    d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            return d[a.Length, b.Length];
        }
    }
}
