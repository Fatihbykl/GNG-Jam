using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using FuzzyLogicLabs.FuzzyIDE.Editor.Core;
using FuzzyLogicLabs.FuzzyIDE.Editor.UI;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.Git
{
    public class GitWindow : EditorWindow
    {
        public static GitWindow Instance { get; private set; }

        private enum GitTab { Changes, History, Stashes }
        private enum PopoverTab { Branches, PullRequests }

        // ──────────────────────────────────────────────────────
        // Toast
        // ──────────────────────────────────────────────────────
        private enum ToastType { Success, Error, Info }

        // ──────────────────────────────────────────────────────
        // Section Helper
        // ──────────────────────────────────────────────────────
        private class FileSectionUI
        {
            public VisualElement Root;
            public VisualElement Header;
            public Label ChevronLabel;
            public Label TitleLabel;
            public Label BadgeLabel;
            public VisualElement ActionsContainer;
            public VisualElement ScrollView;
            public bool IsExpanded = true;
        }

        private struct SectionAction
        {
            public string Icon;
            public string Tooltip;
            public bool IsDanger;
            public Action Action;

            public SectionAction(string icon, string tooltip, bool isDanger, Action action)
            {
                Icon = icon;
                Tooltip = tooltip;
                IsDanger = isDanger;
                Action = action;
            }
        }

        // ──────────────────────────────────────────────────────
        // Fonts (JetBrains Mono from FuzzyIDE Resources)
        // ──────────────────────────────────────────────────────
        private static Font s_JetBrainsMonoRegular;
        private static Font s_JetBrainsMonoBold;

        private static void EnsureFontsLoaded()
        {
            if (s_JetBrainsMonoRegular == null)
            {
                s_JetBrainsMonoRegular = AssetDatabase.LoadAssetAtPath<Font>(
                    "Assets/FuzzyLogicLabs/FuzzyIDE/Editor/Resources/Fonts/Jetbrains Mono/JetBrainsMono-Regular.ttf");
            }
            if (s_JetBrainsMonoBold == null)
            {
                s_JetBrainsMonoBold = AssetDatabase.LoadAssetAtPath<Font>(
                    "Assets/FuzzyLogicLabs/FuzzyIDE/Editor/Resources/Fonts/Jetbrains Mono/JetBrainsMono-Bold.ttf");
            }
        }

        public static void ApplyMonoFont(VisualElement element, bool bold = false)
        {
            EnsureFontsLoaded();
            var font = bold ? (s_JetBrainsMonoBold ?? s_JetBrainsMonoRegular) : s_JetBrainsMonoRegular;
            if (font != null && element != null)
            {
                element.style.unityFontDefinition = FontDefinition.FromFont(font);
            }
        }

        // ──────────────────────────────────────────────────────
        // UI State
        // ──────────────────────────────────────────────────────
        private GitTab m_ActiveTab = GitTab.Changes;
        private bool   m_IsLoading = false;

        // Root containers
        private VisualElement m_RootContainer;
        private VisualElement m_TopBar;
        private VisualElement m_NavTabsBar;
        private VisualElement m_ContentArea;
        private VisualElement m_LoadingOverlay;
        private Label         m_LoadingLabel;
        private VisualElement m_ToastContainer;
        private VisualElement m_ErrorBanner;
        private Label         m_ErrorLabel;

        // Top bar segments (GitHub Desktop style)
        private VisualElement m_RepoSegment;
        private Label         m_RepoTitleLabel;
        private VisualElement m_BranchSegment;
        private Label         m_BranchTitleLabel;
        private Label         m_BranchTrackingLabel;
        private Button        m_SyncButton;
        private Label         m_SyncTitleLabel;
        private Label         m_SyncSubtitleLabel;
        private Button        m_BtnRefresh;

        // Branch Popover (GitHub Desktop Dropdown)
        private VisualElement m_PopoverOverlay;
        private VisualElement m_BranchPopover;
        private PopoverTab    m_ActivePopoverTab = PopoverTab.Branches;
        private Button        m_PopoverTabBranches;
        private Button        m_PopoverTabPRs;
        private TextField     m_PopoverSearchField;
        private VisualElement m_PopoverNewDrawer;
        private TextField     m_PopoverNewBranchInput;
        private Label         m_PopoverNewBranchHint;
        private Toggle        m_PopoverPublishToggle;
        private Label         m_PopoverDrawerTitle;
        private Button        m_PopoverDrawerConfirmBtn;
        private PopupField<string> m_PopoverBaseField;
        private string        m_RenameSourceBranch; // null = drawer creates a branch, otherwise renames this one
        private const string  PublishAfterCreatePrefKey = "FuzzyIDE_Git_PublishAfterCreate";
        private ScrollView    m_PopoverContentScroll;
        private string        m_PopoverSearchQuery = "";
        private List<GitBranchInfo> m_CachedPopoverBranches = new List<GitBranchInfo>();
        private string        m_DefaultBranch = "main";
        private (bool isGitHub, string ownerRepo, string webUrl) m_CachedGitHubInfo;

        // Changes view sections
        private TwoPaneSplitView m_ChangesSplitView;
        private FileSectionUI    m_ConflictSection;
        private FileSectionUI    m_StagedSection;
        private FileSectionUI    m_UnstagedSection;
        private FileSectionUI    m_UntrackedSection;
        private TextField        m_CommitMessageField;
        private Label            m_CommitCharCountLabel;
        private Toggle           m_AmendToggle;
        private Button           m_BtnCommit;
        private Button           m_BtnCommitAndPush;

        // Diff view elements
        private VisualElement m_DiffPanel;
        private VisualElement m_DiffPlaceholderBox;
        private Label         m_DiffFileNameLabel;
        private Label         m_DiffFilePathLabel;
        private Label         m_DiffStatsAdded;
        private Label         m_DiffStatsDeleted;
        private Button        m_BtnOpenInEditor;
        private ScrollView    m_DiffContentScrollView;
        private GitFileStatus m_SelectedDiffFile;
        private bool          m_SelectedDiffIsStaged;

        // Tracking selected file rows
        private VisualElement m_SelectedFileRow;
        private VisualElement m_SelectedCommitFileRow;

        // History view
        private ScrollView    m_HistoryScrollView;
        private VisualElement m_CommitDetailPanel;
        private VisualElement m_SelectedCommitRow;
        private string        m_HistoryFilterQuery = "";
        private List<GitCommitInfo> m_CachedCommits = new List<GitCommitInfo>();

        // Stashes view
        private ScrollView m_StashesScrollView;
        private TextField  m_StashMessageField;

        // Avatar colour palette (deterministic by name)
        private static readonly Color[] s_AvatarColors =
        {
            new Color(0.22f, 0.49f, 0.88f),
            new Color(0.18f, 0.62f, 0.51f),
            new Color(0.69f, 0.31f, 0.73f),
            new Color(0.86f, 0.49f, 0.07f),
            new Color(0.16f, 0.55f, 0.79f),
            new Color(0.73f, 0.23f, 0.23f),
            new Color(0.29f, 0.68f, 0.31f),
            new Color(0.59f, 0.29f, 0.73f),
        };

        // ──────────────────────────────────────────────────────
        // Menu + Lifecycle
        // ──────────────────────────────────────────────────────

        [MenuItem("Window/Fuzzy Logic Labs/Fuzzy IDE/Git Integration", false, 25)]
        public static void ShowWindow()
        {
            var wnd = GetWindow<GitWindow>();
            wnd.titleContent = new GUIContent("Git", EditorGUIUtility.IconContent("d_Collab").image);
            wnd.minSize = new Vector2(650, 480);
            wnd.Show();
        }

        /// <summary>Opens the History tab filtered to <paramref name="hash"/> (full or abbreviated) and shows that commit.</summary>
        public static void RevealCommit(string hash)
        {
            ShowWindow();
            var wnd = GetWindow<GitWindow>();
            wnd.m_HistoryFilterQuery = hash ?? "";
            if (wnd.m_ContentArea != null) wnd.SwitchTab(GitTab.History);
            else wnd.m_ActiveTab = GitTab.History; // CreateGUI opens the active tab
        }

        private void OnEnable()
        {
            Instance = this;
            GitService.OnStatusUpdated -= HandleStatusUpdated;
            GitService.OnStatusUpdated += HandleStatusUpdated;
            GitService.OnGitError      -= HandleGitError;
            GitService.OnGitError      += HandleGitError;
        }

        private void OnDisable()
        {
            if (Instance == this) Instance = null;
            GitService.OnStatusUpdated -= HandleStatusUpdated;
            GitService.OnGitError      -= HandleGitError;
        }

        // ──────────────────────────────────────────────────────
        // CreateGUI
        // ──────────────────────────────────────────────────────

        public void CreateGUI()
        {
            rootVisualElement.Clear();

            var styleSheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(
                "Assets/FuzzyLogicLabs/FuzzyIDE/Editor/Git/GitStyles.uss");
            if (styleSheet != null)
                rootVisualElement.styleSheets.Add(styleSheet);

            m_RootContainer = new VisualElement();
            m_RootContainer.AddToClassList("git-root");
            rootVisualElement.Add(m_RootContainer);

            // Global keyboard shortcuts (F5 or Ctrl+R for refresh)
            rootVisualElement.RegisterCallback<KeyDownEvent>(e =>
            {
                if (e.keyCode == KeyCode.F5 || (e.keyCode == KeyCode.R && (e.ctrlKey || e.commandKey)))
                {
                    e.StopPropagation();
                    _ = GitService.RefreshStatusAsync();
                }
            });

            // Error banner
            BuildErrorBanner();

            if (!GitService.IsGitRepository)
            {
                GitService.DetectRepository();
            }

            if (!GitService.IsGitRepository)
            {
                BuildNoRepositoryUI();
                return;
            }

            BuildTopBar();
            BuildNavTabs();
            BuildOperationBanner();
            _ = RefreshRemoteCacheAsync();

            m_ContentArea = new VisualElement();
            m_ContentArea.style.flexGrow = 1;
            m_ContentArea.style.overflow = Overflow.Hidden;
            m_RootContainer.Add(m_ContentArea);

            // Toast, loading overlay and popover (popover is on top layer)
            BuildToastContainer();
            BuildLoadingOverlay();
            BuildBranchPopover();

            SwitchTab(m_ActiveTab);
            _ = GitService.RefreshStatusAsync();
        }

        // ──────────────────────────────────────────────────────
        // Error Banner
        // ──────────────────────────────────────────────────────

        private void BuildErrorBanner()
        {
            m_ErrorBanner = new VisualElement();
            m_ErrorBanner.AddToClassList("git-error-banner");

            m_ErrorLabel = new Label();
            m_ErrorLabel.AddToClassList("git-error-label");
            m_ErrorBanner.Add(m_ErrorLabel);

            var closeBtn = new Button(() => m_ErrorBanner.style.display = DisplayStyle.None) { text = "✕" };
            closeBtn.AddToClassList("git-error-close-btn");
            m_ErrorBanner.Add(closeBtn);

            m_RootContainer.Add(m_ErrorBanner);
        }

        private void HandleGitError(string error)
        {
            if (string.IsNullOrWhiteSpace(error)) return;
            var info = GitErrorInterpreter.Interpret(error);

            // Banner: readable explanation first, raw git output below for details.
            m_ErrorLabel.text = info.Kind == GitErrorKind.Unknown ? error.Trim() : info.Message + "\n\n" + error.Trim();
            m_ErrorBanner.style.display = DisplayStyle.Flex;

            switch (info.Kind)
            {
                case GitErrorKind.NoUpstream when GitService.CurrentStatus.HasRemote:
                    ShowToast(info.Message, ToastType.Error, 6f, "Publish branch", () => _ = PublishCurrentBranch());
                    break;
                case GitErrorKind.PushRejected:
                    ShowToast(info.Message, ToastType.Error, 6f, "Pull", PullNow);
                    break;
                case GitErrorKind.UnresolvedConflicts:
                    ShowToast(info.Message, ToastType.Error, 6f, "Show conflicts", () => SwitchTab(GitTab.Changes));
                    break;
                default:
                    ShowToast(info.Message, ToastType.Error, info.Kind == GitErrorKind.Unknown ? 3f : 6f);
                    break;
            }
        }

        private async void PullNow()
        {
            SetLoading(true, "Pulling changes from upstream...");
            bool ok = await GitService.PullAsync();
            SetLoading(false);
            if (ok) ShowToast("Pull completed successfully.", ToastType.Success);
            UpdateTopBarState();
        }

        // ──────────────────────────────────────────────────────
        // Toast System
        // ──────────────────────────────────────────────────────

        private void BuildToastContainer()
        {
            m_ToastContainer = new VisualElement();
            m_ToastContainer.AddToClassList("git-toast-container");
            m_ToastContainer.pickingMode = PickingMode.Ignore;
            m_RootContainer.Add(m_ToastContainer);
        }

        private void ShowToast(string message, ToastType type = ToastType.Info, float durationSeconds = 3f,
            string actionLabel = null, Action action = null)
        {
            if (m_ToastContainer == null) return;

            var toast = new VisualElement();
            toast.AddToClassList("git-toast");
            toast.AddToClassList(type switch
            {
                ToastType.Success => "git-toast-success",
                ToastType.Error   => "git-toast-error",
                _                 => "git-toast-info"
            });

            var icon = type switch
            {
                ToastType.Success => "✓ ",
                ToastType.Error   => "✕ ",
                _                 => "ℹ ",
            };

            var lbl = new Label(icon + message);
            lbl.style.whiteSpace = WhiteSpace.Normal;
            toast.Add(lbl);

            if (!string.IsNullOrEmpty(actionLabel) && action != null)
            {
                var actionBtn = new Button(() =>
                {
                    toast.RemoveFromHierarchy();
                    action();
                }) { text = actionLabel };
                actionBtn.AddToClassList("git-toast-action");
                toast.Add(actionBtn);
            }
            m_ToastContainer.Add(toast);

            EditorApplication.CallbackFunction dismissCallback = null;
            double dismissAt = EditorApplication.timeSinceStartup + durationSeconds;

            dismissCallback = () =>
            {
                if (EditorApplication.timeSinceStartup >= dismissAt)
                {
                    EditorApplication.update -= dismissCallback;
                    if (toast?.parent != null) toast.RemoveFromHierarchy();
                }
            };
            EditorApplication.update += dismissCallback;
        }

        // ──────────────────────────────────────────────────────
        // Operation Banner (merge / rebase / cherry-pick / revert in progress)
        // ──────────────────────────────────────────────────────

        private VisualElement m_OperationBanner;
        private Label         m_OperationLabel;
        private Button        m_OperationContinueBtn;
        private Button        m_OperationSkipBtn;

        private void BuildOperationBanner()
        {
            m_OperationBanner = new VisualElement();
            m_OperationBanner.AddToClassList("git-operation-banner");
            m_OperationBanner.style.display = DisplayStyle.None;

            m_OperationLabel = new Label();
            m_OperationLabel.AddToClassList("git-operation-label");
            m_OperationBanner.Add(m_OperationLabel);

            m_OperationContinueBtn = new Button(() => RunOperationStep("--continue")) { text = "Continue" };
            m_OperationContinueBtn.AddToClassList("git-btn-primary-small");
            m_OperationBanner.Add(m_OperationContinueBtn);

            m_OperationSkipBtn = new Button(() => RunOperationStep("--skip")) { text = "Skip Commit", tooltip = "Drop the commit that stopped and continue with the next one" };
            m_OperationSkipBtn.AddToClassList("git-btn-secondary-small");
            m_OperationBanner.Add(m_OperationSkipBtn);

            var abortBtn = new Button(() => RunOperationStep("--abort")) { text = "Abort" };
            abortBtn.AddToClassList("git-btn-danger-small");
            m_OperationBanner.Add(abortBtn);

            m_RootContainer.Add(m_OperationBanner);
            UpdateOperationBanner(GitService.CurrentStatus);
        }

        private static string OperationName(GitRepoState state) => state switch
        {
            GitRepoState.Merging => "Merge",
            GitRepoState.Rebasing => "Rebase",
            GitRepoState.CherryPicking => "Cherry-pick",
            GitRepoState.Reverting => "Revert",
            _ => ""
        };

        private void UpdateOperationBanner(GitRepositoryStatus status)
        {
            if (m_OperationBanner == null) return;
            if (status.OperationState == GitRepoState.None)
            {
                m_OperationBanner.style.display = DisplayStyle.None;
                return;
            }

            int conflicts = status.ConflictedFiles.Count;
            string name = OperationName(status.OperationState);
            m_OperationLabel.text = conflicts > 0
                ? $"⚠ {name} in progress — {conflicts} conflicted file{(conflicts == 1 ? "" : "s")} to resolve"
                : $"⚠ {name} in progress — all conflicts resolved, continue to finish";

            m_OperationContinueBtn.SetEnabled(conflicts == 0);
            m_OperationContinueBtn.tooltip = conflicts > 0 ? "Resolve all conflicts first" : $"Finish the {name.ToLowerInvariant()} with git's default commit message";
            bool canSkip = status.OperationState == GitRepoState.Rebasing || status.OperationState == GitRepoState.CherryPicking;
            m_OperationSkipBtn.style.display = canSkip ? DisplayStyle.Flex : DisplayStyle.None;
            m_OperationBanner.style.display = DisplayStyle.Flex;
        }

        private async void RunOperationStep(string step)
        {
            var state = GitService.CurrentStatus.OperationState;
            string name = OperationName(state);

            if (step == "--abort" && !EditorUtility.DisplayDialog($"Abort {name}",
                    $"Abort the {name.ToLowerInvariant()} and return to the state before it started?\n\nConflict resolutions made so far will be lost.",
                    "Abort", "Cancel"))
                return;

            SetLoading(true, step == "--abort" ? $"Aborting {name.ToLowerInvariant()}..." : $"Continuing {name.ToLowerInvariant()}...");
            bool ok = step switch
            {
                "--abort" => await GitService.AbortOperationAsync(),
                "--skip" => await GitService.SkipOperationStepAsync(),
                _ => await GitService.ContinueOperationAsync()
            };
            SetLoading(false);

            var after = GitService.CurrentStatus;
            if (ok && after.OperationState == GitRepoState.None)
                ShowToast(step == "--abort" ? $"{name} aborted." : $"{name} completed.", step == "--abort" ? ToastType.Info : ToastType.Success);
            else if (after.ConflictedFiles.Count > 0)
                ShowToast($"The next commit has conflicts too. Resolve them and continue.", ToastType.Info, 5f);
            UpdateOperationBanner(after);
        }

        // ──────────────────────────────────────────────────────
        // Prompt Overlay (in-window text input dialog; Unity has no built-in one)
        // ──────────────────────────────────────────────────────

        private VisualElement m_PromptOverlay;

        private struct PromptField
        {
            public string Label;
            public string Value;
            public string Placeholder;

            public PromptField(string label, string value = "", string placeholder = "")
            {
                Label = label;
                Value = value;
                Placeholder = placeholder;
            }
        }

        /// <param name="validate">Returns an error message for the current values, or null when they are acceptable.</param>
        /// <param name="onConfirm">Runs the action; the prompt closes when it returns true.</param>
        private void ShowPrompt(string title, string message, PromptField[] fields, string confirmText,
            Func<string[], string> validate, Func<string[], Task<bool>> onConfirm)
        {
            m_PromptOverlay?.RemoveFromHierarchy();

            m_PromptOverlay = new VisualElement();
            m_PromptOverlay.AddToClassList("git-prompt-overlay");
            m_PromptOverlay.RegisterCallback<PointerDownEvent>(e =>
            {
                if (e.target == m_PromptOverlay) ClosePrompt();
            });

            var box = new VisualElement();
            box.AddToClassList("git-prompt-box");

            var titleLbl = new Label(title);
            titleLbl.AddToClassList("git-prompt-title");
            box.Add(titleLbl);

            if (!string.IsNullOrEmpty(message))
            {
                var msgLbl = new Label(message);
                msgLbl.AddToClassList("git-prompt-message");
                box.Add(msgLbl);
            }

            var inputs = new List<TextField>();
            var errorLbl = new Label();
            var confirmBtn = new Button { text = confirmText };

            string[] Values() => inputs.Select(i => i.value.Trim()).ToArray();
            void Revalidate()
            {
                string error = validate?.Invoke(Values());
                bool anyEmpty = inputs.Any(i => string.IsNullOrWhiteSpace(i.value));
                errorLbl.text = anyEmpty ? "" : error ?? "";
                errorLbl.style.display = string.IsNullOrEmpty(errorLbl.text) ? DisplayStyle.None : DisplayStyle.Flex;
                confirmBtn.SetEnabled(!anyEmpty && error == null);
            }

            async void Confirm()
            {
                if (!confirmBtn.enabledSelf) return;
                confirmBtn.SetEnabled(false);
                bool done = await onConfirm(Values());
                if (done) ClosePrompt();
                else Revalidate();
            }

            foreach (var field in fields)
            {
                var input = new TextField(field.Label) { value = field.Value ?? "" };
                input.AddToClassList("git-prompt-input");
                if (!string.IsNullOrEmpty(field.Placeholder)) input.textEdition.placeholder = field.Placeholder;
                input.RegisterValueChangedCallback(_ => Revalidate());
                input.RegisterCallback<KeyDownEvent>(e =>
                {
                    if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter) { e.StopPropagation(); Confirm(); }
                    else if (e.keyCode == KeyCode.Escape) { e.StopPropagation(); ClosePrompt(); }
                });
                inputs.Add(input);
                box.Add(input);
            }

            errorLbl.AddToClassList("git-prompt-error");
            box.Add(errorLbl);

            var actions = new VisualElement();
            actions.AddToClassList("git-popover-drawer-actions");
            var cancelBtn = new Button(ClosePrompt) { text = "Cancel" };
            cancelBtn.AddToClassList("git-btn-secondary-small");
            actions.Add(cancelBtn);
            confirmBtn.clicked += Confirm;
            confirmBtn.AddToClassList("git-btn-primary-small");
            actions.Add(confirmBtn);
            box.Add(actions);

            m_PromptOverlay.Add(box);
            m_RootContainer.Add(m_PromptOverlay);
            Revalidate();
            inputs.FirstOrDefault()?.schedule.Execute(() => inputs[0].Focus());
        }

        private void ClosePrompt()
        {
            m_PromptOverlay?.RemoveFromHierarchy();
            m_PromptOverlay = null;
        }

        // ──────────────────────────────────────────────────────
        // Loading Overlay
        // ──────────────────────────────────────────────────────

        private void BuildLoadingOverlay()
        {
            m_LoadingOverlay = new VisualElement();
            m_LoadingOverlay.AddToClassList("git-loading-overlay");
            m_LoadingOverlay.pickingMode = PickingMode.Ignore;

            var box = new VisualElement();
            box.AddToClassList("git-loading-box");

            var spinner = new Image();
            spinner.image = EditorGUIUtility.IconContent("d_WaitSpin00").image;
            spinner.style.width  = 24;
            spinner.style.height = 24;
            box.Add(spinner);

            m_LoadingLabel = new Label("Processing...");
            m_LoadingLabel.AddToClassList("git-loading-label");
            box.Add(m_LoadingLabel);

            m_LoadingOverlay.Add(box);
            m_RootContainer.Add(m_LoadingOverlay);
        }

        private void SetLoading(bool loading, string message = "Processing...")
        {
            m_IsLoading = loading;
            if (m_LoadingOverlay == null) return;
            m_LoadingOverlay.style.display = loading ? DisplayStyle.Flex : DisplayStyle.None;
            m_LoadingOverlay.pickingMode   = loading ? PickingMode.Position : PickingMode.Ignore;
            if (m_LoadingLabel != null) m_LoadingLabel.text = message;
        }

        // ──────────────────────────────────────────────────────
        // No Repository UI
        // ──────────────────────────────────────────────────────

        private void BuildNoRepositoryUI()
        {
            var container = new VisualElement();
            container.AddToClassList("git-no-repo-container");

            var icon = new Image();
            icon.image = EditorGUIUtility.IconContent("d_Collab").image;
            icon.AddToClassList("git-no-repo-icon");
            container.Add(icon);

            var title = new Label("No Git Repository Found");
            title.AddToClassList("git-no-repo-title");
            container.Add(title);

            var sub = new Label(
                $"'{Application.productName}' is not currently managed with Git.\n" +
                "You can initialize a new repository with a standard Unity .gitignore in one click.");
            sub.AddToClassList("git-no-repo-subtitle");
            container.Add(sub);

            var initBtn = new Button(async () =>
            {
                SetLoading(true, "Initializing repository...");
                bool ok = await GitService.InitRepositoryAsync(true);
                SetLoading(false);
                if (ok)
                {
                    ShowToast("Git repository initialized successfully.", ToastType.Success);
                    CreateGUI();
                }
                else
                {
                    ShowToast("Failed to initialize repository.", ToastType.Error);
                }
            }) { text = "Initialize Git Repository" };
            initBtn.AddToClassList("git-btn-init-repo");
            container.Add(initBtn);

            m_RootContainer.Add(container);
        }

        // ──────────────────────────────────────────────────────
        // Top Bar (GitHub Desktop 3-Segment Toolbar)
        // ──────────────────────────────────────────────────────

        private void BuildTopBar()
        {
            m_TopBar = new VisualElement();
            m_TopBar.AddToClassList("git-top-bar");

            var left = new VisualElement();
            left.AddToClassList("git-topbar-left");

            // Segment 1: Current Repository
            m_RepoSegment = new VisualElement();
            m_RepoSegment.AddToClassList("git-topbar-segment");
            m_RepoSegment.tooltip = "Repository options";

            var repoCaption = new Label("CURRENT REPOSITORY");
            repoCaption.AddToClassList("git-topbar-segment-caption");
            m_RepoSegment.Add(repoCaption);

            var repoRow = new VisualElement();
            repoRow.AddToClassList("git-topbar-segment-row");

            var repoIcon = new Label("📁");
            repoIcon.AddToClassList("git-topbar-segment-icon");
            repoRow.Add(repoIcon);

            m_RepoTitleLabel = new Label(GitService.CurrentStatus.RepositoryName);
            m_RepoTitleLabel.AddToClassList("git-topbar-segment-title");
            repoRow.Add(m_RepoTitleLabel);
            m_RepoSegment.Add(repoRow);

            m_RepoSegment.RegisterCallback<PointerDownEvent>(e =>
            {
                if (e.button == 0) OpenRepositoryMenu();
            });
            left.Add(m_RepoSegment);

            // Segment 2: Current Branch (Click toggles GitHub Desktop popover)
            m_BranchSegment = new VisualElement();
            m_BranchSegment.AddToClassList("git-topbar-segment");
            m_BranchSegment.tooltip = "Click to switch branch, filter, create new branch or view Pull Requests";

            var branchCaption = new Label("CURRENT BRANCH");
            branchCaption.AddToClassList("git-topbar-segment-caption");
            m_BranchSegment.Add(branchCaption);

            var branchRow = new VisualElement();
            branchRow.AddToClassList("git-topbar-segment-row");

            var branchIcon = new Label("⎇");
            branchIcon.AddToClassList("git-topbar-segment-icon");
            branchRow.Add(branchIcon);

            m_BranchTitleLabel = new Label(GitService.CurrentBranch);
            m_BranchTitleLabel.AddToClassList("git-topbar-segment-title");
            ApplyMonoFont(m_BranchTitleLabel, bold: true);
            branchRow.Add(m_BranchTitleLabel);

            m_BranchTrackingLabel = new Label("");
            m_BranchTrackingLabel.style.fontSize = 10;
            m_BranchTrackingLabel.style.marginLeft = 4;
            ApplyMonoFont(m_BranchTrackingLabel);
            branchRow.Add(m_BranchTrackingLabel);

            var branchArrow = new Label("▾");
            branchArrow.AddToClassList("git-topbar-segment-arrow");
            branchRow.Add(branchArrow);
            m_BranchSegment.Add(branchRow);

            m_BranchSegment.RegisterCallback<PointerDownEvent>(e =>
            {
                if (e.button == 0) ToggleBranchPopover();
            });
            left.Add(m_BranchSegment);

            m_TopBar.Add(left);

            // Segment 3: Sync Action Button (Pull / Push / Fetch)
            var right = new VisualElement();
            right.style.flexDirection = FlexDirection.Row;
            right.style.alignItems = Align.Center;

            m_SyncButton = new Button(OnSyncButtonClicked);
            m_SyncButton.AddToClassList("git-topbar-sync-btn");

            m_SyncTitleLabel = new Label("Fetch origin");
            m_SyncTitleLabel.AddToClassList("git-topbar-sync-title");
            m_SyncButton.Add(m_SyncTitleLabel);

            m_SyncSubtitleLabel = new Label("never");
            m_SyncSubtitleLabel.AddToClassList("git-topbar-sync-subtitle");
            m_SyncButton.Add(m_SyncSubtitleLabel);
            right.Add(m_SyncButton);

            m_BtnRefresh = new Button(async () =>
            {
                SetLoading(true, "Refreshing status...");
                await GitService.RefreshStatusAsync();
                SetLoading(false);
            });
            m_BtnRefresh.AddToClassList("git-toolbar-btn-icon");
            m_BtnRefresh.tooltip = "Refresh Git Status (F5 / Ctrl+R)";
            var refreshImg = new Image();
            refreshImg.image = EditorGUIUtility.IconContent("Refresh").image;
            refreshImg.style.width  = 14;
            refreshImg.style.height = 14;
            m_BtnRefresh.Add(refreshImg);
            right.Add(m_BtnRefresh);

            m_TopBar.Add(right);
            m_RootContainer.Add(m_TopBar);

            UpdateTopBarState();
        }

        private void UpdateTopBarState()
        {
            var st = GitService.CurrentStatus;
            if (m_RepoTitleLabel != null) m_RepoTitleLabel.text = st.RepositoryName;
            if (m_BranchTitleLabel != null) m_BranchTitleLabel.text = st.CurrentBranch;

            if (m_BranchTrackingLabel != null)
            {
                if (st.IsDetached)
                {
                    m_BranchTrackingLabel.text = "no branch";
                    m_BranchTrackingLabel.style.color = new Color(0.9f, 0.75f, 0.45f);
                    m_BranchTrackingLabel.style.display = DisplayStyle.Flex;
                }
                else if (st.CanPublishBranch)
                {
                    m_BranchTrackingLabel.text = st.UpstreamGone ? "upstream gone" : "not published";
                    m_BranchTrackingLabel.style.color = new Color(0.55f, 0.55f, 0.6f);
                    m_BranchTrackingLabel.style.display = DisplayStyle.Flex;
                }
                else if (st.AheadBy > 0 && st.BehindBy > 0)
                {
                    m_BranchTrackingLabel.text = $"↑{st.AheadBy} ↓{st.BehindBy}";
                    m_BranchTrackingLabel.style.color = new Color(0.9f, 0.75f, 0.45f);
                    m_BranchTrackingLabel.style.display = DisplayStyle.Flex;
                }
                else if (st.AheadBy > 0)
                {
                    m_BranchTrackingLabel.text = $"↑{st.AheadBy}";
                    m_BranchTrackingLabel.style.color = new Color(0.35f, 0.8f, 0.5f);
                    m_BranchTrackingLabel.style.display = DisplayStyle.Flex;
                }
                else if (st.BehindBy > 0)
                {
                    m_BranchTrackingLabel.text = $"↓{st.BehindBy}";
                    m_BranchTrackingLabel.style.color = new Color(0.9f, 0.5f, 0.5f);
                    m_BranchTrackingLabel.style.display = DisplayStyle.Flex;
                }
                else
                {
                    m_BranchTrackingLabel.style.display = DisplayStyle.None;
                }
            }

            if (m_SyncButton != null)
            {
                m_SyncButton.RemoveFromClassList("git-topbar-sync-btn-highlight");
                m_SyncButton.SetEnabled(true);
                m_SyncButton.tooltip = "";
                string remote = st.HasRemote ? st.DefaultRemote : "origin";

                switch (GetSyncAction(st))
                {
                    case SyncAction.NoRemote:
                        m_SyncTitleLabel.text = "Publish repository";
                        m_SyncSubtitleLabel.text = "No remote configured";
                        m_SyncButton.tooltip = "Connect this repository to a remote (e.g. an empty GitHub repo) and push the current branch.";
                        m_SyncButton.AddToClassList("git-topbar-sync-btn-highlight");
                        break;
                    case SyncAction.PublishBranch:
                        m_SyncTitleLabel.text = "Publish branch";
                        m_SyncSubtitleLabel.text = $"Publish this branch to {remote}";
                        m_SyncButton.tooltip = $"Push '{st.CurrentBranch}' to {remote} and set it as the upstream (git push -u)";
                        m_SyncButton.AddToClassList("git-topbar-sync-btn-highlight");
                        break;
                    case SyncAction.Pull:
                        m_SyncTitleLabel.text = $"Pull {remote} (↓{st.BehindBy})";
                        m_SyncSubtitleLabel.text = st.AheadBy > 0 ? $"Diverged · ↑{st.AheadBy} local" : "New changes available";
                        m_SyncButton.AddToClassList("git-topbar-sync-btn-highlight");
                        break;
                    case SyncAction.Push:
                        m_SyncTitleLabel.text = $"Push {remote} (↑{st.AheadBy})";
                        m_SyncSubtitleLabel.text = "Local commits to push";
                        m_SyncButton.AddToClassList("git-topbar-sync-btn-highlight");
                        break;
                    default:
                        m_SyncTitleLabel.text = $"Fetch {remote}";
                        m_SyncSubtitleLabel.text = $"Last: {GetRelativeFetchTime()}";
                        break;
                }
            }

            UpdateButtonLabels();
        }

        private enum SyncAction { NoRemote, PublishBranch, Pull, Push, Fetch }

        private static SyncAction GetSyncAction(GitRepositoryStatus st)
        {
            if (!st.HasRemote) return SyncAction.NoRemote;
            if (st.CanPublishBranch) return SyncAction.PublishBranch;
            if (st.BehindBy > 0) return SyncAction.Pull;
            if (st.AheadBy > 0) return SyncAction.Push;
            return SyncAction.Fetch;
        }

        private static string GetRelativeFetchTime()
        {
            if (GitService.LastFetchTime == DateTime.MinValue) return "never";
            var diff = DateTime.Now - GitService.LastFetchTime;
            if (diff.TotalSeconds < 60) return "just now";
            if (diff.TotalMinutes < 60) return $"{Mathf.Max(1, (int)diff.TotalMinutes)}m ago";
            if (diff.TotalHours < 24) return $"{(int)diff.TotalHours}h ago";
            return GitService.LastFetchTime.ToString("MMM d");
        }

        private async void OnSyncButtonClicked()
        {
            var st = GitService.CurrentStatus;
            switch (GetSyncAction(st))
            {
                case SyncAction.NoRemote:
                    ShowPublishRepositoryPrompt();
                    return;
                case SyncAction.PublishBranch:
                    await PublishCurrentBranch();
                    break;
                case SyncAction.Pull:
                {
                    SetLoading(true, "Pulling changes from upstream...");
                    bool ok = await GitService.PullAsync();
                    SetLoading(false);
                    if (ok) ShowToast("Pull completed successfully.", ToastType.Success);
                    break;
                }
                case SyncAction.Push:
                {
                    SetLoading(true, "Pushing commits to remote...");
                    bool ok = await GitService.PushAsync();
                    SetLoading(false);
                    if (ok) ShowToast("Push completed successfully.", ToastType.Success);
                    break;
                }
                default:
                {
                    SetLoading(true, "Fetching from remote...");
                    bool ok = await GitService.FetchAsync();
                    SetLoading(false);
                    if (ok) ShowToast("Fetch completed.", ToastType.Success);
                    break;
                }
            }
            UpdateTopBarState();
        }

        private async Task<bool> PublishCurrentBranch()
        {
            var st = GitService.CurrentStatus;
            string branch = st.CurrentBranch;
            string remote = st.HasRemote ? st.DefaultRemote : "origin";
            SetLoading(true, $"Publishing '{branch}' to {remote}...");
            bool ok = await GitService.PublishBranchAsync();
            SetLoading(false);
            if (ok) ShowToast($"Branch '{branch}' published to {remote}.", ToastType.Success);
            UpdateTopBarState();
            return ok;
        }

        // ── Remotes ──────────────────────────────────────────

        private static string ValidateRemoteName(string name, IEnumerable<GitRemoteInfo> existing, string except = null)
        {
            if (string.IsNullOrWhiteSpace(name)) return "Enter a name.";
            if (name != except && existing.Any(r => r.Name == name)) return $"A remote named '{name}' already exists.";
            // Remote names follow the same ref-name rules and may not contain '/' in practice.
            return name.Contains("/") ? "Can't contain '/'." : GitService.ValidateBranchName(name);
        }

        private static string ValidateRemoteUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return "Enter a URL.";
            if (url.Any(char.IsWhiteSpace)) return "URL can't contain spaces.";
            bool looksValid = url.Contains("://") || url.Contains("@") || Directory.Exists(url);
            return looksValid ? null : "Use an https://, ssh:// or git@host:owner/repo URL.";
        }

        private void ShowPublishRepositoryPrompt()
        {
            string branch = GitService.CurrentBranch;
            ShowPrompt(
                "Publish repository",
                $"Create an empty repository on GitHub (or another host) without a README, paste its URL here, and '{branch}' will be pushed to it as origin.",
                new[] { new PromptField("URL", "", "https://github.com/owner/repo.git") },
                "Publish",
                v => ValidateRemoteUrl(v[0]),
                async v =>
                {
                    SetLoading(true, "Publishing repository...");
                    bool ok = await GitService.PublishRepositoryAsync(v[0]);
                    SetLoading(false);
                    if (ok) ShowToast($"Published '{branch}' to origin.", ToastType.Success);
                    UpdateTopBarState();
                    // The remote may have been added even if the push failed; either way the prompt has done its part.
                    return ok || GitService.CurrentStatus.HasRemote;
                });
        }

        private void ShowAddRemotePrompt(List<GitRemoteInfo> remotes)
        {
            ShowPrompt(
                "Add remote",
                null,
                new[] { new PromptField("Name", remotes.Count == 0 ? "origin" : "", "upstream"), new PromptField("URL", "", "https://github.com/owner/repo.git") },
                "Add",
                v => ValidateRemoteName(v[0], remotes) ?? ValidateRemoteUrl(v[1]),
                async v =>
                {
                    bool ok = await GitService.AddRemoteAsync(v[0], v[1]);
                    if (ok) ShowToast($"Remote '{v[0]}' added.", ToastType.Success);
                    UpdateTopBarState();
                    return ok;
                });
        }

        private void AddRemoteMenuItems(GenericMenu menu, List<GitRemoteInfo> remotes)
        {
            foreach (var remote in remotes)
            {
                var r = remote;
                string prefix = $"Remotes/{r.Name}  ({TruncateMiddle(r.FetchUrl, 48)})/";
                menu.AddItem(new GUIContent(prefix + "Fetch"), false, async () =>
                {
                    SetLoading(true, $"Fetching {r.Name}...");
                    bool ok = await GitService.FetchAsync(r.Name);
                    SetLoading(false);
                    if (ok) ShowToast($"Fetched {r.Name}.", ToastType.Success);
                    UpdateTopBarState();
                });
                menu.AddItem(new GUIContent(prefix + "Copy URL"), false, () =>
                {
                    EditorGUIUtility.systemCopyBuffer = r.FetchUrl;
                    ShowToast("Remote URL copied.", ToastType.Info);
                });
                menu.AddItem(new GUIContent(prefix + "Change URL..."), false, () => ShowPrompt(
                    $"Change URL of '{r.Name}'", null,
                    new[] { new PromptField("URL", r.FetchUrl) }, "Save",
                    v => v[0] == r.FetchUrl ? "Enter a different URL." : ValidateRemoteUrl(v[0]),
                    async v =>
                    {
                        bool ok = await GitService.SetRemoteUrlAsync(r.Name, v[0]);
                        if (ok) ShowToast($"Updated URL of '{r.Name}'.", ToastType.Success);
                        return ok;
                    }));
                menu.AddItem(new GUIContent(prefix + "Rename..."), false, () => ShowPrompt(
                    $"Rename remote '{r.Name}'", "Remote-tracking branches and upstream settings are renamed too.",
                    new[] { new PromptField("Name", r.Name) }, "Rename",
                    v => v[0] == r.Name ? "Enter a different name." : ValidateRemoteName(v[0], remotes, r.Name),
                    async v =>
                    {
                        bool ok = await GitService.RenameRemoteAsync(r.Name, v[0]);
                        if (ok) ShowToast($"Renamed '{r.Name}' to '{v[0]}'.", ToastType.Success);
                        UpdateTopBarState();
                        return ok;
                    }));
                menu.AddItem(new GUIContent(prefix + "Remove..."), false, async () =>
                {
                    if (!EditorUtility.DisplayDialog("Remove Remote",
                            $"Remove '{r.Name}' ({r.FetchUrl})?\n\nNothing is deleted on the server; branches tracking it become unpublished.",
                            "Remove", "Cancel"))
                        return;
                    bool ok = await GitService.RemoveRemoteAsync(r.Name);
                    if (ok) ShowToast($"Remote '{r.Name}' removed.", ToastType.Info);
                    UpdateTopBarState();
                });
            }

            if (remotes.Count > 0) menu.AddSeparator("Remotes/");
            menu.AddItem(new GUIContent("Remotes/Add Remote..."), false, () => ShowAddRemotePrompt(remotes));
        }

        private static string TruncateMiddle(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            if (s.Length > max)
            {
                int half = (max - 1) / 2;
                s = s.Substring(0, half) + "…" + s.Substring(s.Length - half);
            }
            return MenuText(s);
        }

        /// <summary>GenericMenu treats '/' as a submenu separator; branch names and URLs get a look-alike slash (U+2215).</summary>
        private static string MenuText(string s) => (s ?? "").Replace('/', '∕');

        // Remotes are cached so the repository menu can open synchronously inside the click
        // (GenericMenu.ShowAsContext needs the current IMGUI event, which is gone after an await).
        private List<GitRemoteInfo> m_CachedRemotes = new List<GitRemoteInfo>();
        private DateTime m_CachedRemotesConfigTime = DateTime.MinValue;

        private async Task RefreshRemoteCacheAsync()
        {
            m_CachedRemotesConfigTime = GetGitConfigWriteTime();
            m_CachedRemotes = await GitService.GetRemotesAsync();
            string defaultRemote = GitService.CurrentStatus.DefaultRemote;
            var main = m_CachedRemotes.FirstOrDefault(r => r.Name == defaultRemote) ?? m_CachedRemotes.FirstOrDefault();
            m_CachedGitHubInfo = GitService.ParseGitHubUrl(main?.FetchUrl);
        }

        private static DateTime GetGitConfigWriteTime()
        {
            string gitDir = GitService.ResolveGitDir(GitService.RepositoryRoot);
            string config = gitDir != null ? Path.Combine(gitDir, "config") : null;
            return config != null && File.Exists(config) ? File.GetLastWriteTimeUtc(config) : DateTime.MinValue;
        }

        private void OpenRepositoryMenu()
        {
            var remotes = m_CachedRemotes;
            var gh = m_CachedGitHubInfo;
            var menu = new GenericMenu();
            menu.AddItem(new GUIContent("Reveal in File Explorer"), false, () =>
            {
                if (Directory.Exists(GitService.RepositoryRoot))
                    EditorUtility.RevealInFinder(GitService.RepositoryRoot);
            });

            var gitSettings = CodeIDESettings.Current.Git;
            void AddPullStrategy(string label, GitPullStrategy strategy)
            {
                menu.AddItem(new GUIContent($"Pull Strategy/{label}"), gitSettings.PullStrategy == strategy, () =>
                {
                    gitSettings.PullStrategy = strategy;
                    CodeIDESettings.Save();
                });
            }
            AddPullStrategy("Merge", GitPullStrategy.Merge);
            AddPullStrategy("Rebase", GitPullStrategy.Rebase);
            AddPullStrategy("Fast-forward only", GitPullStrategy.FastForwardOnly);

            void AddAutoFetch(string label, int minutes)
            {
                menu.AddItem(new GUIContent($"Auto Fetch/{label}"), gitSettings.AutoFetchMinutes == minutes, () =>
                {
                    gitSettings.AutoFetchMinutes = minutes;
                    CodeIDESettings.Save();
                });
            }
            AddAutoFetch("Off", 0);
            AddAutoFetch("Every 5 minutes", 5);
            AddAutoFetch("Every 15 minutes", 15);
            AddAutoFetch("Every 30 minutes", 30);

            menu.AddSeparator("");
            AddRemoteMenuItems(menu, remotes);

            if (gh.isGitHub && !string.IsNullOrEmpty(gh.webUrl))
            {
                menu.AddSeparator("");
                menu.AddItem(new GUIContent($"View on GitHub ({MenuText(gh.ownerRepo)})"), false, () =>
                {
                    Application.OpenURL(gh.webUrl);
                });
            }
            menu.ShowAsContext();
        }

        // ──────────────────────────────────────────────────────
        // Branch Popover (GitHub Desktop Dropdown Dialog)
        // ──────────────────────────────────────────────────────

        private void BuildBranchPopover()
        {
            m_PopoverOverlay = new VisualElement();
            m_PopoverOverlay.AddToClassList("git-popover-overlay");
            m_PopoverOverlay.style.display = DisplayStyle.None;

            // Click outside dismisses popover
            m_PopoverOverlay.RegisterCallback<PointerDownEvent>(e =>
            {
                if (e.target == m_PopoverOverlay)
                    ToggleBranchPopover(false);
            });

            m_BranchPopover = new VisualElement();
            m_BranchPopover.AddToClassList("git-branch-popover");

            // Popover Tabs: [ Branches | Pull Requests ]
            var tabs = new VisualElement();
            tabs.AddToClassList("git-popover-tabs");

            m_PopoverTabBranches = new Button(() => SwitchPopoverTab(PopoverTab.Branches)) { text = "Branches" };
            m_PopoverTabBranches.AddToClassList("git-popover-tab");
            m_PopoverTabBranches.AddToClassList("git-popover-tab-active");
            tabs.Add(m_PopoverTabBranches);

            m_PopoverTabPRs = new Button(() => SwitchPopoverTab(PopoverTab.PullRequests)) { text = "Pull Requests" };
            m_PopoverTabPRs.AddToClassList("git-popover-tab");
            tabs.Add(m_PopoverTabPRs);

            m_BranchPopover.Add(tabs);

            // Popover Toolbar (Search + New Branch button)
            var toolbar = new VisualElement();
            toolbar.name = "popoverToolbar";
            toolbar.AddToClassList("git-popover-toolbar");

            var searchBox = new VisualElement();
            searchBox.AddToClassList("git-popover-search-box");

            var searchIcon = new Label("🔍");
            searchIcon.style.fontSize = 11;
            searchIcon.style.color = new Color(0.5f, 0.5f, 0.5f);
            searchBox.Add(searchIcon);

            m_PopoverSearchField = new TextField { label = "" };
            m_PopoverSearchField.AddToClassList("git-popover-search-input");
            m_PopoverSearchField.value = m_PopoverSearchQuery;
            m_PopoverSearchField.tooltip = "Filter branches...";
            m_PopoverSearchField.RegisterValueChangedCallback(e =>
            {
                m_PopoverSearchQuery = e.newValue;
                RenderPopoverBranches();
            });
            searchBox.Add(m_PopoverSearchField);
            toolbar.Add(searchBox);

            var newBranchBtn = new Button(ToggleNewBranchDrawer) { text = "+ New Branch" };
            newBranchBtn.AddToClassList("git-popover-new-btn");
            toolbar.Add(newBranchBtn);
            m_BranchPopover.Add(toolbar);

            // Inline New Branch Drawer (hidden by default)
            m_PopoverNewDrawer = new VisualElement();
            m_PopoverNewDrawer.AddToClassList("git-popover-new-drawer");
            m_PopoverNewDrawer.style.display = DisplayStyle.None;

            m_PopoverDrawerTitle = new Label("Create a new branch");
            m_PopoverDrawerTitle.AddToClassList("git-popover-drawer-title");
            m_PopoverNewDrawer.Add(m_PopoverDrawerTitle);

            m_PopoverNewBranchInput = new TextField { label = "" };
            m_PopoverNewBranchInput.AddToClassList("git-popover-drawer-input");
            m_PopoverNewDrawer.Add(m_PopoverNewBranchInput);

            m_PopoverNewBranchInput.RegisterCallback<KeyDownEvent>(e =>
            {
                if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter)
                {
                    e.StopPropagation();
                    OnConfirmBranchDrawer();
                }
                else if (e.keyCode == KeyCode.Escape)
                {
                    e.StopPropagation();
                    m_PopoverNewDrawer.style.display = DisplayStyle.None;
                }
            });
            m_PopoverNewBranchInput.RegisterValueChangedCallback(_ => ValidateBranchDrawerInput());

            m_PopoverNewBranchHint = new Label("");
            m_PopoverNewBranchHint.AddToClassList("git-popover-drawer-hint");
            m_PopoverNewDrawer.Add(m_PopoverNewBranchHint);

            m_PopoverBaseField = new PopupField<string>("Based on", new List<string> { "HEAD" }, 0);
            m_PopoverBaseField.AddToClassList("git-popover-drawer-base");
            m_PopoverNewDrawer.Add(m_PopoverBaseField);

            m_PopoverPublishToggle = new Toggle("Publish to remote after creating");
            m_PopoverPublishToggle.AddToClassList("git-popover-drawer-toggle");
            m_PopoverPublishToggle.value = EditorPrefs.GetBool(PublishAfterCreatePrefKey, false);
            m_PopoverPublishToggle.RegisterValueChangedCallback(e => EditorPrefs.SetBool(PublishAfterCreatePrefKey, e.newValue));
            m_PopoverNewDrawer.Add(m_PopoverPublishToggle);

            var drawerActions = new VisualElement();
            drawerActions.AddToClassList("git-popover-drawer-actions");

            var cancelBtn = new Button(() => m_PopoverNewDrawer.style.display = DisplayStyle.None) { text = "Cancel" };
            cancelBtn.AddToClassList("git-btn-secondary-small");
            drawerActions.Add(cancelBtn);

            m_PopoverDrawerConfirmBtn = new Button(OnConfirmBranchDrawer) { text = "Create Branch" };
            m_PopoverDrawerConfirmBtn.AddToClassList("git-btn-primary-small");
            drawerActions.Add(m_PopoverDrawerConfirmBtn);

            m_PopoverNewDrawer.Add(drawerActions);
            m_BranchPopover.Add(m_PopoverNewDrawer);

            // Scrollable Content
            m_PopoverContentScroll = new ScrollView();
            m_PopoverContentScroll.style.flexGrow = 1;
            m_BranchPopover.Add(m_PopoverContentScroll);

            m_PopoverOverlay.Add(m_BranchPopover);
            m_RootContainer.Add(m_PopoverOverlay);
        }

        private async void ToggleBranchPopover(bool? force = null)
        {
            if (m_PopoverOverlay == null) return;
            bool show = force ?? (m_PopoverOverlay.style.display != DisplayStyle.Flex);

            if (show)
            {
                m_BranchSegment?.AddToClassList("git-topbar-segment-active");
                m_PopoverOverlay.style.display = DisplayStyle.Flex;
                m_PopoverNewDrawer.style.display = DisplayStyle.None;

                // Refresh data
                m_DefaultBranch = await GitService.GetDefaultBranchAsync();
                m_CachedGitHubInfo = await GitService.GetGitHubRepositoryInfoAsync();
                m_CachedPopoverBranches = await GitService.GetBranchesAsync();
                RefreshBaseChoices();

                if (m_ActivePopoverTab == PopoverTab.Branches)
                {
                    RenderPopoverBranches();
                    m_PopoverSearchField?.Focus();
                }
                else
                {
                    RenderPopoverPullRequests();
                }
            }
            else
            {
                m_BranchSegment?.RemoveFromClassList("git-topbar-segment-active");
                m_PopoverOverlay.style.display = DisplayStyle.None;
            }
        }

        private void SwitchPopoverTab(PopoverTab tab)
        {
            m_ActivePopoverTab = tab;
            m_PopoverTabBranches?.RemoveFromClassList("git-popover-tab-active");
            m_PopoverTabPRs?.RemoveFromClassList("git-popover-tab-active");

            var toolbar = m_BranchPopover.Q<VisualElement>("popoverToolbar");

            if (tab == PopoverTab.Branches)
            {
                m_PopoverTabBranches?.AddToClassList("git-popover-tab-active");
                if (toolbar != null) toolbar.style.display = DisplayStyle.Flex;
                RenderPopoverBranches();
            }
            else
            {
                m_PopoverTabPRs?.AddToClassList("git-popover-tab-active");
                if (toolbar != null) toolbar.style.display = DisplayStyle.None;
                m_PopoverNewDrawer.style.display = DisplayStyle.None;
                RenderPopoverPullRequests();
            }
        }

        private void ToggleNewBranchDrawer()
        {
            if (m_PopoverNewDrawer == null) return;
            bool isVisible = m_PopoverNewDrawer.style.display == DisplayStyle.Flex;
            if (isVisible && m_RenameSourceBranch == null)
            {
                m_PopoverNewDrawer.style.display = DisplayStyle.None;
                return;
            }
            OpenBranchDrawer(renameSource: null);
        }

        /// <summary>Shows the inline drawer in "create" mode (<paramref name="renameSource"/> null) or "rename" mode.</summary>
        private void OpenBranchDrawer(string renameSource, string startPoint = null)
        {
            if (m_PopoverNewDrawer == null) return;
            m_RenameSourceBranch = renameSource;
            bool rename = renameSource != null;
            var st = GitService.CurrentStatus;

            m_PopoverDrawerTitle.text = rename ? $"Rename branch '{renameSource}'" : "Create a new branch";
            m_PopoverDrawerConfirmBtn.text = rename ? "Rename" : "Create Branch";
            m_PopoverBaseField.style.display = rename ? DisplayStyle.None : DisplayStyle.Flex;
            m_PopoverPublishToggle.style.display = !rename && st.HasRemote ? DisplayStyle.Flex : DisplayStyle.None;
            if (st.HasRemote) m_PopoverPublishToggle.label = $"Publish to {st.DefaultRemote} after creating";

            if (!rename)
            {
                RefreshBaseChoices();
                if (startPoint != null && m_PopoverBaseField.choices.Contains(startPoint))
                    m_PopoverBaseField.value = startPoint;
            }

            m_PopoverNewDrawer.style.display = DisplayStyle.Flex;
            m_PopoverNewBranchInput.SetValueWithoutNotify(rename ? renameSource : "");
            ValidateBranchDrawerInput();
            m_PopoverNewBranchInput.Focus();
        }

        /// <summary>"Based on" choices: the current branch (or HEAD when detached) first, then the default branch, other local branches and remote branches.</summary>
        private void RefreshBaseChoices()
        {
            if (m_PopoverBaseField == null) return;
            var st = GitService.CurrentStatus;
            string head = st.IsDetached || string.IsNullOrEmpty(st.CurrentBranch) ? "HEAD" : st.CurrentBranch;

            var choices = new List<string> { head };
            var locals = m_CachedPopoverBranches.Where(b => !b.IsRemote && b.Name != head).Select(b => b.Name).ToList();
            if (locals.Remove(m_DefaultBranch)) choices.Add(m_DefaultBranch);
            choices.AddRange(locals);
            choices.AddRange(m_CachedPopoverBranches.Where(b => b.IsRemote).Select(b => b.Name));

            string previous = m_PopoverBaseField.value;
            m_PopoverBaseField.choices = choices;
            m_PopoverBaseField.SetValueWithoutNotify(choices.Contains(previous) && previous != "HEAD" ? previous : head);
        }

        private bool ValidateBranchDrawerInput()
        {
            string name = m_PopoverNewBranchInput?.value?.Trim() ?? "";
            string error = GitService.ValidateBranchName(name);
            if (error == null && name != m_RenameSourceBranch &&
                m_CachedPopoverBranches.Any(b => !b.IsRemote && string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                error = $"A branch named '{name}' already exists.";
            }

            bool empty = string.IsNullOrEmpty(name);
            m_PopoverNewBranchHint.text = error != null && !empty ? error : (m_RenameSourceBranch != null ? "Enter a new name" : "");
            m_PopoverNewBranchHint.EnableInClassList("git-popover-drawer-hint-error", error != null && !empty);
            m_PopoverNewBranchHint.style.display = string.IsNullOrEmpty(m_PopoverNewBranchHint.text) ? DisplayStyle.None : DisplayStyle.Flex;
            bool valid = error == null && name != m_RenameSourceBranch;
            m_PopoverDrawerConfirmBtn?.SetEnabled(valid);
            return valid;
        }

        private void OnConfirmBranchDrawer()
        {
            if (!ValidateBranchDrawerInput()) return;
            if (m_RenameSourceBranch != null) OnRenameBranchFromPopover();
            else OnCreateBranchFromPopover();
        }

        private async void OnRenameBranchFromPopover()
        {
            string from = m_RenameSourceBranch;
            string to = m_PopoverNewBranchInput.value.Trim();
            var branch = m_CachedPopoverBranches.FirstOrDefault(b => !b.IsRemote && b.Name == from);

            SetLoading(true, $"Renaming '{from}'...");
            bool ok = await GitService.RenameBranchAsync(from, to);
            SetLoading(false);
            if (!ok) return;

            m_PopoverNewDrawer.style.display = DisplayStyle.None;
            m_RenameSourceBranch = null;
            ShowToast(branch != null && !string.IsNullOrEmpty(branch.Upstream) && !branch.IsGone
                    ? $"Renamed to '{to}'. Publish it to create '{to}' on the remote; '{branch.Upstream}' is left as is."
                    : $"Branch renamed to '{to}'.",
                ToastType.Success, 5f);
            await ReloadPopoverBranches();
            UpdateTopBarState();
        }

        private async Task ReloadPopoverBranches()
        {
            m_CachedPopoverBranches = await GitService.GetBranchesAsync();
            RefreshBaseChoices();
            RenderPopoverBranches();
        }

        private async void OnCreateBranchFromPopover()
        {
            string name = m_PopoverNewBranchInput?.value?.Trim();
            if (string.IsNullOrWhiteSpace(name)) return;

            // The first choice is the current HEAD, which needs no explicit start point.
            string startPoint = m_PopoverBaseField != null && m_PopoverBaseField.index > 0 ? m_PopoverBaseField.value : null;

            SetLoading(true, $"Creating branch '{name}'...");
            bool ok = await GitService.CreateBranchAsync(name, true, startPoint);
            SetLoading(false);

            if (!ok) return;

            ToggleBranchPopover(false);
            bool publish = m_PopoverPublishToggle != null && m_PopoverPublishToggle.value && GitService.CurrentStatus.HasRemote;
            if (!publish)
            {
                ShowToast($"Branch '{name}' created and checked out.", ToastType.Success);
                await GitService.RefreshStatusAsync();
                return;
            }

            string remote = GitService.CurrentStatus.DefaultRemote;
            SetLoading(true, $"Publishing '{name}' to {remote}...");
            bool published = await GitService.PublishBranchAsync();
            SetLoading(false);
            ShowToast(published
                    ? $"Branch '{name}' created and published to {remote}."
                    : $"Branch '{name}' created, but publishing failed.",
                published ? ToastType.Success : ToastType.Error);
            UpdateTopBarState();
        }

        private void RenderPopoverBranches()
        {
            if (m_PopoverContentScroll == null) return;
            m_PopoverContentScroll.Clear();

            var branches = m_CachedPopoverBranches;
            if (!string.IsNullOrWhiteSpace(m_PopoverSearchQuery))
            {
                string q = m_PopoverSearchQuery.Trim();
                branches = branches.Where(b => b.Name != null && b.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            }

            var recent = GitService.GetRecentBranches();

            // Detached HEAD: commits made here belong to no branch, so offer to create one.
            if (GitService.CurrentStatus.IsDetached && string.IsNullOrWhiteSpace(m_PopoverSearchQuery))
            {
                var warn = new VisualElement();
                warn.AddToClassList("git-popover-detached");
                var warnLbl = new Label("You're not on a branch (detached HEAD). Commits made here won't belong to any branch.");
                warnLbl.AddToClassList("git-popover-detached-label");
                warn.Add(warnLbl);
                var createHere = new Button(() => OpenBranchDrawer(renameSource: null)) { text = "Create Branch Here" };
                createHere.AddToClassList("git-btn-primary-small");
                warn.Add(createHere);
                m_PopoverContentScroll.Add(warn);
            }

            // 1. Default Branch (main/master)
            var defaultB = branches.FirstOrDefault(b => !b.IsRemote && b.Name == m_DefaultBranch);
            if (defaultB != null)
            {
                m_PopoverContentScroll.Add(CreatePopoverGroupHeader("DEFAULT BRANCH"));
                m_PopoverContentScroll.Add(CreatePopoverBranchRow(defaultB, isDefault: true));
            }

            // 2. Recent Branches (excluding default)
            var recentList = branches.Where(b => !b.IsRemote && b.Name != m_DefaultBranch && recent.Contains(b.Name)).ToList();
            if (recentList.Count > 0)
            {
                m_PopoverContentScroll.Add(CreatePopoverGroupHeader("RECENT BRANCHES"));
                foreach (var b in recentList)
                    m_PopoverContentScroll.Add(CreatePopoverBranchRow(b));
            }

            // 3. Other Local Branches
            var otherList = branches.Where(b => !b.IsRemote && b.Name != m_DefaultBranch && !recent.Contains(b.Name)).ToList();
            if (otherList.Count > 0)
            {
                m_PopoverContentScroll.Add(CreatePopoverGroupHeader("OTHER BRANCHES"));
                foreach (var b in otherList)
                    m_PopoverContentScroll.Add(CreatePopoverBranchRow(b));
            }

            // 4. Remote Branches (only those without a local counterpart; the rest are reachable via their local branch)
            var localNames = new HashSet<string>(m_CachedPopoverBranches.Where(b => !b.IsRemote).Select(b => b.Name));
            var trackedUpstreams = new HashSet<string>(m_CachedPopoverBranches.Where(b => !b.IsRemote && !string.IsNullOrEmpty(b.Upstream)).Select(b => b.Upstream));
            var remoteList = branches.Where(b => b.IsRemote && !localNames.Contains(b.ShortName) && !trackedUpstreams.Contains(b.Name)).ToList();
            if (remoteList.Count > 0)
            {
                m_PopoverContentScroll.Add(CreatePopoverGroupHeader("REMOTE BRANCHES"));
                foreach (var b in remoteList)
                    m_PopoverContentScroll.Add(CreatePopoverBranchRow(b));
            }

            if (branches.Count == 0)
            {
                var empty = new Label("No matching branches found.");
                empty.style.color = Color.gray;
                empty.style.paddingLeft = 14;
                empty.style.paddingTop = 14;
                m_PopoverContentScroll.Add(empty);
            }
        }

        private static VisualElement CreatePopoverGroupHeader(string title)
        {
            var lbl = new Label(title);
            lbl.AddToClassList("git-popover-group-label");
            return lbl;
        }

        private VisualElement CreatePopoverBranchRow(GitBranchInfo branch, bool isDefault = false)
        {
            var row = new VisualElement();
            row.AddToClassList("git-popover-item");
            if (branch.IsCurrent) row.AddToClassList("git-popover-item-current");

            var icon = new Label(branch.IsCurrent ? "●" : (isDefault ? "★" : branch.IsRemote ? "☁" : "⎇"));
            icon.AddToClassList("git-popover-item-icon");
            if (branch.IsCurrent) icon.style.color = new Color(0.35f, 0.8f, 0.5f);
            else if (isDefault) icon.style.color = new Color(0.9f, 0.75f, 0.45f);
            row.Add(icon);

            var nameLbl = new Label(branch.Name);
            nameLbl.AddToClassList("git-popover-item-name");
            if (branch.IsCurrent) nameLbl.style.unityFontStyleAndWeight = FontStyle.Bold;
            ApplyMonoFont(nameLbl);
            row.Add(nameLbl);

            if (branch.IsCurrent)
            {
                var check = new Label("✓");
                check.AddToClassList("git-popover-item-current-badge");
                row.Add(check);
            }

            // Upstream tracking badge: ahead/behind counts, "gone" when the remote branch was deleted,
            // "local" when the branch has never been published.
            if (!branch.IsRemote)
            {
                string trackText;
                string trackTooltip;
                if (string.IsNullOrEmpty(branch.Upstream)) { trackText = "local"; trackTooltip = "Not published to a remote"; }
                else if (branch.IsGone) { trackText = "gone"; trackTooltip = $"Upstream '{branch.Upstream}' was deleted on the remote"; }
                else
                {
                    trackText = (branch.AheadBy > 0 ? $"↑{branch.AheadBy} " : "") + (branch.BehindBy > 0 ? $"↓{branch.BehindBy}" : "");
                    trackTooltip = $"Tracking {branch.Upstream}";
                }

                if (!string.IsNullOrWhiteSpace(trackText))
                {
                    var upLbl = new Label(trackText.Trim()) { tooltip = trackTooltip };
                    upLbl.style.fontSize = 10;
                    upLbl.style.color = branch.IsGone ? new Color(0.9f, 0.5f, 0.5f) : new Color(0.5f, 0.5f, 0.5f);
                    upLbl.style.marginRight = 6;
                    ApplyMonoFont(upLbl);
                    row.Add(upLbl);
                }
            }

            // Delete action button on hover (only if not current and not default)
            if (!branch.IsCurrent && !isDefault && !branch.IsRemote)
            {
                var delBtn = new Button(() => DeleteLocalBranchFlow(branch)) { text = "✕" };
                delBtn.AddToClassList("git-popover-item-del-btn");
                delBtn.tooltip = branch.IsGone ? "Delete branch (its remote branch is gone)" : "Delete branch";
                if (branch.IsGone) delBtn.AddToClassList("git-popover-item-del-btn-suggested");
                delBtn.RegisterCallback<PointerDownEvent>(e => e.StopPropagation());
                row.Add(delBtn);
            }

            // Left click: checkout. Right click: branch actions.
            row.RegisterCallback<PointerDownEvent>(e =>
            {
                if (e.button == 1)
                {
                    e.StopPropagation();
                    ShowBranchContextMenu(branch, isDefault);
                    return;
                }
                if (e.button != 0) return;
                if (branch.IsCurrent)
                {
                    ToggleBranchPopover(false);
                    return;
                }
                CheckoutFlow(branch);
            });

            return row;
        }

        // ── Branch actions ───────────────────────────────────

        /// <summary>Checks out a branch; if uncommitted changes are in the way, asks whether to bring them along or leave them stashed.</summary>
        private async void CheckoutFlow(GitBranchInfo branch)
        {
            string target = branch.IsRemote ? branch.ShortName : branch.Name;
            SetLoading(true, $"Checking out '{target}'...");
            var outcome = branch.IsRemote
                ? await GitService.TryCheckoutRemoteBranchAsync(branch, m_CachedPopoverBranches)
                : await GitService.TryCheckoutAsync(branch.Name);
            SetLoading(false);

            if (outcome == CheckoutOutcome.BlockedByLocalChanges)
            {
                string current = GitService.CurrentBranch;
                int choice = EditorUtility.DisplayDialogComplex(
                    "Switch Branch",
                    $"You have uncommitted changes that would be overwritten by switching to '{target}'.\n\n" +
                    $"Bring them to '{target}', or leave them stashed on '{current}' (you can restore them later from the Stashes tab)?",
                    $"Bring my changes", "Cancel", $"Leave on {current}");
                if (choice == 1) return;

                bool bring = choice == 0;
                // A remote branch with a local counterpart is just a checkout of that local branch.
                var remoteToTrack = branch.IsRemote && !GitService.HasLocalCounterpart(branch, m_CachedPopoverBranches) ? branch : null;
                SetLoading(true, bring ? $"Moving changes to '{target}'..." : $"Stashing changes and switching to '{target}'...");
                bool ok = await GitService.CheckoutWithLocalChangesAsync(target, bring, remoteToTrack);
                SetLoading(false);
                if (GitService.CurrentBranch != target) return;

                if (ok && !bring) ShowToast($"Switched to '{target}'. Your changes are stashed for '{current}'.", ToastType.Info, 5f);
                else if (ok) ShowToast($"Switched to '{target}' with your changes.", ToastType.Success);
                ToggleBranchPopover(false);
                return;
            }

            if (outcome == CheckoutOutcome.Success)
            {
                ShowToast($"Switched to branch '{target}'.", ToastType.Success);
                ToggleBranchPopover(false);
            }
        }

        private async void DeleteLocalBranchFlow(GitBranchInfo branch)
        {
            string name = branch.Name;
            if (!EditorUtility.DisplayDialog("Delete Branch",
                    $"Are you sure you want to delete branch '{name}'?", "Delete", "Cancel"))
                return;

            SetLoading(true, $"Deleting branch '{name}'...");
            var outcome = await GitService.TryDeleteBranchAsync(name);
            SetLoading(false);

            if (outcome == DeleteBranchOutcome.NotFullyMerged)
            {
                if (!EditorUtility.DisplayDialog("Branch Not Merged",
                        $"'{name}' has commits that aren't merged into any other branch" +
                        (string.IsNullOrEmpty(branch.Upstream) || branch.IsGone ? " or pushed to a remote" : "") +
                        ".\n\nDeleting it will discard those commits. Delete anyway?",
                        "Delete Anyway", "Cancel"))
                    return;

                SetLoading(true, $"Deleting branch '{name}'...");
                outcome = await GitService.TryDeleteBranchAsync(name, force: true);
                SetLoading(false);
            }

            if (outcome == DeleteBranchOutcome.Success)
            {
                ShowToast($"Branch '{name}' deleted.", ToastType.Info);
                await ReloadPopoverBranches();
            }
        }

        private async void DeleteRemoteBranchFlow(string remote, string branchName)
        {
            if (!EditorUtility.DisplayDialog("Delete Remote Branch",
                    $"Delete '{branchName}' from {remote}?\n\nThis affects everyone using the repository.",
                    "Delete", "Cancel"))
                return;

            SetLoading(true, $"Deleting '{remote}/{branchName}'...");
            bool ok = await GitService.DeleteRemoteBranchAsync(remote, branchName);
            SetLoading(false);
            if (ok)
            {
                ShowToast($"Deleted '{branchName}' from {remote}.", ToastType.Info);
                await ReloadPopoverBranches();
                UpdateTopBarState();
            }
        }

        private async void MergeOrRebaseFlow(GitBranchInfo branch, bool rebase)
        {
            string current = GitService.CurrentBranch;
            string title = rebase ? "Rebase" : "Merge";
            string question = rebase
                ? $"Rebase '{current}' onto '{branch.Name}'?\n\nYour commits on '{current}' will be replayed on top of '{branch.Name}'. Avoid rebasing commits you've already pushed and shared."
                : $"Merge '{branch.Name}' into '{current}'?";
            if (!EditorUtility.DisplayDialog($"{title} Branch", question, title, "Cancel")) return;

            SetLoading(true, rebase ? $"Rebasing onto '{branch.Name}'..." : $"Merging '{branch.Name}'...");
            var outcome = rebase ? await GitService.RebaseOntoAsync(branch.Name) : await GitService.MergeBranchAsync(branch.Name);
            SetLoading(false);

            switch (outcome)
            {
                case MergeOutcome.Success:
                    ShowToast(rebase ? $"Rebased '{current}' onto '{branch.Name}'." : $"Merged '{branch.Name}' into '{current}'.", ToastType.Success);
                    ToggleBranchPopover(false);
                    break;
                case MergeOutcome.Conflicts:
                    ShowToast($"{title} stopped with conflicts. Resolve them in the Changes tab.", ToastType.Error, 6f);
                    ToggleBranchPopover(false);
                    SwitchTab(GitTab.Changes);
                    break;
            }
        }

        private void ShowBranchContextMenu(GitBranchInfo branch, bool isDefault)
        {
            var st = GitService.CurrentStatus;
            var menu = new GenericMenu();
            string current = st.CurrentBranch;
            bool canMergeHere = !branch.IsCurrent && !st.IsDetached && st.OperationState == GitRepoState.None;

            if (!branch.IsCurrent)
                menu.AddItem(new GUIContent("Checkout"), false, () => CheckoutFlow(branch));

            if (canMergeHere)
            {
                menu.AddItem(new GUIContent(MenuText($"Merge into '{current}'...")), false, () => MergeOrRebaseFlow(branch, rebase: false));
                menu.AddItem(new GUIContent(MenuText($"Rebase '{current}' onto this...")), false, () => MergeOrRebaseFlow(branch, rebase: true));
            }

            menu.AddItem(new GUIContent("New Branch From Here..."), false, () => OpenBranchDrawer(renameSource: null, startPoint: branch.Name));
            menu.AddSeparator("");

            if (!branch.IsRemote)
            {
                menu.AddItem(new GUIContent("Rename..."), false, () => OpenBranchDrawer(renameSource: branch.Name));

                if (branch.IsCurrent && st.CanPublishBranch)
                    menu.AddItem(new GUIContent("Publish Branch"), false, async () => { await PublishCurrentBranch(); await ReloadPopoverBranches(); });

                if (!branch.IsCurrent && !isDefault)
                    menu.AddItem(new GUIContent(branch.IsGone ? "Delete (remote branch is gone)..." : "Delete..."), false, () => DeleteLocalBranchFlow(branch));
                else
                    menu.AddDisabledItem(new GUIContent(branch.IsCurrent ? "Delete (checked out)" : "Delete (default branch)"));

                // The upstream "origin/x" → remote "origin", branch "x"
                if (!string.IsNullOrEmpty(branch.Upstream) && !branch.IsGone)
                {
                    var upstream = m_CachedPopoverBranches.FirstOrDefault(b => b.IsRemote && b.Name == branch.Upstream);
                    if (upstream != null)
                        menu.AddItem(new GUIContent(MenuText($"Delete '{branch.Upstream}' on Remote...")), false, () => DeleteRemoteBranchFlow(upstream.Remote, upstream.ShortName));
                }
            }
            else if (branch.ShortName != m_DefaultBranch)
            {
                menu.AddItem(new GUIContent("Delete on Remote..."), false, () => DeleteRemoteBranchFlow(branch.Remote, branch.ShortName));
            }

            if (m_CachedGitHubInfo.isGitHub)
            {
                string shortName = branch.IsRemote ? branch.ShortName : branch.Name;
                bool published = branch.IsRemote || (!string.IsNullOrEmpty(branch.Upstream) && !branch.IsGone);
                menu.AddSeparator("");
                if (published && shortName != m_DefaultBranch)
                    menu.AddItem(new GUIContent("Create Pull Request on GitHub"), false,
                        () => Application.OpenURL($"{m_CachedGitHubInfo.webUrl}/compare/{Uri.EscapeDataString(shortName).Replace("%2F", "/")}?expand=1"));
                else
                    menu.AddDisabledItem(new GUIContent(published ? "Create Pull Request (default branch)" : "Create Pull Request (publish first)"));
            }

            menu.AddSeparator("");
            menu.AddItem(new GUIContent("Copy Branch Name"), false, () =>
            {
                EditorGUIUtility.systemCopyBuffer = branch.IsRemote ? branch.ShortName : branch.Name;
                ShowToast("Branch name copied.", ToastType.Info);
            });

            menu.ShowAsContext();
        }

        private void RenderPopoverPullRequests()
        {
            if (m_PopoverContentScroll == null) return;
            m_PopoverContentScroll.Clear();

            var container = new VisualElement();
            container.AddToClassList("git-pr-container");

            var icon = new Label("⑂");
            icon.AddToClassList("git-pr-icon");
            container.Add(icon);

            var title = new Label("Pull Requests");
            title.AddToClassList("git-pr-title");
            container.Add(title);

            if (m_CachedGitHubInfo.isGitHub)
            {
                var repoLbl = new Label(m_CachedGitHubInfo.ownerRepo);
                repoLbl.style.fontSize = 11;
                repoLbl.style.color = new Color(0.6f, 0.8f, 1f);
                repoLbl.style.marginBottom = 6;
                ApplyMonoFont(repoLbl);
                container.Add(repoLbl);

                var subtitle = new Label($"Propose changes and collaborate with others on branch '{GitService.CurrentBranch}'.");
                subtitle.AddToClassList("git-pr-subtitle");
                container.Add(subtitle);

                string branch = GitService.CurrentBranch;
                string createUrl = $"{m_CachedGitHubInfo.webUrl}/compare/{branch}?expand=1";
                string pullsUrl = $"{m_CachedGitHubInfo.webUrl}/pulls";

                if (GitService.CurrentStatus.CanPublishBranch)
                {
                    // GitHub can't compare a branch it has never seen, so publishing has to come first.
                    var publishHint = new Label("This branch hasn't been published yet. Publish it before creating a pull request.");
                    publishHint.AddToClassList("git-pr-subtitle");
                    container.Add(publishHint);

                    var btnPublish = new Button(async () =>
                    {
                        if (await PublishCurrentBranch()) RenderPopoverPullRequests();
                    }) { text = "Publish branch" };
                    btnPublish.AddToClassList("git-pr-create-btn");
                    container.Add(btnPublish);
                }
                else
                {
                    var btnCreatePR = new Button(() =>
                    {
                        Application.OpenURL(createUrl);
                        ToggleBranchPopover(false);
                    }) { text = $"Create Pull Request on GitHub" };
                    btnCreatePR.AddToClassList("git-pr-create-btn");
                    container.Add(btnCreatePR);
                }

                var btnViewPulls = new Button(() =>
                {
                    Application.OpenURL(pullsUrl);
                    ToggleBranchPopover(false);
                }) { text = "View Open Pull Requests ↗" };
                btnViewPulls.AddToClassList("git-pr-link-btn");
                container.Add(btnViewPulls);
            }
            else
            {
                var subtitle = new Label("No GitHub remote detected for origin.\nPush your repository to GitHub to create and review pull requests.");
                subtitle.AddToClassList("git-pr-subtitle");
                container.Add(subtitle);
            }

            m_PopoverContentScroll.Add(container);
        }

        // ──────────────────────────────────────────────────────
        // Navigation Tabs (Streamlined to Changes, History, Stashes)
        // ──────────────────────────────────────────────────────

        private void BuildNavTabs()
        {
            m_NavTabsBar = new VisualElement();
            m_NavTabsBar.AddToClassList("git-nav-tabs");

            Button MakeTab(string label, GitTab tab)
            {
                var btn = new Button(() => SwitchTab(tab)) { text = label };
                btn.name = $"tab-{tab}";
                btn.AddToClassList("git-nav-tab");
                if (m_ActiveTab == tab) btn.AddToClassList("git-nav-tab-active");
                return btn;
            }

            int changes = GitService.CurrentStatus.TotalChangesCount;
            m_NavTabsBar.Add(MakeTab(changes > 0 ? $"Changes  {changes}" : "Changes", GitTab.Changes));
            m_NavTabsBar.Add(MakeTab("History",  GitTab.History));
            m_NavTabsBar.Add(MakeTab("Stashes",  GitTab.Stashes));

            m_RootContainer.Add(m_NavTabsBar);
        }

        private void SwitchTab(GitTab tab)
        {
            m_ActiveTab = tab;
            m_SelectedFileRow   = null;
            m_SelectedCommitRow = null;

            if (m_NavTabsBar != null)
            {
                foreach (var child in m_NavTabsBar.Children())
                    child.RemoveFromClassList("git-nav-tab-active");
                m_NavTabsBar.Q($"tab-{tab}")?.AddToClassList("git-nav-tab-active");
            }

            if (m_ContentArea == null) return;
            m_ContentArea.Clear();

            switch (tab)
            {
                case GitTab.Changes:  BuildChangesView();  break;
                case GitTab.History:  BuildHistoryView();  break;
                case GitTab.Stashes:  BuildStashesView();  break;
            }
        }

        // ══════════════════════════════════════════════════════
        //  TAB 1 — Changes
        // ══════════════════════════════════════════════════════

        private void BuildChangesView()
        {
            if (m_ContentArea == null) return;
            m_ContentArea.Clear();

            m_ChangesSplitView = new TwoPaneSplitView(0, 300, TwoPaneSplitViewOrientation.Horizontal);
            m_ChangesSplitView.style.flexGrow = 1;
            m_ChangesSplitView.style.height = Length.Percent(100);

            // ── Left pane: file lists + commit box ──────────
            var leftPane = new VisualElement();
            leftPane.style.flexDirection = FlexDirection.Column;
            leftPane.style.flexGrow = 1;
            leftPane.style.flexShrink = 1;
            leftPane.style.minWidth = 240;
            leftPane.style.height = Length.Percent(100);
            leftPane.style.overflow = Overflow.Hidden;

            var fileListsScroll = new ScrollView();
            fileListsScroll.style.flexGrow = 1;
            fileListsScroll.style.flexShrink = 1;
            fileListsScroll.style.flexBasis = 0;
            fileListsScroll.style.minHeight = 0;

            // Merge Conflicts Section (hidden when there are none)
            m_ConflictSection = BuildFileListSection(
                "MERGE CONFLICTS",
                new[]
                {
                    new SectionAction("✓", "Mark All as Resolved", false, async () =>
                    {
                        var files = GitService.CurrentStatus.ConflictedFiles;
                        if (files.Count == 0) return;
                        if (!EditorUtility.DisplayDialog("Mark All as Resolved",
                            $"Stage {files.Count} conflicted file{(files.Count == 1 ? "" : "s")} as resolved?\n\nMake sure no conflict markers (<<<<<<<) are left in them.",
                            "Mark Resolved", "Cancel")) return;
                        SetLoading(true, "Marking conflicts as resolved...");
                        await GitService.StageFilesAsync(files.Select(f => f.Path));
                        SetLoading(false);
                    })
                });
            m_ConflictSection.Root.AddToClassList("git-section-conflicts");
            fileListsScroll.Add(m_ConflictSection.Root);

            // Staged Changes Section
            m_StagedSection = BuildFileListSection(
                "STAGED CHANGES",
                new[]
                {
                    new SectionAction("−", "Unstage All Changes", false, async () =>
                    {
                        if (GitService.CurrentStatus.StagedFiles.Count == 0) return;
                        SetLoading(true, "Unstaging all changes...");
                        await GitService.UnstageAllAsync();
                        SetLoading(false);
                    })
                });
            fileListsScroll.Add(m_StagedSection.Root);

            // Changes (Unstaged) Section
            m_UnstagedSection = BuildFileListSection(
                "CHANGES",
                new[]
                {
                    new SectionAction("+", "Stage All Changes", false, async () =>
                    {
                        if (GitService.CurrentStatus.UnstagedFiles.Count == 0) return;
                        SetLoading(true, "Staging all changes...");
                        await GitService.StageAllAsync();
                        SetLoading(false);
                    }),
                    new SectionAction("↺", "Discard All Changes", true, async () =>
                    {
                        int count = GitService.CurrentStatus.UnstagedFiles.Count;
                        if (count == 0) return;
                        if (EditorUtility.DisplayDialog(
                            "Discard All Changes",
                            $"Are you sure you want to discard all changes in {count} modified file{(count == 1 ? "" : "s")}?\n\nThis will permanently revert all uncommitted modifications. This action cannot be undone.",
                            "Discard All", "Cancel"))
                        {
                            SetLoading(true, "Discarding all changes...");
                            await GitService.DiscardAllChangesAsync();
                            SetLoading(false);
                            ShowToast("All unstaged changes discarded.", ToastType.Info);
                        }
                    })
                });
            fileListsScroll.Add(m_UnstagedSection.Root);

            // Untracked Section
            m_UntrackedSection = BuildFileListSection(
                "UNTRACKED FILES",
                new[]
                {
                    new SectionAction("+", "Stage All Untracked Files", false, async () =>
                    {
                        var files = GitService.CurrentStatus.UntrackedFiles;
                        if (files.Count == 0) return;
                        SetLoading(true, "Staging untracked files...");
                        await GitService.StageFilesAsync(files.Select(f => f.Path));
                        SetLoading(false);
                    }),
                    new SectionAction("✕", "Delete All Untracked Files", true, async () =>
                    {
                        int count = GitService.CurrentStatus.UntrackedFiles.Count;
                        if (count == 0) return;
                        if (EditorUtility.DisplayDialog(
                            "Delete All Untracked Files",
                            $"Are you sure you want to permanently delete {count} untracked file{(count == 1 ? "" : "s")}?\n\nThis action cannot be undone.",
                            "Delete All", "Cancel"))
                        {
                            SetLoading(true, "Deleting untracked files...");
                            await GitService.DiscardAllUntrackedAsync();
                            SetLoading(false);
                            ShowToast("All untracked files deleted.", ToastType.Info);
                        }
                    })
                });
            fileListsScroll.Add(m_UntrackedSection.Root);

            leftPane.Add(fileListsScroll);

            // Commit box
            leftPane.Add(BuildCommitBox());
            m_ChangesSplitView.Add(leftPane);

            // ── Right pane: diff viewer ──────────────────────
            m_DiffPanel = BuildDiffPanel();
            m_ChangesSplitView.Add(m_DiffPanel);

            m_ContentArea.Add(m_ChangesSplitView);
            PopulateChangesLists(GitService.CurrentStatus);
        }

        // ── Custom File List Section ─────────────────────────

        private FileSectionUI BuildFileListSection(string title, IEnumerable<SectionAction> actions = null)
        {
            var section = new FileSectionUI();

            section.Root = new VisualElement();
            section.Root.AddToClassList("git-section-container");

            section.Header = new VisualElement();
            section.Header.AddToClassList("git-section-header");

            section.ChevronLabel = new Label("▼");
            section.ChevronLabel.AddToClassList("git-section-chevron");
            section.Header.Add(section.ChevronLabel);

            section.TitleLabel = new Label(title);
            section.TitleLabel.AddToClassList("git-section-title");
            section.Header.Add(section.TitleLabel);

            section.BadgeLabel = new Label("0");
            section.BadgeLabel.AddToClassList("git-section-badge");
            ApplyMonoFont(section.BadgeLabel, bold: true);
            section.Header.Add(section.BadgeLabel);

            section.ActionsContainer = new VisualElement();
            section.ActionsContainer.AddToClassList("git-section-actions");

            if (actions != null)
            {
                foreach (var act in actions)
                {
                    var btn = new Button(() => act.Action?.Invoke())
                    {
                        text = act.Icon,
                        tooltip = act.Tooltip
                    };
                    btn.AddToClassList("git-section-action-btn");
                    if (act.IsDanger) btn.AddToClassList("git-section-action-btn-danger");
                    btn.RegisterCallback<PointerDownEvent>(e => e.StopPropagation());
                    section.ActionsContainer.Add(btn);
                }
            }
            section.Header.Add(section.ActionsContainer);

            // Toggle collapse
            section.Header.RegisterCallback<PointerDownEvent>(e =>
            {
                if (e.button != 0) return;
                section.IsExpanded = !section.IsExpanded;
                section.ChevronLabel.text = section.IsExpanded ? "▼" : "▶";
                section.ScrollView.style.display = section.IsExpanded ? DisplayStyle.Flex : DisplayStyle.None;
            });

            section.Root.Add(section.Header);

            section.ScrollView = new VisualElement();
            section.ScrollView.AddToClassList("git-section-items");
            section.Root.Add(section.ScrollView);

            return section;
        }

        // ── Commit box ───────────────────────────────────────

        private VisualElement BuildCommitBox()
        {
            var box = new VisualElement();
            box.AddToClassList("git-commit-box");
            box.style.flexShrink = 0;
            box.style.flexGrow = 0;

            string prevMsg = m_CommitMessageField?.value ?? "";

            var commitScroll = new ScrollView(ScrollViewMode.Vertical);
            commitScroll.AddToClassList("git-commit-message-scroll");

            m_CommitMessageField = new TextField { multiline = true };
            m_CommitMessageField.AddToClassList("git-commit-message-field");
            m_CommitMessageField.value = prevMsg;
            m_CommitMessageField.style.whiteSpace = WhiteSpace.Normal;
            m_CommitMessageField.RegisterValueChangedCallback(e => UpdateCommitCharCount(e.newValue));

            Action applyWordWrap = () =>
            {
                var textInput = m_CommitMessageField.Q("unity-text-input");
                if (textInput != null)
                {
                    textInput.style.whiteSpace = WhiteSpace.Normal;
                }
            };
            m_CommitMessageField.RegisterCallback<AttachToPanelEvent>(_ => applyWordWrap());
            applyWordWrap();

            // Focus border styling
            m_CommitMessageField.RegisterCallback<FocusEvent>(_ => commitScroll.AddToClassList("git-commit-message-scroll-focus"));
            m_CommitMessageField.RegisterCallback<BlurEvent>(_ => commitScroll.RemoveFromClassList("git-commit-message-scroll-focus"));
            commitScroll.RegisterCallback<PointerDownEvent>(_ => m_CommitMessageField.Focus());

            // Ctrl+Enter to commit
            m_CommitMessageField.RegisterCallback<KeyDownEvent>(e =>
            {
                if (e.keyCode == KeyCode.Return && (e.ctrlKey || e.commandKey))
                {
                    e.StopPropagation();
                    OnCommitClicked();
                }
            });

            // Placeholder
            var placeholder = new Label("Summary (Ctrl+Enter to commit)...");
            placeholder.style.position = Position.Absolute;
            placeholder.style.left = 6;
            placeholder.style.top = 6;
            placeholder.style.color = new Color(0.48f, 0.48f, 0.48f);
            placeholder.style.fontSize = 11;
            placeholder.pickingMode = PickingMode.Ignore;
            commitScroll.Add(placeholder);

            void UpdatePlaceholder(string val) => placeholder.style.display = string.IsNullOrEmpty(val) ? DisplayStyle.Flex : DisplayStyle.None;
            UpdatePlaceholder(prevMsg);
            m_CommitMessageField.RegisterValueChangedCallback(e => UpdatePlaceholder(e.newValue));

            commitScroll.Add(m_CommitMessageField);
            box.Add(commitScroll);

            m_CommitCharCountLabel = new Label("0 characters");
            m_CommitCharCountLabel.AddToClassList("git-commit-char-count");
            UpdateCommitCharCount(prevMsg);
            box.Add(m_CommitCharCountLabel);

            var opts = new VisualElement();
            opts.style.flexDirection  = FlexDirection.Row;
            opts.style.alignItems     = Align.Center;
            opts.style.marginBottom   = 8;

            m_AmendToggle = new Toggle("Amend previous commit");
            m_AmendToggle.style.fontSize = 11;
            m_AmendToggle.style.flexGrow = 1;
            m_AmendToggle.RegisterValueChangedCallback(_ => UpdateButtonLabels());
            opts.Add(m_AmendToggle);
            box.Add(opts);

            var btns = new VisualElement();
            btns.style.flexDirection = FlexDirection.Row;
            btns.style.flexWrap = Wrap.Wrap;
            btns.style.flexShrink = 0;

            m_BtnCommit = new Button(OnCommitClicked) { text = "Commit" };
            m_BtnCommit.AddToClassList("git-btn-commit");
            m_BtnCommit.style.minWidth = 100;
            m_BtnCommit.style.flexGrow = 1;
            m_BtnCommit.style.flexShrink = 1;
            m_BtnCommit.style.marginBottom = 4;

            m_BtnCommitAndPush = new Button(OnCommitAndPushClicked) { text = "Commit & Push" };
            m_BtnCommitAndPush.AddToClassList("git-btn-commit-push");
            m_BtnCommitAndPush.style.minWidth = 100;
            m_BtnCommitAndPush.style.flexGrow = 1;
            m_BtnCommitAndPush.style.flexShrink = 1;
            m_BtnCommitAndPush.style.marginBottom = 4;

            btns.Add(m_BtnCommit);
            btns.Add(m_BtnCommitAndPush);
            box.Add(btns);

            UpdateButtonLabels();
            return box;
        }

        private void UpdateButtonLabels()
        {
            if (m_BtnCommit == null) return;
            if (m_AmendToggle != null && m_AmendToggle.value)
            {
                m_BtnCommit.text = "Amend Commit";
            }
            else
            {
                string branch = GitService.CurrentBranch;
                m_BtnCommit.text = string.IsNullOrEmpty(branch) ? "Commit" : $"Commit to {branch}";
            }

            if (m_BtnCommitAndPush != null)
            {
                var st = GitService.CurrentStatus;
                m_BtnCommitAndPush.text = st.CanPublishBranch ? "Commit & Publish" : "Commit & Push";
                m_BtnCommitAndPush.SetEnabled(st.HasRemote && !st.IsDetached);
                m_BtnCommitAndPush.tooltip = !st.HasRemote
                    ? "No remote configured for this repository."
                    : st.CanPublishBranch ? $"Commit, then publish '{st.CurrentBranch}' to {st.DefaultRemote} (git push -u)" : "";
            }
        }

        private void UpdateCommitCharCount(string text)
        {
            if (m_CommitCharCountLabel == null) return;
            int len = text?.Length ?? 0;
            m_CommitCharCountLabel.text = $"{len} character{(len == 1 ? "" : "s")}";

            bool warn = len > 72;
            m_CommitCharCountLabel.RemoveFromClassList("git-commit-char-count-warn");
            if (warn) m_CommitCharCountLabel.AddToClassList("git-commit-char-count-warn");
        }

        // ── Diff panel ───────────────────────────────────────

        private VisualElement BuildDiffPanel()
        {
            var panel = new VisualElement();
            panel.AddToClassList("git-diff-panel");
            panel.style.flexGrow = 1;
            panel.style.flexShrink = 1;
            panel.style.height = Length.Percent(100);
            panel.style.overflow = Overflow.Hidden;

            // Header
            var header = new VisualElement();
            header.AddToClassList("git-diff-header");

            m_DiffFileNameLabel = new Label("Select a file to view changes");
            m_DiffFileNameLabel.AddToClassList("git-diff-file-name");
            ApplyMonoFont(m_DiffFileNameLabel, bold: true);
            header.Add(m_DiffFileNameLabel);

            m_DiffFilePathLabel = new Label("");
            m_DiffFilePathLabel.AddToClassList("git-diff-file-path");
            ApplyMonoFont(m_DiffFilePathLabel);
            header.Add(m_DiffFilePathLabel);

            // Diff stats (+X -Y)
            var statsBox = new VisualElement();
            statsBox.AddToClassList("git-diff-stats");

            m_DiffStatsAdded = new Label("");
            m_DiffStatsAdded.AddToClassList("git-diff-stats-added");
            m_DiffStatsAdded.style.display = DisplayStyle.None;
            ApplyMonoFont(m_DiffStatsAdded, bold: true);
            statsBox.Add(m_DiffStatsAdded);

            m_DiffStatsDeleted = new Label("");
            m_DiffStatsDeleted.AddToClassList("git-diff-stats-deleted");
            m_DiffStatsDeleted.style.display = DisplayStyle.None;
            ApplyMonoFont(m_DiffStatsDeleted, bold: true);
            statsBox.Add(m_DiffStatsDeleted);
            header.Add(statsBox);

            m_BtnOpenInEditor = new Button(OpenSelectedFileInFuzzyIDE) { text = "Open in Editor" };
            m_BtnOpenInEditor.AddToClassList("git-diff-open-btn");
            m_BtnOpenInEditor.style.display = DisplayStyle.None;
            header.Add(m_BtnOpenInEditor);

            panel.Add(header);

            // Centered placeholder when no file is selected
            m_DiffPlaceholderBox = new VisualElement();
            m_DiffPlaceholderBox.AddToClassList("git-diff-placeholder");
            m_DiffPlaceholderBox.style.flexGrow = 1;
            m_DiffPlaceholderBox.style.alignItems = Align.Center;
            m_DiffPlaceholderBox.style.justifyContent = Justify.Center;

            var emptyIcon = new Label("⚖");
            emptyIcon.style.fontSize = 32;
            emptyIcon.style.marginBottom = 10;
            emptyIcon.style.unityTextAlign = TextAnchor.MiddleCenter;
            m_DiffPlaceholderBox.Add(emptyIcon);

            var emptyLbl = new Label("Select a file from the list to view its diff");
            emptyLbl.style.color = new Color(0.55f, 0.55f, 0.55f);
            emptyLbl.style.fontSize = 12;
            emptyLbl.style.unityTextAlign = TextAnchor.MiddleCenter;
            m_DiffPlaceholderBox.Add(emptyLbl);

            panel.Add(m_DiffPlaceholderBox);

            // ScrollView for diff content
            m_DiffContentScrollView = new ScrollView(ScrollViewMode.VerticalAndHorizontal);
            m_DiffContentScrollView.style.flexGrow = 1;
            m_DiffContentScrollView.style.flexShrink = 1;
            m_DiffContentScrollView.style.minHeight = 0;
            m_DiffContentScrollView.style.flexBasis = 0;
            m_DiffContentScrollView.style.display = DisplayStyle.None;
            panel.Add(m_DiffContentScrollView);

            ClearDiffPanel();
            return panel;
        }

        // ── Populate lists ───────────────────────────────────

        private void PopulateChangesLists(GitRepositoryStatus status)
        {
            if (m_StagedSection?.ScrollView == null || m_UnstagedSection?.ScrollView == null || m_UntrackedSection?.ScrollView == null)
                return;

            m_StagedSection.BadgeLabel.text = status.StagedFiles.Count.ToString();
            m_UnstagedSection.BadgeLabel.text = status.UnstagedFiles.Count.ToString();
            m_UntrackedSection.BadgeLabel.text = status.UntrackedFiles.Count.ToString();
            m_UntrackedSection.Root.style.display = status.UntrackedFiles.Count > 0 ? DisplayStyle.Flex : DisplayStyle.None;

            if (m_ConflictSection?.ScrollView != null)
            {
                m_ConflictSection.BadgeLabel.text = status.ConflictedFiles.Count.ToString();
                m_ConflictSection.Root.style.display = status.ConflictedFiles.Count > 0 ? DisplayStyle.Flex : DisplayStyle.None;
                m_ConflictSection.ScrollView.Clear();
                foreach (var f in status.ConflictedFiles)
                    m_ConflictSection.ScrollView.Add(CreateFileRow(f, isStaged: false));
            }

            m_StagedSection.ScrollView.Clear();
            foreach (var f in status.StagedFiles)
                m_StagedSection.ScrollView.Add(CreateFileRow(f, isStaged: true));

            m_UnstagedSection.ScrollView.Clear();
            foreach (var f in status.UnstagedFiles)
                m_UnstagedSection.ScrollView.Add(CreateFileRow(f, isStaged: false));

            m_UntrackedSection.ScrollView.Clear();
            foreach (var f in status.UntrackedFiles)
                m_UntrackedSection.ScrollView.Add(CreateFileRow(f, isStaged: false));

            UpdateButtonLabels();

            // Keep diff panel in sync
            if (m_SelectedDiffFile != null)
            {
                bool exists = status.ConflictedFiles.Any(f => f.Path == m_SelectedDiffFile.Path)
                           || status.StagedFiles.Any(f => f.Path == m_SelectedDiffFile.Path)
                           || status.UnstagedFiles.Any(f => f.Path == m_SelectedDiffFile.Path)
                           || status.UntrackedFiles.Any(f => f.Path == m_SelectedDiffFile.Path);
                if (!exists) ClearDiffPanel();
            }
        }

        // ── File row (enhanced) ──────────────────────────────

        private VisualElement CreateFileRow(GitFileStatus file, bool isStaged)
        {
            var row = new VisualElement();
            row.AddToClassList("git-file-row");

            // File type icon
            var iconImg = new Image();
            iconImg.image = GetFileTypeIcon(file.FileName);
            iconImg.style.width    = 14;
            iconImg.style.height   = 14;
            iconImg.style.minWidth = 14;
            row.Add(iconImg);

            // File name + path column
            var nameCol = new VisualElement();
            nameCol.AddToClassList("git-file-info-col");

            var nameLbl = new Label(file.FileName);
            nameLbl.AddToClassList("git-file-name");
            ApplyMonoFont(nameLbl);
            nameCol.Add(nameLbl);

            string dir = Path.GetDirectoryName(file.Path);
            if (!string.IsNullOrEmpty(dir))
            {
                var pathLbl = new Label(dir.Replace('\\', '/'));
                pathLbl.AddToClassList("git-file-path");
                ApplyMonoFont(pathLbl);
                nameCol.Add(pathLbl);
            }
            row.Add(nameCol);

            // Status badge
            var badge = new Label(file.GetBadgeText(isStaged));
            badge.AddToClassList("git-badge");
            badge.style.backgroundColor = file.GetBadgeColor(isStaged);
            ApplyMonoFont(badge, bold: true);
            row.Add(badge);

            // Hover action buttons
            var actions = new VisualElement();
            actions.AddToClassList("git-row-actions");

            if (file.WorkingTreeState == GitFileState.Conflict)
            {
                var (mineLabel, theirsLabel) = GetConflictSideLabels(file);
                actions.Add(CreateRowActionButton("◀", mineLabel, false, () => ResolveConflictFlow(file, keepMine: true)));
                actions.Add(CreateRowActionButton("▶", theirsLabel, false, () => ResolveConflictFlow(file, keepMine: false)));
                actions.Add(CreateRowActionButton("✓", "Mark as resolved (stage the file as it is now)", false, async () =>
                {
                    await GitService.StageFilesAsync(new[] { file.Path });
                }));
            }
            else if (isStaged)
            {
                actions.Add(CreateRowActionButton("−", "Unstage file", false, async () =>
                {
                    await GitService.UnstageFilesAsync(new[] { file.Path });
                }));
            }
            else
            {
                actions.Add(CreateRowActionButton("+", "Stage file", false, async () =>
                {
                    await GitService.StageFilesAsync(new[] { file.Path });
                }));

                if (!file.IsUntracked)
                {
                    actions.Add(CreateRowActionButton("↺", "Discard changes", true, async () =>
                    {
                        if (EditorUtility.DisplayDialog(
                            "Discard Changes",
                            $"Are you sure you want to discard changes in '{file.FileName}'?\n\nThis will permanently revert all uncommitted modifications.",
                            "Discard", "Cancel"))
                        {
                            await GitService.DiscardChangesAsync(new[] { file.Path });
                        }
                    }));
                }
                else
                {
                    actions.Add(CreateRowActionButton("✕", "Delete file", true, async () =>
                    {
                        if (EditorUtility.DisplayDialog(
                            "Delete Untracked File",
                            $"Are you sure you want to permanently delete '{file.FileName}'?",
                            "Delete", "Cancel"))
                        {
                            await GitService.DiscardUntrackedFilesAsync(new[] { file.Path });
                        }
                    }));
                }
            }

            row.Add(actions);

            // Selection, diff viewer & context menu
            row.RegisterCallback<PointerDownEvent>(e =>
            {
                if (e.button == 0) // Left click
                {
                    SelectFileRow(row);
                    ShowDiffForFile(file, isStaged);

                    if (e.clickCount == 2)
                    {
                        OpenSelectedFileInFuzzyIDE();
                    }
                }
                else if (e.button == 1) // Right click
                {
                    SelectFileRow(row);
                    ShowFileContextMenu(file, isStaged);
                }
            });

            return row;
        }

        private void ShowFileContextMenu(GitFileStatus file, bool isStaged)
        {
            var menu = new GenericMenu();
            if (file.WorkingTreeState == GitFileState.Conflict)
            {
                var (mineLabel, theirsLabel) = GetConflictSideLabels(file);
                menu.AddItem(new GUIContent(MenuText(mineLabel)), false, () => ResolveConflictFlow(file, keepMine: true));
                menu.AddItem(new GUIContent(MenuText(theirsLabel)), false, () => ResolveConflictFlow(file, keepMine: false));
                menu.AddItem(new GUIContent("Mark as Resolved"), false, async () =>
                {
                    await GitService.StageFilesAsync(new[] { file.Path });
                });
            }
            else if (isStaged)
            {
                menu.AddItem(new GUIContent("Unstage"), false, async () =>
                {
                    await GitService.UnstageFilesAsync(new[] { file.Path });
                });
            }
            else
            {
                menu.AddItem(new GUIContent("Stage"), false, async () =>
                {
                    await GitService.StageFilesAsync(new[] { file.Path });
                });

                if (!file.IsUntracked)
                {
                    menu.AddItem(new GUIContent("Discard Changes..."), false, async () =>
                    {
                        if (EditorUtility.DisplayDialog("Discard Changes",
                            $"Are you sure you want to discard changes in '{file.FileName}'?", "Discard", "Cancel"))
                        {
                            await GitService.DiscardChangesAsync(new[] { file.Path });
                        }
                    });
                }
                else
                {
                    menu.AddItem(new GUIContent("Delete File..."), false, async () =>
                    {
                        if (EditorUtility.DisplayDialog("Delete File",
                            $"Are you sure you want to permanently delete '{file.FileName}'?", "Delete", "Cancel"))
                        {
                            await GitService.DiscardUntrackedFilesAsync(new[] { file.Path });
                        }
                    });
                }
            }

            menu.AddSeparator("");
            menu.AddItem(new GUIContent("Open in IDE"), false, OpenSelectedFileInFuzzyIDE);
            menu.AddItem(new GUIContent("Reveal in Project"), false, () =>
            {
                var obj = AssetDatabase.LoadMainAssetAtPath(file.Path);
                if (obj != null) EditorGUIUtility.PingObject(obj);
            });
            menu.AddItem(new GUIContent("Reveal in Explorer"), false, () =>
            {
                string fullPath = Path.Combine(GitService.RepositoryRoot, file.Path);
                if (File.Exists(fullPath)) EditorUtility.RevealInFinder(fullPath);
            });
            menu.AddItem(new GUIContent("Copy Relative Path"), false, () =>
            {
                EditorGUIUtility.systemCopyBuffer = file.Path;
                ShowToast("Path copied to clipboard.", ToastType.Info);
            });
            menu.ShowAsContext();
        }

        /// <summary>Menu/tooltip labels for taking one side of a conflict, naming the branches and saying when a side deleted the file.</summary>
        private (string mine, string theirs) GetConflictSideLabels(GitFileStatus file)
        {
            var st = GitService.CurrentStatus;
            bool rebasing = st.OperationState == GitRepoState.Rebasing;
            // While rebasing, "ours" is the branch being rebased onto, so the user's own version is "theirs".
            bool mineDeleted = rebasing ? file.DeletedByThem : file.DeletedByUs;
            bool theirsDeleted = rebasing ? file.DeletedByUs : file.DeletedByThem;
            string mineName = st.IsDetached || rebasing ? "your version" : $"'{st.CurrentBranch}'";
            string theirsName = rebasing ? "the upstream version" : "the incoming version";
            string mine = mineDeleted ? $"Accept deletion from {mineName}" : $"Keep {mineName}";
            string theirs = theirsDeleted ? $"Accept deletion from {theirsName}" : $"Use {theirsName}";
            return (mine, theirs);
        }

        private async void ResolveConflictFlow(GitFileStatus file, bool keepMine)
        {
            var (mine, theirs) = GetConflictSideLabels(file);
            if (!EditorUtility.DisplayDialog("Resolve Conflict",
                    $"{(keepMine ? mine : theirs)} for '{file.FileName}'?\n\nThe other side's changes to this file will be discarded.",
                    "Resolve", "Cancel"))
                return;

            SetLoading(true, $"Resolving '{file.FileName}'...");
            bool ok = await GitService.ResolveConflictAsync(file, keepMine);
            SetLoading(false);
            if (ok) ShowToast($"'{file.FileName}' resolved.", ToastType.Success);
        }

        private static Button CreateRowActionButton(string text, string tooltip, bool isDanger, Action onClick)
        {
            var btn = new Button(() => onClick()) { text = text, tooltip = tooltip };
            btn.AddToClassList("git-row-action-btn");
            if (isDanger) btn.AddToClassList("git-row-action-btn-danger");
            return btn;
        }

        private void SelectFileRow(VisualElement row)
        {
            m_SelectedFileRow?.RemoveFromClassList("git-file-row-selected");
            m_SelectedFileRow = row;
            row?.AddToClassList("git-file-row-selected");
        }

        private void ClearDiffPanel()
        {
            m_SelectedDiffFile = null;
            if (m_DiffFileNameLabel != null) m_DiffFileNameLabel.text = "Select a file to view changes";
            if (m_DiffFilePathLabel != null) m_DiffFilePathLabel.text = "";
            if (m_BtnOpenInEditor  != null) m_BtnOpenInEditor.style.display = DisplayStyle.None;
            if (m_DiffStatsAdded   != null) m_DiffStatsAdded.style.display  = DisplayStyle.None;
            if (m_DiffStatsDeleted != null) m_DiffStatsDeleted.style.display = DisplayStyle.None;

            if (m_DiffContentScrollView != null)
            {
                m_DiffContentScrollView.Clear();
                m_DiffContentScrollView.scrollOffset = Vector2.zero;
                m_DiffContentScrollView.style.display = DisplayStyle.None;
            }

            if (m_DiffPlaceholderBox != null)
            {
                m_DiffPlaceholderBox.style.display = DisplayStyle.Flex;
            }
        }

        // ── Diff viewer ──────────────────────────────────────

        private async void ShowDiffForFile(GitFileStatus file, bool isStaged)
        {
            m_SelectedDiffFile     = file;
            m_SelectedDiffIsStaged = isStaged;

            if (m_DiffFileNameLabel != null) m_DiffFileNameLabel.text = file.FileName;
            if (m_DiffFilePathLabel != null)
            {
                string dir = Path.GetDirectoryName(file.Path)?.Replace('\\', '/') ?? "";
                m_DiffFilePathLabel.text = string.IsNullOrEmpty(dir) ? "" : "/ " + dir;
            }
            if (m_BtnOpenInEditor != null) m_BtnOpenInEditor.style.display = DisplayStyle.Flex;
            if (m_DiffStatsAdded  != null) { m_DiffStatsAdded.text = ""; m_DiffStatsAdded.style.display   = DisplayStyle.None; }
            if (m_DiffStatsDeleted!= null) { m_DiffStatsDeleted.text = ""; m_DiffStatsDeleted.style.display = DisplayStyle.None; }

            if (m_DiffPlaceholderBox != null)
            {
                m_DiffPlaceholderBox.style.display = DisplayStyle.None;
            }

            if (m_DiffContentScrollView != null)
            {
                m_DiffContentScrollView.style.display = DisplayStyle.Flex;
                m_DiffContentScrollView.scrollOffset = Vector2.zero;
                m_DiffContentScrollView.Clear();
            }

            string diffText = await GitService.GetFileDiffAsync(file.Path, isStaged, untracked: file.IsUntracked);
            RenderDiff(diffText);
        }

        private void RenderDiff(string diffText)
        {
            RenderDiffIntoScrollView(m_DiffContentScrollView, diffText, m_DiffStatsAdded, m_DiffStatsDeleted);
        }

        private static void RenderDiffIntoScrollView(ScrollView targetScrollView, string diffText, Label statsAdded = null, Label statsDeleted = null)
        {
            if (targetScrollView == null) return;
            targetScrollView.Clear();
            targetScrollView.scrollOffset = Vector2.zero;

            if (string.IsNullOrWhiteSpace(diffText))
            {
                var empty = new Label("No diff content to display (file may be empty, untracked, or binary).");
                empty.AddToClassList("git-diff-empty-msg");
                targetScrollView.Add(empty);
                if (statsAdded != null) statsAdded.style.display = DisplayStyle.None;
                if (statsDeleted != null) statsDeleted.style.display = DisplayStyle.None;
                return;
            }

            int oldLine = 0, newLine = 0;
            bool inHunk   = false;
            int addCount  = 0;
            int delCount  = 0;

            using var reader = new StringReader(diffText);
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                if (line.StartsWith("@@"))
                {
                    inHunk = true;
                    ParseHunkHeader(line, out oldLine, out newLine);
                    targetScrollView.Add(CreateDiffRow(line, null, null, "hunk"));
                    continue;
                }

                if (!inHunk)
                {
                    if (line.StartsWith("diff ")  || line.StartsWith("index ") ||
                        line.StartsWith("--- ")  || line.StartsWith("+++ "))
                    {
                        targetScrollView.Add(CreateDiffRow(line, null, null, "meta"));
                    }
                    continue;
                }

                string kind;
                string oldNumStr = null, newNumStr = null;

                if (line.StartsWith("+"))
                {
                    kind = "added";
                    newNumStr = newLine.ToString();
                    newLine++;
                    addCount++;
                }
                else if (line.StartsWith("-"))
                {
                    kind = "deleted";
                    oldNumStr = oldLine.ToString();
                    oldLine++;
                    delCount++;
                }
                else if (line.StartsWith("\\"))
                {
                    targetScrollView.Add(CreateDiffRow(line, null, null, "meta"));
                    continue;
                }
                else
                {
                    kind = "context";
                    oldNumStr = oldLine.ToString();
                    newNumStr = newLine.ToString();
                    oldLine++;
                    newLine++;
                }

                targetScrollView.Add(CreateDiffRow(line, oldNumStr, newNumStr, kind));
            }

            // Update stats labels
            if (statsAdded != null)
            {
                if (addCount > 0)
                {
                    statsAdded.text = $"+{addCount}";
                    statsAdded.style.display = DisplayStyle.Flex;
                }
                else
                {
                    statsAdded.text = "";
                    statsAdded.style.display = DisplayStyle.None;
                }
            }
            if (statsDeleted != null)
            {
                if (delCount > 0)
                {
                    statsDeleted.text = $"-{delCount}";
                    statsDeleted.style.display = DisplayStyle.Flex;
                }
                else
                {
                    statsDeleted.text = "";
                    statsDeleted.style.display = DisplayStyle.None;
                }
            }
        }

        private static void ParseHunkHeader(string line, out int oldStart, out int newStart)
        {
            oldStart = 1; newStart = 1;
            var match = Regex.Match(line, @"@@ -(\d+)(?:,\d+)? \+(\d+)(?:,\d+)? @@");
            if (match.Success)
            {
                int.TryParse(match.Groups[1].Value, out oldStart);
                int.TryParse(match.Groups[2].Value, out newStart);
            }
        }

        private static VisualElement CreateDiffRow(string rawLine, string oldNum, string newNum, string kind)
        {
            var row = new VisualElement();
            row.AddToClassList("git-diff-row");
            row.AddToClassList($"git-diff-row-{kind}");

            // Gutter
            var gutter = new VisualElement();
            gutter.AddToClassList("git-diff-gutter");

            var oldLbl = new Label(oldNum ?? "");
            oldLbl.AddToClassList("git-diff-line-num");
            if (!string.IsNullOrEmpty(oldNum)) oldLbl.AddToClassList("git-diff-line-num-active");
            ApplyMonoFont(oldLbl);
            gutter.Add(oldLbl);

            var newLbl = new Label(newNum ?? "");
            newLbl.AddToClassList("git-diff-line-num");
            if (!string.IsNullOrEmpty(newNum)) newLbl.AddToClassList("git-diff-line-num-active");
            ApplyMonoFont(newLbl);
            gutter.Add(newLbl);

            row.Add(gutter);

            // Marker
            string markerChar = kind switch
            {
                "added"   => "+",
                "deleted" => "-",
                "hunk"    => "⋯",
                "meta"    => "·",
                _         => " "
            };
            var marker = new Label(markerChar);
            marker.AddToClassList("git-diff-marker");
            marker.AddToClassList($"git-diff-marker-{kind}");
            ApplyMonoFont(marker, bold: true);
            row.Add(marker);

            // Content
            string content = rawLine.Length > 0 && (kind == "added" || kind == "deleted")
                ? rawLine.Substring(1)
                : rawLine;

            var contentLbl = new Label(content);
            contentLbl.AddToClassList("git-diff-content");
            contentLbl.AddToClassList($"git-diff-content-{kind}");
            ApplyMonoFont(contentLbl);
            row.Add(contentLbl);

            return row;
        }

        private void OpenSelectedFileInFuzzyIDE()
        {
            if (m_SelectedDiffFile == null) return;
            if (File.Exists(m_SelectedDiffFile.FullPath))
            {
                var ide = VirtualIDEWindow.ActiveInstance ?? GetWindow<VirtualIDEWindow>();
                ide.LoadFileAndJumpToLine(m_SelectedDiffFile.Path, 1);
                ide.Focus();
            }
        }

        // ── Commit actions ───────────────────────────────────

        private async void OnCommitClicked()
        {
            string msg = m_CommitMessageField?.value ?? "";
            if (string.IsNullOrWhiteSpace(msg))
            {
                EditorUtility.DisplayDialog("Commit Error", "Please enter a commit message before committing.", "OK");
                return;
            }

            if (GitService.CurrentStatus.StagedFiles.Count == 0 && !m_AmendToggle.value)
            {
                bool stageAll = EditorUtility.DisplayDialog(
                    "No Staged Changes",
                    "No changes are currently staged. Would you like to stage all changes and commit?",
                    "Stage All & Commit", "Cancel");
                if (stageAll)
                    await GitService.StageAllAsync();
                else
                    return;
            }

            SetLoading(true, "Committing...");
            bool ok = await GitService.CommitAsync(msg, m_AmendToggle.value);
            SetLoading(false);

            if (ok)
            {
                if (m_CommitMessageField != null) m_CommitMessageField.value = "";
                if (m_AmendToggle != null) m_AmendToggle.value = false;
                ShowToast("Commit successful.", ToastType.Success);
            }
        }

        private async void OnCommitAndPushClicked()
        {
            string msg = m_CommitMessageField?.value ?? "";
            if (string.IsNullOrWhiteSpace(msg))
            {
                EditorUtility.DisplayDialog("Commit Error", "Please enter a commit message before committing.", "OK");
                return;
            }

            SetLoading(true, "Committing & Pushing...");
            bool committed = await GitService.CommitAsync(msg, m_AmendToggle?.value ?? false);
            if (committed)
            {
                if (m_CommitMessageField != null) m_CommitMessageField.value = "";
                if (m_AmendToggle != null) m_AmendToggle.value = false;
                // PushAsync publishes with -u when the branch has no upstream yet.
                bool pushed = await GitService.PushAsync();
                SetLoading(false);
                ShowToast(pushed ? "Commit & Push successful." : "Committed successfully, but Push failed.",
                    pushed ? ToastType.Success : ToastType.Error);
                UpdateTopBarState();
            }
            else
            {
                SetLoading(false);
            }
        }

        // ══════════════════════════════════════════════════════
        //  TAB 2 — History
        // ══════════════════════════════════════════════════════

        private async void BuildHistoryView()
        {
            if (m_ContentArea == null) return;
            m_ContentArea.Clear();

            var splitView = new TwoPaneSplitView(0, 320, TwoPaneSplitViewOrientation.Horizontal);
            splitView.style.flexGrow = 1;

            // Left: commit list with search bar
            var leftSide = new VisualElement();
            leftSide.style.flexGrow = 1;

            var filterBar = new VisualElement();
            filterBar.AddToClassList("git-filter-bar");

            var searchIcon = new Label("🔍 ");
            searchIcon.style.color = new Color(0.5f, 0.5f, 0.5f);
            searchIcon.style.fontSize = 11;
            filterBar.Add(searchIcon);

            var searchField = new TextField { label = "" };
            searchField.AddToClassList("git-filter-field");
            searchField.value = m_HistoryFilterQuery;
            searchField.tooltip = "Filter commits by message, author, or hash...";
            searchField.RegisterValueChangedCallback(e =>
            {
                m_HistoryFilterQuery = e.newValue;
                RenderCommitsList(m_CachedCommits);
            });
            filterBar.Add(searchField);
            leftSide.Add(filterBar);

            m_HistoryScrollView = new ScrollView();
            m_HistoryScrollView.style.flexGrow = 1;
            leftSide.Add(m_HistoryScrollView);
            splitView.Add(leftSide);

            // Right: commit detail
            m_CommitDetailPanel = BuildCommitDetailPanel();
            splitView.Add(m_CommitDetailPanel);

            m_ContentArea.Add(splitView);

            // Load commits
            SetLoading(true, "Loading commit history...");
            m_CachedCommits = await GitService.GetLogAsync(100);
            SetLoading(false);

            RenderCommitsList(m_CachedCommits);
        }

        private void RenderCommitsList(List<GitCommitInfo> commits)
        {
            if (m_HistoryScrollView == null) return;
            m_HistoryScrollView.Clear();

            var filtered = commits;
            if (!string.IsNullOrWhiteSpace(m_HistoryFilterQuery))
            {
                string q = m_HistoryFilterQuery.Trim();
                filtered = commits.Where(c =>
                    (c.Message != null && c.Message.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) ||
                    (c.Author != null && c.Author.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) ||
                    (c.Hash != null && c.Hash.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
                ).ToList();
            }

            if (filtered.Count == 0)
            {
                var empty = new Label("No commits found.");
                empty.style.color = Color.gray;
                empty.style.paddingLeft = 14;
                empty.style.paddingTop  = 14;
                m_HistoryScrollView.Add(empty);
                return;
            }

            // Group by date
            string lastDateGroup = null;
            foreach (var commit in filtered)
            {
                string dateGroup = GetDateGroupLabel(commit.Date);
                if (dateGroup != lastDateGroup)
                {
                    m_HistoryScrollView.Add(CreateDateGroupHeader(dateGroup));
                    lastDateGroup = dateGroup;
                }

                var row = CreateCommitRow(commit);
                m_HistoryScrollView.Add(row);
            }

            // Select first by default if nothing selected
            if (filtered.Count > 0 && m_SelectedCommitRow == null)
            {
                var firstRow = m_HistoryScrollView.Q<VisualElement>(className: "git-commit-row");
                SelectCommitRow(firstRow);
                ShowCommitDetails(filtered[0]);
            }
        }

        private static VisualElement BuildCommitDetailPanel()
        {
            var panel = new VisualElement();
            panel.AddToClassList("git-commit-detail-panel");
            return panel;
        }

        private static string GetDateGroupLabel(DateTime date)
        {
            if (date == default) return "Unknown Date";
            var today = DateTime.Today;
            if (date.Date == today)              return "Today";
            if (date.Date == today.AddDays(-1))  return "Yesterday";
            if ((today - date.Date).TotalDays < 7) return date.ToString("dddd");
            if (date.Year == today.Year)         return date.ToString("MMMM");
            return date.ToString("MMMM yyyy");
        }

        private static VisualElement CreateDateGroupHeader(string label)
        {
            var row = new VisualElement();
            row.AddToClassList("git-date-group-header");
            var lbl = new Label(label);
            lbl.AddToClassList("git-date-group-label");
            row.Add(lbl);
            return row;
        }

        private VisualElement CreateCommitRow(GitCommitInfo commit)
        {
            var row = new VisualElement();
            row.AddToClassList("git-commit-row");

            // Avatar
            var avatar = new VisualElement();
            avatar.AddToClassList("git-commit-avatar");
            avatar.style.backgroundColor = GetAvatarColor(commit.Author);

            var initials = new Label(GetInitials(commit.Author));
            initials.AddToClassList("git-commit-avatar-label");
            avatar.Add(initials);
            row.Add(avatar);

            // Text column
            var textCol = new VisualElement();
            textCol.AddToClassList("git-commit-text-col");

            var msgLbl = new Label(commit.Message);
            msgLbl.AddToClassList("git-commit-message");
            textCol.Add(msgLbl);

            var metaLbl = new Label($"{commit.Author}  ·  {commit.RelativeTime}");
            metaLbl.AddToClassList("git-commit-meta");
            textCol.Add(metaLbl);

            row.Add(textCol);

            // Hash badge
            var hashBadge = new Label(commit.ShortHash);
            hashBadge.AddToClassList("git-commit-hash-badge");
            ApplyMonoFont(hashBadge);
            row.Add(hashBadge);

            row.RegisterCallback<PointerDownEvent>(e =>
            {
                SelectCommitRow(row);
                ShowCommitDetails(commit);
            });

            return row;
        }

        private void SelectCommitRow(VisualElement row)
        {
            m_SelectedCommitRow?.RemoveFromClassList("git-commit-row-selected");
            m_SelectedCommitRow = row;
            row?.AddToClassList("git-commit-row-selected");
        }

        private async void ShowCommitDetails(GitCommitInfo commit)
        {
            if (m_CommitDetailPanel == null) return;
            m_CommitDetailPanel.Clear();
            m_SelectedCommitFileRow = null;

            // Header
            var header = new VisualElement();
            header.AddToClassList("git-commit-detail-header");

            var hashRow = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center } };
            var hashLbl = new Label($"commit {commit.Hash}");
            hashLbl.AddToClassList("git-commit-detail-hash");
            ApplyMonoFont(hashLbl);
            hashRow.Add(hashLbl);
            header.Add(hashRow);

            var authorLbl = new Label($"{commit.Author} <{commit.Email}>  ·  {commit.Date:g}");
            authorLbl.AddToClassList("git-commit-detail-author");
            header.Add(authorLbl);

            var msgLbl = new Label(commit.Message);
            msgLbl.AddToClassList("git-commit-detail-message");
            header.Add(msgLbl);

            m_CommitDetailPanel.Add(header);

            // Fetch file list
            // --format= drops the commit header so only "<status>\t<path>[\t<new path>]" lines remain; quotePath keeps non-ASCII names readable.
            var result = await GitService.ExecuteGitAsync($"-c core.quotePath=false show --name-status --format= {commit.Hash}");
            if (!result.Success) return;

            var lines = result.Output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var fileRows = new List<(string status, string path)>();
            foreach (var line in lines)
            {
                string f = line.Trim();
                if (string.IsNullOrEmpty(f)) continue;

                var parts = f.Split('\t');
                string statusCode = parts.Length > 1 ? parts[0].Trim() : "";
                string filePath   = parts.Length > 2 ? parts[parts.Length - 1].Trim() : (parts.Length > 1 ? parts[1].Trim() : f);
                fileRows.Add((statusCode, filePath));
            }

            // TwoPaneSplitView (vertical) for files list (top) and diff view (bottom)
            var historySplit = new TwoPaneSplitView(0, 160, TwoPaneSplitViewOrientation.Vertical);
            historySplit.style.flexGrow = 1;

            // Top pane: files list
            var filesTopPane = new VisualElement();
            filesTopPane.style.flexDirection = FlexDirection.Column;
            filesTopPane.style.flexGrow = 1;
            filesTopPane.style.minHeight = 70;

            var filesHeader = new VisualElement();
            filesHeader.AddToClassList("git-commit-detail-files-header");
            var filesHeaderLbl = new Label($"Changed Files ({fileRows.Count})");
            filesHeaderLbl.AddToClassList("git-commit-detail-files-label");
            filesHeader.Add(filesHeaderLbl);
            filesTopPane.Add(filesHeader);

            var commitFilesScrollView = new ScrollView();
            commitFilesScrollView.style.flexGrow = 1;
            filesTopPane.Add(commitFilesScrollView);
            historySplit.Add(filesTopPane);

            // Bottom pane: commit file diff panel
            var diffBottomPane = new VisualElement();
            diffBottomPane.AddToClassList("git-diff-panel");
            diffBottomPane.style.flexGrow = 1;
            diffBottomPane.style.flexShrink = 1;
            diffBottomPane.style.minHeight = 80;

            var diffHeader = new VisualElement();
            diffHeader.AddToClassList("git-diff-header");

            var diffFileNameLbl = new Label("Select a file to view changes");
            diffFileNameLbl.AddToClassList("git-diff-file-name");
            ApplyMonoFont(diffFileNameLbl, bold: true);
            diffHeader.Add(diffFileNameLbl);

            var diffFilePathLbl = new Label("");
            diffFilePathLbl.AddToClassList("git-diff-file-path");
            ApplyMonoFont(diffFilePathLbl);
            diffHeader.Add(diffFilePathLbl);

            var diffStatsBox = new VisualElement();
            diffStatsBox.AddToClassList("git-diff-stats");

            var statsAdded = new Label("");
            statsAdded.AddToClassList("git-diff-stats-added");
            statsAdded.style.display = DisplayStyle.None;
            ApplyMonoFont(statsAdded, bold: true);
            diffStatsBox.Add(statsAdded);

            var statsDeleted = new Label("");
            statsDeleted.AddToClassList("git-diff-stats-deleted");
            statsDeleted.style.display = DisplayStyle.None;
            ApplyMonoFont(statsDeleted, bold: true);
            diffStatsBox.Add(statsDeleted);
            diffHeader.Add(diffStatsBox);

            string currentSelectedFilePath = null;
            var openBtn = new Button(() =>
            {
                if (!string.IsNullOrEmpty(currentSelectedFilePath))
                {
                    var ide = VirtualIDEWindow.ActiveInstance ?? GetWindow<VirtualIDEWindow>();
                    ide.LoadFileAndJumpToLine(currentSelectedFilePath, 1);
                    ide.Focus();
                }
            }) { text = "Open in Editor" };
            openBtn.AddToClassList("git-diff-open-btn");
            openBtn.style.display = DisplayStyle.None;
            diffHeader.Add(openBtn);

            diffBottomPane.Add(diffHeader);

            var diffPlaceholder = new VisualElement();
            diffPlaceholder.AddToClassList("git-diff-placeholder");
            diffPlaceholder.style.flexGrow = 1;
            diffPlaceholder.style.alignItems = Align.Center;
            diffPlaceholder.style.justifyContent = Justify.Center;

            var diffPlaceholderIcon = new Label("⚖");
            diffPlaceholderIcon.style.fontSize = 28;
            diffPlaceholderIcon.style.marginBottom = 8;
            diffPlaceholderIcon.style.unityTextAlign = TextAnchor.MiddleCenter;
            diffPlaceholder.Add(diffPlaceholderIcon);

            var diffPlaceholderLbl = new Label("Select a changed file to view its diff");
            diffPlaceholderLbl.style.color = new Color(0.55f, 0.55f, 0.55f);
            diffPlaceholderLbl.style.fontSize = 11;
            diffPlaceholderLbl.style.unityTextAlign = TextAnchor.MiddleCenter;
            diffPlaceholder.Add(diffPlaceholderLbl);
            diffBottomPane.Add(diffPlaceholder);

            var diffScrollView = new ScrollView(ScrollViewMode.VerticalAndHorizontal);
            diffScrollView.style.flexGrow = 1;
            diffScrollView.style.flexShrink = 1;
            diffScrollView.style.minHeight = 0;
            diffScrollView.style.display = DisplayStyle.None;
            diffBottomPane.Add(diffScrollView);

            historySplit.Add(diffBottomPane);
            m_CommitDetailPanel.Add(historySplit);

            async void DisplayCommitFileDiff(string filePath, VisualElement rowEl)
            {
                m_SelectedCommitFileRow?.RemoveFromClassList("git-commit-file-row-selected");
                m_SelectedCommitFileRow = rowEl;
                rowEl?.AddToClassList("git-commit-file-row-selected");

                currentSelectedFilePath = filePath;
                diffFileNameLbl.text = Path.GetFileName(filePath);
                string dir = Path.GetDirectoryName(filePath)?.Replace('\\', '/') ?? "";
                diffFilePathLbl.text = string.IsNullOrEmpty(dir) ? "" : "/ " + dir;
                openBtn.style.display = File.Exists(Path.Combine(GitService.RepositoryRoot, filePath)) ? DisplayStyle.Flex : DisplayStyle.None;

                diffPlaceholder.style.display = DisplayStyle.None;
                diffScrollView.style.display = DisplayStyle.Flex;
                diffScrollView.Clear();

                var loadingLbl = new Label("Loading diff...");
                loadingLbl.AddToClassList("git-diff-empty-msg");
                diffScrollView.Add(loadingLbl);

                string diffText = await GitService.GetCommitFileDiffAsync(commit.Hash, filePath);
                RenderDiffIntoScrollView(diffScrollView, diffText, statsAdded, statsDeleted);
            }

            VisualElement firstRow = null;
            string firstFilePath = null;

            foreach (var (statusCode, filePath) in fileRows)
            {
                var fileRow = new VisualElement();
                fileRow.AddToClassList("git-commit-file-row");

                if (!string.IsNullOrEmpty(statusCode))
                {
                    Color badgeColor = statusCode[0] switch
                    {
                        'M' => new Color(0.9f, 0.75f, 0.45f),
                        'A' => new Color(0.45f, 0.78f, 0.55f),
                        'D' => new Color(0.9f, 0.4f, 0.4f),
                        'R' => new Color(0.4f, 0.7f, 0.9f),
                        _   => Color.gray
                    };
                    var badge = new Label(statusCode.Substring(0, 1));
                    badge.AddToClassList("git-badge");
                    badge.style.backgroundColor = badgeColor;
                    badge.style.marginRight = 8;
                    ApplyMonoFont(badge, bold: true);
                    fileRow.Add(badge);
                }

                var fileLbl = new Label(filePath);
                fileLbl.AddToClassList("git-commit-file-label");
                ApplyMonoFont(fileLbl);
                fileRow.Add(fileLbl);

                string curPath = filePath;
                fileRow.RegisterCallback<PointerDownEvent>(e =>
                {
                    if (e.clickCount == 2)
                    {
                        var ide = VirtualIDEWindow.ActiveInstance ?? GetWindow<VirtualIDEWindow>();
                        ide.LoadFileAndJumpToLine(curPath, 1);
                        ide.Focus();
                    }
                    else if (e.button == 0)
                    {
                        DisplayCommitFileDiff(curPath, fileRow);
                    }
                });

                commitFilesScrollView.Add(fileRow);

                if (firstRow == null)
                {
                    firstRow = fileRow;
                    firstFilePath = curPath;
                }
            }

            if (firstRow != null && firstFilePath != null)
            {
                DisplayCommitFileDiff(firstFilePath, firstRow);
            }
        }

        // ══════════════════════════════════════════════════════
        //  TAB 3 — Stashes
        // ══════════════════════════════════════════════════════

        private async void BuildStashesView()
        {
            if (m_ContentArea == null) return;
            m_ContentArea.Clear();

            var outer = new ScrollView();
            outer.style.flexGrow = 1;

            var container = new VisualElement();
            container.AddToClassList("git-stashes-container");

            // New stash bar
            var newStashBar = new VisualElement();
            newStashBar.AddToClassList("git-new-stash-bar");

            m_StashMessageField = new TextField { label = "" };
            m_StashMessageField.style.flexGrow = 1;

            var placeholder = new Label("Stash message (optional)...");
            placeholder.style.position = Position.Absolute;
            placeholder.style.left  = 6;
            placeholder.style.top   = 4;
            placeholder.style.color = new Color(0.48f, 0.48f, 0.48f);
            placeholder.style.fontSize = 11;
            placeholder.pickingMode = PickingMode.Ignore;
            m_StashMessageField.Add(placeholder);

            m_StashMessageField.RegisterValueChangedCallback(e =>
                placeholder.style.display = string.IsNullOrEmpty(e.newValue) ? DisplayStyle.Flex : DisplayStyle.None);

            newStashBar.Add(m_StashMessageField);

            var btnStash = new Button(async () =>
            {
                string msg = m_StashMessageField?.value ?? "";
                SetLoading(true, "Stashing changes...");
                bool ok = await GitService.StashSaveAsync(msg, true);
                SetLoading(false);
                if (ok)
                {
                    ShowToast("Changes stashed successfully.", ToastType.Success);
                    if (m_StashMessageField != null) m_StashMessageField.value = "";
                    BuildStashesView();
                }
            }) { text = "Stash Changes" };
            btnStash.AddToClassList("git-btn-primary-small");
            newStashBar.Add(btnStash);

            container.Add(newStashBar);
            outer.Add(container);
            m_ContentArea.Add(outer);

            // Load stashes
            SetLoading(true, "Loading stashes...");
            var stashes = await GitService.GetStashesAsync();
            SetLoading(false);

            if (stashes.Count == 0)
            {
                var emptyLbl = new Label("No saved stashes found.");
                emptyLbl.AddToClassList("git-stash-empty-label");
                container.Add(emptyLbl);
                return;
            }

            foreach (var s in stashes)
                container.Add(CreateStashCard(s));
        }

        private VisualElement CreateStashCard(GitStashInfo stash)
        {
            var card = new VisualElement();
            card.AddToClassList("git-stash-card");

            // Header
            var header = new VisualElement();
            header.AddToClassList("git-stash-card-header");

            var indexBadge = new Label($"stash@{{{stash.Index}}}");
            indexBadge.AddToClassList("git-stash-index-badge");
            ApplyMonoFont(indexBadge);
            header.Add(indexBadge);

            var msgLbl = new Label(stash.Message);
            msgLbl.AddToClassList("git-stash-message");
            header.Add(msgLbl);

            var timeLbl = new Label(stash.RelativeTime);
            timeLbl.AddToClassList("git-stash-time");
            header.Add(timeLbl);

            card.Add(header);

            // Actions
            var actions = new VisualElement();
            actions.AddToClassList("git-stash-card-actions");

            int idx = stash.Index;

            var btnApply = new Button(async () =>
            {
                SetLoading(true, "Applying stash...");
                var r = await GitService.ExecuteGitAsync($"stash apply stash@{{{idx}}}");
                SetLoading(false);
                if (r.Success)
                {
                    ShowToast("Stash applied (kept in stash list).", ToastType.Success);
                    await GitService.RefreshStatusAsync();
                    BuildStashesView();
                }
                else
                {
                    ShowToast("Failed to apply stash: " + r.Error, ToastType.Error);
                }
            }) { text = "Apply" };
            btnApply.AddToClassList("git-btn-secondary-small");
            actions.Add(btnApply);

            var btnPop = new Button(async () =>
            {
                SetLoading(true, "Popping stash...");
                bool ok = await GitService.StashPopAsync(idx);
                SetLoading(false);
                if (ok)
                {
                    ShowToast("Stash popped and removed from list.", ToastType.Success);
                    BuildStashesView();
                }
            }) { text = "Pop" };
            btnPop.AddToClassList("git-btn-primary-small");
            actions.Add(btnPop);

            var btnDrop = new Button(async () =>
            {
                if (EditorUtility.DisplayDialog("Drop Stash",
                    $"stash@{{{idx}}} will be permanently removed. Are you sure?", "Drop", "Cancel"))
                {
                    SetLoading(true, "Dropping stash...");
                    bool ok = await GitService.StashDropAsync(idx);
                    SetLoading(false);
                    if (ok)
                    {
                        ShowToast("Stash dropped.", ToastType.Info);
                        BuildStashesView();
                    }
                }
            }) { text = "Drop" };
            btnDrop.AddToClassList("git-btn-danger-small");
            actions.Add(btnDrop);

            card.Add(actions);
            return card;
        }

        // ══════════════════════════════════════════════════════
        //  Status update handler
        // ══════════════════════════════════════════════════════

        private void HandleStatusUpdated(GitRepositoryStatus status)
        {
            if (status.IsGitRepository && m_ContentArea == null)
            {
                CreateGUI();
                return;
            }
            if (!status.IsGitRepository && m_ContentArea != null)
            {
                CreateGUI();
                return;
            }

            UpdateTopBarState();
            UpdateOperationBanner(status);
            if (GetGitConfigWriteTime() != m_CachedRemotesConfigTime) _ = RefreshRemoteCacheAsync();

            // Tab badge
            if (m_NavTabsBar != null)
            {
                var changesTab = m_NavTabsBar.Q<Button>("tab-Changes");
                if (changesTab != null)
                {
                    int n = status.TotalChangesCount;
                    changesTab.text = n > 0 ? $"Changes  {n}" : "Changes";
                }
            }

            // Refresh changes tab if active
            if (m_ActiveTab == GitTab.Changes)
            {
                if (m_ChangesSplitView == null || m_ContentArea == null ||
                    !m_ContentArea.Contains(m_ChangesSplitView))
                {
                    BuildChangesView();
                }
                else
                {
                    PopulateChangesLists(status);
                }
            }
        }

        // ══════════════════════════════════════════════════════
        //  Helpers
        // ══════════════════════════════════════════════════════

        private static string GetInitials(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "?";
            var parts = name.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 2
                ? $"{parts[0][0]}{parts[^1][0]}".ToUpper()
                : name.Substring(0, Math.Min(2, name.Length)).ToUpper();
        }

        private static Color GetAvatarColor(string name)
        {
            int hash = 0;
            foreach (char c in name ?? "?") hash += c;
            return s_AvatarColors[Math.Abs(hash) % s_AvatarColors.Length];
        }

        private static Texture2D GetFileTypeIcon(string fileName)
        {
            string ext = Path.GetExtension(fileName ?? "").ToLower();
            string iconName = ext switch
            {
                ".cs"             => "cs Script Icon",
                ".png" or ".jpg" or ".jpeg" or ".tga" or ".psd" => "Texture Icon",
                ".prefab"         => "Prefab Icon",
                ".unity"          => "SceneAsset Icon",
                ".mat"            => "Material Icon",
                ".anim"           => "AnimationClip Icon",
                ".controller"     => "AnimatorController Icon",
                ".shader"         => "Shader Icon",
                ".asset"          => "ScriptableObject Icon",
                ".json" or ".xml" or ".yaml" or ".yml" => "TextAsset Icon",
                _                 => "DefaultAsset Icon"
            };

            return EditorGUIUtility.IconContent(iconName).image as Texture2D;
        }
    }
}
