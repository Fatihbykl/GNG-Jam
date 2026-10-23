using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.Roslyn
{
    public enum TokenClass { Keyword, String, Number, Comment, Preprocessor, Type, Method, UnityMessage, Field, Property, Parameter, Local, EnumMember, Event }

    public struct ClassifiedSpan
    {
        public int Start;
        public int Length;
        public TokenClass Class;

        public ClassifiedSpan(int start, int length, TokenClass cls)
        {
            Start = start;
            Length = length;
            Class = cls;
        }
    }

    /// <summary>
    /// Token classification for highlighting. Declarations are classified from syntax; references need the semantic
    /// model (skipped when <c>model</c> is null). Unity messages declared in MonoBehaviours get their own class.
    /// </summary>
    public static class CodeClassifier
    {
        public static List<ClassifiedSpan> Classify(SyntaxNode root, SemanticModel model, CancellationToken cancellationToken = default)
        {
            var spans = new List<ClassifiedSpan>();
            var unityClasses = new Dictionary<SyntaxNode, bool>();

            foreach (var token in root.DescendantTokens(descendIntoTrivia: false))
            {
                if (cancellationToken.IsCancellationRequested) return spans;
                AddTrivia(token.LeadingTrivia, spans);

                var kind = token.Kind();
                if (token.IsKeyword())
                {
                    spans.Add(new ClassifiedSpan(token.SpanStart, token.Span.Length, TokenClass.Keyword));
                }
                else if (kind == SyntaxKind.StringLiteralToken || kind == SyntaxKind.CharacterLiteralToken || kind == SyntaxKind.InterpolatedStringTextToken ||
                         kind == SyntaxKind.InterpolatedStringStartToken || kind == SyntaxKind.InterpolatedVerbatimStringStartToken || kind == SyntaxKind.InterpolatedStringEndToken)
                {
                    spans.Add(new ClassifiedSpan(token.SpanStart, token.Span.Length, TokenClass.String));
                }
                else if (kind == SyntaxKind.NumericLiteralToken)
                {
                    spans.Add(new ClassifiedSpan(token.SpanStart, token.Span.Length, TokenClass.Number));
                }
                else if (kind == SyntaxKind.IdentifierToken)
                {
                    var cls = ClassifyIdentifier(token, model, unityClasses);
                    if (cls.HasValue) spans.Add(new ClassifiedSpan(token.SpanStart, token.Span.Length, cls.Value));
                }

                AddTrivia(token.TrailingTrivia, spans);
            }
            return spans;
        }

        private static void AddTrivia(SyntaxTriviaList trivia, List<ClassifiedSpan> spans)
        {
            foreach (var t in trivia)
            {
                switch (t.Kind())
                {
                    case SyntaxKind.SingleLineCommentTrivia:
                    case SyntaxKind.MultiLineCommentTrivia:
                    case SyntaxKind.SingleLineDocumentationCommentTrivia:
                    case SyntaxKind.MultiLineDocumentationCommentTrivia:
                    case SyntaxKind.DisabledTextTrivia:
                        spans.Add(new ClassifiedSpan(t.SpanStart, t.Span.Length, TokenClass.Comment));
                        break;
                    default:
                        if (t.IsDirective) spans.Add(new ClassifiedSpan(t.SpanStart, t.Span.Length, TokenClass.Preprocessor));
                        break;
                }
            }
        }

        private static TokenClass? ClassifyIdentifier(SyntaxToken token, SemanticModel model, Dictionary<SyntaxNode, bool> unityClasses)
        {
            var parent = token.Parent;
            switch (parent)
            {
                case BaseTypeDeclarationSyntax _:
                case DelegateDeclarationSyntax _:
                case ConstructorDeclarationSyntax _:
                case DestructorDeclarationSyntax _:
                case TypeParameterSyntax _:
                    return TokenClass.Type;
                case MethodDeclarationSyntax method:
                    return IsUnityMessage(method, model, unityClasses) ? TokenClass.UnityMessage : TokenClass.Method;
                case LocalFunctionStatementSyntax _:
                    return TokenClass.Method;
                case PropertyDeclarationSyntax _:
                    return TokenClass.Property;
                case EventDeclarationSyntax _:
                    return TokenClass.Event;
                case EnumMemberDeclarationSyntax _:
                    return TokenClass.EnumMember;
                case ParameterSyntax _:
                    return TokenClass.Parameter;
                case VariableDeclaratorSyntax declarator:
                    var declaration = declarator.Parent?.Parent;
                    if (declaration is EventFieldDeclarationSyntax) return TokenClass.Event;
                    return declaration is FieldDeclarationSyntax ? TokenClass.Field : TokenClass.Local;
                case ForEachStatementSyntax _:
                case CatchDeclarationSyntax _:
                case SingleVariableDesignationSyntax _:
                case FromClauseSyntax _:
                case LetClauseSyntax _:
                    return TokenClass.Local;
                case IdentifierNameSyntax name when name.IsVar:
                    // 'var' is a contextual keyword: only a keyword when it doesn't bind to a type named var.
                    if (model == null) return TokenClass.Keyword;
                    var varSymbol = model.GetSymbolInfo(name).Symbol;
                    return varSymbol == null || varSymbol.Name != "var" ? TokenClass.Keyword : TokenClass.Type;
                case SimpleNameSyntax name:
                    return model == null ? (TokenClass?)null : ClassifySymbol(SymbolOf(model, name));
                default:
                    return null;
            }
        }

        private static ISymbol SymbolOf(SemanticModel model, SyntaxNode node)
        {
            var info = model.GetSymbolInfo(node);
            return info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
        }

        private static TokenClass? ClassifySymbol(ISymbol symbol)
        {
            switch (symbol)
            {
                case null: return null;
                case INamedTypeSymbol _:
                case ITypeParameterSymbol _:
                    return TokenClass.Type;
                case IMethodSymbol method:
                    return method.MethodKind == MethodKind.Constructor ? TokenClass.Type : TokenClass.Method;
                case IFieldSymbol field:
                    return field.ContainingType?.TypeKind == TypeKind.Enum ? TokenClass.EnumMember : TokenClass.Field;
                case IPropertySymbol _: return TokenClass.Property;
                case IEventSymbol _: return TokenClass.Event;
                case IParameterSymbol _: return TokenClass.Parameter;
                case ILocalSymbol _:
                case IRangeVariableSymbol _:
                    return TokenClass.Local;
                default: return null;
            }
        }

        private static bool IsUnityMessage(MethodDeclarationSyntax method, SemanticModel model, Dictionary<SyntaxNode, bool> unityClasses)
        {
            if (model == null || !UnityAnalyzers.Messages.ContainsKey(method.Identifier.ValueText)) return false;
            if (!(method.Parent is TypeDeclarationSyntax type)) return false;
            if (!unityClasses.TryGetValue(type, out bool isUnity))
            {
                isUnity = UnityAnalyzers.IsMonoBehaviour(model.GetDeclaredSymbol(type) as INamedTypeSymbol);
                unityClasses[type] = isUnity;
            }
            return isUnity;
        }
    }
}
