using System;
using System.Collections.Generic;
using System.Linq;
using FuzzyLogicLabs.FuzzyIDE.Editor.Core;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.UI
{
    public partial class VirtualIDEWindow
    {
        private VisualElement m_CompilationToolbarContainer;
        private Label m_PendingBadgeLabel;
        private DropdownField m_PolicyDropdown;
        private Button m_RecompileBtn;
        private Button m_ReloadDomainBtn;

        // Bottom Status Bar Elements
        private VisualElement m_StatusBar;
        private Label m_StatusDiagnosticsLabel;
        private Label m_StatusPendingLabel;
        private Label m_StatusCompileLabel;
        private Label m_StatusTelemetryLabel;
        private Label m_StatusCaretLabel;

        private static readonly List<string> s_PolicyOptions = new List<string>
        {
            "Unity Default",
            "Lock Domain Reload"
        };

        private void InitializeCompilationIntegration()
        {
            CompilationManager.OnCompilationStateChanged -= UpdateCompilationUIState;
            CompilationManager.OnCompilationStateChanged += UpdateCompilationUIState;

            CompilationManager.OnPolicyChanged -= UpdateCompilationUIState;
            CompilationManager.OnPolicyChanged += UpdateCompilationUIState;

            CompilationManager.OnReloadProblem -= OnReloadProblem;
            CompilationManager.OnReloadProblem += OnReloadProblem;

            ProjectDiagnosticsService.OnDiagnosticsUpdated -= UpdateStatusDiagnostics;
            ProjectDiagnosticsService.OnDiagnosticsUpdated += UpdateStatusDiagnostics;

            UpdateCompilationUIState();
            UpdateStatusDiagnostics();
        }

        private void InitializeCompilationToolbar(VisualElement toolbar)
        {
            // Right-aligned container: Add a flexible spacer first
            var spacer = new VisualElement();
            spacer.style.flexGrow = 1;
            toolbar.Add(spacer);

            m_CompilationToolbarContainer = new VisualElement();
            m_CompilationToolbarContainer.style.flexDirection = FlexDirection.Row;
            m_CompilationToolbarContainer.style.alignItems = Align.Center;
            m_CompilationToolbarContainer.style.marginRight = 6;

            // 1. Pending Files Badge (Modern clickable pill)
            m_PendingBadgeLabel = new Label("● 0 pending");
            m_PendingBadgeLabel.style.fontSize = 11;
            m_PendingBadgeLabel.style.color = new Color(0.95f, 0.75f, 0.25f); // Amber
            m_PendingBadgeLabel.style.backgroundColor = new Color(0.95f, 0.75f, 0.25f, 0.12f);
            m_PendingBadgeLabel.style.borderTopWidth = 1;
            m_PendingBadgeLabel.style.borderBottomWidth = 1;
            m_PendingBadgeLabel.style.borderLeftWidth = 1;
            m_PendingBadgeLabel.style.borderRightWidth = 1;
            m_PendingBadgeLabel.style.borderTopColor = new Color(0.95f, 0.75f, 0.25f, 0.3f);
            m_PendingBadgeLabel.style.borderBottomColor = new Color(0.95f, 0.75f, 0.25f, 0.3f);
            m_PendingBadgeLabel.style.borderLeftColor = new Color(0.95f, 0.75f, 0.25f, 0.3f);
            m_PendingBadgeLabel.style.borderRightColor = new Color(0.95f, 0.75f, 0.25f, 0.3f);
            m_PendingBadgeLabel.style.borderTopLeftRadius = 10;
            m_PendingBadgeLabel.style.borderBottomLeftRadius = 10;
            m_PendingBadgeLabel.style.borderTopRightRadius = 10;
            m_PendingBadgeLabel.style.borderBottomRightRadius = 10;
            m_PendingBadgeLabel.style.paddingLeft = 8;
            m_PendingBadgeLabel.style.paddingRight = 8;
            m_PendingBadgeLabel.style.paddingTop = 2;
            m_PendingBadgeLabel.style.paddingBottom = 2;
            m_PendingBadgeLabel.style.marginRight = 8;
            m_PendingBadgeLabel.style.cursor = GetResizeCursor(UnityEditor.MouseCursor.Link);
            m_PendingBadgeLabel.style.display = DisplayStyle.None;
            m_PendingBadgeLabel.tooltip = "Files changed on disk that are not yet compiled. Click to compile now (Ctrl+R).";
            m_PendingBadgeLabel.RegisterCallback<ClickEvent>(e => Recompile());
            m_CompilationToolbarContainer.Add(m_PendingBadgeLabel);

            // 2. Compilation Policy Dropdown (Header Right)
            int initialIdx = (int)CompilationManager.CurrentPolicy;
            if (initialIdx < 0 || initialIdx >= s_PolicyOptions.Count) initialIdx = 0; // Default to Unity Default

            m_PolicyDropdown = new DropdownField("", s_PolicyOptions, initialIdx)
            {
                tooltip = "Compilation & Domain Reload Policy:\n" +
                          "• Unity Default: Standard Unity auto-refresh & auto reload\n" +
                          "• Lock Domain Reload: Compiles in background; reload is paused until you click 'Reload Domain' (Ctrl+Shift+R)"
            };
            m_PolicyDropdown.style.width = 165;
            m_PolicyDropdown.style.height = 24;
            m_PolicyDropdown.style.fontSize = 11;
            m_PolicyDropdown.style.marginRight = 6;
            m_PolicyDropdown.RegisterValueChangedCallback(evt =>
            {
                int idx = s_PolicyOptions.IndexOf(evt.newValue);
                if (idx >= 0)
                {
                    CompilationManager.SetPolicy((CompilationPolicy)idx);
                }
            });
            m_CompilationToolbarContainer.Add(m_PolicyDropdown);

            // 3. Recompile / Sync Button (Modern blue action button)
            m_RecompileBtn = new Button(Recompile)
            {
                tooltip = "Sync modified files & compile (Ctrl+R)"
            };
            m_RecompileBtn.style.flexDirection = FlexDirection.Row;
            m_RecompileBtn.style.alignItems = Align.Center;
            m_RecompileBtn.style.height = 24;
            m_RecompileBtn.style.paddingLeft = 10;
            m_RecompileBtn.style.paddingRight = 10;
            m_RecompileBtn.style.backgroundColor = new Color(0.14f, 0.22f, 0.32f); // Refined steel blue
            m_RecompileBtn.style.borderTopWidth = 1;
            m_RecompileBtn.style.borderBottomWidth = 1;
            m_RecompileBtn.style.borderLeftWidth = 1;
            m_RecompileBtn.style.borderRightWidth = 1;
            m_RecompileBtn.style.borderTopColor = new Color(0.24f, 0.38f, 0.54f);
            m_RecompileBtn.style.borderBottomColor = new Color(0.24f, 0.38f, 0.54f);
            m_RecompileBtn.style.borderLeftColor = new Color(0.24f, 0.38f, 0.54f);
            m_RecompileBtn.style.borderRightColor = new Color(0.24f, 0.38f, 0.54f);
            m_RecompileBtn.style.borderTopLeftRadius = 4;
            m_RecompileBtn.style.borderBottomLeftRadius = 4;
            m_RecompileBtn.style.borderTopRightRadius = 4;
            m_RecompileBtn.style.borderBottomRightRadius = 4;
            m_RecompileBtn.style.marginRight = 4;

            var syncIcon = new Image { image = EditorGUIUtility.IconContent("d_Refresh").image };
            syncIcon.style.width = 12;
            syncIcon.style.height = 12;
            syncIcon.style.marginRight = 4;
            m_RecompileBtn.Add(syncIcon);

            var syncText = new Label("Compile");
            syncText.style.fontSize = 11;
            syncText.style.color = new Color(0.85f, 0.92f, 1.0f);
            m_RecompileBtn.Add(syncText);
            m_CompilationToolbarContainer.Add(m_RecompileBtn);

            // 4. Explicit Reload Domain Button (Modern warm amber action button)
            m_ReloadDomainBtn = new Button(ReloadDomain)
            {
                tooltip = "Force Unity to reload the AppDomain now (Ctrl+Shift+R)"
            };
            m_ReloadDomainBtn.style.flexDirection = FlexDirection.Row;
            m_ReloadDomainBtn.style.alignItems = Align.Center;
            m_ReloadDomainBtn.style.height = 24;
            m_ReloadDomainBtn.style.paddingLeft = 10;
            m_ReloadDomainBtn.style.paddingRight = 10;
            m_ReloadDomainBtn.style.backgroundColor = new Color(0.26f, 0.19f, 0.11f); // Refined warm amber
            m_ReloadDomainBtn.style.borderTopWidth = 1;
            m_ReloadDomainBtn.style.borderBottomWidth = 1;
            m_ReloadDomainBtn.style.borderLeftWidth = 1;
            m_ReloadDomainBtn.style.borderRightWidth = 1;
            m_ReloadDomainBtn.style.borderTopColor = new Color(0.48f, 0.34f, 0.20f);
            m_ReloadDomainBtn.style.borderBottomColor = new Color(0.48f, 0.34f, 0.20f);
            m_ReloadDomainBtn.style.borderLeftColor = new Color(0.48f, 0.34f, 0.20f);
            m_ReloadDomainBtn.style.borderRightColor = new Color(0.48f, 0.34f, 0.20f);
            m_ReloadDomainBtn.style.borderTopLeftRadius = 4;
            m_ReloadDomainBtn.style.borderBottomLeftRadius = 4;
            m_ReloadDomainBtn.style.borderTopRightRadius = 4;
            m_ReloadDomainBtn.style.borderBottomRightRadius = 4;

            var reloadIcon = new Image { image = EditorGUIUtility.IconContent("d_RotateTool").image ?? EditorGUIUtility.IconContent("d_Refresh").image };
            reloadIcon.style.width = 12;
            reloadIcon.style.height = 12;
            reloadIcon.style.marginRight = 4;
            m_ReloadDomainBtn.Add(reloadIcon);

            var reloadText = new Label("Reload Domain");
            reloadText.style.fontSize = 11;
            reloadText.style.color = new Color(0.98f, 0.88f, 0.70f);
            m_ReloadDomainBtn.Add(reloadText);
            m_CompilationToolbarContainer.Add(m_ReloadDomainBtn);

            toolbar.Add(m_CompilationToolbarContainer);
        }

        private void InitializeStatusBar(VisualElement root)
        {
            m_StatusBar = new VisualElement();
            m_StatusBar.name = "fuzzy-status-bar";
            m_StatusBar.style.height = 24;
            m_StatusBar.style.flexShrink = 0;
            m_StatusBar.style.flexDirection = FlexDirection.Row;
            m_StatusBar.style.alignItems = Align.Center;
            m_StatusBar.style.backgroundColor = new Color(0.12f, 0.12f, 0.13f); // #1E1E1E
            m_StatusBar.style.borderTopWidth = 1;
            m_StatusBar.style.borderTopColor = new Color(0.22f, 0.22f, 0.22f);
            m_StatusBar.style.paddingLeft = 10;
            m_StatusBar.style.paddingRight = 10;

            // --- Left Section: Diagnostics & Pending Count ---
            m_StatusDiagnosticsLabel = new Label("✔ 0 Errors");
            m_StatusDiagnosticsLabel.style.fontSize = 11;
            m_StatusDiagnosticsLabel.style.color = new Color(0.6f, 0.6f, 0.6f);
            m_StatusDiagnosticsLabel.style.marginRight = 15;
            m_StatusDiagnosticsLabel.tooltip = "Click to view Diagnostics (Right-click to export to Fuzzy Notes)";
            m_StatusDiagnosticsLabel.RegisterCallback<ContextClickEvent>(e =>
            {
                var menu = new GenericMenu();
                menu.AddItem(new GUIContent("Create Bug Notes in Fuzzy Notes"), false, () =>
                {
                    if (m_ActivePane?.Viewer?.CurrentFilePath != null)
                    {
                        var diag = ProjectDiagnosticsService.GetFileDiagnostics(m_ActivePane.Viewer.CurrentFilePath);
                        if (diag != null && diag.Items != null && diag.Items.Count > 0)
                        {
                            int added = FuzzyLogicLabs.FuzzyIDE.Editor.Notes.Integration.DiagnosticsNoteBridge.AddDiagnostics(
                                m_ActivePane.Viewer.CurrentFilePath,
                                diag.Items.Select(i => (i.Message, i.Line, i.IsError)),
                                m_ActivePane.Viewer.GetLines(),
                                out var tracker, out int revealLine);
                            FuzzyLogicLabs.FuzzyIDE.Editor.Notes.UI.FuzzyNotesWindow.OpenNote(tracker, revealLine);
                            if (added == 0) ShowNotification(new GUIContent("Already in the Bug Tracker note"));
                        }
                    }
                });
                menu.ShowAsContext();
                e.StopPropagation();
            });
            m_StatusBar.Add(m_StatusDiagnosticsLabel);

            m_StatusPendingLabel = new Label("");
            m_StatusPendingLabel.style.fontSize = 11;
            m_StatusPendingLabel.style.color = new Color(0.95f, 0.75f, 0.25f);
            m_StatusPendingLabel.style.marginRight = 15;
            m_StatusPendingLabel.style.cursor = GetResizeCursor(UnityEditor.MouseCursor.Link);
            m_StatusPendingLabel.tooltip = "Uncompiled changes. Click to compile now (Ctrl+R).";
            m_StatusPendingLabel.RegisterCallback<ClickEvent>(e => Recompile());
            m_StatusBar.Add(m_StatusPendingLabel);

            // Flexible Middle Spacer
            var statusSpacer = new VisualElement();
            statusSpacer.style.flexGrow = 1;
            m_StatusBar.Add(statusSpacer);

            // --- Right Section: Compile State, Stopwatch, Caret Position ---
            m_StatusCompileLabel = new Label("Ready");
            m_StatusCompileLabel.style.fontSize = 11;
            m_StatusCompileLabel.style.color = new Color(0.6f, 0.8f, 0.6f);
            m_StatusCompileLabel.style.marginRight = 15;
            m_StatusCompileLabel.RegisterCallback<ClickEvent>(e =>
            {
                if (CompilationManager.State == CompileState.Failed) ShowProblems();
                else if (CompilationManager.State == CompileState.CompiledAwaitingReload) ReloadDomain();
            });
            m_StatusBar.Add(m_StatusCompileLabel);

            m_StatusTelemetryLabel = new Label("");
            m_StatusTelemetryLabel.style.fontSize = 11;
            m_StatusTelemetryLabel.style.color = new Color(0.55f, 0.55f, 0.55f);
            m_StatusTelemetryLabel.style.marginRight = 15;
            m_StatusBar.Add(m_StatusTelemetryLabel);

            m_StatusCaretLabel = new Label("Ln 1, Col 1");
            m_StatusCaretLabel.style.fontSize = 11;
            m_StatusCaretLabel.style.color = new Color(0.65f, 0.65f, 0.65f);
            m_StatusCaretLabel.style.minWidth = 75;
            m_StatusBar.Add(m_StatusCaretLabel);

            root.Add(m_StatusBar);
            ExtendStatusBar();
        }

        public void UpdateCompilationUIState()
        {
            m_LastCompilingCheck = CompilationManager.IsCompiling;
            var policy = CompilationManager.CurrentPolicy;

            // Sync Dropdown
            if (m_PolicyDropdown != null)
            {
                int policyIdx = (int)policy;
                if (policyIdx >= 0 && policyIdx < s_PolicyOptions.Count)
                {
                    m_PolicyDropdown.SetValueWithoutNotify(s_PolicyOptions[policyIdx]);
                }
            }

            int pendingCount = CompilationManager.PendingFilesCount;

            // Header Pending Badge
            if (m_PendingBadgeLabel != null)
            {
                if (pendingCount > 0)
                {
                    m_PendingBadgeLabel.text = $"● {pendingCount} pending";
                    m_PendingBadgeLabel.style.display = DisplayStyle.Flex;
                }
                else
                {
                    m_PendingBadgeLabel.style.display = DisplayStyle.None;
                }
            }

            // Status Bar Pending
            if (m_StatusPendingLabel != null)
            {
                if (pendingCount > 0)
                {
                    m_StatusPendingLabel.text = $"● {pendingCount} uncompiled (Click to sync)";
                    m_StatusPendingLabel.style.display = DisplayStyle.Flex;
                }
                else
                {
                    m_StatusPendingLabel.style.display = DisplayStyle.None;
                }
            }

            // Status Bar State
            if (m_StatusCompileLabel != null)
            {
                string text, tooltip;
                Color color;
                switch (CompilationManager.State)
                {
                    case CompileState.Compiling:
                        text = CompilationManager.ReloadWhenCompiled ? "\u27F3 Compiling, then reloading..." : "\u27F3 Compiling...";
                        tooltip = "Unity is compiling scripts.";
                        color = new Color(0.35f, 0.75f, 1.0f);
                        break;
                    case CompileState.Reloading:
                        text = "\u27F3 Reloading Domain...";
                        tooltip = "Loading the new assemblies.";
                        color = new Color(0.95f, 0.75f, 0.25f);
                        break;
                    case CompileState.Failed:
                        int errors = CompilationManager.CompileErrorCount;
                        text = $"\u2716 Compile failed ({errors} error{(errors == 1 ? "" : "s")}) \u2014 reload blocked";
                        tooltip = (CompilationManager.FirstCompileError ?? "") + "\n\nUnity keeps running the previously loaded code until the errors are fixed. Click to open Problems.";
                        color = new Color(1f, 0.4f, 0.4f);
                        break;
                    case CompileState.CompiledAwaitingReload:
                        text = EditorApplication.isPlaying ? "\u25CF Compiled \u2014 applies after Play Mode" : "\u25CF Compiled \u2014 Reload Domain to apply";
                        tooltip = "New code is built but not loaded yet (Lock Domain Reload). Ctrl+Shift+R loads it.";
                        color = new Color(0.95f, 0.75f, 0.25f);
                        break;
                    case CompileState.ChangesPending:
                        text = "\u25CF Scripts changed";
                        tooltip = CompilationManager.IsReloadLocked
                            ? "Changes are not compiled yet. Compile (Ctrl+R) builds them; Reload Domain (Ctrl+Shift+R) builds and loads them."
                            : "Changes are not compiled yet; Unity compiles them when it refreshes (or press Ctrl+R).";
                        color = new Color(0.95f, 0.75f, 0.25f);
                        break;
                    default:
                        text = CompilationManager.IsReloadLocked ? "\u2714 Up to date \u00B7 \uD83D\uDD12 reload locked" : "\u2714 Up to date";
                        tooltip = CompilationManager.IsReloadLocked
                            ? "Loaded code matches the scripts. New builds wait for Reload Domain (Lock Domain Reload policy)."
                            : "Loaded code matches the scripts.";
                        color = new Color(0.6f, 0.8f, 0.6f);
                        break;
                }
                m_StatusCompileLabel.text = text;
                m_StatusCompileLabel.tooltip = tooltip;
                m_StatusCompileLabel.style.color = color;
            }

            // Buttons follow the state: compiling can't be restarted, and a waiting build lights up Reload.
            if (m_RecompileBtn != null) m_RecompileBtn.SetEnabled(CompilationManager.State != CompileState.Compiling && CompilationManager.State != CompileState.Reloading);
            if (m_ReloadDomainBtn != null)
            {
                bool highlight = CompilationManager.State == CompileState.CompiledAwaitingReload;
                m_ReloadDomainBtn.SetEnabled(CompilationManager.State != CompileState.Reloading);
                m_ReloadDomainBtn.style.backgroundColor = highlight ? new Color(0.55f, 0.36f, 0.12f) : new Color(0.26f, 0.19f, 0.11f);
                var border = highlight ? new Color(0.98f, 0.72f, 0.3f) : new Color(0.48f, 0.34f, 0.20f);
                m_ReloadDomainBtn.style.borderTopColor = border;
                m_ReloadDomainBtn.style.borderBottomColor = border;
                m_ReloadDomainBtn.style.borderLeftColor = border;
                m_ReloadDomainBtn.style.borderRightColor = border;
            }

            // Telemetry
            if (m_StatusTelemetryLabel != null)
            {
                double cDur = CompilationManager.LastCompileDuration;
                double rDur = CompilationManager.LastDomainReloadDuration;
                if (cDur > 0 || rDur > 0)
                {
                    m_StatusTelemetryLabel.text = $"⏱ Compile: {cDur:F1}s | Domain: {rDur:F1}s";
                }
                else
                {
                    m_StatusTelemetryLabel.text = "";
                }
            }

            m_StatusCompileLabel?.MarkDirtyRepaint();
            m_StatusBar?.MarkDirtyRepaint();
            rootVisualElement?.MarkDirtyRepaint();
            Repaint();
        }

        private void OnReloadProblem(string message)
        {
            ShowNotification(new GUIContent(message), 4);
            Debug.LogWarning("[FuzzyIDE] " + message);
            if (CompilationManager.State == CompileState.Failed) ShowProblems();
        }

        public void UpdateStatusDiagnostics()
        {
            if (m_StatusDiagnosticsLabel == null) return;

            var (totalErrors, totalWarnings) = ProjectDiagnosticsService.GetTotalDiagnostics();

            if (totalErrors > 0)
            {
                m_StatusDiagnosticsLabel.text = $"✖ {totalErrors} Error{(totalErrors > 1 ? "s" : "")}  ⚠ {totalWarnings}";
                m_StatusDiagnosticsLabel.style.color = new Color(0.95f, 0.35f, 0.35f); // Red
            }
            else if (totalWarnings > 0)
            {
                m_StatusDiagnosticsLabel.text = $"⚠ {totalWarnings} Warning{(totalWarnings > 1 ? "s" : "")}";
                m_StatusDiagnosticsLabel.style.color = new Color(0.95f, 0.75f, 0.25f); // Yellow
            }
            else
            {
                m_StatusDiagnosticsLabel.text = "✔ 0 Errors";
                m_StatusDiagnosticsLabel.style.color = new Color(0.55f, 0.75f, 0.55f);
            }
        }

        public void UpdateStatusCaret(int line, int col)
        {
            if (m_StatusCaretLabel != null)
            {
                m_StatusCaretLabel.text = $"Ln {line}, Col {col}" + SelectionSuffix();
            }
        }
    }
}
