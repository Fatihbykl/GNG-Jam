using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.Roslyn
{
    public sealed class OverrideCandidate
    {
        public string Name;
        public string Signature;
        /// <summary>Snippet (VS Code syntax) for the whole member, modifiers included.</summary>
        public string Snippet;
    }

    /// <summary>
    /// "override |" in a class body: the base members that can still be overridden, with a stub that calls base
    /// (or throws for abstract members). The stub replaces everything typed from the member's first modifier.
    /// </summary>
    public static class OverrideCompletion
    {
        private static readonly SymbolDisplayFormat s_TypeFormat = SymbolDisplayFormat.MinimallyQualifiedFormat;

        /// <summary>
        /// Candidates when the caret follows the <c>override</c> keyword of a member being typed; null otherwise.
        /// <paramref name="replaceStart"/> is the position of the member's first modifier.
        /// </summary>
        public static List<OverrideCandidate> GetCandidates(SyntaxNode root, SemanticModel model, int caretPosition, out int replaceStart)
        {
            replaceStart = -1;
            if (root == null || model == null || caretPosition <= 0) return null;

            var overrideToken = root.FindToken(caretPosition - 1);
            // "override Fo|": step back over the partially typed name.
            if (overrideToken.IsKind(SyntaxKind.IdentifierToken)) overrideToken = overrideToken.GetPreviousToken();
            if (!overrideToken.IsKind(SyntaxKind.OverrideKeyword)) return null;

            var type = overrideToken.Parent?.FirstAncestorOrSelf<ClassDeclarationSyntax>();
            if (type == null || !(model.GetDeclaredSymbol(type) is INamedTypeSymbol typeSymbol)) return null;

            // The member under construction starts at its first modifier on the same line ("public override", "protected override").
            replaceStart = overrideToken.SpanStart;
            for (var previous = overrideToken.GetPreviousToken(); IsModifier(previous) && SameLine(previous, overrideToken); previous = previous.GetPreviousToken())
                replaceStart = previous.SpanStart;

            var alreadyOverridden = new HashSet<string>(typeSymbol.GetMembers().Where(m => m.IsOverride).Select(Key));
            var seen = new HashSet<string>();
            var result = new List<OverrideCandidate>();
            for (var baseType = typeSymbol.BaseType; baseType != null; baseType = baseType.BaseType)
            {
                foreach (var member in baseType.GetMembers())
                {
                    if (member is IMethodSymbol m && m.MethodKind != MethodKind.Ordinary) continue;
                    if (!(member is IMethodSymbol || member is IPropertySymbol)) continue;
                    // The most derived declaration decides (a sealed override hides the virtual it overrides);
                    // anything this class overrides already isn't offered again.
                    string key = Key(member);
                    if (!seen.Add(key) || alreadyOverridden.Contains(key)) continue;
                    if (!(member.IsVirtual || member.IsAbstract || member.IsOverride) || member.IsSealed || member.IsStatic) continue;
                    if (member.DeclaredAccessibility == Microsoft.CodeAnalysis.Accessibility.Private || member.DeclaredAccessibility == Microsoft.CodeAnalysis.Accessibility.Internal && !SymbolEqualityComparer.Default.Equals(member.ContainingAssembly, typeSymbol.ContainingAssembly)) continue;
                    result.Add(Build(member));
                }
            }
            return result.OrderBy(c => c.Name).ToList();
        }

        private static bool IsModifier(SyntaxToken token)
        {
            switch (token.Kind())
            {
                case SyntaxKind.PublicKeyword:
                case SyntaxKind.ProtectedKeyword:
                case SyntaxKind.InternalKeyword:
                case SyntaxKind.PrivateKeyword:
                case SyntaxKind.SealedKeyword:
                case SyntaxKind.AsyncKeyword:
                case SyntaxKind.UnsafeKeyword:
                    return true;
                default:
                    return false;
            }
        }

        private static bool SameLine(SyntaxToken a, SyntaxToken b)
        {
            var tree = a.SyntaxTree;
            return tree.GetLineSpan(a.Span).StartLinePosition.Line == tree.GetLineSpan(b.Span).StartLinePosition.Line;
        }

        private static string Key(ISymbol member)
        {
            if (member is IMethodSymbol method)
                return method.Name + "(" + string.Join(",", method.Parameters.Select(p => p.Type.ToDisplayString())) + ")";
            return member.Name;
        }

        private static string AccessText(ISymbol member)
        {
            switch (member.DeclaredAccessibility)
            {
                case Microsoft.CodeAnalysis.Accessibility.Public: return "public";
                case Microsoft.CodeAnalysis.Accessibility.Protected: return "protected";
                case Microsoft.CodeAnalysis.Accessibility.Internal: return "internal";
                case Microsoft.CodeAnalysis.Accessibility.ProtectedOrInternal: return "protected internal";
                case Microsoft.CodeAnalysis.Accessibility.ProtectedAndInternal: return "private protected";
                default: return "public";
            }
        }

        private static OverrideCandidate Build(ISymbol member)
        {
            string access = AccessText(member);
            if (member is IMethodSymbol method)
            {
                string returnType = method.ReturnsVoid ? "void" : method.ReturnType.ToDisplayString(s_TypeFormat);
                string parameters = string.Join(", ", method.Parameters.Select(Parameter));
                string arguments = string.Join(", ", method.Parameters.Select(Argument));
                string body = method.IsAbstract
                    ? "throw new System.NotImplementedException();"
                    : (method.ReturnsVoid ? "" : "return ") + "base." + method.Name + "(" + arguments + ");";
                string signature = $"{access} override {returnType} {method.Name}({parameters})";
                return new OverrideCandidate
                {
                    Name = method.Name,
                    Signature = signature,
                    Snippet = Escape(signature) + "\n{\n\t" + Escape(body) + "$0\n}"
                };
            }

            var property = (IPropertySymbol)member;
            string propertyType = property.Type.ToDisplayString(s_TypeFormat);
            string getter = property.GetMethod == null ? null : property.IsAbstract ? "get => throw new System.NotImplementedException();" : $"get => base.{property.Name};";
            string setter = property.SetMethod == null ? null : property.IsAbstract ? "set => throw new System.NotImplementedException();" : $"set => base.{property.Name} = value;";
            string accessors = string.Join(" ", new[] { getter, setter }.Where(a => a != null));
            string propertySignature = $"{access} override {propertyType} {property.Name}";
            return new OverrideCandidate
            {
                Name = property.Name,
                Signature = propertySignature,
                Snippet = Escape(propertySignature) + " { " + Escape(accessors) + " }$0"
            };
        }

        private static string Parameter(IParameterSymbol p)
        {
            string modifier = p.RefKind == RefKind.Ref ? "ref " : p.RefKind == RefKind.Out ? "out " : p.RefKind == RefKind.In ? "in " : p.IsParams ? "params " : "";
            return modifier + p.Type.ToDisplayString(s_TypeFormat) + " " + p.Name;
        }

        private static string Argument(IParameterSymbol p)
        {
            string modifier = p.RefKind == RefKind.Ref ? "ref " : p.RefKind == RefKind.Out ? "out " : p.RefKind == RefKind.In ? "in " : "";
            return modifier + p.Name;
        }

        // Snippet syntax treats $, } and \ specially.
        private static string Escape(string text) => text.Replace("\\", "\\\\").Replace("$", "\\$");
    }
}
