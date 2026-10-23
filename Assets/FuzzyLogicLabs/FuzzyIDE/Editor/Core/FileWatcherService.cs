using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.Core
{
    public enum FileWatcherChangeType
    {
        Changed,
        Created,
        Deleted,
        Renamed
    }

    public struct FileWatcherEvent
    {
        public string FullPath;
        public string OldFullPath;
        public FileWatcherChangeType ChangeType;
    }

    [InitializeOnLoad]
    public static class FileWatcherService
    {
        public static event Action<IReadOnlyList<FileWatcherEvent>> OnFilesChangedBatch;
        public static event Action OnGitIndexChanged;

        private static FileSystemWatcher s_AssetsWatcher;
        private static FileSystemWatcher s_GitWatcher;

        private static readonly ConcurrentDictionary<string, DateTime> s_SelfSavedPaths =
            new ConcurrentDictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        private static readonly ConcurrentDictionary<string, FileWatcherEvent> s_PendingEvents =
            new ConcurrentDictionary<string, FileWatcherEvent>(StringComparer.OrdinalIgnoreCase);

        private static volatile bool s_GitIndexChangedPending = false;
        private static volatile bool s_HasPendingDispatch = false;
        private static long s_LastEventTimeTicks = 0;
        private static readonly object s_TimerLock = new object();
        private static Timer s_DebounceTimer;

        private const int DEBOUNCE_DELAY_MS = 150;
        private const int BUFFER_SIZE_BYTES = 65536;

        static FileWatcherService()
        {
            EditorApplication.delayCall += Initialize;
            AssemblyReloadEvents.beforeAssemblyReload += DisposeWatchers;
        }

        public static void Initialize()
        {
            DisposeWatchers();

            try
            {
                string projectRoot = Directory.GetCurrentDirectory();
                string assetsPath = Path.Combine(projectRoot, "Assets");

                if (Directory.Exists(assetsPath))
                {
                    s_AssetsWatcher = new FileSystemWatcher(assetsPath)
                    {
                        IncludeSubdirectories = true,
                        InternalBufferSize = BUFFER_SIZE_BYTES,
                        NotifyFilter = NotifyFilters.LastWrite |
                                       NotifyFilters.FileName |
                                       NotifyFilters.DirectoryName |
                                       NotifyFilters.Size
                    };

                    s_AssetsWatcher.Changed += OnFileSystemEvent;
                    s_AssetsWatcher.Created += OnFileSystemEvent;
                    s_AssetsWatcher.Deleted += OnFileSystemEvent;
                    s_AssetsWatcher.Renamed += OnFileSystemRenamed;
                    s_AssetsWatcher.Error += OnWatcherError;

                    s_AssetsWatcher.EnableRaisingEvents = true;
                }

                string gitPath = Path.Combine(projectRoot, ".git");
                if (Directory.Exists(gitPath))
                {
                    s_GitWatcher = new FileSystemWatcher(gitPath)
                    {
                        IncludeSubdirectories = true,
                        InternalBufferSize = BUFFER_SIZE_BYTES,
                        NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName
                    };

                    s_GitWatcher.Changed += OnGitSystemEvent;
                    s_GitWatcher.Created += OnGitSystemEvent;
                    s_GitWatcher.Deleted += OnGitSystemEvent;
                    s_GitWatcher.Renamed += OnGitSystemRenamed;
                    s_GitWatcher.Error += OnWatcherError;

                    s_GitWatcher.EnableRaisingEvents = true;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[FuzzyIDE] FileWatcherService initialization warning: {ex.Message}");
            }

            EditorApplication.update -= CheckPendingDispatchFromUpdate;
            EditorApplication.update += CheckPendingDispatchFromUpdate;
        }

        public static void DisposeWatchers()
        {
            EditorApplication.update -= CheckPendingDispatchFromUpdate;

            lock (s_TimerLock)
            {
                s_DebounceTimer?.Dispose();
                s_DebounceTimer = null;
            }

            if (s_AssetsWatcher != null)
            {
                try
                {
                    s_AssetsWatcher.EnableRaisingEvents = false;
                    s_AssetsWatcher.Dispose();
                }
                catch {}
                s_AssetsWatcher = null;
            }

            if (s_GitWatcher != null)
            {
                try
                {
                    s_GitWatcher.EnableRaisingEvents = false;
                    s_GitWatcher.Dispose();
                }
                catch {}
                s_GitWatcher = null;
            }
        }

        public static void SuppressSelfSave(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath)) return;
            string normalized = NormalizePath(fullPath);
            s_SelfSavedPaths[normalized] = DateTime.UtcNow.AddMilliseconds(800);
        }

        private static bool IsSelfSaved(string fullPath)
        {
            string normalized = NormalizePath(fullPath);
            if (s_SelfSavedPaths.TryGetValue(normalized, out var expiry))
            {
                if (DateTime.UtcNow <= expiry)
                {
                    return true;
                }
                s_SelfSavedPaths.TryRemove(normalized, out _);
            }
            return false;
        }

        private static void OnFileSystemEvent(object sender, FileSystemEventArgs e)
        {
            string fullPath = e.FullPath;
            if (string.IsNullOrEmpty(fullPath)) return;

            // Ignore temporary files and self-saved files
            if (fullPath.EndsWith("~") || fullPath.EndsWith(".tmp") || IsSelfSaved(fullPath))
            {
                return;
            }

            FileWatcherChangeType cType;
            switch (e.ChangeType)
            {
                case WatcherChangeTypes.Created:
                    cType = FileWatcherChangeType.Created;
                    break;
                case WatcherChangeTypes.Deleted:
                    cType = FileWatcherChangeType.Deleted;
                    break;
                default:
                    cType = FileWatcherChangeType.Changed;
                    break;
            }

            string norm = NormalizePath(fullPath);
            s_PendingEvents[norm] = new FileWatcherEvent
            {
                FullPath = norm,
                ChangeType = cType
            };

            ScheduleDebouncedDispatch();
        }

        private static void OnFileSystemRenamed(object sender, RenamedEventArgs e)
        {
            string oldPath = e.OldFullPath;
            string newPath = e.FullPath;
            if (string.IsNullOrEmpty(newPath)) return;

            string normNew = NormalizePath(newPath);
            string normOld = string.IsNullOrEmpty(oldPath) ? null : NormalizePath(oldPath);

            s_PendingEvents[normNew] = new FileWatcherEvent
            {
                FullPath = normNew,
                OldFullPath = normOld,
                ChangeType = FileWatcherChangeType.Renamed
            };

            ScheduleDebouncedDispatch();
        }

        private static void OnGitSystemEvent(object sender, FileSystemEventArgs e)
        {
            HandleGitPath(e.FullPath);
        }

        private static void OnGitSystemRenamed(object sender, RenamedEventArgs e)
        {
            HandleGitPath(e.FullPath);
        }

        private static void HandleGitPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            string norm = NormalizePath(path);

            // React if git index, HEAD, config, or refs changed
            if (norm.EndsWith("/.git/index") ||
                norm.Contains("/.git/refs/") ||
                norm.EndsWith("/.git/HEAD") ||
                norm.EndsWith("/.git/packed-refs"))
            {
                s_GitIndexChangedPending = true;
                ScheduleDebouncedDispatch();
            }
        }

        private static void OnWatcherError(object sender, ErrorEventArgs e)
        {
            // Re-initialize if buffer overflowed
            EditorApplication.delayCall += Initialize;
        }

        private static void ScheduleDebouncedDispatch()
        {
            Interlocked.Exchange(ref s_LastEventTimeTicks, DateTime.UtcNow.Ticks);
            s_HasPendingDispatch = true;

            lock (s_TimerLock)
            {
                if (s_DebounceTimer == null)
                {
                    s_DebounceTimer = new Timer(OnTimerFired, null, DEBOUNCE_DELAY_MS, Timeout.Infinite);
                }
                else
                {
                    s_DebounceTimer.Change(DEBOUNCE_DELAY_MS, Timeout.Infinite);
                }
            }
        }

        private static void OnTimerFired(object state)
        {
            // Request execution on Unity's main thread
            EditorApplication.delayCall += DispatchPendingEventsOnMainThread;
        }

        private static void CheckPendingDispatchFromUpdate()
        {
            if (!s_HasPendingDispatch) return;

            long lastTicks = Interlocked.Read(ref s_LastEventTimeTicks);
            long elapsedMs = (DateTime.UtcNow.Ticks - lastTicks) / TimeSpan.TicksPerMillisecond;

            if (elapsedMs >= DEBOUNCE_DELAY_MS)
            {
                DispatchPendingEventsOnMainThread();
            }
        }

        private static void DispatchPendingEventsOnMainThread()
        {
            if (!s_HasPendingDispatch) return;
            s_HasPendingDispatch = false;

            // 1. Git Index Update
            if (s_GitIndexChangedPending)
            {
                s_GitIndexChangedPending = false;
                try
                {
                    OnGitIndexChanged?.Invoke();
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[FuzzyIDE] Error invoking OnGitIndexChanged: {ex.Message}");
                }
            }

            // 2. File Events Batch
            if (!s_PendingEvents.IsEmpty)
            {
                var events = new List<FileWatcherEvent>();
                var keys = new List<string>(s_PendingEvents.Keys);

                foreach (var k in keys)
                {
                    if (s_PendingEvents.TryRemove(k, out var ev))
                    {
                        // Double check self-save suppression at dispatch time
                        if (!IsSelfSaved(ev.FullPath))
                        {
                            events.Add(ev);
                        }
                    }
                }

                if (events.Count > 0)
                {
                    try
                    {
                        OnFilesChangedBatch?.Invoke(events);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[FuzzyIDE] Error invoking OnFilesChangedBatch: {ex.Message}");
                    }
                }
            }
        }

        public static string NormalizePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            try
            {
                return Path.GetFullPath(path).Replace('\\', '/');
            }
            catch
            {
                return path.Replace('\\', '/');
            }
        }
    }
}