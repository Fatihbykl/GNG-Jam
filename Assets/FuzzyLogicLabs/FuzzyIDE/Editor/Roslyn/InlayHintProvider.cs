using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.Roslyn
{
    public struct InlayHint
    {
        /// <summary>Absolute position the hint is drawn before.</summary>
        public int Position;
        public string Text;

        public InlayHint(int position, string text)
        {
            Position = position;
            Text = text;
        }
    }

    /// <summary>
    /// Parameter names before literal arguments (<c>Move(speed: 5)</c>) and inferred types after <c>var</c> variables
    /// whose type isn't obvious from the initializer.
    /// </summary>
    public static class InlayHintProvider
    {
        private static readonly SymbolDisplayFormat s_TypeFormat = SymbolDisplayFormat.MinimallyQualifiedFormat;

        public static List<InlayHint> Compute(SyntaxNode root, SemanticModel model, bool parameterNames, bool varTypes, CancellationToken cancellationToken = default)
        {
            var hints = new List<InlayHint>();
            if (root == null || model == null || (!parameterNames && !varTypes)) return hints;

            foreach (var node in root.DescendantNodes())
            {
                if (cancellationToken.IsCancellationRequested) break;
                if (parameterNames && node is ArgumentListSyntax args) AddParameterHints(args, model, hints);
                else if (varTypes) AddTypeHint(node, model, hints);
            }
            hints.Sort((a, b) => a.Position.CompareTo(b.Position));
            return hints;
        }

        private static void AddParameterHints(ArgumentListSyntax args, SemanticModel model, List<InlayHint> hints)
        {
            if (!args.Arguments.Any(a => a.NameColon == null && IsLiteralLike(a.Expression))) return;
            if (!(model.GetSymbolInfo(args.Parent).Symbol is IMethodSymbol method)) return;
            // A single argument's meaning is clear from the method name (Debug.Log, SetActive...).
            if (method.Parameters.Length < 2) return;

            for (int i = 0; i < args.Arguments.Count; i++)
            {
                var argument = args.Arguments[i];
                if (argument.NameColon != null) break; // positional mapping ends at the first named argument
                if (!IsLiteralLike(argument.Expression)) continue;
                if (i >= method.Parameters.Length) break;
                var parameter = method.Parameters[i];
                if (parameter.IsParams) break;
                if (parameter.Name.Length < 2 || method.Name.EndsWith(parameter.Name, StringComparison.OrdinalIgnoreCase)) continue;
                hints.Add(new InlayHint(argument.SpanStart, parameter.Name + ":"));
            }
        }

        private static bool IsLiteralLike(ExpressionSyntax expression)
        {
            switch (expression)
            {
                case LiteralExpressionSyntax _: return true;
                case PrefixUnaryExpressionSyntax unary: return unary.Operand is LiteralExpressionSyntax;
                default: return false;
            }
        }

        private static void AddTypeHint(SyntaxNode node, SemanticModel model, List<InlayHint> hints)
        {
            switch (node)
            {
                case VariableDeclarationSyntax declaration when declaration.Type.IsVar && declaration.Variables.Count == 1:
                    var variable = declaration.Variables[0];
                    if (variable.Initializer == null || IsTypeApparent(variable.Initializer.Value)) return;
                    AddType(model.GetTypeInfo(declaration.Type).Type, variable.Identifier.Span.End, hints);
                    break;
                case ForEachStatementSyntax forEach when forEach.Type.IsVar:
                    AddType(model.GetTypeInfo(forEach.Type).Type, forEach.Identifier.Span.End, hints);
                    break;
                case DeclarationExpressionSyntax declarationExpression when declarationExpression.Type.IsVar &&
                                                                         declarationExpression.Designation is SingleVariableDesignationSyntax designation:
                    AddType(model.GetTypeInfo(declarationExpression.Type).Type, designation.Span.End, hints);
                    break;
            }
        }

        private static void AddType(ITypeSymbol type, int position, List<InlayHint> hints)
        {
            if (type == null || type.TypeKind == TypeKind.Error || type.IsAnonymousType) return;
            hints.Add(new InlayHint(position, ": " + type.ToDisplayString(s_TypeFormat)));
        }

        // new Foo(), (Foo)x, x as Foo, default(Foo), literals: the type is already on screen.
        private static bool IsTypeApparent(ExpressionSyntax value)
        {
            switch (value)
            {
                case ObjectCreationExpressionSyntax _:
                case ArrayCreationExpressionSyntax _:
                case CastExpressionSyntax _:
                case DefaultExpressionSyntax _:
                case LiteralExpressionSyntax _:
                case TypeOfExpressionSyntax _:
                    return true;
                case BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.AsExpression):
                    return true;
                case InvocationExpressionSyntax invocation when invocation.Expression is MemberAccessExpressionSyntax ma && ma.Name is GenericNameSyntax:
                    return true; // GetComponent<Rigidbody>()
                case InvocationExpressionSyntax invocation when invocation.Expression is GenericNameSyntax:
                    return true;
                default:
                    return false;
            }
        }
    }
}
