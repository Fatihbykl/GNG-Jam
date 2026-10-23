using System;
using System.Collections.Generic;
using UnityEngine;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.Git
{
    public enum GitFileState
    {
        None,
        Untracked,
        Modified,
        Added,
        Deleted,
        Renamed,
        Copied,
        Conflict
    }

    /// <summary>A multi-step operation git has paused in the middle of (usually because of conflicts).</summary>
    public enum GitRepoState
    {
        None,
        Merging,
        Rebasing,
        CherryPicking,
        Reverting
    }

    public enum CheckoutOutcome
    {
        Success,
        /// <summary>Uncommitted changes would be overwritten; nothing was changed.</summary>
        BlockedByLocalChanges,
        Failed
    }

    public enum DeleteBranchOutcome
    {
        Success,
        /// <summary>`git branch -d` refused because the branch has commits not merged anywhere; needs -D.</summary>
        NotFullyMerged,
        Failed
    }

    public enum MergeOutcome
    {
        Success,
        /// <summary>The merge/rebase stopped and is waiting for conflicts to be resolved.</summary>
        Conflicts,
        Failed
    }

    public enum GitLineDiffType : byte
    {
        None = 0,
        Added = 1,
        Modified = 2,
        DeletedAbove = 3
    }

    public class GitFileStatus
    {
        public string Path;
        public string FullPath;
        public string FileName;
        public string OldPath;
        public GitFileState StagedState = GitFileState.None;
        public GitFileState WorkingTreeState = GitFileState.None;
        /// <summary>Raw porcelain code for unmerged paths ("UU", "AA", "DU" = deleted by us, "UD" = deleted by them, ...); null otherwise.</summary>
        public string ConflictCode;

        /// <summary>Our side (HEAD) deleted the file in this conflict.</summary>
        public bool DeletedByUs => ConflictCode == "DU" || ConflictCode == "DD";
        /// <summary>Their side (the incoming change) deleted the file in this conflict.</summary>
        public bool DeletedByThem => ConflictCode == "UD" || ConflictCode == "DD";

        public bool IsStaged => StagedState != GitFileState.None && StagedState != GitFileState.Untracked;
        public bool IsUnstaged => WorkingTreeState != GitFileState.None && WorkingTreeState != GitFileState.Untracked;
        public bool IsUntracked => StagedState == GitFileState.Untracked || WorkingTreeState == GitFileState.Untracked;

        public string GetBadgeText(bool staged)
        {
            var state = staged ? StagedState : WorkingTreeState;
            switch (state)
            {
                case GitFileState.Modified: return "M";
                case GitFileState.Added: return "A";
                case GitFileState.Deleted: return "D";
                case GitFileState.Renamed: return "R";
                case GitFileState.Untracked: return "U";
                case GitFileState.Conflict: return "C";
                default: return "";
            }
        }

        public Color GetBadgeColor(bool staged)
        {
            var state = staged ? StagedState : WorkingTreeState;
            switch (state)
            {
                case GitFileState.Modified: return new Color(0.9f, 0.75f, 0.45f, 1f); // Amber / Yellow
                case GitFileState.Added:
                case GitFileState.Untracked: return new Color(0.45f, 0.78f, 0.55f, 1f); // Green
                case GitFileState.Deleted: return new Color(0.9f, 0.4f, 0.4f, 1f); // Red
                case GitFileState.Renamed: return new Color(0.4f, 0.7f, 0.9f, 1f); // Blue
                case GitFileState.Conflict: return new Color(1f, 0.3f, 0.3f, 1f); // Bright Red
                default: return Color.gray;
            }
        }
    }

    public class GitRepositoryStatus
    {
        public bool IsGitRepository = false;
        public string RepositoryRoot = "";
        public string RepositoryName = "";
        public string CurrentBranch = "";
        public string UpstreamBranch = "";
        public int AheadBy = 0;
        public int BehindBy = 0;
        public bool UpstreamGone = false;
        public bool IsDetached = false;
        public string DefaultRemote = "";

        public bool HasUpstream => !string.IsNullOrEmpty(UpstreamBranch);
        public bool HasRemote => !string.IsNullOrEmpty(DefaultRemote);

        /// <summary>True when the current branch exists only locally (or its upstream was deleted) and a remote is available to publish to.</summary>
        public bool CanPublishBranch => HasRemote && !IsDetached && !string.IsNullOrEmpty(CurrentBranch) && (!HasUpstream || UpstreamGone);

        public List<GitFileStatus> StagedFiles = new List<GitFileStatus>();
        public List<GitFileStatus> UnstagedFiles = new List<GitFileStatus>();
        public List<GitFileStatus> UntrackedFiles = new List<GitFileStatus>();
        public List<GitFileStatus> ConflictedFiles = new List<GitFileStatus>();
        public GitRepoState OperationState = GitRepoState.None;

        public int TotalChangesCount => StagedFiles.Count + UnstagedFiles.Count + UntrackedFiles.Count + ConflictedFiles.Count;
    }

    public class GitBranchInfo
    {
        /// <summary>Local: "feature/x". Remote: "origin/feature/x".</summary>
        public string Name;
        /// <summary>Branch name without the remote prefix ("feature/x" for both cases).</summary>
        public string ShortName;
        /// <summary>Remote name for remote-tracking branches, empty for local ones.</summary>
        public string Remote = "";
        public bool IsCurrent;
        public bool IsRemote;
        public string Upstream;
        public int AheadBy;
        public int BehindBy;
        /// <summary>The configured upstream no longer exists on the remote.</summary>
        public bool IsGone;

        public override string ToString() => Name;
    }

    public class GitRemoteInfo
    {
        public string Name;
        public string FetchUrl;
        public string PushUrl;

        public override string ToString() => Name;
    }

    public class GitCommitInfo
    {
        public string Hash;
        public string ShortHash;
        public string Author;
        public string Email;
        public string RelativeTime;
        public DateTime Date;
        public string Message;
    }

    public class GitStashInfo
    {
        public int Index;
        public string Message;
        public string Branch;
        public string RelativeTime;
    }

    public class GitLineDiffResult
    {
        public Dictionary<int, GitLineDiffType> LineStatuses = new Dictionary<int, GitLineDiffType>();
        public Dictionary<int, List<string>> DeletedLinesAbove = new Dictionary<int, List<string>>();
        public int AddedCount;
        public int ModifiedCount;
        public int DeletedCount;

        public bool HasChanges => AddedCount > 0 || ModifiedCount > 0 || DeletedCount > 0;
    }
}
