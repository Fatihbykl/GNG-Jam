using System;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.Roslyn
{
    /// <summary>Unity serialization rules used to keep Inspector data alive across refactorings.</summary>
    public static class UnitySerialization
    {
        /// <summary>
        /// Whether Unity serializes this field: an instance field of a UnityEngine.Object subclass or a [Serializable]
        /// type that is public or [SerializeField]/[SerializeReference], and not readonly, const or [NonSerialized].
        /// </summary>
        public static bool IsSerializedField(IFieldSymbol field)
        {
            if (field == null || field.IsStatic || field.IsConst || field.IsReadOnly || field.IsImplicitlyDeclared) return false;
            if (HasAttribute(field, "NonSerializedAttribute")) return false;
            bool exposed = field.DeclaredAccessibility == Accessibility.Public ||
                           HasAttribute(field, "SerializeField") || HasAttribute(field, "SerializeReference");
            if (!exposed) return false;

            var type = field.ContainingType;
            if (type == null) return false;
            if (HasAttribute(type, "SerializableAttribute")) return true;
            for (var t = type.BaseType; t != null; t = t.BaseType)
            {
                if (t.Name == "Object" && t.ContainingNamespace?.ToDisplayString() == "UnityEngine") return true;
            }
            return false;
        }

        private static bool HasAttribute(ISymbol symbol, string name)
        {
            string shortName = name.EndsWith("Attribute", StringComparison.Ordinal) ? name.Substring(0, name.Length - 9) : name;
            return symbol.GetAttributes().Any(a => a.AttributeClass != null &&
                (a.AttributeClass.Name == name || a.AttributeClass.Name == shortName || a.AttributeClass.Name == shortName + "Attribute"));
        }

        /// <summary>
        /// Adds <c>[FormerlySerializedAs("oldName")]</c> above the field whose variable identifier is at
        /// <paramref name="identifierPosition"/>, plus <c>using UnityEngine.Serialization;</c> when missing. Returns
        /// null when nothing should change: the field declares several variables (the attribute would apply to all of
        /// them), or the attribute for <paramref name="oldName"/> is already there.
        /// </summary>
        public static string AddFormerlySerializedAs(string text, int identifierPosition, string oldName)
        {
            var tree = CSharpSyntaxTree.ParseText(text);
            var root = (CompilationUnitSyntax)tree.GetRoot();
            var declarator = root.FindToken(identifierPosition).Parent as VariableDeclaratorSyntax;
            var field = declarator?.Parent?.Parent as FieldDeclarationSyntax;
            if (field == null || field.Declaration.Variables.Count != 1) return null;

            bool alreadyThere = field.AttributeLists.SelectMany(l => l.Attributes).Any(a =>
                a.Name.ToString().EndsWith("FormerlySerializedAs", StringComparison.Ordinal) &&
                a.ArgumentList?.Arguments.FirstOrDefault()?.Expression is LiteralExpressionSyntax lit && lit.Token.ValueText == oldName);
            if (alreadyThere) return null;

            string newLine = text.Contains("\r\n") ? "\r\n" : "\n";
            int lineStart = text.LastIndexOf('\n', Math.Max(0, field.SpanStart - 1)) + 1;
            string indent = text.Substring(lineStart, field.SpanStart - lineStart);
            if (indent.Trim().Length != 0) indent = ""; // field doesn't start its line; keep the attribute inline

            string attribute = $"[FormerlySerializedAs(\"{oldName}\")]";
            string result = text.Insert(field.SpanStart, indent.Length > 0 || lineStart == field.SpanStart ? attribute + newLine + indent : attribute + " ");

            if (!HasSerializationUsing(root))
            {
                var usings = root.Usings;
                if (usings.Count > 0)
                {
                    int insertAt = usings.Last().Span.End;
                    result = result.Insert(insertAt, newLine + "using UnityEngine.Serialization;");
                }
                else
                {
                    result = "using UnityEngine.Serialization;" + newLine + result;
                }
            }
            return result;
        }

        private static bool HasSerializationUsing(CompilationUnitSyntax root)
        {
            bool Matches(UsingDirectiveSyntax u) => u.Alias == null && u.Name.ToString() == "UnityEngine.Serialization";
            return root.Usings.Any(Matches) ||
                   root.DescendantNodes(n => !(n is TypeDeclarationSyntax)).OfType<NamespaceDeclarationSyntax>().Any(ns => ns.Usings.Any(Matches));
        }
    }
}
