using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FuzzyLogicLabs.FuzzyIDE.Editor.Core;
using FuzzyLogicLabs.FuzzyIDE.Editor.Roslyn;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEngine;
using UnityEngine.UIElements;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.UI
{
    public class FileState
    {
        public string FilePath;
        public string FileName;
        public List<string> Lines;
        public TextHistoryManager History;
        public bool IsDirty;
        public int CaretLine;
        public int CaretColumn;
        public ILanguageService LanguageService;
        public System.DateTime LastWriteTime;
        public bool HasExternalConflict;
        /// <summary>Line ending chosen in the status bar; applied by the next save (null keeps the file's own).</summary>
        public string LineEndingOverride;
    }

    public class FileNode
    {
        public string Name;
        public string FullPath;
        public bool IsFolder;
        public SortedDictionary<string, FileNode> Children = new SortedDictionary<string, FileNode>(StringComparer.OrdinalIgnoreCase);
        public int ErrorCount;
        public int WarningCount;
        public bool HasUnsavedChanges;
    }

    public partial class VirtualIDEWindow : EditorWindow
    {
        public static VirtualIDEWindow ActiveInstance { get; private set; }

        public class EditorPaneState
        {
            public VisualElement Container;
            public VisualElement TabBar;
            public VirtualizedCodeViewer Viewer;
            public List<string> OpenedTabs = new List<string>();
            public HashSet<string> PinnedTabs = new HashSet<string>();
            public string ActiveTab = null;
        }

        private EditorPaneState m_LeftPane;
        private EditorPaneState m_RightPane;
        private EditorPaneState m_ActivePane;
        private TwoPaneSplitView m_EditorSplitView;
        
        [Serializable]
        private class SerializedFileState
        {
            public string FilePath;
            public string FileName;
            public List<string> Lines;
            public bool IsDirty;
            public int CaretLine;
            public int CaretColumn;
            public string SerializedHistory;
        }

        private StyleCursor GetResizeCursor(UnityEditor.MouseCursor cursorType)
        {
            object boxedCursor = new UnityEngine.UIElements.Cursor();
            var type = typeof(UnityEngine.UIElements.Cursor);
            var prop = type.GetProperty("defaultCursorId", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
            if (prop != null) prop.SetValue(boxedCursor, (int)cursorType);
            else 
            {
                var field = type.GetField("defaultCursorId", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
                if (field != null) field.SetValue(boxedCursor, (int)cursorType);
            }
            return new StyleCursor((UnityEngine.UIElements.Cursor)boxedCursor);
        }

        // Serialization for Domain Reload
        [SerializeField] private System.Collections.Generic.List<string> m_LeftPaneTabs = new System.Collections.Generic.List<string>();
        [SerializeField] private System.Collections.Generic.List<string> m_RightPaneTabs = new System.Collections.Generic.List<string>();
        [SerializeField] private System.Collections.Generic.List<string> m_LeftPanePinnedTabs = new System.Collections.Generic.List<string>();
        [SerializeField] private System.Collections.Generic.List<string> m_RightPanePinnedTabs = new System.Collections.Generic.List<string>();
        [SerializeField] private System.Collections.Generic.List<string> m_ExpandedFolders = new System.Collections.Generic.List<string>();
        [SerializeField] private string m_ActiveLeftTab;
        [SerializeField] private string m_ActiveRightTab;
        [SerializeField] private bool m_IsRightPaneOpen = false;
        [SerializeField] private bool m_LeftPanelVisible = true;
        [SerializeField] private string m_FocusedPane = "left";
        [SerializeField] private System.Collections.Generic.List<string> m_SerializedFiles = new System.Collections.Generic.List<string>();
        [SerializeField] private bool m_IsWatchPanelOpen = false;

        // Multi-Tab System State
        private Dictionary<string, FileState> m_OpenedFiles = new Dictionary<string, FileState>();

        // UI Elements
        private VisualElement m_LeftPanel;
        private VisualElement m_Splitter;
        private ScrollView m_HierarchyScrollView;
        private List<string> m_ProjectScripts = new List<string>();

        // Layout State
        private bool m_IsLeftPanelOpen = true;
        private bool m_IsDraggingSplitter = false;

        // Toolbar Button References
        private Button m_ToggleExplorerBtn;
        private Button m_ToggleSplitBtn;
        private Button m_SaveAllBtn;
        private Button m_FindBtn;
        private Button m_WatchBtn;
        private Button m_AssistantBtn;
        
        // Drag State
        private VisualElement m_GhostTab;
        private string m_DraggingTabPath;
        private EditorPaneState m_DraggingSourcePane;
        private bool m_IsDraggingTab = false;
        private Vector2 m_DragStartPos;
        
        [MenuItem("Window/Fuzzy Logic Labs/Fuzzy IDE/IDE Window", false, 10)]
        public static void ShowWindow()
        {
            var wnd = GetWindow<VirtualIDEWindow>();
            wnd.titleContent = new GUIContent("Virtual IDE");
        }

#if UNITY_6000_1_OR_NEWER
        [OnOpenAsset(-1)]
        public static bool OnOpenAsset(UnityEngine.EntityId instanceID, int line, int column)
        {
            string path = null;
            var getAssetPathMethod = typeof(AssetDatabase).GetMethod("GetAssetPath", new Type[] { typeof(UnityEngine.EntityId) });
            if (getAssetPathMethod != null)
            {
                path = (string)getAssetPathMethod.Invoke(null, new object[] { instanceID });
            }

            if (string.IsNullOrEmpty(path))
            {
                var entityToObjectMethod = typeof(EditorUtility).GetMethod("EntityIdToObject", new Type[] { typeof(UnityEngine.EntityId) });
                if (entityToObjectMethod != null)
                {
                    var targetObj = entityToObjectMethod.Invoke(null, new object[] { instanceID }) as UnityEngine.Object;
                    if (targetObj != null)
                    {
                        path = AssetDatabase.GetAssetPath(targetObj);
                    }
                }
            }

            return OpenAssetPath(path, line);
        }
#else
        [OnOpenAsset(-1)]
        public static bool OnOpenAsset(int instanceID, int line, int column)
        {
            string path = null;
            var getAssetPathMethod = typeof(AssetDatabase).GetMethod("GetAssetPath", new Type[] { typeof(int) });
            if (getAssetPathMethod != null)
            {
                path = (string)getAssetPathMethod.Invoke(null, new object[] { instanceID });
            }

            if (string.IsNullOrEmpty(path))
            {
                var targetObj = EditorUtility.InstanceIDToObject(instanceID);
                if (targetObj != null)
                {
                    path = AssetDatabase.GetAssetPath(targetObj);
                }
            }

            return OpenAssetPath(path, line);
        }
#endif

        /// <summary>Double-clicked assets: .md files go to Fuzzy Notes, supported text files to the IDE.</summary>
        private static bool OpenAssetPath(string path, int line)
        {
            if (string.IsNullOrEmpty(path)) return false;
            var behaviors = CodeIDESettings.Current.Behaviors;

            if (behaviors.OpenMarkdownInFuzzyNotes && FuzzyLogicLabs.FuzzyIDE.Editor.Notes.Integration.MarkdownAssets.IsMarkdown(path) &&
                FuzzyLogicLabs.FuzzyIDE.Editor.Notes.Integration.MarkdownAssets.OpenInNotes(path, line))
                return true;

            if (!behaviors.DefaultOpenInFuzzyIDE || !IsSupportedExtension(path)) return false;
            var wnd = GetWindow<VirtualIDEWindow>();
            wnd.LoadFileAndJumpToLine(path, line);
            wnd.Focus();
            return true;
        }

        [MenuItem("Assets/Open in Fuzzy IDE", false, 1)]
        private static void OpenInFuzzyIDE()
        {
            if (Selection.activeObject != null)
            {
                string path = AssetDatabase.GetAssetPath(Selection.activeObject);
                if (!string.IsNullOrEmpty(path) && IsSupportedExtension(path))
                {
                    var wnd = GetWindow<VirtualIDEWindow>();
                    wnd.LoadFileAndJumpToLine(path, 0);
                    wnd.Focus();
                }
            }
        }

        [MenuItem("Assets/Open in Fuzzy IDE", true)]
        private static bool OpenInFuzzyIDEValidate()
        {
            if (Selection.activeObject != null)
            {
                string path = AssetDatabase.GetAssetPath(Selection.activeObject);
                return !string.IsNullOrEmpty(path) && IsSupportedExtension(path);
            }
            return false;
        }

        private void OnEnable()
        {
            ActiveInstance = this;
            RoslynAnalyzerService.ActiveDefines = UnityEditor.EditorUserBuildSettings.activeScriptCompilationDefines;
            CodeIDESettings.OnSettingsChanged += ApplySettingsPhase14;
            EditorApplication.update += OnEditorUpdate;
            EditorApplication.update += RefreshWatchPanelData;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            ProjectDiagnosticsService.OnDiagnosticsUpdated += OnDiagnosticsUpdatedForIDE;
            ProjectAssetOperations.OnAssetRenamed += OnAssetRenamedInProject;
            ProjectAssetOperations.OnAssetDeleted += OnAssetDeletedInProject;
            FileWatcherService.OnFilesChangedBatch -= OnFilesChangedFromWatcher;
            FileWatcherService.OnFilesChangedBatch += OnFilesChangedFromWatcher;
            
            // Force rebuild on domain reload if Unity doesn't call CreateGUI automatically
            EditorApplication.delayCall += () => {
                if (m_LeftPane == null && rootVisualElement != null)
                {
                    CreateGUI();
                }
            };
        }

        private void OnDisable()
        {
            if (ActiveInstance == this) ActiveInstance = null;
            CodeIDESettings.OnSettingsChanged -= ApplySettingsPhase14;
            EditorApplication.update -= OnEditorUpdate;
            EditorApplication.update -= RefreshWatchPanelData;
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            ProjectDiagnosticsService.OnDiagnosticsUpdated -= OnDiagnosticsUpdatedForIDE;
            CompilationManager.OnReloadProblem -= OnReloadProblem;
            ProjectAssetOperations.OnAssetRenamed -= OnAssetRenamedInProject;
            ProjectAssetOperations.OnAssetDeleted -= OnAssetDeletedInProject;
            FileWatcherService.OnFilesChangedBatch -= OnFilesChangedFromWatcher;
            SaveState();
        }

        private void OnFocus()
        {
            CompilationManager.CheckCompilationStatus();
            UpdateCompilationUIState();
        }

        private void OnDiagnosticsUpdatedForIDE()
        {
            RebuildHierarchyUI();
            RefreshProblems();
        }

        private void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingEditMode)
            {
                bool anySaved = false;
                if (m_LeftPane != null) SaveCurrentActiveFile(m_LeftPane);
                if (m_RightPane != null) SaveCurrentActiveFile(m_RightPane);
                
                foreach (var kvp in m_OpenedFiles)
                {
                    if (kvp.Value.IsDirty)
                    {
                        SaveFile(kvp.Key, kvp.Value);
                        anySaved = true;
                    }
                }
                if (anySaved) AssetDatabase.Refresh();
            }
        }

        private double m_LastFileCheckTime = 0;

        private bool m_LastCompilingCheck = false;
        private double m_NextInlineHintUpdate;

        private void OnEditorUpdate()
        {
            CompilationManager.CheckCompilationStatus();

            bool isCompiling = CompilationManager.IsCompiling;
            if (m_LastCompilingCheck != isCompiling)
            {
                m_LastCompilingCheck = isCompiling;
                UpdateCompilationUIState();
                Repaint();
            }

            if (EditorApplication.isPlaying)
            {
                bool changed = false;
                if (m_LeftPane?.Viewer != null) changed |= m_LeftPane.Viewer.UpdateLiveBadges();
                if (m_RightPane?.Viewer != null) changed |= m_RightPane.Viewer.UpdateLiveBadges();
                
                if (changed)
                {
                    Repaint();
                }
            }

            // Live values, Console messages and asset usages at the end of lines: frequent in Play Mode, relaxed otherwise.
            double hintNow = EditorApplication.timeSinceStartup;
            if (hintNow >= m_NextInlineHintUpdate && !isCompiling)
            {
                m_NextInlineHintUpdate = hintNow + (EditorApplication.isPlaying ? 0.25 : 0.5);
                bool hintsChanged = false;
                if (m_LeftPane?.Viewer != null) hintsChanged |= m_LeftPane.Viewer.UpdateInlineHints();
                if (m_RightPane?.Viewer != null && m_RightPane.Viewer.resolvedStyle.display != DisplayStyle.None) hintsChanged |= m_RightPane.Viewer.UpdateInlineHints();
                if (hintsChanged) Repaint();
            }

            double now = EditorApplication.timeSinceStartup;
            if (now - m_LastFileCheckTime > 1.0)
            {
                m_LastFileCheckTime = now;
                RefreshStatusFileInfo();
                bool anyReloaded = false;
                foreach (var kvp in m_OpenedFiles)
                {
                    var state = kvp.Value;
                    if (!state.IsDirty)
                    {
                        string absolutePath = System.IO.Path.GetFullPath(state.FilePath);
                        if (System.IO.File.Exists(absolutePath))
                        {
                            var lastWrite = System.IO.File.GetLastWriteTime(absolutePath);
                            if (lastWrite > state.LastWriteTime)
                            {
                                state.LastWriteTime = lastWrite;
                                state.Lines = System.IO.File.ReadAllLines(absolutePath).ToList();
                                if (state.Lines.Count == 0) state.Lines.Add("");
                                state.HasExternalConflict = false;
                                
                                if (m_LeftPane != null && IsSamePath(m_LeftPane.ActiveTab, state.FilePath))
                                {
                                    m_LeftPane.Viewer.SetLines(state.Lines, false);
                                    m_LeftPane.Viewer.TriggerExternalReload();
                                    anyReloaded = true;
                                }
                                if (m_RightPane != null && IsSamePath(m_RightPane.ActiveTab, state.FilePath))
                                {
                                    m_RightPane.Viewer.SetLines(state.Lines, false);
                                    m_RightPane.Viewer.TriggerExternalReload();
                                    anyReloaded = true;
                                }
                            }
                        }
                    }
                }
                if (anyReloaded) Repaint();
            }
        }

        private static bool IsSamePath(string pathA, string pathB)
        {
            if (string.IsNullOrEmpty(pathA) || string.IsNullOrEmpty(pathB)) return false;
            return string.Equals(FileWatcherService.NormalizePath(pathA), FileWatcherService.NormalizePath(pathB), StringComparison.OrdinalIgnoreCase);
        }

        private void OnFilesChangedFromWatcher(IReadOnlyList<FileWatcherEvent> events)
        {
            if (events == null || events.Count == 0) return;

            bool anyReloaded = false;
            bool anyAssetStructuralChange = false;

            foreach (var ev in events)
            {
                string normEvPath = FileWatcherService.NormalizePath(ev.FullPath);

                // 1. Check if it matches any open file
                foreach (var kvp in m_OpenedFiles)
                {
                    var state = kvp.Value;
                    string normStatePath = FileWatcherService.NormalizePath(state.FilePath);

                    if (string.Equals(normEvPath, normStatePath, StringComparison.OrdinalIgnoreCase))
                    {
                        if (ev.ChangeType == FileWatcherChangeType.Deleted)
                        {
                            state.IsDirty = true;
                            RefreshTabBar(m_LeftPane);
                            RefreshTabBar(m_RightPane);
                            UpdateWindowTitle();
                            continue;
                        }

                        if (!state.IsDirty)
                        {
                            string abs = Path.GetFullPath(state.FilePath);
                            if (File.Exists(abs))
                            {
                                state.LastWriteTime = File.GetLastWriteTime(abs);
                                state.Lines = File.ReadAllLines(abs).ToList();
                                if (state.Lines.Count == 0) state.Lines.Add("");
                                state.HasExternalConflict = false;

                                if (m_LeftPane != null && IsSamePath(m_LeftPane.ActiveTab, state.FilePath))
                                {
                                    m_LeftPane.Viewer.SetLines(state.Lines, false);
                                    m_LeftPane.Viewer.TriggerExternalReload();
                                    anyReloaded = true;
                                }
                                if (m_RightPane != null && IsSamePath(m_RightPane.ActiveTab, state.FilePath))
                                {
                                    m_RightPane.Viewer.SetLines(state.Lines, false);
                                    m_RightPane.Viewer.TriggerExternalReload();
                                    anyReloaded = true;
                                }
                            }
                        }
                        else
                        {
                            // Conflict: file is dirty in IDE and modified externally
                            state.HasExternalConflict = true;

                            EditorPaneState targetPane = null;
                            if (m_LeftPane != null && IsSamePath(m_LeftPane.ActiveTab, state.FilePath)) targetPane = m_LeftPane;
                            else if (m_RightPane != null && IsSamePath(m_RightPane.ActiveTab, state.FilePath)) targetPane = m_RightPane;

                            if (targetPane != null)
                            {
                                targetPane.Viewer.ShowConflictBanner(
                                    onReloadFromDisk: () =>
                                    {
                                        string abs = Path.GetFullPath(state.FilePath);
                                        if (File.Exists(abs))
                                        {
                                            state.LastWriteTime = File.GetLastWriteTime(abs);
                                            state.Lines = File.ReadAllLines(abs).ToList();
                                            if (state.Lines.Count == 0) state.Lines.Add("");
                                            state.IsDirty = false;
                                            state.HasExternalConflict = false;
                                            targetPane.Viewer.SetLines(state.Lines, false);
                                            targetPane.Viewer.TriggerExternalReload();
                                            RefreshTabBar(m_LeftPane);
                                            RefreshTabBar(m_RightPane);
                                            UpdateWindowTitle();
                                            RebuildHierarchyUI();
                                        }
                                    },
                                    onKeepLocal: () =>
                                    {
                                        state.HasExternalConflict = false;
                                    }
                                );
                            }
                        }
                        break;
                    }
                }

                // 2. Mark modified in CompilationManager
                if (ev.ChangeType == FileWatcherChangeType.Changed || ev.ChangeType == FileWatcherChangeType.Created)
                {
                    CompilationManager.MarkFileModified(ev.FullPath);
                }

                // 3. Structural change (Created, Deleted, Renamed) in Assets
                if (ev.ChangeType == FileWatcherChangeType.Created ||
                    ev.ChangeType == FileWatcherChangeType.Deleted ||
                    ev.ChangeType == FileWatcherChangeType.Renamed)
                {
                    if (IsSupportedExtension(ev.FullPath) || ev.FullPath.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                    {
                        anyAssetStructuralChange = true;
                    }
                }
            }

            if (anyAssetStructuralChange)
            {
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                RefreshProjectScripts();
            }

            if (anyReloaded)
            {
                Repaint();
            }
        }

        private void SaveState()
        {
            if (m_LeftPane != null)
            {
                // Force sync current active tab's viewer state to FileState before saving
                if (!string.IsNullOrEmpty(m_LeftPane.ActiveTab) && m_OpenedFiles.TryGetValue(m_LeftPane.ActiveTab, out var leftState))
                {
                    leftState.Lines = m_LeftPane.Viewer.GetLines();
                    leftState.CaretLine = m_LeftPane.Viewer.CaretLine;
                    leftState.CaretColumn = m_LeftPane.Viewer.CaretColumn;
                }
                
                m_LeftPaneTabs = new List<string>(m_LeftPane.OpenedTabs);
                m_LeftPanePinnedTabs = new List<string>(m_LeftPane.PinnedTabs);
                m_ActiveLeftTab = m_LeftPane.ActiveTab;
            }
            if (m_RightPane != null)
            {
                // Force sync current active tab's viewer state to FileState before saving
                if (!string.IsNullOrEmpty(m_RightPane.ActiveTab) && m_OpenedFiles.TryGetValue(m_RightPane.ActiveTab, out var rightState))
                {
                    rightState.Lines = m_RightPane.Viewer.GetLines();
                    rightState.CaretLine = m_RightPane.Viewer.CaretLine;
                    rightState.CaretColumn = m_RightPane.Viewer.CaretColumn;
                }

                m_RightPaneTabs = new List<string>(m_RightPane.OpenedTabs);
                m_RightPanePinnedTabs = new List<string>(m_RightPane.PinnedTabs);
                m_ActiveRightTab = m_RightPane.ActiveTab;
            }
            m_LeftPanelVisible = m_IsLeftPanelOpen;
            m_FocusedPane = (m_ActivePane == m_RightPane) ? "right" : "left";

            m_SerializedFiles.Clear();
            foreach (var kvp in m_OpenedFiles)
            {
                var sfs = new SerializedFileState
                {
                    FilePath = kvp.Key,
                    FileName = kvp.Value.FileName,
                    Lines = kvp.Value.Lines,
                    IsDirty = kvp.Value.IsDirty,
                    CaretLine = kvp.Value.CaretLine,
                    CaretColumn = kvp.Value.CaretColumn,
                    SerializedHistory = kvp.Value.History != null ? kvp.Value.History.Serialize() : ""
                };
                m_SerializedFiles.Add(JsonUtility.ToJson(sfs));
            }
        }

        private Button CreateModernIconButton(Action action, string iconName, string tooltip, Func<bool> getActive = null)
        {
            var btn = new Button(action) { tooltip = tooltip };
            btn.style.width = 28;
            btn.style.height = 24;
            btn.style.borderTopLeftRadius = 4;
            btn.style.borderBottomLeftRadius = 4;
            btn.style.borderTopRightRadius = 4;
            btn.style.borderBottomRightRadius = 4;
            btn.style.borderTopWidth = 1;
            btn.style.borderBottomWidth = 1;
            btn.style.borderLeftWidth = 1;
            btn.style.borderRightWidth = 1;
            btn.style.borderTopColor = Color.clear;
            btn.style.borderBottomColor = Color.clear;
            btn.style.borderLeftColor = Color.clear;
            btn.style.borderRightColor = Color.clear;
            btn.style.backgroundColor = Color.clear;
            btn.style.marginTop = 2;
            btn.style.marginBottom = 2;
            btn.style.marginLeft = 1;
            btn.style.marginRight = 1;
            btn.style.paddingTop = 0;
            btn.style.paddingBottom = 0;
            btn.style.paddingLeft = 0;
            btn.style.paddingRight = 0;
            btn.style.alignItems = Align.Center;
            btn.style.justifyContent = Justify.Center;

            var icon = new Image();
            var iconContent = EditorGUIUtility.IconContent(iconName);
            if (iconContent != null && iconContent.image != null)
            {
                icon.image = iconContent.image;
            }
            icon.style.width = 16;
            icon.style.height = 16;
            btn.Add(icon);

            bool isHovered = false;

            void UpdateVisuals()
            {
                bool active = getActive != null && getActive();
                if (active)
                {
                    btn.style.backgroundColor = isHovered 
                        ? new Color(0.24f, 0.34f, 0.48f, 0.95f) 
                        : new Color(0.20f, 0.28f, 0.40f, 0.85f);
                    Color borderColor = isHovered ? new Color(0.38f, 0.54f, 0.76f, 0.9f) : new Color(0.30f, 0.42f, 0.60f, 0.7f);
                    btn.style.borderTopColor = borderColor;
                    btn.style.borderBottomColor = borderColor;
                    btn.style.borderLeftColor = borderColor;
                    btn.style.borderRightColor = borderColor;
                }
                else
                {
                    btn.style.backgroundColor = isHovered 
                        ? new Color(0.26f, 0.26f, 0.28f, 0.85f) 
                        : Color.clear;
                    Color borderColor = isHovered ? new Color(0.36f, 0.36f, 0.40f, 0.6f) : Color.clear;
                    btn.style.borderTopColor = borderColor;
                    btn.style.borderBottomColor = borderColor;
                    btn.style.borderLeftColor = borderColor;
                    btn.style.borderRightColor = borderColor;
                }
            }

            btn.RegisterCallback<MouseEnterEvent>(e => { isHovered = true; UpdateVisuals(); });
            btn.RegisterCallback<MouseLeaveEvent>(e => { isHovered = false; UpdateVisuals(); });

            btn.userData = (Action)UpdateVisuals;
            UpdateVisuals();

            return btn;
        }

        private VisualElement CreateToolbarDivider()
        {
            var div = new VisualElement();
            div.style.width = 1;
            div.style.height = 16;
            div.style.backgroundColor = new Color(0.30f, 0.30f, 0.33f, 0.45f);
            div.style.marginLeft = 5;
            div.style.marginRight = 5;
            div.style.alignSelf = Align.Center;
            return div;
        }

        private void UpdateToolbarButtonsState()
        {
            if (m_ToggleExplorerBtn?.userData is Action updateExplorer) updateExplorer();
            if (m_ToggleSplitBtn?.userData is Action updateSplit) updateSplit();
            if (m_WatchBtn?.userData is Action updateWatch) updateWatch();
            if (m_AssistantBtn?.userData is Action updateAssistant) updateAssistant();
        }

        public void CreateGUI()
        {
            rootVisualElement.Clear();

            // Part 1: The Flexbox Layout (UI Hierarchy)
            rootVisualElement.style.flexDirection = FlexDirection.Column;
            rootVisualElement.style.flexGrow = 1;

            // 1. Top Toolbar
            var toolbar = new VisualElement();
            toolbar.style.flexDirection = FlexDirection.Row;
            toolbar.style.height = 32;
            toolbar.style.flexShrink = 0;
            toolbar.style.backgroundColor = new Color(0.165f, 0.165f, 0.170f); // #2A2A2B Sleek modern dark header
            toolbar.style.borderBottomWidth = 1;
            toolbar.style.borderBottomColor = new Color(0.11f, 0.11f, 0.11f);
            toolbar.style.paddingLeft = 6;
            toolbar.style.paddingRight = 6;
            toolbar.style.alignItems = Align.Center;

            // Group 1: View / Layout Controls
            m_ToggleExplorerBtn = CreateModernIconButton(ToggleExplorer, "Project", "Toggle Explorer (Ctrl+B)", () => m_IsLeftPanelOpen);
            m_ToggleSplitBtn = CreateModernIconButton(ToggleSplitView, "d_VerticalSplit", "Toggle Split View (Side-by-Side)", () => m_IsRightPaneOpen);
            toolbar.Add(m_ToggleExplorerBtn);
            toolbar.Add(m_ToggleSplitBtn);

            toolbar.Add(CreateToolbarDivider());

            // Group 2: Editor Tools
            m_SaveAllBtn = CreateModernIconButton(SaveAll, "SaveActive", "Save All Files (Ctrl+Shift+S)");
            m_FindBtn = CreateModernIconButton(FindInProject, "Search Icon", "Find in Project (Ctrl+Shift+F)");
            toolbar.Add(m_SaveAllBtn);
            toolbar.Add(m_FindBtn);

            toolbar.Add(CreateToolbarDivider());

            // Group 3: Diagnostics & Assistant Panels
            m_WatchBtn = CreateModernIconButton(() => ToggleWatchPanel(!m_IsWatchPanelOpen), "d_DebuggerAttached", "Toggle Watch & Diagnostics Panel", () => m_IsWatchPanelOpen);
            m_AssistantBtn = CreateModernIconButton(() => ToggleAssistantPanel(!m_IsAssistantPanelOpen), "d_console.infoicon.sml", "Toggle AI Assistant Panel", () => m_IsAssistantPanelOpen);
            toolbar.Add(m_WatchBtn);
            toolbar.Add(m_AssistantBtn);

            toolbar.Add(CreateToolbarDivider());

            // Group 4: Git Source Control Pill
            InitializeGitToolbar(toolbar);

            // Group 5: Compilation & Domain Reload Controls (Right-aligned via spacer)
            InitializeCompilationToolbar(toolbar);

            rootVisualElement.Add(toolbar);

            // 2. Main Content Area
            var mainContent = new VisualElement();
            mainContent.style.flexDirection = FlexDirection.Row;
            mainContent.style.flexGrow = 1;

            // 3. Left Panel (File Explorer)
            m_LeftPanel = new VisualElement();
            m_LeftPanel.style.width = 250;
            m_LeftPanel.style.backgroundColor = new Color(0.145f, 0.145f, 0.149f); // #252526
            m_LeftPanel.style.display = m_IsLeftPanelOpen ? DisplayStyle.Flex : DisplayStyle.None;

            InitializeExplorerHeader(m_LeftPanel);

            m_HierarchyScrollView = new ScrollView();
            m_HierarchyScrollView.style.flexGrow = 1;
            m_LeftPanel.Add(m_HierarchyScrollView);

            m_LeftPanel.RegisterCallback<KeyDownEvent>(e =>
            {
                if (!string.IsNullOrEmpty(m_SelectedExplorerPath))
                {
                    if (e.keyCode == KeyCode.Delete)
                    {
                        if (AssetDatabase.IsValidFolder(m_SelectedExplorerPath) || Directory.Exists(m_SelectedExplorerPath))
                        {
                            ProjectAssetOperations.DeleteAsset(m_SelectedExplorerPath);
                        }
                        else
                        {
                            if (ProjectAssetOperations.DeleteAsset(m_SelectedExplorerPath))
                            {
                                OnAssetDeletedInProject(m_SelectedExplorerPath);
                            }
                        }
                        e.StopPropagation();
                    }
                    else if (e.keyCode == KeyCode.F2)
                    {
                        CreateAssetDialog.OpenRename(m_SelectedExplorerPath);
                        e.StopPropagation();
                    }
                    else if (e.keyCode == KeyCode.D && ((e.modifiers & (EventModifiers.Control | EventModifiers.Command)) != 0))
                    {
                        if (!AssetDatabase.IsValidFolder(m_SelectedExplorerPath))
                        {
                            if (ProjectAssetOperations.DuplicateAsset(m_SelectedExplorerPath, out string newPath, out _))
                            {
                                LoadFileAndJumpToLine(newPath, -1);
                            }
                        }
                        e.StopPropagation();
                    }
                }
            });
            
            RefreshProjectScripts();
            mainContent.Add(m_LeftPanel);

            // Splitter
            m_Splitter = new VisualElement();
            m_Splitter.style.width = 4;
            m_Splitter.style.backgroundColor = new Color(0.176f, 0.176f, 0.176f);
            m_Splitter.style.cursor = GetResizeCursor(UnityEditor.MouseCursor.SplitResizeLeftRight);

            m_Splitter.style.display = m_IsLeftPanelOpen ? DisplayStyle.Flex : DisplayStyle.None;

            m_Splitter.RegisterCallback<PointerDownEvent>(e => 
            {
                m_IsDraggingSplitter = true; 
                m_Splitter.CapturePointer(e.pointerId); 
                e.StopPropagation();
            });

            m_Splitter.RegisterCallback<PointerUpEvent>(e => 
            {
                m_IsDraggingSplitter = false; 
                m_Splitter.ReleasePointer(e.pointerId); 
                e.StopPropagation();
            });

            m_Splitter.RegisterCallback<PointerMoveEvent>(e => 
            {
                if (m_IsDraggingSplitter) 
                {
                    float newWidth = e.position.x;
                    if (newWidth < 100) newWidth = 100;
                    if (newWidth > rootVisualElement.worldBound.width - 200) newWidth = rootVisualElement.worldBound.width - 200;
                    
                    m_LeftPanel.style.width = newWidth;
                    e.StopPropagation();
                }
            });
            mainContent.Add(m_Splitter);

            // 4. Right Panel (Editor Area)
            var rightPanel = new VisualElement();
            rightPanel.style.flexDirection = FlexDirection.Column;
            rightPanel.style.flexGrow = 1;
            rightPanel.style.minWidth = 0;

            m_LeftPane = CreatePane("left-pane");
            m_RightPane = CreatePane("right-pane");
            
            m_EditorSplitView = new TwoPaneSplitView(0, 400, TwoPaneSplitViewOrientation.Horizontal);
            m_EditorSplitView.style.flexGrow = 1;
            m_EditorSplitView.Add(m_LeftPane.Container);
            m_EditorSplitView.Add(m_RightPane.Container);
            if (!m_IsRightPaneOpen) m_EditorSplitView.CollapseChild(1); // Start collapsed
            
            SetActivePane(m_LeftPane);

            rightPanel.Add(m_EditorSplitView);
            mainContent.Add(rightPanel);

            rootVisualElement.Add(mainContent);

            var overlayContainer = new VisualElement();
            overlayContainer.name = "popup-overlay-container";
            overlayContainer.style.position = Position.Absolute;
            overlayContainer.style.left = 0; overlayContainer.style.top = 0; overlayContainer.style.right = 0; overlayContainer.style.bottom = 0;
            overlayContainer.pickingMode = PickingMode.Ignore;
            rootVisualElement.Add(overlayContainer);
            
            m_LeftPane.Viewer.RegisterPopupsToContainer(overlayContainer);
            m_RightPane.Viewer.RegisterPopupsToContainer(overlayContainer);

            // Initial Restore
            RestoreState();

            InitializeAssistantPanel(mainContent);
            InitializePhase14UI();
            InitializeBottomPanel();
            InitializeGitIntegration();
            InitializeStatusBar(rootVisualElement);
            InitializeCompilationIntegration();
            UpdateToolbarButtonsState();

            rootVisualElement.RegisterCallback<KeyDownEvent>(e =>
            {
                if (CodeIDESettings.Current.Keybindings.CheckAction(IDEAction.Recompile, e))
                {
                    Recompile();
                    e.StopPropagation();
                }
                else if (CodeIDESettings.Current.Keybindings.CheckAction(IDEAction.ReloadDomain, e))
                {
                    ReloadDomain();
                    e.StopPropagation();
                }
            });
        }

        private static bool IsSupportedExtension(string path)
        {
            string ext = Path.GetExtension(path).ToLower();
            return ext == ".cs" || ext == ".json" || ext == ".asmdef" || ext == ".shader" || 
                   ext == ".hlsl" || ext == ".cginc" || ext == ".xml" || ext == ".uxml" || 
                   ext == ".txt" || ext == ".md" || ext == ".uss";
        }

        private void ToggleExplorer()
        {
            m_IsLeftPanelOpen = !m_IsLeftPanelOpen;
            m_LeftPanel.style.display = m_IsLeftPanelOpen ? DisplayStyle.Flex : DisplayStyle.None;
            m_Splitter.style.display = m_IsLeftPanelOpen ? DisplayStyle.Flex : DisplayStyle.None;
            UpdateToolbarButtonsState();
        }

        private void ToggleSplitView()
        {
            if (!m_IsRightPaneOpen)
            {
                m_IsRightPaneOpen = true;
                m_EditorSplitView.UnCollapse();
                SetActivePane(m_RightPane);
            }
            else
            {
                m_IsRightPaneOpen = false;
                // Merge right into left
                foreach (var tab in m_RightPane.OpenedTabs)
                {
                    if (!m_LeftPane.OpenedTabs.Contains(tab))
                        m_LeftPane.OpenedTabs.Add(tab);
                }
                string nextLeftTab = m_LeftPane.ActiveTab ?? m_RightPane.ActiveTab;
                m_RightPane.OpenedTabs.Clear();
                m_RightPane.ActiveTab = null;
                m_RightPane.Viewer.SetLines(new System.Collections.Generic.List<string> { "// No active script. Double click a script in the Explorer to open it." }, false);
                
                m_EditorSplitView.CollapseChild(1);
                SetActivePane(m_LeftPane);
                
                if (nextLeftTab != null) SwitchToTab(m_LeftPane, nextLeftTab);
                RefreshTabBar(m_LeftPane);
                RefreshTabBar(m_RightPane);
            }
            UpdateToolbarButtonsState();
        }


        private EditorPaneState CreatePane(string name)
        {
            var pane = new EditorPaneState();
            pane.Container = new VisualElement { name = name };
            pane.Container.style.flexDirection = FlexDirection.Column;
            pane.Container.style.flexGrow = 1;
            pane.Container.style.overflow = Overflow.Hidden;
            pane.Container.style.minWidth = 0;

            pane.TabBar = new VisualElement();
            pane.TabBar.style.flexDirection = FlexDirection.Row;
            pane.TabBar.style.height = 35;

            var tabContainer = new VisualElement();
            tabContainer.style.height = 35;
            tabContainer.style.flexShrink = 0;
            
            var scrollView = new ScrollView(ScrollViewMode.Horizontal);
            scrollView.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            scrollView.style.position = Position.Absolute;
            scrollView.style.left = 0;
            scrollView.style.right = 0;
            scrollView.style.top = 0;
            scrollView.style.bottom = 0;
            scrollView.style.backgroundColor = new Color(0.145f, 0.145f, 0.149f);
            
            scrollView.Add(pane.TabBar);
            tabContainer.Add(scrollView);
            pane.Container.Add(tabContainer);

            pane.Viewer = new VirtualizedCodeViewer(null);
            pane.Viewer.style.flexGrow = 1;
            pane.Viewer.OnTextChanged += () => HandleTextChanged(pane);
            pane.Viewer.OnSaveRequested += () => SaveCurrentActiveFile(pane);
            pane.Viewer.OnRecompileRequested += Recompile;
            pane.Viewer.OnReloadDomainRequested += ReloadDomain;
            pane.Viewer.OnCaretPositionChanged += (line, col) => UpdateStatusCaret(line, col);
            pane.Viewer.OnLoadFileRequested += LoadFileAndJumpToLine;
            pane.Viewer.OnJumpRequested += (from, to) => OnViewerJumpRequested(pane, from, to);
            pane.Viewer.OnRenameRequested += pos => HandleRenameRequested(pane, pos);
            pane.Viewer.OnFindReferencesRequested += pos => HandleFindReferencesRequested(pane, pos);
            pane.Viewer.OnWatchLiveRequested += pos => HandleWatchLiveRequested(pane, pos);
            pane.Viewer.OnRemoveWatchLiveRequested += pos => HandleRemoveWatchLiveRequested(pane, pos);
            pane.Container.Add(pane.Viewer);

            pane.Container.RegisterCallback<PointerDownEvent>(e => SetActivePane(pane), TrickleDown.TrickleDown);

            return pane;
        }

        private void SetActivePane(EditorPaneState pane)
        {
            if (pane == null) return;
            m_ActivePane = pane;
            m_LeftPane.TabBar.style.backgroundColor = (m_ActivePane == m_LeftPane) ? new Color(0.16f, 0.16f, 0.16f) : new Color(0.12f, 0.12f, 0.12f);
            m_RightPane.TabBar.style.backgroundColor = (m_ActivePane == m_RightPane) ? new Color(0.16f, 0.16f, 0.16f) : new Color(0.12f, 0.12f, 0.12f);
        }

        /// <summary>Opens a file (tab keys are project-relative) and jumps to a 1-based line; both ends go into Back/Forward history.</summary>
        public void LoadFileAndJumpToLine(string assetPath, int line)
        {
            RecordCurrentLocation();
            bool wasLoading = m_InLoadJump;
            m_InLoadJump = true;
            try
            {
                LoadFileAndJumpToLineCore(assetPath, line);
            }
            finally
            {
                m_InLoadJump = wasLoading;
            }
            RecordCurrentLocation();
        }

        private bool m_InLoadJump;

        private void LoadFileAndJumpToLineCore(string assetPath, int line)
        {
            // Go to Definition hands over absolute paths; tabs are keyed by project-relative asset paths.
            if (!string.IsNullOrEmpty(assetPath) && Path.IsPathRooted(assetPath))
                assetPath = ProjectDiagnosticsService.NormalizeAssetPath(assetPath);

            if (m_ActivePane == null) SetActivePane(m_LeftPane);
            EditorPaneState targetPane = m_ActivePane;

            if (!m_OpenedFiles.ContainsKey(assetPath))
            {
                string absolutePath = Path.GetFullPath(assetPath);
                var newLines = new System.Collections.Generic.List<string> { "" };
                if (System.IO.File.Exists(absolutePath))
                {
                    newLines = System.IO.File.ReadAllLines(absolutePath).ToList();
                    if (newLines.Count == 0) newLines.Add("");
                }

                ILanguageService langService = CreateLanguageService(assetPath);

                var state = new FileState
                {
                    FilePath = assetPath,
                    FileName = System.IO.Path.GetFileName(assetPath),
                    Lines = newLines,
                    History = new TextHistoryManager(),
                    IsDirty = false,
                    CaretLine = 0,
                    CaretColumn = 0,
                    LanguageService = langService,
                    LastWriteTime = System.IO.File.GetLastWriteTime(absolutePath)
                };

                m_OpenedFiles[assetPath] = state;
                targetPane.OpenedTabs.Add(assetPath);
            }
            else
            {
                if (m_LeftPane.OpenedTabs.Contains(assetPath)) targetPane = m_LeftPane;
                else if (m_RightPane.OpenedTabs.Contains(assetPath)) targetPane = m_RightPane;
                else targetPane.OpenedTabs.Add(assetPath);
            }

            SetActivePane(targetPane);
            RefreshTabBar(targetPane);
            SwitchToTab(targetPane, assetPath);

            if (line > 0 && targetPane.Viewer != null)
            {
                targetPane.Viewer.JumpToLine(line - 1);
            }
        }

        private static List<string> s_FallbackReferences;

        // References for C# files outside every script assembly (project files use ProjectCompilationCache instead).
        // Computed once per domain: scanning the AppDomain for every opened file was a noticeable hitch.
        private static List<string> GetFallbackReferences()
        {
            if (s_FallbackReferences != null) return s_FallbackReferences;
            s_FallbackReferences = UnityCompat.GetLoadedAssemblies()
                .Select(a => (asm: a, path: UnityCompat.GetAssemblyPath(a)))
                .Where(a => a.path != null && System.IO.File.Exists(a.path))
                .GroupBy(a => a.asm.GetName().Name, StringComparer.OrdinalIgnoreCase)
                .Select(g => {
                    var preferred = g.FirstOrDefault(a => a.path.Replace('\\', '/').Contains("/Library/ScriptAssemblies/"));
                    return preferred.path ?? g.First().path;
                })
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            return s_FallbackReferences;
        }

        private ILanguageService CreateLanguageService(string path)
        {
            string ext = System.IO.Path.GetExtension(path).ToLower();
            switch (ext)
            {
                case ".cs":
                    return new CSharpLanguageService(System.IO.Path.GetFullPath(path), GetFallbackReferences());
                case ".json":
                case ".asmdef":
                    return new JsonLanguageService();
                case ".shader":
                case ".hlsl":
                case ".cginc":
                    return new ShaderLanguageService();
                case ".xml":
                case ".uxml":
                case ".uss":
                    return new XmlLanguageService();
                default:
                    return new PlaintextLanguageService();
            }
        }

        private void SwitchToTab(EditorPaneState pane, string assetPath)
        {
            string newPath = string.IsNullOrEmpty(assetPath) ? null : System.IO.Path.GetFullPath(assetPath);
            if (pane.ActiveTab == assetPath && pane.Viewer.CurrentFilePath == newPath) return;

            if (!string.IsNullOrEmpty(pane.ActiveTab) && m_OpenedFiles.TryGetValue(pane.ActiveTab, out var oldState))
            {
                oldState.Lines = pane.Viewer.GetLines();
                oldState.History = pane.Viewer.History;
                oldState.CaretLine = pane.Viewer.CaretLine;
                oldState.CaretColumn = pane.Viewer.CaretColumn;
            }

            pane.ActiveTab = assetPath;
            OnTabActivated(assetPath);

            if (pane.ActiveTab != null && m_OpenedFiles.TryGetValue(pane.ActiveTab, out var newState))
            {
                pane.Viewer.LanguageService = newState.LanguageService; 
                pane.Viewer.CurrentFilePath = System.IO.Path.GetFullPath(newState.FilePath);
                pane.Viewer.SetLines(newState.Lines, false); 
                pane.Viewer.History = newState.History;
                pane.Viewer.CaretLine = newState.CaretLine;
                pane.Viewer.JumpToLine(newState.CaretLine, true);

                if (newState.HasExternalConflict)
                {
                    pane.Viewer.ShowConflictBanner(
                        onReloadFromDisk: () =>
                        {
                            string abs = Path.GetFullPath(newState.FilePath);
                            if (File.Exists(abs))
                            {
                                newState.LastWriteTime = File.GetLastWriteTime(abs);
                                newState.Lines = File.ReadAllLines(abs).ToList();
                                if (newState.Lines.Count == 0) newState.Lines.Add("");
                                newState.IsDirty = false;
                                newState.HasExternalConflict = false;
                                pane.Viewer.SetLines(newState.Lines, false);
                                pane.Viewer.TriggerExternalReload();
                                RefreshTabBar(pane);
                                UpdateWindowTitle();
                                RebuildHierarchyUI();
                            }
                        },
                        onKeepLocal: () =>
                        {
                            newState.HasExternalConflict = false;
                        }
                    );
                }
            }
            else
            {
                pane.Viewer.SetLines(new System.Collections.Generic.List<string> { "// No active script. Double click a script in the Explorer to open it." }, false);
                pane.Viewer.CurrentFilePath = null;
            }
            
            RefreshTabBar(pane);
            UpdateWindowTitle();
        }

        private void TogglePinTab(EditorPaneState pane, string filePath)
        {
            if (pane.PinnedTabs.Contains(filePath))
            {
                pane.PinnedTabs.Remove(filePath);
            }
            else
            {
                pane.PinnedTabs.Add(filePath);
            }
            RefreshTabBar(pane);
            SaveState();
        }

        private void RefreshTabBar(EditorPaneState pane)
        {
            if (pane.TabBar == null) return;
            pane.TabBar.Clear();

            var pinned = pane.OpenedTabs.Where(x => pane.PinnedTabs.Contains(x)).ToList();
            var unpinned = pane.OpenedTabs.Where(x => !pane.PinnedTabs.Contains(x)).ToList();
            pane.OpenedTabs.Clear();
            pane.OpenedTabs.AddRange(pinned);
            pane.OpenedTabs.AddRange(unpinned);

            foreach (var filePath in pane.OpenedTabs)
            {
                if (!m_OpenedFiles.TryGetValue(filePath, out var state)) continue;

                var tabBtn = new VisualElement();
                tabBtn.style.flexDirection = FlexDirection.Row;
                tabBtn.style.alignItems = Align.Center;
                tabBtn.style.paddingLeft = 10;
                tabBtn.style.paddingRight = 10;
                tabBtn.style.paddingTop = 0;
                tabBtn.style.paddingBottom = 0;
                tabBtn.style.borderRightWidth = 1;
                tabBtn.style.borderRightColor = new Color(0.145f, 0.145f, 0.149f);
                tabBtn.style.borderLeftWidth = 0;
                tabBtn.style.borderBottomWidth = 0;
                tabBtn.style.borderTopWidth = 2;
                tabBtn.style.borderTopColor = Color.clear;
                tabBtn.style.marginTop = 0;
                tabBtn.style.marginBottom = 0;
                tabBtn.style.marginLeft = 0;
                tabBtn.style.marginRight = 0;

                string displayTitle = state.FileName + (state.IsDirty ? "*" : "");
                var titleLabel = new Label(displayTitle);
                titleLabel.style.marginRight = 5;

                bool isPinned = pane.PinnedTabs.Contains(filePath);
                
                var pinIcon = new Label("📌");
                pinIcon.style.marginRight = 4;
                pinIcon.style.fontSize = 10;
                pinIcon.style.opacity = isPinned ? 1f : 0f;
                pinIcon.style.paddingLeft = 4;
                pinIcon.style.paddingRight = 4;
                pinIcon.style.paddingTop = 2;
                pinIcon.style.paddingBottom = 2;
                pinIcon.style.borderTopLeftRadius = 4;
                pinIcon.style.borderBottomLeftRadius = 4;
                pinIcon.style.borderTopRightRadius = 4;
                pinIcon.style.borderBottomRightRadius = 4;
                
                pinIcon.RegisterCallback<PointerDownEvent>(e => {
                    TogglePinTab(pane, filePath);
                    e.StopPropagation();
                });
                
                pinIcon.RegisterCallback<MouseEnterEvent>(e => {
                    pinIcon.style.backgroundColor = new Color(1f, 1f, 1f, 0.15f);
                    if (!isPinned) pinIcon.style.opacity = 1f;
                });
                pinIcon.RegisterCallback<MouseLeaveEvent>(e => {
                    pinIcon.style.backgroundColor = Color.clear;
                    if (!isPinned) pinIcon.style.opacity = 0.6f;
                });
                
                tabBtn.RegisterCallback<MouseEnterEvent>(e => {
                    if (!isPinned) pinIcon.style.opacity = 0.6f;
                });
                tabBtn.RegisterCallback<MouseLeaveEvent>(e => {
                    if (!isPinned) pinIcon.style.opacity = 0f;
                });

                tabBtn.Add(pinIcon);
                
                if (filePath == pane.ActiveTab)
                {
                    tabBtn.style.backgroundColor = new Color(0.117f, 0.117f, 0.117f); 
                    tabBtn.style.borderTopColor = new Color(0f, 0.478f, 0.8f); 
                    titleLabel.style.color = new Color(1f, 1f, 1f);
                    titleLabel.style.opacity = 1f;
                }
                else
                {
                    tabBtn.style.backgroundColor = new Color(0.176f, 0.176f, 0.176f); 
                    tabBtn.style.borderTopColor = Color.clear;
                    titleLabel.style.color = new Color(0.6f, 0.6f, 0.6f);
                    titleLabel.style.opacity = 0.7f;
                }

                tabBtn.Add(titleLabel);

                var closeBtn = new Button(() => CloseTab(pane, filePath));
                closeBtn.text = "x";
                closeBtn.style.backgroundColor = Color.clear;
                closeBtn.style.color = titleLabel.style.color;
                closeBtn.style.borderTopWidth = 0;
                closeBtn.style.borderBottomWidth = 0;
                closeBtn.style.borderLeftWidth = 0;
                closeBtn.style.borderRightWidth = 0;
                closeBtn.style.marginTop = 0;
                closeBtn.style.marginBottom = 0;
                closeBtn.style.marginLeft = 0;
                closeBtn.style.marginRight = 0;
                closeBtn.style.paddingTop = 0;
                closeBtn.style.paddingBottom = 0;
                closeBtn.style.paddingLeft = 4;
                closeBtn.style.paddingRight = 4;
                
                closeBtn.RegisterCallback<MouseEnterEvent>(e => closeBtn.style.backgroundColor = new Color(1f, 0.3f, 0.3f, 0.5f));
                closeBtn.RegisterCallback<MouseLeaveEvent>(e => closeBtn.style.backgroundColor = Color.clear);

                tabBtn.Add(closeBtn);
                
                tabBtn.RegisterCallback<ContextClickEvent>(e => {
                    var menu = new GenericMenu();
                    if (pane.PinnedTabs.Contains(filePath))
                        menu.AddItem(new GUIContent("Unpin Tab"), false, () => TogglePinTab(pane, filePath));
                    else
                        menu.AddItem(new GUIContent("Pin Tab"), false, () => TogglePinTab(pane, filePath));
                    
                    menu.AddItem(new GUIContent("Close Tab"), false, () => CloseTab(pane, filePath));
                    AddTabContextItems(menu, pane, filePath);
                    menu.ShowAsContext();
                    e.StopPropagation();
                });
                
                // Drag and Drop Logic
                tabBtn.RegisterCallback<PointerDownEvent>(e => {
                    if (e.target == closeBtn) return; // Allow close button to be clicked
                    if (e.button != 0) return;
                    m_IsDraggingTab = false;
                    m_DragStartPos = e.position;
                    tabBtn.CapturePointer(e.pointerId);
                    e.StopPropagation();
                });

                tabBtn.RegisterCallback<PointerMoveEvent>(e => {
                    if (tabBtn.HasPointerCapture(e.pointerId))
                    {
                        if (!m_IsDraggingTab && Vector2.Distance(m_DragStartPos, e.position) > 5f)
                        {
                            m_IsDraggingTab = true;
                            m_DraggingTabPath = filePath;
                            m_DraggingSourcePane = pane;
                            
                            m_GhostTab = new Button();
                            ((Button)m_GhostTab).text = tabBtn.Q<Label>().text; 
                            m_GhostTab.style.position = Position.Absolute;
                            m_GhostTab.style.width = tabBtn.resolvedStyle.width;
                            m_GhostTab.style.height = tabBtn.resolvedStyle.height;
                            m_GhostTab.style.backgroundColor = new Color(0.2f, 0.2f, 0.2f, 0.8f);
                            rootVisualElement.Add(m_GhostTab);
                            
                            tabBtn.style.opacity = 0.5f;
                        }

                        if (m_IsDraggingTab && m_GhostTab != null)
                        {
                            Vector2 localPos = rootVisualElement.WorldToLocal(e.position);
                            m_GhostTab.style.left = localPos.x - m_GhostTab.resolvedStyle.width / 2;
                            m_GhostTab.style.top = localPos.y - m_GhostTab.resolvedStyle.height / 2;
                        }
                    }
                });

                tabBtn.RegisterCallback<PointerUpEvent>(e => {
                    if (e.button == 2)
                    {
                        if (tabBtn.HasPointerCapture(e.pointerId))
                        {
                            tabBtn.ReleasePointer(e.pointerId);
                        }
                        CloseTab(pane, filePath);
                        e.StopPropagation();
                        return;
                    }

                    if (tabBtn.HasPointerCapture(e.pointerId))
                    {
                        tabBtn.ReleasePointer(e.pointerId);
                        if (m_IsDraggingTab)
                        {
                            FinishDrag(e.position);
                        }
                        else
                        {
                            SetActivePane(pane);
                            SwitchToTab(pane, filePath);
                        }
                        e.StopPropagation();
                    }
                });
                
                tabBtn.RegisterCallback<PointerCaptureOutEvent>(e => {
                    if (m_IsDraggingTab)
                    {
                        FinishDrag(new Vector2(-1000, -1000)); // Reset drag if capture lost
                    }
                });

                pane.TabBar.Add(tabBtn);
            }
        }

        private void FinishDrag(Vector2 screenPos)
        {
            m_IsDraggingTab = false;
            if (m_GhostTab != null)
            {
                if (rootVisualElement.Contains(m_GhostTab)) rootVisualElement.Remove(m_GhostTab);
                m_GhostTab = null;
            }
            
            if (m_DraggingSourcePane == null || m_DraggingTabPath == null) return;
            
            EditorPaneState targetPane = null;
            if (m_RightPane.Container.resolvedStyle.width > 0 && m_RightPane.Container.worldBound.Contains(screenPos))
            {
                targetPane = m_RightPane;
            }
            else if (m_LeftPane.Container.worldBound.Contains(screenPos))
            {
                targetPane = m_LeftPane;
            }
            
            if (targetPane != null)
            {
                var tabBar = targetPane.TabBar;
                int insertIndex = targetPane.OpenedTabs.Count;
                for (int i = 0; i < tabBar.childCount; i++)
                {
                    var child = tabBar[i];
                    if (screenPos.x < child.worldBound.center.x)
                    {
                        insertIndex = i;
                        break;
                    }
                }
                
                m_DraggingSourcePane.OpenedTabs.Remove(m_DraggingTabPath);
                if (m_DraggingSourcePane.ActiveTab == m_DraggingTabPath)
                {
                    SwitchToTab(m_DraggingSourcePane, m_DraggingSourcePane.OpenedTabs.FirstOrDefault());
                }
                
                if (targetPane == m_DraggingSourcePane && insertIndex > targetPane.OpenedTabs.Count)
                    insertIndex = targetPane.OpenedTabs.Count;

                targetPane.OpenedTabs.Insert(insertIndex, m_DraggingTabPath);
                
                SetActivePane(targetPane);
                SwitchToTab(targetPane, m_DraggingTabPath);
                
                RefreshTabBar(m_LeftPane);
                if (m_RightPane.Container.style.display != DisplayStyle.None)
                    RefreshTabBar(m_RightPane);
            }
            else
            {
                RefreshTabBar(m_DraggingSourcePane);
            }
            
            m_DraggingTabPath = null;
            m_DraggingSourcePane = null;
        }

        private void CloseTab(EditorPaneState pane, string assetPath)
        {
            if (m_OpenedFiles.TryGetValue(assetPath, out var state))
            {
                if (state.IsDirty)
                {
                    bool save = EditorUtility.DisplayDialog("Unsaved Changes", $"Save changes to {state.FileName} before closing?", "Save", "Don't Save");
                    if (save) SaveFile(assetPath, state);
                    else ProjectCompilationCache.RevertToDisk(assetPath);
                }

                pane.OpenedTabs.Remove(assetPath);
                if (!m_LeftPane.OpenedTabs.Contains(assetPath) && !m_RightPane.OpenedTabs.Contains(assetPath))
                {
                    m_OpenedFiles.Remove(assetPath);
                    ProjectDiagnosticsService.ClearRoslynDiagnostics(assetPath);
                }
                
                if (pane.ActiveTab == assetPath)
                {
                    SwitchToTab(pane, pane.OpenedTabs.FirstOrDefault());
                }
                else
                {
                    RefreshTabBar(pane);
                }
                UpdateWindowTitle();
                RebuildHierarchyUI();
            }
        }

        private void HandleTextChanged(EditorPaneState pane)
        {
            // Both panes showing one file share its line list; the other view must rebuild its rows.
            var otherPane = pane == m_LeftPane ? m_RightPane : m_LeftPane;
            if (otherPane?.Viewer != null && otherPane.Viewer.GetLines() == pane.Viewer.GetLines())
                otherPane.Viewer.RefreshAfterSharedEdit();

            if (!string.IsNullOrEmpty(pane.ActiveTab) && m_OpenedFiles.TryGetValue(pane.ActiveTab, out var state))
            {
                if (!state.IsDirty)
                {
                    state.IsDirty = true;
                    RefreshTabBar(pane);
                    UpdateWindowTitle();
                    RebuildHierarchyUI();
                }
            }
        }

        private void SaveCurrentActiveFile(EditorPaneState pane)
        {
            if (pane == null) pane = m_ActivePane;
            if (pane != null && !string.IsNullOrEmpty(pane.ActiveTab) && m_OpenedFiles.TryGetValue(pane.ActiveTab, out var state))
            {
                state.Lines = pane.Viewer.GetLines();
                SaveFile(pane.ActiveTab, state);
                var compSettings = CodeIDESettings.Current?.Compilation;
                if (compSettings != null && compSettings.CompileOnSave)
                {
                    CompilationManager.RequestCompile();
                }
            }
        }

        private void SaveFile(string assetPath, FileState state)
        {
            if (state.IsDirty)
            {
                string absolutePath = System.IO.Path.GetFullPath(assetPath);
                // Keep the file's own line endings and trailing newline; joining with Environment.NewLine rewrote
                // every line of LF files and stripped the final newline on each save.
                var format = TextFileFormat.DetectFile(absolutePath);
                if (state.LineEndingOverride != null) format.NewLine = state.LineEndingOverride;
                string fullCode = format.Join(state.Lines, CodeIDESettings.Current.Behaviors.InsertFinalNewline);
                
                FileWatcherService.SuppressSelfSave(absolutePath);
                System.IO.File.WriteAllText(absolutePath, fullCode, format.Encoding); // keeps a BOM the file had
                state.LineEndingOverride = null;
                state.LastWriteTime = System.IO.File.GetLastWriteTime(absolutePath);
                
                state.IsDirty = false;
                state.HasExternalConflict = false;
                CompilationManager.MarkFileModified(assetPath);
                FuzzyLogicLabs.FuzzyIDE.Editor.Notes.Integration.CodeTodoIndex.NotifySaved(assetPath, fullCode);
                // Debug.Log($"[FuzzyIDE] Saved {state.FileName}");
                RefreshTabBar(m_LeftPane);
                RefreshTabBar(m_RightPane);
                UpdateWindowTitle();
                RebuildHierarchyUI();
                _ = Git.GitService.RefreshStatusAsync();
            }
        }

        private void SaveAll()
        {
            SaveAll(true);
        }

        private void SaveAll(bool triggerCompile)
        {
            if (m_LeftPane.ActiveTab != null && m_OpenedFiles.TryGetValue(m_LeftPane.ActiveTab, out var lState))
                lState.Lines = m_LeftPane.Viewer.GetLines();
            if (m_RightPane.ActiveTab != null && m_OpenedFiles.TryGetValue(m_RightPane.ActiveTab, out var rState))
                rState.Lines = m_RightPane.Viewer.GetLines();

            bool anySaved = false;
            foreach (var kvp in m_OpenedFiles)
            {
                if (kvp.Value.IsDirty)
                {
                    SaveFile(kvp.Key, kvp.Value);
                    anySaved = true;
                }
            }

            var compSettings = CodeIDESettings.Current?.Compilation;
            if (triggerCompile && anySaved && compSettings != null && compSettings.CompileOnSave)
            {
                CompilationManager.RequestCompile();
            }
        }

        public void Recompile()
        {
            SaveAll(false);
            CompilationManager.RequestCompile();
        }

        public void ReloadDomain()
        {
            SaveAll(false);
            CompilationManager.TriggerDomainReload();
        }

        private void FindInProject()
        {
            ShowFindInFiles();
        }

        private void UpdateWindowTitle()
        {
            string title = "Virtual IDE";
            if (m_ActivePane != null && !string.IsNullOrEmpty(m_ActivePane.ActiveTab) && m_OpenedFiles.TryGetValue(m_ActivePane.ActiveTab, out var state))
            {
                title = state.FileName + (state.IsDirty ? "*" : "");
            }
            titleContent = new GUIContent(title);
        }

        private void RestoreState()
        {
            m_IsLeftPanelOpen = m_LeftPanelVisible;
            m_LeftPanel.style.display = m_IsLeftPanelOpen ? DisplayStyle.Flex : DisplayStyle.None;
            m_Splitter.style.display = m_IsLeftPanelOpen ? DisplayStyle.Flex : DisplayStyle.None;

            m_LeftPane.OpenedTabs.Clear();
            m_RightPane.OpenedTabs.Clear();
            m_OpenedFiles.Clear();

            if (m_SerializedFiles != null)
            {
                foreach (var json in m_SerializedFiles)
                {
                    if (string.IsNullOrEmpty(json)) continue;
                    var sfs = JsonUtility.FromJson<SerializedFileState>(json);
                    if (sfs != null && !string.IsNullOrEmpty(sfs.FilePath))
                    {
                        var history = new TextHistoryManager();

                        var state = new FileState
                        {
                            FilePath = sfs.FilePath,
                            FileName = sfs.FileName,
                            Lines = sfs.Lines,
                            IsDirty = sfs.IsDirty,
                            CaretLine = sfs.CaretLine,
                            CaretColumn = sfs.CaretColumn,
                            History = history,
                            LanguageService = CreateLanguageService(sfs.FilePath)
                        };

                        if (!string.IsNullOrEmpty(sfs.SerializedHistory))
                        {
                            history.Deserialize(
                                sfs.SerializedHistory, 
                                state.Lines, 
                                (r, c) => { 
                                    if (m_LeftPane != null && m_LeftPane.ActiveTab == state.FilePath && m_LeftPane.Viewer != null)
                                        m_LeftPane.Viewer.SetCaretPosition(r, c, true);
                                    else if (m_RightPane != null && m_RightPane.ActiveTab == state.FilePath && m_RightPane.Viewer != null)
                                        m_RightPane.Viewer.SetCaretPosition(r, c, true);
                                    else
                                    {
                                        state.CaretLine = r;
                                        state.CaretColumn = c;
                                    }
                                },
                                () => { } // UpdateSelection no-op for now
                            );
                        }
                        
                        m_OpenedFiles[sfs.FilePath] = state;
                    }
                }
            }

            foreach (var path in m_LeftPaneTabs) 
            {
                if (!m_OpenedFiles.ContainsKey(path)) LoadFileWithoutFocus(m_LeftPane, path);
                if (!m_LeftPane.OpenedTabs.Contains(path)) m_LeftPane.OpenedTabs.Add(path);
            }
            if (m_LeftPanePinnedTabs != null)
            {
                foreach (var path in m_LeftPanePinnedTabs) m_LeftPane.PinnedTabs.Add(path);
            }
            
            foreach (var path in m_RightPaneTabs) 
            {
                if (!m_OpenedFiles.ContainsKey(path)) LoadFileWithoutFocus(m_RightPane, path);
                if (!m_RightPane.OpenedTabs.Contains(path)) m_RightPane.OpenedTabs.Add(path);
            }
            if (m_RightPanePinnedTabs != null)
            {
                foreach (var path in m_RightPanePinnedTabs) m_RightPane.PinnedTabs.Add(path);
            }

            if (m_IsRightPaneOpen)
            {
                m_EditorSplitView.UnCollapse();
            }
            else
            {
                m_EditorSplitView.CollapseChild(1);
            }

            if (!string.IsNullOrEmpty(m_ActiveLeftTab) && m_LeftPane.OpenedTabs.Contains(m_ActiveLeftTab))
                SwitchToTab(m_LeftPane, m_ActiveLeftTab);
            else if (m_LeftPane.OpenedTabs.Count > 0)
                SwitchToTab(m_LeftPane, m_LeftPane.OpenedTabs[0]);
            else
                m_LeftPane.Viewer.SetLines(new List<string> { "// Double click a .cs script to open it in Virtual IDE!" });

            if (m_IsRightPaneOpen)
            {
                if (!string.IsNullOrEmpty(m_ActiveRightTab) && m_RightPane.OpenedTabs.Contains(m_ActiveRightTab))
                    SwitchToTab(m_RightPane, m_ActiveRightTab);
                else if (m_RightPane.OpenedTabs.Count > 0)
                    SwitchToTab(m_RightPane, m_RightPane.OpenedTabs[0]);
            }

            m_ActivePane = (m_FocusedPane == "right" && m_IsRightPaneOpen) ? m_RightPane : m_LeftPane;
            SetActivePane(m_ActivePane);
            UpdateToolbarButtonsState();
        }

        private void LoadFileWithoutFocus(EditorPaneState pane, string assetPath)
        {
            if (!m_OpenedFiles.ContainsKey(assetPath))
            {
                string absolutePath = System.IO.Path.GetFullPath(assetPath);
                if (!System.IO.File.Exists(absolutePath)) return;
                
                var newLines = System.IO.File.ReadAllLines(absolutePath).ToList();
                if (newLines.Count == 0) newLines.Add("");

                ILanguageService langService = CreateLanguageService(assetPath);

                var state = new FileState
                {
                    FilePath = assetPath,
                    FileName = System.IO.Path.GetFileName(assetPath),
                    Lines = newLines,
                    History = new TextHistoryManager(),
                    IsDirty = false,
                    CaretLine = 0,
                    CaretColumn = 0,
                    LanguageService = langService
                };

                m_OpenedFiles[assetPath] = state;
            }
            if (!pane.OpenedTabs.Contains(assetPath))
            {
                pane.OpenedTabs.Add(assetPath);
            }
        }
    }
}