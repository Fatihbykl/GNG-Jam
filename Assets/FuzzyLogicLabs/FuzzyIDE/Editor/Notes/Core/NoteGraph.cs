using System;
using System.Collections.Generic;
using System.Linq;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.Notes.Core
{
    public enum GraphNodeKind { Note, Tag, Unresolved, Reference }

    public sealed class GraphNode
    {
        public int Index;
        /// <summary>Stable across rebuilds ("note:path", "tag:x", "missing:x", "ref:key"): positions are kept by id.</summary>
        public string Id;
        public string Label;
        public GraphNodeKind Kind;
        public NoteItem Note;
        /// <summary>Link text to open for <see cref="GraphNodeKind.Reference"/> nodes.</summary>
        public string Target;
        public int Degree;
    }

    public readonly struct GraphEdge
    {
        public readonly int A;
        public readonly int B;

        public GraphEdge(int a, int b)
        {
            A = a;
            B = b;
        }
    }

    /// <summary>What the graph shows (Obsidian's graph filters).</summary>
    public sealed class GraphFilter
    {
        /// <summary>Only notes matching this search query (operators allowed); empty shows every note.</summary>
        public string Query = "";
        public bool ShowTags;
        public bool ShowReferences;
        public bool ShowUnresolved;
        public bool ShowOrphans = true;
    }

    /// <summary>
    /// Notes and what they link to as an undirected graph: notes, plus optionally tags, unresolved links and
    /// code/asset references. Pure model built from the note list and two lookups, so it is unit tested.
    /// </summary>
    public sealed class NoteGraph
    {
        public readonly List<GraphNode> Nodes = new List<GraphNode>();
        public readonly List<GraphEdge> Edges = new List<GraphEdge>();
        private List<int>[] m_Neighbors = Array.Empty<List<int>>();
        private readonly Dictionary<NoteItem, int> m_NoteIndex = new Dictionary<NoteItem, int>();

        public IReadOnlyList<int> Neighbors(int node) => m_Neighbors[node];
        public int IndexOf(NoteItem note) => note != null && m_NoteIndex.TryGetValue(note, out int i) ? i : -1;

        /// <param name="outgoingLinks">Normalized wiki-link targets of a note (see <see cref="NoteIndex.GetOutgoingLinks"/>).</param>
        /// <param name="references">Code/asset references of a note as (key, label, link text).</param>
        public static NoteGraph Build(IEnumerable<NoteItem> notes, Func<NoteItem, IEnumerable<string>> outgoingLinks,
            Func<NoteItem, IEnumerable<(string key, string label, string target)>> references, GraphFilter filter)
        {
            filter = filter ?? new GraphFilter();
            var all = notes.Where(n => n != null).ToList();

            // every name a link may use -> note (first one wins, like link resolution)
            var byKey = new Dictionary<string, NoteItem>(StringComparer.OrdinalIgnoreCase);
            foreach (var note in all)
                foreach (var key in NoteIndex.GetLinkKeys(note))
                    if (!byKey.ContainsKey(key)) byKey[key] = note;

            var query = string.IsNullOrWhiteSpace(filter.Query) ? null : NoteSearchQuery.Parse(filter.Query);
            var included = query == null ? all : all.Where(n => NoteSearch.Match(n, query) != null).ToList();

            var graph = new NoteGraph();
            var ids = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var edgeSet = new HashSet<long>();

            int AddNode(string id, string label, GraphNodeKind kind, NoteItem note = null, string target = null)
            {
                if (ids.TryGetValue(id, out int existing)) return existing;
                var node = new GraphNode { Index = graph.Nodes.Count, Id = id, Label = label, Kind = kind, Note = note, Target = target };
                graph.Nodes.Add(node);
                ids[id] = node.Index;
                if (note != null) graph.m_NoteIndex[note] = node.Index;
                return node.Index;
            }

            void AddEdge(int a, int b)
            {
                if (a == b) return;
                long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
                if (!edgeSet.Add(key)) return;
                graph.Edges.Add(new GraphEdge(a, b));
            }

            foreach (var note in included) AddNode(NoteId(note), note.Title, GraphNodeKind.Note, note);

            foreach (var note in included)
            {
                int from = graph.m_NoteIndex[note];
                foreach (var link in outgoingLinks(note) ?? Enumerable.Empty<string>())
                {
                    if (byKey.TryGetValue(link, out var target))
                    {
                        if (graph.m_NoteIndex.TryGetValue(target, out int to)) AddEdge(from, to);
                    }
                    else if (filter.ShowUnresolved)
                    {
                        AddEdge(from, AddNode("missing:" + link, link, GraphNodeKind.Unresolved));
                    }
                }

                if (filter.ShowTags)
                    foreach (var tag in note.Tags)
                        if (!string.IsNullOrWhiteSpace(tag)) AddEdge(from, AddNode("tag:" + tag, "#" + tag, GraphNodeKind.Tag));

                if (filter.ShowReferences && references != null)
                    foreach (var (key, label, target) in references(note))
                        if (!string.IsNullOrEmpty(key)) AddEdge(from, AddNode("ref:" + key, label, GraphNodeKind.Reference, null, target));
            }

            graph.Finish();
            return filter.ShowOrphans ? graph : graph.Subgraph(n => n.Degree > 0);
        }

        public static string NoteId(NoteItem note) => "note:" + (note.FilePath ?? note.Title);

        /// <summary>
        /// The local graph of <paramref name="center"/>: everything within <paramref name="depth"/> links of it.
        /// Returns an empty graph when the note is not in this graph.
        /// </summary>
        public NoteGraph Local(NoteItem center, int depth)
        {
            int start = IndexOf(center);
            if (start < 0) return new NoteGraph();

            var distance = new Dictionary<int, int> { [start] = 0 };
            var queue = new Queue<int>();
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                int n = queue.Dequeue();
                if (distance[n] >= depth) continue;
                foreach (int m in m_Neighbors[n])
                {
                    if (distance.ContainsKey(m)) continue;
                    distance[m] = distance[n] + 1;
                    queue.Enqueue(m);
                }
            }
            return Subgraph(node => distance.ContainsKey(node.Index));
        }

        private NoteGraph Subgraph(Func<GraphNode, bool> keep)
        {
            var result = new NoteGraph();
            var map = new int[Nodes.Count];
            foreach (var node in Nodes)
            {
                if (!keep(node)) { map[node.Index] = -1; continue; }
                var copy = new GraphNode { Index = result.Nodes.Count, Id = node.Id, Label = node.Label, Kind = node.Kind, Note = node.Note, Target = node.Target };
                map[node.Index] = copy.Index;
                result.Nodes.Add(copy);
                if (copy.Note != null) result.m_NoteIndex[copy.Note] = copy.Index;
            }
            foreach (var e in Edges)
                if (map[e.A] >= 0 && map[e.B] >= 0) result.Edges.Add(new GraphEdge(map[e.A], map[e.B]));
            result.Finish();
            return result;
        }

        private void Finish()
        {
            m_Neighbors = new List<int>[Nodes.Count];
            for (int i = 0; i < Nodes.Count; i++)
            {
                m_Neighbors[i] = new List<int>();
                Nodes[i].Degree = 0;
            }
            foreach (var e in Edges)
            {
                m_Neighbors[e.A].Add(e.B);
                m_Neighbors[e.B].Add(e.A);
                Nodes[e.A].Degree++;
                Nodes[e.B].Degree++;
            }
        }
    }
}
