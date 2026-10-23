using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FuzzyLogicLabs.FuzzyIDE.Editor.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using UnityEditor;
using UnityEditor.Compilation;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.Roslyn
{
    /// <summary>
    /// Keeps one long-lived <see cref="CSharpCompilation"/> per Unity script assembly so analysing an open file only
    /// swaps that file's syntax tree (reusing bound metadata and declarations) instead of building a fresh
    /// compilation on every keystroke. Sources, defines and references come from <see cref="CompilationPipeline"/>,
    /// so asmdef boundaries match Unity's own build and unsaved edits are visible to the other files of the assembly.
    /// </summary>
    [InitializeOnLoad]
    public static class ProjectCompilationCache
    {
        private sealed class AssemblyDescriptor
        {
            public string Name;
            public string[] SourceFiles;
            public string[] References;
            public string[] Defines;
            public string[] ReferencedAssemblies;
            public bool AllowUnsafe;

            public CSharpParseOptions ParseOptions => CSharpParseOptions.Default
                .WithLanguageVersion(LanguageVersion.CSharp9)
                .WithPreprocessorSymbols(Defines);

            public CSharpCompilationOptions CompilationOptions =>
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: AllowUnsafe);

            public bool SameAs(AssemblyDescriptor other)
            {
                return other != null && Name == other.Name && AllowUnsafe == other.AllowUnsafe &&
                       SourceFiles.SequenceEqual(other.SourceFiles, StringComparer.OrdinalIgnoreCase) &&
                       ReferencedAssemblies.SequenceEqual(other.ReferencedAssemblies) &&
                       References.SequenceEqual(other.References, StringComparer.OrdinalIgnoreCase) &&
                       Defines.SequenceEqual(other.Defines);
            }
        }

        private sealed class AssemblyEntry
        {
            public readonly AssemblyDescriptor Descriptor;
            public readonly object Gate = new object();
            // Trees of a replaced entry (they may hold unsaved buffer text), reused when this entry is built.
            public Dictionary<string, SyntaxTree> SeedTrees;
            public Dictionary<string, SyntaxTree> Trees;
            public CSharpCompilation Compilation;
            public Task BuildTask;

            public AssemblyEntry(AssemblyDescriptor descriptor) { Descriptor = descriptor; }
            public bool IsReady => Compilation != null;
        }

        private static readonly object s_Gate = new object();
        private static Dictionary<string, AssemblyEntry> s_EntriesByName = new Dictionary<string, AssemblyEntry>();
        private static Dictionary<string, AssemblyEntry> s_EntriesByFile = new Dictionary<string, AssemblyEntry>(StringComparer.OrdinalIgnoreCase);

        private static readonly ConcurrentDictionary<string, MetadataReference> s_MetadataReferences =
            new ConcurrentDictionary<string, MetadataReference>(StringComparer.OrdinalIgnoreCase);

        private static readonly ConcurrentQueue<string> s_ReadyAssemblies = new ConcurrentQueue<string>();

        /// <summary>Raised on the main thread when an assembly's compilation finished building (argument: assembly name).</summary>
        public static event Action<string> AssemblyReady;

        static ProjectCompilationCache()
        {
            try
            {
                EditorApplication.delayCall += RefreshDescriptors;
                EditorApplication.update += DispatchReadyAssemblies;
                CompilationPipeline.compilationFinished += _ => RefreshDescriptors();
                FileWatcherService.OnFilesChangedBatch += OnFilesChanged;
            }
            catch
            {
                // Outside the editor (pure-logic test harness) the cache simply stays empty.
            }
        }

        public static string NormalizePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            try { return Path.GetFullPath(path); }
            catch { return path; }
        }

        /// <summary>
        /// Re-reads the assembly layout from Unity. Must run on the main thread. Entries whose inputs did not change
        /// are kept warm; changed ones are rebuilt lazily on the next request.
        /// </summary>
        public static void RefreshDescriptors()
        {
            UnityEditor.Compilation.Assembly[] assemblies;
            string[] frameworkReferences;
            try
            {
                assemblies = CompilationPipeline.GetAssemblies(AssembliesType.Editor);
                frameworkReferences = GetFrameworkReferences();
            }
            catch
            {
                return;
            }

            var byName = new Dictionary<string, AssemblyEntry>();
            var existingFiles = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            var byFile = new Dictionary<string, AssemblyEntry>(StringComparer.OrdinalIgnoreCase);

            lock (s_Gate)
            {
                foreach (var asm in assemblies)
                {
                    if (asm == null || string.IsNullOrEmpty(asm.name) || byName.ContainsKey(asm.name)) continue;

                    var descriptor = new AssemblyDescriptor
                    {
                        Name = asm.name,
                        SourceFiles = (asm.sourceFiles ?? new string[0]).Select(NormalizePath).ToArray(),
                        References = BuildReferenceList(asm.allReferences, frameworkReferences, existingFiles),
                        Defines = asm.defines ?? new string[0],
                        ReferencedAssemblies = (asm.assemblyReferences ?? new UnityEditor.Compilation.Assembly[0])
                            .Where(r => r != null).Select(r => r.name).ToArray(),
                        AllowUnsafe = asm.compilerOptions.AllowUnsafeCode
                    };

                    AssemblyEntry entry;
                    if (s_EntriesByName.TryGetValue(descriptor.Name, out var previous) && previous.Descriptor.SameAs(descriptor))
                    {
                        entry = previous;
                    }
                    else
                    {
                        entry = new AssemblyEntry(descriptor);
                        if (previous != null)
                        {
                            lock (previous.Gate)
                            {
                                entry.SeedTrees = previous.Trees ?? previous.SeedTrees;
                            }
                        }
                    }

                    byName[descriptor.Name] = entry;
                    foreach (var file in descriptor.SourceFiles)
                    {
                        if (!byFile.ContainsKey(file)) byFile[file] = entry;
                    }
                }

                s_EntriesByName = byName;
                s_EntriesByFile = byFile;
            }
        }

        /// <summary>True when <paramref name="filePath"/> belongs to a known script assembly.</summary>
        public static bool ContainsFile(string filePath)
        {
            lock (s_Gate) return s_EntriesByFile.ContainsKey(NormalizePath(filePath));
        }

        /// <summary>
        /// Pushes the editor buffer of <paramref name="filePath"/> into its assembly's compilation and returns the
        /// resulting tree/compilation. Returns false (and starts a background build) while the assembly is not ready,
        /// or when the file is not part of any script assembly.
        /// </summary>
        public static bool TryUpdateDocument(string filePath, string code, out SyntaxTree tree, out CSharpCompilation compilation)
        {
            tree = null;
            compilation = null;

            string key = NormalizePath(filePath);
            if (string.IsNullOrEmpty(key)) return false;

            AssemblyEntry entry;
            lock (s_Gate)
            {
                if (!s_EntriesByFile.TryGetValue(key, out entry)) return false;
            }

            if (!entry.IsReady)
            {
                EnsureBuildStarted(entry);
                return false;
            }

            var newText = SourceText.From(code ?? string.Empty);
            lock (entry.Gate)
            {
                if (!entry.Trees.TryGetValue(key, out var oldTree)) return false;

                tree = ReplaceText(entry, key, oldTree, newText);
                compilation = entry.Compilation;
                return true;
            }
        }

        /// <summary>Current compilation of the assembly that owns <paramref name="filePath"/>, if it is built.</summary>
        public static bool TryGetCompilation(string filePath, out CSharpCompilation compilation)
        {
            compilation = null;
            AssemblyEntry entry;
            lock (s_Gate)
            {
                if (!s_EntriesByFile.TryGetValue(NormalizePath(filePath), out entry)) return false;
            }

            if (!entry.IsReady)
            {
                EnsureBuildStarted(entry);
                return false;
            }

            lock (entry.Gate) compilation = entry.Compilation;
            return true;
        }

        /// <summary>
        /// Builds a workspace solution for rename / find references: one project per script assembly of the project
        /// itself (immutable PackageCache assemblies are left out unless they own <paramref name="includeFile"/>),
        /// linked by project references so cross-asmdef usages are found. Text comes from <paramref name="bufferOverlays"/>
        /// (unsaved editor buffers, keyed by full path), then the cached trees, then disk.
        /// </summary>
        public static Solution CreateSolution(Microsoft.CodeAnalysis.Host.HostServices hostServices,
            IReadOnlyDictionary<string, string> bufferOverlays, string includeFile)
        {
            List<AssemblyEntry> entries;
            lock (s_Gate) entries = s_EntriesByName.Values.ToList();

            string includeKey = NormalizePath(includeFile);
            var included = entries.Where(e => e.Descriptor.SourceFiles.Any(f =>
                    !f.Replace('\\', '/').Contains("/Library/PackageCache/") ||
                    string.Equals(f, includeKey, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            var projectIds = included.ToDictionary(e => e.Descriptor.Name, e => ProjectId.CreateNewId(e.Descriptor.Name));

            var projects = new List<ProjectInfo>();
            foreach (var entry in included)
            {
                var descriptor = entry.Descriptor;
                var projectId = projectIds[descriptor.Name];

                Dictionary<string, SyntaxTree> trees = null;
                if (entry.IsReady)
                {
                    lock (entry.Gate) trees = new Dictionary<string, SyntaxTree>(entry.Trees, StringComparer.OrdinalIgnoreCase);
                }

                var documents = new List<DocumentInfo>();
                foreach (var file in descriptor.SourceFiles)
                {
                    SourceText text;
                    if (bufferOverlays != null && bufferOverlays.TryGetValue(file, out var buffer)) text = SourceText.From(buffer);
                    else if (trees != null && trees.TryGetValue(file, out var tree)) text = tree.GetText();
                    else
                    {
                        try { text = SourceText.From(File.ReadAllText(file)); }
                        catch { continue; }
                    }

                    documents.Add(DocumentInfo.Create(DocumentId.CreateNewId(projectId), Path.GetFileName(file), filePath: file,
                        loader: TextLoader.From(TextAndVersion.Create(text, VersionStamp.Create(), file))));
                }

                var projectReferences = descriptor.ReferencedAssemblies.Where(projectIds.ContainsKey)
                    .Select(n => new ProjectReference(projectIds[n])).ToList();
                // Referenced assemblies that are projects in this solution must not also come in as compiled DLLs.
                var projectOutputs = new HashSet<string>(descriptor.ReferencedAssemblies.Where(projectIds.ContainsKey)
                    .Select(n => n + ".dll"), StringComparer.OrdinalIgnoreCase);
                var metadataReferences = new List<MetadataReference>();
                foreach (var path in descriptor.References)
                {
                    if (projectOutputs.Contains(Path.GetFileName(path))) continue;
                    try { metadataReferences.Add(GetMetadataReference(path)); } catch { }
                }

                projects.Add(ProjectInfo.Create(projectId, VersionStamp.Create(), descriptor.Name, descriptor.Name, LanguageNames.CSharp,
                    compilationOptions: descriptor.CompilationOptions, parseOptions: descriptor.ParseOptions,
                    documents: documents, projectReferences: projectReferences, metadataReferences: metadataReferences));
            }

            var workspace = new AdhocWorkspace(hostServices);
            workspace.AddSolution(SolutionInfo.Create(SolutionId.CreateNewId(), VersionStamp.Create(), projects: projects));
            return workspace.CurrentSolution;
        }

        /// <summary>
        /// Syntax trees of the project's own assemblies (not immutable PackageCache ones) that are built; the rest start
        /// building in the background and show up in a later call.
        /// </summary>
        public static List<SyntaxTree> GetProjectTrees()
        {
            List<AssemblyEntry> entries;
            lock (s_Gate) entries = s_EntriesByName.Values.ToList();

            var trees = new List<SyntaxTree>();
            foreach (var entry in entries)
            {
                if (entry.Descriptor.SourceFiles.All(f => f.Replace('\\', '/').Contains("/Library/PackageCache/"))) continue;
                if (!entry.IsReady)
                {
                    EnsureBuildStarted(entry);
                    continue;
                }
                lock (entry.Gate) trees.AddRange(entry.Trees.Values);
            }
            return trees;
        }

        /// <summary>Drops unsaved buffer text for <paramref name="filePath"/> (e.g. a tab closed without saving).</summary>
        public static void RevertToDisk(string filePath)
        {
            string key = NormalizePath(filePath);
            Task.Run(() => ReloadFromDisk(key));
        }

        /// <summary>Shared, cached metadata reference (with XML docs when a sibling .xml file exists).</summary>
        public static MetadataReference GetMetadataReference(string path)
        {
            long stamp = 0;
            try { stamp = File.GetLastWriteTimeUtc(path).Ticks; } catch { }

            return s_MetadataReferences.AddOrUpdate(path + "|" + stamp,
                p => CreateMetadataReference(path),
                (p, existing) => existing);
        }

        private static MetadataReference CreateMetadataReference(string path)
        {
            string xmlPath = Path.ChangeExtension(path, ".xml");
            DocumentationProvider docs = null;
            try
            {
                if (File.Exists(xmlPath)) docs = XmlDocumentationProvider.CreateFromFile(xmlPath);
            }
            catch { }

            return MetadataReference.CreateFromFile(path, documentation: docs);
        }

        private static SyntaxTree ReplaceText(AssemblyEntry entry, string key, SyntaxTree oldTree, SourceText newText)
        {
            if (oldTree.TryGetText(out var oldText) && oldText.ContentEquals(newText)) return oldTree;

            var newTree = oldTree.WithChangedText(newText);
            entry.Compilation = entry.Compilation.ReplaceSyntaxTree(oldTree, newTree);
            entry.Trees[key] = newTree;
            return newTree;
        }

        private static void EnsureBuildStarted(AssemblyEntry entry)
        {
            lock (entry.Gate)
            {
                if (entry.BuildTask == null) entry.BuildTask = Task.Run(() => Build(entry));
            }
        }

        private static void Build(AssemblyEntry entry)
        {
            try
            {
                var descriptor = entry.Descriptor;
                var parseOptions = descriptor.ParseOptions;

                Dictionary<string, SyntaxTree> seed;
                lock (entry.Gate) seed = entry.SeedTrees;

                var parsed = new ConcurrentDictionary<string, SyntaxTree>(StringComparer.OrdinalIgnoreCase);
                Parallel.ForEach(descriptor.SourceFiles, file =>
                {
                    try
                    {
                        SourceText text;
                        if (seed != null && seed.TryGetValue(file, out var seedTree) && seedTree.TryGetText(out var seedText))
                        {
                            if (seedTree.Options.Equals(parseOptions)) { parsed[file] = seedTree; return; }
                            text = seedText;
                        }
                        else
                        {
                            text = SourceText.From(File.ReadAllText(file));
                        }
                        parsed[file] = CSharpSyntaxTree.ParseText(text, parseOptions, file);
                    }
                    catch
                    {
                        // Unreadable file: leave it out, the rest of the assembly still analyses.
                    }
                });

                var references = new List<MetadataReference>(descriptor.References.Length);
                foreach (var path in descriptor.References)
                {
                    try { references.Add(GetMetadataReference(path)); } catch { }
                }

                var options = descriptor.CompilationOptions;
                var trees = new Dictionary<string, SyntaxTree>(parsed, StringComparer.OrdinalIgnoreCase);
                var compilation = CSharpCompilation.Create(descriptor.Name,
                    descriptor.SourceFiles.Where(trees.ContainsKey).Select(f => trees[f]), references, options);

                // Bind references and declarations once here so the first keystroke doesn't pay for it.
                compilation.GlobalNamespace.GetMembers();

                lock (entry.Gate)
                {
                    entry.Trees = trees;
                    entry.Compilation = compilation;
                    entry.SeedTrees = null;
                }

                s_ReadyAssemblies.Enqueue(descriptor.Name);
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning($"[FuzzyIDE] Could not build analysis compilation for {entry.Descriptor.Name}: {ex.Message}");
            }
        }

        private static void ReloadFromDisk(string key)
        {
            AssemblyEntry entry;
            lock (s_Gate)
            {
                if (!s_EntriesByFile.TryGetValue(key, out entry)) return;
            }
            if (!entry.IsReady) return;

            SourceText text = null;
            try
            {
                if (File.Exists(key)) text = SourceText.From(File.ReadAllText(key));
            }
            catch
            {
                return;
            }

            lock (entry.Gate)
            {
                if (!entry.Trees.TryGetValue(key, out var oldTree)) return;

                if (text != null)
                {
                    ReplaceText(entry, key, oldTree, text);
                }
                else
                {
                    entry.Compilation = entry.Compilation.RemoveSyntaxTrees(oldTree);
                    entry.Trees.Remove(key);
                }
            }
        }

        private static void OnFilesChanged(IReadOnlyList<FileWatcherEvent> events)
        {
            foreach (var ev in events)
            {
                if (!string.IsNullOrEmpty(ev.OldFullPath) && ev.OldFullPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                {
                    string oldKey = NormalizePath(ev.OldFullPath);
                    Task.Run(() => ReloadFromDisk(oldKey));
                }
                if (!string.IsNullOrEmpty(ev.FullPath) && ev.FullPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                {
                    string key = NormalizePath(ev.FullPath);
                    Task.Run(() => ReloadFromDisk(key));
                }
            }
        }

        private static void DispatchReadyAssemblies()
        {
            while (s_ReadyAssemblies.TryDequeue(out var name))
            {
                try { AssemblyReady?.Invoke(name); }
                catch (Exception ex) { UnityEngine.Debug.LogException(ex); }
            }
        }

        private static string[] BuildReferenceList(string[] assemblyReferences, string[] frameworkReferences, Dictionary<string, bool> existingFiles)
        {
            var result = new List<string>();
            var fileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void Add(string path)
            {
                if (string.IsNullOrEmpty(path)) return;
                string full = NormalizePath(path);
                // Most assemblies share the same few hundred references; stat each file once per refresh.
                if (!existingFiles.TryGetValue(full, out bool exists)) existingFiles[full] = exists = File.Exists(full);
                if (!exists || !fileNames.Add(Path.GetFileName(full))) return;
                result.Add(full);
            }

            if (assemblyReferences != null)
            {
                foreach (var r in assemblyReferences) Add(r);
            }
            foreach (var r in frameworkReferences) Add(r);
            return result.ToArray();
        }

        // Unity compiles scripts against .NET Standard 2.1 plus its netfx/netstandard facade shims; the engine
        // assemblies reference mscorlib, so the facades are needed for System.Object & co. to unify.
        private static string[] GetFrameworkReferences()
        {
            string netStandard = Path.Combine(EditorApplication.applicationContentsPath, "NetStandard");
            var result = new List<string>();

            string core = Path.Combine(netStandard, "ref", "2.1.0", "netstandard.dll");
            if (File.Exists(core)) result.Add(core);

            foreach (var dir in new[]
            {
                Path.Combine(netStandard, "compat", "2.1.0", "shims", "netfx"),
                Path.Combine(netStandard, "compat", "2.1.0", "shims", "netstandard"),
                Path.Combine(netStandard, "Extensions", "2.0.0")
            })
            {
                if (Directory.Exists(dir)) result.AddRange(Directory.GetFiles(dir, "*.dll"));
            }

            return result.ToArray();
        }
    }
}
