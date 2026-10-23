using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.Roslyn
{
    public enum CodeSymbolKind { Class, Struct, Interface, Enum, Record, Delegate, Method, Constructor, Property, Field, Event, EnumMember }

    public struct CodeSymbol
    {
        public string Name;
        /// <summary>Enclosing type(s), e.g. "PlayerController" or "Outer.Inner"; empty for top-level types.</summary>
        public string Container;
        public CodeSymbolKind Kind;
        public string FilePath;
        public int Line;   // 0-based
        public int Column; // 0-based
    }

    /// <summary>
    /// Declarations found syntactically (no binding) for "Go to Symbol". Results are cached per syntax tree, and the
    /// project compilation hands out new trees only for edited files, so repeated queries are cheap.
    /// </summary>
    public static class SymbolIndex
    {
        private static readonly ConditionalWeakTable<SyntaxTree, List<CodeSymbol>> s_Cache = new ConditionalWeakTable<SyntaxTree, List<CodeSymbol>>();

        public static List<CodeSymbol> GetSymbols(SyntaxTree tree)
        {
            return s_Cache.GetValue(tree, Collect);
        }

        /// <summary>Symbols of an editor buffer that may not be part of any compilation.</summary>
        public static List<CodeSymbol> GetSymbols(string code, string filePath)
        {
            return Collect(CSharpSyntaxTree.ParseText(code ?? "", path: filePath ?? ""));
        }

        /// <summary>Declarations of every project file whose assembly compilation is built (others start building).</summary>
        public static List<CodeSymbol> GetWorkspaceSymbols()
        {
            var result = new List<CodeSymbol>();
            foreach (var tree in ProjectCompilationCache.GetProjectTrees()) result.AddRange(GetSymbols(tree));
            return result;
        }

        private static List<CodeSymbol> Collect(SyntaxTree tree)
        {
            var result = new List<CodeSymbol>();
            var root = tree.GetRoot();
            // Member bodies hold no declarations we list, so don't walk into them.
            foreach (var node in root.DescendantNodes(n => !(n is BlockSyntax || n is ArrowExpressionClauseSyntax || n is EqualsValueClauseSyntax)))
            {
                switch (node)
                {
                    case BaseTypeDeclarationSyntax type:
                        Add(result, tree, type.Identifier, KindOf(type), Container(type));
                        break;
                    case DelegateDeclarationSyntax d:
                        Add(result, tree, d.Identifier, CodeSymbolKind.Delegate, Container(d));
                        break;
                    case MethodDeclarationSyntax m:
                        Add(result, tree, m.Identifier, CodeSymbolKind.Method, Container(m));
                        break;
                    case ConstructorDeclarationSyntax c:
                        Add(result, tree, c.Identifier, CodeSymbolKind.Constructor, Container(c));
                        break;
                    case PropertyDeclarationSyntax p:
                        Add(result, tree, p.Identifier, CodeSymbolKind.Property, Container(p));
                        break;
                    case EventDeclarationSyntax e:
                        Add(result, tree, e.Identifier, CodeSymbolKind.Event, Container(e));
                        break;
                    case BaseFieldDeclarationSyntax f:
                        var kind = f is EventFieldDeclarationSyntax ? CodeSymbolKind.Event : CodeSymbolKind.Field;
                        foreach (var v in f.Declaration.Variables) Add(result, tree, v.Identifier, kind, Container(f));
                        break;
                    case EnumMemberDeclarationSyntax em:
                        Add(result, tree, em.Identifier, CodeSymbolKind.EnumMember, Container(em));
                        break;
                }
            }
            return result;
        }

        private static void Add(List<CodeSymbol> list, SyntaxTree tree, SyntaxToken identifier, CodeSymbolKind kind, string container)
        {
            if (identifier.IsMissing || string.IsNullOrEmpty(identifier.ValueText)) return;
            var position = tree.GetLineSpan(identifier.Span).StartLinePosition;
            list.Add(new CodeSymbol
            {
                Name = identifier.ValueText,
                Container = container,
                Kind = kind,
                FilePath = tree.FilePath,
                Line = position.Line,
                Column = position.Character
            });
        }

        private static CodeSymbolKind KindOf(BaseTypeDeclarationSyntax type)
        {
            switch (type)
            {
                case StructDeclarationSyntax _: return CodeSymbolKind.Struct;
                case InterfaceDeclarationSyntax _: return CodeSymbolKind.Interface;
                case EnumDeclarationSyntax _: return CodeSymbolKind.Enum;
                case RecordDeclarationSyntax _: return CodeSymbolKind.Record;
                default: return CodeSymbolKind.Class;
            }
        }

        private static string Container(SyntaxNode node)
        {
            string container = null;
            for (var parent = node.Parent; parent != null; parent = parent.Parent)
            {
                if (parent is BaseTypeDeclarationSyntax type)
                    container = container == null ? type.Identifier.ValueText : type.Identifier.ValueText + "." + container;
            }
            return container ?? "";
        }
    }
}
