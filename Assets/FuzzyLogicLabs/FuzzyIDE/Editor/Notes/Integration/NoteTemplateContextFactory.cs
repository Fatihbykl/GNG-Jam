using System;
using System.IO;
using UnityEditor.SceneManagement;
using UnityEngine;
using FuzzyLogicLabs.FuzzyIDE.Editor.Git;
using FuzzyLogicLabs.FuzzyIDE.Editor.Notes.Core;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.Notes.Integration
{
    /// <summary>Fills template variables from the editor: git branch, active scene, project name.</summary>
    public static class NoteTemplateContextFactory
    {
        public static TemplateContext Create(string title = "", string selection = "")
        {
            string branch = "";
            try { if (GitService.IsGitRepository) branch = GitService.CurrentBranch ?? ""; }
            catch (Exception) { /* git unavailable */ }

            string scene = "";
            try { scene = EditorSceneManager.GetActiveScene().name; }
            catch (Exception) { /* no scene loaded */ }

            return new TemplateContext
            {
                Now = DateTime.Now,
                Title = title ?? "",
                Selection = selection ?? "",
                Branch = branch,
                Scene = scene,
                Project = Path.GetFileName(Path.GetFullPath(Path.Combine(Application.dataPath, "..")))
            };
        }
    }
}
