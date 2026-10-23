using System;
using System.Collections.Generic;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.Notes.UI
{
    /// <summary>
    /// Back/forward history of one pane, like a browser: every item the pane showed, in order. Going back and then
    /// visiting something new drops the forward entries. Pure model (runtime only, not persisted).
    /// </summary>
    public sealed class NavigationHistory
    {
        public const int DefaultCapacity = 50;

        /// <summary>A visited item: the persisted tab key and the runtime object behind it (see <see cref="WorkspaceTab"/>).</summary>
        public sealed class Entry
        {
            public string Key;
            public object Ref;
        }

        private readonly List<Entry> m_Entries = new List<Entry>();
        private int m_Index = -1;
        public int Capacity = DefaultCapacity;

        public bool CanGoBack => m_Index > 0;
        public bool CanGoForward => m_Index >= 0 && m_Index < m_Entries.Count - 1;
        public int Count => m_Entries.Count;
        public Entry Current => m_Index >= 0 ? m_Entries[m_Index] : null;

        /// <summary>Records that the pane now shows an item. Showing the current item again changes nothing.</summary>
        public void Visit(string key, object reference)
        {
            if (key == null) return;
            var current = Current;
            if (current != null && Same(current, key, reference))
            {
                current.Key = key; // a rename changes the key of the same note
                return;
            }

            m_Entries.RemoveRange(m_Index + 1, m_Entries.Count - m_Index - 1);
            m_Entries.Add(new Entry { Key = key, Ref = reference });
            while (m_Entries.Count > Math.Max(1, Capacity)) m_Entries.RemoveAt(0);
            m_Index = m_Entries.Count - 1;
        }

        /// <summary>Steps back and returns the entry to show, or null at the start.</summary>
        public Entry GoBack()
        {
            if (!CanGoBack) return null;
            return m_Entries[--m_Index];
        }

        /// <summary>Steps forward and returns the entry to show, or null at the end.</summary>
        public Entry GoForward()
        {
            if (!CanGoForward) return null;
            return m_Entries[++m_Index];
        }

        /// <summary>Forgets entries (e.g. deleted notes); neighbours that become equal are merged.</summary>
        public void Remove(Func<Entry, bool> match)
        {
            for (int i = m_Entries.Count - 1; i >= 0; i--)
            {
                if (!match(m_Entries[i])) continue;
                m_Entries.RemoveAt(i);
                if (i <= m_Index) m_Index--;
            }

            for (int i = m_Entries.Count - 1; i > 0; i--)
            {
                if (!Same(m_Entries[i - 1], m_Entries[i].Key, m_Entries[i].Ref)) continue;
                m_Entries.RemoveAt(i);
                if (i <= m_Index) m_Index--;
            }

            if (m_Index < 0 && m_Entries.Count > 0) m_Index = 0;
        }

        private static bool Same(Entry entry, string key, object reference) =>
            entry.Ref != null && reference != null ? ReferenceEquals(entry.Ref, reference) : entry.Key == key;
    }
}
