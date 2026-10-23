using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.Notes.LiveEditor
{
    /// <summary>Resolves image embeds (![[img.png]], ![](path)) to files and caches the loaded textures.</summary>
    public static class NoteImageCache
    {
        private struct Entry
        {
            public DateTime WriteTime;
            public Texture2D Texture;
        }

        private static readonly Dictionary<string, Entry> s_Cache = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, string[]> s_VaultFiles = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, double> s_VaultScanTime = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        private static string ProjectRoot => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        /// <summary>
        /// Resolution order (Obsidian-compatible): absolute path, project path (Assets/, Packages/),
        /// relative to the note, relative to the vault root, the vault's assets folder, then any file
        /// in the vault with the same name.
        /// </summary>
        public static string ResolvePath(string target, string noteDirectory, string vaultRoot)
        {
            if (string.IsNullOrWhiteSpace(target)) return null;
            target = Uri.UnescapeDataString(target.Trim()).Replace('\\', '/');
            if (target.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                target.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return null;

            try
            {
                if (Path.IsPathRooted(target)) return File.Exists(target) ? target : null;

                if (target.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) ||
                    target.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
                {
                    string p = Path.Combine(ProjectRoot, target);
                    if (File.Exists(p)) return p;
                }

                foreach (var baseDir in new[] { noteDirectory, vaultRoot, vaultRoot != null ? Path.Combine(vaultRoot, "assets") : null })
                {
                    if (string.IsNullOrEmpty(baseDir)) continue;
                    string p = Path.GetFullPath(Path.Combine(baseDir, target));
                    if (File.Exists(p)) return p;
                }

                if (!string.IsNullOrEmpty(vaultRoot) && Directory.Exists(vaultRoot))
                {
                    string name = Path.GetFileName(target);
                    foreach (var f in GetVaultFiles(vaultRoot))
                    {
                        if (string.Equals(Path.GetFileName(f), name, StringComparison.OrdinalIgnoreCase)) return f;
                    }
                }
            }
            catch (ArgumentException)
            {
                // Invalid characters in the target: treat as unresolved.
            }
            return null;
        }

        /// <summary>Loads (and caches) a texture; null for missing or unsupported files.</summary>
        public static Texture2D Load(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath) || !File.Exists(fullPath)) return null;

            var writeTime = File.GetLastWriteTimeUtc(fullPath);
            if (s_Cache.TryGetValue(fullPath, out var e) && e.WriteTime == writeTime && e.Texture != null) return e.Texture;

            Texture2D tex = null;
            string projectRoot = ProjectRoot.Replace('\\', '/').TrimEnd('/') + "/";
            string normalized = Path.GetFullPath(fullPath).Replace('\\', '/');
            if (normalized.StartsWith(projectRoot + "Assets/", StringComparison.OrdinalIgnoreCase))
            {
                tex = AssetDatabase.LoadAssetAtPath<Texture2D>(normalized.Substring(projectRoot.Length));
            }

            if (tex == null)
            {
                string ext = Path.GetExtension(fullPath).ToLowerInvariant();
                if (ext == ".png" || ext == ".jpg" || ext == ".jpeg")
                {
                    try
                    {
                        var loaded = new Texture2D(2, 2) { hideFlags = HideFlags.HideAndDontSave };
                        if (loaded.LoadImage(File.ReadAllBytes(fullPath))) tex = loaded;
                        else UnityEngine.Object.DestroyImmediate(loaded);
                    }
                    catch (IOException)
                    {
                        return null;
                    }
                }
            }

            if (e.Texture != null && e.Texture != tex && e.Texture.hideFlags == HideFlags.HideAndDontSave)
            {
                UnityEngine.Object.DestroyImmediate(e.Texture);
            }
            s_Cache[fullPath] = new Entry { WriteTime = writeTime, Texture = tex };
            return tex;
        }

        public static bool IsImageFile(string path)
        {
            switch (Path.GetExtension(path ?? "").ToLowerInvariant())
            {
                case ".png": case ".jpg": case ".jpeg": case ".gif": case ".bmp": case ".tga": case ".psd": case ".webp":
                    return true;
                default:
                    return false;
            }
        }

        private static string[] GetVaultFiles(string vaultRoot)
        {
            double now = EditorApplication.timeSinceStartup;
            if (s_VaultFiles.TryGetValue(vaultRoot, out var files) &&
                s_VaultScanTime.TryGetValue(vaultRoot, out var t) && now - t < 5.0)
            {
                return files;
            }

            try { files = Directory.GetFiles(vaultRoot, "*.*", SearchOption.AllDirectories); }
            catch (Exception) { files = Array.Empty<string>(); }

            s_VaultFiles[vaultRoot] = files;
            s_VaultScanTime[vaultRoot] = now;
            return files;
        }
    }
}
