using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FuzzyLogicLabs.FuzzyIDE.Editor.Core;
using FuzzyLogicLabs.FuzzyIDE.Editor.Roslyn;
using Microsoft.CodeAnalysis.CSharp;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.UI
{
    /// <summary>
    /// End-of-line hints that only an in-editor IDE can show: live field values of the script's instances, Console
    /// messages at the line that logged them, and scene/prefab/UnityEvent usages. Clicking a hint opens its actions.
    /// </summary>
    public partial class VirtualizedCodeViewer
    {
        private const string InlineHintName = "inlineHint";

        private sealed class InlineHint
        {
            public string Markup;
            public string Tooltip;
            public LiveTypeHint LiveType;
            public IReadOnlyList<string> Assets;
            public string AssetsTitle;
            public ConsoleLineEntry Console;
            public string TestFixture;
            public string TestMethod; // null: the hint is on the fixture
            public TestResultInfo TestResult;

            public bool SameAs(InlineHint other) => other != null && other.Markup == Markup && other.Tooltip == Tooltip;
        }

        private Dictionary<int, InlineHint> m_InlineHints = new Dictionary<int, InlineHint>();
        private List<ScriptMember> m_ScriptMembers = new List<ScriptMember>();
        private int m_ScriptMembersVersion = -1;
        private string m_ScriptMembersPath;

        private static string AssetPathOf(string path) => string.IsNullOrEmpty(path) ? "" : ProjectDiagnosticsService.NormalizeAssetPath(path);

        private Label CreateInlineHintLabel()
        {
            var label = new Label { name = InlineHintName, enableRichText = true };
            label.style.flexShrink = 0;
            label.style.alignSelf = Align.Center;
            label.style.marginLeft = 24;
            label.style.paddingLeft = 4;
            label.style.paddingRight = 4;
            label.style.unityFontStyleAndWeight = FontStyle.Italic;
            label.style.whiteSpace = WhiteSpace.NoWrap;
            label.style.display = DisplayStyle.None;
            label.RegisterCallback<PointerDownEvent>(OnInlineHintPointerDown);
            label.RegisterCallback<MouseEnterEvent>(e => label.style.backgroundColor = new Color(1f, 1f, 1f, 0.06f));
            label.RegisterCallback<MouseLeaveEvent>(e => label.style.backgroundColor = Color.clear);
            return label;
        }

        private void BindInlineHint(VisualElement row, int realIndex)
        {
            var label = row.Q<Label>(InlineHintName);
            if (label == null) return;
            label.userData = realIndex;
            label.style.fontSize = Mathf.Max(8, CodeIDESettings.Current.Editor.FontSize - 2);
            ApplyInlineHint(label, realIndex >= 0 && m_InlineHints.TryGetValue(realIndex, out var hint) ? hint : null);
        }

        private static void ApplyInlineHint(Label label, InlineHint hint)
        {
            if (hint == null)
            {
                if (label.style.display != DisplayStyle.None) label.style.display = DisplayStyle.None;
                return;
            }
            if (label.text != hint.Markup) label.text = hint.Markup;
            label.tooltip = hint.Tooltip ?? "";
            label.style.display = DisplayStyle.Flex;
        }

        private void RefreshVisibleInlineHints()
        {
            m_ListView?.Query<Label>(InlineHintName).ForEach(label =>
            {
                ApplyInlineHint(label, label.userData is int line && line >= 0 && m_InlineHints.TryGetValue(line, out var hint) ? hint : null);
            });
        }

        /// <summary>Recomputes all hints; called periodically by the window. Returns true when anything visible changed.</summary>
        public bool UpdateInlineHints()
        {
            var settings = CodeIDESettings.Current.Unity;
            string assetPath = AssetPathOf(CurrentFilePath);
            var hints = new Dictionary<int, InlineHint>();

            if (m_Lines != null && m_PendingDiff == null && assetPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                EnsureScriptMembers(assetPath);
                if (TestRunnerBridge.IsAvailable) AddTestHints(hints);
                if (settings.ShowAssetUsageHints) AddUsageHints(hints, assetPath);
                if (settings.ShowLiveValues) AddLiveValueHints(hints, LiveValueProvider.Collect(assetPath, m_ScriptMembers));
                if (settings.ShowConsoleMessagesInline) AddConsoleHints(hints, assetPath, settings.InlineConsoleIncludeLogs);
            }

            bool changed = hints.Count != m_InlineHints.Count ||
                           hints.Any(kvp => !m_InlineHints.TryGetValue(kvp.Key, out var old) || !old.SameAs(kvp.Value));
            m_InlineHints = hints;
            if (changed) RefreshVisibleInlineHints();
            return changed;
        }

        // Members come from the buffer (unsaved edits included); re-parse only after the text changed.
        private void EnsureScriptMembers(string assetPath)
        {
            if (m_ScriptMembersVersion == m_AnalysisVersion && m_ScriptMembersPath == assetPath) return;
            m_ScriptMembersVersion = m_AnalysisVersion;
            m_ScriptMembersPath = assetPath;
            m_ScriptMembers = UnityScriptMembers.Collect(CSharpSyntaxTree.ParseText(string.Join("\n", m_Lines)));
        }

        private static InlineHint GetOrAdd(Dictionary<int, InlineHint> hints, int line)
        {
            if (!hints.TryGetValue(line, out var hint)) hints[line] = hint = new InlineHint { Markup = "" };
            return hint;
        }

        private static void Append(InlineHint hint, string colorHex, string text, string tooltip = null)
        {
            string escaped = text.Replace("<", "<\u200B");
            hint.Markup += (hint.Markup.Length > 0 ? "    " : "") + $"<color={colorHex}>{escaped}</color>";
            if (!string.IsNullOrEmpty(tooltip)) hint.Tooltip = string.IsNullOrEmpty(hint.Tooltip) ? tooltip : hint.Tooltip + "\n\n" + tooltip;
        }

        private void AddUsageHints(Dictionary<int, InlineHint> hints, string assetPath)
        {
            string guid = AssetDatabase.AssetPathToGUID(assetPath);
            string mainType = Path.GetFileNameWithoutExtension(assetPath);
            foreach (var member in m_ScriptMembers)
            {
                if (member.Kind == ScriptMemberKind.Type)
                {
                    // Unity attaches a MonoScript by GUID, which maps to the class named like the file.
                    string shortName = member.TypeName.Substring(member.TypeName.LastIndexOfAny(new[] { '.', '+' }) + 1);
                    if (shortName != mainType || member.TypeName.Contains("+")) continue;
                    var users = AssetUsageIndex.GetScriptUsers(guid);
                    if (users.Count == 0) continue;
                    var hint = GetOrAdd(hints, member.Line);
                    Append(hint, "#8C8C8C", "\u29C9 used in " + AssetUsageIndex.Summarize(users), "Used in:\n" + string.Join("\n", users.Take(15)));
                    hint.Assets = users;
                    hint.AssetsTitle = "Used in";
                }
                else if (member.Kind == ScriptMemberKind.Method && AssetUsageIndex.HasEventCallers(member.TypeName))
                {
                    var callers = AssetUsageIndex.GetEventCallers(member.TypeName, member.Name);
                    if (callers.Count == 0) continue;
                    var hint = GetOrAdd(hints, member.Line);
                    Append(hint, "#B5A46A", $"\u26A1 {callers.Count} UnityEvent reference{(callers.Count == 1 ? "" : "s")}", "Called from the Inspector in:\n" + string.Join("\n", callers.Take(15)));
                    hint.Assets = callers;
                    hint.AssetsTitle = "UnityEvent in";
                }
            }
        }

        private void AddTestHints(Dictionary<int, InlineHint> hints)
        {
            var fixtures = new HashSet<string>(m_ScriptMembers.Where(m => m.IsTest).Select(m => m.TypeName));
            if (fixtures.Count == 0) return;
            foreach (var member in m_ScriptMembers)
            {
                bool isFixture = member.Kind == ScriptMemberKind.Type && fixtures.Contains(member.TypeName);
                if (!isFixture && !(member.Kind == ScriptMemberKind.Method && member.IsTest)) continue;

                string method = isFixture ? null : member.Name;
                var result = TestRunnerBridge.GetResult(member.TypeName, method, out int passed, out int total);
                string fullName = member.TypeName + "." + member.Name;
                string text, color, tooltip = null;
                if (isFixture ? TestRunnerBridge.IsRunning(member.TypeName + ".") : TestRunnerBridge.IsRunning(fullName))
                {
                    text = "\u25CC running\u2026"; color = "#9A9A9A";
                }
                else if (result == null)
                {
                    int count = m_ScriptMembers.Count(m => m.IsTest && m.TypeName == member.TypeName);
                    text = isFixture ? $"\u25B6 run all ({count})" : "\u25B6 run test"; color = "#7F8C8D";
                }
                else if (result.Outcome == TestOutcome.Failed)
                {
                    string message = (result.Message ?? "").Trim();
                    int newLine = message.IndexOf('\n');
                    if (newLine >= 0) message = message.Substring(0, newLine).Trim();
                    if (message.Length > 100) message = message.Substring(0, 99) + "\u2026";
                    text = total > 1 ? $"\u2716 {total - passed}/{total} failed" : "\u2716 failed";
                    if (!isFixture && message.Length > 0) text += ": " + message;
                    color = "#F14C4C";
                    tooltip = ((result.Message ?? "") + "\n\n" + (result.StackTrace ?? "")).Trim();
                }
                else if (result.Outcome == TestOutcome.Passed)
                {
                    string ms = (result.Duration * 1000).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " ms";
                    text = total > 1 ? $"\u2714 {passed}/{total} passed \u00B7 {ms}" : $"\u2714 passed \u00B7 {ms}";
                    color = "#73C991";
                }
                else
                {
                    text = "\u2298 skipped"; color = "#9A9A9A";
                    tooltip = result.Message;
                }

                var hint = GetOrAdd(hints, member.Line);
                Append(hint, color, text, tooltip);
                hint.TestFixture = member.TypeName;
                hint.TestMethod = method;
                hint.TestResult = result;
            }
        }

        private static void AddLiveValueHints(Dictionary<int, InlineHint> hints, LiveValueResult live)
        {
            foreach (var kvp in live.Types)
            {
                var type = kvp.Value;
                if (type.Target == null && type.Instances.Length == 0) continue;
                string text;
                if (type.Target == null) text = "\u25C6 no live instance";
                else if (type.FromSelection) text = $"\u25C6 {type.Target.name} (selected)";
                else if (type.Instances.Length > 1) text = $"\u25C6 {type.Target.name} ({Array.IndexOf(type.Instances, type.Target) + 1}/{type.Instances.Length})";
                else text = $"\u25C6 {type.Target.name}";
                var hint = GetOrAdd(hints, kvp.Key);
                Append(hint, "#4EC9B0", text, type.Instances.Length > 1 ? "Click to pick which instance's values are shown" : null);
                hint.LiveType = type;
            }
            foreach (var kvp in live.Values)
            {
                Append(GetOrAdd(hints, kvp.Key), "#6FB3B8", "= " + kvp.Value);
            }
        }

        private void AddConsoleHints(Dictionary<int, InlineHint> hints, string assetPath, bool includeLogs)
        {
            foreach (var entry in ConsoleMessageTracker.GetEntries(assetPath))
            {
                if (entry.Severity == ConsoleSeverity.Log && !includeLogs) continue;
                int line = ConsoleSourceLocator.LocateInBuffer(entry, m_Lines);
                if (line < 0) continue;

                string glyph, color;
                switch (entry.Severity)
                {
                    case ConsoleSeverity.Error: glyph = "\u2716"; color = "#F14C4C"; break;
                    case ConsoleSeverity.Warning: glyph = "\u26A0"; color = "#CCA700"; break;
                    default: glyph = "\u25CF"; color = "#9A9A9A"; break;
                }
                string message = entry.Message.Length > 120 ? entry.Message.Substring(0, 119) + "\u2026" : entry.Message;
                string count = entry.Count > 1 ? $"  \u00D7{entry.Count}" : "";
                var hint = GetOrAdd(hints, line);
                Append(hint, color, $"{glyph} {message}{count}", TrimStack(entry));
                hint.Console = entry;
            }
        }

        private static string TrimStack(ConsoleLineEntry entry)
        {
            var stack = (entry.StackTrace ?? "").Split('\n').Where(l => l.Trim().Length > 0).Take(8);
            return entry.FullMessage.Trim() + "\n\n" + string.Join("\n", stack);
        }

        private void OnInlineHintPointerDown(PointerDownEvent evt)
        {
            if (!(evt.currentTarget is Label label) || !(label.userData is int line) || !m_InlineHints.TryGetValue(line, out var hint)) return;
            evt.StopPropagation();

            var menu = new GenericMenu();
            if (hint.TestFixture != null)
            {
                string fixture = hint.TestFixture, method = hint.TestMethod, path = CurrentFilePath;
                string shortFixture = fixture.Substring(fixture.LastIndexOfAny(new[] { '.', '+' }) + 1);
                if (method != null) menu.AddItem(new GUIContent("Run " + method), false, () => TestRunnerBridge.Run(fixture, method, AssetPathOf(path)));
                menu.AddItem(new GUIContent("Run All Tests in " + shortFixture), false, () => TestRunnerBridge.Run(fixture, null, AssetPathOf(path)));
                if (hint.TestResult != null && hint.TestResult.Outcome == TestOutcome.Failed)
                {
                    var failed = hint.TestResult;
                    menu.AddItem(new GUIContent("Copy Failure"), false, () => EditorGUIUtility.systemCopyBuffer = ((failed.Message ?? "") + "\n" + (failed.StackTrace ?? "")).Trim());
                }
                menu.AddItem(new GUIContent("Open Test Runner"), false, () => EditorApplication.ExecuteMenuItem("Window/General/Test Runner"));
            }
            if (hint.LiveType != null)
            {
                var type = hint.LiveType;
                foreach (var instance in type.Instances.Take(50))
                {
                    var target = instance;
                    string name = MenuSafe(target.name) + (target is Component c && c.gameObject.scene.IsValid() ? "  \u2014 " + MenuSafe(c.gameObject.scene.name) : "");
                    menu.AddItem(new GUIContent("Show Values Of/" + name), ReferenceEquals(target, type.Target), () =>
                    {
                        LiveValueProvider.Pin(type.Type, target);
                        Selection.activeObject = target is Component comp ? comp.gameObject : target;
                        EditorGUIUtility.PingObject(target);
                    });
                }
                if (type.Target != null)
                {
                    var current = type.Target;
                    menu.AddItem(new GUIContent("Select " + MenuSafe(current.name)), false, () =>
                    {
                        Selection.activeObject = current is Component comp ? comp.gameObject : current;
                        EditorGUIUtility.PingObject(current);
                    });
                }
            }
            if (hint.Assets != null)
            {
                if (menu.GetItemCount() > 0) menu.AddSeparator("");
                foreach (var asset in hint.Assets.Take(50))
                {
                    string path = asset;
                    menu.AddItem(new GUIContent(hint.AssetsTitle + "/" + MenuSafe(Path.GetFileName(path)) + "  \u2014 " + MenuSafe(Path.GetDirectoryName(path))), false, () =>
                    {
                        var obj = AssetDatabase.LoadMainAssetAtPath(path);
                        if (obj == null) return;
                        Selection.activeObject = obj;
                        EditorGUIUtility.PingObject(obj);
                    });
                }
            }
            if (hint.Console != null)
            {
                if (menu.GetItemCount() > 0) menu.AddSeparator("");
                var entry = hint.Console;
                menu.AddItem(new GUIContent("Copy Message and Stack Trace"), false, () => EditorGUIUtility.systemCopyBuffer = entry.FullMessage.Trim() + "\n" + entry.StackTrace);
                menu.AddItem(new GUIContent("Open Console"), false, () => EditorApplication.ExecuteMenuItem("Window/General/Console"));
                menu.AddItem(new GUIContent("Clear Inline Console Messages"), false, ConsoleMessageTracker.Clear);
            }
            if (menu.GetItemCount() > 0) menu.ShowAsContext();
        }

        // GenericMenu turns '/' into submenus.
        private static string MenuSafe(string text) => (text ?? "").Replace('/', '\u2215').Replace('\\', '\u2215');
    }
}
