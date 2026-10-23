using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Rename;
using FuzzyLogicLabs.FuzzyIDE.Editor.Core;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.Roslyn
{
    public class FileRenameChange
    {
        public string FilePath;
        public string NewText;
    }

    public class ReferenceResult
    {
        public string FilePath;
        public int LineNumber;
        public int ColumnNumber;
        public string CodeSnippet;
    }

    public static class RoslynProjectExtensions
    {
        private static Microsoft.CodeAnalysis.Host.HostServices s_HostServices;

        // MEF composition is expensive (hundreds of ms); build it once per domain.
        private static Microsoft.CodeAnalysis.Host.HostServices GetHostServices()
        {
            if (s_HostServices != null) return s_HostServices;

            var assemblies = new List<System.Reflection.Assembly>();
            string[] assemblyNames = new[]
            {
                "Microsoft.CodeAnalysis",
                "Microsoft.CodeAnalysis.CSharp",
                "Microsoft.CodeAnalysis.Workspaces",
                "Microsoft.CodeAnalysis.CSharp.Workspaces",
                "Microsoft.CodeAnalysis.Features",
                "Microsoft.CodeAnalysis.CSharp.Features"
            };
            foreach (var name in assemblyNames)
            {
                try { assemblies.Add(System.Reflection.Assembly.Load(name)); } catch { }
            }
            s_HostServices = Microsoft.CodeAnalysis.Host.Mef.MefHostServices.Create(assemblies);
            return s_HostServices;
        }

        public static AdhocWorkspace CreateWorkspace(Dictionary<string, string> allFiles)
        {
            var workspace = new AdhocWorkspace(GetHostServices());

            var projectId = ProjectId.CreateNewId();
            var projectInfo = ProjectInfo.Create(projectId, VersionStamp.Default, "VirtualIDEAssembly", "VirtualIDEAssembly", LanguageNames.CSharp);
            workspace.AddProject(projectInfo);

            var references = new List<MetadataReference>();
            try
            {
                references.Add(MetadataReference.CreateFromFile(UnityCompat.GetAssemblyPath(typeof(object).Assembly)));
                references.Add(MetadataReference.CreateFromFile(UnityCompat.GetAssemblyPath(typeof(UnityEngine.GameObject).Assembly)));
                references.Add(MetadataReference.CreateFromFile(UnityCompat.GetAssemblyPath(typeof(UnityEngine.Debug).Assembly)));
            } catch {}

            var project = workspace.CurrentSolution.GetProject(projectId).AddMetadataReferences(references);
            workspace.TryApplyChanges(project.Solution);

            foreach (var kvp in allFiles)
            {
                var docId = DocumentId.CreateNewId(projectId);
                var docInfo = DocumentInfo.Create(docId, System.IO.Path.GetFileName(kvp.Key), filePath: kvp.Key, loader: TextLoader.From(TextAndVersion.Create(Microsoft.CodeAnalysis.Text.SourceText.From(kvp.Value), VersionStamp.Default)));
                workspace.AddDocument(docInfo);
            }

            return workspace;
        }

        /// <summary>
        /// Solution + document for a refactoring. Files of a Unity script assembly use the project-wide solution from
        /// <see cref="ProjectCompilationCache"/> (real references, one project per asmdef; <paramref name="files"/> only
        /// needs to hold unsaved buffers). Anything else falls back to a single ad-hoc project over <paramref name="files"/>.
        /// </summary>
        private static (Solution solution, Document document) OpenSolution(string currentFilePath, Dictionary<string, string> files)
        {
            string fullPath = ProjectCompilationCache.NormalizePath(currentFilePath);
            if (ProjectCompilationCache.ContainsFile(fullPath))
            {
                var overlays = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var kvp in files) overlays[ProjectCompilationCache.NormalizePath(kvp.Key)] = kvp.Value;

                var projectSolution = ProjectCompilationCache.CreateSolution(GetHostServices(), overlays, fullPath);
                var docId = projectSolution.GetDocumentIdsWithFilePath(fullPath).FirstOrDefault();
                if (docId != null) return (projectSolution, projectSolution.GetDocument(docId));
            }

            var solution = CreateWorkspace(files).CurrentSolution;
            var document = solution.Projects.First().Documents.FirstOrDefault(d => d.FilePath == currentFilePath);
            return (solution, document);
        }

        // Callers key open files by project-relative asset path ("Assets/..."), the project solution uses full paths.
        private static string ToCallerPath(string path) => Core.ProjectDiagnosticsService.NormalizeAssetPath(path);

        /// <param name="addFormerlySerializedAs">Renaming a Unity-serialized field also adds [FormerlySerializedAs("old")].</param>
        public static Task<List<FileRenameChange>> RenameSymbolAsync(string currentFilePath, int caretPosition, string newName, Dictionary<string, string> allFiles, bool addFormerlySerializedAs = false)
        {
            return Task.Run(async () =>
            {
                try
                {
                    var (solution, document) = OpenSolution(currentFilePath, allFiles);
                    if (document == null) return new List<FileRenameChange>();

                    var symbol= await SymbolFinder.FindSymbolAtPositionAsync(document, caretPosition);
                    if (symbol == null && caretPosition > 0)
                    {
                        symbol = await SymbolFinder.FindSymbolAtPositionAsync(document, caretPosition - 1);
                    }

                    // UnityEngine.Debug.Log($"[FuzzyIDE] Found symbol for rename: {(symbol != null ? symbol.Name : "NULL")} at pos: {caretPosition}");

                    if (symbol == null) return new List<FileRenameChange>();

#pragma warning disable 0618
                    var newSolution = await Renamer.RenameSymbolAsync(solution, symbol, newName, solution.Workspace.Options);
#pragma warning restore 0618

                    if (addFormerlySerializedAs && symbol is IFieldSymbol field && field.Name != newName && UnitySerialization.IsSerializedField(field))
                        newSolution = await AddFormerlySerializedAsAsync(solution, newSolution, field);
                    
                    var changes = new List<FileRenameChange>();
                    foreach (var projectChanges in newSolution.GetChanges(solution).GetProjectChanges())
                    {
                        foreach (var changedDocId in projectChanges.GetChangedDocuments())
                        {
                            var newDoc = newSolution.GetDocument(changedDocId);
                            var text = await newDoc.GetTextAsync();
                            changes.Add(new FileRenameChange { FilePath = ToCallerPath(newDoc.FilePath), NewText = text.ToString() });
                        }
                    }

                    return changes;
                }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogError($"[FuzzyIDE] Rename failed with exception: {ex}");
                    return new List<FileRenameChange>();
                }
            });
        }

        // The renamed solution moved text around, so map the declarator's old position through the document's changes.
        private static async Task<Solution> AddFormerlySerializedAsAsync(Solution before, Solution after, IFieldSymbol field)
        {
            var syntaxRef = field.DeclaringSyntaxReferences.FirstOrDefault();
            if (syntaxRef == null || !(syntaxRef.GetSyntax() is Microsoft.CodeAnalysis.CSharp.Syntax.VariableDeclaratorSyntax declarator)) return after;
            var documentId = before.GetDocumentId(syntaxRef.SyntaxTree);
            if (documentId == null || after.GetDocument(documentId) == null) return after;

            int position = declarator.Identifier.SpanStart;
            var textChanges = await after.GetDocument(documentId).GetTextChangesAsync(before.GetDocument(documentId));
            int shift = textChanges.Where(c => c.Span.End <= position).Sum(c => c.NewText.Length - c.Span.Length);

            string text = (await after.GetDocument(documentId).GetTextAsync()).ToString();
            string updated = UnitySerialization.AddFormerlySerializedAs(text, position + shift, field.Name);
            return updated == null ? after : after.WithDocumentText(documentId, Microsoft.CodeAnalysis.Text.SourceText.From(updated));
        }

        public static Task<List<ReferenceResult>> FindReferencesAsync(string currentFilePath, int caretPosition, Dictionary<string, string> allFiles)
        {
            return Task.Run(async () =>
            {
                var (solution, document) = OpenSolution(currentFilePath, allFiles);
            if (document == null) return new List<ReferenceResult>();

            var symbol= await SymbolFinder.FindSymbolAtPositionAsync(document, caretPosition);
            if (symbol == null && caretPosition > 0)
            {
                symbol = await SymbolFinder.FindSymbolAtPositionAsync(document, caretPosition - 1);
            }

            if (symbol == null) return new List<ReferenceResult>();

            var references = await SymbolFinder.FindReferencesAsync(symbol, solution);
            
            var results = new List<ReferenceResult>();
            foreach (var reference in references)
            {
                foreach (var location in reference.Locations)
                {
                    var locDoc = location.Document;
                    var text = await locDoc.GetTextAsync();
                    var linePosition = text.Lines.GetLinePosition(location.Location.SourceSpan.Start);
                    var lineText = text.Lines[linePosition.Line].ToString();

                    results.Add(new ReferenceResult
                    {
                        FilePath = ToCallerPath(locDoc.FilePath),
                        LineNumber = linePosition.Line + 1,
                        ColumnNumber = linePosition.Character + 1,
                        CodeSnippet = lineText.Trim()
                    });
                }
            }

                return results;
            });
        }
        
        public static Task<FileRenameChange> InjectWatchLiveAsync(string currentFilePath, int caretPosition, Dictionary<string, string> allFiles)
        {
            return Task.Run(async () =>
            {
                var workspace = CreateWorkspace(allFiles);
                var solution = workspace.CurrentSolution;
                var documentId = solution.GetProject(workspace.CurrentSolution.ProjectIds.First()).Documents.FirstOrDefault(d => d.FilePath == currentFilePath)?.Id;
                if (documentId == null) 
                {
                    UnityEngine.Debug.LogWarning("[FuzzyIDE] InjectWatchLiveAsync: documentId is null for path " + currentFilePath);
                    return null;
                }

                var document = solution.GetDocument(documentId);
                var root = await document.GetSyntaxRootAsync();
                var syntaxToken = root.FindToken(caretPosition);
                
                var node = syntaxToken.Parent;
                // UnityEngine.Debug.Log($"[FuzzyIDE] InjectWatchLiveAsync: Found token '{syntaxToken.Text}' of kind {syntaxToken.Kind()} at {caretPosition}. Parent: {node?.GetType().Name}");
                
                Microsoft.CodeAnalysis.CSharp.Syntax.StatementSyntax statementToInsertAfter = null;
                string variableName = syntaxToken.ValueText;
                
                while (node != null)
                {
                    if (node is Microsoft.CodeAnalysis.CSharp.Syntax.StatementSyntax stmt)
                    {
                        statementToInsertAfter = stmt;
                        break;
                    }
                    node = node.Parent;
                }
                
                if (statementToInsertAfter == null) 
                {
                    UnityEngine.Debug.LogWarning($"[FuzzyIDE] InjectWatchLiveAsync: statementToInsertAfter is null. Walked up to root without finding StatementSyntax.");
                    return null;
                }

                var lineSpan = statementToInsertAfter.SyntaxTree.GetLineSpan(statementToInsertAfter.Span);
                int lineNumber = lineSpan.StartLinePosition.Line + 1;
                string trackId = $"{System.IO.Path.GetFileName(currentFilePath)}_{lineNumber}";
                
                string escapedPath = currentFilePath.Replace("\\", "\\\\");
                var leadingTrivia = statementToInsertAfter.GetLeadingTrivia();
                var trackStatement = Microsoft.CodeAnalysis.CSharp.SyntaxFactory.ParseStatement($"FuzzyLogicLabs.LiveStateTracker.Track(\"{trackId}\", \"{escapedPath}\", \"{variableName}\", {variableName});\n")
                    .WithLeadingTrivia(leadingTrivia)
                    .WithTrailingTrivia(Microsoft.CodeAnalysis.CSharp.SyntaxFactory.CarriageReturnLineFeed);
                
                var rewriter = new WatchLiveRewriter(statementToInsertAfter, trackStatement);
                var newRoot = rewriter.Visit(root);
                
                if (newRoot == root || newRoot.IsEquivalentTo(root))
                {
                    UnityEngine.Debug.LogWarning("[FuzzyIDE] InjectWatchLiveAsync: WatchLiveRewriter failed to rewrite the syntax tree. The new root is equivalent to the old root.");
                    return null;
                }
                
                return new FileRenameChange { FilePath = currentFilePath, NewText = newRoot.ToFullString() };
            });
        }
        
        class WatchLiveRewriter : Microsoft.CodeAnalysis.CSharp.CSharpSyntaxRewriter
        {
            private Microsoft.CodeAnalysis.CSharp.Syntax.StatementSyntax targetStatement;
            private Microsoft.CodeAnalysis.CSharp.Syntax.StatementSyntax trackStatement;

            public WatchLiveRewriter(Microsoft.CodeAnalysis.CSharp.Syntax.StatementSyntax target, Microsoft.CodeAnalysis.CSharp.Syntax.StatementSyntax track)
            {
                targetStatement = target;
                trackStatement = track;
            }

            public override SyntaxNode Visit(SyntaxNode node)
            {
                if (node == targetStatement)
                {
                    var visited = base.Visit(node);
                    if (node.Parent is Microsoft.CodeAnalysis.CSharp.Syntax.BlockSyntax || node.Parent is Microsoft.CodeAnalysis.CSharp.Syntax.SwitchSectionSyntax || node.Parent is Microsoft.CodeAnalysis.CSharp.Syntax.GlobalStatementSyntax)
                    {
                        return visited;
                    }
                    else
                    {
                        return Microsoft.CodeAnalysis.CSharp.SyntaxFactory.Block((Microsoft.CodeAnalysis.CSharp.Syntax.StatementSyntax)visited, trackStatement);
                    }
                }
                return base.Visit(node);
            }

            public override SyntaxNode VisitBlock(Microsoft.CodeAnalysis.CSharp.Syntax.BlockSyntax node)
            {
                var newNode = (Microsoft.CodeAnalysis.CSharp.Syntax.BlockSyntax)base.VisitBlock(node);
                var statements = newNode.Statements;
                for (int i = 0; i < node.Statements.Count; i++)
                {
                    if (node.Statements[i] == targetStatement)
                    {
                        var newStatements = statements.Insert(i + 1, trackStatement);
                        return newNode.WithStatements(newStatements);
                    }
                }
                return newNode;
            }
            
            public override SyntaxNode VisitSwitchSection(Microsoft.CodeAnalysis.CSharp.Syntax.SwitchSectionSyntax node)
            {
                var newNode = (Microsoft.CodeAnalysis.CSharp.Syntax.SwitchSectionSyntax)base.VisitSwitchSection(node);
                var statements = newNode.Statements;
                for (int i = 0; i < node.Statements.Count; i++)
                {
                    if (node.Statements[i] == targetStatement)
                    {
                        var newStatements = statements.Insert(i + 1, trackStatement);
                        return newNode.WithStatements(newStatements);
                    }
                }
                return newNode;
            }
        }
        
        public static Task<FileRenameChange> RemoveWatchLiveAsync(string currentFilePath, string trackId, Dictionary<string, string> allFiles)
        {
            return Task.Run(async () =>
            {
                var workspace = CreateWorkspace(allFiles);
                var solution = workspace.CurrentSolution;
                var documentId = solution.GetProject(workspace.CurrentSolution.ProjectIds.First()).Documents.FirstOrDefault(d => d.FilePath != null && (d.FilePath == currentFilePath || d.FilePath.EndsWith(currentFilePath)))?.Id;
                if (documentId == null) return null;

                var document = solution.GetDocument(documentId);
                var root = await document.GetSyntaxRootAsync();
                
                var invocations = root.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax>();
                Microsoft.CodeAnalysis.CSharp.Syntax.StatementSyntax statementToRemove = null;
                
                foreach (var inv in invocations)
                {
                    if (inv.Expression.ToString().Contains("LiveStateTracker.Track"))
                    {
                        var args = inv.ArgumentList.Arguments;
                        if (args.Count > 0 && args[0].Expression.ToString() == $"\"{trackId}\"")
                        {
                            var node = inv.Parent;
                            while (node != null)
                            {
                                if (node is Microsoft.CodeAnalysis.CSharp.Syntax.StatementSyntax stmt)
                                {
                                    statementToRemove = stmt;
                                    break;
                                }
                                node = node.Parent;
                            }
                            break;
                        }
                    }
                }
                
                if (statementToRemove == null) return null;
                
                var newRoot = root.RemoveNode(statementToRemove, SyntaxRemoveOptions.KeepNoTrivia);
                return new FileRenameChange { FilePath = document.FilePath, NewText = newRoot.ToFullString() };
            });
        }
    }
}
