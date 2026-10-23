using System;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.Git
{
    public enum GitErrorKind
    {
        Unknown,
        NoUpstream,
        AuthenticationFailed,
        PushRejected,
        DivergentBranches,
        Network,
        RepositoryNotFound,
        IndexLocked,
        LocalChangesWouldBeOverwritten,
        UnresolvedConflicts,
        Timeout
    }

    public struct GitErrorInfo
    {
        public GitErrorKind Kind;
        /// <summary>One-line, user-facing explanation.</summary>
        public string Message;
    }

    /// <summary>Maps raw git stderr (English, see GitService's LANGUAGE=C) to a known kind and a readable message.</summary>
    public static class GitErrorInterpreter
    {
        public static GitErrorInfo Interpret(string error)
        {
            string e = error ?? "";
            bool Has(string s) => e.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0;

            if (Has("has no upstream branch"))
                return Info(GitErrorKind.NoUpstream, "This branch hasn't been published yet.");

            if (Has("Authentication failed") || Has("could not read Username") || Has("terminal prompts disabled") ||
                Has("Permission denied (publickey)") || Has("Invalid username or password") || Has("HTTP Basic: Access denied"))
                return Info(GitErrorKind.AuthenticationFailed,
                    "Authentication failed. Sign in through Git Credential Manager (run any git push in a terminal once) or set up an SSH key.");

            if (Has("[rejected]") && (Has("fetch first") || Has("non-fast-forward")) || Has("Updates were rejected"))
                return Info(GitErrorKind.PushRejected, "The remote has commits you don't have yet. Pull first, then push again.");

            if (Has("divergent branches") || Has("Not possible to fast-forward"))
                return Info(GitErrorKind.DivergentBranches,
                    "Your branch and the remote have diverged. Choose Merge or Rebase under Repository ▸ Pull Strategy, then pull again.");

            if (Has("Could not resolve host") || Has("Failed to connect") || Has("Connection timed out") ||
                Has("Could not connect to server") || Has("unable to access"))
                return Info(GitErrorKind.Network, "Couldn't reach the remote. Check your internet connection or the remote URL.");

            if (Has("Repository not found") || Has("does not appear to be a git repository"))
                return Info(GitErrorKind.RepositoryNotFound, "The remote repository wasn't found, or you don't have access to it.");

            if (Has("index.lock"))
                return Info(GitErrorKind.IndexLocked,
                    "Another git process is using this repository. Wait for it to finish; if none is running, delete .git/index.lock.");

            if (Has("would be overwritten by"))
                return Info(GitErrorKind.LocalChangesWouldBeOverwritten, "Your uncommitted changes would be overwritten. Commit or stash them first.");

            if (Has("you need to resolve your current index first") || Has("unmerged files") || Has("Committing is not possible because you have unmerged files"))
                return Info(GitErrorKind.UnresolvedConflicts, "There are unresolved conflicts. Resolve them in the Changes tab first.");

            if (Has("timed out after"))
                return Info(GitErrorKind.Timeout, FirstLine(e));

            return Info(GitErrorKind.Unknown, FirstLine(e));
        }

        private static GitErrorInfo Info(GitErrorKind kind, string message) => new GitErrorInfo { Kind = kind, Message = message };

        /// <summary>First meaningful line, skipping git's "hint:" lines and LF/CRLF warnings.</summary>
        private static string FirstLine(string e)
        {
            foreach (var raw in e.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("hint:") || line.StartsWith("warning: in the working copy")) continue;
                return line;
            }
            return e.Trim();
        }
    }
}
