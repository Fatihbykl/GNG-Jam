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
        private void TriggerCodeAnalysis()
        {
            // Any in-flight analysis was computed for older text: stop it and make sure its results are dropped.
            m_AnalysisVersion++;
            m_AnalysisCts?.Cancel();

            if (m_DebounceTask != null)
            {
                m_DebounceTask.Pause();
                m_DebounceTask.StartingIn(100); // 100ms fast debounce
                m_DebounceTask.Resume();
            }
        }

        private static int FindLineIndex(int[] lineOffsets, int charOffset)
        {
            if (lineOffsets == null || lineOffsets.Length <= 1) return 0;
            int idx = Array.BinarySearch(lineOffsets, charOffset);
            if (idx >= 0)
            {
                if (idx >= lineOffsets.Length - 1) return lineOffsets.Length - 2;
                return idx;
            }
            int line = ~idx - 1;
            if (line < 0) return 0;
            if (line >= lineOffsets.Length - 1) return lineOffsets.Length - 2;
            return line;
        }

        private int m_AnalysisVersion;
        private int m_AutoCompleteReplaceStartCol = -1;
        private System.Threading.CancellationTokenSource m_AnalysisCts;

        private async System.Threading.Tasks.Task RunAnalyzerAsync()
        {
            if (LanguageService == null || m_Lines == null) return;

            m_AnalysisCts?.Cancel();
            m_AnalysisCts = new System.Threading.CancellationTokenSource();
            var token = m_AnalysisCts.Token;
            int version = ++m_AnalysisVersion;
            // Results are only valid for the text snapshot they were computed from.
            bool IsStale() => version != m_AnalysisVersion || m_Lines == null;

            string fullCode = string.Join("\n", m_Lines);
            int lineCount = m_Lines.Count;
            int[] lineLengths = new int[lineCount];
            for (int i = 0; i < lineCount; i++) lineLengths[i] = m_Lines[i].Length;

            await LanguageService.ParseCode(fullCode, token);
            if (IsStale()) return;

            int[] lineOffsets = new int[lineCount + 1];
            int offset = 0;
            for (int i = 0; i < lineCount; i++)
            {
                lineOffsets[i] = offset;
                offset += lineLengths[i] + 1;
            }
            lineOffsets[lineCount] = offset;

            // 1. Fetch and render syntax highlights immediately without waiting for diagnostics
            var syntaxSpans = await LanguageService.GetSyntaxHighlightsAsync(token);
            if (IsStale()) return;
            var newHighlights = await System.Threading.Tasks.Task.Run(() =>
            {
                var highlights = new Dictionary<int, List<SyntaxSpan>>();
                if (syntaxSpans != null)
                {
                    foreach (var span in syntaxSpans)
                    {
                        int spanStart = span.StartIndex;
                        int spanEnd = span.StartIndex + span.Length;

                        int startLine = FindLineIndex(lineOffsets, spanStart);
                        int endLine = FindLineIndex(lineOffsets, spanEnd);

                        for (int i = startLine; i <= endLine; i++)
                        {
                            if (!highlights.TryGetValue(i, out var lineSpans))
                            {
                                lineSpans = new List<SyntaxSpan>();
                                highlights[i] = lineSpans;
                            }

                            int localStart = Mathf.Max(0, spanStart - lineOffsets[i]);
                            int localEnd = Mathf.Min(lineLengths[i], spanEnd - lineOffsets[i]);
                            int localLength = localEnd - localStart;

                            if (localLength > 0)
                            {
                                lineSpans.Add(new SyntaxSpan(localStart, localLength, span.ColorHex, span.IsBold, span.IsItalic));
                            }
                        }
                    }

                    // Pre-sort line spans once here in background thread so BindItem never has to sort
                    foreach (var list in highlights.Values)
                    {
                        list.Sort((a, b) => a.StartIndex.CompareTo(b.StartIndex));
                    }
                }
                return highlights;
            });
            if (IsStale()) return;

            m_LineHighlights = newHighlights;
            m_ListView.RefreshItems();

            // 2. Process Foldable Blocks, Diagnostics and Signature Help in parallel/follow-up
            if (LanguageService is CSharpLanguageService csharpService)
            {
                var newBlocks = await csharpService.Roslyn.GetFoldableBlocksAsync();
                if (IsStale()) return;
                var newBlockMap = new Dictionary<int, FoldableBlock>(newBlocks.Count);
                for (int i = 0; i < newBlocks.Count; i++)
                {
                    var nb = newBlocks[i];
                    if (m_FoldableBlockMap.TryGetValue(nb.StartLine, out var oldB))
                    {
                        nb.IsFolded = oldB.IsFolded;
                        newBlocks[i] = nb;
                    }
                    newBlockMap[nb.StartLine] = nb;
                }
                m_FoldableBlocks = newBlocks;
                m_FoldableBlockMap = newBlockMap;
                RecalculateVisibleLines();

                var unityNames = CodeIDESettings.Current.Unity.UnityStringIntelligence ? UnityProjectData.Current : null;
                var diagnostics = await csharpService.Roslyn.GetDiagnosticsAsync(token, unityNames, CodeIDESettings.Current.SmartCode.UnityAnalyzers);
                if (IsStale()) return;
                var newDiagnostics = await System.Threading.Tasks.Task.Run(() =>
                {
                    var diags = new Dictionary<int, List<DiagnosticSpan>>();
                    if (diagnostics != null)
                    {
                        foreach (var diag in diagnostics)
                        {
                            if (diag.Location == Microsoft.CodeAnalysis.Location.None) continue;
                            if (RoslynAnalyzerService.IsFalsePositiveDuplicateTypeDiagnostic(diag)) continue;

                            int spanStart = diag.Location.SourceSpan.Start;
                            int spanEnd = diag.Location.SourceSpan.End;
                            if (spanEnd == spanStart) spanEnd++;
                            string message = diag.GetMessage();
                            bool isError = diag.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error;

                            int startLine = FindLineIndex(lineOffsets, spanStart);
                            int endLine = FindLineIndex(lineOffsets, spanEnd);

                            for (int i = startLine; i <= endLine; i++)
                            {
                                if (!diags.TryGetValue(i, out var lineDiags))
                                {
                                    lineDiags = new List<DiagnosticSpan>();
                                    diags[i] = lineDiags;
                                }

                                int localStart = Mathf.Max(0, spanStart - lineOffsets[i]);
                                int localEnd = Mathf.Min(lineLengths[i], spanEnd - lineOffsets[i]);
                                localStart = Mathf.Clamp(localStart, 0, lineLengths[i]);
                                localEnd = Mathf.Clamp(localEnd, localStart, lineLengths[i]);

                                if (localEnd > localStart)
                                {
                                    lineDiags.Add(new DiagnosticSpan(i, localStart, localEnd, message, isError, diag.Id));
                                }
                            }
                        }
                    }
                    return diags;
                });
                if (IsStale()) return;

                m_LineDiagnostics = newDiagnostics;
                UpdateCaretPosition();
                m_ListView.RefreshItems();

                if (!string.IsNullOrEmpty(CurrentFilePath))
                {
                    var items = new List<FileDiagnosticItem>();
                    if (diagnostics != null)
                    {
                        foreach (var diag in diagnostics)
                        {
                            if (diag.Location == Microsoft.CodeAnalysis.Location.None) continue;
                            if (RoslynAnalyzerService.IsFalsePositiveDuplicateTypeDiagnostic(diag)) continue;
                            bool isError = diag.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error;
                            var lineSpan = diag.Location.GetLineSpan();
                            items.Add(new FileDiagnosticItem(diag.GetMessage(), isError, lineSpan.StartLinePosition.Line + 1, lineSpan.StartLinePosition.Character + 1));
                        }
                    }
                    ProjectDiagnosticsService.SetRoslynDiagnostics(CurrentFilePath, items);
                }

                var smart = CodeIDESettings.Current.SmartCode;
                if (smart.InlayParameterNames || smart.InlayVarTypes)
                {
                    var inlayHints = await csharpService.Roslyn.GetInlayHintsAsync(smart.InlayParameterNames, smart.InlayVarTypes, token);
                    if (IsStale()) return;
                    ApplyInlayHints(inlayHints, lineOffsets);
                    m_ListView.RefreshItems();
                    UpdateCaretPosition(); // hints before the caret move it
                }
                else if (m_LineInlays.Count > 0)
                {
                    m_LineInlays.Clear();
                    m_ListView.RefreshItems();
                    UpdateCaretPosition();
                }

                var signatureHelp = await csharpService.Roslyn.GetSignatureHelpAsync(GetAbsoluteIndex(m_CaretLine, m_CaretColumn));
                if (IsStale()) return;
                ShowSignatureHelp(signatureHelp);
            }
            else
            {
                m_SignatureHelpPopup.style.display = DisplayStyle.None;
            }
        }

        private RoslynAnalyzerService.SignatureHelpInfo m_SignatureInfo;
        private int m_SignatureOverload;
        private string m_SignatureKey;

        private void ShowSignatureHelp(RoslynAnalyzerService.SignatureHelpInfo info)
        {
            if (info == null || info.Overloads.Count == 0)
            {
                m_SignatureInfo = null;
                m_SignatureKey = null;
                m_SignatureHelpPopup.style.display = DisplayStyle.None;
                return;
            }
            // Same call as before (only the active parameter moved): keep the overload the user picked with Up/Down.
            string key = info.Overloads.Count + ":" + string.Join("|", info.Overloads.Select(o => System.Text.RegularExpressions.Regex.Replace(o, "<[^>]*>", "")));
            if (key != m_SignatureKey) m_SignatureOverload = info.Active;
            m_SignatureKey = key;
            m_SignatureInfo = info;
            RenderSignatureHelp();
        }

        private void RenderSignatureHelp()
        {
            var info = m_SignatureInfo;
            if (info == null) return;
            m_SignatureOverload = Mathf.Clamp(m_SignatureOverload, 0, info.Overloads.Count - 1);
            string counter = info.Overloads.Count > 1 ? $"<color=#8C8C8C>\u25B2\u25BC {m_SignatureOverload + 1}/{info.Overloads.Count}</color>  " : "";
            m_SignatureHelpPopup.enableRichText = true;
            m_SignatureHelpPopup.text = counter + info.Overloads[m_SignatureOverload];
            m_SignatureHelpPopup.style.display = DisplayStyle.Flex;
            UpdateSignatureHelpPosition();
        }

        /// <summary>Up/Down while signature help shows several overloads. True when the key was used.</summary>
        private bool TryCycleSignatureOverload(int delta)
        {
            if (m_SignatureInfo == null || m_SignatureInfo.Overloads.Count < 2 || m_SignatureHelpPopup.style.display != DisplayStyle.Flex) return false;
            int count = m_SignatureInfo.Overloads.Count;
            m_SignatureOverload = (m_SignatureOverload + delta + count) % count;
            RenderSignatureHelp();
            return true;
        }

        private async void CleanupCodeAsync()
        {
            if (!(LanguageService is CSharpLanguageService csharpService)) return;
            string fullCode = string.Join("\n", m_Lines);
            string cleaned = await csharpService.Roslyn.CleanupCodeAsync(fullCode);
            if (string.IsNullOrEmpty(cleaned)) return;
            cleaned = cleaned.Replace("\r\n", "\n");
            if (cleaned == fullCode || fullCode != string.Join("\n", m_Lines)) return; // nothing to do, or edited meanwhile

            var cmds = new List<IEditCommand>
            {
                new DeleteSelectionCommand(m_Lines, 0, 0, m_Lines.Count - 1, m_Lines[m_Lines.Count - 1].Length, SetCaretAction),
                new InsertTextCommand(m_Lines, cleaned, 0, 0, SetCaretAction)
            };
            int caretLine = m_CaretLine;
            ExecuteHistory(cmds);
            int line = Mathf.Clamp(caretLine, 0, m_Lines.Count - 1);
            SetCaretPosition(line, Mathf.Min(m_CaretColumn, m_Lines[line].Length), true);
            RefreshListView();
        }

        private void UpdateSignatureHelpPosition()
        {
            if (m_SignatureHelpPopup == null || m_SignatureHelpPopup.style.display != DisplayStyle.Flex)
                return;

            if (m_CaretLine < 0 || m_CaretLine >= m_Lines.Count)
            {
                m_SignatureHelpPopup.style.display = DisplayStyle.None;
                return;
            }

            var scrollView = m_ListView?.Q<ScrollView>();
            if (scrollView == null) return;

            int visIdx = GetVisibleLineIndex(m_CaretLine);
            if (visIdx == -1)
            {
                m_SignatureHelpPopup.style.display = DisplayStyle.None;
                return;
            }

            VisualElement parentContainer = m_SignatureHelpPopup.parent;
            if (parentContainer == null)
            {
                Add(m_SignatureHelpPopup);
                parentContainer = this;
            }

            string curLine = m_Lines[m_CaretLine];

            // Find the start column of the method invocation or opening parenthesis on the current line
            int targetCol = m_CaretColumn;
            int depth = 0;
            int openParenCol = -1;
            int scanEnd = Mathf.Min(m_CaretColumn - 1, curLine.Length - 1);
            for (int c = scanEnd; c >= 0; c--)
            {
                // Ignore parentheses inside string literals
                int quoteCount = 0;
                for (int q = 0; q < c; q++)
                {
                    if (curLine[q] == '"' && (q == 0 || curLine[q - 1] != '\\')) quoteCount++;
                }
                if (quoteCount % 2 != 0) continue;

                if (curLine[c] == ')') depth++;
                else if (curLine[c] == '(')
                {
                    if (depth > 0) depth--;
                    else
                    {
                        openParenCol = c;
                        break;
                    }
                }
            }

            if (openParenCol >= 0)
            {
                int idStart = openParenCol;
                while (idStart > 0 && char.IsWhiteSpace(curLine[idStart - 1])) idStart--;
                while (idStart > 0 && (char.IsLetterOrDigit(curLine[idStart - 1]) || curLine[idStart - 1] == '_')) idStart--;
                targetCol = idStart;
            }
            else
            {
                // Multi-line invocation: align with indentation of current line
                int firstNonWs = 0;
                while (firstNonWs < curLine.Length && char.IsWhiteSpace(curLine[firstNonWs])) firstNonWs++;
                targetCol = firstNonWs;
            }

            int safeCol = Mathf.Clamp(targetCol, 0, curLine.Length);
            float targetLocalX = LINE_NUM_WIDTH + CODE_PADDING_LEFT + GetTextWidth(m_CaretLine, safeCol) - scrollView.scrollOffset.x;
            float minLocalX = LINE_NUM_WIDTH + CODE_PADDING_LEFT;
            float clampedLocalX = Mathf.Max(targetLocalX, minLocalX);

            float lineTopLocal = (visIdx * LINE_HEIGHT) - scrollView.scrollOffset.y;

            PopupPlacement.Show(m_SignatureHelpPopup, PopupPlacement.LineAnchor(this, clampedLocalX, clampedLocalX + 1, lineTopLocal, LINE_HEIGHT), preferAbove: true, minHeight: 20f);
        }

        private async void ShowAutoCompleteAsync(bool isScope)
        {
            if (m_AutoCompletePopup == null || !(LanguageService is CSharpLanguageService csharpService)) return;

            int requestId = ++m_AutoCompleteRequestId;

            string fullCode = string.Join("\n", m_Lines);
            int absoluteCaret = GetAbsoluteCaretPosition();

            List<CompletionItem> suggestions = null;
            string prefix = null;
            m_AutoCompleteReplaceStartCol = -1;

            // "override |": base members with ready-made stubs.
            if (isScope)
            {
                var overrides = await csharpService.Roslyn.GetOverrideCompletionsAsync(fullCode, absoluteCaret);
                if (requestId != m_AutoCompleteRequestId) return;
                if (overrides.items != null)
                {
                    int startCol = m_CaretColumn - (absoluteCaret - overrides.replaceStart);
                    if (overrides.items.Count == 0 || startCol < 0 || startCol > m_CaretColumn) { m_AutoCompletePopup.Hide(); return; }
                    suggestions = overrides.items;
                    prefix = GetWordBeforeCaret();
                    m_AutoCompleteReplaceStartCol = startCol;
                }
            }

            // Inside "..." passed to CompareTag / LoadScene / Animator.Play etc.: offer the project's names instead.
            if (suggestions == null && CodeIDESettings.Current.Unity.UnityStringIntelligence)
            {
                var unityStrings = await csharpService.Roslyn.GetUnityStringCompletionsAsync(fullCode, absoluteCaret, UnityProjectData.Current);
                if (requestId != m_AutoCompleteRequestId) return;
                if (unityStrings != null)
                {
                    int startCol = m_CaretColumn - (absoluteCaret - unityStrings.ContentStart);
                    if (startCol < 0 || startCol > m_CaretColumn) { m_AutoCompletePopup.Hide(); return; }
                    suggestions = unityStrings.Items;
                    prefix = m_Lines[m_CaretLine].Substring(startCol, m_CaretColumn - startCol);
                }
                else if (IsCaretInsideString())
                {
                    m_AutoCompletePopup.Hide();
                    return;
                }
            }

            if (suggestions == null)
            {
                if (isScope)
                    suggestions = await csharpService.Roslyn.GetScopeCompletionsAsync(fullCode, absoluteCaret);
                else
                    suggestions = await csharpService.Roslyn.GetCompletionsAsync(fullCode, absoluteCaret);
                prefix = isScope ? GetWordBeforeCaret() : "";
            }

            if (requestId != m_AutoCompleteRequestId) return; // Stale request

            if (suggestions == null || suggestions.Count == 0)
            {
                m_AutoCompletePopup.Hide();
                return;
            }

            m_AutoCompletePrefix = prefix;

            var scrollView = m_ListView.Q<ScrollView>();
            if (scrollView == null) return;

            int safeCol = Mathf.Clamp(m_CaretColumn - prefix.Length, 0, m_Lines[m_CaretLine].Length);
            float wordX = LINE_NUM_WIDTH + CODE_PADDING_LEFT + GetTextWidth(m_CaretLine, safeCol) - scrollView.scrollOffset.x;
            float caretX = LINE_NUM_WIDTH + CODE_PADDING_LEFT + GetTextWidth(m_CaretLine, m_CaretColumn) - scrollView.scrollOffset.x;
            int caretVisIdx = Mathf.Max(0, GetVisibleLineIndex(m_CaretLine)); // folded blocks above shift the row
            float lineTop = caretVisIdx * LINE_HEIGHT - scrollView.scrollOffset.y;

            // Below the caret line (signature help uses the space above), flipping up near the bottom edge.
            m_HoverInfoPopup.style.display = DisplayStyle.None;
            m_AutoCompletePopup.Show(PopupPlacement.LineAnchor(this, wordX, Mathf.Max(wordX + 1, caretX), lineTop, LINE_HEIGHT), suggestions);
            if (prefix.Length > 0) m_AutoCompletePopup.Filter(prefix);
        }

        private bool IsCaretInsideString()
        {
            if (m_CaretLine < 0 || m_CaretLine >= m_Lines.Count) return false;
            string line = m_Lines[m_CaretLine];
            int quotes = 0;
            for (int i = 0; i < m_CaretColumn && i < line.Length; i++)
            {
                if (line[i] == '"' && (i == 0 || line[i - 1] != '\\')) quotes++;
            }
            return quotes % 2 != 0;
        }

        private async void FormatDocumentAsync(CSharpLanguageService csharpService)
        {
            string fullCode = string.Join("\n", m_Lines);
            string formattedCode = await csharpService.Roslyn.FormatDocumentAsync(fullCode);

            if (fullCode != formattedCode && !string.IsNullOrEmpty(formattedCode))
            {
                string cleanCode = formattedCode.Replace("\r\n", "\n");

                var cmds = new List<IEditCommand>();
                cmds.Add(new DeleteSelectionCommand(m_Lines, 0, 0, m_Lines.Count - 1, m_Lines[m_Lines.Count - 1].Length, SetCaretAction));
                cmds.Add(new InsertTextCommand(m_Lines, cleanCode, 0, 0, SetCaretAction));

                ExecuteHistory(cmds);
                SetCaretPosition(0, 0, true);
                RefreshListView();
            }
        }

        // The first analysis after a domain reload runs standalone while the project compilation builds in the
        // background; re-run once it is ready so cross-file types and diagnostics become accurate.
        private void OnProjectAssemblyReady(string assemblyName)
        {
            if (LanguageService is CSharpLanguageService csharpService && !csharpService.Roslyn.IsProjectAnalysis &&
                ProjectCompilationCache.ContainsFile(CurrentFilePath))
            {
                TriggerCodeAnalysis();
            }
        }
    }
}
