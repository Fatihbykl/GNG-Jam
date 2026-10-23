using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.Core
{
    public enum CompilationPolicy
    {
        UnityDefault = 0,         // Unity default: Auto-refresh on focus, automatic reload
        LockDomainReload = 1      // Lock Domain Reload: Compiles in background, AppDomain reload is paused
    }

    /// <summary>
    /// Compilation and domain reload control for the IDE. State lives in <see cref="CompilationStateMachine"/>, fed
    /// exclusively by Unity's public compilation and reload events; this class performs its actions (refresh and
    /// request compilation, release the reload lock and request a reload) and owns the single reload lock.
    /// It never writes global editor preferences: Auto Refresh stays the user's choice, and Enter Play Mode options
    /// are only written when that setting is changed.
    /// </summary>
    [InitializeOnLoad]
    public static class CompilationManager
    {
        public static event Action OnCompilationStateChanged;
        public static event Action OnPolicyChanged;
        /// <summary>A reload the user asked for could not happen; the argument says why.</summary>
        public static event Action<string> OnReloadProblem;

        private const string PREFS_POLICY_KEY = "FuzzyIDE_CompilationPolicy";
        private const string SESSION_DOMAIN_RELOAD_START = "FuzzyIDE_DomainReloadStartTime";
        private const string SESSION_LAST_COMPILE_DUR = "FuzzyIDE_LastCompileDuration";
        private const string SESSION_LAST_RELOAD_DUR = "FuzzyIDE_LastReloadDuration";

        private static readonly CompilationStateMachine s_Machine = new CompilationStateMachine();
        private static readonly HashSet<string> s_PendingFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static bool s_LockHeld;
        private static double s_CompileStartTime;
        private const int MaxCompileKicks = 3;
        private static int s_CompileKicks;
        private static double s_NextCompileKick;
        private static bool s_DriveCompilation; // we asked for the running/pending compilation and tick it along
        private static CompileState s_LastNotifiedState;

        public static CompileState State => s_Machine.State;
        public static int PendingFilesCount => s_PendingFiles.Count;
        public static IEnumerable<string> PendingFiles => s_PendingFiles;
        public static bool IsCompiling => s_Machine.State == CompileState.Compiling;
        public static bool IsReloadLocked => s_LockHeld;
        public static bool IsDomainReloadRequested => s_Machine.State == CompileState.Reloading || s_Machine.ReloadWhenCompiled;
        public static bool IsAwaitingReload => s_Machine.State == CompileState.CompiledAwaitingReload;
        public static bool ReloadWhenCompiled => s_Machine.ReloadWhenCompiled;
        public static int CompileErrorCount => s_Machine.ErrorCount;
        public static string FirstCompileError => s_Machine.FirstError;

        public static double LastCompileDuration
        {
            get => SessionState.GetFloat(SESSION_LAST_COMPILE_DUR, 0f);
            private set => SessionState.SetFloat(SESSION_LAST_COMPILE_DUR, (float)value);
        }

        public static double LastDomainReloadDuration
        {
            get => SessionState.GetFloat(SESSION_LAST_RELOAD_DUR, 0f);
            private set => SessionState.SetFloat(SESSION_LAST_RELOAD_DUR, (float)value);
        }

        public static CompilationPolicy CurrentPolicy
        {
            get
            {
                var settings = CodeIDESettings.Current?.Compilation;
                if (settings != null)
                {
                    if ((int)settings.Policy > 1 || (int)settings.Policy < 0)
                        settings.Policy = Sanitize((int)settings.Policy);
                    return settings.Policy;
                }
                return Sanitize(EditorPrefs.GetInt(PREFS_POLICY_KEY, (int)CompilationPolicy.UnityDefault));
            }
        }

        // The legacy value 2 (LockCompileAndReload) was a locking policy too; anything else unknown falls back to Unity's behavior.
        private static CompilationPolicy Sanitize(int value)
        {
            if (value == 0 || value == 1) return (CompilationPolicy)value;
            return value == 2 ? CompilationPolicy.LockDomainReload : CompilationPolicy.UnityDefault;
        }

        private static double Now => EditorApplication.timeSinceStartup;

        static CompilationManager()
        {
            CompilationPipeline.compilationStarted += OnCompilationStarted;
            CompilationPipeline.assemblyCompilationFinished += OnAssemblyCompilationFinished;
            CompilationPipeline.compilationFinished += OnCompilationFinished;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            AssemblyReloadEvents.afterAssemblyReload += OnAfterAssemblyReload;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            EditorApplication.update += OnEditorUpdate;

            if (LastCompileDuration > 600.0 || LastCompileDuration < 0.0) LastCompileDuration = 0.0;

            // The last build failed before this domain was loaded (e.g. the project opened with errors).
            if (EditorUtility.scriptCompilationFailed)
                s_Machine.RestoreFailed("Scripts have compile errors (see the Console).");

            EditorApplication.delayCall += () =>
            {
                s_Machine.LockMode = CurrentPolicy == CompilationPolicy.LockDomainReload;
                ApplyCurrentPolicy();
                Notify();
            };
        }

        // ---- Policy & lock ------------------------------------------------------------------------------------

        public static void SetPolicy(CompilationPolicy newPolicy)
        {
            var settings = CodeIDESettings.Current.Compilation;
            if (settings != null)
            {
                settings.Policy = newPolicy;
                CodeIDESettings.Save();
            }
            EditorPrefs.SetInt(PREFS_POLICY_KEY, (int)newPolicy);

            Perform(s_Machine.OnLockModeChanged(newPolicy == CompilationPolicy.LockDomainReload, Now));
            ApplyCurrentPolicy();
            // Unity Default reacts to changes on its own; pick up what accumulated while locked.
            if (newPolicy == CompilationPolicy.UnityDefault && s_Machine.State == CompileState.ChangesPending)
                RequestCompile();

            OnPolicyChanged?.Invoke();
            Notify();
        }

        /// <summary>Holds the reload lock in Lock mode, except while a reload is under way.</summary>
        public static void ApplyCurrentPolicy()
        {
            bool wantLock = CurrentPolicy == CompilationPolicy.LockDomainReload &&
                            s_Machine.State != CompileState.Reloading && !EditorApplication.isPlayingOrWillChangePlaymode;
            SetReloadLocked(wantLock);
        }

        public static void SetReloadLocked(bool lockReload)
        {
            // The lock is reference counted by Unity; this class holds at most one reference.
            if (s_LockHeld == lockReload) return;
            s_LockHeld = lockReload;
            if (lockReload) EditorApplication.LockReloadAssemblies();
            else EditorApplication.UnlockReloadAssemblies();
        }

        // ---- Pending files --------------------------------------------------------------------------------------

        public static void MarkFileModified(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return;
            string normalized = assetPath.Replace('\\', '/');
            string projectPath = Application.dataPath.Substring(0, Application.dataPath.Length - "Assets".Length).Replace('\\', '/');
            if (normalized.StartsWith(projectPath, StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized.Substring(projectPath.Length).TrimStart('/');
            }
            s_PendingFiles.Add(normalized);
            if (IsScript(normalized)) s_Machine.OnFilesChanged();
            Notify();
        }

        public static void ClearPendingFiles()
        {
            s_PendingFiles.Clear();
            Notify();
        }

        private static bool IsScript(string path) =>
            path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".asmdef", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".asmref", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".rsp", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);

        // ---- Commands -------------------------------------------------------------------------------------------

        /// <summary>Compiles changed scripts now. In Lock mode the result waits for <see cref="TriggerDomainReload"/>.</summary>
        public static void RequestCompile()
        {
            Perform(s_Machine.RequestCompile(Now));
            Notify();
        }

        /// <summary>
        /// Loads the latest code: compiles first when scripts changed (reloading only if that succeeds), otherwise
        /// releases the lock and reloads right away.
        /// </summary>
        public static void TriggerDomainReload()
        {
            Perform(s_Machine.RequestReload(Now));
            Notify();
            ReportProblems();
        }

        // Fallback when the pipeline can't be ticked: a synchronous refresh (works when reload isn't locked).
        private static void KickCompilation()
        {
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        }

        /// <summary>
        /// Moves a compilation we requested forward. With reload locked Unity would otherwise only start it on its
        /// next focus-change refresh (alt-tab).
        /// </summary>
        private static void DriveCompilation()
        {
            if (!s_DriveCompilation) return;
            if (!s_Machine.IsWaitingForCompileStart && s_Machine.State != CompileState.Compiling)
            {
                s_DriveCompilation = false;
                return;
            }
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;

            if (CompilationTicker.Available)
            {
                int status = CompilationTicker.Tick();
                // The finished event normally fired inside the tick; settle here if it didn't.
                if ((status == CompilationTicker.CompilationComplete || status == CompilationTicker.CompilationFailed) &&
                    s_Machine.State == CompileState.Compiling)
                {
                    OnCompilationFinished(null);
                }
            }
            else if (s_Machine.IsWaitingForCompileStart && s_CompileKicks < MaxCompileKicks && Now >= s_NextCompileKick && !EditorApplication.isUpdating)
            {
                s_CompileKicks++;
                s_NextCompileKick = Now + 1.0;
                KickCompilation();
            }
        }

        /// <summary>Kept for callers that poll; state advances in the editor update.</summary>
        public static void CheckCompilationStatus()
        {
            DriveCompilation();

            Perform(s_Machine.Tick(Now, EditorApplication.isCompiling));
            ReportProblems();
            if (s_Machine.State != s_LastNotifiedState) Notify();
        }

        private static void Perform(CompileAction action)
        {
            switch (action)
            {
                case CompileAction.Compile:
                    // Import scripts changed on disk, request the compilation and start it right away.
                    AssetDatabase.Refresh();
                    CompilationPipeline.RequestScriptCompilation();
                    s_DriveCompilation = true;
                    s_CompileKicks = 0;
                    s_NextCompileKick = Now + 1.0;
                    DriveCompilation();
                    break;
                case CompileAction.Reload:
                    SetReloadLocked(false);
                    EditorUtility.RequestScriptReload();
                    break;
            }
        }

        private static void ReportProblems()
        {
            if (s_Machine.ReloadBlockedByErrors)
            {
                string first = s_Machine.FirstError;
                OnReloadProblem?.Invoke($"Reload cancelled: {s_Machine.ErrorCount} compile error{(s_Machine.ErrorCount == 1 ? "" : "s")}" + (first != null ? $"\n{first}" : ""));
            }
            else if (s_Machine.ReloadTimedOut)
            {
                OnReloadProblem?.Invoke(EditorApplication.isPlaying
                    ? "Reload is waiting for Play Mode to end (Preferences > General > Script Changes While Playing)."
                    : "Unity did not reload; another tool may be holding the reload lock. Try Reload Domain again.");
                ApplyCurrentPolicy();
            }
            s_Machine.AcknowledgeNotifications();
        }

        private static void Notify()
        {
            s_LastNotifiedState = s_Machine.State;
            OnCompilationStateChanged?.Invoke();
        }

        // ---- Unity events ---------------------------------------------------------------------------------------

        private static void OnCompilationStarted(object context)
        {
            s_CompileStartTime = Now;
            s_Machine.OnCompilationStarted();
            Notify();
        }

        private static void OnAssemblyCompilationFinished(string assemblyPath, CompilerMessage[] messages)
        {
            var errors = messages?.Where(m => m.type == CompilerMessageType.Error)
                .Select(m => string.IsNullOrEmpty(m.file) ? m.message : $"{m.file}({m.line}): {StripLocation(m.message)}")
                .ToList();
            s_Machine.OnAssemblyCompiled(errors);
        }

        // Compiler messages repeat the location ("Assets/A.cs(3,4): error CS1002: ; expected"); keep the error part.
        private static string StripLocation(string message)
        {
            int index = message.IndexOf(": error ", StringComparison.Ordinal);
            return index >= 0 ? message.Substring(index + 2) : message;
        }

        private static void OnCompilationFinished(object context)
        {
            s_DriveCompilation = false;
            if (s_CompileStartTime > 0)
            {
                LastCompileDuration = Now - s_CompileStartTime;
                s_CompileStartTime = 0;
            }
            bool userAskedForReload = s_Machine.ReloadWhenCompiled;
            bool lockWasHeld = s_LockHeld;
            var action = s_Machine.OnCompilationFinished(Now);
            if (s_Machine.State != CompileState.Failed) s_PendingFiles.Clear();
            // Unlocked, Unity reloads new assemblies by itself; asking again would reload twice.
            if (action == CompileAction.Reload && !lockWasHeld && !userAskedForReload) action = CompileAction.None;
            Perform(action);
            ReportProblems();
            Notify();
        }

        private static void OnBeforeAssemblyReload()
        {
            s_Machine.OnReloadStarted();
            SessionState.SetFloat(SESSION_DOMAIN_RELOAD_START, (float)Now);
        }

        private static void OnAfterAssemblyReload()
        {
            float startTime = SessionState.GetFloat(SESSION_DOMAIN_RELOAD_START, 0f);
            if (startTime > 0f)
            {
                LastDomainReloadDuration = Now - startTime;
                SessionState.EraseFloat(SESSION_DOMAIN_RELOAD_START);
            }
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            switch (state)
            {
                case PlayModeStateChange.ExitingEditMode:
                    // Play the code that is on disk: release the lock and let a waiting build (or pending changes) load.
                    SetReloadLocked(false);
                    if (s_Machine.State == CompileState.ChangesPending) KickCompilation();
                    // With domain reload disabled for Play Mode nothing else would load the waiting build first.
                    else if (s_Machine.State == CompileState.CompiledAwaitingReload) EditorUtility.RequestScriptReload();
                    break;
                case PlayModeStateChange.EnteredEditMode:
                    ApplyCurrentPolicy();
                    Notify();
                    break;
            }
        }

        private static void OnEditorUpdate() => CheckCompilationStatus();

        // ---- Settings -------------------------------------------------------------------------------------------

        /// <summary>Writes the project's Enter Play Mode options; only called when the user changes the setting.</summary>
        public static void ApplyFastPlayModeSettings(bool disableDomainReload)
        {
            UnityEditor.EditorSettings.enterPlayModeOptionsEnabled = disableDomainReload;
            UnityEditor.EditorSettings.enterPlayModeOptions = disableDomainReload
                ? EnterPlayModeOptions.DisableDomainReload | EnterPlayModeOptions.DisableSceneReload
                : EnterPlayModeOptions.None;
        }
    }
}
