using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;


namespace FuzzyLogicLabs.FuzzyIDE.Editor.Core
{
    public enum IDEAction
    {
        SemanticRename,
        FindReferences,
        Save,
        AutoComplete,
        QuickSearch,
        DuplicateLine,
        DeleteLine,
        MoveLineUp,
        MoveLineDown,
        FormatDocument,
        QuickFix,
        Recompile,
        ReloadDomain,
        OpenNotes,
        QuickScratchpad,
        // Fuzzy Notes editor (appended: settings store bindings by enum value)
        NoteToggleTask,
        NoteBold,
        NoteItalic,
        NoteInsertLink,
        NoteFind,
        NoteQuickSwitcher,
        NoteCommandPalette,
        NoteDailyNote,
        // Fuzzy Notes workspace
        NoteToggleLeftSidebar,
        NoteToggleRightSidebar,
        NoteSplitRight,
        NoteSplitDown,
        NoteCloseTab,
        NoteNextTab,
        NotePreviousTab,
        NoteFocusNextPane,
        NoteNavigateBack,
        NoteNavigateForward,
        // Fuzzy Notes editor surface
        NoteToggleReadingMode,
        NoteZoomIn,
        NoteZoomOut,
        NoteZoomReset,
        NoteToggleFocusMode,
        // Fuzzy Notes search
        NoteSearch,
        // Fuzzy Notes graph
        NoteOpenGraph,
        // Code editor basics (previously hard-coded in the viewer)
        Undo,
        Redo,
        SelectAll,
        Copy,
        Cut,
        Paste,
        Find,
        Replace,
        GoToDefinition,
        ToggleLineComment,
        ZoomIn,
        ZoomOut,
        ZoomReset,
        InsertLineBelow,
        InsertLineAbove,
        SelectLine,
        FindInFiles,
        GoToSymbol,
        GoToFileSymbol,
        GoToLine,
        CommandPalette,
        NavigateBack,
        NavigateForward,
        CodeCleanup,
        ToggleProblems
    }

    [Serializable]
    public class KeyBinding
    {
        public KeyCode Key;
        public EventModifiers Modifiers;

        public KeyBinding(KeyCode key, EventModifiers modifiers = EventModifiers.None)
        {
            Key = key;
            Modifiers = modifiers;
        }

        public bool Matches(KeyDownEvent evt)
        {
            EventModifiers ignoreMask = EventModifiers.FunctionKey | EventModifiers.Numeric | EventModifiers.CapsLock;
            EventModifiers activeMask = evt.modifiers & ~ignoreMask;
            EventModifiers requiredMask = Modifiers & ~ignoreMask;
            
            // Treat Command and Control interchangeably
            bool ctrlMatch = ((activeMask & (EventModifiers.Control | EventModifiers.Command)) != 0) == ((requiredMask & (EventModifiers.Control | EventModifiers.Command)) != 0);
            
            activeMask &= ~(EventModifiers.Control | EventModifiers.Command);
            requiredMask &= ~(EventModifiers.Control | EventModifiers.Command);

            bool keyMatch = evt.keyCode == Key || (Key == KeyCode.Return && evt.keyCode == KeyCode.KeypadEnter);
            return keyMatch && activeMask == requiredMask && ctrlMatch;
        }
        
        public override string ToString()
        {
            if (Modifiers == EventModifiers.None) return Key.ToString();
            return $"{Modifiers}+{Key}";
        }
    }

    [Serializable]
    public class ActionKeyBinding
    {
        public IDEAction Action;
        public KeyBinding Binding;
    }

    [Serializable]
    public class EditorSettings
    {
        public string FontFamily = "JetBrainsMono";
        public int FontSize = 14;
        public float LineHeightSpacing = 1.6f;
        public int TabWidth = 4;
    }

    public enum ThemePreset
    {
        Custom,
        Dark,
        Light,
        Monokai,
        HighContrast,
        DarkPlus,
        RiderDark,
        OneDark,
        SolarizedDark
    }

    [Serializable]
    public class ThemeSettings
    {
        public ThemePreset ActivePreset = ThemePreset.Dark;

        public Color Background = new Color(0.12f, 0.12f, 0.12f, 1f);
        public Color GutterBackground = new Color(0.15f, 0.15f, 0.15f, 1f);
        public Color ActiveLineBackground = new Color(0.18f, 0.18f, 0.18f, 1f);
        public Color SelectionHighlight = new Color(0.15f, 0.4f, 0.6f, 0.5f);
        public Color CaretColor = new Color(0.8f, 0.8f, 0.8f, 1f);
        public Color DefaultText = new Color(0.85f, 0.85f, 0.85f, 1f);
        
        public Color KeywordColor = new Color(0.337f, 0.611f, 0.839f, 1f);
        public Color MethodColor = new Color(0.862f, 0.862f, 0.666f, 1f);
        public Color VariableColor = new Color(0.607f, 0.847f, 0.96f, 1f);
        public Color StringColor = new Color(0.807f, 0.568f, 0.47f, 1f);
        public Color CommentColor = new Color(0.415f, 0.6f, 0.333f, 1f);
        public Color TypeColor = new Color(0.305f, 0.788f, 0.69f, 1f);
        
        public Color UIBorder = new Color(0.3f, 0.3f, 0.3f, 1f);
        public Color UIPopupBackground = new Color(0.18f, 0.18f, 0.18f, 0.95f);

        public Color FieldColor = new Color(0.78f, 0.6f, 0.93f, 1f);
        public Color PropertyColor = new Color(0.84f, 0.73f, 0.49f, 1f);
        public Color ParameterColor = new Color(0.75f, 0.82f, 0.95f, 1f);
        public Color NumberColor = new Color(0.71f, 0.81f, 0.66f, 1f);
        public Color UnityMessageColor = new Color(0.95f, 0.75f, 0.3f, 1f);
        public Color InlayHintColor = new Color(0.55f, 0.55f, 0.55f, 1f);
        
        public Color GitAdded = new Color(0.16f, 0.65f, 0.27f, 1f);
        public Color GitModified = new Color(0.0f, 0.48f, 0.8f, 1f);
        public Color GitDeleted = new Color(0.84f, 0.23f, 0.29f, 1f);

        // Background, Gutter, ActiveLine, Selection, Caret, Text, Keyword, Method, Variable, String, Comment, Type,
        // UIBorder, UIPopup, Field, Property, Parameter, Number, UnityMessage, InlayHint
        private static readonly Dictionary<ThemePreset, string[]> s_Palettes = new Dictionary<ThemePreset, string[]>
        {
            { ThemePreset.DarkPlus, new[] { "#1E1E1E", "#1E1E1E", "#2A2D2E", "#264F7899", "#AEAFAD", "#D4D4D4", "#569CD6", "#DCDCAA", "#9CDCFE", "#CE9178", "#6A9955", "#4EC9B0", "#3C3C3C", "#252526F7", "#9CDCFE", "#9CDCFE", "#9CDCFE", "#B5CEA8", "#DCDCAA", "#8C8C8C" } },
            { ThemePreset.RiderDark, new[] { "#262626", "#262626", "#2E2E2E", "#2F4A7299", "#E0E0E0", "#BDBDBD", "#6C95EB", "#39CC8F", "#BDBDBD", "#C9A26D", "#85C46C", "#C191FF", "#3A3A3A", "#2B2B2BF7", "#66C3CC", "#66C3CC", "#BDBDBD", "#ED94C0", "#39CC8F", "#787878" } },
            { ThemePreset.OneDark, new[] { "#282C34", "#282C34", "#2C313C", "#3E445199", "#528BFF", "#ABB2BF", "#C678DD", "#61AFEF", "#E06C75", "#98C379", "#5C6370", "#E5C07B", "#3E4451", "#21252BF7", "#E06C75", "#E06C75", "#D19A66", "#D19A66", "#61AFEF", "#636D83" } },
            { ThemePreset.SolarizedDark, new[] { "#002B36", "#073642", "#073642", "#274642AA", "#839496", "#839496", "#859900", "#268BD2", "#839496", "#2AA198", "#586E75", "#B58900", "#0B4A58", "#00212BF7", "#CB4B16", "#6C71C4", "#93A1A1", "#D33682", "#268BD2", "#586E75" } },
        };

        private void ApplyPalette(string[] p)
        {
            Color C(int i) => ThemeImporter.TryParse(p[i], out var c) ? c : Color.magenta;
            Background = C(0); GutterBackground = C(1); ActiveLineBackground = C(2); SelectionHighlight = C(3); CaretColor = C(4); DefaultText = C(5);
            KeywordColor = C(6); MethodColor = C(7); VariableColor = C(8); StringColor = C(9); CommentColor = C(10); TypeColor = C(11);
            UIBorder = C(12); UIPopupBackground = C(13); FieldColor = C(14); PropertyColor = C(15); ParameterColor = C(16); NumberColor = C(17);
            UnityMessageColor = C(18); InlayHintColor = C(19);
        }

        public void ApplyPreset(ThemePreset preset)
        {
            if (preset == ThemePreset.Custom) return;
            if (s_Palettes.TryGetValue(preset, out var palette))
            {
                ActivePreset = preset;
                ApplyPalette(palette);
                return;
            }
            
            ActivePreset = preset;
            if (preset == ThemePreset.Dark)
            {
                Background = new Color(0.12f, 0.12f, 0.12f, 1f);
                GutterBackground = new Color(0.15f, 0.15f, 0.15f, 1f);
                ActiveLineBackground = new Color(0.18f, 0.18f, 0.18f, 1f);
                SelectionHighlight = new Color(0.15f, 0.4f, 0.6f, 0.5f);
                CaretColor = new Color(0.8f, 0.8f, 0.8f, 1f);
                DefaultText = new Color(0.85f, 0.85f, 0.85f, 1f);
                
                KeywordColor = new Color(0.337f, 0.611f, 0.839f, 1f);
                MethodColor = new Color(0.862f, 0.862f, 0.666f, 1f);
                VariableColor = new Color(0.607f, 0.847f, 0.96f, 1f);
                StringColor = new Color(0.807f, 0.568f, 0.47f, 1f);
                CommentColor = new Color(0.415f, 0.6f, 0.333f, 1f);
                TypeColor = new Color(0.305f, 0.788f, 0.69f, 1f);
                
                UIBorder = new Color(0.3f, 0.3f, 0.3f, 1f);
                UIPopupBackground = new Color(0.18f, 0.18f, 0.18f, 0.95f);

                FieldColor = new Color(0.78f, 0.6f, 0.93f, 1f);
                PropertyColor = new Color(0.84f, 0.73f, 0.49f, 1f);
                ParameterColor = new Color(0.75f, 0.82f, 0.95f, 1f);
                NumberColor = new Color(0.71f, 0.81f, 0.66f, 1f);
                UnityMessageColor = new Color(0.95f, 0.75f, 0.3f, 1f);
                InlayHintColor = new Color(0.55f, 0.55f, 0.55f, 1f);
            }
            else if (preset == ThemePreset.Light)
            {
                Background = new Color(0.95f, 0.95f, 0.95f, 1f);
                GutterBackground = new Color(0.9f, 0.9f, 0.9f, 1f);
                ActiveLineBackground = new Color(0.85f, 0.85f, 0.85f, 1f);
                SelectionHighlight = new Color(0.6f, 0.8f, 1.0f, 0.5f);
                CaretColor = new Color(0.1f, 0.1f, 0.1f, 1f);
                DefaultText = new Color(0.2f, 0.2f, 0.2f, 1f);
                
                KeywordColor = new Color(0.0f, 0.0f, 1.0f, 1f); 
                MethodColor = new Color(0.44f, 0.33f, 0.11f, 1f); 
                VariableColor = new Color(0.1f, 0.5f, 0.6f, 1f); 
                StringColor = new Color(0.63f, 0.13f, 0.13f, 1f); 
                CommentColor = new Color(0.0f, 0.5f, 0.0f, 1f); 
                TypeColor = new Color(0.16f, 0.55f, 0.55f, 1f); 
                
                UIBorder = new Color(0.7f, 0.7f, 0.7f, 1f);
                UIPopupBackground = new Color(0.9f, 0.9f, 0.9f, 0.95f);

                FieldColor = new Color(0.5f, 0.2f, 0.6f, 1f);
                PropertyColor = new Color(0.55f, 0.4f, 0.1f, 1f);
                ParameterColor = new Color(0.2f, 0.3f, 0.5f, 1f);
                NumberColor = new Color(0.05f, 0.5f, 0.35f, 1f);
                UnityMessageColor = new Color(0.75f, 0.4f, 0.0f, 1f);
                InlayHintColor = new Color(0.5f, 0.5f, 0.5f, 1f);
            }
            else if (preset == ThemePreset.Monokai)
            {
                Background = new Color(0.15f, 0.15f, 0.15f, 1f);
                GutterBackground = new Color(0.18f, 0.18f, 0.18f, 1f);
                ActiveLineBackground = new Color(0.24f, 0.23f, 0.21f, 1f);
                SelectionHighlight = new Color(0.29f, 0.28f, 0.25f, 1f);
                CaretColor = new Color(0.97f, 0.97f, 0.94f, 1f);
                DefaultText = new Color(0.97f, 0.97f, 0.94f, 1f);
                
                KeywordColor = new Color(0.97f, 0.15f, 0.44f, 1f);
                MethodColor = new Color(0.65f, 0.89f, 0.18f, 1f);
                VariableColor = new Color(0.99f, 0.59f, 0.0f, 1f);
                StringColor = new Color(0.9f, 0.86f, 0.45f, 1f);
                CommentColor = new Color(0.46f, 0.44f, 0.37f, 1f);
                TypeColor = new Color(0.4f, 0.85f, 0.93f, 1f);
                
                UIBorder = new Color(0.3f, 0.3f, 0.3f, 1f);
                UIPopupBackground = new Color(0.15f, 0.15f, 0.15f, 0.95f);

                FieldColor = new Color(0.9f, 0.55f, 0.75f, 1f);
                PropertyColor = new Color(0.9f, 0.7f, 0.5f, 1f);
                ParameterColor = new Color(0.99f, 0.59f, 0.0f, 1f);
                NumberColor = new Color(0.68f, 0.51f, 1.0f, 1f);
                UnityMessageColor = new Color(0.65f, 0.89f, 0.18f, 1f);
                InlayHintColor = new Color(0.46f, 0.44f, 0.37f, 1f);
            }
            else if (preset == ThemePreset.HighContrast)
            {
                Background = new Color(0.0f, 0.0f, 0.0f, 1f);
                GutterBackground = new Color(0.05f, 0.05f, 0.05f, 1f);
                ActiveLineBackground = new Color(0.1f, 0.1f, 0.1f, 1f);
                SelectionHighlight = new Color(0.0f, 0.5f, 1.0f, 0.8f);
                CaretColor = new Color(1.0f, 1.0f, 1.0f, 1f);
                DefaultText = new Color(1.0f, 1.0f, 1.0f, 1f);
                
                KeywordColor = new Color(0.3f, 0.8f, 1.0f, 1f);
                MethodColor = new Color(1.0f, 1.0f, 0.2f, 1f);
                VariableColor = new Color(0.7f, 1.0f, 0.7f, 1f);
                StringColor = new Color(1.0f, 0.5f, 0.5f, 1f);
                CommentColor = new Color(0.5f, 1.0f, 0.5f, 1f);
                TypeColor = new Color(0.5f, 0.8f, 1.0f, 1f);
                
                UIBorder = new Color(1.0f, 1.0f, 1.0f, 1f);
                UIPopupBackground = new Color(0.0f, 0.0f, 0.0f, 0.95f);

                FieldColor = new Color(1.0f, 0.6f, 1.0f, 1f);
                PropertyColor = new Color(1.0f, 0.85f, 0.5f, 1f);
                ParameterColor = new Color(0.8f, 0.9f, 1.0f, 1f);
                NumberColor = new Color(0.6f, 1.0f, 0.8f, 1f);
                UnityMessageColor = new Color(1.0f, 0.75f, 0.2f, 1f);
                InlayHintColor = new Color(0.7f, 0.7f, 0.7f, 1f);
            }
        }
    }

    [Serializable]
    public class BehaviorSettings
    {
        public float DoubleTapShiftThreshold = 0.4f;
        public bool AutoCloseBrackets = true;
        public bool DefaultOpenInFuzzyIDE = true;
        /// <summary>Double-clicked .md files open in Fuzzy Notes (Live Preview) instead of the code editor.</summary>
        public bool OpenMarkdownInFuzzyNotes = true;
        
        public enum DragDropBehavior { ContextMenu, AutoAssign }
        public DragDropBehavior SmartDragDrop = DragDropBehavior.ContextMenu;
        
        public bool MinimapVisible = true;
        public float ScrollSpeedMultiplier = 1.0f;

        public bool HighlightMatchingBrackets = true;
        public bool HighlightOccurrences = true;
        public bool ShowIndentGuides = true;
        // Files that already end with a newline keep it regardless; this adds one to files that don't.
        public bool InsertFinalNewline = false;
    }

    [Serializable]
    public class KeybindingSettings
    {
        public List<ActionKeyBinding> Bindings = new List<ActionKeyBinding>();
        
        private Dictionary<IDEAction, KeyBinding> _lookup;

        public static List<ActionKeyBinding> GetDefaultBindingsList()
        {
            return new List<ActionKeyBinding>
            {
                new ActionKeyBinding { Action = IDEAction.SemanticRename, Binding = new KeyBinding(KeyCode.F2) },
                new ActionKeyBinding { Action = IDEAction.FindReferences, Binding = new KeyBinding(KeyCode.F12, EventModifiers.Shift) },
                new ActionKeyBinding { Action = IDEAction.Save, Binding = new KeyBinding(KeyCode.S, EventModifiers.Control) },
                new ActionKeyBinding { Action = IDEAction.AutoComplete, Binding = new KeyBinding(KeyCode.Space, EventModifiers.Control) },
                new ActionKeyBinding { Action = IDEAction.QuickSearch, Binding = new KeyBinding(KeyCode.P, EventModifiers.Control) },
                new ActionKeyBinding { Action = IDEAction.DuplicateLine, Binding = new KeyBinding(KeyCode.D, EventModifiers.Control) },
                new ActionKeyBinding { Action = IDEAction.DeleteLine, Binding = new KeyBinding(KeyCode.L, EventModifiers.Control | EventModifiers.Shift) },
                new ActionKeyBinding { Action = IDEAction.MoveLineUp, Binding = new KeyBinding(KeyCode.UpArrow, EventModifiers.Alt) },
                new ActionKeyBinding { Action = IDEAction.MoveLineDown, Binding = new KeyBinding(KeyCode.DownArrow, EventModifiers.Alt) },
                new ActionKeyBinding { Action = IDEAction.FormatDocument, Binding = new KeyBinding(KeyCode.F, EventModifiers.Control | EventModifiers.Alt) },
                new ActionKeyBinding { Action = IDEAction.QuickFix, Binding = new KeyBinding(KeyCode.Return, EventModifiers.Alt) },
                new ActionKeyBinding { Action = IDEAction.Recompile, Binding = new KeyBinding(KeyCode.R, EventModifiers.Control) },
                new ActionKeyBinding { Action = IDEAction.ReloadDomain, Binding = new KeyBinding(KeyCode.R, EventModifiers.Control | EventModifiers.Shift) },
                new ActionKeyBinding { Action = IDEAction.OpenNotes, Binding = new KeyBinding(KeyCode.M, EventModifiers.Control | EventModifiers.Alt) },
                new ActionKeyBinding { Action = IDEAction.QuickScratchpad, Binding = new KeyBinding(KeyCode.N, EventModifiers.Control | EventModifiers.Alt) },
                new ActionKeyBinding { Action = IDEAction.NoteToggleTask, Binding = new KeyBinding(KeyCode.Return, EventModifiers.Control) },
                new ActionKeyBinding { Action = IDEAction.NoteBold, Binding = new KeyBinding(KeyCode.B, EventModifiers.Control) },
                new ActionKeyBinding { Action = IDEAction.NoteItalic, Binding = new KeyBinding(KeyCode.I, EventModifiers.Control) },
                new ActionKeyBinding { Action = IDEAction.NoteInsertLink, Binding = new KeyBinding(KeyCode.K, EventModifiers.Control) },
                new ActionKeyBinding { Action = IDEAction.NoteFind, Binding = new KeyBinding(KeyCode.F, EventModifiers.Control) },
                new ActionKeyBinding { Action = IDEAction.NoteQuickSwitcher, Binding = new KeyBinding(KeyCode.O, EventModifiers.Control) },
                new ActionKeyBinding { Action = IDEAction.NoteCommandPalette, Binding = new KeyBinding(KeyCode.P, EventModifiers.Control) },
                new ActionKeyBinding { Action = IDEAction.NoteDailyNote, Binding = new KeyBinding(KeyCode.D, EventModifiers.Control | EventModifiers.Alt) },
                new ActionKeyBinding { Action = IDEAction.NoteToggleLeftSidebar, Binding = new KeyBinding(KeyCode.LeftBracket, EventModifiers.Control | EventModifiers.Alt) },
                new ActionKeyBinding { Action = IDEAction.NoteToggleRightSidebar, Binding = new KeyBinding(KeyCode.RightBracket, EventModifiers.Control | EventModifiers.Alt) },
                new ActionKeyBinding { Action = IDEAction.NoteSplitRight, Binding = new KeyBinding(KeyCode.Backslash, EventModifiers.Control) },
                new ActionKeyBinding { Action = IDEAction.NoteSplitDown, Binding = new KeyBinding(KeyCode.Backslash, EventModifiers.Control | EventModifiers.Shift) },
                new ActionKeyBinding { Action = IDEAction.NoteCloseTab, Binding = new KeyBinding(KeyCode.W, EventModifiers.Control) },
                new ActionKeyBinding { Action = IDEAction.NoteNextTab, Binding = new KeyBinding(KeyCode.Tab, EventModifiers.Control) },
                new ActionKeyBinding { Action = IDEAction.NotePreviousTab, Binding = new KeyBinding(KeyCode.Tab, EventModifiers.Control | EventModifiers.Shift) },
                new ActionKeyBinding { Action = IDEAction.NoteFocusNextPane, Binding = new KeyBinding(KeyCode.BackQuote, EventModifiers.Control) },
                new ActionKeyBinding { Action = IDEAction.NoteNavigateBack, Binding = new KeyBinding(KeyCode.LeftArrow, EventModifiers.Alt) },
                new ActionKeyBinding { Action = IDEAction.NoteNavigateForward, Binding = new KeyBinding(KeyCode.RightArrow, EventModifiers.Alt) },
                new ActionKeyBinding { Action = IDEAction.NoteToggleReadingMode, Binding = new KeyBinding(KeyCode.E, EventModifiers.Control) },
                new ActionKeyBinding { Action = IDEAction.NoteZoomIn, Binding = new KeyBinding(KeyCode.Equals, EventModifiers.Control) },
                new ActionKeyBinding { Action = IDEAction.NoteZoomOut, Binding = new KeyBinding(KeyCode.Minus, EventModifiers.Control) },
                new ActionKeyBinding { Action = IDEAction.NoteZoomReset, Binding = new KeyBinding(KeyCode.Alpha0, EventModifiers.Control) },
                new ActionKeyBinding { Action = IDEAction.NoteToggleFocusMode, Binding = new KeyBinding(KeyCode.F11) },
                new ActionKeyBinding { Action = IDEAction.NoteSearch, Binding = new KeyBinding(KeyCode.F, EventModifiers.Control | EventModifiers.Shift) },
                new ActionKeyBinding { Action = IDEAction.NoteOpenGraph, Binding = new KeyBinding(KeyCode.G, EventModifiers.Control) },
                new ActionKeyBinding { Action = IDEAction.Undo, Binding = new KeyBinding(KeyCode.Z, EventModifiers.Control) },
                new ActionKeyBinding { Action = IDEAction.Redo, Binding = new KeyBinding(KeyCode.Y, EventModifiers.Control) },
                new ActionKeyBinding { Action = IDEAction.SelectAll, Binding = new KeyBinding(KeyCode.A, EventModifiers.Control) },
                new ActionKeyBinding { Action = IDEAction.Copy, Binding = new KeyBinding(KeyCode.C, EventModifiers.Control) },
                new ActionKeyBinding { Action = IDEAction.Cut, Binding = new KeyBinding(KeyCode.X, EventModifiers.Control) },
                new ActionKeyBinding { Action = IDEAction.Paste, Binding = new KeyBinding(KeyCode.V, EventModifiers.Control) },
                new ActionKeyBinding { Action = IDEAction.Find, Binding = new KeyBinding(KeyCode.F, EventModifiers.Control) },
                new ActionKeyBinding { Action = IDEAction.Replace, Binding = new KeyBinding(KeyCode.H, EventModifiers.Control) },
                new ActionKeyBinding { Action = IDEAction.GoToDefinition, Binding = new KeyBinding(KeyCode.F12) },
                new ActionKeyBinding { Action = IDEAction.ToggleLineComment, Binding = new KeyBinding(KeyCode.Slash, EventModifiers.Control) },
                new ActionKeyBinding { Action = IDEAction.ZoomIn, Binding = new KeyBinding(KeyCode.Equals, EventModifiers.Control) },
                new ActionKeyBinding { Action = IDEAction.ZoomOut, Binding = new KeyBinding(KeyCode.Minus, EventModifiers.Control) },
                new ActionKeyBinding { Action = IDEAction.ZoomReset, Binding = new KeyBinding(KeyCode.Alpha0, EventModifiers.Control) },
                new ActionKeyBinding { Action = IDEAction.InsertLineBelow, Binding = new KeyBinding(KeyCode.Return, EventModifiers.Control) },
                new ActionKeyBinding { Action = IDEAction.InsertLineAbove, Binding = new KeyBinding(KeyCode.Return, EventModifiers.Control | EventModifiers.Shift) },
                new ActionKeyBinding { Action = IDEAction.SelectLine, Binding = new KeyBinding(KeyCode.L, EventModifiers.Control) },
                new ActionKeyBinding { Action = IDEAction.FindInFiles, Binding = new KeyBinding(KeyCode.F, EventModifiers.Control | EventModifiers.Shift) },
                new ActionKeyBinding { Action = IDEAction.GoToSymbol, Binding = new KeyBinding(KeyCode.T, EventModifiers.Control) },
                new ActionKeyBinding { Action = IDEAction.GoToFileSymbol, Binding = new KeyBinding(KeyCode.O, EventModifiers.Control | EventModifiers.Shift) },
                new ActionKeyBinding { Action = IDEAction.GoToLine, Binding = new KeyBinding(KeyCode.G, EventModifiers.Control) },
                new ActionKeyBinding { Action = IDEAction.CommandPalette, Binding = new KeyBinding(KeyCode.P, EventModifiers.Control | EventModifiers.Shift) },
                new ActionKeyBinding { Action = IDEAction.NavigateBack, Binding = new KeyBinding(KeyCode.LeftArrow, EventModifiers.Alt) },
                new ActionKeyBinding { Action = IDEAction.NavigateForward, Binding = new KeyBinding(KeyCode.RightArrow, EventModifiers.Alt) },
                new ActionKeyBinding { Action = IDEAction.CodeCleanup, Binding = new KeyBinding(KeyCode.F, EventModifiers.Control | EventModifiers.Shift | EventModifiers.Alt) },
                new ActionKeyBinding { Action = IDEAction.ToggleProblems, Binding = new KeyBinding(KeyCode.M, EventModifiers.Control | EventModifiers.Shift) }
};
        }

        public void InitializeLookup()
        {
            if (Bindings == null) Bindings = new List<ActionKeyBinding>();
            
            var existing = new HashSet<IDEAction>(Bindings.Select(b => b.Action));
            foreach (var def in GetDefaultBindingsList())
            {
                if (!existing.Contains(def.Action))
                {
                    Bindings.Add(def);
                }
            }

            _lookup = new Dictionary<IDEAction, KeyBinding>();
            foreach (var b in Bindings)
            {
                _lookup[b.Action] = b.Binding;
            }
        }

        public bool CheckAction(IDEAction action, KeyDownEvent evt)
        {
            if (_lookup == null) InitializeLookup();
            if (_lookup.TryGetValue(action, out var binding))
            {
                return binding.Matches(evt);
            }
            return false;
        }

        public void ResetToDefaults()
        {
            Bindings = GetDefaultBindingsList();
            InitializeLookup();
        }
    }

    public enum AIProvider { OpenAI, Gemini, Claude }

    [Serializable]
    public class AISettings
    {
        public AIProvider ActiveProvider = AIProvider.OpenAI;
        public string OpenAIKey = "";
        public string GeminiKey = "";
        public string ClaudeKey = "";
        public string ModelName = "gpt-4o"; 
        public bool AutoApplyDiffs = false;
    }

    [Serializable]
    public class GitSettings
    {
        public bool EnableGitIntegration = true;
        public bool ShowGutterDiffMarkers = true;
        public bool ShowMinimapDiffMarkers = true;
        public bool ShowFileTreeBadges = true;
        public string CustomGitPath = "";
        public GitPullStrategy PullStrategy = GitPullStrategy.Merge;
        /// <summary>Background `git fetch` interval in minutes; 0 disables it.</summary>
        public int AutoFetchMinutes = 0;
    }

    public enum GitPullStrategy
    {
        Merge,
        Rebase,
        FastForwardOnly
    }

    [Serializable]
    public class CompilationSettings
    {
        public CompilationPolicy Policy = CompilationPolicy.UnityDefault;
        public bool CompileOnSave = false;
        public bool DeferScriptCreation = true;
        public bool FastPlayModeDisableDomainReload = true;
    }

    [Serializable]
    public class SmartCodeSettings
    {
        // FZU010-FZU015: empty / misspelled messages, wrong signatures, tag comparison, per-frame lookups, ?. on Objects.
        public bool UnityAnalyzers = true;
        public bool InlayParameterNames = true;
        public bool InlayVarTypes = true;
        // Fields, parameters, locals, properties, Unity messages and numbers in their own colors.
        public bool SemanticHighlighting = true;
        public bool CamelCaseCompletion = true;
        // Remembers accepted completions and ranks them first next time.
        public bool LearnCompletionRanking = true;
    }

    [Serializable]
    public class UnityIntegrationSettings
    {
        // Field / auto-property values of the open script's live instance (Play Mode, or the selected object).
        public bool ShowLiveValues = true;
        // Console errors, warnings and logs shown at the end of the line that produced them.
        public bool ShowConsoleMessagesInline = true;
        public bool InlineConsoleIncludeLogs = true;
        // Renaming a serialized field adds [FormerlySerializedAs("old")] so Inspector data isn't lost.
        public bool AddFormerlySerializedAsOnRename = true;
        // Tag/layer/scene/input/animator/resources string literals: completion and validation.
        public bool UnityStringIntelligence = true;
        // "Used in N assets" / UnityEvent reference hints on classes and methods.
        public bool ShowAssetUsageHints = true;
    }

    [Serializable]
    public class CodeIDESettingsProfile
    {
        public EditorSettings Editor = new EditorSettings();
        public ThemeSettings Theme = new ThemeSettings();
        public BehaviorSettings Behaviors = new BehaviorSettings();
        public KeybindingSettings Keybindings = new KeybindingSettings();
        public AISettings AI = new AISettings();
        public GitSettings Git = new GitSettings();
        public CompilationSettings Compilation = new CompilationSettings();
        public UnityIntegrationSettings Unity = new UnityIntegrationSettings();
        public SmartCodeSettings SmartCode = new SmartCodeSettings();

        public CodeIDESettingsProfile()
        {
            Keybindings.ResetToDefaults();
        }
    }


    public static class CodeIDESettings
    {
        public static event Action OnSettingsChanged;
        private const string PREFS_KEY = "VirtualIDE_SettingsProfile_v2";

        private static CodeIDESettingsProfile _current;
        public static CodeIDESettingsProfile Current
        {
            get
            {
                if (_current == null) Load();
                return _current;
            }
        }

        public static void Load()
        {
            if (EditorPrefs.HasKey(PREFS_KEY))
            {
                string json = EditorPrefs.GetString(PREFS_KEY);
                try {
                    _current = JsonUtility.FromJson<CodeIDESettingsProfile>(json);
                } catch {
                    _current = new CodeIDESettingsProfile();
                }
            }
            else
            {
                _current = new CodeIDESettingsProfile();
            }

            if (_current == null) _current = new CodeIDESettingsProfile();
            if (_current.Keybindings.Bindings.Count == 0) _current.Keybindings.ResetToDefaults();
            _current.Keybindings.InitializeLookup();
        }

        public static void Save()
        {
            if (_current == null) return;
            string json = JsonUtility.ToJson(_current);
            string oldJson = EditorPrefs.GetString(PREFS_KEY, "");
            if (json == oldJson) return; // Prevent infinite reload loops
            
            EditorPrefs.SetString(PREFS_KEY, json);
            _current.Keybindings.InitializeLookup();
            OnSettingsChanged?.Invoke();
        }

        [SettingsProvider]
        public static SettingsProvider CreateSettingsProvider()
        {
            var provider = new SettingsProvider("Preferences/Fuzzy Logic Labs/Code IDE", SettingsScope.User)
            {
                activateHandler = (searchContext, rootElement) =>
                {
                    var settings = Current;
                    
                    var container = new VisualElement { style = { flexDirection = FlexDirection.Row, flexGrow = 1 } };
                    
                    var sidebar = new ListView();
                    sidebar.style.width = 150;
                    sidebar.style.borderRightWidth = 1;
                    sidebar.style.borderRightColor = new Color(0.2f, 0.2f, 0.2f);
                    
                    var contentArea = new ScrollView { style = { flexGrow = 1, paddingLeft = 15, paddingRight = 15, paddingTop = 15, paddingBottom = 15 } };
                    
                    container.Add(sidebar);
                    container.Add(contentArea);
                    
                    var categories = new List<string> { "Editor", "Theme", "Behaviors", "Smart Code", "Unity Integration", "Compilation", "Keybindings", "AI Assistant" };
                    sidebar.itemsSource = categories;
                    sidebar.makeItem = () => new Label { style = { paddingLeft = 10, paddingTop = 8, paddingBottom = 8, fontSize = 14 } };
                    sidebar.bindItem = (e, i) => (e as Label).text = categories[i];
                    
                    sidebar.selectionType = SelectionType.Single;

                    sidebar.selectionChanged += (selection) => 
                    {
                        contentArea.Clear();
                        var selected = selection.GetEnumerator();
                        if (selected.MoveNext())
                        {
                            string category = (string)selected.Current;
                            var title = new Label(category + " Settings") { style = { fontSize = 18, unityFontStyleAndWeight = FontStyle.Bold, marginBottom = 15 } };
                            contentArea.Add(title);

                            switch (category)
                            {
                                case "Editor": RenderEditorSettings(contentArea, settings); break;
                                case "Theme": RenderThemeSettings(contentArea, settings); break;
                                case "Behaviors": RenderBehaviorSettings(contentArea, settings); break;
                                case "Smart Code": RenderSmartCodeSettings(contentArea, settings); break;
                                case "Unity Integration": RenderUnitySettings(contentArea, settings); break;
                                case "Compilation": RenderCompilationSettings(contentArea, settings); break;
                                case "Keybindings": RenderKeybindingSettings(contentArea, settings); break;
                                case "AI Assistant": RenderAISettings(contentArea, settings); break;
                            }
                        }
                    };
                    
                    sidebar.selectedIndex = 0;
                    rootElement.Add(container);
                }
            };
            return provider;
        }

        private static void BindField<TValue, TField>(VisualElement parent, string label, TValue initialValue, Action<TValue> setter) where TField : BaseField<TValue>, new()
        {
            var field = new TField { label = label, value = initialValue };
            
            if (field is TextInputBaseField<TValue> textInputField)
            {
                textInputField.isDelayed = false;
            }
            
            field.style.marginBottom = 5;
            field.RegisterValueChangedCallback(e => {
                setter(e.newValue);
                Save();
            });
            parent.Add(field);
        }

        private static void RenderEditorSettings(VisualElement content, CodeIDESettingsProfile settings)
        {
            var availableCodingFonts = new List<string>();
            var fonts = Resources.LoadAll<Font>("Fonts");
            foreach (var f in fonts)
            {
                string baseName = f.name;
                if (baseName.EndsWith("-Regular")) baseName = baseName.Substring(0, baseName.Length - 8);
                else if (baseName.EndsWith("-Bold")) baseName = baseName.Substring(0, baseName.Length - 5);
                else if (baseName.EndsWith("-Italic")) baseName = baseName.Substring(0, baseName.Length - 7);
                else if (baseName.EndsWith(" Regular")) baseName = baseName.Substring(0, baseName.Length - 8);
                else if (baseName.EndsWith(" Bold")) baseName = baseName.Substring(0, baseName.Length - 5);
                else if (baseName.EndsWith(" Italic")) baseName = baseName.Substring(0, baseName.Length - 7);

                if (!availableCodingFonts.Contains(baseName))
                    availableCodingFonts.Add(baseName);
            }
            if (availableCodingFonts.Count == 0) availableCodingFonts.Add("Consolas");

            if (!availableCodingFonts.Contains(settings.Editor.FontFamily))
            {
                settings.Editor.FontFamily = availableCodingFonts[0];
            }
            var fontDropdown = new DropdownField("Font Family", availableCodingFonts, settings.Editor.FontFamily);
            fontDropdown.style.marginBottom = 5;
            fontDropdown.RegisterValueChangedCallback(e => {
                settings.Editor.FontFamily = e.newValue;
                Save();
            });
            content.Add(fontDropdown);
            BindField<int, IntegerField>(content, "Font Size", settings.Editor.FontSize, v => settings.Editor.FontSize = v);
            BindField<float, FloatField>(content, "Line Height Spacing", settings.Editor.LineHeightSpacing, v => settings.Editor.LineHeightSpacing = v);
            BindField<int, IntegerField>(content, "Tab Width", settings.Editor.TabWidth, v => settings.Editor.TabWidth = v);
        }

        private static void RenderThemeSettings(VisualElement content, CodeIDESettingsProfile settings)
        {
            var presetContainer = new VisualElement { style = { flexDirection = FlexDirection.Row, marginBottom = 15, alignItems = Align.Center } };
            var presetDropdown = new EnumField("Theme Preset", settings.Theme.ActivePreset);
            presetDropdown.style.flexGrow = 1;
            
            Action renderColors = null;
            var colorsContainer = new VisualElement();

            presetDropdown.RegisterValueChangedCallback(e => {
                var newPreset = (ThemePreset)e.newValue;
                if (newPreset != ThemePreset.Custom)
                {
                    settings.Theme.ApplyPreset(newPreset);
                    Save();
                    renderColors?.Invoke();
                }
                else
                {
                    settings.Theme.ActivePreset = ThemePreset.Custom;
                    Save();
                }
            });
            presetContainer.Add(presetDropdown);

            var importButton = new Button(() =>
            {
                string path = EditorUtility.OpenFilePanel("Import VS Code Theme", "", "json");
                if (string.IsNullOrEmpty(path)) return;
                if (ThemeImporter.ImportVsCode(System.IO.File.ReadAllText(path), settings.Theme, out string error))
                {
                    presetDropdown.SetValueWithoutNotify(ThemePreset.Custom);
                    Save();
                    renderColors?.Invoke();
                }
                else
                {
                    EditorUtility.DisplayDialog("Import VS Code Theme", "Could not read the theme:\n" + error, "OK");
                }
            }) { text = "Import VS Code Theme...", tooltip = "Load a VS Code color theme (.json); colors and token scopes are mapped to the editor." };
            importButton.style.marginLeft = 6;
            presetContainer.Add(importButton);

            var exportButton = new Button(() =>
            {
                string path = EditorUtility.SaveFilePanel("Export Theme", "", "fuzzyide-theme.json", "json");
                if (string.IsNullOrEmpty(path)) return;
                System.IO.File.WriteAllText(path, ThemeImporter.ExportVsCode(settings.Theme, System.IO.Path.GetFileNameWithoutExtension(path)));
            }) { text = "Export...", tooltip = "Save the current colors as a VS Code-compatible theme file." };
            exportButton.style.marginLeft = 4;
            presetContainer.Add(exportButton);
            content.Add(presetContainer);
            content.Add(colorsContainer);

            void BindColorField(string label, Color value, Action<Color> setter)
            {
                var field = new ColorField { label = label, value = value };
                field.style.marginBottom = 5;
                field.RegisterValueChangedCallback(e => {
                    setter(e.newValue);
                    if (settings.Theme.ActivePreset != ThemePreset.Custom)
                    {
                        settings.Theme.ActivePreset = ThemePreset.Custom;
                        presetDropdown.SetValueWithoutNotify(ThemePreset.Custom);
                    }
                    Save();
                });
                colorsContainer.Add(field);
            }

            renderColors = () => {
                colorsContainer.Clear();
                
                BindColorField("Background", settings.Theme.Background, v => settings.Theme.Background = v);
                BindColorField("Gutter Background", settings.Theme.GutterBackground, v => settings.Theme.GutterBackground = v);
                BindColorField("Active Line Background", settings.Theme.ActiveLineBackground, v => settings.Theme.ActiveLineBackground = v);
                BindColorField("Selection Highlight", settings.Theme.SelectionHighlight, v => settings.Theme.SelectionHighlight = v);
                BindColorField("Caret Color", settings.Theme.CaretColor, v => settings.Theme.CaretColor = v);
                BindColorField("Default Text", settings.Theme.DefaultText, v => settings.Theme.DefaultText = v);
                
                colorsContainer.Add(new Label("Syntax Highlighting") { style = { marginTop = 15, marginBottom = 5, unityFontStyleAndWeight = FontStyle.Bold } });
                
                BindColorField("Keyword", settings.Theme.KeywordColor, v => settings.Theme.KeywordColor = v);
                BindColorField("Method", settings.Theme.MethodColor, v => settings.Theme.MethodColor = v);
                BindColorField("Variable", settings.Theme.VariableColor, v => settings.Theme.VariableColor = v);
                BindColorField("String", settings.Theme.StringColor, v => settings.Theme.StringColor = v);
                BindColorField("Comment", settings.Theme.CommentColor, v => settings.Theme.CommentColor = v);
                BindColorField("Type", settings.Theme.TypeColor, v => settings.Theme.TypeColor = v);
                BindColorField("Field", settings.Theme.FieldColor, v => settings.Theme.FieldColor = v);
                BindColorField("Property", settings.Theme.PropertyColor, v => settings.Theme.PropertyColor = v);
                BindColorField("Parameter", settings.Theme.ParameterColor, v => settings.Theme.ParameterColor = v);
                BindColorField("Number", settings.Theme.NumberColor, v => settings.Theme.NumberColor = v);
                BindColorField("Unity Message", settings.Theme.UnityMessageColor, v => settings.Theme.UnityMessageColor = v);
                BindColorField("Inlay Hint", settings.Theme.InlayHintColor, v => settings.Theme.InlayHintColor = v);

                colorsContainer.Add(new Label("UI Elements") { style = { marginTop = 15, marginBottom = 5, unityFontStyleAndWeight = FontStyle.Bold } });
                BindColorField("UI Border", settings.Theme.UIBorder, v => settings.Theme.UIBorder = v);
                BindColorField("UI Popup Background", settings.Theme.UIPopupBackground, v => settings.Theme.UIPopupBackground = v);
            };

            renderColors();
        }


        private static void RenderBehaviorSettings(VisualElement content, CodeIDESettingsProfile settings)
        {
            BindField<float, FloatField>(content, "Double-Tap Shift Threshold (s)", settings.Behaviors.DoubleTapShiftThreshold, v => settings.Behaviors.DoubleTapShiftThreshold = v);
            BindField<bool, Toggle>(content, "Auto-close Brackets", settings.Behaviors.AutoCloseBrackets, v => settings.Behaviors.AutoCloseBrackets = v);
            BindField<bool, Toggle>(content, "Open Scripts in Fuzzy IDE Default", settings.Behaviors.DefaultOpenInFuzzyIDE, v => settings.Behaviors.DefaultOpenInFuzzyIDE = v);
            BindField<bool, Toggle>(content, "Open .md Files in Fuzzy Notes", settings.Behaviors.OpenMarkdownInFuzzyNotes, v => settings.Behaviors.OpenMarkdownInFuzzyNotes = v);
            
            var dragDropField = new EnumField("Smart Drag & Drop", settings.Behaviors.SmartDragDrop);
            dragDropField.style.marginBottom = 5;
            dragDropField.RegisterValueChangedCallback(e => {
                settings.Behaviors.SmartDragDrop = (BehaviorSettings.DragDropBehavior)e.newValue;
                Save();
            });
            content.Add(dragDropField);

            BindField<bool, Toggle>(content, "Minimap Visible", settings.Behaviors.MinimapVisible, v => settings.Behaviors.MinimapVisible = v);
            BindField<float, FloatField>(content, "Scroll Speed Multiplier", settings.Behaviors.ScrollSpeedMultiplier, v => settings.Behaviors.ScrollSpeedMultiplier = v);
            BindField<bool, Toggle>(content, "Highlight Matching Brackets", settings.Behaviors.HighlightMatchingBrackets, v => settings.Behaviors.HighlightMatchingBrackets = v);
            BindField<bool, Toggle>(content, "Highlight Word Occurrences", settings.Behaviors.HighlightOccurrences, v => settings.Behaviors.HighlightOccurrences = v);
            BindField<bool, Toggle>(content, "Show Indent Guides", settings.Behaviors.ShowIndentGuides, v => settings.Behaviors.ShowIndentGuides = v);
            BindField<bool, Toggle>(content, "Insert Final Newline on Save", settings.Behaviors.InsertFinalNewline, v => settings.Behaviors.InsertFinalNewline = v);
        }

        private static void RenderSmartCodeSettings(VisualElement content, CodeIDESettingsProfile settings)
        {
            var smart = settings.SmartCode;
            BindField<bool, Toggle>(content, "Unity Analyzers (FZU010-015)", smart.UnityAnalyzers, v => smart.UnityAnalyzers = v);
            BindField<bool, Toggle>(content, "Inlay Hints: Parameter Names", smart.InlayParameterNames, v => smart.InlayParameterNames = v);
            BindField<bool, Toggle>(content, "Inlay Hints: var Types", smart.InlayVarTypes, v => smart.InlayVarTypes = v);
            BindField<bool, Toggle>(content, "Semantic Highlighting", smart.SemanticHighlighting, v => smart.SemanticHighlighting = v);
            BindField<bool, Toggle>(content, "CamelCase Completion Matching", smart.CamelCaseCompletion, v => smart.CamelCaseCompletion = v);
            BindField<bool, Toggle>(content, "Learn Completion Ranking", smart.LearnCompletionRanking, v => smart.LearnCompletionRanking = v);
        }

        private static void RenderUnitySettings(VisualElement content, CodeIDESettingsProfile settings)
        {
            var unity = settings.Unity;
            BindField<bool, Toggle>(content, "Inline Live Values (Play Mode / Selection)", unity.ShowLiveValues, v => unity.ShowLiveValues = v);
            BindField<bool, Toggle>(content, "Inline Console Messages", unity.ShowConsoleMessagesInline, v => unity.ShowConsoleMessagesInline = v);
            BindField<bool, Toggle>(content, "Include Debug.Log Messages", unity.InlineConsoleIncludeLogs, v => unity.InlineConsoleIncludeLogs = v);
            BindField<bool, Toggle>(content, "Add [FormerlySerializedAs] on Field Rename", unity.AddFormerlySerializedAsOnRename, v => unity.AddFormerlySerializedAsOnRename = v);
            BindField<bool, Toggle>(content, "Tag / Layer / Scene String Completion & Validation", unity.UnityStringIntelligence, v => unity.UnityStringIntelligence = v);
            BindField<bool, Toggle>(content, "Asset & UnityEvent Usage Hints", unity.ShowAssetUsageHints, v => unity.ShowAssetUsageHints = v);
        }

        private static void RenderCompilationSettings(VisualElement content, CodeIDESettingsProfile settings)
        {
            var policyField = new EnumField("Compilation Policy", settings.Compilation.Policy);
            policyField.style.marginBottom = 10;
            policyField.RegisterValueChangedCallback(e => {
                CompilationManager.SetPolicy((CompilationPolicy)e.newValue);
                Save();
            });
            content.Add(policyField);

            var policyDesc = new HelpBox(
                "• Unity Default: Standard Unity auto-refresh on window focus & automatic AppDomain reload.\n" +
                "• Lock Domain Reload: Unity compiles in background without freezing the editor. AppDomain reload is paused until you click 'Reload Domain' (Ctrl+Shift+R).",
                HelpBoxMessageType.Info);
            policyDesc.style.marginBottom = 15;
            content.Add(policyDesc);

            BindField<bool, Toggle>(content, "Compile on Save (Ctrl+S)", settings.Compilation.CompileOnSave, v => {
                settings.Compilation.CompileOnSave = v;
                Save();
            });

            BindField<bool, Toggle>(content, "Defer Script Creation (No Reload on New File)", settings.Compilation.DeferScriptCreation, v => {
                settings.Compilation.DeferScriptCreation = v;
                Save();
            });

            BindField<bool, Toggle>(content, "Fast Play Mode (Disable Domain Reload)", settings.Compilation.FastPlayModeDisableDomainReload, v => {
                settings.Compilation.FastPlayModeDisableDomainReload = v;
                CompilationManager.ApplyFastPlayModeSettings(v);
                Save();
            });
        }

        private static void RenderKeybindingSettings(VisualElement content, CodeIDESettingsProfile settings)
        {
            var container = new VisualElement();
            content.Add(container);

            Action render = null;
            render = () => {
                container.Clear();
                var resetBtn = new Button(() => {
                    settings.Keybindings.ResetToDefaults();
                    Save();
                    render();
                }) { text = "Reset to Defaults", style = { width = 150, marginBottom = 15 } };
                container.Add(resetBtn);

                foreach (var kb in settings.Keybindings.Bindings)
                {
                    var row = new VisualElement { style = { flexDirection = FlexDirection.Row, marginBottom = 5, alignItems = Align.Center } };
                    
                    var actionLabel = new Label(kb.Action.ToString()) { style = { width = 150 } };
                    
                    var modField = new EnumFlagsField(kb.Binding.Modifiers);
                    modField.Init(kb.Binding.Modifiers);
                    modField.style.width = 100;
                    modField.RegisterValueChangedCallback(e => {
                        kb.Binding.Modifiers = (EventModifiers)e.newValue;
                        Save();
                    });
                    
                    var keyField = new EnumField(kb.Binding.Key);
                    keyField.Init(kb.Binding.Key);
                    keyField.style.width = 150;
                    keyField.RegisterValueChangedCallback(e => {
                        kb.Binding.Key = (KeyCode)e.newValue;
                        Save();
                    });

                    row.Add(actionLabel);
                    row.Add(modField);
                    row.Add(new Label(" + ") { style = { width = 20, unityTextAlign = TextAnchor.MiddleCenter } });
                    row.Add(keyField);
                    
                    container.Add(row);
                }
            };
            
            render();
        }

        private static void RenderAISettings(VisualElement content, CodeIDESettingsProfile settings)
        {
            var providerField = new EnumField("Active Provider", settings.AI.ActiveProvider);
            providerField.style.marginBottom = 10;
            
            var keyContainer = new VisualElement();
            
            Action updateKeys = () => {
                keyContainer.Clear();
                switch (settings.AI.ActiveProvider)
                {
                    case AIProvider.OpenAI:
                        BindField<string, TextField>(keyContainer, "OpenAI API Key", settings.AI.OpenAIKey, v => settings.AI.OpenAIKey = v);
                        break;
                    case AIProvider.Gemini:
                        BindField<string, TextField>(keyContainer, "Gemini API Key", settings.AI.GeminiKey, v => settings.AI.GeminiKey = v);
                        break;
                    case AIProvider.Claude:
                        BindField<string, TextField>(keyContainer, "Claude API Key", settings.AI.ClaudeKey, v => settings.AI.ClaudeKey = v);
                        break;
                }
            };
            
            updateKeys();

            providerField.RegisterValueChangedCallback(e => {
                settings.AI.ActiveProvider = (AIProvider)e.newValue;
                Save();
                updateKeys();
            });
            content.Add(providerField);
            content.Add(keyContainer);
            
            var warningLabel = new Label("API Keys are stored locally in EditorPrefs.");
            warningLabel.style.marginTop = 15;
            warningLabel.style.color = new Color(0.9f, 0.6f, 0.2f);
            warningLabel.style.whiteSpace = WhiteSpace.Normal;
            content.Add(warningLabel);
        }
    }
}