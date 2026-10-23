using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.Roslyn
{
    public enum ScriptMemberKind { Type, Field, AutoProperty, Method }

    public struct ScriptMember
    {
        /// <summary>Reflection name of the declaring type ("Game.Player", "Game.Player+State"), or of the type itself.</summary>
        public string TypeName;
        public string Name;
        public ScriptMemberKind Kind;
        public bool IsStatic;
        /// <summary>Method marked [Test], [UnityTest], [TestCase]... (NUnit / Unity Test Framework).</summary>
        public bool IsTest;
        public int Line; // 0-based line of the identifier
    }

    /// <summary>
    /// Members of a script that can be matched with runtime objects: types, fields and auto-properties (whose backing
    /// field can be read without running user code) and methods (UnityEvent targets). Generic types are skipped since
    /// their reflection names can't be instantiated meaningfully.
    /// </summary>
    public static class UnityScriptMembers
    {
        public static List<ScriptMember> Collect(SyntaxTree tree)
        {
            var result = new List<ScriptMember>();
            foreach (var type in tree.GetRoot().DescendantNodes(n => !(n is BlockSyntax || n is ArrowExpressionClauseSyntax)).OfType<TypeDeclarationSyntax>())
            {
                string typeName = ReflectionName(type);
                if (typeName == null) continue;

                result.Add(Make(tree, typeName, type.Identifier, ScriptMemberKind.Type, false));
                foreach (var member in type.Members)
                {
                    bool isStatic = member.Modifiers.Any(SyntaxKind.StaticKeyword);
                    switch (member)
                    {
                        case FieldDeclarationSyntax field when !field.Modifiers.Any(SyntaxKind.ConstKeyword):
                            foreach (var variable in field.Declaration.Variables)
                                result.Add(Make(tree, typeName, variable.Identifier, ScriptMemberKind.Field, isStatic));
                            break;
                        case PropertyDeclarationSyntax property when IsAutoProperty(property):
                            result.Add(Make(tree, typeName, property.Identifier, ScriptMemberKind.AutoProperty, isStatic));
                            break;
                        case MethodDeclarationSyntax method when method.TypeParameterList == null:
                            var entry = Make(tree, typeName, method.Identifier, ScriptMemberKind.Method, isStatic);
                            entry.IsTest = method.AttributeLists.SelectMany(l => l.Attributes).Any(a => s_TestAttributes.Contains(ShortName(a.Name)));
                            result.Add(entry);
                            break;
                    }
                }
            }
            return result;
        }

        private static readonly HashSet<string> s_TestAttributes = new HashSet<string>
        {
            "Test", "UnityTest", "TestCase", "TestCaseSource", "Theory", "Performance"
        };

        private static string ShortName(NameSyntax name)
        {
            string text = name is QualifiedNameSyntax q ? q.Right.Identifier.ValueText : name.ToString();
            return text.EndsWith("Attribute") ? text.Substring(0, text.Length - 9) : text;
        }

        private static bool IsAutoProperty(PropertyDeclarationSyntax property)
        {
            if (property.ExpressionBody != null || property.AccessorList == null) return false;
            var accessors = property.AccessorList.Accessors;
            return accessors.Count > 0 && accessors.All(a => a.Body == null && a.ExpressionBody == null) &&
                   !property.Modifiers.Any(SyntaxKind.AbstractKeyword) && !property.Modifiers.Any(SyntaxKind.ExternKeyword);
        }

        /// <summary>Namespace.Outer+Inner, or null for generic types (or types nested in one) and interfaces.</summary>
        public static string ReflectionName(TypeDeclarationSyntax type)
        {
            if (type is InterfaceDeclarationSyntax) return null;
            var names = new List<string>();
            SyntaxNode node = type;
            for (; node is TypeDeclarationSyntax t; node = node.Parent)
            {
                if (t.TypeParameterList != null) return null;
                names.Insert(0, t.Identifier.ValueText);
            }
            string name = string.Join("+", names);

            var namespaces = new List<string>();
            for (; node != null; node = node.Parent)
            {
                if (node is NamespaceDeclarationSyntax ns) namespaces.Insert(0, ns.Name.ToString().Replace(" ", ""));
            }
            return namespaces.Count == 0 ? name : string.Join(".", namespaces) + "." + name;
        }

        private static ScriptMember Make(SyntaxTree tree, string typeName, SyntaxToken identifier, ScriptMemberKind kind, bool isStatic)
        {
            return new ScriptMember
            {
                TypeName = typeName,
                Name = identifier.ValueText,
                Kind = kind,
                IsStatic = isStatic,
                Line = tree.GetLineSpan(identifier.Span).StartLinePosition.Line
            };
        }
    }
}
