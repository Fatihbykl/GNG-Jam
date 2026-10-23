using System;
using System.Collections.Generic;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.Core
{
    [Serializable]
    public class SerializedCommand
    {
        public int Type;
        public string Text;
        public int L, C;
        public bool WasNewline;
        public int SL, SC, EL, EC;
        public int Dir;
        public List<string> DeletedLines;
        public int ChildCount;
    }

    [Serializable]
    public class SerializedHistoryData
    {
        public List<SerializedCommand> UndoStack;
        public List<SerializedCommand> RedoStack;
    }

    public interface IEditCommand
    {
        void Execute();
        void Undo();
        void Redo();
        void SerializeInto(List<SerializedCommand> list);
    }

    public class TextHistoryManager
    {
        private Stack<IEditCommand> m_UndoStack = new Stack<IEditCommand>();
        private Stack<IEditCommand> m_RedoStack = new Stack<IEditCommand>();
        private const int MAX_HISTORY = 200;

        // Consecutive single-character edits typed within this window form one undo step.
        public const double CoalesceWindowSeconds = 1.0;

        /// <summary>Time source for coalescing; replaceable in tests.</summary>
        public Func<DateTime> Clock = () => DateTime.UtcNow;

        private DateTime m_LastAddTime = DateTime.MinValue;
        private bool m_CoalescingBroken = true;

        public string Serialize()
        {
            var data = new SerializedHistoryData();
            data.UndoStack = new List<SerializedCommand>();
            foreach (var cmd in m_UndoStack) cmd.SerializeInto(data.UndoStack);
            data.UndoStack.Reverse(); 

            data.RedoStack = new List<SerializedCommand>();
            foreach (var cmd in m_RedoStack) cmd.SerializeInto(data.RedoStack);
            data.RedoStack.Reverse();

            return UnityEngine.JsonUtility.ToJson(data);
        }

        public void Deserialize(string json, List<string> lines, Action<int, int> setCaret, Action updateSelection)
        {
            m_UndoStack.Clear();
            m_RedoStack.Clear();
            if (string.IsNullOrEmpty(json)) return;

            var data = UnityEngine.JsonUtility.FromJson<SerializedHistoryData>(json);
            if (data == null) return;

            if (data.UndoStack != null)
            {
                int index = 0;
                while (index < data.UndoStack.Count)
                {
                    var cmd = ParseCommand(data.UndoStack, ref index, lines, setCaret, updateSelection);
                    if (cmd != null) m_UndoStack.Push(cmd);
                }
            }
            
            if (data.RedoStack != null)
            {
                int index = 0;
                while (index < data.RedoStack.Count)
                {
                    var cmd = ParseCommand(data.RedoStack, ref index, lines, setCaret, updateSelection);
                    if (cmd != null) m_RedoStack.Push(cmd);
                }
            }
        }

        private static IEditCommand ParseCommand(List<SerializedCommand> list, ref int index, List<string> lines, Action<int, int> setCaret, Action updateSelection)
        {
            if (index >= list.Count) return null;
            var s = list[index++];
            switch (s.Type)
            {
                case 1: return new InsertTextCommand(lines, s.Text, s.L, s.C, setCaret);
                case 2: return new DeleteTextCommand(lines, s.Text, s.L, s.C, s.WasNewline, setCaret);
                case 3: 
                    return new DeleteSelectionCommand(lines, s.SL, s.SC, s.EL, s.EC, setCaret, s.DeletedLines);
                case 4: return new MoveLinesCommand(lines, s.SL, s.EL, s.Dir, updateSelection);
                case 5:
                    var children = new List<IEditCommand>();
                    for (int k = 0; k < s.ChildCount; k++)
                    {
                        var child = ParseCommand(list, ref index, lines, setCaret, updateSelection);
                        if (child != null) children.Add(child);
                    }
                    return new CompositeCommand(children);
            }
            return null;
        }

        /// <summary>Starts a new undo step for the next edit (e.g. after a save or caret jump).</summary>
        public void BreakCoalescing()
        {
            m_CoalescingBroken = true;
        }

        public void AddCommand(IEditCommand command)
        {
            DateTime now = Clock();
            bool withinWindow = !m_CoalescingBroken && (now - m_LastAddTime).TotalSeconds <= CoalesceWindowSeconds;
            m_LastAddTime = now;
            m_CoalescingBroken = false;
            m_RedoStack.Clear();

            if (withinWindow && m_UndoStack.Count > 0 && TypingGroupCommand.TryMerge(m_UndoStack.Peek(), command, out var merged))
            {
                if (merged != null)
                {
                    m_UndoStack.Pop();
                    m_UndoStack.Push(merged);
                }
                return;
            }

            m_UndoStack.Push(command);

            if (m_UndoStack.Count > MAX_HISTORY)
            {
                var arr = m_UndoStack.ToArray();
                m_UndoStack.Clear();
                // arr[0] is newest, arr[arr.Length - 1] is oldest.
                for (int i = arr.Length - 2; i >= 0; i--) 
                {
                    m_UndoStack.Push(arr[i]);
                }
            }
        }

        public bool Undo()
        {
            if (m_UndoStack.Count > 0)
            {
                var command = m_UndoStack.Pop();
                command.Undo();
                m_RedoStack.Push(command);
                m_CoalescingBroken = true;
                return true;
            }
            return false;
        }

        public bool Redo()
        {
            if (m_RedoStack.Count > 0)
            {
                var command = m_RedoStack.Pop();
                command.Redo();
                m_UndoStack.Push(command);
                m_CoalescingBroken = true;
                return true;
            }
            return false;
        }
    }

    public class CompositeCommand : IEditCommand
    {
        private List<IEditCommand> m_Commands;
        public CompositeCommand(List<IEditCommand> commands) { m_Commands = commands; }
        
        public void Execute() { foreach(var c in m_Commands) c.Execute(); }
        public void Undo() { for(int i = m_Commands.Count - 1; i >= 0; i--) m_Commands[i].Undo(); }
        public void Redo() { foreach(var c in m_Commands) c.Redo(); }
        
        public void SerializeInto(List<SerializedCommand> list) 
        { 
            list.Add(new SerializedCommand { Type = 5, ChildCount = m_Commands.Count }); 
            foreach(var c in m_Commands) c.SerializeInto(list);
        }
    }

    /// <summary>
    /// A run of single-character inserts (typing) or deletes (Backspace / Delete) that undoes as one step.
    /// Serialized as a composite, so restored history keeps the grouping but no longer merges.
    /// </summary>
    public class TypingGroupCommand : IEditCommand
    {
        private readonly List<IEditCommand> m_Commands;

        private TypingGroupCommand(List<IEditCommand> commands) { m_Commands = commands; }

        public IEditCommand Last => m_Commands[m_Commands.Count - 1];

        public void Execute() { foreach (var c in m_Commands) c.Execute(); }
        public void Undo() { for (int i = m_Commands.Count - 1; i >= 0; i--) m_Commands[i].Undo(); }
        public void Redo() { foreach (var c in m_Commands) c.Redo(); }

        public void SerializeInto(List<SerializedCommand> list)
        {
            list.Add(new SerializedCommand { Type = 5, ChildCount = m_Commands.Count });
            foreach (var c in m_Commands) c.SerializeInto(list);
        }

        /// <summary>
        /// Merges <paramref name="next"/> into <paramref name="top"/> when both are contiguous typing of the same kind.
        /// Returns true when merged; <paramref name="replacement"/> is the new stack top (null when <paramref name="top"/>
        /// was already a group that absorbed the command).
        /// </summary>
        public static bool TryMerge(IEditCommand top, IEditCommand next, out IEditCommand replacement)
        {
            replacement = null;
            var group = top as TypingGroupCommand;
            var previous = group != null ? group.Last : top;

            if (!Continues(previous, next)) return false;

            if (group != null)
            {
                group.m_Commands.Add(next);
            }
            else
            {
                replacement = new TypingGroupCommand(new List<IEditCommand> { top, next });
            }
            return true;
        }

        private static bool Continues(IEditCommand previous, IEditCommand next)
        {
            if (previous is InsertTextCommand a && next is InsertTextCommand b)
            {
                if (!IsTypedChar(a.Text) || !IsTypedChar(b.Text)) return false;
                if (b.Line != a.Line || b.Column != a.Column + a.Text.Length) return false;
                // Typing whitespace after a word closes the word's undo step (like VS Code / Rider).
                return !(char.IsWhiteSpace(b.Text[0]) && !char.IsWhiteSpace(a.Text[a.Text.Length - 1]));
            }

            if (previous is DeleteTextCommand d1 && next is DeleteTextCommand d2)
            {
                if (d1.WasNewline || d2.WasNewline || d1.DeletedText.Length != 1 || d2.DeletedText.Length != 1) return false;
                if (d2.Line != d1.Line) return false;
                // Backspace walks left, Delete stays in place.
                return d2.Column == d1.Column - 1 || d2.Column == d1.Column;
            }

            return false;
        }

        private static bool IsTypedChar(string text) => text != null && text.Length == 1 && text[0] != '\n';
    }

    public class InsertTextCommand : IEditCommand
    {
        private List<string> m_Lines;
        private string m_Text;
        private int m_Line;
        private int m_Col;
        private Action<int, int> m_SetCaret;

        public string Text => m_Text;
        public int Line => m_Line;
        public int Column => m_Col;

        public void SerializeInto(List<SerializedCommand> list) { list.Add(new SerializedCommand { Type = 1, Text = m_Text, L = m_Line, C = m_Col }); }

        public InsertTextCommand(List<string> lines, string text, int line, int col, Action<int, int> setCaret)
        {
            m_Lines = lines;
            m_Text = text;
            m_Line = line;
            m_Col = col;
            m_SetCaret = setCaret;
        }

        public void Execute() { Redo(); }

        public void Undo()
        {
            string[] parts = m_Text.Split('\n');
            if (parts.Length == 1)
            {
                m_Lines[m_Line] = m_Lines[m_Line].Remove(m_Col, m_Text.Length);
            }
            else
            {
                string firstLine = m_Lines[m_Line];
                string lastLine = m_Lines[m_Line + parts.Length - 1];
                
                string before = firstLine.Substring(0, m_Col);
                string after = lastLine.Substring(parts[parts.Length - 1].Length);
                
                m_Lines[m_Line] = before + after;
                
                for (int i = 1; i < parts.Length; i++)
                {
                    m_Lines.RemoveAt(m_Line + 1);
                }
            }
            m_SetCaret?.Invoke(m_Line, m_Col);
        }

        public void Redo()
        {
            string[] parts = m_Text.Split('\n');
            if (parts.Length == 1)
            {
                m_Lines[m_Line] = m_Lines[m_Line].Insert(m_Col, m_Text);
                m_SetCaret?.Invoke(m_Line, m_Col + m_Text.Length);
            }
            else
            {
                string currentLine = m_Lines[m_Line];
                string before = currentLine.Substring(0, m_Col);
                string after = currentLine.Substring(m_Col);
                
                m_Lines[m_Line] = before + parts[0];
                
                for (int i = 1; i < parts.Length - 1; i++)
                {
                    m_Lines.Insert(m_Line + i, parts[i]);
                }
                
                m_Lines.Insert(m_Line + parts.Length - 1, parts[parts.Length - 1] + after);
                m_SetCaret?.Invoke(m_Line + parts.Length - 1, parts[parts.Length - 1].Length);
            }
        }
    }

    public class DeleteTextCommand : IEditCommand
    {
        private List<string> m_Lines;
        private string m_DeletedText;
        private int m_Line;
        private int m_Col;
        private bool m_WasNewline;
        private Action<int, int> m_SetCaret;

        public string DeletedText => m_DeletedText;
        public int Line => m_Line;
        public int Column => m_Col;
        public bool WasNewline => m_WasNewline;

        public void SerializeInto(List<SerializedCommand> list) { list.Add(new SerializedCommand { Type = 2, Text = m_DeletedText, L = m_Line, C = m_Col, WasNewline = m_WasNewline }); }

        public DeleteTextCommand(List<string> lines, string deletedText, int line, int col, bool wasNewline, Action<int, int> setCaret)
        {
            m_Lines = lines;
            m_DeletedText = deletedText;
            m_Line = line;
            m_Col = col;
            m_WasNewline = wasNewline;
            m_SetCaret = setCaret;
        }

        public void Execute() { Redo(); }

        public void Undo()
        {
            if (m_WasNewline)
            {
                string currentLine = m_Lines[m_Line];
                string before = currentLine.Substring(0, m_Col);
                string after = currentLine.Substring(m_Col);
                m_Lines[m_Line] = before;
                m_Lines.Insert(m_Line + 1, after);
                m_SetCaret(m_Line + 1, 0);
            }
            else
            {
                m_Lines[m_Line] = m_Lines[m_Line].Insert(m_Col, m_DeletedText);
                m_SetCaret(m_Line, m_Col + m_DeletedText.Length);
            }
        }

        public void Redo()
        {
            if (m_WasNewline)
            {
                string nextLine = m_Lines[m_Line + 1];
                m_Lines[m_Line] = m_Lines[m_Line] + nextLine;
                m_Lines.RemoveAt(m_Line + 1);
            }
            else
            {
                m_Lines[m_Line] = m_Lines[m_Line].Remove(m_Col, m_DeletedText.Length);
            }
            m_SetCaret(m_Line, m_Col);
        }
    }

    public class DeleteSelectionCommand : IEditCommand
    {
        private List<string> m_Lines;
        private List<string> m_DeletedLines = new List<string>();
        private int m_StartL, m_StartC, m_EndL, m_EndC;
        private Action<int, int> m_SetCaret;

        public void SerializeInto(List<SerializedCommand> list) { list.Add(new SerializedCommand { Type = 3, SL = m_StartL, SC = m_StartC, EL = m_EndL, EC = m_EndC, DeletedLines = m_DeletedLines }); }

        public DeleteSelectionCommand(List<string> lines, int startL, int startC, int endL, int endC, Action<int, int> setCaret, List<string> precomputedDeletedLines = null)
        {
            m_Lines = lines;
            m_StartL = startL;
            m_StartC = startC;
            m_EndL = endL;
            m_EndC = endC;
            m_SetCaret = setCaret;

            if (precomputedDeletedLines != null)
            {
                m_DeletedLines = precomputedDeletedLines;
            }
            else
            {
                if (startL == endL)
                {
                    m_DeletedLines.Add(lines[startL].Substring(startC, endC - startC));
                }
                else
                {
                    m_DeletedLines.Add(lines[startL].Substring(startC));
                    for(int i = startL + 1; i < endL; i++) m_DeletedLines.Add(lines[i]);
                    m_DeletedLines.Add(lines[endL].Substring(0, endC));
                }
            }
        }

        public void Execute() { Redo(); }

        public void Undo()
        {
            if (m_StartL == m_EndL)
            {
                m_Lines[m_StartL] = m_Lines[m_StartL].Insert(m_StartC, m_DeletedLines[0]);
            }
            else
            {
                string remnant = m_Lines[m_StartL];
                string before = remnant.Substring(0, m_StartC);
                string after = remnant.Substring(m_StartC); 
                
                m_Lines[m_StartL] = before + m_DeletedLines[0];
                
                var insertLines = new List<string>();
                for (int i = 1; i < m_DeletedLines.Count - 1; i++)
                    insertLines.Add(m_DeletedLines[i]);
                    
                insertLines.Add(m_DeletedLines[m_DeletedLines.Count - 1] + after);
                
                m_Lines.InsertRange(m_StartL + 1, insertLines);
            }
            m_SetCaret(m_EndL, m_EndC); 
        }

        public void Redo()
        {
            if (m_StartL == m_EndL)
            {
                m_Lines[m_StartL] = m_Lines[m_StartL].Remove(m_StartC, m_EndC - m_StartC);
            }
            else
            {
                string startRemnant = m_Lines[m_StartL].Substring(0, m_StartC);
                string endRemnant = m_Lines[m_EndL].Substring(m_EndC);
                m_Lines[m_StartL] = startRemnant + endRemnant;
                m_Lines.RemoveRange(m_StartL + 1, m_EndL - m_StartL);
            }
            m_SetCaret(m_StartL, m_StartC);
        }
    }

    public class MoveLinesCommand : IEditCommand
    {
        private List<string> m_Lines;
        private int m_StartLine;
        private int m_EndLine;
        private int m_Direction; // -1 for Up, +1 for Down
        private Action m_UpdateSelection;

        public void SerializeInto(List<SerializedCommand> list) { list.Add(new SerializedCommand { Type = 4, SL = m_StartLine, EL = m_EndLine, Dir = m_Direction }); }

        public MoveLinesCommand(List<string> lines, int startLine, int endLine, int direction, Action updateSelection)
        {
            m_Lines = lines;
            m_StartLine = startLine;
            m_EndLine = endLine;
            m_Direction = direction;
            m_UpdateSelection = updateSelection;
        }

        public void Execute() { Redo(); }

        public void Undo()
        {
            Move(-m_Direction);
            m_UpdateSelection();
        }

        public void Redo()
        {
            Move(m_Direction);
            m_UpdateSelection();
        }

        private void Move(int dir)
        {
            if (dir == -1)
            {
                string temp = m_Lines[m_StartLine - 1];
                m_Lines.RemoveAt(m_StartLine - 1);
                m_Lines.Insert(m_EndLine, temp);
            }
            else
            {
                string temp = m_Lines[m_EndLine + 1];
                m_Lines.RemoveAt(m_EndLine + 1);
                m_Lines.Insert(m_StartLine, temp);
            }
        }
    }
}
