using System;
using System.Collections.Generic;
using System.Linq;
using FuzzyLogicLabs.FuzzyIDE.Editor.Core;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.Notes.UI
{
    /// <summary>A named action of Fuzzy Notes, shown in the command palette, the ribbon and bound to a hotkey.</summary>
    public sealed class NoteCommand
    {
        public string Id;
        public string Title;
        public string Icon;
        public string Category;
        /// <summary>Key binding (from the IDE keybindings) that runs the command, if any.</summary>
        public IDEAction? Hotkey;
        /// <summary>The hotkey is handled by the focused element (e.g. Ctrl+F in the editor); it is only shown, never dispatched.</summary>
        public bool LocalHotkey;
        public Action Run;
        /// <summary>Null means always available.</summary>
        public Func<bool> IsAvailable;

        public bool Available => IsAvailable == null || IsAvailable();
    }

    /// <summary>
    /// The one list of Fuzzy Notes commands (Obsidian's command registry). Remembers the most recently used ones so the
    /// palette can show them first. Pure logic: persistence of <see cref="Recent"/> is up to the owner.
    /// </summary>
    public sealed class NoteCommandRegistry
    {
        public const int MaxRecent = 6;

        private readonly List<NoteCommand> m_Commands = new List<NoteCommand>();
        private readonly Dictionary<string, NoteCommand> m_ById = new Dictionary<string, NoteCommand>();
        private readonly List<string> m_Recent = new List<string>();

        public IReadOnlyList<NoteCommand> All => m_Commands;
        /// <summary>Ids of recently used commands, most recent first.</summary>
        public IReadOnlyList<string> Recent => m_Recent;

        /// <summary>Raised after <see cref="Recent"/> changed.</summary>
        public event Action RecentChanged;

        public NoteCommand Add(string id, string title, string icon, string category, Action run, IDEAction? hotkey = null, Func<bool> isAvailable = null)
        {
            if (string.IsNullOrEmpty(id)) throw new ArgumentException("A command needs an id.", nameof(id));
            if (m_ById.ContainsKey(id)) throw new ArgumentException($"Command '{id}' is already registered.", nameof(id));
            var command = new NoteCommand { Id = id, Title = title, Icon = icon, Category = category, Run = run, Hotkey = hotkey, IsAvailable = isAvailable };
            m_Commands.Add(command);
            m_ById[id] = command;
            return command;
        }

        public NoteCommand Get(string id) => id != null && m_ById.TryGetValue(id, out var c) ? c : null;

        public IEnumerable<NoteCommand> Available() => m_Commands.Where(c => c.Available);

        /// <summary>Recently used commands that are available right now, most recent first.</summary>
        public IEnumerable<NoteCommand> RecentAvailable() => m_Recent.Select(Get).Where(c => c != null && c.Available);

        /// <summary>Runs a command if it exists and is available; palette runs are remembered as recent.</summary>
        public bool Execute(string id, bool remember = true)
        {
            var command = Get(id);
            if (command == null || !command.Available) return false;
            if (remember) RecordUse(id);
            command.Run?.Invoke();
            return true;
        }

        /// <summary>The first available command whose hotkey <paramref name="matches"/> (e.g. the pressed keys).</summary>
        public NoteCommand FindByHotkey(Func<IDEAction, bool> matches) =>
            m_Commands.FirstOrDefault(c => c.Hotkey.HasValue && !c.LocalHotkey && matches(c.Hotkey.Value) && c.Available);

        public void RecordUse(string id)
        {
            if (!m_ById.ContainsKey(id)) return;
            m_Recent.Remove(id);
            m_Recent.Insert(0, id);
            if (m_Recent.Count > MaxRecent) m_Recent.RemoveRange(MaxRecent, m_Recent.Count - MaxRecent);
            RecentChanged?.Invoke();
        }

        /// <summary>Restores remembered ids (unknown ids are kept: their command may be registered later).</summary>
        public void SetRecent(IEnumerable<string> ids)
        {
            m_Recent.Clear();
            foreach (var id in ids ?? Enumerable.Empty<string>())
                if (!string.IsNullOrEmpty(id) && !m_Recent.Contains(id) && m_Recent.Count < MaxRecent) m_Recent.Add(id);
        }
    }
}
