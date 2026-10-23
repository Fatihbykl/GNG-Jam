using System;
using System.Collections.Generic;
using System.Linq;
using FuzzyLogicLabs.FuzzyIDE.Editor.Core;
using FuzzyLogicLabs.FuzzyIDE.Editor.Roslyn;
using UnityEngine;
using UnityEngine.UIElements;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.UI
{
    public class AutoCompletePopup : VisualElement
    {
        private ListView m_ListView;
        private List<CompletionItem> m_AllSuggestions = new List<CompletionItem>();
        private List<CompletionItem> m_Suggestions = new List<CompletionItem>();
        private string m_CurrentPrefix = "";
        public Action<CompletionItem> OnSuggestionAccepted;

        private VisualElement m_TooltipPanel;
        private Label m_TooltipTitle;
        private Label m_TooltipDescription;

        public AutoCompletePopup()
        {
            name = "auto-complete-popup";
            style.position = Position.Absolute;
            style.backgroundColor = new Color(0.15f, 0.15f, 0.15f, 1f);
            style.borderLeftColor = style.borderRightColor = style.borderTopColor = style.borderBottomColor = new Color(0.3f, 0.3f, 0.3f, 1f);
            style.borderLeftWidth = style.borderRightWidth = style.borderTopWidth = style.borderBottomWidth = 1f;
            style.width = 250f;
            style.width = 280f;
            style.height = 180f;
            style.display = DisplayStyle.None;
            RegisterCallback<GeometryChangedEvent>(e => PlaceTooltipPanel());

            m_TooltipPanel = new VisualElement();
            m_TooltipPanel.name = "tooltip-panel";
            m_TooltipPanel.style.position = Position.Absolute;
            m_TooltipPanel.style.left = 282f;
            m_TooltipPanel.style.top = 0f;
            m_TooltipPanel.style.width = 300f;
            m_TooltipPanel.style.backgroundColor = new Color(0.15f, 0.15f, 0.15f, 1f);
            m_TooltipPanel.style.borderLeftColor = m_TooltipPanel.style.borderRightColor = m_TooltipPanel.style.borderTopColor = m_TooltipPanel.style.borderBottomColor = new Color(0.3f, 0.3f, 0.3f, 1f);
            m_TooltipPanel.style.borderLeftWidth = m_TooltipPanel.style.borderRightWidth = m_TooltipPanel.style.borderTopWidth = m_TooltipPanel.style.borderBottomWidth = 1f;
            m_TooltipPanel.style.paddingLeft = m_TooltipPanel.style.paddingRight = m_TooltipPanel.style.paddingTop = m_TooltipPanel.style.paddingBottom = 6;
            m_TooltipPanel.style.display = DisplayStyle.None;

            m_TooltipTitle = new Label();
            m_TooltipTitle.style.fontSize = 12;
            m_TooltipTitle.style.color = new Color(0.9f, 0.9f, 0.9f, 1f);
            m_TooltipTitle.enableRichText = true;
            m_TooltipTitle.style.whiteSpace = WhiteSpace.Normal;
            m_TooltipPanel.Add(m_TooltipTitle);

            m_TooltipDescription = new Label();
            m_TooltipDescription.style.fontSize = 11;
            m_TooltipDescription.style.color = new Color(0.7f, 0.7f, 0.7f, 1f);
            m_TooltipDescription.enableRichText = true;
            m_TooltipDescription.style.whiteSpace = WhiteSpace.Normal;
            m_TooltipDescription.style.marginTop = 6;
            m_TooltipPanel.Add(m_TooltipDescription);

            Add(m_TooltipPanel);

            m_ListView = new ListView();
            m_ListView.style.flexGrow = 1;
            m_ListView.itemsSource = m_Suggestions;
            
            // Modern UI Toolkit API for Lists
            m_ListView.fixedItemHeight = 22f;
            
            m_ListView.makeItem = () => 
            {
                var container = new VisualElement();
                container.style.flexDirection = FlexDirection.Row;
                container.style.alignItems = Align.Center;

                var icon = new Label();
                icon.name = "icon";
                icon.style.width = 16;
                icon.style.height = 16;
                icon.style.marginRight = 4;
                icon.style.marginLeft = 4;
                icon.style.unityTextAlign = TextAnchor.MiddleCenter;
                icon.style.fontSize = 11;
                icon.style.borderTopLeftRadius = 3;
                icon.style.borderTopRightRadius = 3;
                icon.style.borderBottomLeftRadius = 3;
                icon.style.borderBottomRightRadius = 3;
                container.Add(icon);

                var label = new Label();
                label.name = "text";
                label.enableRichText = true;
                label.style.color = new Color(0.85f, 0.85f, 0.85f, 1f);
                label.style.paddingLeft = 2;
                label.style.paddingTop = 2;
                label.style.paddingBottom = 2;
                label.style.fontSize = 13;
                container.Add(label);

                var nsHint = new Label();
                nsHint.name = "nshint";
                nsHint.style.color = new Color(0.5f, 0.5f, 0.5f, 1f);
                nsHint.style.paddingLeft = 6;
                nsHint.style.fontSize = 10;
                nsHint.style.unityTextAlign = TextAnchor.MiddleRight;
                nsHint.style.flexGrow = 1;
                container.Add(nsHint);

                return container;
            };
            
            m_ListView.bindItem = (e, i) => 
            {
                var item = m_Suggestions[i];
                var label = e.Q<Label>("text");

                string displayText = item.Name;
                if (!string.IsNullOrEmpty(m_CurrentPrefix))
                {
                    int matchIndex = displayText.IndexOf(m_CurrentPrefix, StringComparison.OrdinalIgnoreCase);
                    if (matchIndex >= 0)
                    {
                        string before = displayText.Substring(0, matchIndex);
                        string match = displayText.Substring(matchIndex, m_CurrentPrefix.Length);
                        string after = displayText.Substring(matchIndex + m_CurrentPrefix.Length);
                        displayText = $"{before}<color=#ffffff><b>{match}</b></color>{after}";
                    }
                }
                label.text = displayText;

                var nsHint = e.Q<Label>("nshint");
                if (nsHint != null)
                {
                    nsHint.text = !string.IsNullOrEmpty(item.NamespaceHint) ? item.NamespaceHint : "";
                }

                var icon = e.Q<Label>("icon");
                SetupIcon(icon, item.Kind);
            };
            
            m_ListView.selectionType = SelectionType.Single;
            
            m_ListView.itemsChosen += (items) => 
            {
                foreach(var item in items)
                {
                    ConfirmSelection();
                    break;
                }
            };
            m_ListView.selectionChanged += OnSelectionChanged;

            Add(m_ListView);
        }

        private void OnSelectionChanged(IEnumerable<object> selection)
        {
            var enumerator = selection.GetEnumerator();
            if (enumerator.MoveNext())
            {
                var item = (CompletionItem)enumerator.Current;
                ShowTooltip(item);
            }
            else
            {
                m_TooltipPanel.style.display = DisplayStyle.None;
            }
        }

        private void ShowTooltip(CompletionItem item)
        {
            if (string.IsNullOrEmpty(item.TooltipText) && string.IsNullOrEmpty(item.SignatureText))
            {
                m_TooltipPanel.style.display = DisplayStyle.None;
                return;
            }

            m_TooltipTitle.text = !string.IsNullOrEmpty(item.SignatureText) ? item.SignatureText : item.Name;
            
            if (!string.IsNullOrEmpty(item.TooltipText))
            {
                m_TooltipDescription.text = item.TooltipText;
                m_TooltipDescription.style.display = DisplayStyle.Flex;
            }
            else
            {
                m_TooltipDescription.style.display = DisplayStyle.None;
            }

            m_TooltipPanel.style.display = DisplayStyle.Flex;
        }

        public bool IsVisible => style.display == DisplayStyle.Flex;

        /// <summary>Opens the list next to <paramref name="anchorWorld"/> (the word being completed).</summary>
        public void Show(Rect anchorWorld, List<CompletionItem> suggestions)
        {
            m_CurrentPrefix = "";
            m_AllSuggestions.Clear();
            m_AllSuggestions.AddRange(suggestions);

            m_Suggestions.Clear();
            m_Suggestions.AddRange(suggestions);

            m_ListView.RefreshItems();

            if (m_Suggestions.Count > 0)
                m_ListView.selectedIndex = 0;

            PopupPlacement.Show(this, anchorWorld, preferAbove: false, minHeight: 80f);
        }

        // The details panel goes on whichever side of the list has room.
        private void PlaceTooltipPanel()
        {
            if (panel == null || float.IsNaN(worldBound.xMax)) return;
            float windowWidth = panel.visualTree.layout.width;
            bool fitsRight = worldBound.xMax + 2f + m_TooltipPanel.resolvedStyle.width <= windowWidth;
            m_TooltipPanel.style.left = fitsRight ? 282f : -302f;
        }

        public void Hide()
        {
            style.display = DisplayStyle.None;
        }

        public static int CalculateLevenshteinDistance(string source, string target)
        {
            if (string.IsNullOrEmpty(source)) return string.IsNullOrEmpty(target) ? 0 : target.Length;
            if (string.IsNullOrEmpty(target)) return source.Length;

            int sLen = source.Length;
            int tLen = target.Length;

            // Two 1D arrays instead of int[,] matrix prevents 2D heap allocation per comparison
            int[] v0 = new int[tLen + 1];
            int[] v1 = new int[tLen + 1];

            for (int j = 0; j <= tLen; j++) v0[j] = j;

            for (int i = 0; i < sLen; i++)
            {
                v1[0] = i + 1;
                char sChar = char.ToLowerInvariant(source[i]);

                for (int j = 0; j < tLen; j++)
                {
                    int cost = (sChar == char.ToLowerInvariant(target[j])) ? 0 : 1;
                    int min1 = v0[j + 1] + 1;
                    int min2 = v1[j] + 1;
                    int min3 = v0[j] + cost;
                    v1[j + 1] = Math.Min(Math.Min(min1, min2), min3);
                }

                for (int j = 0; j <= tLen; j++) v0[j] = v1[j];
            }
            return v0[tLen];
        }

        public void Filter(string prefix)
        {
            m_CurrentPrefix = prefix;
            m_Suggestions.Clear();
            var smart = CodeIDESettings.Current.SmartCode;
            var addedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(prefix))
            {
                m_Suggestions.AddRange(m_AllSuggestions);
                // Frequently accepted names first; OrderBy is stable, so the rest keeps the service's order.
                if (smart.LearnCompletionRanking)
                {
                    var ranked = m_Suggestions.OrderByDescending(item => CompletionHistory.GetCount(item.Name)).ToList();
                    m_Suggestions.Clear();
                    m_Suggestions.AddRange(ranked);
                }
            }
            else
            {
                // Exact > prefix > CamelCase humps ("gCo" = GetComponent) > substring; ties go to frequent picks.
                var scored = new List<(int score, int learned, int order, CompletionItem item)>();
                for (int i = 0; i < m_AllSuggestions.Count; i++)
                {
                    var s = m_AllSuggestions[i];
                    int score = CompletionMatcher.Score(s.Name, prefix, smart.CamelCaseCompletion);
                    if (score < 0 || !addedNames.Add(s.Name)) continue;
                    scored.Add((score, smart.LearnCompletionRanking ? CompletionHistory.GetCount(s.Name) : 0, i, s));
                }
                scored.Sort((a, b) => a.score != b.score ? b.score.CompareTo(a.score) : a.learned != b.learned ? b.learned.CompareTo(a.learned) : a.order.CompareTo(b.order));
                foreach (var entry in scored) m_Suggestions.Add(entry.item);

                // Only perform expensive fuzzy matching if direct matches are scarce
                if (m_Suggestions.Count < 30 && prefix.Length >= 3)
                {
                    int maxAllowedDistance = prefix.Length <= 4 ? 1 : 2;
                    var fuzzyMatches = new List<Tuple<int, CompletionItem>>();

                    foreach (var s in m_AllSuggestions)
                    {
                        if (addedNames.Contains(s.Name)) continue;
                        if (Math.Abs(s.Name.Length - prefix.Length) > maxAllowedDistance) continue;

                        int dist = CalculateLevenshteinDistance(prefix, s.Name);
                        if (dist <= maxAllowedDistance)
                        {
                            fuzzyMatches.Add(Tuple.Create(dist, s));
                        }
                    }

                    if (fuzzyMatches.Count > 0)
                    {
                        fuzzyMatches.Sort((a, b) => a.Item1.CompareTo(b.Item1));
                        foreach (var fm in fuzzyMatches)
                        {
                            if (addedNames.Add(fm.Item2.Name))
                            {
                                m_Suggestions.Add(fm.Item2);
                            }
                        }
                    }
                }
            }

            m_ListView.RefreshItems();

            if (m_Suggestions.Count > 0) m_ListView.selectedIndex = 0;
        }

        public bool HasMatches() => m_Suggestions.Count > 0;

        public void SelectNext()
        {
            if (m_Suggestions.Count == 0) return;
            m_ListView.selectedIndex = Mathf.Min(m_ListView.selectedIndex + 1, m_Suggestions.Count - 1);
            m_ListView.ScrollToItem(m_ListView.selectedIndex);
        }

        public void SelectPrevious()
        {
            if (m_Suggestions.Count == 0) return;
            m_ListView.selectedIndex = Mathf.Max(m_ListView.selectedIndex - 1, 0);
            m_ListView.ScrollToItem(m_ListView.selectedIndex);
        }

        public void ConfirmSelection()
        {
            if (m_ListView.selectedIndex >= 0 && m_ListView.selectedIndex < m_Suggestions.Count)
            {
                var selected = m_Suggestions[m_ListView.selectedIndex];
                if (CodeIDESettings.Current.SmartCode.LearnCompletionRanking) CompletionHistory.Record(selected.Name);
                OnSuggestionAccepted?.Invoke(selected);
                Hide();
            }
        }

        private void SetupIcon(Label icon, CompletionKind kind)
        {
            string symbol = "";
            Color bgColor = new Color(0.2f, 0.2f, 0.2f);
            Color textColor = Color.white;

            switch (kind)
            {
                case CompletionKind.Method:
                    symbol = "()";
                    bgColor = new Color(0.5f, 0f, 0.5f); // Purple
                    break;
                case CompletionKind.Class:
                case CompletionKind.Struct:
                    symbol = "C";
                    bgColor = new Color(1f, 0.65f, 0f); // Orange
                    textColor = Color.black;
                    break;
                case CompletionKind.Interface:
                    symbol = "I";
                    bgColor = new Color(0f, 0.5f, 0f); // Green
                    break;
                case CompletionKind.Property:
                    symbol = "{ }";
                    bgColor = new Color(0f, 0.8f, 0.8f); // Cyan
                    textColor = Color.black;
                    break;
                case CompletionKind.Field:
                case CompletionKind.Variable:
                case CompletionKind.Parameter:
                    symbol = "v";
                    bgColor = new Color(0.68f, 0.85f, 0.9f); // Light blue
                    textColor = Color.black;
                    break;
                case CompletionKind.Keyword:
                    symbol = "K";
                    bgColor = new Color(0.3f, 0.3f, 0.3f);
                    break;
                case CompletionKind.Enum:
                    symbol = "E";
                    bgColor = new Color(0.6f, 0.8f, 0.2f); // Yellowish green
                    textColor = Color.black;
                    break;
                case CompletionKind.Snippet:
                    symbol = "[ ]";
                    bgColor = new Color(0.8f, 0.2f, 0.2f); // Red
                    break;
                case CompletionKind.UnityName:
                    symbol = "U";
                    bgColor = new Color(0.2f, 0.55f, 0.6f); // Teal
                    break;
                default:
                    symbol = "?";
                    break;
            }

            icon.text = symbol;
            icon.style.backgroundColor = bgColor;
            icon.style.color = textColor;
        }
    }
}