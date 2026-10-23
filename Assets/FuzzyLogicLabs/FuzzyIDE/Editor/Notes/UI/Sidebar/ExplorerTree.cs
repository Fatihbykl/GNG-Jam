using System;
using System.Collections.Generic;
using System.Linq;
using FuzzyLogicLabs.FuzzyIDE.Editor.Notes.Core;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.Notes.UI
{
    public enum ExplorerRowKind { Vault, Folder, Note, Section }

    /// <summary>One visible row of the file explorer.</summary>
    public sealed class ExplorerRow
    {
        public ExplorerRowKind Kind;
        public int Depth;
        public NoteVault Vault;
        /// <summary>Folder path relative to the vault ("" = root); for notes, the folder they are in.</summary>
        public string Folder = "";
        public NoteItem Note;
        /// <summary>Notes inside a vault/folder, recursively.</summary>
        public int Count;
        public bool Expanded;
        public string Label;

        public string Key => Kind == ExplorerRowKind.Note ? "n:" + Note?.FilePath : Kind + ":" + Vault?.Id + ":" + Folder.ToLowerInvariant();
    }

    /// <summary>
    /// Flattens vaults → folders → notes into the rows that are currently visible (collapsed folders hide their
    /// children), so the explorer can show them in a virtualized list. Pure logic, unit tested.
    /// </summary>
    public static class ExplorerTree
    {
        public static List<ExplorerRow> Build(
            IReadOnlyList<NoteVault> vaults,
            IEnumerable<NoteItem> notes,
            Func<NoteVault, IEnumerable<string>> listFolders,
            Func<NoteVault, string, bool> isExpanded,
            Func<NoteVault, bool> isVaultCollapsed,
            Func<IEnumerable<NoteItem>, IEnumerable<NoteItem>> sort)
        {
            var rows = new List<ExplorerRow>();
            var all = notes.ToList();
            var vaultIds = new HashSet<string>(vaults.Select(v => v.Id));
            // An Assets vault without markdown files would only be an empty root.
            var shown = vaults.Where(v => !v.IsProjectFiles || all.Any(n => !n.IsExternal && n.VaultId == v.Id)).ToList();
            bool multiVault = shown.Count > 1;

            foreach (var vault in shown)
            {
                var vaultNotes = all.Where(n => !n.IsExternal && n.VaultId == vault.Id).ToList();

                // folders on disk plus every folder (and parent) that holds a note
                var folders = new HashSet<string>(listFolders(vault) ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
                foreach (var n in vaultNotes)
                    for (string p = n.Folder; p.Length > 0; p = Parent(p)) folders.Add(p);

                var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var n in vaultNotes)
                    for (string p = n.Folder; p.Length > 0; p = Parent(p))
                        counts[p] = counts.TryGetValue(p, out int c) ? c + 1 : 1;

                var children = folders.GroupBy(Parent, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList(), StringComparer.OrdinalIgnoreCase);
                var notesByFolder = vaultNotes.GroupBy(n => n.Folder, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

                int depth = 0;
                if (multiVault)
                {
                    bool collapsed = isVaultCollapsed(vault);
                    rows.Add(new ExplorerRow { Kind = ExplorerRowKind.Vault, Vault = vault, Count = vaultNotes.Count, Expanded = !collapsed, Label = vault.Name });
                    if (collapsed) continue;
                    depth = 1;
                }
                AddChildren(rows, vault, "", depth, children, notesByFolder, counts, isExpanded, sort);
            }

            var others = all.Where(n => n.IsExternal || !vaultIds.Contains(n.VaultId ?? "")).ToList();
            if (others.Count > 0)
            {
                rows.Add(new ExplorerRow { Kind = ExplorerRowKind.Section, Label = "Other files", Count = others.Count });
                foreach (var n in sort(others)) rows.Add(new ExplorerRow { Kind = ExplorerRowKind.Note, Note = n, Label = n.Title, Folder = "" });
            }
            return rows;
        }

        private static void AddChildren(List<ExplorerRow> rows, NoteVault vault, string folder, int depth,
            Dictionary<string, List<string>> children, Dictionary<string, List<NoteItem>> notesByFolder, Dictionary<string, int> counts,
            Func<NoteVault, string, bool> isExpanded, Func<IEnumerable<NoteItem>, IEnumerable<NoteItem>> sort)
        {
            if (children.TryGetValue(folder, out var subs))
            {
                foreach (var sub in subs)
                {
                    bool expanded = isExpanded(vault, sub);
                    rows.Add(new ExplorerRow
                    {
                        Kind = ExplorerRowKind.Folder, Vault = vault, Folder = sub, Depth = depth, Expanded = expanded,
                        Count = counts.TryGetValue(sub, out int c) ? c : 0, Label = Leaf(sub)
                    });
                    if (expanded) AddChildren(rows, vault, sub, depth + 1, children, notesByFolder, counts, isExpanded, sort);
                }
            }

            if (notesByFolder.TryGetValue(folder, out var notes))
                foreach (var n in sort(notes))
                    rows.Add(new ExplorerRow { Kind = ExplorerRowKind.Note, Vault = vault, Folder = folder, Note = n, Depth = depth, Label = n.Title });
        }

        public static string Parent(string folder)
        {
            int slash = folder.LastIndexOf('/');
            return slash < 0 ? "" : folder.Substring(0, slash);
        }

        public static string Leaf(string folder)
        {
            int slash = folder.LastIndexOf('/');
            return slash < 0 ? folder : folder.Substring(slash + 1);
        }

        /// <summary>Index of the row a "parent" key press goes to (the enclosing folder or vault), or -1.</summary>
        public static int ParentRowIndex(IReadOnlyList<ExplorerRow> rows, int index)
        {
            if (index <= 0 || index >= rows.Count) return -1;
            int depth = rows[index].Depth;
            for (int i = index - 1; i >= 0; i--)
                if (rows[i].Depth < depth && rows[i].Kind != ExplorerRowKind.Note) return i;
            return -1;
        }
    }
}
