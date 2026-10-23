using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using FuzzyLogicLabs.FuzzyIDE.Editor.Notes.Core;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.Notes.Integration
{
    /// <summary>
    /// Editor context for Quick Capture: active scene, selection, the last Console error and Game view screenshots.
    /// The last error is recorded from the moment the editor loads.
    /// </summary>
    [InitializeOnLoad]
    public static class CaptureContext
    {
        private static string s_LastError;
        private static string s_LastErrorLocation;
        private static DateTime s_LastErrorTime;

        static CaptureContext()
        {
            Application.logMessageReceived -= OnLog;
            Application.logMessageReceived += OnLog;
        }

        private static void OnLog(string message, string stackTrace, LogType type)
        {
            if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert) return;
            if (message != null && message.StartsWith("[FuzzyNotes]")) return;
            s_LastError = message;
            s_LastErrorLocation = FirstProjectFrame(stackTrace);
            s_LastErrorTime = DateTime.Now;
        }

        public static bool HasRecentError => s_LastError != null && (DateTime.Now - s_LastErrorTime).TotalMinutes < 30;

        /// <summary>Markdown block describing the current editor/play state.</summary>
        public static string BuildContextMarkdown(bool includeError)
        {
            var sb = new StringBuilder();
            var scene = SceneManager.GetActiveScene();
            sb.Append("> [!info] ").Append(EditorApplication.isPlaying ? "🎮 Play Mode" : "🛠 Edit Mode");
            if (scene.IsValid()) sb.Append(" · Scene: `").Append(scene.name).Append('`');
            if (EditorApplication.isPlaying)
            {
                float fps = Time.unscaledDeltaTime > 0 ? 1f / Time.unscaledDeltaTime : 0f;
                sb.Append($" · t = {Time.time:F1}s · ~{fps:F0} FPS");
            }
            sb.Append(" · ").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm")).Append('\n');

            var selected = Selection.activeObject;
            string link = selected != null ? UnityObjectLinks.CreateLink(selected) : null;
            if (link != null) sb.Append("> Selected: ").Append(link).Append('\n');

            if (includeError && HasRecentError)
            {
                string error = s_LastError.Split('\n')[0].Trim();
                if (error.Length > 300) error = error.Substring(0, 297) + "…";
                sb.Append("> Last error (").Append(s_LastErrorTime.ToString("HH:mm:ss")).Append("): `").Append(error.Replace('`', '\'')).Append('`');
                if (!string.IsNullOrEmpty(s_LastErrorLocation)) sb.Append(" — [[").Append(s_LastErrorLocation).Append("]]");
                sb.Append('\n');
            }
            return sb.ToString();
        }

        /// <summary>
        /// Saves a Game view screenshot into the vault's assets folder and returns the embed ("![[assets/…png]]").
        /// Unity writes the file at the end of the frame, so the image appears in the note a moment later.
        /// </summary>
        public static string CaptureGameView(NoteVault vault)
        {
            try
            {
                Directory.CreateDirectory(vault.AssetsDirectory);
                string name = $"capture-{DateTime.Now:yyyyMMdd-HHmmss}.png";
                ScreenCapture.CaptureScreenshot(Path.Combine(vault.AssetsDirectory, name));
                return $"![[assets/{name}]]";
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[FuzzyNotes] Screenshot failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>"at Foo.Bar () [0x0] in C:\Proj\Assets\Scripts\Foo.cs:42" → "Foo.cs:42" (first frame inside Assets/).</summary>
        internal static string FirstProjectFrame(string stackTrace)
        {
            if (string.IsNullOrEmpty(stackTrace)) return null;
            foreach (var raw in stackTrace.Split('\n'))
            {
                string line = raw.Replace('\\', '/');
                int assets = line.IndexOf("Assets/", StringComparison.Ordinal);
                if (assets < 0) continue;
                string tail = line.Substring(assets).TrimEnd(')', ' ', '\r');
                int colon = tail.LastIndexOf(':');
                if (colon <= 0) continue;
                string file = tail.Substring(0, colon);
                if (!file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) continue;
                if (int.TryParse(tail.Substring(colon + 1), out int lineNumber)) return Path.GetFileName(file) + ":" + lineNumber;
            }
            return null;
        }
    }
}
