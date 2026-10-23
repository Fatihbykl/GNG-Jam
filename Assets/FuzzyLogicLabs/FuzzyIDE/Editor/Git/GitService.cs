using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FuzzyLogicLabs.FuzzyIDE.Editor.Core;
using UnityEditor;
using UnityEngine;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.Git
{
    public class GitCommandResult
    {
        public int ExitCode;
        public string Output = "";
        public string Error = "";
        public bool Success => ExitCode == 0;
    }

    [InitializeOnLoad]
    public static class GitService
    {
        public static GitRepositoryStatus CurrentStatus { get; private set; } = new GitRepositoryStatus();
        public static bool IsGitRepository => CurrentStatus.IsGitRepository;
        public static string RepositoryRoot => CurrentStatus.RepositoryRoot;
        public static string CurrentBranch => CurrentStatus.CurrentBranch;

        public static event Action<GitRepositoryStatus> OnStatusUpdated;
        public static event Action<string> OnGitError;

        private static bool s_IsRefreshing = false;
        private static double s_LastRefreshTime = 0;

        static GitService()
        {
            DetectRepository();
            EditorApplication.delayCall += () =>
            {
                Initialize();
            };
        }

        public static void Initialize()
        {
            DetectRepository();
            _ = RefreshStatusAsync();

            EditorApplication.update -= OnEditorUpdate;
            EditorApplication.update += OnEditorUpdate;

            FileWatcherService.OnGitIndexChanged -= OnGitIndexChangedFromWatcher;
            FileWatcherService.OnGitIndexChanged += OnGitIndexChangedFromWatcher;

            FileWatcherService.OnFilesChangedBatch -= OnFilesChangedFromWatcher;
            FileWatcherService.OnFilesChangedBatch += OnFilesChangedFromWatcher;
        }

        private static void OnGitIndexChangedFromWatcher()
        {
            if (IsGitRepository && !s_IsRefreshing)
            {
                _ = RefreshStatusAsync();
            }
        }

        private static void OnFilesChangedFromWatcher(IReadOnlyList<FileWatcherEvent> events)
        {
            if (IsGitRepository && !s_IsRefreshing)
            {
                _ = RefreshStatusAsync();
            }
        }

        private static void OnEditorUpdate()
        {
            if (EditorApplication.isCompiling || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                return;
            }

            double now = EditorApplication.timeSinceStartup;
            if (now - s_LastRefreshTime > 10.0) // Auto-refresh background polling every 10s
            {
                s_LastRefreshTime = now;
                if (!s_IsRefreshing && IsGitRepository)
                {
                    _ = RefreshStatusAsync();
                }
            }

            // Checked every 30s; fetches when FETCH_HEAD is older than the configured interval.
            int autoFetchMinutes = CodeIDESettings.Current?.Git?.AutoFetchMinutes ?? 0;
            if (autoFetchMinutes > 0 && now - s_LastAutoFetchCheck > 30.0)
            {
                s_LastAutoFetchCheck = now;
                if (!s_IsAutoFetching && CurrentStatus.HasRemote && CurrentStatus.OperationState == GitRepoState.None &&
                    DateTime.Now >= s_AutoFetchRetryAfter &&
                    (DateTime.Now - LastFetchTime).TotalMinutes >= autoFetchMinutes)
                {
                    _ = AutoFetchAsync(autoFetchMinutes);
                }
            }
        }

        private static bool s_IsAutoFetching;
        private static double s_LastAutoFetchCheck;
        private static DateTime s_AutoFetchRetryAfter = DateTime.MinValue;

        /// <summary>Background fetch: failures (offline, auth) are logged once per session instead of raising the error banner every few minutes.</summary>
        private static async Task AutoFetchAsync(int intervalMinutes)
        {
            s_IsAutoFetching = true;
            try
            {
                var result = await ExecuteGitAsync("fetch --prune --all --quiet", timeoutMs: NetworkTimeoutMs);
                if (result.Success)
                {
                    await RefreshStatusAsync();
                    return;
                }

                // FETCH_HEAD wasn't updated, so without this the next check would retry immediately.
                s_AutoFetchRetryAfter = DateTime.Now.AddMinutes(intervalMinutes);
                if (!s_AutoFetchErrorLogged)
                {
                    s_AutoFetchErrorLogged = true;
                    UnityEngine.Debug.LogWarning("[FuzzyIDE Git] Auto-fetch failed: " + GitErrorInterpreter.Interpret(result.Error).Message);
                }
            }
            finally
            {
                s_IsAutoFetching = false;
            }
        }

        private static bool s_AutoFetchErrorLogged;

        public static string GetGitExecutablePath()
        {
            string customPath = CodeIDESettings.Current?.Git?.CustomGitPath;
            if (!string.IsNullOrEmpty(customPath) && File.Exists(customPath))
            {
                return customPath;
            }

            // Standard fallback paths on Windows if git isn't resolved directly
            if (Application.platform == RuntimePlatform.WindowsEditor)
            {
                string[] candidates = new string[]
                {
                    "git",
                    @"C:\Program Files\Git\cmd\git.exe",
                    @"C:\Program Files\Git\bin\git.exe",
                    @"C:\Program Files (x86)\Git\cmd\git.exe",
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\Git\cmd\git.exe")
                };

                foreach (var candidate in candidates)
                {
                    if (candidate == "git" || File.Exists(candidate))
                        return candidate;
                }
            }

            return "git";
        }

        public static bool DetectRepository(string startingPath = null)
        {
            if (string.IsNullOrEmpty(startingPath))
            {
                startingPath = Directory.GetCurrentDirectory();
            }

            string currentDir = startingPath;
            while (!string.IsNullOrEmpty(currentDir))
            {
                if (Directory.Exists(Path.Combine(currentDir, ".git")) || File.Exists(Path.Combine(currentDir, ".git")))
                {
                    CurrentStatus.IsGitRepository = true;
                    CurrentStatus.RepositoryRoot = currentDir.Replace('\\', '/');
                    CurrentStatus.RepositoryName = Path.GetFileName(CurrentStatus.RepositoryRoot);
                    return true;
                }

                var parent = Directory.GetParent(currentDir);
                if (parent == null || parent.FullName == currentDir) break;
                currentDir = parent.FullName;
            }

            CurrentStatus.IsGitRepository = false;
            CurrentStatus.RepositoryRoot = Directory.GetCurrentDirectory().Replace('\\', '/');
            CurrentStatus.RepositoryName = Path.GetFileName(CurrentStatus.RepositoryRoot);
            return false;
        }

        /// <summary>Upper bound for commands that talk to a remote (push/pull/fetch), so a stuck credential helper can't hang the UI forever.</summary>
        public const int NetworkTimeoutMs = 10 * 60 * 1000;

        /// <param name="standardInput">Written to git's stdin as UTF-8 (no BOM); stdin is always closed so git never waits on it.</param>
        /// <param name="timeoutMs">Kill the process after this many milliseconds; 0 means no limit.</param>
        public static async Task<GitCommandResult> ExecuteGitAsync(string arguments, string workingDir = null, CancellationToken cancellationToken = default,
            string standardInput = null, int timeoutMs = 0)
        {
            if (string.IsNullOrEmpty(workingDir))
            {
                workingDir = !string.IsNullOrEmpty(CurrentStatus.RepositoryRoot) ? CurrentStatus.RepositoryRoot : Directory.GetCurrentDirectory();
            }

            string gitExe = GetGitExecutablePath();

            return await Task.Run(() =>
            {
                var result = new GitCommandResult();
                try
                {
                    var startInfo = new ProcessStartInfo
                    {
                        FileName = gitExe,
                        Arguments = arguments,
                        WorkingDirectory = workingDir,
                        RedirectStandardInput = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        StandardOutputEncoding = Encoding.UTF8,
                        StandardErrorEncoding = Encoding.UTF8
                    };
                    // No hidden terminal prompts (they would block forever); GUI credential helpers still work.
                    startInfo.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";
                    // English messages so error detection doesn't depend on the user's locale.
                    startInfo.EnvironmentVariables["LANGUAGE"] = "C";
                    startInfo.EnvironmentVariables["LC_MESSAGES"] = "C";
                    // Never open an editor (merge/rebase --continue would otherwise wait on an invisible one); keep git's default messages.
                    startInfo.EnvironmentVariables["GIT_EDITOR"] = "true";
                    startInfo.EnvironmentVariables["GIT_SEQUENCE_EDITOR"] = "true";

                    using (var process = new Process { StartInfo = startInfo })
                    {
                        var outputBuilder = new StringBuilder();
                        var errorBuilder = new StringBuilder();

                        process.OutputDataReceived += (s, e) => { if (e.Data != null) outputBuilder.Append(e.Data).Append('\n'); };
                        process.ErrorDataReceived += (s, e) => { if (e.Data != null) errorBuilder.Append(e.Data).Append('\n'); };

                        process.Start();
                        process.BeginOutputReadLine();
                        process.BeginErrorReadLine();

                        try
                        {
                            if (!string.IsNullOrEmpty(standardInput))
                            {
                                byte[] bytes = new UTF8Encoding(false).GetBytes(standardInput);
                                process.StandardInput.BaseStream.Write(bytes, 0, bytes.Length);
                            }
                            process.StandardInput.Close();
                        }
                        catch (IOException) { } // git exited before reading stdin

                        var stopwatch = Stopwatch.StartNew();
                        while (!process.WaitForExit(100))
                        {
                            bool timedOut = timeoutMs > 0 && stopwatch.ElapsedMilliseconds > timeoutMs;
                            if (cancellationToken.IsCancellationRequested || timedOut)
                            {
                                try { process.Kill(); } catch { }
                                result.ExitCode = -1;
                                result.Error = timedOut
                                    ? $"git {arguments.Split(' ')[0]} timed out after {timeoutMs / 1000}s."
                                    : "Command cancelled.";
                                return result;
                            }
                        }
                        process.WaitForExit(); // flush async output handlers

                        result.ExitCode = process.ExitCode;
                        result.Output = outputBuilder.ToString();
                        result.Error = errorBuilder.ToString();
                    }
                }
                catch (Exception ex)
                {
                    result.ExitCode = -1;
                    result.Error = ex.Message;
                }

                return result;
            }, cancellationToken);
        }

        private static Task<GitRepositoryStatus> s_RefreshTask;
        private static Task<GitRepositoryStatus> s_QueuedRefreshTask;

        /// <summary>
        /// Refreshes the status. A call made while a refresh is running schedules exactly one follow-up run
        /// (shared by all such callers), so callers awaiting this after a git operation always see its effect.
        /// </summary>
        public static Task<GitRepositoryStatus> RefreshStatusAsync()
        {
            if (s_RefreshTask == null || s_RefreshTask.IsCompleted)
            {
                s_RefreshTask = RefreshStatusCoreAsync();
                return s_RefreshTask;
            }

            return s_QueuedRefreshTask ??= RefreshAfterAsync(s_RefreshTask);
        }

        private static async Task<GitRepositoryStatus> RefreshAfterAsync(Task<GitRepositoryStatus> running)
        {
            try { await running; } catch { }
            s_QueuedRefreshTask = null;
            s_RefreshTask = RefreshStatusCoreAsync();
            return await s_RefreshTask;
        }

        private static async Task<GitRepositoryStatus> RefreshStatusCoreAsync()
        {
            s_IsRefreshing = true;

            try
            {
                if (!DetectRepository())
                {
                    NotifyStatusUpdated(CurrentStatus);
                    return CurrentStatus;
                }

                // -z: NUL-separated, unquoted paths (non-ASCII names stay intact).
                // --no-optional-locks: background polling must not grab index.lock and break the user's own git commands.
                var cmdResult = await ExecuteGitAsync("--no-optional-locks -c core.quotePath=false status --porcelain=v1 -z -b -u");
                if (!cmdResult.Success)
                {
                    CurrentStatus.IsGitRepository = false;
                    NotifyStatusUpdated(CurrentStatus);
                    return CurrentStatus;
                }

                var status = new GitRepositoryStatus
                {
                    IsGitRepository = true,
                    RepositoryRoot = CurrentStatus.RepositoryRoot,
                    RepositoryName = CurrentStatus.RepositoryName
                };

                ParsePorcelainOutput(cmdResult.Output, status);
                status.DefaultRemote = await GetDefaultRemoteAsync();
                status.OperationState = DetectOperationState(status.RepositoryRoot);
                CurrentStatus = status;
                s_LastRefreshTime = EditorApplication.timeSinceStartup;

                NotifyStatusUpdated(CurrentStatus);
                return CurrentStatus;
            }
            finally
            {
                s_IsRefreshing = false;
            }
        }

        // `git remote` only changes when .git/config does, so cache it against the config's timestamp
        // instead of spawning a second process on every status refresh.
        private static string s_CachedDefaultRemote;
        private static DateTime s_CachedRemoteConfigTime = DateTime.MinValue;

        private static async Task<string> GetDefaultRemoteAsync()
        {
            string gitDir = ResolveGitDir(CurrentStatus.RepositoryRoot);
            string configPath = gitDir != null ? Path.Combine(gitDir, "config") : null;
            DateTime configTime = configPath != null && File.Exists(configPath) ? File.GetLastWriteTimeUtc(configPath) : DateTime.MinValue;
            if (s_CachedDefaultRemote != null && configTime != DateTime.MinValue && configTime == s_CachedRemoteConfigTime)
            {
                return s_CachedDefaultRemote;
            }

            var result = await ExecuteGitAsync("remote");
            var remotes = result.Success
                ? result.Output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(r => r.Trim()).ToList()
                : new List<string>();

            s_CachedDefaultRemote = remotes.Contains("origin") ? "origin" : (remotes.FirstOrDefault() ?? "");
            s_CachedRemoteConfigTime = configTime;
            return s_CachedDefaultRemote;
        }

        private static string ResolveRemote(string remote)
        {
            if (!string.IsNullOrEmpty(remote)) return remote;
            return CurrentStatus.HasRemote ? CurrentStatus.DefaultRemote : "origin";
        }

        /// <summary>Quotes a single command-line argument per the Windows CommandLineToArgvW rules (also safe for mono on macOS/Linux).</summary>
        public static string QuoteArg(string arg)
        {
            if (string.IsNullOrEmpty(arg)) return "\"\"";
            if (arg.IndexOfAny(new[] { ' ', '\t', '"', '\'', '&', '|', '<', '>', '^', '%' }) < 0) return arg;

            var sb = new StringBuilder("\"");
            int backslashes = 0;
            foreach (char c in arg)
            {
                if (c == '\\')
                {
                    backslashes++;
                    continue;
                }
                if (c == '"')
                {
                    sb.Append('\\', backslashes * 2 + 1);
                }
                else
                {
                    sb.Append('\\', backslashes);
                }
                backslashes = 0;
                sb.Append(c);
            }
            sb.Append('\\', backslashes * 2);
            sb.Append('"');
            return sb.ToString();
        }

        public static void ParsePorcelainOutput(string output, GitRepositoryStatus status)
        {
            status.StagedFiles.Clear();
            status.UnstagedFiles.Clear();
            status.UntrackedFiles.Clear();
            status.ConflictedFiles.Clear();

            // `-z` output is NUL-separated with raw paths and a rename's original path as the *next* entry;
            // plain output is line-based with C-quoted paths and "old -> new" renames. Both are accepted.
            bool nulSeparated = output.IndexOf('\0') >= 0;
            string[] entries = nulSeparated ? output.Split('\0') : output.Split('\n');

            for (int i = 0; i < entries.Length; i++)
            {
                string entry = nulSeparated ? entries[i] : entries[i].TrimEnd('\r');
                if (string.IsNullOrWhiteSpace(entry)) continue;

                // Branch header: ## main...origin/main [ahead 1, behind 2]
                if (entry.StartsWith("## "))
                {
                    ParseBranchHeader(entry.Substring(3).Trim(), status);
                    continue;
                }

                if (entry.Length < 4) continue;

                char indexCode = entry[0];
                char workTreeCode = entry[1];
                string filePath = entry.Substring(3);
                string oldPath = null;

                if (nulSeparated)
                {
                    if ((indexCode == 'R' || indexCode == 'C') && i + 1 < entries.Length)
                    {
                        oldPath = entries[++i];
                    }
                }
                else
                {
                    filePath = filePath.Trim();
                    if (filePath.Contains(" -> "))
                    {
                        var parts = filePath.Split(new[] { " -> " }, StringSplitOptions.None);
                        oldPath = Unquote(parts[0].Trim());
                        filePath = parts[1].Trim();
                    }
                    filePath = Unquote(filePath);
                }

                var fileStatus = new GitFileStatus
                {
                    Path = filePath.Replace('\\', '/'),
                    FullPath = Path.Combine(status.RepositoryRoot, filePath).Replace('\\', '/'),
                    FileName = Path.GetFileName(filePath.TrimEnd('/')),
                    OldPath = oldPath?.Replace('\\', '/'),
                    StagedState = ParseStateCode(indexCode),
                    WorkingTreeState = ParseStateCode(workTreeCode)
                };

                if (indexCode == '?' && workTreeCode == '?')
                {
                    fileStatus.StagedState = GitFileState.None;
                    fileStatus.WorkingTreeState = GitFileState.Untracked;
                    status.UntrackedFiles.Add(fileStatus);
                }
                else if (IsConflictCode(indexCode, workTreeCode))
                {
                    // Unmerged paths belong to neither the index nor the working tree list until resolved.
                    fileStatus.StagedState = GitFileState.None;
                    fileStatus.WorkingTreeState = GitFileState.Conflict;
                    fileStatus.ConflictCode = $"{indexCode}{workTreeCode}";
                    status.ConflictedFiles.Add(fileStatus);
                }
                else
                {
                    if (fileStatus.IsStaged)
                    {
                        status.StagedFiles.Add(fileStatus);
                    }
                    if (fileStatus.IsUnstaged)
                    {
                        status.UnstagedFiles.Add(fileStatus);
                    }
                }
            }
        }

        public static void ParseBranchHeader(string header, GitRepositoryStatus status)
        {
            // e.g., "main...origin/main [ahead 1, behind 2]"
            // or "main"
            // or "Initial commit on main"
            // or "HEAD (no branch)"
            if (header.StartsWith("Initial commit on "))
            {
                status.CurrentBranch = header.Substring("Initial commit on ".Length).Trim();
                return;
            }

            if (header.StartsWith("No commits yet on "))
            {
                status.CurrentBranch = header.Substring("No commits yet on ".Length).Trim();
                return;
            }

            if (header.StartsWith("HEAD (no branch)"))
            {
                status.CurrentBranch = "DETACHED HEAD";
                status.IsDetached = true;
                return;
            }

            int dots = header.IndexOf("...");
            if (dots != -1)
            {
                status.CurrentBranch = header.Substring(0, dots).Trim();
                string rest = header.Substring(dots + 3).Trim();

                int bracket = rest.IndexOf('[');
                if (bracket != -1)
                {
                    status.UpstreamBranch = rest.Substring(0, bracket).Trim();
                    string counts = rest.Substring(bracket + 1).TrimEnd(']');

                    var parts = counts.Split(',');
                    foreach (var part in parts)
                    {
                        string p = part.Trim();
                        if (p == "gone")
                        {
                            status.UpstreamGone = true;
                        }
                        else if (p.StartsWith("ahead "))
                        {
                            int.TryParse(p.Substring(6), out status.AheadBy);
                        }
                        else if (p.StartsWith("behind "))
                        {
                            int.TryParse(p.Substring(7), out status.BehindBy);
                        }
                    }
                }
                else
                {
                    status.UpstreamBranch = rest;
                }
            }
            else
            {
                status.CurrentBranch = header;
            }
        }

        // Unmerged combinations from `git status --porcelain`: DD AU UD UA DU AA UU.
        private static bool IsConflictCode(char x, char y)
        {
            return x == 'U' || y == 'U' || (x == 'A' && y == 'A') || (x == 'D' && y == 'D');
        }

        private static string Unquote(string path)
        {
            if (path.Length >= 2 && path[0] == '"' && path[path.Length - 1] == '"')
            {
                return path.Substring(1, path.Length - 2).Replace("\\\"", "\"").Replace("\\\\", "\\");
            }
            return path;
        }

        /// <summary>Detects an in-progress merge / rebase / cherry-pick / revert from the marker files git leaves in its directory.</summary>
        public static GitRepoState DetectOperationState(string repositoryRoot)
        {
            string gitDir = ResolveGitDir(repositoryRoot);
            if (gitDir == null) return GitRepoState.None;

            if (Directory.Exists(Path.Combine(gitDir, "rebase-merge")) || Directory.Exists(Path.Combine(gitDir, "rebase-apply")))
                return GitRepoState.Rebasing;
            if (File.Exists(Path.Combine(gitDir, "MERGE_HEAD"))) return GitRepoState.Merging;
            if (File.Exists(Path.Combine(gitDir, "CHERRY_PICK_HEAD"))) return GitRepoState.CherryPicking;
            if (File.Exists(Path.Combine(gitDir, "REVERT_HEAD"))) return GitRepoState.Reverting;
            return GitRepoState.None;
        }

        /// <summary>Returns the .git directory, following the "gitdir: ..." pointer file used by worktrees and submodules.</summary>
        public static string ResolveGitDir(string repositoryRoot)
        {
            if (string.IsNullOrEmpty(repositoryRoot)) return null;
            string dotGit = Path.Combine(repositoryRoot, ".git");
            if (Directory.Exists(dotGit)) return dotGit;
            if (!File.Exists(dotGit)) return null;

            try
            {
                string line = File.ReadAllText(dotGit).Trim();
                const string prefix = "gitdir:";
                if (!line.StartsWith(prefix)) return null;
                string dir = line.Substring(prefix.Length).Trim();
                return Path.IsPathRooted(dir) ? dir : Path.GetFullPath(Path.Combine(repositoryRoot, dir));
            }
            catch (IOException)
            {
                return null;
            }
        }

        private static GitFileState ParseStateCode(char code)
        {
            switch (code)
            {
                case 'M': return GitFileState.Modified;
                case 'A': return GitFileState.Added;
                case 'D': return GitFileState.Deleted;
                case 'R': return GitFileState.Renamed;
                case 'C': return GitFileState.Copied;
                case 'U': return GitFileState.Conflict;
                case '?': return GitFileState.Untracked;
                default: return GitFileState.None;
            }
        }

        private static bool s_StatusNotificationPending = false;

        private static void NotifyStatusUpdated(GitRepositoryStatus status)
        {
            if (s_StatusNotificationPending) return;
            s_StatusNotificationPending = true;

            EditorApplication.delayCall += () =>
            {
                s_StatusNotificationPending = false;
                try
                {
                    OnStatusUpdated?.Invoke(status);
                }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogException(ex);
                }
            };
        }

        // --- Git Operations ---

        /// <summary>
        /// Runs a pathspec-taking command with the paths fed through stdin (NUL-separated) instead of the command line,
        /// so long selections can't hit the Windows command-line limit and names like "Sprite [1].png" aren't treated as globs.
        /// </summary>
        private static Task<GitCommandResult> ExecuteWithPathsAsync(string command, IEnumerable<string> paths)
        {
            return ExecuteGitAsync($"--literal-pathspecs {command} --pathspec-from-file=- --pathspec-file-nul",
                standardInput: string.Join("\0", paths));
        }

        private static string JoinQuotedPaths(IEnumerable<string> paths) => string.Join(" ", paths.Select(QuoteArg));

        public static async Task<bool> StageFilesAsync(IEnumerable<string> paths)
        {
            var result = await ExecuteWithPathsAsync("add", paths);
            if (result.Success)
            {
                await RefreshStatusAsync();
                return true;
            }
            OnGitError?.Invoke(result.Error);
            return false;
        }

        public static async Task<bool> StageAllAsync()
        {
            var result = await ExecuteGitAsync("add -A");
            if (result.Success)
            {
                await RefreshStatusAsync();
                return true;
            }
            OnGitError?.Invoke(result.Error);
            return false;
        }

        public static async Task<bool> UnstageFilesAsync(IEnumerable<string> paths)
        {
            var pathList = paths.ToList();
            var result = await ExecuteWithPathsAsync("restore --staged", pathList);
            if (!result.Success && !await HasHeadCommitAsync())
            {
                // No commits yet: there is no HEAD to restore from, so just drop the paths from the index.
                result = await ExecuteWithPathsAsync("rm --cached -r -q", pathList);
            }
            else if (!result.Success)
            {
                // Fallback for git < 2.23 (no `restore`)
                result = await ExecuteGitAsync($"--literal-pathspecs reset -q HEAD -- {JoinQuotedPaths(pathList)}");
            }

            if (result.Success)
            {
                await RefreshStatusAsync();
                return true;
            }
            OnGitError?.Invoke(result.Error);
            return false;
        }

        public static async Task<bool> UnstageAllAsync()
        {
            GitCommandResult result;
            if (!await HasHeadCommitAsync())
            {
                result = await ExecuteGitAsync("rm --cached -r -q .");
            }
            else
            {
                result = await ExecuteGitAsync("restore --staged .");
                if (!result.Success)
                {
                    result = await ExecuteGitAsync("reset -q HEAD");
                }
            }
            if (result.Success)
            {
                await RefreshStatusAsync();
                return true;
            }
            OnGitError?.Invoke(result.Error);
            return false;
        }

        /// <summary>False on a freshly initialised repository that has no commits yet.</summary>
        public static async Task<bool> HasHeadCommitAsync()
        {
            var result = await ExecuteGitAsync("rev-parse -q --verify HEAD");
            return result.Success;
        }

        public static async Task<bool> DiscardChangesAsync(IEnumerable<string> paths)
        {
            var pathList = paths.ToList();
            var result = await ExecuteWithPathsAsync("restore", pathList);
            if (!result.Success)
            {
                // Fallback for git < 2.23 (no `restore`)
                result = await ExecuteGitAsync($"--literal-pathspecs checkout -- {JoinQuotedPaths(pathList)}");
            }

            if (result.Success)
            {
                await RefreshStatusAsync();
                AssetDatabase.Refresh();
                return true;
            }
            OnGitError?.Invoke(result.Error);
            return false;
        }

        public static async Task<bool> DiscardUntrackedFilesAsync(IEnumerable<string> paths)
        {
            // `clean` has no --pathspec-from-file; quote each path instead.
            var result = await ExecuteGitAsync($"--literal-pathspecs clean -fd -- {JoinQuotedPaths(paths)}");
            if (result.Success)
            {
                await RefreshStatusAsync();
                AssetDatabase.Refresh();
                return true;
            }
            OnGitError?.Invoke(result.Error);
            return false;
        }

        public static async Task<bool> DiscardAllChangesAsync()
        {
            var result = await ExecuteGitAsync("restore .");
            if (!result.Success)
            {
                result = await ExecuteGitAsync("checkout -- .");
            }

            if (result.Success)
            {
                await RefreshStatusAsync();
                AssetDatabase.Refresh();
                return true;
            }
            OnGitError?.Invoke(result.Error);
            return false;
        }

        public static async Task<bool> DiscardAllUntrackedAsync()
        {
            var result = await ExecuteGitAsync("clean -fd");
            if (result.Success)
            {
                await RefreshStatusAsync();
                AssetDatabase.Refresh();
                return true;
            }
            OnGitError?.Invoke(result.Error);
            return false;
        }


        public static async Task<bool> CommitAsync(string message, bool amend = false)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                OnGitError?.Invoke("Commit message cannot be empty.");
                return false;
            }

            string tempFile = Path.Combine(Path.GetTempPath(), $"fuzzyide_commit_{Guid.NewGuid():N}.txt");
            try
            {
                File.WriteAllText(tempFile, message, Encoding.UTF8);
                string args = $"commit --file=\"{tempFile}\"" + (amend ? " --amend" : "");
                var result = await ExecuteGitAsync(args);
                if (result.Success)
                {
                    await RefreshStatusAsync();
                    return true;
                }
                OnGitError?.Invoke(result.Error);
                return false;
            }
            finally
            {
                if (File.Exists(tempFile))
                {
                    try { File.Delete(tempFile); } catch { }
                }
            }
        }

        /// <summary>True when the checked-out branch tracks a remote branch. Asks git directly so it is correct even if CurrentStatus is stale.</summary>
        public static async Task<bool> HasUpstreamAsync()
        {
            var result = await ExecuteGitAsync("rev-parse --abbrev-ref --symbolic-full-name @{u}");
            return result.Success && !string.IsNullOrWhiteSpace(result.Output);
        }

        /// <summary>Pushes the current branch and sets its upstream (`git push -u remote branch`).</summary>
        public static async Task<bool> PublishBranchAsync(string remote = null)
        {
            var branchResult = await ExecuteGitAsync("symbolic-ref --short -q HEAD");
            string branch = branchResult.Output.Trim();
            if (!branchResult.Success || string.IsNullOrEmpty(branch))
            {
                OnGitError?.Invoke("Cannot publish: HEAD is detached. Create a branch first.");
                return false;
            }

            return await PushAsync(remote, branch);
        }

        /// <summary>
        /// Pushes to the upstream. If the current branch has never been published (no upstream),
        /// falls back to `push -u` so new branches get published instead of failing.
        /// </summary>
        public static async Task<bool> PushAsync(string remote = null, string branch = null)
        {
            if (string.IsNullOrEmpty(branch) && !await HasUpstreamAsync())
            {
                return await PublishBranchAsync(remote);
            }

            remote = ResolveRemote(remote);
            string args = string.IsNullOrEmpty(branch)
                ? $"push {QuoteArg(remote)}"
                : $"push -u {QuoteArg(remote)} {QuoteArg(branch)}";
            var result = await ExecuteGitAsync(args, timeoutMs: NetworkTimeoutMs);
            if (result.Success)
            {
                await RefreshStatusAsync();
                return true;
            }
            OnGitError?.Invoke(!string.IsNullOrEmpty(result.Error) ? result.Error : result.Output);
            return false;
        }

        public static async Task<bool> PullAsync(string remote = null, string branch = null)
        {
            // Without an explicit strategy git >= 2.27 refuses to pull divergent branches.
            string strategy = (CodeIDESettings.Current?.Git?.PullStrategy ?? GitPullStrategy.Merge) switch
            {
                GitPullStrategy.Rebase => "--rebase",
                GitPullStrategy.FastForwardOnly => "--ff-only",
                _ => "--no-rebase"
            };

            // A bare `git pull` uses the upstream; naming a remote without a branch would fail on non-default branches.
            string args = string.IsNullOrEmpty(branch)
                ? $"pull {strategy}"
                : $"pull {strategy} {QuoteArg(ResolveRemote(remote))} {QuoteArg(branch)}";
            var result = await ExecuteGitAsync(args, timeoutMs: NetworkTimeoutMs);
            if (result.Success)
            {
                await RefreshStatusAsync();
                AssetDatabase.Refresh();
                return true;
            }
            OnGitError?.Invoke(!string.IsNullOrEmpty(result.Error) ? result.Error : result.Output);
            return false;
        }

        /// <summary>When the repository last fetched, from .git/FETCH_HEAD, so it survives domain reloads and counts fetches made outside the IDE.</summary>
        public static DateTime LastFetchTime
        {
            get
            {
                string gitDir = ResolveGitDir(CurrentStatus.RepositoryRoot);
                string fetchHead = gitDir != null ? Path.Combine(gitDir, "FETCH_HEAD") : null;
                return fetchHead != null && File.Exists(fetchHead) ? File.GetLastWriteTime(fetchHead) : DateTime.MinValue;
            }
        }

        /// <param name="remote">null fetches every remote.</param>
        public static async Task<bool> FetchAsync(string remote = null)
        {
            string target = string.IsNullOrEmpty(remote) ? "--all" : QuoteArg(remote);
            var result = await ExecuteGitAsync($"fetch --prune {target}", timeoutMs: NetworkTimeoutMs);
            if (result.Success)
            {
                await RefreshStatusAsync();
                return true;
            }
            OnGitError?.Invoke(!string.IsNullOrEmpty(result.Error) ? result.Error : result.Output);
            return false;
        }

        public static async Task<List<GitBranchInfo>> GetBranchesAsync()
        {
            var result = await ExecuteGitAsync(
                "for-each-ref --format=\"%(refname)%1f%(HEAD)%1f%(upstream:short)%1f%(upstream:track)\" refs/heads refs/remotes");
            return result.Success ? ParseBranchRefs(result.Output) : new List<GitBranchInfo>();
        }

        /// <summary>
        /// Parses `for-each-ref` output (fields separated by U+001F). Local vs remote comes from the ref namespace,
        /// so local branches with slashes (e.g. "feature/x") are not mistaken for remote branches.
        /// </summary>
        public static List<GitBranchInfo> ParseBranchRefs(string output)
        {
            const string headsPrefix = "refs/heads/";
            const string remotesPrefix = "refs/remotes/";
            var branches = new List<GitBranchInfo>();

            foreach (var rawLine in output.Split('\n'))
            {
                string line = rawLine.TrimEnd('\r');
                if (string.IsNullOrWhiteSpace(line)) continue;
                var parts = line.Split('\u001f');
                string refName = parts[0].Trim();
                var info = new GitBranchInfo
                {
                    IsCurrent = parts.Length > 1 && parts[1].Trim() == "*",
                    Upstream = parts.Length > 2 ? parts[2].Trim() : ""
                };

                if (refName.StartsWith(headsPrefix))
                {
                    info.Name = info.ShortName = refName.Substring(headsPrefix.Length);
                }
                else if (refName.StartsWith(remotesPrefix))
                {
                    string remoteRef = refName.Substring(remotesPrefix.Length); // origin/feature/x
                    int slash = remoteRef.IndexOf('/');
                    if (slash <= 0) continue;
                    info.IsRemote = true;
                    info.Remote = remoteRef.Substring(0, slash);
                    info.ShortName = remoteRef.Substring(slash + 1);
                    info.Name = remoteRef;
                    if (info.ShortName == "HEAD") continue; // origin/HEAD is a pointer, not a branch
                }
                else
                {
                    continue;
                }

                string track = parts.Length > 3 ? parts[3].Trim().TrimStart('[').TrimEnd(']') : "";
                foreach (var item in track.Split(','))
                {
                    string t = item.Trim();
                    if (t == "gone") info.IsGone = true;
                    else if (t.StartsWith("ahead ")) int.TryParse(t.Substring(6), out info.AheadBy);
                    else if (t.StartsWith("behind ")) int.TryParse(t.Substring(7), out info.BehindBy);
                }

                branches.Add(info);
            }

            return branches;
        }

        /// <summary>Switches to a remote branch: reuses the local branch of the same name if there is one, otherwise creates a tracking branch.</summary>
        public static async Task<bool> CheckoutRemoteBranchAsync(GitBranchInfo remoteBranch, IEnumerable<GitBranchInfo> knownBranches)
        {
            return await TryCheckoutRemoteBranchAsync(remoteBranch, knownBranches) == CheckoutOutcome.Success;
        }

        /// <summary>Remote-branch counterpart of <see cref="TryCheckoutAsync"/>.</summary>
        public static async Task<CheckoutOutcome> TryCheckoutRemoteBranchAsync(GitBranchInfo remoteBranch, IEnumerable<GitBranchInfo> knownBranches)
        {
            if (HasLocalCounterpart(remoteBranch, knownBranches))
            {
                return await TryCheckoutAsync(remoteBranch.ShortName);
            }

            var result = await ExecuteGitAsync($"checkout --track {QuoteArg(remoteBranch.Name)} --");
            return await FinishCheckoutAsync(result, remoteBranch.ShortName);
        }

        public static bool HasLocalCounterpart(GitBranchInfo remoteBranch, IEnumerable<GitBranchInfo> knownBranches)
        {
            return knownBranches != null && knownBranches.Any(b => !b.IsRemote && b.Name == remoteBranch.ShortName);
        }

        private const string RecentBranchesPrefKey = "FuzzyIDE_Git_RecentBranches";

        public static List<string> GetRecentBranches()
        {
            string raw = EditorPrefs.GetString(RecentBranchesPrefKey, "");
            if (string.IsNullOrEmpty(raw)) return new List<string>();
            return raw.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries).ToList();
        }

        public static void RecordRecentBranch(string branchName)
        {
            if (string.IsNullOrWhiteSpace(branchName)) return;
            string clean = branchName.Trim();
            var list = GetRecentBranches();
            list.RemoveAll(b => string.Equals(b, clean, StringComparison.OrdinalIgnoreCase));
            list.Insert(0, clean);
            if (list.Count > 6) list = list.Take(6).ToList();
            EditorPrefs.SetString(RecentBranchesPrefKey, string.Join("|", list));
        }

        public static async Task<string> GetDefaultBranchAsync()
        {
            string remote = ResolveRemote(null);
            string prefix = $"refs/remotes/{remote}/";
            var result = await ExecuteGitAsync($"symbolic-ref -q {QuoteArg(prefix + "HEAD")}");
            string target = result.Success ? result.Output.Trim() : "";
            if (target.StartsWith(prefix) && target.Length > prefix.Length)
            {
                return target.Substring(prefix.Length);
            }

            var branches = await GetBranchesAsync();
            if (branches.Any(b => b.Name == "main")) return "main";
            if (branches.Any(b => b.Name == "master")) return "master";
            return branches.FirstOrDefault(b => !b.IsRemote)?.Name ?? "main";
        }

        public static async Task<string> GetRemoteUrlAsync(string remote = "origin")
        {
            var result = await ExecuteGitAsync($"remote get-url {QuoteArg(remote)}");
            if (result.Success && !string.IsNullOrWhiteSpace(result.Output))
            {
                return result.Output.Trim();
            }
            result = await ExecuteGitAsync($"config --get remote.{remote}.url");
            return result.Success ? result.Output.Trim() : null;
        }

        // --- Remotes ---

        public static async Task<List<GitRemoteInfo>> GetRemotesAsync()
        {
            var result = await ExecuteGitAsync("remote -v");
            return result.Success ? ParseRemotes(result.Output) : new List<GitRemoteInfo>();
        }

        /// <summary>Parses `git remote -v` ("name\turl (fetch)" / "(push)" pairs) into one entry per remote.</summary>
        public static List<GitRemoteInfo> ParseRemotes(string output)
        {
            var remotes = new List<GitRemoteInfo>();
            foreach (var rawLine in output.Split('\n'))
            {
                string line = rawLine.Trim();
                int tab = line.IndexOf('\t');
                if (tab <= 0) continue;

                string name = line.Substring(0, tab);
                string rest = line.Substring(tab + 1);
                bool isPush = rest.EndsWith(" (push)");
                int paren = rest.LastIndexOf(" (", StringComparison.Ordinal);
                string url = paren > 0 ? rest.Substring(0, paren) : rest;

                var remote = remotes.FirstOrDefault(r => r.Name == name);
                if (remote == null)
                {
                    remote = new GitRemoteInfo { Name = name };
                    remotes.Add(remote);
                }
                if (isPush) remote.PushUrl = url;
                else remote.FetchUrl = url;
            }
            return remotes;
        }

        private static async Task<bool> RunRemoteCommandAsync(string args)
        {
            var result = await ExecuteGitAsync(args);
            s_CachedDefaultRemote = null; // remote list changed
            if (result.Success)
            {
                await RefreshStatusAsync();
                return true;
            }
            OnGitError?.Invoke(result.Error);
            return false;
        }

        public static Task<bool> AddRemoteAsync(string name, string url) =>
            RunRemoteCommandAsync($"remote add {QuoteArg(name)} {QuoteArg(url)}");

        public static Task<bool> RemoveRemoteAsync(string name) =>
            RunRemoteCommandAsync($"remote remove {QuoteArg(name)}");

        public static Task<bool> RenameRemoteAsync(string oldName, string newName) =>
            RunRemoteCommandAsync($"remote rename {QuoteArg(oldName)} {QuoteArg(newName)}");

        public static Task<bool> SetRemoteUrlAsync(string name, string url) =>
            RunRemoteCommandAsync($"remote set-url {QuoteArg(name)} {QuoteArg(url)}");

        /// <summary>First publish of a repository: adds <paramref name="remoteName"/> → <paramref name="url"/> and pushes the current branch with -u.</summary>
        public static async Task<bool> PublishRepositoryAsync(string url, string remoteName = "origin")
        {
            if (!await AddRemoteAsync(remoteName, url)) return false;
            if (!await HasHeadCommitAsync())
            {
                OnGitError?.Invoke("The remote was added, but there is nothing to push yet. Make a first commit, then publish the branch.");
                return false;
            }
            return await PublishBranchAsync(remoteName);
        }

        // --- Conflicts and paused operations ---

        /// <summary>
        /// Resolves a conflicted file by taking one side wholesale and staging the result.
        /// <paramref name="keepMine"/> means the version from the branch the user is on: "ours" while merging,
        /// but "theirs" while rebasing (git replays your commits onto the other branch, swapping the sides).
        /// When the chosen side deleted the file, the resolution is the deletion.
        /// </summary>
        public static async Task<bool> ResolveConflictAsync(GitFileStatus file, bool keepMine)
        {
            bool rebasing = CurrentStatus.OperationState == GitRepoState.Rebasing;
            bool useOurs = keepMine != rebasing;
            bool chosenSideDeleted = useOurs ? file.DeletedByUs : file.DeletedByThem;
            string path = QuoteArg(file.Path);

            GitCommandResult result;
            if (chosenSideDeleted)
            {
                result = await ExecuteGitAsync($"--literal-pathspecs rm -q -- {path}");
            }
            else
            {
                result = await ExecuteGitAsync($"--literal-pathspecs checkout {(useOurs ? "--ours" : "--theirs")} -- {path}");
                if (result.Success)
                {
                    result = await ExecuteGitAsync($"--literal-pathspecs add -- {path}");
                }
            }

            if (result.Success)
            {
                await RefreshStatusAsync();
                AssetDatabase.Refresh();
                return true;
            }
            OnGitError?.Invoke(result.Error);
            return false;
        }

        private static string OperationCommand(GitRepoState state)
        {
            switch (state)
            {
                case GitRepoState.Merging: return "merge";
                case GitRepoState.Rebasing: return "rebase";
                case GitRepoState.CherryPicking: return "cherry-pick";
                case GitRepoState.Reverting: return "revert";
                default: return null;
            }
        }

        /// <summary>Aborts the paused merge/rebase/cherry-pick/revert and restores the state from before it started.</summary>
        public static Task<bool> AbortOperationAsync() => RunOperationStepAsync("--abort");

        /// <summary>Continues the paused operation after all conflicts are resolved (commits with git's default message).</summary>
        public static Task<bool> ContinueOperationAsync() => RunOperationStepAsync("--continue");

        /// <summary>Rebase/cherry-pick only: drops the commit that stopped and moves on.</summary>
        public static Task<bool> SkipOperationStepAsync() => RunOperationStepAsync("--skip");

        private static async Task<bool> RunOperationStepAsync(string step)
        {
            string command = OperationCommand(DetectOperationState(CurrentStatus.RepositoryRoot));
            if (command == null)
            {
                await RefreshStatusAsync();
                return true;
            }

            var result = await ExecuteGitAsync($"{command} {step}");
            await RefreshStatusAsync();
            AssetDatabase.Refresh();

            // A rebase that hits the next conflicting commit "fails" but has made progress; the banner stays up with the new conflicts.
            if (result.Success || (step != "--abort" && CurrentStatus.ConflictedFiles.Count > 0))
            {
                return result.Success;
            }
            OnGitError?.Invoke(!string.IsNullOrWhiteSpace(result.Error) ? result.Error : result.Output);
            return false;
        }

        public static async Task<(bool isGitHub, string ownerRepo, string webUrl)> GetGitHubRepositoryInfoAsync()
        {
            return ParseGitHubUrl(await GetRemoteUrlAsync(ResolveRemote(null)));
        }

        /// <summary>Recognises https://github.com/owner/repo(.git) and git@github.com:owner/repo(.git) remote URLs.</summary>
        public static (bool isGitHub, string ownerRepo, string webUrl) ParseGitHubUrl(string remoteUrl)
        {
            if (string.IsNullOrEmpty(remoteUrl)) return (false, null, null);

            string lower = remoteUrl.ToLowerInvariant();
            if (!lower.Contains("github.com")) return (false, null, null);

            string ownerRepo = null;
            if (remoteUrl.Contains("github.com/"))
            {
                int idx = remoteUrl.IndexOf("github.com/", StringComparison.OrdinalIgnoreCase) + "github.com/".Length;
                ownerRepo = remoteUrl.Substring(idx);
            }
            else if (remoteUrl.Contains("github.com:"))
            {
                int idx = remoteUrl.IndexOf("github.com:", StringComparison.OrdinalIgnoreCase) + "github.com:".Length;
                ownerRepo = remoteUrl.Substring(idx);
            }

            if (string.IsNullOrEmpty(ownerRepo)) return (false, null, null);

            ownerRepo = ownerRepo.Trim().TrimEnd('/');
            if (ownerRepo.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            {
                ownerRepo = ownerRepo.Substring(0, ownerRepo.Length - 4);
            }

            string webUrl = $"https://github.com/{ownerRepo}";
            return (true, ownerRepo, webUrl);
        }

        public static async Task<bool> CheckoutBranchAsync(string branchName)
        {
            var outcome = await TryCheckoutAsync(branchName);
            return outcome == CheckoutOutcome.Success;
        }

        /// <summary>
        /// Checks out a local branch. Unlike <see cref="CheckoutBranchAsync"/>, a checkout refused because local changes
        /// would be overwritten is reported as <see cref="CheckoutOutcome.BlockedByLocalChanges"/> (no error event),
        /// so the UI can offer to carry or stash the changes.
        /// </summary>
        public static async Task<CheckoutOutcome> TryCheckoutAsync(string branchName)
        {
            var result = await ExecuteGitAsync($"checkout {QuoteArg(branchName)} --");
            return await FinishCheckoutAsync(result, branchName);
        }

        private static async Task<CheckoutOutcome> FinishCheckoutAsync(GitCommandResult result, string branchName)
        {
            if (result.Success)
            {
                RecordRecentBranch(branchName);
                await RefreshStatusAsync();
                AssetDatabase.Refresh();
                return CheckoutOutcome.Success;
            }
            if (IsBlockedByLocalChanges(result.Error))
            {
                return CheckoutOutcome.BlockedByLocalChanges;
            }
            OnGitError?.Invoke(result.Error);
            return CheckoutOutcome.Failed;
        }

        public static bool IsBlockedByLocalChanges(string error)
        {
            return !string.IsNullOrEmpty(error) &&
                   (error.Contains("would be overwritten by checkout") || error.Contains("Please commit your changes or stash them"));
        }

        /// <summary>
        /// Switches branches when local changes are in the way.
        /// <paramref name="bringChanges"/> = true: stash, switch, re-apply the stash on the target branch.
        /// false: stash the changes with a message naming the branch they belong to and leave them in the stash list.
        /// </summary>
        /// <param name="remoteBranch">When set, the target is created as a tracking branch of this remote branch.</param>
        public static async Task<bool> CheckoutWithLocalChangesAsync(string branchName, bool bringChanges, GitBranchInfo remoteBranch = null)
        {
            string from = CurrentStatus.CurrentBranch;
            string message = bringChanges
                ? $"FuzzyIDE: carried from {from} to {branchName}"
                : $"FuzzyIDE: left on {from} when switching to {branchName}";

            var stash = await ExecuteGitAsync($"stash push -u -m {QuoteArg(message)}");
            if (!stash.Success)
            {
                OnGitError?.Invoke(stash.Error);
                return false;
            }

            var checkout = remoteBranch != null
                ? await ExecuteGitAsync($"checkout --track {QuoteArg(remoteBranch.Name)} --")
                : await ExecuteGitAsync($"checkout {QuoteArg(branchName)} --");
            if (!checkout.Success)
            {
                // Put the changes back where they were so nothing is lost.
                await ExecuteGitAsync("stash pop");
                await RefreshStatusAsync();
                OnGitError?.Invoke(checkout.Error);
                return false;
            }

            RecordRecentBranch(branchName);
            bool ok = true;
            if (bringChanges)
            {
                var pop = await ExecuteGitAsync("stash pop");
                if (!pop.Success)
                {
                    // A conflicting re-apply keeps the stash entry, so the changes are still safe in the stash list.
                    OnGitError?.Invoke("Switched branches, but your changes conflict with this branch. Resolve the conflicts in the Changes tab; " +
                                       "an untouched copy of your changes is kept in the stash list.\n" + pop.Error + pop.Output);
                    ok = false;
                }
            }

            await RefreshStatusAsync();
            AssetDatabase.Refresh();
            return ok;
        }

        /// <param name="startPoint">Branch, remote branch or commit to start from; null = current HEAD.
        /// The new branch never tracks the start point, so it shows up as unpublished.</param>
        public static async Task<bool> CreateBranchAsync(string branchName, bool checkout = true, string startPoint = null)
        {
            string start = string.IsNullOrEmpty(startPoint) ? "" : $" --no-track {QuoteArg(startPoint)}";
            string args = checkout
                ? $"checkout -b {QuoteArg(branchName)}{start}"
                : $"branch {QuoteArg(branchName)}{start}";
            var result = await ExecuteGitAsync(args);
            if (result.Success)
            {
                if (checkout) RecordRecentBranch(branchName);
                await RefreshStatusAsync();
                if (checkout && !string.IsNullOrEmpty(startPoint)) AssetDatabase.Refresh();
                return true;
            }
            OnGitError?.Invoke(result.Error);
            return false;
        }

        /// <param name="unsetUpstream">Drop the tracking link: the remote branch keeps the old name, and with differing
        /// names a plain push fails (push.default=simple), so the renamed branch is better treated as unpublished.</param>
        public static async Task<bool> RenameBranchAsync(string oldName, string newName, bool unsetUpstream = true)
        {
            var result = await ExecuteGitAsync($"branch -m {QuoteArg(oldName)} {QuoteArg(newName)}");
            if (result.Success)
            {
                if (unsetUpstream)
                {
                    // Fails harmlessly when there was no upstream.
                    await ExecuteGitAsync($"branch --unset-upstream {QuoteArg(newName)}");
                }

                var recent = GetRecentBranches();
                int idx = recent.IndexOf(oldName);
                if (idx >= 0)
                {
                    recent[idx] = newName;
                    EditorPrefs.SetString(RecentBranchesPrefKey, string.Join("|", recent));
                }
                await RefreshStatusAsync();
                return true;
            }
            OnGitError?.Invoke(result.Error);
            return false;
        }

        public static async Task<bool> DeleteBranchAsync(string branchName, bool force = false)
        {
            var outcome = await TryDeleteBranchAsync(branchName, force);
            if (outcome == DeleteBranchOutcome.NotFullyMerged)
            {
                OnGitError?.Invoke($"The branch '{branchName}' is not fully merged.");
            }
            return outcome == DeleteBranchOutcome.Success;
        }

        /// <summary>Deletes a local branch; an unmerged branch is reported as <see cref="DeleteBranchOutcome.NotFullyMerged"/> without an error event so the UI can ask before forcing.</summary>
        public static async Task<DeleteBranchOutcome> TryDeleteBranchAsync(string branchName, bool force = false)
        {
            string flag = force ? "-D" : "-d";
            var result = await ExecuteGitAsync($"branch {flag} {QuoteArg(branchName)}");
            if (result.Success)
            {
                await RefreshStatusAsync();
                return DeleteBranchOutcome.Success;
            }
            if (!force && result.Error.Contains("not fully merged"))
            {
                return DeleteBranchOutcome.NotFullyMerged;
            }
            OnGitError?.Invoke(result.Error);
            return DeleteBranchOutcome.Failed;
        }

        public static async Task<bool> DeleteRemoteBranchAsync(string remote, string branchName)
        {
            var result = await ExecuteGitAsync($"push {QuoteArg(ResolveRemote(remote))} --delete {QuoteArg(branchName)}", timeoutMs: NetworkTimeoutMs);
            if (result.Success)
            {
                await RefreshStatusAsync();
                return true;
            }
            OnGitError?.Invoke(result.Error);
            return false;
        }

        /// <summary>Merges <paramref name="branchName"/> into the current branch. Conflicts are an expected outcome, not an error.</summary>
        public static async Task<MergeOutcome> MergeBranchAsync(string branchName)
        {
            var result = await ExecuteGitAsync($"merge --no-edit {QuoteArg(branchName)}");
            return await FinishMergeLikeAsync(result);
        }

        /// <summary>Rebases the current branch onto <paramref name="upstream"/>. Conflicts are an expected outcome, not an error.</summary>
        public static async Task<MergeOutcome> RebaseOntoAsync(string upstream)
        {
            var result = await ExecuteGitAsync($"rebase {QuoteArg(upstream)}");
            return await FinishMergeLikeAsync(result);
        }

        private static async Task<MergeOutcome> FinishMergeLikeAsync(GitCommandResult result)
        {
            await RefreshStatusAsync();
            AssetDatabase.Refresh();
            if (result.Success)
            {
                return MergeOutcome.Success;
            }
            if (CurrentStatus.ConflictedFiles.Count > 0 || CurrentStatus.OperationState != GitRepoState.None)
            {
                return MergeOutcome.Conflicts;
            }
            OnGitError?.Invoke(!string.IsNullOrWhiteSpace(result.Error) ? result.Error : result.Output);
            return MergeOutcome.Failed;
        }

        /// <summary>
        /// Checks a branch name against git's ref-name rules (see `git check-ref-format --branch`) without spawning git.
        /// Returns null when valid, otherwise a short reason.
        /// </summary>
        public static string ValidateBranchName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "Enter a branch name.";
            if (name == "@" || name == "HEAD") return $"'{name}' is reserved.";
            if (name.StartsWith("-")) return "Can't start with '-'.";
            if (name.StartsWith("/") || name.EndsWith("/")) return "Can't start or end with '/'.";
            if (name.EndsWith(".")) return "Can't end with '.'.";
            if (name.EndsWith(".lock")) return "Can't end with '.lock'.";
            if (name.Contains("..")) return "Can't contain '..'.";
            if (name.Contains("//")) return "Can't contain '//'.";
            if (name.Contains("@{")) return "Can't contain '@{'.";
            foreach (char c in name)
            {
                if (char.IsWhiteSpace(c) || char.IsControl(c)) return "Can't contain spaces.";
                if ("~^:?*[\\".IndexOf(c) >= 0) return $"Can't contain '{c}'.";
            }
            foreach (var component in name.Split('/'))
            {
                if (component.StartsWith(".")) return "A path segment can't start with '.'.";
                if (component.EndsWith(".lock")) return "A path segment can't end with '.lock'.";
            }
            return null;
        }

        public static async Task<List<GitCommitInfo>> GetLogAsync(int maxCount = 50)
        {
            // Fields are separated by U+001F and records by U+001E so subjects containing '|' survive intact.
            var result = await ExecuteGitAsync($"log -n {maxCount} --pretty=format:%H%x1f%h%x1f%an%x1f%ae%x1f%ar%x1f%aI%x1f%s%x1e");
            return result.Success ? ParseLog(result.Output) : new List<GitCommitInfo>();
        }

        public static List<GitCommitInfo> ParseLog(string output)
        {
            var commits = new List<GitCommitInfo>();
            foreach (var record in output.Split('\u001e'))
            {
                string trimmed = record.Trim('\r', '\n');
                if (string.IsNullOrWhiteSpace(trimmed)) continue;
                var parts = trimmed.Split('\u001f');
                if (parts.Length < 7) continue;

                DateTimeOffset.TryParse(parts[5], System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var date);
                commits.Add(new GitCommitInfo
                {
                    Hash = parts[0],
                    ShortHash = parts[1],
                    Author = parts[2],
                    Email = parts[3],
                    RelativeTime = parts[4],
                    Date = date == default ? default : date.LocalDateTime,
                    Message = parts[6]
                });
            }

            return commits;
        }

        public static async Task<List<GitStashInfo>> GetStashesAsync()
        {
            var stashes = new List<GitStashInfo>();
            var result = await ExecuteGitAsync("stash list --format=\"%gd|%gs|%cr\"");
            if (!result.Success) return stashes;

            using (var reader = new StringReader(result.Output))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var parts = line.Split('|');
                    if (parts.Length >= 3)
                    {
                        string idStr = parts[0].Trim(); // e.g. stash@{0}
                        int index = 0;
                        int openBrace = idStr.IndexOf('{');
                        int closeBrace = idStr.IndexOf('}');
                        if (openBrace != -1 && closeBrace != -1)
                        {
                            int.TryParse(idStr.Substring(openBrace + 1, closeBrace - openBrace - 1), out index);
                        }

                        stashes.Add(new GitStashInfo
                        {
                            Index = index,
                            Message = parts[1].Trim(),
                            RelativeTime = parts[2].Trim()
                        });
                    }
                }
            }

            return stashes;
        }

        public static async Task<bool> StashSaveAsync(string message = null, bool includeUntracked = false)
        {
            string args = "stash push" + (includeUntracked ? " -u" : "");
            if (!string.IsNullOrEmpty(message))
            {
                args += $" -m {QuoteArg(message)}";
            }
            var result = await ExecuteGitAsync(args);
            if (result.Success)
            {
                await RefreshStatusAsync();
                AssetDatabase.Refresh();
                return true;
            }
            OnGitError?.Invoke(result.Error);
            return false;
        }

        public static async Task<bool> StashPopAsync(int index = 0)
        {
            var result = await ExecuteGitAsync($"stash pop stash@{{{index}}}");
            if (result.Success)
            {
                await RefreshStatusAsync();
                AssetDatabase.Refresh();
                return true;
            }
            OnGitError?.Invoke(result.Error);
            return false;
        }

        public static async Task<bool> StashDropAsync(int index = 0)
        {
            var result = await ExecuteGitAsync($"stash drop stash@{{{index}}}");
            if (result.Success)
            {
                await RefreshStatusAsync();
                return true;
            }
            OnGitError?.Invoke(result.Error);
            return false;
        }

        public static async Task<string> GetHeadFileContentAsync(string repoRelativePath)
        {
            if (string.IsNullOrEmpty(repoRelativePath)) return null;
            string cleanPath = repoRelativePath.Replace('\\', '/').TrimStart('/');
            var result = await ExecuteGitAsync($"show HEAD:\"{cleanPath}\"");
            if (result.Success)
            {
                return result.Output;
            }
            return null;
        }

        /// <param name="staged">Index vs HEAD (the "Staged" list). Otherwise working tree vs index (the "Changes" list).</param>
        /// <param name="untracked">The file isn't known to git; diffs it against an empty file so its content is shown as added.</param>
        public static async Task<string> GetFileDiffAsync(string repoRelativePath, bool staged = false, bool untracked = false)
        {
            if (string.IsNullOrEmpty(repoRelativePath)) return "";
            string cleanPath = QuoteArg(repoRelativePath.Replace('\\', '/').TrimStart('/'));

            if (untracked)
            {
                // --no-index exits with 1 when the files differ, which is the expected outcome here.
                var noIndex = await ExecuteGitAsync($"diff --no-index -- /dev/null {cleanPath}");
                return noIndex.ExitCode == 0 || noIndex.ExitCode == 1 ? noIndex.Output : noIndex.Error;
            }

            // `diff --cached` also works before the first commit (compares against the empty tree).
            string args = staged ? $"--literal-pathspecs diff --cached -- {cleanPath}" : $"--literal-pathspecs diff -- {cleanPath}";
            var result = await ExecuteGitAsync(args);
            return result.Success ? result.Output : result.Error;
        }

        public static async Task<string> GetCommitFileDiffAsync(string commitHash, string repoRelativePath)
        {
            if (string.IsNullOrEmpty(commitHash) || string.IsNullOrEmpty(repoRelativePath)) return "";
            string cleanPath = QuoteArg(repoRelativePath.Replace('\\', '/').TrimStart('/'));
            var result = await ExecuteGitAsync($"--literal-pathspecs show --format= --patch {commitHash} -- {cleanPath}");
            return result.Success ? result.Output : result.Error;
        }

        public static async Task<bool> InitRepositoryAsync(bool createStandardGitIgnore = true)
        {
            var result = await ExecuteGitAsync("init");
            if (result.Success)
            {
                string root = Directory.GetCurrentDirectory();
                if (createStandardGitIgnore)
                {
                    string gitIgnorePath = Path.Combine(root, ".gitignore");
                    if (!File.Exists(gitIgnorePath))
                    {
                        File.WriteAllText(gitIgnorePath, GetUnityGitIgnoreContent(), Encoding.UTF8);
                    }
                }
                DetectRepository();
                await RefreshStatusAsync();
                AssetDatabase.Refresh();
                return true;
            }
            OnGitError?.Invoke(result.Error);
            return false;
        }

        public static string GetUnityGitIgnoreContent()
        {
            return @"# This .gitignore file should be placed at the root of your Unity project directory
#
# Unity generated folders
/[Ll]ibrary/
/[Tt]emp/
/[Oo]bj/
/[Bb]uild/
/[Bb]uilds/
/[Ll]ogs/
/[Uu]ser[Ss]ettings/
/[Mm]emoryCaptures/

# Asset meta data should only be ignored when using the line based version control support
/[Aa]ssets/AssetStoreTools*

# Autogenerated VS/MD/Consulo solution and project files
ExportedObj/
.consulo/
*.csproj
*.unityproj
*.sln
*.suo
*.tmp
*.user
*.userprefs
*.pidb
*.booproj
*.svd
*.pdb
*.mdb
*.opendb
*.VC.db

# Unity3D generated meta files
*.pidb.meta
*.pdb.meta
*.mdb.meta

# Unity3D Generated File On Crash Reports
sysinfo.txt

# Builds
*.apk
*.aab
*.unitypackage
*.app

# Crashlytics
crashlytics-build.properties

# OS generated
.DS_Store
Thumbs.db
";
        }
    }
}
