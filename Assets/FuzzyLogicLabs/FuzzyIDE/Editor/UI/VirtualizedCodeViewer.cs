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
    public static class MeshGenerationContextExtensions
    {
        public static void DrawSolidRect(this MeshGenerationContext mgc, Rect rect, Color color)
        {
            var mwd = mgc.Allocate(4, 6);
            mwd.SetNextVertex(new Vertex { position = new Vector3(rect.xMin, rect.yMin, 0), tint = color });
            mwd.SetNextVertex(new Vertex { position = new Vector3(rect.xMax, rect.yMin, 0), tint = color });
            mwd.SetNextVertex(new Vertex { position = new Vector3(rect.xMax, rect.yMax, 0), tint = color });
            mwd.SetNextVertex(new Vertex { position = new Vector3(rect.xMin, rect.yMax, 0), tint = color });
            mwd.SetNextIndex(0); mwd.SetNextIndex(1); mwd.SetNextIndex(2);
            mwd.SetNextIndex(2); mwd.SetNextIndex(3); mwd.SetNextIndex(0);
        }
    }

    public partial class VirtualizedCodeViewer : VisualElement
    {
        private ListView m_ListView;
        private List<string> m_Lines;
        public List<int> m_VisibleLines = new List<int>();
        private List<FoldableBlock> m_FoldableBlocks = new List<FoldableBlock>();
        private Dictionary<int, FoldableBlock> m_FoldableBlockMap = new Dictionary<int, FoldableBlock>();
        private Dictionary<int, int> m_LineToVisibleIndex = new Dictionary<int, int>();
        private MinimapCanvas m_Minimap;
        private Dictionary<int, string> m_LiveBadges = new Dictionary<int, string>();

        public int GetVisibleLineIndex(int line)
        {
            if (m_LineToVisibleIndex.TryGetValue(line, out int visIdx)) return visIdx;
            return -1;
        }

        public int GetDynamicLineForTrackId(string trackId)
        {
            foreach (var kvp in m_LiveBadges)
            {
                if (kvp.Value == trackId) return kvp.Key;
            }
            return -1;
        }

        private void RecalculateLiveBadges()
        {
            m_LiveBadges.Clear();
            for (int i = 0; i < m_Lines.Count; i++)
            {
                string line = m_Lines[i];
                if (!line.Contains("LiveStateTracker")) continue; // Fast skip avoids running regex on thousands of lines
                var match = System.Text.RegularExpressions.Regex.Match(line, @"FuzzyLogicLabs\.LiveStateTracker\.Track\(""([^""]+)"",\s*""[^""]*"",\s*(?:\d+,\s*)?""([^""]+)""");
                if (match.Success)
                {
                    string trackId = match.Groups[1].Value;
                    string varName = match.Groups[2].Value;
                    string pattern = @"\b" + System.Text.RegularExpressions.Regex.Escape(varName) + @"\b";
                
                    int targetLine = -1;
                    int offset = 1;
                    while (i - offset >= 0 || i + offset < m_Lines.Count)
                    {
                        int upLine = i - offset;
                        if (upLine >= 0 && System.Text.RegularExpressions.Regex.IsMatch(m_Lines[upLine], pattern))
                        {
                            targetLine = upLine;
                            break;
                        }
                    
                        int downLine = i + offset;
                        if (downLine < m_Lines.Count && System.Text.RegularExpressions.Regex.IsMatch(m_Lines[downLine], pattern))
                        {
                            targetLine = downLine;
                            break;
                        }
                        offset++;
                    }
                
                    if (targetLine >= 0)
                    {
                        m_LiveBadges[targetLine] = trackId;
                    }
                    else
                    {
                        m_LiveBadges[Math.Max(0, i - 1)] = trackId;
                    }
                }
            }
        }

        private void RecalculateVisibleLines()
        {
            m_VisibleLines.Clear();
            m_LineToVisibleIndex.Clear();
            int skipUntil = -1;
            for (int i = 0; i < m_Lines.Count; i++)
            {
                if (i <= skipUntil) continue;
            
                if (m_PendingDiff != null && i == m_PendingDiff.StartLine)
                {
                    for (int d = 0; d < m_PendingDiff.DeletedLines.Count; d++)
                    {
                        m_VisibleLines.Add(-(d + 1));
                    }
                }

                int visIdx = m_VisibleLines.Count;
                m_VisibleLines.Add(i);
                m_LineToVisibleIndex[i] = visIdx;
            
                if (m_FoldableBlockMap.TryGetValue(i, out var b) && b.IsFolded)
                {
                    skipUntil = b.EndLine;
                }
            }
        
            if (m_PendingDiff != null && m_PendingDiff.StartLine >= m_Lines.Count)
            {
                for (int d = 0; d < m_PendingDiff.DeletedLines.Count; d++)
                {
                    m_VisibleLines.Add(-(d + 1));
                }
            }

            if (!ReferenceEquals(m_ListView.itemsSource, m_VisibleLines)) m_ListView.itemsSource = m_VisibleLines;
            m_ListView.RefreshItems();
            if (m_Minimap != null) m_Minimap.MarkDirtyRepaint();
        }

        private class MinimapCanvas : VisualElement
        {
            public VirtualizedCodeViewer Viewer;
            private VisualElement m_Overlay;

            public MinimapCanvas(VirtualizedCodeViewer viewer)
            {
                Viewer = viewer;
                style.width = 60; // 60px minimap width
                style.position = Position.Absolute;
                style.right = 0; style.top = 0; style.bottom = 0;
                style.backgroundColor = new Color(0.1f, 0.1f, 0.1f, 0.8f);

                m_Overlay = new VisualElement();
                m_Overlay.style.position = Position.Absolute;
                m_Overlay.style.left = 0; m_Overlay.style.right = 0;
                m_Overlay.style.backgroundColor = new Color(1f, 1f, 1f, 0.1f);
                Add(m_Overlay);

                generateVisualContent += OnGenerateVisualContent;
            
                RegisterCallback<PointerDownEvent>(OnPointerDown);
                RegisterCallback<PointerMoveEvent>(OnPointerMove);
                RegisterCallback<PointerUpEvent>(OnPointerUp);
            }

            private bool m_IsDragging = false;

            private void OnPointerDown(PointerDownEvent evt)
            {
                m_IsDragging = true;
                this.CapturePointer(evt.pointerId);
                UpdateScrollFromPointer(evt.localPosition.y);
            }

            private void OnPointerMove(PointerMoveEvent evt)
            {
                if (m_IsDragging)
                {
                    UpdateScrollFromPointer(evt.localPosition.y);
                }
            }

            private void OnPointerUp(PointerUpEvent evt)
            {
                if (m_IsDragging)
                {
                    this.ReleasePointer(evt.pointerId);
                    m_IsDragging = false;
                }
            }

            private void UpdateScrollFromPointer(float y)
            {
                float totalLines = Viewer.m_VisibleLines.Count;
                if (totalLines == 0) return;

                float scaleY = layout.height / totalLines;
                if (scaleY > 2f) scaleY = 2f;

                float actualMinimapHeight = totalLines * scaleY;
                if (actualMinimapHeight <= 0) return;

                y = Mathf.Clamp(y, 0, actualMinimapHeight);
                float ratio = y / actualMinimapHeight;

                var scrollView = Viewer.m_ListView.Q<ScrollView>();
                if (scrollView != null)
                {
                    float maxScroll = Mathf.Max(0, scrollView.contentContainer.layout.height - scrollView.layout.height);
                    float desiredScroll = ratio * scrollView.contentContainer.layout.height - (scrollView.layout.height * 0.5f);
                    scrollView.scrollOffset = new Vector2(scrollView.scrollOffset.x, Mathf.Clamp(desiredScroll, 0, maxScroll));
                }
            }

            public void UpdateOverlay()
            {
                var scrollView = Viewer.m_ListView.Q<ScrollView>();
                if (scrollView == null || scrollView.layout.height == 0 || scrollView.contentContainer.layout.height == 0) return;

                float totalLines = Viewer.m_VisibleLines.Count;
                float contentHeight = scrollView.contentContainer.layout.height;

                float visibleRatio = scrollView.layout.height / contentHeight;
                float scrollRatio = scrollView.scrollOffset.y / contentHeight;

                float scaleY = layout.height / totalLines;
                if (scaleY > 2f) scaleY = 2f; // Max 2px per line

                float overlayHeight = scrollView.layout.height / Viewer.LINE_HEIGHT * scaleY;
                float overlayY = (scrollView.scrollOffset.y / Viewer.LINE_HEIGHT) * scaleY;

                m_Overlay.style.height = overlayHeight;
                m_Overlay.style.top = overlayY;
            }

            private void OnGenerateVisualContent(MeshGenerationContext mgc)
            {
                int totalLines = Viewer.m_VisibleLines.Count;
                if (totalLines == 0) return;

                float scaleY = layout.height / totalLines;
                if (scaleY > 2f) scaleY = 2f;

                var theme = CodeIDESettings.Current.Theme;

                for (int i = 0; i < totalLines; i++)
                {
                    int realIndex = Viewer.m_VisibleLines[i];
                    if (realIndex < 0 || realIndex >= Viewer.m_Lines.Count) continue;

                    float y = i * scaleY;

                    if (Viewer.m_LineHighlights.TryGetValue(realIndex, out var spans) && spans.Count > 0)
                    {
                        foreach (var span in spans)
                        {
                            if (ColorUtility.TryParseHtmlString(span.ColorHex, out Color color))
                            {
                                float x = span.StartIndex * 0.5f; 
                                float w = span.Length * 0.5f;
                                if (x > layout.width - 2) continue; 
                                if (x + w > layout.width) w = layout.width - x;

                                mgc.DrawSolidRect(new Rect(x, y, w, scaleY * 0.8f), color);
                            }
                        }
                    }
                    else
                    {
                        string text = Viewer.m_Lines[realIndex];
                        int startX = text.Length - text.TrimStart().Length; 
                        int len = text.Trim().Length;
                        if (len > 0)
                        {
                            float x = startX * 0.5f;
                            float w = len * 0.5f;
                            if (x > layout.width - 2) x = layout.width - 2;
                            if (x + w > layout.width) w = layout.width - x;
                            mgc.DrawSolidRect(new Rect(x, y, w, scaleY * 0.8f), theme.DefaultText);
                        }
                    }
                
                    if (Viewer.m_LineDiagnostics != null && Viewer.m_LineDiagnostics.TryGetValue(realIndex, out var diags) && diags.Count > 0)
                    {
                        bool hasError = false;
                        foreach (var diag in diags) if (diag.IsError) hasError = true;
                        Color diagColor = hasError ? new Color(1f, 0.2f, 0.2f, 0.8f) : new Color(1f, 0.8f, 0.2f, 0.8f);
                        mgc.DrawSolidRect(new Rect(0, y, layout.width, scaleY), diagColor);
                    }

                    if (Viewer.m_GitLineDiffResult != null && CodeIDESettings.Current.Git.ShowMinimapDiffMarkers &&
                        Viewer.m_GitLineDiffResult.LineStatuses.TryGetValue(realIndex, out var gitType))
                    {
                        Color markerCol = gitType == Git.GitLineDiffType.Added ? theme.GitAdded :
                                         (gitType == Git.GitLineDiffType.Modified ? theme.GitModified : theme.GitDeleted);
                        mgc.DrawSolidRect(new Rect(0, y, 3, scaleY), markerCol);
                    }
                }
            }
        }
        private Font m_MonospaceFont;
        private bool m_IsMonospaceFontDynamic = false;
        
        private Font FindFontVariant(string baseName, string variant)
        {
            var fonts = Resources.LoadAll<Font>("Fonts");
            foreach (var f in fonts)
            {
                if (f.name == $"{baseName}-{variant}" || f.name == $"{baseName} {variant}") return f;
            }
            return null;
        }

        private Font FindFontInResources(string fontName)
        {
            var fonts = Resources.LoadAll<Font>("Fonts");
            foreach (var f in fonts)
            {
                if (f.name == fontName) return f;
            }
            return null;
        }

        private Font LoadCodeFont(string fontFamily, int fontSize)
        {
            var font = FindFontVariant(fontFamily, "Regular") ?? FindFontInResources(fontFamily);
            if (font != null) return font;
            return Font.CreateDynamicFontFromOSFont(new string[] { fontFamily, "Consolas", "Courier New", "Menlo", "Monaco", "Courier" }, fontSize);
        }

        // Selection & Caret State
        private VisualElement m_SelectionContainer;
        private List<VisualElement> m_SelectionRects = new List<VisualElement>();

        private Label m_SignatureHelpPopup;
        private VisualElement m_HoverInfoPopup;
        private VisualElement m_ContextMenuPopup;
        private VisualElement m_SubContextMenuPopup;
        private UnityEngine.UIElements.IVisualElementScheduledItem m_HideSubmenuTask;
        private UnityEngine.UIElements.IVisualElementScheduledItem m_HideHoverPopupTask;
        private bool m_IsHoveringDiagnostic = false;

        // Phase 5: Find & Replace
        private FindReplacePanel m_FindReplacePanel;
        private VisualElement m_SearchContainer;
        private List<VisualElement> m_SearchRects = new List<VisualElement>();
        private struct SearchMatch { public int Line; public int Col; public int Length; }
        private List<SearchMatch> m_SearchMatches = new List<SearchMatch>();
        private int m_ActiveSearchMatchIndex = -1;

        // Phase 3.4: Real-Time Error Visuals
        private VisualElement m_ErrorContainer;
        private List<VisualElement> m_ErrorRects = new List<VisualElement>();

        private struct DiagnosticSpan
        {
            public int Line;
            public int LocalStart;
            public int LocalEnd;
            public string Message;
            public bool IsError;
            public string Id;
            public DiagnosticSpan(int l, int s, int e, string m, bool err, string id) { Line = l; LocalStart = s; LocalEnd = e; Message = m; IsError = err; Id = id; }
        }
        private Dictionary<int, List<DiagnosticSpan>> m_LineDiagnostics = new Dictionary<int, List<DiagnosticSpan>>();

        private VisualElement m_Caret;
        private Label m_MeasureLabel;
        private IVisualElementScheduledItem m_BlinkTask;
        private int m_CaretLine = -1;
        private int m_CaretColumn = -1;
        public int CaretLine { get => m_CaretLine; set => m_CaretLine = value; }
        public int CaretColumn { get => m_CaretColumn; set => m_CaretColumn = value; }
        private bool m_IsCaretVisible = false;

        private bool m_HasSelection = false;
        private int m_AnchorLine = -1;
        private int m_AnchorColumn = -1;
        private bool m_IsDragging = false;

        // Phase 10: Multi-Language Service
        public ILanguageService LanguageService { get; set; }
        private IVisualElementScheduledItem m_DebounceTask;
        private Dictionary<int, List<SyntaxSpan>> m_LineHighlights = new Dictionary<int, List<SyntaxSpan>>();

        // Phase 3.2 & 3.3 & 6.5: Auto-Complete UI
        private AutoCompletePopup m_AutoCompletePopup;
        private string m_AutoCompletePrefix = "";
        private int m_AutoCompleteRequestId = 0;

        // Phase 4: History & Saving
private TextHistoryManager m_History = new TextHistoryManager();
        public TextHistoryManager History { get => m_History; set => m_History = value; }

        public Action OnTextChanged;
        public Action OnSaveRequested;
        public Action OnRecompileRequested;
        public Action OnReloadDomainRequested;
        public Action<int, int> OnCaretPositionChanged;
        public struct CaretPos
        {
            public int Line;
            public int Column;
            public CaretPos(int line, int col) { Line = line; Column = col; }
        }

        private List<CaretPos> m_ExtraCarets = new List<CaretPos>();
        private List<VisualElement> m_ExtraCaretOverlays = new List<VisualElement>();
        private IVisualElementScheduledItem m_HoverDebounceTask;
        private int m_LastHoverPos = -1;

        public event Action<int> OnRenameRequested;
        public event Action<int> OnFindReferencesRequested;
        public event Action<int> OnWatchLiveRequested;
        public event Action<int> OnRemoveWatchLiveRequested;
        public event Action<string, string> OnFixErrorRequested;
        public Action<string, int> OnLoadFileRequested;
        public string CurrentFilePath { get; set; }

        // Git In-Editor Diff State
        private Git.GitLineDiffResult m_GitLineDiffResult;
        private List<string> m_HeadFileLines;
        private string m_LastGitFilePath;
        private IVisualElementScheduledItem m_GitDebounceTask;
        private VisualElement m_GitDiffPopup;

        public class PendingDiff
        {
            public List<IEditCommand> Commands;
            public int StartLine;
            public int AddedLinesCount;
            public List<string> DeletedLines;
            public Action OnAccept;
            public Action OnReject;
        }

        private PendingDiff m_PendingDiff;
        private VisualElement m_DiffOverlay;

        // ──────────────────────────────────────────────────────
        // External Change & Conflict Handling
        // ──────────────────────────────────────────────────────
        private VisualElement m_ConflictBanner;

        // Click tracking for double/triple clicks
        private double m_LastClickTime = 0;
        private int m_LastClickLine = -1;
        private int m_CustomClickCount = 0;
    
        private float m_TargetMenuX = 0f;
        private float m_TargetMenuY = 0f;

        // Constants and Settings
        private float LINE_HEIGHT => CodeIDESettings.Current.Editor.FontSize * CodeIDESettings.Current.Editor.LineHeightSpacing;
        private const float LINE_NUM_WIDTH = 62f; // git marker 3 + note marker 12 + numbers + fold toggle 16
        private const float CODE_PADDING_LEFT = 4f;
        private float m_CharWidth = 7.64f; // Will be measured dynamically

        public VirtualizedCodeViewer(List<string> lines)
        {
            m_Lines = lines;
            if (m_Lines == null || m_Lines.Count == 0) m_Lines = new List<string> { "" };

            style.flexGrow = 1;
            style.overflow = Overflow.Hidden;
            style.backgroundColor = new Color(0.1176f, 0.1176f, 0.1176f, 1f);
            focusable = true; // Crucial for receiving Keyboard Events

            CodeIDESettings.OnSettingsChanged += ApplySettings;
            Git.GitService.OnStatusUpdated += OnGitStatusUpdated;
            ProjectCompilationCache.AssemblyReady += OnProjectAssemblyReady;
            OnTextChanged += () => ScheduleGitDiff(false);
            InitNoteMarkers();

            RegisterCallback<DetachFromPanelEvent>(e => 
            {
                CodeIDESettings.OnSettingsChanged -= ApplySettings;
                Git.GitService.OnStatusUpdated -= OnGitStatusUpdated;
                ProjectCompilationCache.AssemblyReady -= OnProjectAssemblyReady;
            });

            int fontSize = CodeIDESettings.Current.Editor.FontSize;

            // 1. Setup ListView
            m_ListView = new ListView();
            m_ListView.style.position = Position.Absolute;
            m_ListView.style.left = 0; m_ListView.style.right = 0; m_ListView.style.top = 0; m_ListView.style.bottom = 0;
            m_ListView.itemsSource = m_VisibleLines;
            m_ListView.makeItem = MakeItem;
            m_ListView.bindItem = BindItem;
            m_ListView.unbindItem = UnbindItem;
            m_ListView.fixedItemHeight = LINE_HEIGHT;
            m_ListView.showAlternatingRowBackgrounds = AlternatingRowBackground.None;
            m_ListView.selectionType = SelectionType.None;
            m_ListView.focusable = false;

            var cursorContainer = new IMGUIContainer(() =>
            {
                if (Event.current.type == EventType.Repaint)
                {
                    var cursor = (Event.current.control || Event.current.command)
                        ? UnityEditor.MouseCursor.Link
                        : UnityEditor.MouseCursor.Text;
                    UnityEditor.EditorGUIUtility.AddCursorRect(new Rect(0, 0, layout.width, layout.height), cursor);
                }
            });
            cursorContainer.style.position = Position.Absolute;
            cursorContainer.style.left = 0; cursorContainer.style.top = 0; cursorContainer.style.right = 0; cursorContainer.style.bottom = 0;
            cursorContainer.pickingMode = PickingMode.Ignore;
            Add(cursorContainer);

            Add(m_ListView);
            m_Minimap = new MinimapCanvas(this);
            Add(m_Minimap);
        
            m_ListView.RegisterCallback<GeometryChangedEvent>(e => {
                if (m_Minimap != null) m_Minimap.UpdateOverlay();
            });
        
            schedule.Execute(() => {
                var scrollView = m_ListView.Q<ScrollView>();
                if (scrollView != null)
                {
                    scrollView.verticalScroller.valueChanged += (v) => {
                        if (m_Minimap != null) {
                            m_Minimap.UpdateOverlay();
                            m_Minimap.MarkDirtyRepaint();
                        }
                        if (m_SignatureHelpPopup != null && m_SignatureHelpPopup.style.display == DisplayStyle.Flex)
                        {
                            UpdateSignatureHelpPosition();
                        }
                    };
                    scrollView.horizontalScroller.valueChanged += (v) => {
                        if (m_SignatureHelpPopup != null && m_SignatureHelpPopup.style.display == DisplayStyle.Flex)
                        {
                            UpdateSignatureHelpPosition();
                        }
                    };
                }
            }).ExecuteLater(100);


            schedule.Execute(() =>
            {
                string fontFamily = CodeIDESettings.Current.Editor.FontFamily;
                if (m_MonospaceFont != null && m_IsMonospaceFontDynamic) UnityEngine.Object.DestroyImmediate(m_MonospaceFont);
                
                m_MonospaceFont = LoadCodeFont(fontFamily, fontSize);
                m_IsMonospaceFontDynamic = (FindFontVariant(fontFamily, "Regular") ?? FindFontInResources(fontFamily)) == null;
                
                if (m_MonospaceFont != null && m_IsMonospaceFontDynamic) m_MonospaceFont.hideFlags = HideFlags.HideAndDontSave;
                if (m_MeasureLabel != null) m_MeasureLabel.style.unityFontDefinition = new StyleFontDefinition(m_MonospaceFont);
                m_ListView.Rebuild();
            });

            // 2. Setup Selection Container (Under Caret, Over Text)
            m_SelectionContainer = new VisualElement();
            m_SelectionContainer.name = "selection-container";
            m_SelectionContainer.style.position = Position.Absolute;
            m_SelectionContainer.style.top = 0; m_SelectionContainer.style.bottom = 0;
            m_SelectionContainer.style.left = 0; m_SelectionContainer.style.right = 0;
            m_SelectionContainer.pickingMode = PickingMode.Ignore; // Pass clicks through
            Add(m_SelectionContainer);

            m_ErrorContainer = new VisualElement();
            m_ErrorContainer.name = "error-container";
            m_ErrorContainer.style.position = Position.Absolute;
            m_ErrorContainer.style.top = 0; m_ErrorContainer.style.bottom = 0;
            m_ErrorContainer.style.left = 0; m_ErrorContainer.style.right = 0;
            m_ErrorContainer.pickingMode = PickingMode.Ignore;
            Add(m_ErrorContainer);

            m_SearchContainer = new VisualElement();
            m_SearchContainer.name = "search-container";
            m_SearchContainer.style.position = Position.Absolute;
            m_SearchContainer.style.top = 0; m_SearchContainer.style.bottom = 0;
            m_SearchContainer.style.left = 0; m_SearchContainer.style.right = 0;
            m_SearchContainer.pickingMode = PickingMode.Ignore;
            Add(m_SearchContainer);

            InitDecorations();

            m_FindReplacePanel = new FindReplacePanel();
            m_FindReplacePanel.style.display = DisplayStyle.None;
            m_FindReplacePanel.OnClose += () => { m_FindReplacePanel.style.display = DisplayStyle.None; ClearSearch(); this.Focus(); };
            m_FindReplacePanel.OnFindTextChanged += (t, c) => PerformSearch(t, c);
            m_FindReplacePanel.OnFindNext += (t, c) => NavigateSearch(1);
            m_FindReplacePanel.OnFindPrev += (t, c) => NavigateSearch(-1);
            m_FindReplacePanel.OnReplace += ReplaceActiveMatch;
            m_FindReplacePanel.OnReplaceAll += ReplaceAllMatches;
            Add(m_FindReplacePanel);
            m_SignatureHelpPopup = new Label();
            m_SignatureHelpPopup.name = "signature-help-popup";
            m_SignatureHelpPopup.enableRichText = true;
            m_SignatureHelpPopup.style.position = Position.Absolute;
            m_SignatureHelpPopup.style.borderBottomWidth = 1; m_SignatureHelpPopup.style.borderTopWidth = 1; m_SignatureHelpPopup.style.borderLeftWidth = 1; m_SignatureHelpPopup.style.borderRightWidth = 1;
            m_SignatureHelpPopup.style.borderTopLeftRadius = 4; m_SignatureHelpPopup.style.borderTopRightRadius = 4;
            m_SignatureHelpPopup.style.borderBottomLeftRadius = 4; m_SignatureHelpPopup.style.borderBottomRightRadius = 4;
            m_SignatureHelpPopup.style.paddingBottom = 4; m_SignatureHelpPopup.style.paddingTop = 4; m_SignatureHelpPopup.style.paddingLeft = 8; m_SignatureHelpPopup.style.paddingRight = 8;
            m_SignatureHelpPopup.style.fontSize = 12;
            m_SignatureHelpPopup.style.whiteSpace = WhiteSpace.NoWrap;
            m_SignatureHelpPopup.style.backgroundColor = new Color(0.15f, 0.15f, 0.15f, 0.95f);
            m_SignatureHelpPopup.style.borderBottomColor = m_SignatureHelpPopup.style.borderTopColor = m_SignatureHelpPopup.style.borderLeftColor = m_SignatureHelpPopup.style.borderRightColor = new Color(0.3f, 0.3f, 0.3f, 1f);
            m_SignatureHelpPopup.style.color = new Color(0.9f, 0.9f, 0.9f, 1f);
            m_SignatureHelpPopup.style.display = DisplayStyle.None;
            m_SignatureHelpPopup.pickingMode = PickingMode.Ignore;
        
            // Setup Hover Info Popup
            m_HoverInfoPopup = new VisualElement();
            m_HoverInfoPopup.name = "hover-info-popup";
            m_HoverInfoPopup.style.position = Position.Absolute;
            m_HoverInfoPopup.style.borderBottomWidth = 1; m_HoverInfoPopup.style.borderTopWidth = 1; m_HoverInfoPopup.style.borderLeftWidth = 1; m_HoverInfoPopup.style.borderRightWidth = 1;
            m_HoverInfoPopup.style.minWidth = 250;
            m_HoverInfoPopup.style.minHeight = 40;
            m_HoverInfoPopup.style.paddingBottom = 6; m_HoverInfoPopup.style.paddingTop = 6; m_HoverInfoPopup.style.paddingLeft = 8; m_HoverInfoPopup.style.paddingRight = 8;
            m_HoverInfoPopup.style.display = DisplayStyle.None;
            m_HoverInfoPopup.pickingMode = PickingMode.Position;
            m_HoverInfoPopup.style.overflow = Overflow.Hidden; // PopupPlacement may cap its height

            m_HideHoverPopupTask = schedule.Execute(() => {
                m_IsHoveringDiagnostic = false;
                if (m_HoverInfoPopup != null) m_HoverInfoPopup.style.display = DisplayStyle.None;
            });
            m_HideHoverPopupTask.Pause();

            m_HoverInfoPopup.RegisterCallback<PointerEnterEvent>(e => {
                m_IsHoveringDiagnostic = true;
                m_HideHoverPopupTask.Pause();
            });
            m_HoverInfoPopup.RegisterCallback<PointerLeaveEvent>(e => {
                m_HideHoverPopupTask.ExecuteLater(400);
            });
            m_HoverInfoPopup.RegisterCallback<PointerDownEvent>(e => e.StopPropagation());
            m_HoverInfoPopup.RegisterCallback<PointerUpEvent>(e => e.StopPropagation());
            m_HoverInfoPopup.RegisterCallback<PointerMoveEvent>(e => e.StopPropagation());

            // Setup Context Menu Popup
            m_ContextMenuPopup = new ScrollView(ScrollViewMode.Vertical);
            m_ContextMenuPopup.name = "context-menu-popup";
            m_ContextMenuPopup.style.position = Position.Absolute;
            m_ContextMenuPopup.style.borderBottomWidth = 1; m_ContextMenuPopup.style.borderTopWidth = 1; m_ContextMenuPopup.style.borderLeftWidth = 1; m_ContextMenuPopup.style.borderRightWidth = 1;
            m_ContextMenuPopup.style.minWidth = 220;
            m_ContextMenuPopup.style.minHeight = 30;
            m_ContextMenuPopup.style.maxHeight = 400; // Constrain height and allow scrolling
            m_ContextMenuPopup.style.paddingBottom = 4; m_ContextMenuPopup.style.paddingTop = 4; m_ContextMenuPopup.style.paddingLeft = 4; m_ContextMenuPopup.style.paddingRight = 4;
            m_ContextMenuPopup.style.display = DisplayStyle.None;
            m_ContextMenuPopup.pickingMode = PickingMode.Position;
        
            m_ContextMenuPopup.RegisterCallback<GeometryChangedEvent>(e => {
                if (m_ContextMenuPopup.style.display == DisplayStyle.None) return;
                if (m_ContextMenuPopup.panel == null || m_ContextMenuPopup.panel.visualTree == null) return;

                float actualHeight = e.newRect.height;
                float actualWidth = e.newRect.width;
                float windowHeight = m_ContextMenuPopup.panel.visualTree.layout.height;
                float windowWidth = m_ContextMenuPopup.panel.visualTree.layout.width;

                float targetY = m_TargetMenuY;
                float targetX = m_TargetMenuX;

                if (targetY + actualHeight > windowHeight) targetY = Mathf.Max(0, m_TargetMenuY - actualHeight);
                if (targetX + actualWidth > windowWidth) targetX = Mathf.Max(0, m_TargetMenuX - actualWidth);

                m_ContextMenuPopup.style.left = targetX;
                m_ContextMenuPopup.style.top = targetY;
            });
        
            m_SubContextMenuPopup = new ScrollView(ScrollViewMode.Vertical);
            m_SubContextMenuPopup.style.position = Position.Absolute;
            m_SubContextMenuPopup.style.borderBottomWidth = 1; m_SubContextMenuPopup.style.borderTopWidth = 1; m_SubContextMenuPopup.style.borderLeftWidth = 1; m_SubContextMenuPopup.style.borderRightWidth = 1;
            m_SubContextMenuPopup.style.minWidth = 200;
            m_SubContextMenuPopup.style.maxHeight = 400;
            m_SubContextMenuPopup.style.paddingBottom = 4; m_SubContextMenuPopup.style.paddingTop = 4; m_SubContextMenuPopup.style.paddingLeft = 4; m_SubContextMenuPopup.style.paddingRight = 4;
            m_SubContextMenuPopup.style.display = DisplayStyle.None;
            m_SubContextMenuPopup.pickingMode = PickingMode.Position;

            m_SubContextMenuPopup.RegisterCallback<GeometryChangedEvent>(e => {
                if (m_SubContextMenuPopup.style.display == DisplayStyle.None) return;
                if (m_SubContextMenuPopup.panel == null || m_SubContextMenuPopup.panel.visualTree == null) return;
            
                float actualWidth = e.newRect.width;
                float windowWidth = m_SubContextMenuPopup.panel.visualTree.layout.width;
                float currentLeft = m_SubContextMenuPopup.style.left.value.value;
            
                // If sub-menu overflows right, flip it to the left side of the parent menu
                if (currentLeft + actualWidth > windowWidth) {
                    float leftSideX = m_ContextMenuPopup.worldBound.xMin - actualWidth;
                    m_SubContextMenuPopup.style.left = Mathf.Max(0, leftSideX);
                }
            });

            m_HideSubmenuTask = schedule.Execute(() => {
                if (m_SubContextMenuPopup != null) m_SubContextMenuPopup.style.display = DisplayStyle.None;
            });
            m_HideSubmenuTask.Pause();

            m_SubContextMenuPopup.RegisterCallback<PointerEnterEvent>(e => m_HideSubmenuTask.Pause());
            m_SubContextMenuPopup.RegisterCallback<PointerLeaveEvent>(e => m_HideSubmenuTask.ExecuteLater(150));
        
            // Hide context menu when clicking outside
            this.RegisterCallback<PointerDownEvent>(e => {
                bool clickedMain = m_ContextMenuPopup.style.display == DisplayStyle.Flex && m_ContextMenuPopup.worldBound.Contains(e.position);
                bool clickedSub = m_SubContextMenuPopup.style.display == DisplayStyle.Flex && m_SubContextMenuPopup.worldBound.Contains(e.position);
            
                if (m_ContextMenuPopup.style.display == DisplayStyle.Flex && !clickedMain && !clickedSub)
                {
                    m_ContextMenuPopup.style.display = DisplayStyle.None;
                    m_SubContextMenuPopup.style.display = DisplayStyle.None;
                }
            });

            // 3. Setup Caret
            m_Caret = new VisualElement();
            m_Caret.name = "caret-overlay";
            m_Caret.style.position = Position.Absolute;
            m_Caret.style.width = 2;
            m_Caret.style.height = LINE_HEIGHT;
            m_Caret.style.backgroundColor = new Color(0.9f, 0.9f, 0.9f, 1f);
            m_Caret.style.display = DisplayStyle.None;
            Add(m_Caret);

            m_BlinkTask = schedule.Execute(ToggleCaretBlink).Every(500);
            m_BlinkTask.Pause();

            // 4. Setup Measure Label (Hidden)
            m_MeasureLabel = new Label();
            m_MeasureLabel.name = "measure-label";
            m_MeasureLabel.style.position = Position.Absolute;
            m_MeasureLabel.style.visibility = Visibility.Hidden;
            m_MeasureLabel.style.fontSize = 14;
            m_MeasureLabel.style.whiteSpace = WhiteSpace.Pre;
            // CRITICAL: Enable rich text on m_MeasureLabel so physical text measurement matches bold/italic syntax highlights
            m_MeasureLabel.enableRichText = true;
            if (m_MonospaceFont != null)
            {
                m_MeasureLabel.style.unityFontDefinition = new StyleFontDefinition(m_MonospaceFont);
            }
            Add(m_MeasureLabel);
        
            ApplySettings();

            // 5. Input Events
            m_ListView.RegisterCallback<PointerDownEvent>(OnPointerDown);
            m_ListView.RegisterCallback<PointerMoveEvent>(OnPointerMove);
            m_ListView.RegisterCallback<PointerUpEvent>(OnPointerUp);
            m_ListView.RegisterCallback<PointerLeaveEvent>(e => HideHoverInfo());
            // A click in the text always wins over a hover popup.
            m_ListView.RegisterCallback<PointerDownEvent>(e => DismissHoverPopups(), TrickleDown.TrickleDown);
            RegisterCallback<KeyDownEvent>(OnKeyDown, TrickleDown.TrickleDown);
            RegisterCallback<DragUpdatedEvent>(OnDragUpdated, TrickleDown.TrickleDown);
            RegisterCallback<DragPerformEvent>(OnDragPerform, TrickleDown.TrickleDown);
            RegisterCallback<DragEnterEvent>(e => { DragAndDrop.visualMode = DragAndDropVisualMode.Copy; e.StopPropagation(); }, TrickleDown.TrickleDown);
            RegisterCallback<DragLeaveEvent>(e => { DragAndDrop.visualMode = DragAndDropVisualMode.None; e.StopPropagation(); }, TrickleDown.TrickleDown);

            RegisterCallback<NavigationMoveEvent>(e => { 
                e.StopPropagation(); 
#if UNITY_6000_0_OR_NEWER
                this.panel?.focusController?.IgnoreEvent(e);
#else
                e.PreventDefault(); 
#endif
            }, TrickleDown.TrickleDown);

            m_ListView.RegisterCallback<WheelEvent>(e =>
            {
                if (e.ctrlKey || e.commandKey)
                {
                    Zoom(e.delta.y < 0 ? 1 : -1);
                    e.StopPropagation();
                    return;
                }

                var mult = CodeIDESettings.Current.Behaviors.ScrollSpeedMultiplier;
                if (mult != 1.0f)
                {
                    var scrollView = m_ListView.Q<ScrollView>();
                    if (scrollView != null)
                    {
                        scrollView.scrollOffset = new Vector2(scrollView.scrollOffset.x, scrollView.scrollOffset.y + e.delta.y * 50 * (mult - 1.0f));
                    }
                }
            }, TrickleDown.TrickleDown);

            schedule.Execute(() =>
            {
                MeasureCharWidth();

                var scrollView = m_ListView.Q<ScrollView>();
                if (scrollView != null)
                {
                    scrollView.mode = ScrollViewMode.VerticalAndHorizontal;
                    scrollView.verticalScrollerVisibility = ScrollerVisibility.AlwaysVisible;
                    InitScrollMarkers();
                    scrollView.verticalScroller.valueChanged += (v) => { UpdateCaretPosition(); OnViewScrolled(); };
                    scrollView.horizontalScroller.valueChanged += (v) => { UpdateCaretPosition(); OnViewScrolled(); };
                }

                RefreshListView(); // Force update with exact width

                // Initial Analyzer Run
                TriggerCodeAnalysis();
            });

            // Initialize Debouncer
            m_DebounceTask = schedule.Execute(async () => await RunAnalyzerAsync());
            m_DebounceTask.Pause();

            // Initialize AutoComplete Popup
            m_AutoCompletePopup = new AutoCompletePopup();
            m_AutoCompletePopup.OnSuggestionAccepted = (item) =>
            {
                var cmds = new List<IEditCommand>();
                int cLine = m_CaretLine;
                int cCol = m_CaretColumn;
                string word = item.Name;

                if (m_AutoCompleteReplaceStartCol >= 0 && m_AutoCompleteReplaceStartCol <= cCol)
                {
                    // Override stubs replace the modifiers typed so far ("public override Na|").
                    string typed = m_Lines[cLine].Substring(m_AutoCompleteReplaceStartCol, cCol - m_AutoCompleteReplaceStartCol);
                    cCol = m_AutoCompleteReplaceStartCol;
                    if (typed.Length > 0) cmds.Add(new DeleteTextCommand(m_Lines, typed, cLine, cCol, false, SetCaretAction));
                    m_AutoCompleteReplaceStartCol = -1;
                }
                else if (m_AutoCompletePrefix.Length > 0)
                {
                    cCol -= m_AutoCompletePrefix.Length;
                    cmds.Add(new DeleteTextCommand(m_Lines, m_AutoCompletePrefix, cLine, cCol, false, SetCaretAction));
                }

                if (!string.IsNullOrEmpty(item.SnippetText))
                {
                    m_AutoCompletePrefix = "";
                    InsertSnippet(item.SnippetText, cLine, cCol, cmds);
                    return;
                }

                bool isSnippet = false;
                string snippet = word;
                int finalCaretLine = cLine;
                int finalCaretCol = cCol;

                if (item.Kind == CompletionKind.Method)
                {
                    string curLine = m_Lines[cLine];
                    int charAfterCaret = cCol;
                    bool nextCharIsParen = charAfterCaret < curLine.Length && curLine[charAfterCaret] == '(';
                    if (!nextCharIsParen)
                    {
                        snippet = word + "()";
                        bool hasParams = !string.IsNullOrEmpty(item.SignatureText) && !item.SignatureText.EndsWith("()") && !item.SignatureText.EndsWith("() ") && item.SignatureText.Contains("(");
                        if (hasParams)
                        {
                            finalCaretLine = cLine;
                            finalCaretCol = cCol + word.Length + 1;
                            isSnippet = true;
                        }
                        else
                        {
                            finalCaretLine = cLine;
                            finalCaretCol = cCol + word.Length + 2;
                            isSnippet = true;
                        }
                    }
                }

                cmds.Add(new InsertTextCommand(m_Lines, snippet, cLine, cCol, SetCaretAction));
                ExecuteHistory(cmds);

                if (isSnippet)
                {
                    SetCaretPosition(finalCaretLine, finalCaretCol, clearSelection: true);
                    RefreshListView();
                }

                m_AutoCompletePrefix = "";

                if (!string.IsNullOrEmpty(item.NamespaceHint))
                {
                    if (LanguageService is CSharpLanguageService csharpService)
                    {
                        ApplyUsingDirectiveAsync(csharpService, item.NamespaceHint);
                    }
                }
            };
        }

        // Popups anchored to text would drift away from it while scrolling.
        private void OnViewScrolled()
        {
            DismissHoverPopups();
            if (m_AutoCompletePopup != null && m_AutoCompletePopup.IsVisible) m_AutoCompletePopup.Hide();
            if (m_GitDiffPopup != null) m_GitDiffPopup.style.display = DisplayStyle.None;
        }

        public void RegisterPopupsToContainer(VisualElement rootContainer)
        {
            if (m_SignatureHelpPopup.parent != null) m_SignatureHelpPopup.parent.Remove(m_SignatureHelpPopup);
            if (m_HoverInfoPopup.parent != null) m_HoverInfoPopup.parent.Remove(m_HoverInfoPopup);
            if (m_ContextMenuPopup.parent != null) m_ContextMenuPopup.parent.Remove(m_ContextMenuPopup);
            if (m_SubContextMenuPopup.parent != null) m_SubContextMenuPopup.parent.Remove(m_SubContextMenuPopup);
            if (m_AutoCompletePopup.parent != null) m_AutoCompletePopup.parent.Remove(m_AutoCompletePopup);

            rootContainer.Add(m_SignatureHelpPopup);
            rootContainer.Add(m_HoverInfoPopup);
            rootContainer.Add(m_ContextMenuPopup);
            rootContainer.Add(m_SubContextMenuPopup);
            rootContainer.Add(m_AutoCompletePopup);
        }

        public void SetLines(List<string> lines, bool resetState = true)
        {
            int tabWidth = CodeIDESettings.Current.Editor.TabWidth;
            string spaces = new string(' ', tabWidth);

            m_Lines = lines;
            for (int i = 0; i < m_Lines.Count; i++)
            {
                if (m_Lines[i].Contains("\t")) 
                    m_Lines[i] = m_Lines[i].Replace("\t", spaces);
            }
            if (m_Lines.Count == 0) m_Lines.Add("");
            m_LineMarkupCache.Clear();
            if (resetState) m_InlineHints.Clear();

            RecalculateVisibleLines();
            RecalculateLiveBadges();
        
            if (resetState)
            {
                m_CaretLine = 0;
                m_CaretColumn = 0;
                m_HasSelection = false;
                m_History = new TextHistoryManager(); // Clear history on new file
            }
            DismissConflictBanner();
            RefreshListView();
            ScheduleGitDiff(true);
            ScheduleNoteMarkers(true);
        }

        public List<string> GetLines()
        {
            return m_Lines;
        }

        public void JumpToLine(int lineIndex, bool takeFocus = true)
        {
            if (m_Lines == null || m_Lines.Count == 0) return;

            lineIndex = Mathf.Clamp(lineIndex, 0, m_Lines.Count - 1);
            OnJumpRequested?.Invoke(m_CaretLine, lineIndex);

            SetCaretPosition(lineIndex, 0, clearSelection: true);

            m_ListView.ScrollToItem(lineIndex);
            if (takeFocus) this.Focus();
            RefreshListView();
        }

        private void SetCaretAction(int line, int col)
        {
            SetCaretPosition(line, col, clearSelection: true);
        }

        private void RefreshListView()
        {
            // RecalculateVisibleLines refreshes the visible rows (also when the row count changed); a full Rebuild()
            // would tear down and recreate every row element on each multi-line edit.
            RecalculateVisibleLines();
            UpdateCaretPosition();
            TriggerCodeAnalysis();
        }

        private void ApplySettings()
        {
            int fontSize = CodeIDESettings.Current.Editor.FontSize;
            var theme = CodeIDESettings.Current.Theme;

            style.backgroundColor = theme.Background;
            m_ListView.fixedItemHeight = LINE_HEIGHT;

            bool showMinimap = CodeIDESettings.Current.Behaviors.MinimapVisible;
            if (m_Minimap != null) m_Minimap.style.display = showMinimap ? DisplayStyle.Flex : DisplayStyle.None;
            m_ListView.style.right = showMinimap ? 60 : 0;

            if (m_Caret != null) m_Caret.style.backgroundColor = theme.CaretColor;
            foreach (var rect in m_SelectionRects)
            {
                rect.style.backgroundColor = theme.SelectionHighlight;
            }

            if (m_SignatureHelpPopup != null)
            {
                m_SignatureHelpPopup.style.backgroundColor = theme.UIPopupBackground;
                m_SignatureHelpPopup.style.borderBottomColor = theme.UIBorder; m_SignatureHelpPopup.style.borderTopColor = theme.UIBorder;
                m_SignatureHelpPopup.style.borderLeftColor = theme.UIBorder; m_SignatureHelpPopup.style.borderRightColor = theme.UIBorder;
                m_SignatureHelpPopup.style.color = theme.DefaultText;
                if (m_MonospaceFont != null)
                {
                    m_SignatureHelpPopup.style.unityFontDefinition = new StyleFontDefinition(m_MonospaceFont);
                }
            }
            if (m_HoverInfoPopup != null)
            {
                m_HoverInfoPopup.style.backgroundColor = theme.UIPopupBackground;
                m_HoverInfoPopup.style.borderBottomColor = theme.UIBorder; m_HoverInfoPopup.style.borderTopColor = theme.UIBorder;
                m_HoverInfoPopup.style.borderLeftColor = theme.UIBorder; m_HoverInfoPopup.style.borderRightColor = theme.UIBorder;
            }
            if (m_ContextMenuPopup != null)
            {
                m_ContextMenuPopup.style.backgroundColor = theme.UIPopupBackground;
                m_ContextMenuPopup.style.borderBottomColor = theme.UIBorder; m_ContextMenuPopup.style.borderTopColor = theme.UIBorder;
                m_ContextMenuPopup.style.borderLeftColor = theme.UIBorder; m_ContextMenuPopup.style.borderRightColor = theme.UIBorder;
            }

            string fontFamily = CodeIDESettings.Current.Editor.FontFamily;
            bool fontChanged = m_MonospaceFont == null;
            if (m_MonospaceFont != null)
            {
                if (m_IsMonospaceFontDynamic) 
                {
                    if (m_MonospaceFont.fontSize != fontSize || m_MonospaceFont.fontNames.Length == 0 || m_MonospaceFont.fontNames[0] != fontFamily) fontChanged = true;
                }
                else 
                {
                    if (!m_MonospaceFont.name.StartsWith(fontFamily)) fontChanged = true;
                }
            }

            if (fontChanged)
            {
                if (m_MonospaceFont != null && m_IsMonospaceFontDynamic) UnityEngine.Object.DestroyImmediate(m_MonospaceFont);
                
                m_MonospaceFont = LoadCodeFont(fontFamily, fontSize);
                m_IsMonospaceFontDynamic = (FindFontVariant(fontFamily, "Regular") ?? FindFontInResources(fontFamily)) == null;
                
                if (m_MonospaceFont != null && m_IsMonospaceFontDynamic) m_MonospaceFont.hideFlags = HideFlags.HideAndDontSave;
                if (m_MeasureLabel != null) m_MeasureLabel.style.unityFontDefinition = new StyleFontDefinition(m_MonospaceFont);
                if (m_SignatureHelpPopup != null && m_MonospaceFont != null) m_SignatureHelpPopup.style.unityFontDefinition = new StyleFontDefinition(m_MonospaceFont);
            }

            if (m_MeasureLabel != null)
            {
                m_MeasureLabel.style.fontSize = fontSize;
                MeasureCharWidth();
            }

            if (m_Caret != null)
            {
                m_Caret.style.height = LINE_HEIGHT;
            }

            m_ListView.Rebuild();

            TriggerCodeAnalysis(); // Re-trigger to apply new semantic colors
        }

        private void MeasureCharWidth()
        {
            if (this.panel == null)
            {
                this.UnregisterCallback<AttachToPanelEvent>(OnAttachedToPanelForMeasure);
                this.RegisterCallback<AttachToPanelEvent>(OnAttachedToPanelForMeasure);
                return;
            }
            DoMeasureCharWidth();
        }

        private void OnAttachedToPanelForMeasure(AttachToPanelEvent evt)
        {
            this.UnregisterCallback<AttachToPanelEvent>(OnAttachedToPanelForMeasure);
            DoMeasureCharWidth();
        }

        private void DoMeasureCharWidth()
        {
            var testLabel = new Label("X");
            int fontSize = CodeIDESettings.Current.Editor.FontSize;
            testLabel.style.fontSize = fontSize;
            if (m_MonospaceFont != null)
            {
                testLabel.style.unityFontDefinition = new StyleFontDefinition(m_MonospaceFont);
            }
            Add(testLabel);
            float measuredWidth = testLabel.MeasureTextSize("X", 0, MeasureMode.Undefined, 0, MeasureMode.Undefined).x;
            Remove(testLabel);

            if (measuredWidth > 0) m_CharWidth = measuredWidth;
        }
    }
}