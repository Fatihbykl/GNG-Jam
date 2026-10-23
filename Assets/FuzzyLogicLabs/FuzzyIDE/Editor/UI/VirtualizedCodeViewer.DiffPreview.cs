using System;
using System.Collections.Generic;
using System.Linq;
using FuzzyLogicLabs.FuzzyIDE.Editor.Core;
using FuzzyLogicLabs.FuzzyIDE.Editor.Git;
using FuzzyLogicLabs.FuzzyIDE.Editor.Roslyn;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.UI
{
    public partial class VirtualizedCodeViewer
    {
        public void ShowDiffPreview(List<IEditCommand> cmds, int startLine, List<string> deletedLines, int addedLinesCount, Action onAccept = null, Action onReject = null)
        {
            m_PendingDiff = new PendingDiff
            {
                Commands = cmds,
                StartLine = startLine,
                AddedLinesCount = addedLinesCount,
                DeletedLines = deletedLines,
                OnAccept = onAccept,
                OnReject = onReject
            };

            ExecuteHistory(cmds);
            
            if (m_DiffOverlay == null)
            {
                m_DiffOverlay = new VisualElement();
                m_DiffOverlay.style.position = Position.Absolute;
                m_DiffOverlay.style.top = 10;
                m_DiffOverlay.style.right = 30;
                m_DiffOverlay.style.flexDirection = FlexDirection.Row;
                m_DiffOverlay.style.backgroundColor = new Color(0.15f, 0.15f, 0.15f, 0.9f);
                m_DiffOverlay.style.paddingLeft = 10;
                m_DiffOverlay.style.paddingRight = 10;
                m_DiffOverlay.style.paddingTop = 5;
                m_DiffOverlay.style.paddingBottom = 5;
                m_DiffOverlay.style.borderBottomLeftRadius = 5;
                m_DiffOverlay.style.borderBottomRightRadius = 5;
                m_DiffOverlay.style.borderTopLeftRadius = 5;
                m_DiffOverlay.style.borderTopRightRadius = 5;

                m_DiffOverlay.Add(new Label("AI Changes") { style = { unityFontStyleAndWeight = FontStyle.Bold, alignSelf = Align.Center, marginRight = 10 } });
                
                var acceptBtn = new Button(AcceptDiff) { text = "Accept" };
                acceptBtn.style.backgroundColor = new Color(0.2f, 0.6f, 0.2f);
                acceptBtn.style.color = Color.white;
                
                var rejectBtn = new Button(RejectDiff) { text = "Reject" };
                rejectBtn.style.backgroundColor = new Color(0.6f, 0.2f, 0.2f);
                rejectBtn.style.color = Color.white;
                
                m_DiffOverlay.Add(acceptBtn);
                m_DiffOverlay.Add(rejectBtn);
                
                this.Add(m_DiffOverlay);
            }
            
            m_DiffOverlay.style.display = DisplayStyle.Flex;
            RecalculateVisibleLines();
        }

        public void AcceptDiff()
        {
            var acceptAction = m_PendingDiff?.OnAccept;
            m_PendingDiff = null;
            if (m_DiffOverlay != null) m_DiffOverlay.style.display = DisplayStyle.None;
            RecalculateVisibleLines();
            OnTextChanged?.Invoke();
            acceptAction?.Invoke();
        }

        public void RejectDiff()
        {
            if (m_PendingDiff != null)
            {
                var rejectAction = m_PendingDiff.OnReject;
                History.Undo();
                m_PendingDiff = null;
                if (m_DiffOverlay != null) m_DiffOverlay.style.display = DisplayStyle.None;
                SetLines(m_Lines, false);
                OnTextChanged?.Invoke();
                rejectAction?.Invoke();
            }
        }

        public void TriggerExternalReload()
        {
            TriggerCodeAnalysis();
            ScheduleGitDiff(true);
            ScheduleNoteMarkers(true);
            RefreshListView();
        }

        public void ShowConflictBanner(Action onReloadFromDisk, Action onKeepLocal)
        {
            if (m_ConflictBanner == null)
            {
                m_ConflictBanner = new VisualElement();
                m_ConflictBanner.style.position = Position.Absolute;
                m_ConflictBanner.style.top = 8;
                m_ConflictBanner.style.left = 40;
                m_ConflictBanner.style.right = 40;
                m_ConflictBanner.style.flexDirection = FlexDirection.Row;
                m_ConflictBanner.style.alignItems = Align.Center;
                m_ConflictBanner.style.backgroundColor = new Color(0.28f, 0.16f, 0.08f, 0.96f);
                m_ConflictBanner.style.borderTopWidth = 1;
                m_ConflictBanner.style.borderBottomWidth = 1;
                m_ConflictBanner.style.borderLeftWidth = 1;
                m_ConflictBanner.style.borderRightWidth = 1;
                m_ConflictBanner.style.borderTopColor = new Color(0.85f, 0.55f, 0.15f, 0.9f);
                m_ConflictBanner.style.borderBottomColor = new Color(0.85f, 0.55f, 0.15f, 0.9f);
                m_ConflictBanner.style.borderLeftColor = new Color(0.85f, 0.55f, 0.15f, 0.9f);
                m_ConflictBanner.style.borderRightColor = new Color(0.85f, 0.55f, 0.15f, 0.9f);
                m_ConflictBanner.style.borderTopLeftRadius = 6;
                m_ConflictBanner.style.borderTopRightRadius = 6;
                m_ConflictBanner.style.borderBottomLeftRadius = 6;
                m_ConflictBanner.style.borderBottomRightRadius = 6;
                m_ConflictBanner.style.paddingLeft = 12;
                m_ConflictBanner.style.paddingRight = 12;
                m_ConflictBanner.style.paddingTop = 6;
                m_ConflictBanner.style.paddingBottom = 6;

                this.Add(m_ConflictBanner);
            }

            m_ConflictBanner.Clear();

            var warnIcon = new Label("⚠️")
            {
                style =
                {
                    marginRight = 8,
                    fontSize = 14
                }
            };
            m_ConflictBanner.Add(warnIcon);

            var textLabel = new Label("Bu dosya harici bir program (CLI/editör) tarafından diskte değiştirildi.")
            {
                style =
                {
                    color = new Color(0.95f, 0.88f, 0.80f),
                    unityFontStyleAndWeight = FontStyle.Bold,
                    fontSize = 11,
                    flexGrow = 1
                }
            };
            m_ConflictBanner.Add(textLabel);

            var reloadBtn = new Button(() =>
            {
                DismissConflictBanner();
                onReloadFromDisk?.Invoke();
            })
            {
                text = "Diskten Yenile"
            };
            reloadBtn.style.backgroundColor = new Color(0.20f, 0.45f, 0.80f);
            reloadBtn.style.color = Color.white;
            reloadBtn.style.marginRight = 6;
            m_ConflictBanner.Add(reloadBtn);

            var keepBtn = new Button(() =>
            {
                DismissConflictBanner();
                onKeepLocal?.Invoke();
            })
            {
                text = "Yerel Kodu Koru"
            };
            keepBtn.style.backgroundColor = new Color(0.35f, 0.35f, 0.38f);
            keepBtn.style.color = Color.white;
            m_ConflictBanner.Add(keepBtn);

            m_ConflictBanner.style.display = DisplayStyle.Flex;
        }

        public void DismissConflictBanner()
        {
            if (m_ConflictBanner != null)
            {
                m_ConflictBanner.style.display = DisplayStyle.None;
            }
        }
    }
}
