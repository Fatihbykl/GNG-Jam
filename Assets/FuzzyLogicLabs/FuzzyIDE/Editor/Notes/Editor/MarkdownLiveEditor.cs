using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using FuzzyLogicLabs.FuzzyIDE.Editor.Core;
using FuzzyLogicLabs.FuzzyIDE.Editor.Notes.Core;
using FuzzyLogicLabs.FuzzyIDE.Editor.Notes.UI;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.Notes.LiveEditor
{
    /// <summary>
    /// Single-surface markdown editor for Fuzzy Notes. One focusable element owns the whole document:
    /// caret, selection, undo and keyboard input are managed here (no per-line TextFields).
    ///
    /// Live Preview: lines the caret/selection is not on hide their markdown markers and render styled
    /// (headings, lists, tasks, callouts, tables, images, code blocks). The caret line shows raw markdown.
    ///
    /// Virtualized: lines are absolutely positioned and only those near the viewport are measured,
    /// rendered and attached, so long notes stay fast. Off-screen line heights are estimated until needed.
    ///
    /// Reading mode (<see cref="ReadOnly"/>) is the same surface with no raw line, no caret and no edits;
    /// a plain click opens links. Sizes below are at zoom 1 and scale with <see cref="Zoom"/>.
    /// </summary>
    public partial class MarkdownLiveEditor : VisualElement
    {
        // ---- layout constants (sans-serif, Obsidian-like reading column)
        public const int BaseFontSize = 15;
        public const float BaseRowHeight = 24f;
        public const float PageMaxWidth = 760f;
        private const int PageViewMargin = 28;
        private const float ListIndentPx = 22f;
        private const float QuoteIndentPx = 16f;
        private const int CodeFontSize = 13;
        private const float CodeRowHeight = 20f;
        private const float CodePadX = 12f;
        private const float CodeHeaderHeight = 26f;
        private const float TableRowHeight = 28f;
        private const float TableCellPadX = 8f;
        private const float ImageMaxHeight = 420f;
        private const int EmbedFontSize = 13;
        private const float EmbedTitleHeight = 20f;
        private const float EmbedPad = 10f;
        private const int EmbedMaxChars = 2000;
        private const float VirtualMarginPx = 800f;
        public const float MinZoom = 0.6f;
        public const float MaxZoom = 2f;
        private const int ReadOnlyRange = -2; // m_ActiveFrom/To while no line is shown raw

        private static readonly int[] HeadingSizes = { 28, 24, 20, 18, 16, 15 };
        private const string MonoFontPath = "Assets/FuzzyLogicLabs/FuzzyIDE/Editor/Resources/Fonts/Jetbrains Mono/JetBrainsMono-Regular.ttf";
        private static Font s_MonoFont;

        /// <summary>Raised when the user edits the document (not for programmatic <see cref="SetText"/>).</summary>
        public event Action OnTextChanged;
        /// <summary>Raised after the caret or selection moved, including as a result of an edit.</summary>
        public event Action OnCaretMoved;
        /// <summary>Run button of a shell code block was pressed: (code, language).</summary>
        public event Action<string, string> OnRunCode;
        /// <summary>The pointer rests on a link: (target, rect in editor-local coordinates). A null target ends the hover.</summary>
        public event Action<string, Rect> OnLinkHover;

        /// <summary>Gets the first chance at key events (e.g. an open autocomplete popup). Return true to consume.</summary>
        public Func<KeyDownEvent, bool> KeyInterceptor;
        /// <summary>Resolves an image embed target to an absolute file path (null if not found).</summary>
        public Func<string, string> ImagePathResolver;
        /// <summary>Resolves a note embed (![[Note#Heading]]) to its title and markdown text; null if missing.</summary>
        public Func<string, EmbedContent> EmbedResolver;
        /// <summary>Whether a link target exists; unresolved links are drawn faded (like Obsidian).</summary>
        public Func<string, bool> LinkResolves;
        /// <summary>
        /// Ctrl+Click on a link (a plain click in reading mode): (target, toTheSide). toTheSide is true for Alt+Click.
        /// When nobody listens, the link is opened with <see cref="NoteLinkResolver"/>.
        /// </summary>
        public event Action<string, bool> OnLinkActivated;
        /// <summary>Right click, after the caret moved there (unless it hit the selection): the link target under the pointer or null.</summary>
        public event Action<string> OnContextMenu;
        /// <summary>Ctrl+Wheel: +1 to zoom in, -1 to zoom out. The owner decides and calls <see cref="SetAppearance"/>.</summary>
        public event Action<int> OnZoomRequested;

        public sealed class EmbedContent
        {
            public string Title;
            public string Text;
        }

        public NoteTextBuffer Buffer => m_Buffer;
        public string Text => m_Buffer.GetText();
        public TextPos Caret => m_Caret;
        public TextPos Anchor => m_Anchor;
        public bool HasSelection => m_Caret != m_Anchor;
        /// <summary>Content shown above the first line inside the scrolling page (inline title, properties).</summary>
        public VisualElement HeaderSlot => m_HeaderSlot;
        public float Zoom => m_Zoom;

        /// <summary>Reading mode: every line rendered, caret hidden, edits ignored.</summary>
        public bool ReadOnly
        {
            get => m_ReadOnly;
            set
            {
                if (m_ReadOnly == value) return;
                m_ReadOnly = value;
                m_ActiveFrom = m_ActiveTo = -1; // re-evaluate every line
                SetHoverTarget(null, default);
                RefreshVisuals(false);
            }
        }

        private readonly NoteTextBuffer m_Buffer = new NoteTextBuffer();
        private readonly List<LineView> m_Views = new List<LineView>();
        private float[] m_Tops = new float[2];

        private readonly ScrollView m_Scroll;
        private readonly VisualElement m_Page;
        private readonly VisualElement m_HeaderSlot;
        private readonly VisualElement m_LinesContainer;
        private readonly VisualElement m_Overlay;
        private readonly VisualElement m_CaretElement;
        private readonly List<VisualElement> m_SelectionRects = new List<VisualElement>();
        private readonly List<VisualElement> m_SearchRects = new List<VisualElement>();
        private readonly Dictionary<(int, bool, bool), Label> m_MeasureLabels = new Dictionary<(int, bool, bool), Label>();
        private readonly VisualElement m_MeasureHost;

        private byte[] m_CodeStates = new byte[0];
        private bool m_CodeDirty = true;
        private bool m_StructureDirty = true;
        private float m_LayoutWidth;

        // virtual window
        private int m_ShownFirst = -1;
        private int m_ShownLast = -1;
        private bool m_InRefresh;

        // scroll restore that waits for layout (see ApplyPendingScroll)
        private int m_PendingScrollLine = -1;
        private float m_PendingScrollOffset;
        private int m_PendingScrollTries;

        // active (raw) line range currently applied
        private int m_ActiveFrom = -1;
        private int m_ActiveTo = -1;

        private TextPos m_Caret;
        private TextPos m_Anchor;
        private float m_DesiredX = -1f;

        private bool m_Loading;
        private bool m_Dragging;
        private bool m_CaretBlinkOn = true;
        private bool m_ReadOnly;
        private float m_Zoom = 1f;
        private bool m_ReadableLineLength = true;

        // change notification bookkeeping
        private int m_TextVersion;
        private int m_NotifiedTextVersion = -1;
        private TextPos m_NotifiedCaret = new TextPos(-1, -1);
        private TextPos m_NotifiedAnchor = new TextPos(-1, -1);

        // multi-click tracking
        private int m_ClickCount;
        private double m_LastClickTime;
        private Vector2 m_LastClickPos;

        // link hover
        private string m_HoverTarget;
        private IVisualElementScheduledItem m_HoverTimer;

        // =====================================================================
        // Block context shared by the lines of a rendered table
        // =====================================================================

        private sealed class TableBlock
        {
            public int Start;
            public int End;
            public string Signature;
            public int Columns;
            public float[] Widths;
            public float WidthFor = -1f;
            public float CellPadY;             // keeps a one-line cell centred in a default-height row
            public bool Active;
        }

        // =====================================================================
        // Per-line view
        // =====================================================================

        private sealed class LineView : VisualElement
        {
            public readonly MarkdownLiveEditor Owner;

            public string Raw = "";
            public bool Dirty = true;          // layout must be recomputed
            public bool InfoValid;             // Info matches Raw + CodeState
            public bool Measured;              // Rows come from real text measurement
            public bool NeedsRender = true;
            public bool Active;
            public bool Hidden;                // inside a folded section
            public bool Shown;                 // attached in the virtual window
            public byte CodeState;
            public BlockInfo Info;
            public List<InlineSegment> Segments;

            // block context (UpdateStructure)
            public string CodeLang;
            public bool FenceOpening;
            public string CalloutType;
            public TableBlock Table;
            public List<string> TableCells;
            public bool Folded;

            public string Display = "";
            public int MapRawPrefix;           // raw chars replaced by the glyph in passive mode
            public int MapGlyphLen;            // glyph length in display chars

            public int FontSize = BaseFontSize;
            public bool Bold;
            public bool Mono;
            public float RowH = BaseRowHeight;
            public float ExtraTop;
            public float ExtraBottom;
            public float BlockHeight;          // non-text content (code header, table row, image)
            public float IndentX;
            public float GlyphW;
            public bool TextRows = true;
            public int EstimatedRows = 1;
            public Texture2D Image;
            public float ImageW, ImageH;
            public EmbedContent Embed;
            public readonly List<(int s, int e)> Rows = new List<(int, int)>();

            public float AppliedTop = -1f;
            public float AppliedHeight = -1f;

            private readonly List<Label> m_Labels = new List<Label>();
            private readonly VisualElement m_Bar;
            private readonly VisualElement m_Hr;
            private VisualElement m_CodeHeader;
            private Label m_CodeLang;
            private Button m_RunButton;
            private readonly List<Label> m_Cells = new List<Label>();
            private Image m_ImageElement;
            private VisualElement m_EmbedBox;
            private Label m_EmbedTitle;
            private Label m_EmbedText;
            private Label m_FoldArrow;
            private Label m_FoldBadge;

            public int RowCount => !TextRows ? 0 : Measured ? Math.Max(1, Rows.Count) : Math.Max(1, EstimatedRows);
            public float ContentHeight => Hidden ? 0f : ExtraTop + BlockHeight + RowCount * RowH + ExtraBottom;

            public LineView(MarkdownLiveEditor owner)
            {
                Owner = owner;
                pickingMode = PickingMode.Ignore;
                style.position = Position.Absolute;
                style.left = 0;
                style.right = 0;

                m_Bar = new VisualElement { pickingMode = PickingMode.Ignore, style = { position = Position.Absolute, left = 2, width = 3, top = 0, bottom = 0, display = DisplayStyle.None } };
                m_Hr = new VisualElement { pickingMode = PickingMode.Ignore, style = { position = Position.Absolute, left = 0, right = 0, height = 1, display = DisplayStyle.None } };
                Add(m_Bar);
                Add(m_Hr);
            }

            public void Render(ThemeSettings theme)
            {
                NeedsRender = false;
                bool code = Info.Kind == BlockKind.CodeLine || Info.Kind == BlockKind.CodeFence;
                bool tablePassive = Table != null && !Table.Active;
                Color calloutColor = CalloutType != null ? CalloutColor(CalloutType) : Color.clear;

                // background + rounded code block ends
                Color bg = Color.clear;
                if (code) bg = NotesTheme.CodeBackground;
                else if (CalloutType != null) bg = new Color(calloutColor.r, calloutColor.g, calloutColor.b, 0.08f);
                else if (tablePassive && Table.Start == IndexHint) bg = NotesTheme.BgHover;
                style.backgroundColor = bg;
                float topRadius = code && Info.Kind == BlockKind.CodeFence && FenceOpening ? 5f : 0f;
                float bottomRadius = code && Info.Kind == BlockKind.CodeFence && !FenceOpening ? 5f : 0f;
                style.borderTopLeftRadius = topRadius; style.borderTopRightRadius = topRadius;
                style.borderBottomLeftRadius = bottomRadius; style.borderBottomRightRadius = bottomRadius;
                style.borderBottomWidth = tablePassive && Info.Kind == BlockKind.Table ? 1 : 0;
                style.borderBottomColor = NotesTheme.BorderSubtle;

                // quote / callout bar
                bool bar = Info.Kind == BlockKind.Quote && (!Active || CalloutType != null);
                m_Bar.style.display = bar ? DisplayStyle.Flex : DisplayStyle.None;
                m_Bar.style.backgroundColor = CalloutType != null ? calloutColor : NotesTheme.Accent;

                // horizontal rule / table header separator
                bool hr = (Info.Kind == BlockKind.HorizontalRule && !Active) || (tablePassive && Info.Kind == BlockKind.TableSeparator);
                m_Hr.style.display = hr ? DisplayStyle.Flex : DisplayStyle.None;
                m_Hr.style.backgroundColor = NotesTheme.Border;
                m_Hr.style.top = Info.Kind == BlockKind.TableSeparator ? 0f : ExtraTop + BlockHeight * 0.5f;
                m_Hr.style.height = Info.Kind == BlockKind.TableSeparator ? 2f : 1f;

                RenderTextRows(theme, code, calloutColor);
                RenderCodeHeader(theme);
                RenderTable(theme, tablePassive);
                RenderImage();
                RenderEmbed(theme);
                RenderFold(theme);
            }

            private void RenderEmbed(ThemeSettings theme)
            {
                bool show = Embed != null && Info.Kind == BlockKind.Image && !Active;
                if (!show)
                {
                    if (m_EmbedBox != null) m_EmbedBox.style.display = DisplayStyle.None;
                    return;
                }

                if (m_EmbedBox == null)
                {
                    m_EmbedBox = new VisualElement
                    {
                        pickingMode = PickingMode.Ignore,
                        style =
                        {
                            position = Position.Absolute, right = 0,
                            borderLeftWidth = 3,
                            paddingLeft = EmbedPad, paddingRight = EmbedPad, paddingTop = 4,
                            borderTopRightRadius = 4, borderBottomRightRadius = 4
                        }
                    };
                    m_EmbedTitle = new Label { pickingMode = PickingMode.Ignore, style = { height = EmbedTitleHeight, fontSize = 12, unityFontStyleAndWeight = FontStyle.Bold, unityTextAlign = TextAnchor.MiddleLeft } };
                    m_EmbedText = new Label { pickingMode = PickingMode.Ignore, enableRichText = false, style = { fontSize = EmbedFontSize, whiteSpace = WhiteSpace.Normal } };
                    m_EmbedBox.Add(m_EmbedTitle);
                    m_EmbedBox.Add(m_EmbedText);
                    Add(m_EmbedBox);
                }

                m_EmbedBox.style.display = DisplayStyle.Flex;
                m_EmbedBox.style.left = IndentX;
                m_EmbedBox.style.top = ExtraTop + 4f;
                m_EmbedBox.style.height = BlockHeight - 8f;
                m_EmbedBox.style.borderLeftColor = NotesTheme.Accent;
                m_EmbedBox.style.backgroundColor = NotesTheme.BgRaised;
                m_EmbedTitle.text = "↪ " + Embed.Title;
                m_EmbedTitle.style.color = NotesTheme.TextNormal;
                m_EmbedText.text = Embed.Text;
                m_EmbedText.style.fontSize = Owner.Fs(EmbedFontSize);
                m_EmbedText.style.color = NotesTheme.TextMuted;
            }

            /// <summary>Line index hint; only valid during structure/render passes.</summary>
            public int IndexHint;

            private void RenderTextRows(ThemeSettings theme, bool code, Color calloutColor)
            {
                Color text = NotesTheme.TextNormal;
                if (CalloutType != null && Info.IsCallout && !Active) text = calloutColor;
                else if (Info.Kind == BlockKind.Quote && !Active) text = NotesTheme.TextMuted;
                else if (Info.Kind == BlockKind.CodeFence) text = NotesTheme.TextFaint;
                else if (Info.Kind == BlockKind.Task && Info.TaskMark == 'x' && !Active) text = NotesTheme.TextFaint;
                else if (Info.Kind == BlockKind.Image && !Active) text = NotesTheme.TextFaint;

                int count = TextRows ? Rows.Count : 0;
                while (m_Labels.Count < count)
                {
                    var l = new Label
                    {
                        pickingMode = PickingMode.Ignore,
                        enableRichText = false,
                        style =
                        {
                            position = Position.Absolute,
                            whiteSpace = WhiteSpace.Pre,
                            unityTextAlign = TextAnchor.MiddleLeft,
                            paddingLeft = 0, paddingRight = 0, paddingTop = 0, paddingBottom = 0,
                            marginLeft = 0, marginRight = 0, marginTop = 0, marginBottom = 0
                        }
                    };
                    m_Labels.Add(l);
                    Add(l);
                }

                bool richInline = !Active && Segments != null && Segments.Count > 0;
                for (int i = 0; i < m_Labels.Count; i++)
                {
                    var l = m_Labels[i];
                    if (i >= count) { l.style.display = DisplayStyle.None; continue; }

                    var (s, e) = Rows[i];
                    l.style.display = DisplayStyle.Flex;

                    if (code && Info.Kind == BlockKind.CodeLine)
                    {
                        l.enableRichText = true;
                        l.text = Owner.BuildCodeRichText(Display, s, e, CodeLang, theme);
                    }
                    else if (richInline)
                    {
                        l.enableRichText = true;
                        l.text = BuildInlineRichText(s, e, theme);
                    }
                    else
                    {
                        l.enableRichText = false;
                        l.text = Display.Substring(s, e - s);
                    }

                    l.style.fontSize = FontSize;
                    l.style.unityFontStyleAndWeight = Bold ? FontStyle.Bold : FontStyle.Normal;
                    l.style.unityFontDefinition = Mono ? MonoFontDefinition() : new StyleFontDefinition(StyleKeyword.Null);
                    l.style.color = text;
                    l.style.left = i == 0 ? IndentX : IndentX + GlyphW;
                    l.style.top = ExtraTop + BlockHeight + i * RowH;
                    l.style.height = RowH;
                }
            }

            private string BuildInlineRichText(int rowStart, int rowEnd, ThemeSettings theme)
            {
                string codeColor = "#" + ColorUtility.ToHtmlStringRGB(theme.MethodColor);
                string linkColor = "#" + ColorUtility.ToHtmlStringRGB(theme.KeywordColor);
                string tagColor = "#" + ColorUtility.ToHtmlStringRGB(theme.TypeColor);
                string unresolvedColor = "#" + ColorUtility.ToHtmlStringRGBA(new Color(theme.KeywordColor.r, theme.KeywordColor.g, theme.KeywordColor.b, 0.5f));
                var sb = new StringBuilder();
                int cur = rowStart;

                if (cur < MapGlyphLen)
                {
                    int gEnd = Math.Min(rowEnd, MapGlyphLen);
                    sb.Append(NoParse(Display.Substring(cur, gEnd - cur)));
                    cur = gEnd;
                }

                if (cur >= rowEnd) return sb.ToString();

                bool isTaskDone = Info.Kind == BlockKind.Task && Info.TaskMark == 'x' && !Active;

                for (int k = 0; k < Segments.Count; k++)
                {
                    var seg = Segments[k];
                    if (seg.DisplayEnd <= cur) continue;
                    if (seg.DisplayStart >= rowEnd) break;

                    int isectStart = Math.Max(cur, seg.DisplayStart);
                    int isectEnd = Math.Min(rowEnd, seg.DisplayEnd);
                    if (isectStart >= isectEnd) continue;

                    string part = NoParse(seg.DisplayText.Substring(isectStart - seg.DisplayStart, isectEnd - isectStart));
                    bool unresolved = seg.Style.HasFlag(InlineStyle.Link) && Owner.LinkResolves != null && !Owner.LinkResolves(seg.LinkTarget);
                    part = ApplyInlineStyle(part, seg.Style, codeColor, unresolved ? unresolvedColor : linkColor, tagColor);
                    if (isTaskDone && !seg.Style.HasFlag(InlineStyle.Strikethrough)) part = $"<s>{part}</s>";

                    sb.Append(part);
                    cur = isectEnd;
                }

                return sb.ToString();
            }

            private void RenderCodeHeader(ThemeSettings theme)
            {
                bool show = Info.Kind == BlockKind.CodeFence && FenceOpening && !Active;
                if (!show)
                {
                    if (m_CodeHeader != null) m_CodeHeader.style.display = DisplayStyle.None;
                    return;
                }

                if (m_CodeHeader == null)
                {
                    m_CodeHeader = new VisualElement
                    {
                        pickingMode = PickingMode.Ignore,
                        style =
                        {
                            position = Position.Absolute, left = 0, right = 0,
                            flexDirection = FlexDirection.Row, alignItems = Align.Center,
                            paddingLeft = CodePadX, paddingRight = 6
                        }
                    };
                    m_CodeLang = new Label { pickingMode = PickingMode.Ignore, style = { flexGrow = 1, fontSize = 11, unityFontStyleAndWeight = FontStyle.Bold } };
                    m_CodeHeader.Add(m_CodeLang);

                    var copy = MakeHeaderButton("Copy", "Copy code to clipboard", () => Owner.CopyCodeBlock(this));
                    m_CodeHeader.Add(copy);
                    m_RunButton = MakeHeaderButton("Run", "Run in Fuzzy Native Terminal", () => Owner.RunCodeBlock(this));
                    m_RunButton.style.backgroundColor = NotesTheme.WithAlpha(NotesTheme.Success, 0.25f);
                    m_CodeHeader.Add(m_RunButton);
                    Add(m_CodeHeader);
                }

                m_CodeHeader.style.display = DisplayStyle.Flex;
                m_CodeHeader.style.top = ExtraTop;
                m_CodeHeader.style.height = BlockHeight;
                m_CodeLang.text = string.IsNullOrEmpty(CodeLang) ? "code" : CodeLang;
                m_CodeLang.style.color = NotesTheme.TextFaint;
                m_RunButton.style.display = BlockParser.IsShellLanguage(CodeLang) ? DisplayStyle.Flex : DisplayStyle.None;
            }

            private static Button MakeHeaderButton(string text, string tooltip, Action onClick)
            {
                var b = new Button(onClick)
                {
                    text = text,
                    tooltip = tooltip,
                    style =
                    {
                        fontSize = 10, height = 18, marginLeft = 4,
                        paddingLeft = 6, paddingRight = 6, paddingTop = 0, paddingBottom = 0,
                        color = NotesTheme.TextNormal,
                        backgroundColor = NotesTheme.BgActive,
                        borderTopWidth = 0, borderBottomWidth = 0, borderLeftWidth = 0, borderRightWidth = 0,
                        borderTopLeftRadius = 3, borderTopRightRadius = 3, borderBottomLeftRadius = 3, borderBottomRightRadius = 3
                    }
                };
                b.AddToClassList(InteractiveClass);
                return b;
            }

            private void RenderTable(ThemeSettings theme, bool tablePassive)
            {
                int count = tablePassive && Info.Kind == BlockKind.Table && TableCells != null ? Math.Min(TableCells.Count, Table.Columns) : 0;
                while (m_Cells.Count < count)
                {
                    var l = new Label
                    {
                        pickingMode = PickingMode.Ignore,
                        enableRichText = true,
                        style =
                        {
                            position = Position.Absolute,
                            whiteSpace = WhiteSpace.Normal,
                            unityTextAlign = TextAnchor.UpperLeft,
                            paddingLeft = 0, paddingRight = 0, paddingTop = 0, paddingBottom = 0,
                            marginLeft = 0, marginRight = 0, marginTop = 0, marginBottom = 0
                        }
                    };
                    m_Cells.Add(l);
                    Add(l);
                }

                bool header = Table != null && Table.Start == IndexHint;
                float x = 0f;
                for (int c = 0; c < m_Cells.Count; c++)
                {
                    var l = m_Cells[c];
                    if (c >= count) { l.style.display = DisplayStyle.None; continue; }

                    float w = Table.Widths != null && c < Table.Widths.Length ? Table.Widths[c] : 80f;
                    l.style.display = DisplayStyle.Flex;
                    l.text = Owner.BuildCellRichText(TableCells[c], theme);
                    l.style.fontSize = Owner.Fs(BaseFontSize) - 1;
                    l.style.unityFontStyleAndWeight = header ? FontStyle.Bold : FontStyle.Normal;
                    l.style.color = theme.DefaultText;
                    l.style.left = x + TableCellPadX;
                    l.style.width = Math.Max(10f, w - 2 * TableCellPadX);
                    l.style.top = ExtraTop + Table.CellPadY;
                    l.style.height = Math.Max(0f, BlockHeight - 2 * Table.CellPadY);
                    x += w;
                }
            }

            private void RenderImage()
            {
                bool show = Image != null && Info.Kind == BlockKind.Image && !Active;
                if (!show)
                {
                    if (m_ImageElement != null) m_ImageElement.style.display = DisplayStyle.None;
                    return;
                }

                if (m_ImageElement == null)
                {
                    m_ImageElement = new Image { pickingMode = PickingMode.Ignore, scaleMode = ScaleMode.ScaleToFit, style = { position = Position.Absolute } };
                    Add(m_ImageElement);
                }
                m_ImageElement.image = Image;
                m_ImageElement.style.display = DisplayStyle.Flex;
                m_ImageElement.style.left = IndentX;
                m_ImageElement.style.top = ExtraTop + 4f;
                m_ImageElement.style.width = ImageW;
                m_ImageElement.style.height = ImageH;
            }

            private void RenderFold(ThemeSettings theme)
            {
                bool heading = Info.Kind == BlockKind.Heading;
                if (!heading)
                {
                    if (m_FoldArrow != null) m_FoldArrow.style.display = DisplayStyle.None;
                    if (m_FoldBadge != null) m_FoldBadge.style.display = DisplayStyle.None;
                    return;
                }

                if (m_FoldArrow == null)
                {
                    m_FoldArrow = new Label
                    {
                        tooltip = "Fold / unfold section",
                        style =
                        {
                            position = Position.Absolute, left = -20, width = 16,
                            unityTextAlign = TextAnchor.MiddleCenter, fontSize = 11
                        }
                    };
                    m_FoldArrow.AddToClassList(InteractiveClass);
                    m_FoldArrow.RegisterCallback<PointerDownEvent>(e =>
                    {
                        if (e.button != 0) return;
                        Owner.ToggleFoldAt(this);
                        e.StopPropagation();
                    });
                    Add(m_FoldArrow);

                    m_FoldBadge = new Label("⋯") { pickingMode = PickingMode.Ignore, style = { position = Position.Absolute, fontSize = 13, unityTextAlign = TextAnchor.MiddleLeft } };
                    Add(m_FoldBadge);
                }

                m_FoldArrow.style.display = DisplayStyle.Flex;
                m_FoldArrow.text = Folded ? "▸" : "▾";
                m_FoldArrow.style.top = ExtraTop;
                m_FoldArrow.style.height = RowH;
                m_FoldArrow.style.color = Folded ? NotesTheme.TextMuted : NotesTheme.TextFaint;

                m_FoldBadge.style.display = Folded ? DisplayStyle.Flex : DisplayStyle.None;
                if (Folded && Rows.Count > 0)
                {
                    int last = Rows.Count - 1;
                    var (s, e) = Rows[last];
                    float x = (last == 0 ? IndentX : IndentX + GlyphW) + Owner.Measure(Display.Substring(s, e - s), FontSize, Bold, Mono);
                    m_FoldBadge.style.left = x + 8f;
                    m_FoldBadge.style.top = ExtraTop + last * RowH;
                    m_FoldBadge.style.height = RowH;
                    m_FoldBadge.style.color = NotesTheme.Accent;
                }
            }
        }

        private const string InteractiveClass = "fuzzy-note-interactive";

        // =====================================================================
        // Construction
        // =====================================================================

        public MarkdownLiveEditor()
        {
            var theme = CodeIDESettings.Current.Theme;

            focusable = true;
            tabIndex = 0;
            style.flexGrow = 1;
            style.backgroundColor = NotesTheme.BgPrimary;
            style.overflow = Overflow.Hidden;

            m_Scroll = new ScrollView(ScrollViewMode.Vertical)
            {
                horizontalScrollerVisibility = ScrollerVisibility.Hidden,
                focusable = false
            };
            m_Scroll.style.flexGrow = 1;
            m_Scroll.contentContainer.focusable = false;

            m_Page = new VisualElement
            {
                pickingMode = PickingMode.Ignore,
                style =
                {
                    width = Length.Percent(100),
                    maxWidth = PageMaxWidth,
                    alignSelf = Align.Center,
                    paddingLeft = PageViewMargin, paddingRight = PageViewMargin,
                    paddingTop = 18, paddingBottom = 160,
                    flexShrink = 0
                }
            };

            // Header content has its own input handling: the editor ignores pointer and key events from it.
            m_HeaderSlot = new VisualElement { style = { flexShrink = 0 } };
            m_HeaderSlot.AddToClassList(InteractiveClass);
            m_Page.Add(m_HeaderSlot);

            m_LinesContainer = new VisualElement
            {
                pickingMode = PickingMode.Ignore,
                style = { flexShrink = 0, minHeight = BaseRowHeight }
            };

            m_Overlay = new VisualElement
            {
                pickingMode = PickingMode.Ignore,
                style = { position = Position.Absolute, left = 0, top = 0, right = 0, bottom = 0 }
            };

            m_CaretElement = new VisualElement
            {
                pickingMode = PickingMode.Ignore,
                style = { position = Position.Absolute, width = 2, backgroundColor = theme.CaretColor }
            };
            m_Overlay.Add(m_CaretElement);

            m_MeasureHost = new VisualElement { pickingMode = PickingMode.Ignore, style = { position = Position.Absolute, visibility = Visibility.Hidden } };
            foreach (int size in HeadingSizes) GetMeasureLabel(size, true, false);
            GetMeasureLabel(BaseFontSize, false, false);
            GetMeasureLabel(CodeFontSize, false, true);

            m_LinesContainer.Add(m_Overlay);
            m_Page.Add(m_LinesContainer);
            m_Scroll.Add(m_Page);
            Add(m_Scroll);
            Add(m_MeasureHost);

            m_Buffer.Changed += OnBufferChanged;
            m_Buffer.SetText("");

            // Input
            RegisterCallback<KeyDownEvent>(OnKeyDown, TrickleDown.TrickleDown);
            RegisterCallback<PointerDownEvent>(OnPointerDown);
            RegisterCallback<WheelEvent>(OnWheel, TrickleDown.TrickleDown);
            RegisterCallback<PointerMoveEvent>(OnPointerMove);
            RegisterCallback<PointerUpEvent>(OnPointerUp);
            RegisterCallback<PointerLeaveEvent>(e => SetHoverTarget(null, default));
            RegisterCallback<PointerCaptureOutEvent>(e => m_Dragging = false);
            RegisterCallback<FocusInEvent>(e => { m_CaretBlinkOn = true; RefreshVisuals(false); });
            RegisterCallback<FocusOutEvent>(e => RefreshVisuals(false));
            RegisterCallback<AttachToPanelEvent>(e => schedule.Execute(() => { MarkAllDirty(); RefreshVisuals(false); }));

            // Wrapping depends on the available width: relayout whenever it changes.
            m_LinesContainer.RegisterCallback<GeometryChangedEvent>(e =>
            {
                float w = m_LinesContainer.layout.width;
                if (float.IsNaN(w) || Mathf.Abs(w - m_LayoutWidth) < 0.5f) return;
                m_LayoutWidth = w;
                MarkAllDirty();
                RefreshVisuals(false);
            });

            // Virtualization: lay out lines that scroll into view.
            m_Scroll.verticalScroller.valueChanged += _ => { if (!m_InRefresh) RefreshVisuals(false); };
            m_Scroll.contentViewport.RegisterCallback<GeometryChangedEvent>(e => { if (!m_InRefresh) RefreshVisuals(false); });
            // the scroll range follows the content height only after layout: finish a restore then
            m_Scroll.contentContainer.RegisterCallback<GeometryChangedEvent>(e => { if (!m_InRefresh) ApplyPendingScroll(); });

            // user input wins over a restore that has not settled yet
            RegisterCallback<WheelEvent>(e => CancelPendingScroll(), TrickleDown.TrickleDown);
            RegisterCallback<PointerDownEvent>(e => CancelPendingScroll(), TrickleDown.TrickleDown);
            RegisterCallback<KeyDownEvent>(e => CancelPendingScroll(), TrickleDown.TrickleDown);

            schedule.Execute(() =>
            {
                m_CaretBlinkOn = !m_CaretBlinkOn;
                UpdateCaretVisibility();
            }).Every(530);
        }

        // =====================================================================
        // Public API
        // =====================================================================

        public void SetText(string text)
        {
            m_Loading = true;
            m_Folded.Clear();
            m_Buffer.SetText(text ?? "");
            m_Caret = m_Anchor = new TextPos(0, 0);
            m_DesiredX = -1f;
            m_SearchMatches.Clear();
            CancelPendingScroll();
            m_Scroll.scrollOffset = Vector2.zero;
            m_Loading = false;
            RefreshVisuals(false);
        }

        public void FocusEditor() => Focus();

        /// <summary>Text zoom and whether the page is limited to a readable column width.</summary>
        public void SetAppearance(float zoom, bool readableLineLength)
        {
            zoom = Mathf.Clamp(zoom, MinZoom, MaxZoom);
            if (Mathf.Approximately(zoom, m_Zoom) && readableLineLength == m_ReadableLineLength) return;
            m_Zoom = zoom;
            m_ReadableLineLength = readableLineLength;
            m_Page.style.maxWidth = readableLineLength ? new StyleLength(Px(PageMaxWidth)) : new StyleLength(StyleKeyword.None);

            // Measure labels for new sizes get their style on the next frame: lay out again then.
            foreach (int size in HeadingSizes) GetMeasureLabel(Fs(size), true, false);
            GetMeasureLabel(Fs(BaseFontSize), false, false);
            GetMeasureLabel(Fs(CodeFontSize), false, true);
            RefreshRendering();
            schedule.Execute(RefreshRendering);
        }

        public void Cut() { if (!m_ReadOnly) DoCut(); }
        public void Copy() => DoCopy();
        public void Paste() { if (!m_ReadOnly) DoPaste(); }

        public void SelectAll()
        {
            m_Anchor = new TextPos(0, 0);
            MoveCaret(m_Buffer.End, true);
        }

        /// <summary>Font size at the current zoom.</summary>
        private int Fs(int size) => Mathf.Max(6, Mathf.RoundToInt(size * m_Zoom));
        /// <summary>Length at the current zoom.</summary>
        private float Px(float value) => Mathf.Round(value * m_Zoom);

        private static readonly System.Text.RegularExpressions.Regex LinePrefixRegex =
            new System.Text.RegularExpressions.Regex(@"^(#{1,6}\s+|[-*]\s*\[[ xX/]\]\s+|[-*+]\s+|\d+\.\s+|>\s*(\[![\w-]+\][+-]?\s*)?)");

        /// <summary>Sets/replaces the markdown block prefix (heading, list, task, quote) of the caret line.</summary>
        public void ApplyLinePrefix(string prefix)
        {
            if (m_ReadOnly) return;
            string text = m_Buffer.GetLine(m_Caret.Line);
            var m = LinePrefixRegex.Match(text);
            int oldLen = m.Success ? m.Length : 0;
            string stripped = text.Substring(oldLen);

            var start = new TextPos(m_Caret.Line, 0);
            var end = new TextPos(m_Caret.Line, text.Length);
            m_Buffer.Replace(start, end, prefix + stripped, m_Caret);
            m_Caret = m_Anchor = new TextPos(m_Caret.Line, Math.Max(prefix.Length, m_Caret.Col - oldLen + prefix.Length));
            m_Buffer.SealHistory();
            AfterEdit();
        }

        /// <summary>Inserts a fenced code block on new lines below the caret line.</summary>
        public void InsertCodeBlock(string lang)
        {
            if (m_ReadOnly) return;
            string line = m_Buffer.GetLine(m_Caret.Line);
            var eol = new TextPos(m_Caret.Line, line.Length);
            string prefix = line.Length == 0 ? "" : "\n";
            string body = prefix + "```" + lang + "\n";
            m_Buffer.Replace(eol, eol, body + "\n```", m_Caret);

            m_Caret = m_Anchor = NoteTextBuffer.EndOfInsert(eol, body);
            m_Buffer.SealHistory();
            AfterEdit();
        }

        /// <summary>Inserts a block of lines after the caret line (e.g. a table or a divider).</summary>
        public void InsertBlock(string block)
        {
            if (m_ReadOnly) return;
            string line = m_Buffer.GetLine(m_Caret.Line);
            var eol = new TextPos(m_Caret.Line, line.Length);
            string text = (line.Length == 0 ? "" : "\n") + block;
            m_Caret = m_Anchor = m_Buffer.Replace(eol, eol, text, m_Caret);
            m_Buffer.SealHistory();
            AfterEdit();
        }

        public string GetSelectedText()
        {
            if (!HasSelection) return "";
            return m_Buffer.GetRange(m_Anchor, m_Caret);
        }

        /// <summary>Inserts text at the caret, replacing any selection.</summary>
        public void InsertText(string text)
        {
            if (m_ReadOnly) return;
            ReplaceSelection(text);
            m_Buffer.SealHistory();
        }

        /// <summary>Wraps the selection (or places the caret between prefix and suffix).</summary>
        public void WrapSelection(string prefix, string suffix)
        {
            if (m_ReadOnly) return;
            GetSelection(out var s, out var e);
            string selected = m_Buffer.GetRange(s, e);
            m_Buffer.Replace(s, e, prefix + selected + suffix, m_Caret);

            if (selected.Length == 0)
            {
                m_Caret = m_Anchor = NoteTextBuffer.EndOfInsert(s, prefix);
            }
            else
            {
                m_Anchor = NoteTextBuffer.EndOfInsert(s, prefix);
                m_Caret = NoteTextBuffer.EndOfInsert(m_Anchor, selected);
            }
            m_Buffer.SealHistory();
            AfterEdit();
        }

        /// <summary>Replaces a range as one undo step and puts the caret after the inserted text.</summary>
        public void ReplaceRange(TextPos a, TextPos b, string text)
        {
            if (m_ReadOnly) return;
            NoteTextBuffer.Order(ref a, ref b);
            m_Caret = m_Anchor = m_Buffer.Replace(m_Buffer.Clamp(a), m_Buffer.Clamp(b), text ?? "", m_Caret);
            m_Buffer.SealHistory();
            AfterEdit();
        }

        public void SetCaret(TextPos pos, bool ensureVisible = true)
        {
            m_Caret = m_Anchor = m_Buffer.Clamp(pos);
            m_DesiredX = -1f;
            m_Buffer.SealHistory();
            RefreshVisuals(ensureVisible);
        }

        /// <summary>Selects a range, unfolding and scrolling to it.</summary>
        public void Select(TextPos anchor, TextPos caret)
        {
            m_Anchor = m_Buffer.Clamp(anchor);
            m_Caret = m_Buffer.Clamp(caret);
            m_DesiredX = -1f;
            m_Buffer.SealHistory();
            RefreshVisuals(true);
        }

        /// <summary>Caret rectangle in this element's local coordinates.</summary>
        public Rect GetCaretRect()
        {
            if (m_Views.Count == 0) return new Rect(0, 0, 2, BaseRowHeight);
            EnsureLayout(m_Caret.Line);
            GetPoint(m_Caret, out float x, out float y, out float h);
            var p = m_LinesContainer.ChangeCoordinatesTo(this, new Vector2(x, y));
            return new Rect(p.x, p.y, 2f, h);
        }

        public struct ViewState
        {
            public TextPos Caret;
            public TextPos Anchor;
            // Scroll is kept as "line at the top of the viewport + pixels into it": line heights are
            // estimates until measured, so a raw pixel offset would land on a different line.
            public int TopLine;
            public float TopLineOffset;
            public int[] Folded;
        }

        public ViewState GetViewState()
        {
            // a restore that has not settled yet is still the real position
            int topLine = m_PendingScrollLine;
            float topOffset = m_PendingScrollOffset;
            if (topLine < 0)
            {
                float y = m_Scroll.scrollOffset.y - LinesOffset();
                topLine = FindLine(y);
                topOffset = topLine < m_Views.Count ? y - m_Tops[topLine] : 0f;
            }
            return new ViewState
            {
                Caret = m_Caret,
                Anchor = m_Anchor,
                TopLine = topLine,
                TopLineOffset = topOffset,
                Folded = new List<int>(m_Folded).ToArray()
            };
        }

        /// <summary>Restores caret, folds and scroll after <see cref="SetText"/> (values are clamped to the document).</summary>
        public void RestoreViewState(ViewState state)
        {
            m_Folded.Clear();
            if (state.Folded != null) foreach (int f in state.Folded) if (f < m_Buffer.LineCount) m_Folded.Add(f);
            m_StructureDirty = true;
            m_Caret = m_Buffer.Clamp(state.Caret);
            m_Anchor = m_Buffer.Clamp(state.Anchor);
            RefreshVisuals(false);

            m_PendingScrollLine = Mathf.Clamp(state.TopLine, 0, Math.Max(0, m_Views.Count - 1));
            m_PendingScrollOffset = state.TopLineOffset;
            m_PendingScrollTries = 0;
            ApplyPendingScroll();
        }

        private float LinesOffset() =>
            m_LinesContainer.panel != null ? m_LinesContainer.ChangeCoordinatesTo(m_Scroll.contentContainer, Vector2.zero).y : 0f;

        /// <summary>
        /// Scrolls to the restored line. The scroll view clamps to the content height of its last layout
        /// pass and lines get their real heights once they are laid out, so this re-applies on every
        /// content resize until the position holds (or the user scrolls first).
        /// </summary>
        private void ApplyPendingScroll()
        {
            if (m_PendingScrollLine < 0) return;
            if (m_PendingScrollLine >= m_Views.Count || ++m_PendingScrollTries > 30) { m_PendingScrollLine = -1; return; }
            if (m_LinesContainer.panel == null) { schedule.Execute(ApplyPendingScroll).StartingIn(16); return; }

            float target = Mathf.Max(0f, LinesOffset() + m_Tops[m_PendingScrollLine] + m_PendingScrollOffset);
            m_Scroll.scrollOffset = new Vector2(0f, target);
            RefreshVisuals(false);

            // measuring the newly shown lines may have moved the target line: check again after layout
            float now = Mathf.Max(0f, LinesOffset() + m_Tops[m_PendingScrollLine] + m_PendingScrollOffset);
            if (Mathf.Abs(m_Scroll.scrollOffset.y - now) > 0.5f || Mathf.Abs(target - now) > 0.5f)
                schedule.Execute(ApplyPendingScroll).StartingIn(16);
            else
                m_PendingScrollLine = -1;
        }

        private void CancelPendingScroll() => m_PendingScrollLine = -1;

        // =====================================================================
        // Buffer -> View sync
        // =====================================================================

        private void OnBufferChanged(int first, int removed, int inserted)
        {
            removed = Math.Min(removed, m_Views.Count - first);
            int reuse = Math.Min(removed, inserted);

            for (int k = 0; k < reuse; k++)
            {
                var v = m_Views[first + k];
                v.Raw = m_Buffer.GetLine(first + k);
                v.Dirty = true;
                v.InfoValid = false;
            }

            for (int k = reuse; k < removed; k++)
            {
                m_Views[first + reuse].RemoveFromHierarchy();
                m_Views.RemoveAt(first + reuse);
            }

            for (int k = reuse; k < inserted; k++)
            {
                var v = new LineView(this) { Raw = m_Buffer.GetLine(first + k) };
                v.EstimatedRows = EstimateRows(v.Raw);
                m_Views.Insert(first + k, v);
            }

            ShiftFolds(first, removed, inserted);
            m_ShownFirst = m_ShownLast = -1; // indices shifted: re-evaluate the whole window
            m_ActiveFrom = m_ActiveTo = -1;
            m_CodeDirty = true;
            m_StructureDirty = true;
            m_TextVersion++;
        }

        private void MarkAllDirty()
        {
            foreach (var v in m_Views)
            {
                v.Dirty = true;
                if (v.Table != null) v.Table.WidthFor = -1f;
            }
        }

        private int EstimateRows(string raw)
        {
            if (m_LayoutWidth <= 0f || string.IsNullOrEmpty(raw)) return 1;
            return Math.Max(1, Mathf.CeilToInt(raw.Length * Fs(BaseFontSize) * 0.5f / m_LayoutWidth));
        }

        // =====================================================================
        // Measurement
        // =====================================================================

        private static StyleFontDefinition MonoFontDefinition()
        {
            if (s_MonoFont == null) s_MonoFont = AssetDatabase.LoadAssetAtPath<Font>(MonoFontPath);
            return s_MonoFont != null ? new StyleFontDefinition(FontDefinition.FromFont(s_MonoFont)) : new StyleFontDefinition(StyleKeyword.Null);
        }

        private Label GetMeasureLabel(int size, bool bold, bool mono)
        {
            if (m_MeasureLabels.TryGetValue((size, bold, mono), out var l)) return l;
            l = new Label("X")
            {
                pickingMode = PickingMode.Ignore,
                style =
                {
                    position = Position.Absolute,
                    whiteSpace = WhiteSpace.Pre,
                    fontSize = size,
                    unityFontStyleAndWeight = bold ? FontStyle.Bold : FontStyle.Normal,
                    paddingLeft = 0, paddingRight = 0, paddingTop = 0, paddingBottom = 0,
                    marginLeft = 0, marginRight = 0, marginTop = 0, marginBottom = 0
                }
            };
            if (mono) l.style.unityFontDefinition = MonoFontDefinition();
            m_MeasureLabels[(size, bold, mono)] = l;
            m_MeasureHost.Add(l);
            return l;
        }

        private float Measure(string text, int size, bool bold, bool mono = false)
        {
            if (string.IsNullOrEmpty(text)) return 0f;
            var l = GetMeasureLabel(size, bold, mono);
            if (l.panel == null) return text.Length * size * 0.55f; // pre-attach estimate

            // MeasureTextSize trims trailing spaces; measure with a sentinel and subtract it.
            if (text[text.Length - 1] == ' ')
            {
                float withX = l.MeasureTextSize(text + "X", 0, MeasureMode.Undefined, 0, MeasureMode.Undefined).x;
                float x = l.MeasureTextSize("X", 0, MeasureMode.Undefined, 0, MeasureMode.Undefined).x;
                return withX - x;
            }
            return l.MeasureTextSize(text, 0, MeasureMode.Undefined, 0, MeasureMode.Undefined).x;
        }

        private float Measure(LineView v, string text) => Measure(text, v.FontSize, v.Bold, v.Mono);

        // =====================================================================
        // Structure: per-line block info and multi-line context (fences, callouts, tables, folds)
        // =====================================================================

        private void UpdateStructure()
        {
            int n = m_Views.Count;

            for (int i = 0; i < n; i++)
            {
                var v = m_Views[i];
                byte cs = i < m_CodeStates.Length ? m_CodeStates[i] : BlockParser.StateNone;
                if (!v.InfoValid || v.CodeState != cs)
                {
                    v.Info = BlockParser.Parse(v.Raw, cs);
                    v.CodeState = cs;
                    v.InfoValid = true;
                    v.Dirty = true;
                }
            }

            // fences + callouts
            bool inFence = false;
            string lang = null;
            string callout = null;
            for (int i = 0; i < n; i++)
            {
                var v = m_Views[i];
                var info = v.Info;
                string codeLang = null;
                bool opening = false;

                if (info.Kind == BlockKind.CodeFence)
                {
                    if (!inFence) { inFence = true; lang = info.Target; opening = true; }
                    else inFence = false;
                    codeLang = lang;
                }
                else if (info.Kind == BlockKind.CodeLine)
                {
                    codeLang = lang;
                }

                if (info.Kind == BlockKind.Quote)
                {
                    if (info.IsCallout) callout = info.Target;
                }
                else callout = null;
                string calloutType = info.Kind == BlockKind.Quote ? callout : null;

                if (v.CodeLang != codeLang || v.FenceOpening != opening || v.CalloutType != calloutType)
                {
                    v.CodeLang = codeLang;
                    v.FenceOpening = opening;
                    v.CalloutType = calloutType;
                    v.Dirty = true;
                }
            }

            // tables: a run of table lines whose second line is a separator
            for (int i = 0; i < n;)
            {
                var kind = m_Views[i].Info.Kind;
                if (kind != BlockKind.Table && kind != BlockKind.TableSeparator)
                {
                    SetTable(m_Views[i], null, null);
                    i++;
                    continue;
                }

                int j = i;
                var sig = new StringBuilder();
                while (j < n && (m_Views[j].Info.Kind == BlockKind.Table || m_Views[j].Info.Kind == BlockKind.TableSeparator))
                {
                    sig.Append(m_Views[j].Raw).Append('\n');
                    j++;
                }

                bool valid = j - i >= 2 && m_Views[i].Info.Kind == BlockKind.Table && m_Views[i + 1].Info.Kind == BlockKind.TableSeparator;
                TableBlock table = null;
                if (valid)
                {
                    string signature = sig.ToString();
                    table = m_Views[i].Table;
                    if (table == null || table.Signature != signature || table.Start != i || table.End != j - 1)
                    {
                        table = new TableBlock
                        {
                            Start = i,
                            End = j - 1,
                            Signature = signature,
                            Columns = BlockParser.SplitTableCells(m_Views[i].Raw).Count
                        };
                    }
                }

                for (int k = i; k < j; k++)
                {
                    var cells = table != null && m_Views[k].Info.Kind == BlockKind.Table ? BlockParser.SplitTableCells(m_Views[k].Raw) : null;
                    SetTable(m_Views[k], table, cells);
                }
                i = j;
            }

            // folding
            m_Folded.RemoveWhere(f => f >= n || m_Views[f].Info.Kind != BlockKind.Heading);
            int hideBelowLevel = 0; // > 0 while inside a folded section
            for (int i = 0; i < n; i++)
            {
                var v = m_Views[i];
                bool hidden;
                bool folded = false;
                if (v.Info.Kind == BlockKind.Heading)
                {
                    if (hideBelowLevel > 0 && v.Info.Level <= hideBelowLevel) hideBelowLevel = 0;
                    hidden = hideBelowLevel > 0;
                    folded = m_Folded.Contains(i);
                    if (!hidden && folded) hideBelowLevel = v.Info.Level;
                }
                else hidden = hideBelowLevel > 0;

                if (v.Folded != folded) { v.Folded = folded; v.NeedsRender = true; }
                if (v.Hidden != hidden) v.Hidden = hidden;
                v.IndexHint = i;
            }

            m_StructureDirty = false;
        }

        private static void SetTable(LineView v, TableBlock table, List<string> cells)
        {
            bool cellsChanged = !SameCells(v.TableCells, cells);
            if (v.Table == table && !cellsChanged) return;
            v.Table = table;
            v.TableCells = cells;
            v.Dirty = true;
        }

        private static bool SameCells(List<string> a, List<string> b)
        {
            if (a == null || b == null) return a == b;
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++) if (a[i] != b[i]) return false;
            return true;
        }

        // =====================================================================
        // Line layout (style, display text, wrapping)
        // =====================================================================

        private void EnsureLayout(int i)
        {
            if (i < 0 || i >= m_Views.Count) return;
            var v = m_Views[i];
            if (v.Dirty || !v.Measured) LayoutLine(i);
        }

        private void LayoutLine(int i)
        {
            var v = m_Views[i];
            var info = v.Info;
            string raw = v.Raw;

            v.Bold = false;
            v.Mono = false;
            v.FontSize = Fs(BaseFontSize);
            v.RowH = Px(BaseRowHeight);
            v.ExtraTop = 0f;
            v.ExtraBottom = 0f;
            v.BlockHeight = 0f;
            v.IndentX = 0f;
            v.TextRows = true;
            v.Image = null;
            v.Embed = null;
            v.Segments = null;
            v.Display = raw;
            v.MapRawPrefix = 0;
            v.MapGlyphLen = 0;

            bool passive = !v.Active;

            switch (info.Kind)
            {
                case BlockKind.Heading:
                    v.FontSize = Fs(HeadingSizes[Mathf.Clamp(info.Level, 1, 6) - 1]);
                    v.Bold = true;
                    v.RowH = Mathf.Round(v.FontSize * 1.45f);
                    v.ExtraTop = Px(info.Level <= 2 ? 10f : 6f);
                    if (passive) BuildInlineDisplay(v, info.PrefixLen, "");
                    break;

                case BlockKind.CodeLine:
                    v.Mono = true;
                    v.FontSize = Fs(CodeFontSize);
                    v.RowH = Px(CodeRowHeight);
                    v.IndentX = CodePadX;
                    break;

                case BlockKind.CodeFence:
                    v.Mono = true;
                    v.FontSize = Fs(CodeFontSize);
                    v.RowH = Px(CodeRowHeight);
                    v.IndentX = CodePadX;
                    if (v.FenceOpening) v.ExtraTop = 4f; else v.ExtraBottom = 4f;
                    if (passive) MakeBlockLine(v, v.FenceOpening ? Px(CodeHeaderHeight) : 8f);
                    break;

                case BlockKind.HorizontalRule:
                    if (passive) MakeBlockLine(v, Px(BaseRowHeight));
                    break;

                case BlockKind.Table:
                case BlockKind.TableSeparator:
                    if (v.Table != null && !v.Table.Active)
                    {
                        EnsureTableWidths(v.Table);
                        MakeBlockLine(v, info.Kind == BlockKind.TableSeparator ? 2f : TableRowContentHeight(v, i == v.Table.Start));
                    }
                    else if (passive) BuildInlineDisplay(v, 0, "");
                    break;

                case BlockKind.Image:
                    if (passive)
                    {
                        if (NoteImageCache.IsImageFile(info.Target)) LayoutImage(v, info.Target);
                        else LayoutEmbed(v, info.Target);
                    }
                    break;

                case BlockKind.Bullet:
                case BlockKind.Task:
                case BlockKind.Numbered:
                    if (passive)
                    {
                        BuildInlineDisplay(v, info.PrefixLen, info.Glyph);
                        v.IndentX = info.IndentLevel * Px(ListIndentPx);
                    }
                    break;

                case BlockKind.Quote:
                    if (passive)
                    {
                        BuildInlineDisplay(v, info.PrefixLen, info.IsCallout ? info.Glyph : "");
                        if (info.IsCallout && v.Display.Length == v.MapGlyphLen)
                        {
                            // "> [!warning]" without a title shows the type name, like Obsidian.
                            v.Display += char.ToUpperInvariant(info.Target[0]) + info.Target.Substring(1);
                            v.MapGlyphLen = v.Display.Length;
                        }
                        v.IndentX = Px(QuoteIndentPx);
                        v.Bold = info.IsCallout;
                    }
                    else if (v.CalloutType != null)
                    {
                        v.IndentX = Px(QuoteIndentPx);
                    }
                    break;

                default:
                    if (passive) BuildInlineDisplay(v, 0, "");
                    break;
            }

            v.GlyphW = v.MapGlyphLen > 0 ? Measure(v, v.Display.Substring(0, v.MapGlyphLen)) : 0f;

            v.Rows.Clear();
            if (v.TextRows)
            {
                // wrap the body (everything after the glyph)
                string body = v.Display.Substring(v.MapGlyphLen);
                float avail = m_LayoutWidth > 0f ? m_LayoutWidth - v.IndentX - v.GlyphW - (v.Mono ? CodePadX : 0f) : 0f;
                var rows = LineWrapper.Wrap(body, t => Measure(v, t), avail);
                for (int r = 0; r < rows.Count; r++)
                {
                    int s = rows[r].start + v.MapGlyphLen;
                    int e = rows[r].end + v.MapGlyphLen;
                    if (r == 0) s = 0;
                    v.Rows.Add((s, e));
                }
            }

            v.Measured = true;
            v.Dirty = false;
            v.NeedsRender = true;
        }

        /// <summary>A passive line drawn as a fixed-height block (rule, code header, table row, image) instead of text.</summary>
        private static void MakeBlockLine(LineView v, float height)
        {
            v.TextRows = false;
            v.Display = "";
            v.MapRawPrefix = v.Raw.Length;
            v.MapGlyphLen = 0;
            v.BlockHeight = height;
        }

        private void BuildInlineDisplay(LineView v, int prefixLen, string glyph)
        {
            string rawBody = v.Raw.Substring(prefixLen);
            v.Segments = InlineTokenizer.Tokenize(rawBody, baseOffset: prefixLen);

            var sb = new StringBuilder();
            sb.Append(glyph);
            int dispOffset = glyph.Length;
            for (int k = 0; k < v.Segments.Count; k++)
            {
                var seg = v.Segments[k];
                seg.DisplayStart = dispOffset;
                sb.Append(seg.DisplayText);
                dispOffset += seg.DisplayText.Length;
                seg.DisplayEnd = dispOffset;
            }

            v.Display = sb.ToString();
            v.MapRawPrefix = prefixLen;
            v.MapGlyphLen = glyph.Length;
        }

        private void LayoutImage(LineView v, string target)
        {
            string path = ImagePathResolver != null ? ImagePathResolver(target) : null;
            var tex = path != null ? NoteImageCache.Load(path) : null;
            if (tex == null || tex.width <= 0 || tex.height <= 0)
            {
                v.Display = "🖼 " + target + (path == null ? "  (not found)" : "  (unsupported format)");
                v.MapRawPrefix = v.Raw.Length;
                v.MapGlyphLen = v.Display.Length;
                return;
            }

            float maxW = Mathf.Max(40f, m_LayoutWidth - v.IndentX);
            float w = Mathf.Min(tex.width, maxW);
            float h = w * tex.height / tex.width;
            if (h > ImageMaxHeight)
            {
                h = ImageMaxHeight;
                w = h * tex.width / tex.height;
            }

            MakeBlockLine(v, h + 8f);
            v.Image = tex;
            v.ImageW = w;
            v.ImageH = h;
        }

        /// <summary>![[Note]] / ![[Note#Heading]]: shows the referenced text in a read-only box.</summary>
        private void LayoutEmbed(LineView v, string target)
        {
            var content = EmbedResolver != null ? EmbedResolver(target) : null;
            if (content == null)
            {
                v.Display = "↪ " + target + "  (not found)";
                v.MapRawPrefix = v.Raw.Length;
                v.MapGlyphLen = v.Display.Length;
                return;
            }

            string text = content.Text ?? "";
            if (text.Length > EmbedMaxChars) text = text.Substring(0, EmbedMaxChars) + "…";
            if (text.Trim().Length == 0) text = "(empty)";
            v.Embed = new EmbedContent { Title = content.Title, Text = text };

            float width = Mathf.Max(80f, m_LayoutWidth - v.IndentX - 2 * EmbedPad - 3f);
            float textH = MeasureWrapped(text, width, Fs(EmbedFontSize));
            MakeBlockLine(v, textH + EmbedTitleHeight + 4f + 8f + 8f);
        }

        private readonly Dictionary<int, Label> m_WrapMeasureLabels = new Dictionary<int, Label>();

        private float MeasureWrapped(string text, float width, int fontSize, bool bold = false)
        {
            int key = fontSize * 2 + (bold ? 1 : 0);
            if (!m_WrapMeasureLabels.TryGetValue(key, out var label))
            {
                label = new Label
                {
                    pickingMode = PickingMode.Ignore,
                    enableRichText = false,
                    style =
                    {
                        position = Position.Absolute, whiteSpace = WhiteSpace.Normal, fontSize = fontSize,
                        unityFontStyleAndWeight = bold ? FontStyle.Bold : FontStyle.Normal,
                        paddingLeft = 0, paddingRight = 0, paddingTop = 0, paddingBottom = 0
                    }
                };
                m_WrapMeasureLabels[key] = label;
                m_MeasureHost.Add(label);
            }

            if (label.panel == null)
            {
                int lines = 0;
                foreach (var l in text.Split('\n')) lines += Math.Max(1, Mathf.CeilToInt(l.Length * fontSize * 0.55f / width));
                return lines * fontSize * 1.4f;
            }
            return label.MeasureTextSize(text, width, MeasureMode.Exactly, 0, MeasureMode.Undefined).y;
        }

        /// <summary>Re-renders every line (e.g. after notes were created/deleted so link resolution changed).</summary>
        public void RefreshRendering()
        {
            MarkAllDirty();
            RefreshVisuals(false);
        }

        private void EnsureTableWidths(TableBlock table)
        {
            if (table.WidthFor == m_LayoutWidth && table.Widths != null) return;

            var widths = new float[table.Columns];
            for (int line = table.Start; line <= table.End; line++)
            {
                var cells = m_Views[line].TableCells;
                if (cells == null) continue;
                bool header = line == table.Start;
                for (int c = 0; c < Math.Min(cells.Count, table.Columns); c++)
                {
                    float w = Measure(PlainInline(cells[c]), Fs(BaseFontSize) - 1, header) + 2 * TableCellPadX + 2f;
                    if (w > widths[c]) widths[c] = w;
                }
            }

            float total = 0f;
            for (int c = 0; c < widths.Length; c++) { widths[c] = Mathf.Max(48f, widths[c]); total += widths[c]; }
            if (m_LayoutWidth > 0f && total > m_LayoutWidth) FitColumns(widths, m_LayoutWidth);

            int fs = Fs(BaseFontSize) - 1;
            table.CellPadY = Mathf.Max(4f, (Px(TableRowHeight) - MeasureWrapped("Ag", 1000f, fs)) * 0.5f);
            table.Widths = widths;
            table.WidthFor = m_LayoutWidth;
        }

        /// <summary>
        /// Shrinks columns to fit <paramref name="available"/>: columns narrower than an even share keep
        /// their natural width, the wide ones split what is left and their cells wrap.
        /// </summary>
        private static void FitColumns(float[] widths, float available)
        {
            var order = new List<int>();
            for (int c = 0; c < widths.Length; c++) order.Add(c);
            order.Sort((a, b) => widths[a].CompareTo(widths[b]));

            float remaining = available;
            for (int k = 0; k < order.Count; k++)
            {
                float share = remaining / (order.Count - k);
                if (widths[order[k]] <= share)
                {
                    remaining -= widths[order[k]];
                    continue;
                }
                for (int r = k; r < order.Count; r++) widths[order[r]] = Mathf.Max(32f, share);
                break;
            }
        }

        /// <summary>Height of a rendered table row: tall enough for its most wrapped cell.</summary>
        private float TableRowContentHeight(LineView v, bool header)
        {
            float rowH = Px(TableRowHeight);
            var table = v.Table;
            var cells = v.TableCells;
            if (cells == null || table.Widths == null) return rowH;

            int fs = Fs(BaseFontSize) - 1;
            float tallest = 0f;
            for (int c = 0; c < Math.Min(cells.Count, table.Widths.Length); c++)
            {
                // a little slack: cells render rich text (bold code spans) that measures slightly wider
                float w = Math.Max(10f, table.Widths[c] - 2 * TableCellPadX) - 2f;
                tallest = Mathf.Max(tallest, MeasureWrapped(PlainInline(cells[c]), w, fs, header));
            }
            return Mathf.Max(rowH, Mathf.Ceil(tallest + 2 * table.CellPadY));
        }

        private static string PlainInline(string text)
        {
            var sb = new StringBuilder();
            foreach (var seg in InlineTokenizer.Tokenize(text ?? "")) sb.Append(seg.DisplayText);
            return sb.ToString();
        }

        // =====================================================================
        // Rich text helpers
        // =====================================================================

        private static string NoParse(string s) => string.IsNullOrEmpty(s) ? "" : "<noparse>" + s + "</noparse>";

        private static string ApplyInlineStyle(string part, InlineStyle style, string codeColor, string linkColor, string tagColor)
        {
            if (style.HasFlag(InlineStyle.Code)) part = $"<b><color={codeColor}>{part}</color></b>";
            if (style.HasFlag(InlineStyle.Link)) part = $"<u><color={linkColor}>{part}</color></u>";
            if (style.HasFlag(InlineStyle.Tag)) part = $"<color={tagColor}>{part}</color>";
            if (style.HasFlag(InlineStyle.Bold)) part = $"<b>{part}</b>";
            if (style.HasFlag(InlineStyle.Italic)) part = $"<i>{part}</i>";
            if (style.HasFlag(InlineStyle.Strikethrough)) part = $"<s>{part}</s>";
            return part;
        }

        private string BuildCellRichText(string cell, ThemeSettings theme)
        {
            string codeColor = "#" + ColorUtility.ToHtmlStringRGB(theme.MethodColor);
            string linkColor = "#" + ColorUtility.ToHtmlStringRGB(theme.KeywordColor);
            string tagColor = "#" + ColorUtility.ToHtmlStringRGB(theme.TypeColor);
            var sb = new StringBuilder();
            foreach (var seg in InlineTokenizer.Tokenize(cell ?? ""))
                sb.Append(ApplyInlineStyle(NoParse(seg.DisplayText), seg.Style, codeColor, linkColor, tagColor));
            return sb.ToString();
        }

        private string BuildCodeRichText(string line, int start, int end, string lang, ThemeSettings theme)
        {
            var sb = new StringBuilder();
            int cur = start;
            foreach (var t in CodeHighlighter.Tokenize(line, lang))
            {
                int ts = Math.Max(t.Start, start);
                int te = Math.Min(t.Start + t.Length, end);
                if (te <= ts) continue;
                if (ts > cur) sb.Append(NoParse(line.Substring(cur, ts - cur)));
                sb.Append("<color=#").Append(ColorUtility.ToHtmlStringRGB(TokenColor(t.Kind, theme))).Append('>')
                  .Append(NoParse(line.Substring(ts, te - ts))).Append("</color>");
                cur = te;
            }
            if (cur < end) sb.Append(NoParse(line.Substring(cur, end - cur)));
            return sb.ToString();
        }

        private static Color TokenColor(CodeTokenKind kind, ThemeSettings theme)
        {
            switch (kind)
            {
                case CodeTokenKind.Keyword: return theme.KeywordColor;
                case CodeTokenKind.Type: return theme.TypeColor;
                case CodeTokenKind.String: return theme.StringColor;
                case CodeTokenKind.Comment: return theme.CommentColor;
                case CodeTokenKind.Variable: return theme.VariableColor;
                default: return new Color(0.71f, 0.81f, 0.66f, 1f);
            }
        }

        private static Color CalloutColor(string type)
        {
            switch (type)
            {
                case "tip": case "hint": case "important": return new Color(0.2f, 0.75f, 0.7f, 1f);
                case "success": case "check": case "done": return new Color(0.3f, 0.75f, 0.4f, 1f);
                case "question": case "help": case "faq": return new Color(0.9f, 0.75f, 0.25f, 1f);
                case "warning": case "caution": case "attention": return new Color(0.95f, 0.55f, 0.2f, 1f);
                case "failure": case "fail": case "missing": case "danger": case "error": case "bug": return new Color(0.9f, 0.3f, 0.3f, 1f);
                case "example": return new Color(0.65f, 0.45f, 0.9f, 1f);
                case "quote": case "cite": return new Color(0.6f, 0.6f, 0.6f, 1f);
                case "abstract": case "summary": case "todo": return new Color(0.3f, 0.7f, 0.9f, 1f);
                default: return new Color(0.3f, 0.55f, 0.95f, 1f);
            }
        }

        // =====================================================================
        // Sync: structure, active lines, virtual window
        // =====================================================================

        private void SyncLayout()
        {
            int n = m_Views.Count;

            if (m_CodeDirty || m_CodeStates.Length != n)
            {
                m_CodeStates = BlockParser.ComputeCodeStates(m_Buffer.Lines);
                m_CodeDirty = false;
                m_StructureDirty = true;
            }
            if (m_StructureDirty) UpdateStructure();

            UpdateActiveLines();

            // Lay out what is (about to be) visible plus the caret/anchor lines; estimate the rest.
            ComputeTops();
            GetVisibleRange(out int first, out int last);
            bool changed = LayoutRange(first, last);
            EnsureLayout(m_Caret.Line);
            EnsureLayout(m_Anchor.Line);
            if (changed)
            {
                ComputeTops();
                GetVisibleRange(out first, out last);
                LayoutRange(first, last);
                ComputeTops();
            }

            ApplyVirtualWindow(first, last);
        }

        private void UpdateActiveLines()
        {
            GetSelection(out var s, out var e);
            int from = s.Line, to = e.Line;

            if (m_ReadOnly) from = to = ReadOnlyRange;
            else
            {
                // A table is edited as a whole: any caret inside shows all of its rows raw.
                for (int i = from; i <= to && i < m_Views.Count; i++)
                {
                    var t = m_Views[i].Table;
                    if (t == null) continue;
                    from = Math.Min(from, t.Start);
                    to = Math.Max(to, t.End);
                }
            }

            if (from == m_ActiveFrom && to == m_ActiveTo) return;

            int lo = Math.Min(from, m_ActiveFrom < 0 ? from : m_ActiveFrom);
            int hi = Math.Max(to, m_ActiveTo < 0 ? to : m_ActiveTo);
            if (m_ActiveFrom < 0 || from < 0) { lo = 0; hi = m_Views.Count - 1; } // indices shifted or mode changed: re-evaluate all

            for (int i = Math.Max(0, lo); i <= hi && i < m_Views.Count; i++)
            {
                var v = m_Views[i];
                bool want = i >= from && i <= to;
                if (v.Active != want) { v.Active = want; v.Dirty = true; }
                if (v.Table != null)
                {
                    bool tableActive = v.Table.Start <= to && v.Table.End >= from;
                    if (v.Table.Active != tableActive)
                    {
                        // the whole table switches between grid and raw markdown
                        v.Table.Active = tableActive;
                        for (int k = v.Table.Start; k <= v.Table.End && k < m_Views.Count; k++) m_Views[k].Dirty = true;
                    }
                }
            }

            m_ActiveFrom = from;
            m_ActiveTo = to;
        }

        private bool LayoutRange(int first, int last)
        {
            bool changed = false;
            for (int i = first; i <= last && i < m_Views.Count; i++)
            {
                var v = m_Views[i];
                if (v.Hidden) continue;
                if (v.Dirty || !v.Measured)
                {
                    float before = v.ContentHeight;
                    LayoutLine(i);
                    if (Math.Abs(before - v.ContentHeight) > 0.01f) changed = true;
                }
            }
            return changed;
        }

        private void ComputeTops()
        {
            int n = m_Views.Count;
            if (m_Tops.Length < n + 1) m_Tops = new float[Math.Max(n + 1, m_Tops.Length * 2)];
            float y = 0f;
            for (int i = 0; i < n; i++)
            {
                m_Tops[i] = y;
                y += m_Views[i].ContentHeight;
            }
            m_Tops[n] = y;
            m_LinesContainer.style.height = Math.Max(BaseRowHeight, y);
        }

        private void GetVisibleRange(out int first, out int last)
        {
            int n = m_Views.Count;
            float viewH = m_Scroll.contentViewport != null ? m_Scroll.contentViewport.layout.height : float.NaN;
            if (float.IsNaN(viewH) || viewH <= 0f) viewH = 1000f;

            float offset = 0f;
            if (m_LinesContainer.panel != null)
                offset = m_LinesContainer.ChangeCoordinatesTo(m_Scroll.contentContainer, Vector2.zero).y;

            float top = m_Scroll.scrollOffset.y - offset - VirtualMarginPx;
            float bottom = m_Scroll.scrollOffset.y - offset + viewH + VirtualMarginPx;
            first = FindLine(top);
            last = FindLine(bottom);
            if (n == 0) { first = 0; last = -1; }
        }

        private void ApplyVirtualWindow(int first, int last)
        {
            var theme = CodeIDESettings.Current.Theme;

            // detach lines that left the window
            if (m_ShownFirst >= 0)
            {
                for (int i = m_ShownFirst; i <= m_ShownLast && i < m_Views.Count; i++)
                {
                    if (i >= first && i <= last) continue;
                    Detach(m_Views[i]);
                }
            }
            else
            {
                // Line indices shifted (edit or reload): check every line once.
                for (int i = 0; i < m_Views.Count; i++)
                {
                    if (m_Views[i].Shown && (i < first || i > last)) Detach(m_Views[i]);
                }
            }

            for (int i = first; i <= last && i < m_Views.Count; i++)
            {
                var v = m_Views[i];
                v.IndexHint = i;
                if (v.Hidden) { Detach(v); continue; }

                if (!v.Shown)
                {
                    m_LinesContainer.Insert(Math.Max(0, m_LinesContainer.childCount - 1), v); // keep the overlay on top
                    v.Shown = true;
                    v.NeedsRender = true;
                }

                float top = m_Tops[i];
                float h = v.ContentHeight;
                if (v.AppliedTop != top) { v.style.top = top; v.AppliedTop = top; }
                if (v.AppliedHeight != h) { v.style.height = h; v.AppliedHeight = h; }
                if (v.NeedsRender) v.Render(theme);
            }

            m_ShownFirst = first;
            m_ShownLast = last;
        }

        private static void Detach(LineView v)
        {
            if (!v.Shown) return;
            v.RemoveFromHierarchy();
            v.Shown = false;
        }

        // =====================================================================
        // Geometry helpers
        // =====================================================================

        private float Top(int line) => m_Tops[line];

        private int FindLine(float y)
        {
            int n = m_Views.Count;
            if (n == 0) return 0;
            if (y <= 0f) return FirstVisibleFrom(0, 1);
            if (y >= m_Tops[n]) return FirstVisibleFrom(n - 1, -1);
            int lo = 0, hi = n - 1;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (m_Tops[mid] <= y) lo = mid; else hi = mid - 1;
            }
            // zero-height (folded) lines share a top with the next visible line
            return m_Views[lo].Hidden ? FirstVisibleFrom(lo, -1) : lo;
        }

        private int FirstVisibleFrom(int i, int dir)
        {
            int n = m_Views.Count;
            int j = i;
            while (j >= 0 && j < n && m_Views[j].Hidden) j += dir;
            if (j < 0 || j >= n)
            {
                j = i;
                while (j >= 0 && j < n && m_Views[j].Hidden) j -= dir;
            }
            return Mathf.Clamp(j, 0, n - 1);
        }

        private static int RawToDisp(LineView v, int col)
        {
            if (v.Segments != null && v.Segments.Count > 0)
            {
                if (col < v.MapRawPrefix) return col == 0 ? 0 : v.MapGlyphLen;

                for (int i = 0; i < v.Segments.Count; i++)
                {
                    var seg = v.Segments[i];
                    if (col >= seg.RawStart && col <= seg.RawEnd)
                    {
                        if (col < seg.RawContentStart) return seg.DisplayStart;
                        if (col > seg.RawContentEnd) return seg.DisplayEnd;
                        return seg.DisplayStart + (col - seg.RawContentStart);
                    }
                }
                return v.Display.Length;
            }

            if (v.MapRawPrefix == 0 && v.MapGlyphLen == 0) return Math.Min(col, v.Display.Length);
            if (col >= v.MapRawPrefix) return Math.Min(col - v.MapRawPrefix + v.MapGlyphLen, v.Display.Length);
            return col == 0 ? 0 : v.MapGlyphLen;
        }

        private static int DispToRaw(LineView v, int d)
        {
            if (v.Segments != null && v.Segments.Count > 0)
            {
                if (d < v.MapGlyphLen)
                    return d * 2 >= v.MapGlyphLen ? v.MapRawPrefix : 0;

                for (int i = 0; i < v.Segments.Count; i++)
                {
                    var seg = v.Segments[i];
                    if (d >= seg.DisplayStart && d <= seg.DisplayEnd)
                    {
                        int offset = d - seg.DisplayStart;
                        return Mathf.Clamp(seg.RawContentStart + offset, seg.RawStart, seg.RawEnd);
                    }
                }
                return v.Raw.Length;
            }

            if (v.MapRawPrefix == 0 && v.MapGlyphLen == 0) return d;
            if (d >= v.MapGlyphLen) return Math.Min(d - v.MapGlyphLen + v.MapRawPrefix, v.Raw.Length);
            return d * 2 >= v.MapGlyphLen ? v.MapRawPrefix : 0;
        }

        private static InlineSegment GetSegmentAtDisp(LineView v, int d)
        {
            if (v.Segments == null) return null;
            for (int i = 0; i < v.Segments.Count; i++)
            {
                var seg = v.Segments[i];
                if (d >= seg.DisplayStart && d < seg.DisplayEnd)
                    return seg;
            }
            return null;
        }

        private static int RowOfDisp(LineView v, int d)
        {
            for (int r = v.Rows.Count - 1; r > 0; r--)
                if (d >= v.Rows[r].s) return r;
            return 0;
        }

        private static float RowX0(LineView v, int row) => row == 0 ? v.IndentX : v.IndentX + v.GlyphW;

        private float XInRow(LineView v, int row, int d)
        {
            if (v.Rows.Count == 0) return RowX0(v, 0);
            var (s, e) = v.Rows[row];
            d = Mathf.Clamp(d, s, e);
            return RowX0(v, row) + Measure(v, v.Display.Substring(s, d - s));
        }

        private static float RowTop(LineView v, int row) => v.ExtraTop + v.BlockHeight + row * v.RowH;

        /// <summary>Visual position of a text position: x within the lines container, top y and row height.</summary>
        private void GetPoint(TextPos p, out float x, out float y, out float h)
        {
            var v = m_Views[p.Line];
            int d = RawToDisp(v, p.Col);
            int row = RowOfDisp(v, d);
            x = XInRow(v, row, d);
            y = Top(p.Line) + (v.TextRows ? RowTop(v, row) : v.ExtraTop);
            h = v.TextRows ? v.RowH : Math.Max(v.BlockHeight, 8f);
        }

        private int DispColAtX(LineView v, int row, float x)
        {
            if (v.Rows.Count == 0) return 0;
            var (s, e) = v.Rows[row];
            string text = v.Display.Substring(s, e - s);
            float lx = x - RowX0(v, row);
            int maxCol = row < v.Rows.Count - 1 ? Math.Max(0, text.Length - 1) : text.Length;
            if (lx <= 0f || text.Length == 0) return s;

            if (lx >= Measure(v, text)) return s + maxCol;

            int lo = 0, hi = text.Length;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (Measure(v, text.Substring(0, mid)) >= lx) hi = mid; else lo = mid + 1;
            }

            if (lo > 0)
            {
                float wPrev = Measure(v, text.Substring(0, lo - 1));
                float wCur = Measure(v, text.Substring(0, lo));
                if (lx - wPrev < wCur - lx) lo--;
            }
            return s + Math.Min(lo, maxCol);
        }

        private TextPos PosFromLocal(Vector2 local, out int rowOut)
        {
            int line = FindLine(local.y);
            EnsureLayout(line);
            var v = m_Views[line];
            float ly = local.y - Top(line) - v.ExtraTop - v.BlockHeight;
            int row = v.Rows.Count == 0 ? 0 : Mathf.Clamp(Mathf.FloorToInt(ly / v.RowH), 0, v.Rows.Count - 1);
            rowOut = row;
            int d = DispColAtX(v, row, local.x);
            return new TextPos(line, Mathf.Clamp(DispToRaw(v, d), 0, v.Raw.Length));
        }

        private TextPos PosFromWorld(Vector2 worldPos) => PosFromLocal(m_LinesContainer.WorldToLocal(worldPos), out _);

        // =====================================================================
        // Visuals: caret + selection + search highlights
        // =====================================================================

        private void RefreshVisuals(bool ensureVisible = true)
        {
            if (m_InRefresh) return;
            if (m_Buffer.LineCount == 0 || m_Views.Count != m_Buffer.LineCount) return;

            m_InRefresh = true;
            try
            {
                m_Caret = m_Buffer.Clamp(m_Caret);
                m_Anchor = m_Buffer.Clamp(m_Anchor);

                if (m_CodeDirty || m_StructureDirty)
                {
                    if (m_CodeDirty)
                    {
                        m_CodeStates = BlockParser.ComputeCodeStates(m_Buffer.Lines);
                        m_CodeDirty = false;
                    }
                    UpdateStructure();
                }
                if (m_Views[m_Caret.Line].Hidden) UnfoldToReveal(m_Caret.Line);

                if (ensureVisible)
                {
                    CancelPendingScroll(); // the caret moved on purpose: follow it, not the restore
                    ScrollToCaretEstimate();
                }
                SyncLayout();
                PositionOverlays(out float cy, out float ch);

                // Scrolling brings new lines into the window; their real heights move everything below.
                if (ensureVisible && EnsureCaretVisible(cy, ch))
                {
                    SyncLayout();
                    PositionOverlays(out cy, out ch);
                }
            }
            finally
            {
                m_InRefresh = false;
            }

            NotifyCaretMoved();
        }

        private void PositionOverlays(out float caretY, out float caretH)
        {
            GetPoint(m_Caret, out float cx, out caretY, out caretH);
            m_CaretElement.style.left = cx;
            m_CaretElement.style.top = caretY + 2;
            m_CaretElement.style.height = Math.Max(8f, caretH - 4f);
            UpdateCaretVisibility();

            UpdateSelectionRects();
            UpdateSearchRects();
        }

        private void NotifyCaretMoved()
        {
            if (m_Loading) return;
            if (m_Caret == m_NotifiedCaret && m_Anchor == m_NotifiedAnchor && m_TextVersion == m_NotifiedTextVersion) return;
            m_NotifiedCaret = m_Caret;
            m_NotifiedAnchor = m_Anchor;
            m_NotifiedTextVersion = m_TextVersion;
            OnCaretMoved?.Invoke();
        }

        private void UpdateCaretVisibility()
        {
            bool show = m_CaretBlinkOn && !HasSelection && !m_ReadOnly && IsFocused();
            m_CaretElement.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
        }

        private bool IsFocused()
        {
            var fc = panel?.focusController;
            return fc != null && fc.focusedElement == this;
        }

        private void UpdateSelectionRects()
        {
            int used = 0;
            if (HasSelection)
            {
                GetSelection(out var s, out var e);
                var color = CodeIDESettings.Current.Theme.SelectionHighlight;
                used = AddRangeRects(m_SelectionRects, 0, s, e, color, extendLineEnds: true);
            }

            for (int i = used; i < m_SelectionRects.Count; i++)
                m_SelectionRects[i].style.display = DisplayStyle.None;
        }

        /// <summary>Adds highlight rectangles for [s, e) on the lines currently in the virtual window.</summary>
        private int AddRangeRects(List<VisualElement> pool, int used, TextPos s, TextPos e, Color color, bool extendLineEnds)
        {
            int fromLine = Math.Max(s.Line, m_ShownFirst);
            int toLine = Math.Min(e.Line, m_ShownLast);
            for (int l = fromLine; l <= toLine; l++)
            {
                var v = m_Views[l];
                if (v.Hidden) continue;
                EnsureLayout(l);

                int dStart = l == s.Line ? RawToDisp(v, s.Col) : 0;
                int dEnd = l == e.Line ? RawToDisp(v, e.Col) : v.Display.Length;
                bool continues = extendLineEnds && l < e.Line;

                if (!v.TextRows)
                {
                    // Passive block line (table row, image, code header): highlight it as a whole.
                    var block = GetPooledRect(pool, used++);
                    block.style.display = DisplayStyle.Flex;
                    block.style.backgroundColor = color;
                    block.style.left = 0;
                    block.style.top = Top(l);
                    block.style.width = m_LayoutWidth;
                    block.style.height = v.ContentHeight;
                    continue;
                }

                for (int r = 0; r < Math.Max(1, v.Rows.Count); r++)
                {
                    int rs = v.Rows.Count == 0 ? 0 : v.Rows[r].s;
                    int re = v.Rows.Count == 0 ? 0 : v.Rows[r].e;
                    bool lastRow = r >= v.Rows.Count - 1;

                    int a = Math.Max(dStart, rs);
                    int b = Math.Min(dEnd, re);
                    if (b < a || (b == a && !(continues && lastRow))) continue;

                    float x0 = XInRow(v, r, a);
                    float x1 = XInRow(v, r, b) + (continues && lastRow && b == re ? 6f : 0f);
                    if (x1 <= x0) continue;

                    var rect = GetPooledRect(pool, used++);
                    rect.style.display = DisplayStyle.Flex;
                    rect.style.backgroundColor = color;
                    rect.style.left = x0;
                    rect.style.top = Top(l) + RowTop(v, r);
                    rect.style.width = x1 - x0;
                    rect.style.height = v.RowH;
                }
            }
            return used;
        }

        private VisualElement GetPooledRect(List<VisualElement> pool, int index)
        {
            while (pool.Count <= index)
            {
                var r = new VisualElement { pickingMode = PickingMode.Ignore, style = { position = Position.Absolute } };
                m_Overlay.Insert(0, r); // behind caret
                pool.Add(r);
            }
            return pool[index];
        }

        /// <summary>Scrolls roughly to the caret line before layout so far-away targets get measured.</summary>
        private void ScrollToCaretEstimate()
        {
            if (m_Scroll.contentViewport == null || m_Tops.Length <= m_Caret.Line) return;
            float viewH = m_Scroll.contentViewport.layout.height;
            if (float.IsNaN(viewH) || viewH <= 0f) return;

            float offset = m_LinesContainer.panel != null ? m_LinesContainer.ChangeCoordinatesTo(m_Scroll.contentContainer, Vector2.zero).y : 0f;
            float top = offset + m_Tops[m_Caret.Line];
            var scroll = m_Scroll.scrollOffset;
            if (top < scroll.y - VirtualMarginPx || top > scroll.y + viewH + VirtualMarginPx)
            {
                m_Scroll.scrollOffset = new Vector2(scroll.x, Mathf.Max(0f, top - viewH * 0.3f));
            }
        }

        /// <returns>True if the scroll position changed.</returns>
        private bool EnsureCaretVisible(float caretY, float caretH)
        {
            if (m_Scroll.contentViewport == null) return false;
            float viewH = m_Scroll.contentViewport.layout.height;
            if (float.IsNaN(viewH) || viewH <= 0f) return false;

            float top = m_LinesContainer.ChangeCoordinatesTo(m_Scroll.contentContainer, new Vector2(0, caretY)).y;
            float bottom = top + caretH;
            var offset = m_Scroll.scrollOffset;
            const float margin = 24f;

            if (top - margin < offset.y) offset.y = Mathf.Max(0, top - margin);
            else if (bottom + margin > offset.y + viewH) offset.y = bottom + margin - viewH;
            else return false;

            m_Scroll.scrollOffset = offset;
            return true;
        }

        // =====================================================================
        // Selection helpers
        // =====================================================================

        private void GetSelection(out TextPos start, out TextPos end)
        {
            start = m_Anchor; end = m_Caret;
            NoteTextBuffer.Order(ref start, ref end);
        }

        private void MoveCaret(TextPos pos, bool extend, bool keepDesiredX = false)
        {
            m_Caret = m_Buffer.Clamp(pos);
            if (!extend) m_Anchor = m_Caret;
            if (!keepDesiredX) m_DesiredX = -1f;
            m_CaretBlinkOn = true;
            m_Buffer.SealHistory();
            RefreshVisuals();
        }

        // =====================================================================
        // Editing primitives
        // =====================================================================

        private void ReplaceSelection(string text, bool coalesce = false)
        {
            GetSelection(out var s, out var e);
            bool hadSelection = s != e;
            m_Caret = m_Anchor = m_Buffer.Replace(s, e, text, m_Caret, coalesce && !hadSelection);
            AfterEdit();
        }

        private void AfterEdit()
        {
            m_DesiredX = -1f;
            m_CaretBlinkOn = true;
            RefreshVisuals();
            if (!m_Loading) OnTextChanged?.Invoke();
        }

        private void DoDelete(bool word)
        {
            if (HasSelection) { ReplaceSelection(""); return; }

            string line = m_Buffer.GetLine(m_Caret.Line);
            TextPos to;
            if (m_Caret.Col < line.Length)
                to = new TextPos(m_Caret.Line, word ? WordRight(m_Caret) : m_Caret.Col + 1);
            else if (m_Caret.Line < m_Buffer.LineCount - 1)
                to = new TextPos(m_Caret.Line + 1, 0);
            else return;

            m_Caret = m_Anchor = m_Buffer.Replace(m_Caret, to, "", m_Caret);
            AfterEdit();
        }

        private void DoCopy()
        {
            if (!HasSelection) return;
            EditorGUIUtility.systemCopyBuffer = GetSelectedText();
        }

        private void DoCut()
        {
            if (!HasSelection) return;
            DoCopy();
            ReplaceSelection("");
            m_Buffer.SealHistory();
        }

        private void DoPaste()
        {
            string clip = EditorGUIUtility.systemCopyBuffer;
            if (string.IsNullOrEmpty(clip)) return;
            clip = MarkdownScan.NormalizeNewlines(clip);

            // Pasting a URL over selected text makes a markdown link.
            string selected = GetSelectedText();
            if (selected.Length > 0 && selected.IndexOf('\n') < 0 && IsUrl(clip))
            {
                WrapSelection("[", "](" + clip.Trim() + ")");
                return;
            }

            ReplaceSelection(clip);
            m_Buffer.SealHistory();
        }

        private static bool IsUrl(string s)
        {
            s = s.Trim();
            return s.IndexOf(' ') < 0 && s.IndexOf('\n') < 0 &&
                   (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
        }

        private void DoUndo()
        {
            if (!m_Buffer.Undo(out var caret)) return;
            m_Caret = m_Anchor = caret;
            AfterEdit();
        }

        private void DoRedo()
        {
            if (!m_Buffer.Redo(out var caret)) return;
            m_Caret = m_Anchor = caret;
            AfterEdit();
        }

        /// <summary>Cycles the task state of a line by editing the single state character.</summary>
        private void CycleTask(int line)
        {
            var v = m_Views[line];
            if (v.Info.Kind != BlockKind.Task || v.Info.TaskMarkIndex < 0) return;

            char next = BlockParser.NextTaskMark(v.Info.TaskMark);
            var a = new TextPos(line, v.Info.TaskMarkIndex);
            var b = new TextPos(line, v.Info.TaskMarkIndex + 1);
            m_Buffer.Replace(a, b, next.ToString(), m_Caret);
            m_Buffer.SealHistory();
            AfterEdit();
        }

        // =====================================================================
        // Code blocks
        // =====================================================================

        /// <summary>Text between the fences of the block whose opening fence is <paramref name="fenceLine"/>.</summary>
        public string GetCodeBlockText(int fenceLine)
        {
            var sb = new StringBuilder();
            for (int i = fenceLine + 1; i < m_Buffer.LineCount; i++)
            {
                if (i < m_CodeStates.Length && m_CodeStates[i] == BlockParser.StateFence) break;
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(m_Buffer.GetLine(i));
            }
            return sb.ToString();
        }

        private void CopyCodeBlock(LineView v)
        {
            int line = m_Views.IndexOf(v);
            if (line >= 0) EditorGUIUtility.systemCopyBuffer = GetCodeBlockText(line);
        }

        private void RunCodeBlock(LineView v)
        {
            int line = m_Views.IndexOf(v);
            if (line >= 0) OnRunCode?.Invoke(GetCodeBlockText(line), v.CodeLang);
        }

        // =====================================================================
        // Word boundaries
        // =====================================================================

        private static int CharClass(char c) => char.IsWhiteSpace(c) ? 0 : (char.IsLetterOrDigit(c) || c == '_') ? 1 : 2;

        private int WordLeft(TextPos p)
        {
            string t = m_Buffer.GetLine(p.Line);
            int i = Math.Min(p.Col, t.Length);
            while (i > 0 && CharClass(t[i - 1]) == 0) i--;
            if (i == 0) return 0;
            int cls = CharClass(t[i - 1]);
            while (i > 0 && CharClass(t[i - 1]) == cls) i--;
            return i;
        }

        private int WordRight(TextPos p)
        {
            string t = m_Buffer.GetLine(p.Line);
            int i = Math.Min(p.Col, t.Length);
            if (i >= t.Length) return t.Length;
            int cls = CharClass(t[i]);
            if (cls != 0) while (i < t.Length && CharClass(t[i]) == cls) i++;
            while (i < t.Length && CharClass(t[i]) == 0) i++;
            return i;
        }

        private void SelectWordAt(TextPos p)
        {
            string t = m_Buffer.GetLine(p.Line);
            if (t.Length == 0) { m_Anchor = m_Caret = p; return; }

            int i = Math.Min(p.Col, t.Length - 1);
            int cls = CharClass(t[i]);
            int a = i, b = i + 1;
            while (a > 0 && CharClass(t[a - 1]) == cls) a--;
            while (b < t.Length && CharClass(t[b]) == cls) b++;
            m_Anchor = new TextPos(p.Line, a);
            m_Caret = new TextPos(p.Line, b);
        }

        // =====================================================================
        // Keyboard
        // =====================================================================

        private void OnKeyDown(KeyDownEvent evt)
        {
            if (evt.target != this && IsInteractiveTarget(evt.target)) return; // e.g. typing in the inline title

            if (m_ReadOnly)
            {
                if (HandleReadOnlyKey(evt)) evt.StopPropagation();
                return;
            }

            if (KeyInterceptor != null && KeyInterceptor(evt))
            {
                evt.StopPropagation();
                return;
            }

            bool shift = evt.shiftKey;
            bool ctrl = evt.actionKey;
            bool alt = evt.altKey;
            bool altGr = evt.ctrlKey && evt.altKey; // Windows AltGr (e.g. Turkish @ € etc.)

            if (evt.keyCode != KeyCode.None && HandleCommandShortcut(evt))
            {
                evt.StopPropagation();
                return;
            }

            bool handled = true;

            switch (evt.keyCode)
            {
                case KeyCode.LeftArrow: MoveHorizontal(-1, ctrl, shift); break;
                case KeyCode.RightArrow: MoveHorizontal(1, ctrl, shift); break;
                case KeyCode.UpArrow: MoveVertical(-1, shift); break;
                case KeyCode.DownArrow: MoveVertical(1, shift); break;
                case KeyCode.PageUp: MoveVertical(-12, shift); break;
                case KeyCode.PageDown: MoveVertical(12, shift); break;
                case KeyCode.Home: DoHome(ctrl, shift); break;
                case KeyCode.End: DoEnd(ctrl, shift); break;
                case KeyCode.Backspace: DoBackspace(ctrl); break;
                case KeyCode.Delete: DoDelete(ctrl); break;
                case KeyCode.Return:
                case KeyCode.KeypadEnter:
                    if (shift) DoSoftEnter(); else DoEnter();
                    break;
                case KeyCode.Tab: DoTab(shift); break;
                case KeyCode.Escape:
                    if (HasSelection) MoveCaret(m_Caret, false); else handled = false;
                    break;

                default:
                    handled = HandleCharacterOrShortcut(evt, ctrl, shift, alt, altGr);
                    break;
            }

            if (handled) evt.StopPropagation();
        }

        private bool HandleCharacterOrShortcut(KeyDownEvent evt, bool ctrl, bool shift, bool alt, bool altGr)
        {
            if (ctrl && !altGr)
            {
                switch (evt.keyCode)
                {
                    case KeyCode.A: SelectAll(); return true;
                    case KeyCode.C: DoCopy(); return true;
                    case KeyCode.X: DoCut(); return true;
                    case KeyCode.V: DoPaste(); return true;
                    case KeyCode.Z: if (shift) DoRedo(); else DoUndo(); return true;
                    case KeyCode.Y: DoRedo(); return true;
                }
                return false;
            }

            char ch = evt.character;
            if (ch != '\0' && !char.IsControl(ch))
            {
                ReplaceSelection(ch.ToString(), coalesce: true);
                return true;
            }
            return false;
        }

        /// <summary>Reading mode: keys scroll the page; only copy and select-all touch the selection.</summary>
        private bool HandleReadOnlyKey(KeyDownEvent evt)
        {
            if (evt.actionKey && !evt.altKey)
            {
                switch (evt.keyCode)
                {
                    case KeyCode.A: SelectAll(); return true;
                    case KeyCode.C: DoCopy(); return true;
                    case KeyCode.Home: ScrollBy(float.NegativeInfinity); return true;
                    case KeyCode.End: ScrollBy(float.PositiveInfinity); return true;
                }
                return false;
            }

            float page = m_Scroll.contentViewport.layout.height;
            if (float.IsNaN(page)) page = 400f;
            switch (evt.keyCode)
            {
                case KeyCode.UpArrow: ScrollBy(-Px(BaseRowHeight) * 2f); return true;
                case KeyCode.DownArrow: ScrollBy(Px(BaseRowHeight) * 2f); return true;
                case KeyCode.PageUp: ScrollBy(-page * 0.9f); return true;
                case KeyCode.PageDown:
                case KeyCode.Space: ScrollBy(evt.shiftKey ? -page * 0.9f : page * 0.9f); return true;
                case KeyCode.Home: ScrollBy(float.NegativeInfinity); return true;
                case KeyCode.End: ScrollBy(float.PositiveInfinity); return true;
                case KeyCode.Escape:
                    if (!HasSelection) return false;
                    MoveCaret(m_Caret, false);
                    return true;
            }
            return false;
        }

        private void ScrollBy(float dy)
        {
            float max = Mathf.Max(0f, m_Scroll.contentContainer.layout.height - m_Scroll.contentViewport.layout.height);
            if (float.IsNaN(max)) max = 0f;
            float y = float.IsNegativeInfinity(dy) ? 0f : float.IsPositiveInfinity(dy) ? max : Mathf.Clamp(m_Scroll.scrollOffset.y + dy, 0f, max);
            m_Scroll.scrollOffset = new Vector2(0f, y);
        }

        private void MoveHorizontal(int dir, bool word, bool shift)
        {
            GetSelection(out var s, out var e);
            if (HasSelection && !shift)
            {
                MoveCaret(dir < 0 ? s : e, false);
                return;
            }

            TextPos p = m_Caret;
            if (dir < 0)
            {
                if (p.Col > 0) p = new TextPos(p.Line, word ? WordLeft(p) : p.Col - 1);
                else if (p.Line > 0)
                {
                    int prev = FirstVisibleFrom(p.Line - 1, -1);
                    p = new TextPos(prev, m_Buffer.GetLine(prev).Length);
                }
            }
            else
            {
                int len = m_Buffer.GetLine(p.Line).Length;
                if (p.Col < len) p = new TextPos(p.Line, word ? WordRight(p) : p.Col + 1);
                else if (p.Line < m_Buffer.LineCount - 1) p = new TextPos(FirstVisibleFrom(p.Line + 1, 1), 0);
            }
            MoveCaret(p, shift);
        }

        /// <summary>Moves by visual rows (wrapped rows count individually; folded lines are skipped).</summary>
        private void MoveVertical(int deltaRows, bool shift)
        {
            EnsureLayout(m_Caret.Line);
            var v = m_Views[m_Caret.Line];
            int d = RawToDisp(v, m_Caret.Col);
            int row = RowOfDisp(v, d);
            int line = m_Caret.Line;

            if (m_DesiredX < 0f) m_DesiredX = XInRow(v, row, d);

            int dir = deltaRows < 0 ? -1 : 1;
            for (int step = 0; step < Math.Abs(deltaRows); step++)
            {
                int rowCount = Math.Max(1, m_Views[line].Rows.Count);
                if (dir < 0)
                {
                    if (row > 0) { row--; continue; }
                    int prev = line - 1;
                    while (prev >= 0 && m_Views[prev].Hidden) prev--;
                    if (prev < 0) { MoveCaret(new TextPos(0, 0), shift); return; }
                    line = prev;
                    EnsureLayout(line);
                    row = Math.Max(1, m_Views[line].Rows.Count) - 1;
                }
                else
                {
                    if (row < rowCount - 1) { row++; continue; }
                    int next = line + 1;
                    while (next < m_Views.Count && m_Views[next].Hidden) next++;
                    if (next >= m_Views.Count) { MoveCaret(m_Buffer.End, shift); return; }
                    line = next;
                    EnsureLayout(line);
                    row = 0;
                }
            }

            var tv = m_Views[line];
            int td = DispColAtX(tv, row, m_DesiredX);
            MoveCaret(new TextPos(line, Mathf.Clamp(DispToRaw(tv, td), 0, tv.Raw.Length)), shift, keepDesiredX: true);
        }

        private void DoHome(bool ctrl, bool shift)
        {
            if (ctrl) { MoveCaret(new TextPos(0, 0), shift); return; }

            string t = m_Buffer.GetLine(m_Caret.Line);
            int firstNonWs = 0;
            while (firstNonWs < t.Length && (t[firstNonWs] == ' ' || t[firstNonWs] == '\t')) firstNonWs++;
            int col = m_Caret.Col == firstNonWs ? 0 : firstNonWs;
            MoveCaret(new TextPos(m_Caret.Line, col), shift);
        }

        private void DoEnd(bool ctrl, bool shift)
        {
            if (ctrl) { MoveCaret(m_Buffer.End, shift); return; }
            MoveCaret(new TextPos(m_Caret.Line, m_Buffer.GetLine(m_Caret.Line).Length), shift);
        }

        // =====================================================================
        // Pointer
        // =====================================================================

        private bool IsInteractiveTarget(IEventHandler target)
        {
            for (var cur = target as VisualElement; cur != null && cur != this; cur = cur.parent)
            {
                if (cur is Scroller || cur.ClassListContains(InteractiveClass)) return true;
            }
            return false;
        }

        private void OnPointerDown(PointerDownEvent evt)
        {
            if (evt.button != 0 && evt.button != 1) return;
            if (IsInteractiveTarget(evt.target)) return;

            Focus();
            SetHoverTarget(null, default);

            Vector2 local = m_LinesContainer.WorldToLocal(evt.position);
            var pos = PosFromLocal(local, out int row);
            var lv = m_Views[pos.Line];

            if (evt.button == 1)
            {
                GetSelection(out var selStart, out var selEnd);
                bool inSelection = HasSelection && pos >= selStart && pos <= selEnd;
                if (!inSelection) MoveCaret(pos, false);
                OnContextMenu?.Invoke(LinkAt(lv, pos, local));
                evt.StopPropagation();
                return;
            }

            // Ctrl/Cmd + Click (any click in reading mode): link navigation, Alt opens it to the side
            if (evt.actionKey || evt.ctrlKey || evt.commandKey || m_ReadOnly)
            {
                // reading mode reacts to the link text itself, not to clicks that merely land next to it
                string target = !m_ReadOnly || lv.Info.Kind == BlockKind.Image ? LinkAt(lv, pos, local) : RenderedLinkAt(lv, row, local, out _, out _);
                if (target != null)
                {
                    if (OnLinkActivated != null) OnLinkActivated(target, evt.altKey);
                    else NoteLinkResolver.ResolveAndOpen(target);
                    evt.StopPropagation();
                    return;
                }
            }

            // Task checkbox click (only on passive lines, where the glyph is visible)
            if (!lv.Active && lv.Info.Kind == BlockKind.Task && row == 0)
            {
                float lx = local.x - lv.IndentX;
                if (lx >= -4f && lx <= lv.GlyphW + 2f)
                {
                    CycleTask(pos.Line);
                    evt.StopPropagation();
                    return;
                }
            }

            // multi-click detection
            double now = EditorApplication.timeSinceStartup;
            if (now - m_LastClickTime < 0.4 && (evt.position - (Vector3)m_LastClickPos).sqrMagnitude < 16f)
                m_ClickCount++;
            else
                m_ClickCount = 1;
            m_LastClickTime = now;
            m_LastClickPos = evt.position;

            if (m_ClickCount >= 3)
            {
                m_Anchor = new TextPos(pos.Line, 0);
                m_Caret = pos.Line < m_Buffer.LineCount - 1 ? new TextPos(pos.Line + 1, 0) : new TextPos(pos.Line, m_Buffer.GetLine(pos.Line).Length);
                m_ClickCount = 3;
                RefreshVisuals(false);
            }
            else if (m_ClickCount == 2)
            {
                SelectWordAt(pos);
                RefreshVisuals(false);
            }
            else
            {
                MoveCaret(pos, evt.shiftKey);
            }

            m_Dragging = true;
            this.CapturePointer(evt.pointerId);
            evt.StopPropagation();
        }

        /// <summary>Link target under a position (passive lines use rendered segments, active lines the raw text).</summary>
        private string LinkAt(LineView lv, TextPos pos, Vector2 local)
        {
            if (!lv.Active && lv.Info.Kind == BlockKind.Image) return lv.Info.Target; // image / note embed

            if (!lv.Active)
            {
                int d = RawToDisp(lv, pos.Col);
                var seg = GetSegmentAtDisp(lv, d);
                if (seg == null && d > 0) seg = GetSegmentAtDisp(lv, d - 1);
                return seg != null && seg.Style.HasFlag(InlineStyle.Link) ? seg.LinkTarget : null;
            }

            foreach (var seg in InlineTokenizer.Tokenize(lv.Raw))
            {
                if (seg.Style.HasFlag(InlineStyle.Link) && pos.Col >= seg.RawStart && pos.Col <= seg.RawEnd) return seg.LinkTarget;
            }
            return null;
        }

        private void OnPointerMove(PointerMoveEvent evt)
        {
            if (m_Dragging && this.HasPointerCapture(evt.pointerId))
            {
                if (m_ClickCount > 1) return;
                var pos = PosFromWorld(evt.position);
                if (pos == m_Caret) return;
                m_Caret = pos;
                m_CaretBlinkOn = true;
                RefreshVisuals();
                return;
            }

            UpdateHover(evt.position);
        }

        private void OnPointerUp(PointerUpEvent evt)
        {
            if (!m_Dragging) return;
            m_Dragging = false;
            if (this.HasPointerCapture(evt.pointerId)) this.ReleasePointer(evt.pointerId);
            m_Buffer.SealHistory();
        }

        private void OnWheel(WheelEvent evt)
        {
            if (!evt.actionKey || OnZoomRequested == null || evt.delta.y == 0f) return;
            OnZoomRequested(evt.delta.y < 0f ? 1 : -1);
            evt.StopPropagation(); // the scroll view must not scroll
        }

        // ---- link hover preview

        private void UpdateHover(Vector2 worldPos)
        {
            if (OnLinkHover == null || m_Views.Count == 0) return;

            Vector2 local = m_LinesContainer.WorldToLocal(worldPos);
            if (local.y < 0f || local.y > m_Tops[m_Views.Count]) { SetHoverTarget(null, default); return; }

            var pos = PosFromLocal(local, out int row);
            var v = m_Views[pos.Line];
            Rect rect = default;
            string target = RenderedLinkAt(v, row, local, out float x0, out float x1);
            if (target != null)
            {
                var p = m_LinesContainer.ChangeCoordinatesTo(this, new Vector2(x0, Top(pos.Line) + RowTop(v, row)));
                rect = new Rect(p.x, p.y, x1 - x0, v.RowH);
            }

            SetHoverTarget(target, rect);
        }

        /// <summary>The link whose rendered text is under a point of a passive line (x0..x1 = its span in the row).</summary>
        private string RenderedLinkAt(LineView v, int row, Vector2 local, out float x0, out float x1)
        {
            x0 = x1 = 0f;
            if (v.Active || v.Segments == null || !v.TextRows || row >= v.Rows.Count) return null;

            var seg = GetSegmentAtDisp(v, DispColAtX(v, row, local.x));
            if (seg == null || !seg.Style.HasFlag(InlineStyle.Link)) return null;
            x0 = XInRow(v, row, Math.Max(seg.DisplayStart, v.Rows[row].s));
            x1 = XInRow(v, row, Math.Min(seg.DisplayEnd, v.Rows[row].e));
            return local.x >= x0 - 2f && local.x <= x1 + 2f ? seg.LinkTarget : null;
        }

        private void SetHoverTarget(string target, Rect rect)
        {
            if (target == m_HoverTarget) return;
            bool wasShowing = m_HoverTarget != null;
            m_HoverTarget = target;
            m_HoverTimer?.Pause();
            m_HoverTimer = null;

            if (target == null)
            {
                if (wasShowing) OnLinkHover?.Invoke(null, default);
                return;
            }

            m_HoverTimer = schedule.Execute(() =>
            {
                if (m_HoverTarget == target) OnLinkHover?.Invoke(target, rect);
            }).StartingIn(450);
        }
    }
}
