using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using FuzzyLogicLabs.FuzzyIDE.Editor.Git;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.Notes.Integration
{
    /// <summary>[[commit:hash]] links and commit summaries for notes.</summary>
    public static class GitLinks
    {
        public static bool OpenCommit(string hash)
        {
            if (string.IsNullOrWhiteSpace(hash)) return false;
            GitWindow.RevealCommit(hash.Trim());
            return true;
        }

        /// <summary>Markdown list of commits since local midnight on the current branch, newest first ("" when none).</summary>
        public static async Task<string> GetTodaysCommitsMarkdownAsync()
        {
            if (!GitService.IsGitRepository) return "";
            var result = await GitService.ExecuteGitAsync("log --since=midnight --no-merges --pretty=format:\"%h|%s\"");
            if (result == null || !result.Success || string.IsNullOrWhiteSpace(result.Output)) return "";

            var sb = new StringBuilder();
            using (var reader = new StringReader(result.Output))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    int bar = line.IndexOf('|');
                    if (bar <= 0) continue;
                    sb.Append("- [[commit:").Append(line.Substring(0, bar).Trim()).Append("]] ")
                      .Append(line.Substring(bar + 1).Trim()).Append('\n');
                }
            }
            return sb.ToString();
        }
    }
}
