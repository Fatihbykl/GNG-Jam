using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.Notes.Core
{
    /// <summary>
    /// Watches the notes directory for .md changes made outside Fuzzy Notes (git pull, Obsidian, other editors).
    /// Events are collected on the watcher thread and handed out in debounced batches through <see cref="Pump"/>,
    /// which must be called from the main thread. Our own writes are filtered later by content comparison.
    /// </summary>
    public sealed class NoteFileWatcher : IDisposable
    {
        private const int DebounceMs = 200;

        private readonly ConcurrentDictionary<string, byte> m_Pending = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        private FileSystemWatcher m_Watcher;
        private long m_LastEventTicks;
        private volatile bool m_NeedsFullRescan;

        public NoteFileWatcher(string directory)
        {
            if (!Directory.Exists(directory)) return;

            m_Watcher = new FileSystemWatcher(directory)
            {
                IncludeSubdirectories = true,
                InternalBufferSize = 65536,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size
            };
            m_Watcher.Changed += (s, e) => Enqueue(e.FullPath);
            m_Watcher.Created += (s, e) => Enqueue(e.FullPath);
            m_Watcher.Deleted += (s, e) => Enqueue(e.FullPath);
            m_Watcher.Renamed += (s, e) => { Enqueue(e.OldFullPath); Enqueue(e.FullPath); };
            m_Watcher.Error += (s, e) => { m_NeedsFullRescan = true; Touch(); };
            m_Watcher.EnableRaisingEvents = true;
        }

        /// <summary>
        /// Returns true when a debounced batch is ready. <paramref name="fullRescan"/> is set when the OS
        /// dropped events (buffer overflow) and the caller should re-check every note.
        /// </summary>
        public bool Pump(out List<string> paths, out bool fullRescan)
        {
            paths = null;
            fullRescan = false;
            if (m_Pending.IsEmpty && !m_NeedsFullRescan) return false;

            long elapsedMs = (DateTime.UtcNow.Ticks - Interlocked.Read(ref m_LastEventTicks)) / TimeSpan.TicksPerMillisecond;
            if (elapsedMs < DebounceMs) return false;

            paths = new List<string>();
            foreach (var key in m_Pending.Keys)
            {
                if (m_Pending.TryRemove(key, out _)) paths.Add(key);
            }
            fullRescan = m_NeedsFullRescan;
            m_NeedsFullRescan = false;
            return true;
        }

        public void Dispose()
        {
            if (m_Watcher == null) return;
            try
            {
                m_Watcher.EnableRaisingEvents = false;
                m_Watcher.Dispose();
            }
            catch { }
            m_Watcher = null;
        }

        private void Enqueue(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            if (path.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            {
                m_Pending[Path.GetFullPath(path)] = 0;
            }
            else if (Path.GetExtension(path).Length == 0)
            {
                // Folder created/renamed/deleted: individual file events are not raised for its contents.
                m_NeedsFullRescan = true;
            }
            else return;
            Touch();
        }

        private void Touch() => Interlocked.Exchange(ref m_LastEventTicks, DateTime.UtcNow.Ticks);
    }
}
