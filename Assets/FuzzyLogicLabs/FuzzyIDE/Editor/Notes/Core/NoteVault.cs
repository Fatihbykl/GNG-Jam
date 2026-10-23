using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.Notes.Core
{
    /// <summary>
    /// A folder of markdown notes: the project vault, the personal vault, an attached Obsidian vault, or the
    /// project's own Assets folder (<see cref="IsProjectFiles"/>).
    /// </summary>
    public sealed class NoteVault
    {
        public const string ProjectId = "project";
        public const string PersonalId = "personal";
        public const string ProjectFilesId = "assets";

        public string Id { get; }
        public string Name { get; }
        public string Root { get; }
        public bool IsPrimary { get; }

        /// <summary>
        /// The existing .md files of the Unity project's Assets folder. Files there are Unity assets: they are listed
        /// through the AssetDatabase and renamed/deleted through it (keeping .meta files), and no notes, folders,
        /// trash or attachments are ever created in it.
        /// </summary>
        public bool IsProjectFiles => Id == ProjectFilesId;

        internal NoteFileWatcher Watcher;

        public string TrashDirectory => Path.Combine(Root, ".trash");
        public string AssetsDirectory => Path.Combine(Root, "assets");

        public NoteVault(string id, string name, string root, bool isPrimary)
        {
            Id = id;
            Name = name;
            Root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            IsPrimary = isPrimary;
        }

        public bool Contains(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath)) return false;
            string full = Path.GetFullPath(fullPath);
            return full.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(full, Root, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Vault-relative path with forward slashes ("Design/Player.md").</summary>
        public string RelativePathOf(string fullPath)
        {
            string full = Path.GetFullPath(fullPath);
            if (!Contains(full)) return Path.GetFileName(full);
            return full.Length <= Root.Length ? "" : full.Substring(Root.Length + 1).Replace('\\', '/');
        }

        /// <summary>Absolute path of a vault-relative folder ("" = root).</summary>
        public string FolderPath(string relativeFolder) =>
            string.IsNullOrEmpty(relativeFolder) ? Root : Path.GetFullPath(Path.Combine(Root, relativeFolder.Replace('/', Path.DirectorySeparatorChar)));
    }

    /// <summary>User preferences for Fuzzy Notes (per machine, stored in EditorPrefs).</summary>
    public static class NoteSettings
    {
        private const string Prefix = "FuzzyNotes.";

        public static bool PersonalVaultEnabled
        {
            get => EditorPrefs.GetBool(Prefix + "PersonalVault.Enabled", false);
            set => EditorPrefs.SetBool(Prefix + "PersonalVault.Enabled", value);
        }

        public static string PersonalVaultPath
        {
            get => EditorPrefs.GetString(Prefix + "PersonalVault.Path",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "FuzzyNotes"));
            set => EditorPrefs.SetString(Prefix + "PersonalVault.Path", value ?? "");
        }

        /// <summary>Absolute paths of attached external (e.g. Obsidian) vaults.</summary>
        public static List<string> ExternalVaults
        {
            get
            {
                var list = new List<string>();
                foreach (var p in EditorPrefs.GetString(Prefix + "ExternalVaults", "").Split('\n'))
                {
                    if (!string.IsNullOrWhiteSpace(p)) list.Add(p.Trim());
                }
                return list;
            }
            set => EditorPrefs.SetString(Prefix + "ExternalVaults", string.Join("\n", value ?? new List<string>()));
        }

        /// <summary>Lists the .md files of the project's Assets folder as an extra "Assets" vault.</summary>
        public static bool ShowProjectMarkdown
        {
            get => EditorPrefs.GetBool(Prefix + "ProjectMarkdown.Enabled", true);
            set => EditorPrefs.SetBool(Prefix + "ProjectMarkdown.Enabled", value);
        }

        /// <summary>Shows the Assets vault's files in the graph view (off by default: READMEs would crowd it).</summary>
        public static bool ShowProjectMarkdownInGraph
        {
            get => EditorPrefs.GetBool(Prefix + "ProjectMarkdown.InGraph", false);
            set => EditorPrefs.SetBool(Prefix + "ProjectMarkdown.InGraph", value);
        }

        public static readonly string[] DefaultProjectMarkdownExcludes =
        {
            "Plugins", "ThirdParty", "Third Party", "TextMesh Pro", "Samples", "LICENSE*", "Third-Party Notices*", "ThirdPartyNotices*"
        };

        /// <summary>Patterns hiding Assets files from the Assets vault; see <see cref="ProjectMarkdownFilter"/>.</summary>
        public static List<string> ProjectMarkdownExcludes
        {
            get
            {
                string stored = EditorPrefs.GetString(Prefix + "ProjectMarkdown.Excludes", null);
                if (stored == null) return new List<string>(DefaultProjectMarkdownExcludes);
                var list = new List<string>();
                foreach (var p in stored.Split('\n'))
                {
                    if (!string.IsNullOrWhiteSpace(p)) list.Add(p.Trim());
                }
                return list;
            }
            set => EditorPrefs.SetString(Prefix + "ProjectMarkdown.Excludes", string.Join("\n", value ?? new List<string>()));
        }

        public static string DailyFolder
        {
            get => EditorPrefs.GetString(Prefix + "Daily.Folder", "Daily");
            set => EditorPrefs.SetString(Prefix + "Daily.Folder", value ?? "");
        }

        public static string DailyDateFormat
        {
            get => EditorPrefs.GetString(Prefix + "Daily.Format", "yyyy-MM-dd");
            set => EditorPrefs.SetString(Prefix + "Daily.Format", string.IsNullOrWhiteSpace(value) ? "yyyy-MM-dd" : value);
        }

        public static string DailyTemplate
        {
            get => EditorPrefs.GetString(Prefix + "Daily.Template", "");
            set => EditorPrefs.SetString(Prefix + "Daily.Template", value ?? "");
        }

        public static string TemplatesFolder
        {
            get => EditorPrefs.GetString(Prefix + "Templates.Folder", "Templates");
            set => EditorPrefs.SetString(Prefix + "Templates.Folder", value ?? "");
        }

        /// <summary>Raised after an appearance setting (zoom, line length, toolbar) changed.</summary>
        public static event Action AppearanceChanged;

        /// <summary>Text zoom of the note editor (1 = 100%).</summary>
        public static float EditorZoom
        {
            get => EditorPrefs.GetFloat(Prefix + "Editor.Zoom", 1f);
            set => SetAppearance(() => EditorPrefs.SetFloat(Prefix + "Editor.Zoom", (float)Math.Round(value, 2)));
        }

        /// <summary>Limits the note text to a comfortable column width instead of the full pane.</summary>
        public static bool ReadableLineLength
        {
            get => EditorPrefs.GetBool(Prefix + "Editor.ReadableLineLength", true);
            set => SetAppearance(() => EditorPrefs.SetBool(Prefix + "Editor.ReadableLineLength", value));
        }

        /// <summary>Shows the formatting toolbar above the editor (formatting is also in the context menu and slash commands).</summary>
        public static bool ShowFormattingBar
        {
            get => EditorPrefs.GetBool(Prefix + "Editor.FormattingBar", false);
            set => SetAppearance(() => EditorPrefs.SetBool(Prefix + "Editor.FormattingBar", value));
        }

        /// <summary>Shows the note title as a heading at the top of the page.</summary>
        public static bool ShowInlineTitle
        {
            get => EditorPrefs.GetBool(Prefix + "Editor.InlineTitle", true);
            set => SetAppearance(() => EditorPrefs.SetBool(Prefix + "Editor.InlineTitle", value));
        }

        /// <summary>Shows the vertical icon strip on the left of the window.</summary>
        public static bool ShowRibbon
        {
            get => EditorPrefs.GetBool(Prefix + "Window.Ribbon", true);
            set => SetAppearance(() => EditorPrefs.SetBool(Prefix + "Window.Ribbon", value));
        }

        /// <summary>Raised after a setting that is baked into built UI (accent color) changed: windows rebuild.</summary>
        public static event Action ThemeChanged;

        private static string s_AccentColor;

        /// <summary>Accent color as "#RRGGBB"; empty uses the IDE theme's keyword color. Cached: read on every repaint.</summary>
        public static string AccentColor
        {
            get => s_AccentColor ?? (s_AccentColor = EditorPrefs.GetString(Prefix + "Window.Accent", ""));
            set
            {
                s_AccentColor = value ?? "";
                EditorPrefs.SetString(Prefix + "Window.Accent", s_AccentColor);
                ThemeChanged?.Invoke();
            }
        }

        /// <summary>New panes open notes in reading view instead of editing view.</summary>
        public static bool DefaultReadingMode
        {
            get => EditorPrefs.GetBool(Prefix + "Editor.DefaultReading", false);
            set => EditorPrefs.SetBool(Prefix + "Editor.DefaultReading", value);
        }

        /// <summary>Asks before moving notes to the trash (otherwise an "Undo" notice is shown).</summary>
        public static bool ConfirmDelete
        {
            get => EditorPrefs.GetBool(Prefix + "Files.ConfirmDelete", false);
            set => EditorPrefs.SetBool(Prefix + "Files.ConfirmDelete", value);
        }

        private static void SetAppearance(Action write)
        {
            write();
            AppearanceChanged?.Invoke();
        }

        public static DailyNoteOptions DailyOptions => new DailyNoteOptions
        {
            Folder = DailyFolder,
            DateFormat = DailyDateFormat,
            Template = DailyTemplate
        };
    }
}
