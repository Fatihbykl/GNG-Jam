using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.TextCore.Text;
using UnityEngine.UIElements;
using FuzzyLogicLabs.FuzzyIDE.Editor.Notes.Core;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.Notes.UI
{
    /// <summary>A color group of the graph: notes matching the search query get the color.</summary>
    [Serializable]
    public sealed class GraphGroup
    {
        public string Query = "";
        public Color Color;
    }

    /// <summary>Filters, groups, display and forces of one graph (saved per graph kind).</summary>
    [Serializable]
    public sealed class GraphSettings
    {
        public string Query = "";
        public bool ShowTags;
        public bool ShowReferences;
        public bool ShowUnresolved;
        public bool ShowOrphans = true;
        public List<GraphGroup> Groups = new List<GraphGroup>();
        public float NodeSize = 1f;
        public float LinkThickness = 1f;
        public float TextFadeZoom = 1.1f;
        public int LocalDepth = 1;
        public ForceSettings Forces = new ForceSettings();

        public GraphFilter ToFilter() => new GraphFilter
        {
            Query = Query, ShowTags = ShowTags, ShowReferences = ShowReferences, ShowUnresolved = ShowUnresolved, ShowOrphans = ShowOrphans
        };
    }

    /// <summary>
    /// Obsidian-style graph view: notes as nodes, links as edges, laid out by <see cref="ForceLayout"/> and drawn with
    /// Painter2D. Drag the background to pan, wheel to zoom, hover to highlight neighbours, drag nodes, click to open
    /// (Ctrl/Alt+click to the right). The global graph is a workspace tab; the local graph (one note and its
    /// neighbourhood) lives in the right sidebar. The simulation only runs while it is moving.
    /// </summary>
    public class GraphView : VisualElement
    {
        private const float MinZoom = 0.05f;
        private const float MaxZoom = 6f;
        private const int MaxLabels = 400;
        private const int PreSettleSteps = 160;
        private const long PreSettleBudgetMs = 150; // big vaults: settle the rest on screen instead of freezing
        private const float ClickSlop = 4f;

        private readonly bool m_Local;
        private readonly string m_PrefKey;
        private readonly GraphSettings m_Settings;
        private readonly VisualElement m_Canvas;
        private readonly VisualElement m_SettingsPanel;
        private readonly Label m_Empty;
        private IVisualElementScheduledItem m_Ticker;
        private IVisualElementScheduledItem m_PendingRebuild;

        private NoteGraph m_Graph = new NoteGraph();
        private ForceLayout m_Layout;
        private Color[] m_NodeColors = Array.Empty<Color>();
        private float[] m_Radius = Array.Empty<float>();
        private NoteItem m_Center;
        private bool m_Dirty = true;
        private bool m_NeedsFit = true;

        private Vector2 m_Pan;
        private float m_Zoom = 1f;
        private int m_Hover = -1;
        private readonly HashSet<int> m_Highlight = new HashSet<int>();

        private int m_DragNode = -1;
        private bool m_Panning;
        private Vector2 m_PointerDown;
        private Vector2 m_LastPointer;
        private bool m_Moved;

        private FontAsset m_Font;

        /// <summary>A note node was clicked: (note, open mode).</summary>
        public event Action<NoteItem, NoteOpenMode> OnOpenNote;
        /// <summary>A tag node was clicked: show this search.</summary>
        public event Action<string> OnSearchRequested;

        public GraphView(bool local)
        {
            m_Local = local;
            m_PrefKey = local ? "FuzzyNotes.Graph.Local" : "FuzzyNotes.Graph.Global";
            m_Settings = LoadSettings();

            style.flexGrow = 1;
            style.overflow = Overflow.Hidden;
            style.backgroundColor = NotesTheme.BgPrimary;
            focusable = true;

            m_Canvas = new VisualElement { style = { position = Position.Absolute, left = 0, top = 0, right = 0, bottom = 0 } };
            m_Canvas.generateVisualContent += Draw;
            m_Canvas.RegisterCallback<PointerDownEvent>(OnPointerDown);
            m_Canvas.RegisterCallback<PointerMoveEvent>(OnPointerMove);
            m_Canvas.RegisterCallback<PointerUpEvent>(OnPointerUp);
            m_Canvas.RegisterCallback<PointerLeaveEvent>(_ => SetHover(-1));
            m_Canvas.RegisterCallback<WheelEvent>(OnWheel);
            m_Canvas.RegisterCallback<ContextClickEvent>(OnContextClick);
            Add(m_Canvas);

            m_Empty = new Label { pickingMode = PickingMode.Ignore, style = { position = Position.Absolute, left = 0, right = 0, top = Length.Percent(45), unityTextAlign = TextAnchor.MiddleCenter, color = NotesTheme.TextFaint, fontSize = 12, display = DisplayStyle.None } };
            Add(m_Empty);

            var tools = new VisualElement { style = { position = Position.Absolute, top = 6, right = 6, flexDirection = FlexDirection.Row } };
            tools.Add(NotesIcons.IconButton("scan", "Zoom to fit", Fit));
            tools.Add(NotesIcons.IconButton("settings", "Graph settings", ToggleSettings));
            Add(tools);

            m_SettingsPanel = BuildSettingsPanel();
            Add(m_SettingsPanel);

            RegisterCallback<AttachToPanelEvent>(_ =>
            {
                NoteStore.OnNotesChanged += ScheduleRebuild;
                NoteStore.OnNoteSaved += HandleNoteSaved;
                NoteStore.OnActiveNoteChanged += HandleActiveNoteChanged;
            });
            RegisterCallback<DetachFromPanelEvent>(_ =>
            {
                NoteStore.OnNotesChanged -= ScheduleRebuild;
                NoteStore.OnNoteSaved -= HandleNoteSaved;
                NoteStore.OnActiveNoteChanged -= HandleActiveNoteChanged;
                m_Ticker?.Pause();
            });
            // rebuild when shown again after changes happened while hidden
            m_Canvas.RegisterCallback<GeometryChangedEvent>(_ =>
            {
                if (!IsShown) return;
                if (m_Dirty) Rebuild();
                else if (m_NeedsFit && m_Layout != null) Fit();
                m_Canvas.MarkDirtyRepaint();
            });
        }

        private bool IsShown => panel != null && m_Canvas.layout.width > 1f && m_Canvas.layout.height > 1f;

        /// <summary>Local graph: centres the graph on a note (null shows nothing).</summary>
        public void SetCenter(NoteItem note)
        {
            if (!m_Local || note == m_Center) return;
            m_Center = note;
            m_NeedsFit = true;
            ScheduleRebuild();
        }

        /// <summary>The sidebar hides and shows the local graph.</summary>
        public void SetVisible(bool visible)
        {
            if (visible && m_Dirty) ScheduleRebuild();
            if (!visible) m_Ticker?.Pause();
        }

        private void HandleNoteSaved(NoteItem _) => ScheduleRebuild();

        private void HandleActiveNoteChanged(NoteItem _) => m_Canvas.MarkDirtyRepaint();

        private void ScheduleRebuild()
        {
            m_Dirty = true;
            m_PendingRebuild?.Pause();
            m_PendingRebuild = schedule.Execute(() => { if (IsShown) Rebuild(); }).StartingIn(250);
        }

        // =====================================================================
        // Model
        // =====================================================================

        /// <summary>
        /// Notes the graph shows. Files of the Assets vault (READMEs, package docs) only appear when a note links to
        /// them, unless the setting shows all of them; the local graph of an Assets file always includes it.
        /// </summary>
        private IEnumerable<NoteItem> GraphNotes(NoteStore store)
        {
            if (NoteSettings.ShowProjectMarkdownInGraph || store.GetVault(NoteVault.ProjectFilesId) == null) return store.Notes;

            bool IsProjectFile(NoteItem n) => n.VaultId == NoteVault.ProjectFilesId && !n.IsExternal;
            var linked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var n in store.Notes)
                if (!IsProjectFile(n)) linked.UnionWith(store.Index.GetOutgoingLinks(n));

            return store.Notes.Where(n => !IsProjectFile(n) || (m_Local && n == m_Center) || NoteIndex.GetLinkKeys(n).Any(linked.Contains));
        }

        private void Rebuild()
        {
            m_Dirty = false;
            var store = NoteStore.Instance;
            var filter = m_Settings.ToFilter();
            if (m_Local) filter.ShowOrphans = true;

            var graph = NoteGraph.Build(GraphNotes(store), n => store.Index.GetOutgoingLinks(n), n => References(store, n), filter);
            if (m_Local) graph = m_Center != null ? graph.Local(m_Center, Mathf.Clamp(m_Settings.LocalDepth, 1, 4)) : new NoteGraph();

            // keep where known nodes were; new nodes start next to a placed neighbour
            var previous = new Dictionary<string, Vector2>();
            if (m_Layout != null)
                for (int i = 0; i < m_Graph.Nodes.Count && i < m_Layout.Count; i++) previous[m_Graph.Nodes[i].Id] = new Vector2(m_Layout.X[i], m_Layout.Y[i]);

            var layout = new ForceLayout(graph.Nodes.Count, graph.Edges, seed: 7) { Settings = m_Settings.Forces };
            int kept = 0;
            var placed = new bool[graph.Nodes.Count];
            for (int i = 0; i < graph.Nodes.Count; i++)
            {
                if (!previous.TryGetValue(graph.Nodes[i].Id, out var p)) continue;
                layout.SetPosition(i, p.x, p.y);
                placed[i] = true;
                kept++;
            }
            if (kept > 0)
            {
                for (int i = 0; i < graph.Nodes.Count; i++)
                {
                    if (placed[i]) continue;
                    int anchor = graph.Neighbors(i).FirstOrDefault(j => placed[j]);
                    if (graph.Neighbors(i).Any(j => placed[j]))
                        layout.SetPosition(i, layout.X[anchor] + (i % 7 - 3) * 6f, layout.Y[anchor] + (i % 5 - 2) * 6f);
                }
            }

            m_Graph = graph;
            m_Layout = layout;
            m_Hover = -1;
            m_Highlight.Clear();
            m_DragNode = -1;
            UpdateStyleArrays();

            if (kept == 0)
            {
                // start close to the final shape instead of exploding on screen
                var clock = System.Diagnostics.Stopwatch.StartNew();
                for (int s = 0; s < PreSettleSteps && layout.IsHot && clock.ElapsedMilliseconds < PreSettleBudgetMs; s++) layout.Step();
                m_NeedsFit = true;
            }
            else layout.Reheat(kept == graph.Nodes.Count ? 0.05f : 0.3f);

            m_Empty.text = m_Local ? (m_Center == null ? "Open a note to see its local graph" : "") : graph.Nodes.Count == 0 ? "No notes match the filters" : "";
            m_Empty.style.display = string.IsNullOrEmpty(m_Empty.text) ? DisplayStyle.None : DisplayStyle.Flex;

            if (m_NeedsFit) Fit();
            StartTicking();
            m_Canvas.MarkDirtyRepaint();
        }

        private static IEnumerable<(string key, string label, string target)> References(NoteStore store, NoteItem note)
        {
            foreach (var r in store.Index.GetRefLinks(note))
            {
                string label = string.IsNullOrEmpty(r.Alias) ? System.IO.Path.GetFileName(r.Target ?? r.Raw) : r.Alias;
                yield return (r.Key, string.IsNullOrEmpty(label) ? r.Raw : label, r.Raw);
            }
        }

        private void UpdateStyleArrays()
        {
            var groups = m_Settings.Groups
                .Where(g => !string.IsNullOrWhiteSpace(g.Query))
                .Select(g => (query: NoteSearchQuery.Parse(g.Query), color: g.Color))
                .ToList();

            int n = m_Graph.Nodes.Count;
            m_NodeColors = new Color[n];
            m_Radius = new float[n];
            for (int i = 0; i < n; i++)
            {
                var node = m_Graph.Nodes[i];
                Color color;
                switch (node.Kind)
                {
                    case GraphNodeKind.Tag: color = NotesTheme.Success; break;
                    case GraphNodeKind.Reference: color = NotesTheme.Purple; break;
                    case GraphNodeKind.Unresolved: color = NotesTheme.WithAlpha(NotesTheme.TextFaint, 0.6f); break;
                    default:
                        color = NotesTheme.TextMuted;
                        foreach (var (query, groupColor) in groups)
                            if (NoteSearch.Match(node.Note, query) != null) { color = groupColor; break; }
                        break;
                }
                m_NodeColors[i] = color;
                m_Radius[i] = (3.5f + Mathf.Sqrt(node.Degree) * 1.6f) * m_Settings.NodeSize;
            }
        }

        // =====================================================================
        // Simulation
        // =====================================================================

        private void StartTicking()
        {
            if (m_Ticker == null) m_Ticker = schedule.Execute(Tick).Every(16);
            else m_Ticker.Resume();
        }

        private void Tick()
        {
            if (m_Layout == null || !IsShown || (!m_Layout.IsHot && m_DragNode < 0))
            {
                m_Ticker.Pause();
                return;
            }
            m_Layout.Step();
            m_Canvas.MarkDirtyRepaint();
        }

        // =====================================================================
        // View transform
        // =====================================================================

        private Vector2 Center => new Vector2(m_Canvas.layout.width / 2f, m_Canvas.layout.height / 2f);

        private Vector2 ToScreen(int i) => Center + m_Pan + new Vector2(m_Layout.X[i], m_Layout.Y[i]) * m_Zoom;

        private Vector2 ToWorld(Vector2 screen) => (screen - Center - m_Pan) / m_Zoom;

        private void Fit()
        {
            if (m_Layout == null || !IsShown) { m_NeedsFit = true; return; }
            m_NeedsFit = false;
            if (m_Layout.Count == 0) { m_Pan = Vector2.zero; m_Zoom = 1f; return; }

            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            for (int i = 0; i < m_Layout.Count; i++)
            {
                minX = Mathf.Min(minX, m_Layout.X[i]); maxX = Mathf.Max(maxX, m_Layout.X[i]);
                minY = Mathf.Min(minY, m_Layout.Y[i]); maxY = Mathf.Max(maxY, m_Layout.Y[i]);
            }
            float w = Mathf.Max(1f, maxX - minX) + 80f, h = Mathf.Max(1f, maxY - minY) + 80f;
            m_Zoom = Mathf.Clamp(Mathf.Min(m_Canvas.layout.width / w, m_Canvas.layout.height / h), MinZoom, m_Local ? 1.5f : 2f);
            m_Pan = -new Vector2((minX + maxX) / 2f, (minY + maxY) / 2f) * m_Zoom;
            m_Canvas.MarkDirtyRepaint();
        }

        // =====================================================================
        // Drawing
        // =====================================================================

        private void Draw(MeshGenerationContext mgc)
        {
            if (m_Layout == null || m_Layout.Count == 0) return;
            var painter = mgc.painter2D;
            var bounds = new Rect(-40f, -40f, m_Canvas.layout.width + 80f, m_Canvas.layout.height + 80f);
            bool focus = m_Hover >= 0 || m_DragNode >= 0;
            int n = m_Layout.Count;

            var screen = new Vector2[n];
            for (int i = 0; i < n; i++) screen[i] = ToScreen(i);

            // links: dimmed ones first, highlighted ones on top
            painter.lineWidth = Mathf.Max(0.5f, m_Settings.LinkThickness * Mathf.Clamp(m_Zoom, 0.6f, 1.6f));
            var normal = NotesTheme.WithAlpha(NotesTheme.TextFaint, focus ? 0.12f : 0.45f);
            StrokeEdges(painter, screen, bounds, normal, e => !focus || !IsFocusEdge(e));
            if (focus) StrokeEdges(painter, screen, bounds, NotesTheme.WithAlpha(NotesTheme.Accent, 0.85f), IsFocusEdge);

            // nodes, grouped by color into single fills
            float radiusScale = Mathf.Clamp(Mathf.Sqrt(m_Zoom), 0.5f, 2f);
            var buckets = new Dictionary<Color, List<int>>();
            for (int i = 0; i < n; i++)
            {
                if (!bounds.Contains(screen[i])) continue;
                var color = m_NodeColors[i];
                if (focus && !m_Highlight.Contains(i)) color = NotesTheme.WithAlpha(color, color.a * 0.25f);
                else if (focus && (i == m_Hover || i == m_DragNode)) color = NotesTheme.Accent;
                if (!buckets.TryGetValue(color, out var list)) buckets[color] = list = new List<int>();
                list.Add(i);
            }
            foreach (var kv in buckets)
            {
                painter.fillColor = kv.Key;
                painter.BeginPath();
                foreach (int i in kv.Value)
                {
                    float r = m_Radius[i] * radiusScale;
                    painter.MoveTo(screen[i] + new Vector2(r, 0f));
                    painter.Arc(screen[i], r, Angle.Degrees(0f), Angle.Degrees(360f));
                    painter.ClosePath();
                }
                painter.Fill();
            }

            // the open note gets a ring
            int active = m_Graph.IndexOf(NoteStore.Instance.ActiveNote);
            if (active >= 0 && bounds.Contains(screen[active]))
            {
                painter.strokeColor = NotesTheme.Accent;
                painter.lineWidth = 2f;
                painter.BeginPath();
                painter.Arc(screen[active], m_Radius[active] * radiusScale + 3f, Angle.Degrees(0f), Angle.Degrees(360f));
                painter.Stroke();
            }

            DrawLabels(mgc, screen, bounds, radiusScale, focus);
        }

        private bool IsFocusEdge(GraphEdge e)
        {
            int f = m_DragNode >= 0 ? m_DragNode : m_Hover;
            return e.A == f || e.B == f;
        }

        private void StrokeEdges(Painter2D painter, Vector2[] screen, Rect bounds, Color color, Func<GraphEdge, bool> include)
        {
            painter.strokeColor = color;
            painter.BeginPath();
            bool any = false;
            foreach (var e in m_Graph.Edges)
            {
                if (!include(e)) continue;
                Vector2 a = screen[e.A], b = screen[e.B];
                if (!bounds.Contains(a) && !bounds.Contains(b) && !SegmentMayCross(a, b, bounds)) continue;
                painter.MoveTo(a);
                painter.LineTo(b);
                any = true;
            }
            if (any) painter.Stroke();
        }

        private static bool SegmentMayCross(Vector2 a, Vector2 b, Rect r) =>
            !(Mathf.Max(a.x, b.x) < r.xMin || Mathf.Min(a.x, b.x) > r.xMax || Mathf.Max(a.y, b.y) < r.yMin || Mathf.Min(a.y, b.y) > r.yMax);

        private void DrawLabels(MeshGenerationContext mgc, Vector2[] screen, Rect bounds, float radiusScale, bool focus)
        {
            var font = Font();
            float fade = Mathf.InverseLerp(m_Settings.TextFadeZoom * 0.7f, m_Settings.TextFadeZoom, m_Zoom);
            if (m_Local) fade = Mathf.Max(fade, 0.85f);
            float fontSize = Mathf.Clamp(11f * Mathf.Sqrt(m_Zoom), 9f, 16f);
            int drawn = 0;

            for (int i = 0; i < screen.Length && drawn < MaxLabels; i++)
            {
                bool highlighted = focus && m_Highlight.Contains(i);
                float alpha = highlighted ? 1f : focus ? fade * 0.2f : fade;
                if (alpha < 0.05f || !bounds.Contains(screen[i])) continue;

                string label = m_Graph.Nodes[i].Label ?? "";
                if (label.Length > 40) label = label.Substring(0, 39) + "…";
                float width = label.Length * fontSize * 0.52f;
                var pos = new Vector2(screen[i].x - width / 2f, screen[i].y + m_Radius[i] * radiusScale + 3f);
                mgc.DrawText(label, pos, fontSize, NotesTheme.WithAlpha(NotesTheme.TextNormal, alpha), font);
                drawn++;
            }
        }

        private FontAsset Font()
        {
            if (m_Font != null) return m_Font;
            var definition = resolvedStyle.unityFontDefinition;
            m_Font = definition.fontAsset;
            if (m_Font == null && definition.font != null) m_Font = FontAsset.CreateFontAsset(definition.font);
            if (m_Font == null && resolvedStyle.unityFont != null) m_Font = FontAsset.CreateFontAsset(resolvedStyle.unityFont);
            return m_Font;
        }

        // =====================================================================
        // Input
        // =====================================================================

        private int NodeAt(Vector2 screenPos)
        {
            if (m_Layout == null) return -1;
            float radiusScale = Mathf.Clamp(Mathf.Sqrt(m_Zoom), 0.5f, 2f);
            int best = -1;
            float bestD2 = float.MaxValue;
            for (int i = 0; i < m_Layout.Count; i++)
            {
                float r = m_Radius[i] * radiusScale + 4f;
                float d2 = (ToScreen(i) - screenPos).sqrMagnitude;
                if (d2 <= r * r && d2 < bestD2) { best = i; bestD2 = d2; }
            }
            return best;
        }

        private void SetHover(int node)
        {
            if (node == m_Hover) return;
            m_Hover = node;
            UpdateHighlight();
            m_Canvas.MarkDirtyRepaint();
        }

        private void UpdateHighlight()
        {
            m_Highlight.Clear();
            int f = m_DragNode >= 0 ? m_DragNode : m_Hover;
            if (f < 0) return;
            m_Highlight.Add(f);
            foreach (int j in m_Graph.Neighbors(f)) m_Highlight.Add(j);
        }

        private void OnPointerDown(PointerDownEvent e)
        {
            if (e.button != 0 && e.button != 2) return;
            Focus();
            m_PointerDown = m_LastPointer = e.localPosition;
            m_Moved = false;
            int node = e.button == 0 ? NodeAt(e.localPosition) : -1;
            if (node >= 0)
            {
                m_DragNode = node;
                m_Layout.SetFixed(node, true);
                UpdateHighlight();
            }
            else m_Panning = true;
            m_Canvas.CapturePointer(e.pointerId);
            e.StopPropagation();
        }

        private void OnPointerMove(PointerMoveEvent e)
        {
            Vector2 pos = e.localPosition;
            if ((pos - m_PointerDown).sqrMagnitude > ClickSlop * ClickSlop) m_Moved = true;

            if (m_DragNode >= 0 && m_Moved)
            {
                var world = ToWorld(pos);
                m_Layout.SetPosition(m_DragNode, world.x, world.y);
                m_Layout.Reheat(0.3f);
                StartTicking();
                m_Canvas.MarkDirtyRepaint();
            }
            else if (m_Panning)
            {
                m_Pan += pos - m_LastPointer;
                m_Canvas.MarkDirtyRepaint();
            }
            else if (m_DragNode < 0) SetHover(NodeAt(pos));
            m_LastPointer = pos;
        }

        private void OnPointerUp(PointerUpEvent e)
        {
            if (m_Canvas.HasPointerCapture(e.pointerId)) m_Canvas.ReleasePointer(e.pointerId);
            int node = m_DragNode;
            if (node >= 0)
            {
                m_Layout.SetFixed(node, false);
                m_DragNode = -1;
                UpdateHighlight();
                if (!m_Moved) Activate(node, e.actionKey || e.altKey ? NoteOpenMode.SplitRight : NoteOpenMode.Default);
            }
            m_Panning = false;
            m_Canvas.MarkDirtyRepaint();
        }

        private void OnWheel(WheelEvent e)
        {
            Vector2 mouse = m_Canvas.WorldToLocal(e.mousePosition);
            var world = ToWorld(mouse);
            float factor = Mathf.Exp(-e.delta.y * 0.08f);
            m_Zoom = Mathf.Clamp(m_Zoom * factor, MinZoom, MaxZoom);
            m_Pan = mouse - Center - world * m_Zoom;
            m_Canvas.MarkDirtyRepaint();
            e.StopPropagation();
        }

        private void OnContextClick(ContextClickEvent e)
        {
            int node = NodeAt(m_Canvas.WorldToLocal(e.mousePosition));
            var menu = new GenericMenu();
            if (node >= 0 && m_Graph.Nodes[node].Note != null)
            {
                var note = m_Graph.Nodes[node].Note;
                menu.AddItem(new GUIContent("Open"), false, () => OnOpenNote?.Invoke(note, NoteOpenMode.Default));
                menu.AddItem(new GUIContent("Open to the Right"), false, () => OnOpenNote?.Invoke(note, NoteOpenMode.SplitRight));
                menu.AddItem(new GUIContent("Copy Link"), false, () => EditorGUIUtility.systemCopyBuffer = $"[[{note.LinkName}]]");
                menu.AddSeparator("");
            }
            menu.AddItem(new GUIContent("Zoom to Fit"), false, Fit);
            menu.AddItem(new GUIContent("Restart Layout"), false, () =>
            {
                m_Layout = null;
                m_NeedsFit = true;
                Rebuild();
            });
            menu.ShowAsContext();
            e.StopPropagation();
        }

        private void Activate(int index, NoteOpenMode mode)
        {
            var node = m_Graph.Nodes[index];
            switch (node.Kind)
            {
                case GraphNodeKind.Note: OnOpenNote?.Invoke(node.Note, mode); break;
                case GraphNodeKind.Tag: OnSearchRequested?.Invoke("tag:" + node.Label.TrimStart('#')); break;
                case GraphNodeKind.Reference: NoteLinkResolver.ResolveAndOpen(node.Target); break;
                case GraphNodeKind.Unresolved: NoteStore.Instance.CreateNote(node.Label); break; // like Obsidian
            }
        }

        // =====================================================================
        // Settings panel
        // =====================================================================

        private static readonly Color[] GroupColors =
        {
            new Color(0.95f, 0.55f, 0.3f), new Color(0.35f, 0.75f, 0.45f), new Color(0.4f, 0.6f, 0.95f),
            new Color(0.85f, 0.45f, 0.85f), new Color(0.9f, 0.8f, 0.3f), new Color(0.9f, 0.4f, 0.4f), new Color(0.3f, 0.8f, 0.8f)
        };

        private GraphSettings LoadSettings()
        {
            string json = EditorPrefs.GetString(m_PrefKey, "");
            GraphSettings settings = null;
            if (json.Length > 0)
            {
                try { settings = JsonUtility.FromJson<GraphSettings>(json); }
                catch (ArgumentException) { settings = null; }
            }
            settings = settings ?? new GraphSettings { LocalDepth = 1, ShowTags = false };
            if (settings.Forces == null) settings.Forces = new ForceSettings();
            if (settings.Groups == null) settings.Groups = new List<GraphGroup>();
            return settings;
        }

        private void SaveSettings() => EditorPrefs.SetString(m_PrefKey, JsonUtility.ToJson(m_Settings));

        private void ToggleSettings() =>
            m_SettingsPanel.style.display = m_SettingsPanel.style.display == DisplayStyle.Flex ? DisplayStyle.None : DisplayStyle.Flex;

        private VisualElement BuildSettingsPanel()
        {
            var panel = new ScrollView
            {
                style =
                {
                    position = Position.Absolute, top = 38, right = 6, width = 250, maxHeight = Length.Percent(85), display = DisplayStyle.None,
                    backgroundColor = NotesTheme.PopupBackground,
                    borderTopColor = NotesTheme.Border, borderBottomColor = NotesTheme.Border, borderLeftColor = NotesTheme.Border, borderRightColor = NotesTheme.Border
                }
            }.WithClass("fn-popup");

            // Filters
            var filters = Section(panel, "Filters");
            var query = new TextField { value = m_Settings.Query, tooltip = "Only notes matching this search (tag:, path:, …)" }.WithClass("fn-input");
            query.textEdition.placeholder = "Search files…";
            query.RegisterValueChangedCallback(e => { m_Settings.Query = e.newValue; Changed(rebuild: true); });
            filters.Add(query);
            Toggle(filters, "Tags", m_Settings.ShowTags, v => m_Settings.ShowTags = v);
            Toggle(filters, "Code & asset links", m_Settings.ShowReferences, v => m_Settings.ShowReferences = v);
            Toggle(filters, "Unresolved links", m_Settings.ShowUnresolved, v => m_Settings.ShowUnresolved = v);
            if (!m_Local) Toggle(filters, "Orphans", m_Settings.ShowOrphans, v => m_Settings.ShowOrphans = v);
            else Slider(filters, "Depth", 1, 4, m_Settings.LocalDepth, v => m_Settings.LocalDepth = Mathf.RoundToInt(v), rebuild: true, whole: true);

            // Groups
            var groups = Section(panel, "Groups");
            var groupList = new VisualElement();
            groups.Add(groupList);
            void RebuildGroups()
            {
                groupList.Clear();
                foreach (var group in m_Settings.Groups.ToList())
                {
                    var row = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, marginBottom = 3 } };
                    var swatch = new Button { tooltip = "Change color", style = { width = 16, height = 16, marginRight = 6, backgroundColor = group.Color, borderTopLeftRadius = 8, borderTopRightRadius = 8, borderBottomLeftRadius = 8, borderBottomRightRadius = 8, borderTopWidth = 0, borderBottomWidth = 0, borderLeftWidth = 0, borderRightWidth = 0 } };
                    swatch.clicked += () =>
                    {
                        int at = Array.FindIndex(GroupColors, c => c == group.Color);
                        group.Color = GroupColors[(at + 1) % GroupColors.Length];
                        swatch.style.backgroundColor = group.Color;
                        Changed(rebuild: false);
                    };
                    row.Add(swatch);
                    var field = new TextField { value = group.Query, style = { flexGrow = 1 } }.WithClass("fn-input");
                    field.textEdition.placeholder = "tag:x  path:folder…";
                    field.RegisterValueChangedCallback(e => { group.Query = e.newValue; Changed(rebuild: false); });
                    row.Add(field);
                    row.Add(NotesIcons.IconButton("x", "Remove group", () => { m_Settings.Groups.Remove(group); RebuildGroups(); Changed(rebuild: false); }, 12f));
                    groupList.Add(row);
                }
            }
            RebuildGroups();
            groups.Add(new Button(() =>
            {
                m_Settings.Groups.Add(new GraphGroup { Query = "", Color = GroupColors[m_Settings.Groups.Count % GroupColors.Length] });
                RebuildGroups();
            }) { text = "New group" }.WithClass("fn-btn"));

            // Display
            var display = Section(panel, "Display");
            Slider(display, "Node size", 0.4f, 3f, m_Settings.NodeSize, v => m_Settings.NodeSize = v, rebuild: false);
            Slider(display, "Link thickness", 0.3f, 4f, m_Settings.LinkThickness, v => m_Settings.LinkThickness = v, rebuild: false);
            Slider(display, "Text fade threshold", 0.2f, 3f, m_Settings.TextFadeZoom, v => m_Settings.TextFadeZoom = v, rebuild: false);

            // Forces
            var forces = Section(panel, "Forces");
            var f = m_Settings.Forces;
            Slider(forces, "Center force", 0f, 0.3f, f.CenterStrength, v => f.CenterStrength = v, rebuild: false, reheat: true);
            Slider(forces, "Repel force", 50f, 4000f, f.RepelStrength, v => f.RepelStrength = v, rebuild: false, reheat: true);
            Slider(forces, "Link force", 0.05f, 1.5f, f.LinkStrength, v => f.LinkStrength = v, rebuild: false, reheat: true);
            Slider(forces, "Link distance", 20f, 250f, f.LinkDistance, v => f.LinkDistance = v, rebuild: false, reheat: true);
            return panel;
        }

        private static VisualElement Section(VisualElement panel, string title)
        {
            var foldout = new Foldout { text = title, value = true, style = { marginBottom = 4 } };
            foldout.style.color = NotesTheme.TextMuted;
            panel.Add(foldout);
            return foldout;
        }

        private void Toggle(VisualElement parent, string label, bool value, Action<bool> set)
        {
            var toggle = new Toggle(label) { value = value, style = { color = NotesTheme.TextNormal } };
            toggle.RegisterValueChangedCallback(e => { set(e.newValue); Changed(rebuild: true); });
            parent.Add(toggle);
        }

        private void Slider(VisualElement parent, string label, float min, float max, float value, Action<float> set, bool rebuild, bool reheat = false, bool whole = false)
        {
            var slider = new Slider(label, min, max) { value = value, style = { color = NotesTheme.TextNormal } };
            slider.labelElement.style.minWidth = 100;
            slider.RegisterValueChangedCallback(e =>
            {
                float v = whole ? Mathf.Round(e.newValue) : e.newValue;
                if (whole && !Mathf.Approximately(v, e.newValue)) slider.SetValueWithoutNotify(v);
                set(v);
                Changed(rebuild);
                if (reheat && m_Layout != null) { m_Layout.Reheat(0.5f); StartTicking(); }
            });
            parent.Add(slider);
        }

        private void Changed(bool rebuild)
        {
            SaveSettings();
            if (rebuild) ScheduleRebuild();
            else
            {
                UpdateStyleArrays();
                m_Canvas.MarkDirtyRepaint();
            }
        }
    }
}
