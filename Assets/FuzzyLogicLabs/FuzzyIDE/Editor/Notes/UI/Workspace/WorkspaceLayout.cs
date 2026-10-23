using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.Notes.UI
{
    /// <summary>Where a dragged tab lands relative to a leaf: inside it, or in a new pane on one side.</summary>
    public enum DropZone { Center, Left, Right, Top, Bottom }

    public abstract class WorkspaceNode
    {
        public WorkspaceSplit Parent { get; internal set; }
    }

    /// <summary>
    /// An open tab. <see cref="Key"/> is what is persisted ("note:&lt;path&gt;", "view:tasks");
    /// <see cref="Ref"/> is the runtime object behind it (a NoteItem), so a rename does not lose the tab.
    /// </summary>
    public sealed class WorkspaceTab
    {
        public string Key;
        public object Ref;

        public WorkspaceTab(string key, object reference = null)
        {
            Key = key;
            Ref = reference;
        }
    }

    /// <summary>A pane with its own tab strip.</summary>
    public sealed class WorkspaceLeaf : WorkspaceNode
    {
        public readonly List<WorkspaceTab> Tabs = new List<WorkspaceTab>();
        public int ActiveIndex = -1;
        /// <summary>What this pane showed, for back/forward (runtime only).</summary>
        public readonly NavigationHistory History = new NavigationHistory();

        public WorkspaceTab ActiveTab => ActiveIndex >= 0 && ActiveIndex < Tabs.Count ? Tabs[ActiveIndex] : null;

        public int IndexOf(Func<WorkspaceTab, bool> match)
        {
            for (int i = 0; i < Tabs.Count; i++)
                if (match(Tabs[i])) return i;
            return -1;
        }
    }

    /// <summary>Two panes side by side (<see cref="Vertical"/> = false) or stacked.</summary>
    public sealed class WorkspaceSplit : WorkspaceNode
    {
        public bool Vertical;
        public WorkspaceNode First;
        public WorkspaceNode Second;
        /// <summary>Size of <see cref="First"/> in pixels; 0 or less means "half of the space".</summary>
        public float FirstSize;

        internal void Replace(WorkspaceNode child, WorkspaceNode with)
        {
            if (First == child) First = with;
            else if (Second == child) Second = with;
            else throw new InvalidOperationException("Not a child of this split.");
            with.Parent = this;
        }
    }

    /// <summary>
    /// The Obsidian-style workspace: a binary tree of splits whose leaves are tab groups. Pure model (no UI), so
    /// it can be unit tested and persisted; the views rebuild from it after every structural change.
    /// </summary>
    public sealed class WorkspaceLayout
    {
        public const int DefaultMaxTabsPerLeaf = 12;

        public WorkspaceNode Root { get; private set; }
        public WorkspaceLeaf ActiveLeaf { get; set; }
        public int MaxTabsPerLeaf = DefaultMaxTabsPerLeaf;

        public WorkspaceLayout()
        {
            var leaf = new WorkspaceLeaf();
            Root = leaf;
            ActiveLeaf = leaf;
        }

        // =====================================================================
        // Queries
        // =====================================================================

        /// <summary>Leaves from left to right / top to bottom.</summary>
        public List<WorkspaceLeaf> Leaves()
        {
            var list = new List<WorkspaceLeaf>();
            Collect(Root, list);
            return list;
        }

        private static void Collect(WorkspaceNode node, List<WorkspaceLeaf> list)
        {
            if (node is WorkspaceLeaf leaf) list.Add(leaf);
            else if (node is WorkspaceSplit split)
            {
                Collect(split.First, list);
                Collect(split.Second, list);
            }
        }

        public IEnumerable<WorkspaceTab> AllTabs()
        {
            foreach (var leaf in Leaves())
                foreach (var tab in leaf.Tabs) yield return tab;
        }

        public bool IsOnlyLeaf(WorkspaceLeaf leaf) => Root == leaf;

        // =====================================================================
        // Tabs
        // =====================================================================

        /// <summary>
        /// Shows <paramref name="key"/> in <paramref name="leaf"/>: activates the tab if the leaf already has it,
        /// otherwise inserts it after the active tab. The oldest inactive tabs are dropped beyond the limit.
        /// </summary>
        public WorkspaceTab Open(WorkspaceLeaf leaf, string key, object reference = null)
        {
            int existing = leaf.IndexOf(t => reference != null ? ReferenceEquals(t.Ref, reference) : t.Key == key);
            if (existing >= 0)
            {
                var tab = leaf.Tabs[existing];
                if (reference != null) tab.Ref = reference;
                tab.Key = key;
                leaf.ActiveIndex = existing;
                return tab;
            }

            var created = new WorkspaceTab(key, reference);
            int insertAt = leaf.ActiveIndex >= 0 ? leaf.ActiveIndex + 1 : leaf.Tabs.Count;
            leaf.Tabs.Insert(insertAt, created);
            leaf.ActiveIndex = insertAt;

            while (leaf.Tabs.Count > Math.Max(1, MaxTabsPerLeaf))
            {
                int victim = leaf.ActiveIndex == 0 ? 1 : 0;
                leaf.Tabs.RemoveAt(victim);
                if (victim < leaf.ActiveIndex) leaf.ActiveIndex--;
            }
            return created;
        }

        /// <summary>
        /// Closes a tab. An emptied leaf is removed unless it is the only one.
        /// Returns false when the index was invalid.
        /// </summary>
        public bool CloseTab(WorkspaceLeaf leaf, int index)
        {
            if (index < 0 || index >= leaf.Tabs.Count) return false;
            leaf.Tabs.RemoveAt(index);
            if (leaf.Tabs.Count == 0) leaf.ActiveIndex = -1;
            else if (index < leaf.ActiveIndex || leaf.ActiveIndex >= leaf.Tabs.Count) leaf.ActiveIndex = Math.Max(0, leaf.ActiveIndex - 1);
            if (leaf.Tabs.Count == 0 && !IsOnlyLeaf(leaf)) RemoveLeaf(leaf);
            return true;
        }

        /// <summary>Removes matching tabs everywhere (e.g. deleted notes) and collapses emptied leaves.</summary>
        public int RemoveTabs(Func<WorkspaceTab, bool> match)
        {
            int removed = 0;
            foreach (var leaf in Leaves())
            {
                for (int i = leaf.Tabs.Count - 1; i >= 0; i--)
                {
                    if (!match(leaf.Tabs[i])) continue;
                    leaf.Tabs.RemoveAt(i);
                    if (i < leaf.ActiveIndex || leaf.ActiveIndex >= leaf.Tabs.Count) leaf.ActiveIndex--;
                    removed++;
                }
                if (leaf.Tabs.Count == 0)
                {
                    leaf.ActiveIndex = -1;
                    if (!IsOnlyLeaf(leaf)) RemoveLeaf(leaf);
                }
                else if (leaf.ActiveIndex < 0) leaf.ActiveIndex = 0;
            }
            return removed;
        }

        /// <summary>Moves a tab to another position, possibly in another leaf (drag &amp; drop between tab strips).</summary>
        public void MoveTab(WorkspaceLeaf from, int index, WorkspaceLeaf to, int insertIndex)
        {
            if (index < 0 || index >= from.Tabs.Count) return;
            var tab = from.Tabs[index];

            if (from == to)
            {
                insertIndex = Clamp(insertIndex, 0, from.Tabs.Count);
                if (insertIndex > index) insertIndex--; // removing shifts the target left
                from.Tabs.RemoveAt(index);
                from.Tabs.Insert(insertIndex, tab);
                from.ActiveIndex = insertIndex;
                return;
            }

            // the target may already show the same note: just focus it there
            int duplicate = to.IndexOf(t => SameTab(t, tab));
            from.Tabs.RemoveAt(index);
            if (from.Tabs.Count == 0) from.ActiveIndex = -1;
            else if (index < from.ActiveIndex || from.ActiveIndex >= from.Tabs.Count) from.ActiveIndex = Math.Max(0, from.ActiveIndex - 1);

            if (duplicate >= 0) to.ActiveIndex = duplicate;
            else
            {
                insertIndex = Clamp(insertIndex, 0, to.Tabs.Count);
                to.Tabs.Insert(insertIndex, tab);
                to.ActiveIndex = insertIndex;
            }
            ActiveLeaf = to;
            if (from.Tabs.Count == 0 && !IsOnlyLeaf(from)) RemoveLeaf(from);
        }

        /// <summary>Moves a tab into a new pane on one side of <paramref name="target"/>.</summary>
        public WorkspaceLeaf MoveTabToSplit(WorkspaceLeaf from, int index, WorkspaceLeaf target, DropZone zone)
        {
            if (index < 0 || index >= from.Tabs.Count) return null;
            if (zone == DropZone.Center)
            {
                MoveTab(from, index, target, target.Tabs.Count);
                return target;
            }
            // splitting a leaf off its own only tab would leave an empty pane behind: nothing to do
            if (from == target && from.Tabs.Count == 1) return null;

            var tab = from.Tabs[index];
            var created = Split(target, zone);
            CloseTabKeepLeaf(from, index);
            created.Tabs.Add(tab);
            created.ActiveIndex = 0;
            ActiveLeaf = created;
            if (from.Tabs.Count == 0 && !IsOnlyLeaf(from)) RemoveLeaf(from);
            return created;
        }

        private void CloseTabKeepLeaf(WorkspaceLeaf leaf, int index)
        {
            leaf.Tabs.RemoveAt(index);
            if (leaf.Tabs.Count == 0) leaf.ActiveIndex = -1;
            else if (index < leaf.ActiveIndex || leaf.ActiveIndex >= leaf.Tabs.Count) leaf.ActiveIndex = Math.Max(0, leaf.ActiveIndex - 1);
        }

        private static bool SameTab(WorkspaceTab a, WorkspaceTab b) =>
            a.Ref != null && b.Ref != null ? ReferenceEquals(a.Ref, b.Ref) : a.Key == b.Key;

        // =====================================================================
        // Splits
        // =====================================================================

        /// <summary>Puts a new empty leaf next to <paramref name="leaf"/> and returns it.</summary>
        public WorkspaceLeaf Split(WorkspaceLeaf leaf, DropZone side)
        {
            if (side == DropZone.Center) return leaf;
            var created = new WorkspaceLeaf();
            var parent = leaf.Parent;
            bool newFirst = side == DropZone.Left || side == DropZone.Top;
            var split = new WorkspaceSplit
            {
                Vertical = side == DropZone.Top || side == DropZone.Bottom,
                First = newFirst ? (WorkspaceNode)created : leaf,
                Second = newFirst ? (WorkspaceNode)leaf : created
            };

            if (parent == null) Root = split;
            else parent.Replace(leaf, split);
            split.Parent = parent;
            leaf.Parent = split;
            created.Parent = split;
            return created;
        }

        /// <summary>Removes a leaf; its sibling takes the parent split's place. The last leaf is never removed.</summary>
        public void RemoveLeaf(WorkspaceLeaf leaf)
        {
            var parent = leaf.Parent;
            if (parent == null) return;

            var sibling = parent.First == leaf ? parent.Second : parent.First;
            var grand = parent.Parent;
            if (grand == null)
            {
                Root = sibling;
                sibling.Parent = null;
            }
            else grand.Replace(parent, sibling);
            leaf.Parent = null;

            if (ActiveLeaf == leaf) ActiveLeaf = FirstLeaf(sibling);
        }

        private static WorkspaceLeaf FirstLeaf(WorkspaceNode node)
        {
            while (node is WorkspaceSplit split) node = split.First;
            return (WorkspaceLeaf)node;
        }

        /// <summary>The leaf next to <paramref name="leaf"/> in reading order (wraps around).</summary>
        public WorkspaceLeaf Neighbor(WorkspaceLeaf leaf, int direction)
        {
            var leaves = Leaves();
            int i = leaves.IndexOf(leaf);
            if (i < 0 || leaves.Count == 0) return leaf;
            return leaves[((i + direction) % leaves.Count + leaves.Count) % leaves.Count];
        }

        // =====================================================================
        // Persistence: an indented outline, one node per line
        //   split h 320          (h = side by side, v = stacked, then the first pane size)
        //     leaf 1             (index of the active tab)
        //       tab note:C:/...  (the tab key, rest of the line)
        //   active 0             (index of the active leaf, reading order)
        // =====================================================================

        public string Serialize()
        {
            var sb = new StringBuilder();
            Write(Root, 0, sb);
            sb.Append("active ").Append(Math.Max(0, Leaves().IndexOf(ActiveLeaf)).ToString(CultureInfo.InvariantCulture)).Append('\n');
            return sb.ToString();
        }

        private static void Write(WorkspaceNode node, int depth, StringBuilder sb)
        {
            string indent = new string(' ', depth * 2);
            if (node is WorkspaceSplit split)
            {
                sb.Append(indent).Append("split ").Append(split.Vertical ? 'v' : 'h').Append(' ')
                  .Append(split.FirstSize.ToString("0.#", CultureInfo.InvariantCulture)).Append('\n');
                Write(split.First, depth + 1, sb);
                Write(split.Second, depth + 1, sb);
            }
            else if (node is WorkspaceLeaf leaf)
            {
                sb.Append(indent).Append("leaf ").Append(leaf.ActiveIndex.ToString(CultureInfo.InvariantCulture)).Append('\n');
                foreach (var tab in leaf.Tabs)
                    sb.Append(indent).Append("  tab ").Append(tab.Key.Replace('\n', ' ')).Append('\n');
            }
        }

        /// <summary>Parses <see cref="Serialize"/> output. Returns a fresh single-leaf layout for empty or broken text.</summary>
        public static WorkspaceLayout Deserialize(string text)
        {
            var layout = new WorkspaceLayout();
            if (string.IsNullOrWhiteSpace(text)) return layout;

            var lines = new List<(int depth, string content)>();
            foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
            {
                if (raw.Trim().Length == 0) continue;
                int spaces = 0;
                while (spaces < raw.Length && raw[spaces] == ' ') spaces++;
                lines.Add((spaces / 2, raw.Substring(spaces)));
            }

            try
            {
                int pos = 0;
                var root = ReadNode(lines, ref pos, 0);
                if (root == null) return layout;
                root.Parent = null;
                layout.Root = root;

                var leaves = layout.Leaves();
                int active = 0;
                for (; pos < lines.Count; pos++)
                    if (lines[pos].content.StartsWith("active ", StringComparison.Ordinal))
                        int.TryParse(lines[pos].content.Substring(7), NumberStyles.Integer, CultureInfo.InvariantCulture, out active);
                layout.ActiveLeaf = leaves[Clamp(active, 0, leaves.Count - 1)];
                return layout;
            }
            catch (Exception)
            {
                return new WorkspaceLayout();
            }
        }

        private static WorkspaceNode ReadNode(List<(int depth, string content)> lines, ref int pos, int depth)
        {
            if (pos >= lines.Count || lines[pos].depth != depth) return null;
            string content = lines[pos].content;

            if (content.StartsWith("split ", StringComparison.Ordinal))
            {
                var parts = content.Split(' ');
                var split = new WorkspaceSplit { Vertical = parts.Length > 1 && parts[1] == "v" };
                if (parts.Length > 2) float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out split.FirstSize);
                pos++;
                var first = ReadNode(lines, ref pos, depth + 1);
                var second = ReadNode(lines, ref pos, depth + 1);
                if (first == null || second == null) return first ?? second; // tolerate a half-written split
                split.First = first; split.Second = second;
                first.Parent = split; second.Parent = split;
                return split;
            }

            if (content.StartsWith("leaf", StringComparison.Ordinal))
            {
                var leaf = new WorkspaceLeaf();
                if (content.Length > 5) int.TryParse(content.Substring(5), NumberStyles.Integer, CultureInfo.InvariantCulture, out leaf.ActiveIndex);
                pos++;
                while (pos < lines.Count && lines[pos].depth == depth + 1 && lines[pos].content.StartsWith("tab ", StringComparison.Ordinal))
                {
                    leaf.Tabs.Add(new WorkspaceTab(lines[pos].content.Substring(4)));
                    pos++;
                }
                leaf.ActiveIndex = leaf.Tabs.Count == 0 ? -1 : Clamp(leaf.ActiveIndex, 0, leaf.Tabs.Count - 1);
                return leaf;
            }

            return null;
        }

        private static int Clamp(int v, int min, int max) => v < min ? min : v > max ? max : v;
    }
}
