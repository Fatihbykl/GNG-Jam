using System.Collections.Generic;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.Core
{
    public enum CompileState
    {
        /// <summary>The loaded code matches the scripts on disk.</summary>
        UpToDate,
        /// <summary>Scripts changed since the last compilation.</summary>
        ChangesPending,
        Compiling,
        /// <summary>New assemblies are built but not loaded yet (reload is locked).</summary>
        CompiledAwaitingReload,
        /// <summary>The last compilation had errors; Unity keeps running the previous assemblies.</summary>
        Failed,
        Reloading
    }

    public enum CompileAction { None, Compile, Reload }

    /// <summary>
    /// Compilation / domain reload state, driven only by Unity's public signals (compilation started/finished,
    /// per-assembly results, reloads). Holds no Unity references so every transition can be unit tested; the caller
    /// performs the returned <see cref="CompileAction"/>.
    /// </summary>
    public sealed class CompilationStateMachine
    {
        /// <summary>Seconds to wait for a requested compilation to start before concluding nothing needed building.</summary>
        public const double CompileStartTimeout = 3.0;
        /// <summary>Same, even while Unity still reports compiling (it can, with reload locked, until the next reload).</summary>
        public const double CompileStartHardTimeout = 20.0;
        /// <summary>A reload that hasn't happened by then is reported as stuck instead of waiting forever.</summary>
        public const double ReloadTimeout = 45.0;

        public CompileState State { get; private set; } = CompileState.UpToDate;
        /// <summary>Lock Domain Reload policy: successful compilations wait for an explicit reload.</summary>
        public bool LockMode { get; set; }
        /// <summary>The user asked for a reload while code still had to be compiled.</summary>
        public bool ReloadWhenCompiled { get; private set; }
        public int ErrorCount { get; private set; }
        public string FirstError { get; private set; }
        /// <summary>Set when a requested reload was cancelled because compilation failed (for a one-off notification).</summary>
        public bool ReloadBlockedByErrors { get; private set; }
        public bool ReloadTimedOut { get; private set; }
        /// <summary>A compilation was requested and Unity hasn't started it yet.</summary>
        public bool IsWaitingForCompileStart => m_CompileRequestedAt >= 0 && State != CompileState.Compiling;

        private bool m_HasUnloadedBuild;   // assemblies built (successfully) since the last reload
        private bool m_ChangedDuringCompile; // the running build may not contain these changes
        private int m_AssembliesBuilt;     // in the current compilation
        private readonly List<string> m_Errors = new List<string>();
        private double m_CompileRequestedAt = -1;
        private double m_ReloadRequestedAt = -1;

        public void OnFilesChanged()
        {
            if (State == CompileState.Compiling) m_ChangedDuringCompile = true;
            if (State == CompileState.UpToDate || State == CompileState.CompiledAwaitingReload || State == CompileState.Failed)
                State = CompileState.ChangesPending;
        }

        public CompileAction RequestCompile(double now)
        {
            if (State == CompileState.Compiling || State == CompileState.Reloading) return CompileAction.None;
            m_CompileRequestedAt = now;
            return CompileAction.Compile;
        }

        public CompileAction RequestReload(double now)
        {
            switch (State)
            {
                case CompileState.Reloading:
                    return CompileAction.None;
                case CompileState.Compiling:
                    ReloadWhenCompiled = true;
                    return CompileAction.None;
                case CompileState.ChangesPending:
                case CompileState.Failed:
                    // Compile first (a failed build may have been fixed meanwhile); reload only if it succeeds.
                    ReloadWhenCompiled = true;
                    m_CompileRequestedAt = now;
                    return CompileAction.Compile;
                default:
                    return BeginReload(now);
            }
        }

        public void OnCompilationStarted()
        {
            State = CompileState.Compiling;
            m_CompileRequestedAt = -1;
            m_AssembliesBuilt = 0;
            m_ChangedDuringCompile = false;
            m_Errors.Clear();
            ReloadBlockedByErrors = false;
        }

        public void OnAssemblyCompiled(IEnumerable<string> errors)
        {
            m_AssembliesBuilt++;
            if (errors != null) m_Errors.AddRange(errors);
        }

        public CompileAction OnCompilationFinished(double now)
        {
            m_CompileRequestedAt = -1;
            ErrorCount = m_Errors.Count;
            FirstError = m_Errors.Count > 0 ? m_Errors[0] : null;

            if (m_Errors.Count > 0)
            {
                State = CompileState.Failed;
                ReloadBlockedByErrors = ReloadWhenCompiled;
                ReloadWhenCompiled = false;
                return CompileAction.None;
            }

            if (m_AssembliesBuilt > 0) m_HasUnloadedBuild = true;
            if (m_ChangedDuringCompile)
            {
                // Don't load a build that misses the latest edits: compile them first when a reload is waiting.
                m_ChangedDuringCompile = false;
                if (ReloadWhenCompiled)
                {
                    m_CompileRequestedAt = now;
                    State = CompileState.ChangesPending;
                    return CompileAction.Compile;
                }
                State = CompileState.ChangesPending;
                return CompileAction.None;
            }
            return Settle(now);
        }

        /// <summary>After a compilation (or a request that turned out to need none): reload, wait, or rest.</summary>
        private CompileAction Settle(double now)
        {
            if (!m_HasUnloadedBuild)
            {
                // Nothing new to load; a reload the user asked for still happens (it may be wanted for its own sake).
                if (ReloadWhenCompiled) return BeginReload(now);
                State = CompileState.UpToDate;
                return CompileAction.None;
            }
            if (LockMode && !ReloadWhenCompiled)
            {
                State = CompileState.CompiledAwaitingReload;
                return CompileAction.None;
            }
            // Unlocked: Unity reloads by itself once compilation is done; asking again is harmless and covers the
            // case where the lock was only released after compilation finished.
            return BeginReload(now);
        }

        private CompileAction BeginReload(double now)
        {
            State = CompileState.Reloading;
            ReloadWhenCompiled = false;
            ReloadTimedOut = false;
            m_ReloadRequestedAt = now;
            return CompileAction.Reload;
        }

        /// <summary>A domain loaded while the last build had failed (the errors themselves are gone with the old domain).</summary>
        public void RestoreFailed(string message)
        {
            m_Errors.Clear();
            m_Errors.Add(message);
            ErrorCount = 1;
            FirstError = message;
            State = CompileState.Failed;
        }

        /// <summary>Unity started reloading assemblies (whoever asked for it).</summary>
        public void OnReloadStarted() => State = CompileState.Reloading;

        /// <summary>The policy changed; leaving Lock mode with a build waiting loads it.</summary>
        public CompileAction OnLockModeChanged(bool lockMode, double now)
        {
            LockMode = lockMode;
            if (!lockMode && State == CompileState.CompiledAwaitingReload) return BeginReload(now);
            return CompileAction.None;
        }

        /// <summary>
        /// Called every editor update with Unity's compiling flag. Compilations themselves are tracked through the
        /// started/finished events (they also fire for auto-refresh compiles); the flag only delays the conclusion
        /// that a requested compilation had nothing to build.
        /// </summary>
        public CompileAction Tick(double now, bool unityCompiling)
        {
            double waited = m_CompileRequestedAt >= 0 ? now - m_CompileRequestedAt : -1;
            if (State != CompileState.Compiling && waited >= 0 &&
                ((!unityCompiling && waited > CompileStartTimeout) || waited > CompileStartHardTimeout))
            {
                m_CompileRequestedAt = -1;
                m_AssembliesBuilt = 0;
                if (State == CompileState.Failed) return CompileAction.None; // nothing was recompiled: the errors stand
                return Settle(now);
            }

            if (State == CompileState.Reloading && m_ReloadRequestedAt >= 0 && !unityCompiling && now - m_ReloadRequestedAt > ReloadTimeout)
            {
                // Something else holds the reload (another lock, Play Mode settings); let the user retry.
                m_ReloadRequestedAt = -1;
                ReloadTimedOut = true;
                State = m_HasUnloadedBuild ? CompileState.CompiledAwaitingReload : CompileState.UpToDate;
            }
            return CompileAction.None;
        }

        /// <summary>Clears the one-off flags once the UI has shown them.</summary>
        public void AcknowledgeNotifications()
        {
            ReloadBlockedByErrors = false;
            ReloadTimedOut = false;
        }
    }
}
