using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.UI
{
    public class FindReplacePanel : VisualElement
    {
        private TextField m_FindField;
        private TextField m_ReplaceField;
        private VisualElement m_ReplaceRow;
        private Label m_MatchCountLabel;
        private Button m_ExpandToggleBtn;
        
        private bool m_IsReplaceExpanded = false;
        private bool m_MatchCase = false;
        
        public Action<string, bool> OnFindNext;
        public Action<string, bool> OnFindPrev;
        public Action<string, string, bool> OnReplace;
        public Action<string, string, bool> OnReplaceAll;
        public Action OnClose;
        public Action<string, bool> OnFindTextChanged;

        public FindReplacePanel()
        {
            // Panel container styling
            style.position = Position.Absolute;
            style.top = 10;
            style.right = 30;
            style.width = 340;
            style.backgroundColor = new Color(0.18f, 0.18f, 0.18f, 0.98f);
            style.borderBottomColor = new Color(0.1f, 0.1f, 0.1f);
            style.borderTopColor = new Color(0.1f, 0.1f, 0.1f);
            style.borderLeftColor = new Color(0.1f, 0.1f, 0.1f);
            style.borderRightColor = new Color(0.1f, 0.1f, 0.1f);
            style.borderBottomWidth = 1; style.borderTopWidth = 1; style.borderLeftWidth = 1; style.borderRightWidth = 1;
            style.borderTopLeftRadius = 4; style.borderTopRightRadius = 4; style.borderBottomLeftRadius = 4; style.borderBottomRightRadius = 4;
            style.paddingBottom = 6; style.paddingTop = 6; style.paddingLeft = 6; style.paddingRight = 6;
            style.flexDirection = FlexDirection.Row; // Left column (expand), Right column (find/replace)

            // --- Left Column (Expand Arrow) ---
            var leftCol = new VisualElement() { style = { width = 20, justifyContent = Justify.FlexStart, alignItems = Align.Center, paddingTop = 2 } };
            m_ExpandToggleBtn = CreateIconButton("▶", () => ToggleReplaceRow());
            m_ExpandToggleBtn.style.fontSize = 10;
            m_ExpandToggleBtn.style.color = new Color(0.7f, 0.7f, 0.7f);
            leftCol.Add(m_ExpandToggleBtn);

            // --- Right Column (Inputs) ---
            var rightCol = new VisualElement() { style = { flexGrow = 1, flexDirection = FlexDirection.Column } };

            // Find Row
            var findRow = new VisualElement() { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, marginBottom = 4 } };
            
            // Find Input Container (Fake Border)
            var findInputBox = new VisualElement() { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, flexGrow = 1, backgroundColor = new Color(0.12f, 0.12f, 0.12f), borderTopColor = new Color(0.2f, 0.2f, 0.2f), borderBottomColor = new Color(0.2f, 0.2f, 0.2f), borderLeftColor = new Color(0.2f, 0.2f, 0.2f), borderRightColor = new Color(0.2f, 0.2f, 0.2f), borderTopWidth = 1, borderBottomWidth = 1, borderLeftWidth = 1, borderRightWidth = 1, borderTopLeftRadius = 2, borderTopRightRadius = 2, borderBottomLeftRadius = 2, borderBottomRightRadius = 2, marginRight = 4 } };
            
            m_FindField = new TextField();
            m_FindField.style.flexGrow = 1; m_FindField.style.flexShrink = 1;
            m_FindField.style.backgroundColor = Color.clear;
            m_FindField.style.borderTopWidth = 0; m_FindField.style.borderBottomWidth = 0; m_FindField.style.borderLeftWidth = 0; m_FindField.style.borderRightWidth = 0;
            m_FindField.RegisterCallback<KeyDownEvent>(e => {
                if (e.keyCode == KeyCode.Return) { OnFindNext?.Invoke(m_FindField.value, m_MatchCase); e.StopPropagation(); }
                if (e.keyCode == KeyCode.Escape) { OnClose?.Invoke(); e.StopPropagation(); }
            });
            m_FindField.RegisterValueChangedCallback(e => {
                OnFindTextChanged?.Invoke(e.newValue, m_MatchCase);
            });
            findInputBox.Add(m_FindField);

            var matchCaseBtn = CreateIconButton("Aa", () => {
                m_MatchCase = !m_MatchCase;
                OnFindTextChanged?.Invoke(m_FindField.value, m_MatchCase);
            });
            matchCaseBtn.style.paddingLeft = 4; matchCaseBtn.style.paddingRight = 4;
            findInputBox.Add(matchCaseBtn);

            findRow.Add(findInputBox);

            m_MatchCountLabel = new Label("No results") { style = { color = new Color(0.5f, 0.5f, 0.5f), fontSize = 11, width = 60, unityTextAlign = TextAnchor.MiddleRight, marginRight = 4 } };
            findRow.Add(m_MatchCountLabel);

            findRow.Add(CreateIconButton("↑", () => OnFindPrev?.Invoke(m_FindField.value, m_MatchCase)));
            findRow.Add(CreateIconButton("↓", () => OnFindNext?.Invoke(m_FindField.value, m_MatchCase)));
            
            var closeBtn = CreateIconButton("✕", () => OnClose?.Invoke());
            closeBtn.style.marginLeft = 4;
            findRow.Add(closeBtn);

            // Replace Row
            m_ReplaceRow = new VisualElement() { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, display = DisplayStyle.None } };
            
            var replaceInputBox = new VisualElement() { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, flexGrow = 1, backgroundColor = new Color(0.12f, 0.12f, 0.12f), borderTopColor = new Color(0.2f, 0.2f, 0.2f), borderBottomColor = new Color(0.2f, 0.2f, 0.2f), borderLeftColor = new Color(0.2f, 0.2f, 0.2f), borderRightColor = new Color(0.2f, 0.2f, 0.2f), borderTopWidth = 1, borderBottomWidth = 1, borderLeftWidth = 1, borderRightWidth = 1, borderTopLeftRadius = 2, borderTopRightRadius = 2, borderBottomLeftRadius = 2, borderBottomRightRadius = 2, marginRight = 4 } };
            m_ReplaceField = new TextField();
            m_ReplaceField.style.flexGrow = 1; m_ReplaceField.style.flexShrink = 1;
            m_ReplaceField.style.backgroundColor = Color.clear;
            m_ReplaceField.style.borderTopWidth = 0; m_ReplaceField.style.borderBottomWidth = 0; m_ReplaceField.style.borderLeftWidth = 0; m_ReplaceField.style.borderRightWidth = 0;
            m_ReplaceField.RegisterCallback<KeyDownEvent>(e => {
                if (e.keyCode == KeyCode.Return) { OnReplace?.Invoke(m_FindField.value, m_ReplaceField.value, m_MatchCase); e.StopPropagation(); }
                if (e.keyCode == KeyCode.Escape) { OnClose?.Invoke(); e.StopPropagation(); }
            });
            replaceInputBox.Add(m_ReplaceField);
            
            m_ReplaceRow.Add(replaceInputBox);

            var btnRep = CreateIconButton("↺", () => OnReplace?.Invoke(m_FindField.value, m_ReplaceField.value, m_MatchCase));
            btnRep.tooltip = "Replace";
            var btnAll = CreateIconButton("↻", () => OnReplaceAll?.Invoke(m_FindField.value, m_ReplaceField.value, m_MatchCase));
            btnAll.tooltip = "Replace All";
            
            m_ReplaceRow.Add(btnRep);
            m_ReplaceRow.Add(btnAll);

            // Spacer for alignment with find row
            m_ReplaceRow.Add(new VisualElement() { style = { width = 24 } });

            rightCol.Add(findRow);
            rightCol.Add(m_ReplaceRow);

            Add(leftCol);
            Add(rightCol);

            // Set up match case button styling to reflect active state
            matchCaseBtn.schedule.Execute(() => {
                matchCaseBtn.style.backgroundColor = m_MatchCase ? new Color(0.3f, 0.3f, 0.3f) : Color.clear;
            }).Every(100);
        }

        private Button CreateIconButton(string icon, Action onClick)
        {
            var btn = new Button(onClick) { text = icon };
            btn.style.width = 20; btn.style.height = 20;
            btn.style.paddingBottom = 0; btn.style.paddingTop = 0; btn.style.paddingLeft = 0; btn.style.paddingRight = 0;
            btn.style.marginLeft = 1; btn.style.marginRight = 1;
            btn.style.backgroundColor = Color.clear;
            btn.style.borderTopWidth = 0; btn.style.borderBottomWidth = 0; btn.style.borderLeftWidth = 0; btn.style.borderRightWidth = 0;
            btn.style.color = new Color(0.8f, 0.8f, 0.8f);
            
            btn.RegisterCallback<PointerEnterEvent>(e => btn.style.backgroundColor = new Color(0.3f, 0.3f, 0.3f));
            btn.RegisterCallback<PointerLeaveEvent>(e => {
                // Don't clear if it's the active toggle (handled elsewhere)
                if (btn.text != "Aa" || !m_MatchCase) btn.style.backgroundColor = Color.clear;
            });
            return btn;
        }

        private void ToggleReplaceRow()
        {
            m_IsReplaceExpanded = !m_IsReplaceExpanded;
            m_ReplaceRow.style.display = m_IsReplaceExpanded ? DisplayStyle.Flex : DisplayStyle.None;
            m_ExpandToggleBtn.text = m_IsReplaceExpanded ? "▼" : "▶";
            if (m_IsReplaceExpanded) m_ReplaceField.Focus();
        }

        public void FocusFind() 
        { 
            m_FindField.Focus(); 
            schedule.Execute(() =>
            {
                m_FindField.Focus();
                m_FindField.SelectAll();
            }).StartingIn(10);
        }

        public void FocusReplace() 
        { 
            if (!m_IsReplaceExpanded) ToggleReplaceRow();
            m_ReplaceField.Focus(); 
            schedule.Execute(() =>
            {
                m_ReplaceField.Focus();
                m_ReplaceField.SelectAll();
            }).StartingIn(10);
        }

        public void SetFindText(string text)
        {
            if (m_FindField != null)
            {
                m_FindField.value = text;
                schedule.Execute(() =>
                {
                    m_FindField.Focus();
                    m_FindField.SelectAll();
                }).StartingIn(10);
            }
        }

        public string GetFindText() => m_FindField.value;
        public bool GetMatchCase() => m_MatchCase;

        public void UpdateMatchCount(int current, int total)
        {
            if (total == 0)
            {
                m_MatchCountLabel.text = "No results";
                m_MatchCountLabel.style.color = new Color(1f, 0.4f, 0.4f);
            }
            else
            {
                m_MatchCountLabel.text = $"{current} of {total}";
                m_MatchCountLabel.style.color = new Color(0.6f, 0.6f, 0.6f);
            }
        }
    }
}
