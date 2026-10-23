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
        // -----------------------
        // Smart Drag & Drop (Phase 12)
        // -----------------------
        private void OnDragUpdated(DragUpdatedEvent evt)
        {
            if (DragAndDrop.objectReferences.Length > 0)
            {
                DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
                var scrollView = m_ListView.Q<ScrollView>();
                if (scrollView != null)
                {
                    Vector2 localPos = m_ListView.WorldToLocal(evt.mousePosition);
                    GetLineAndCol(localPos, scrollView, out int line, out int col);
                    SetCaretPosition(line, col, clearSelection: true);
                    RefreshListView();
                }
                evt.StopPropagation();
            }
        }

        private void OnDragPerform(DragPerformEvent evt)
        {
            if (DragAndDrop.objectReferences.Length == 0) return;
            DragAndDrop.AcceptDrag();

            UnityEngine.Object draggedObj = DragAndDrop.objectReferences[0];
            if (draggedObj == null) return;

            string objType = "";
            string varName = "";
            bool isComponent = false;

            if (draggedObj is GameObject go)
            {
                objType = "GameObject";
                string rawName = go.name.Replace(" ", "");
                if (string.IsNullOrEmpty(rawName)) rawName = "gameObject";
                varName = char.ToLowerInvariant(rawName[0]) + rawName.Substring(1);
            }
            else if (draggedObj is Component comp)
            {
                isComponent = true;
                objType = comp.GetType().Name;
                varName = "my" + objType;
            }
            else return;

            varName = new string(varName.Where(char.IsLetterOrDigit).ToArray());

            if (CodeIDESettings.Current.Behaviors.SmartDragDrop == BehaviorSettings.DragDropBehavior.AutoAssign)
            {
                ShowVariableNamePopup(varName, (finalName) => {
                    CreateExternalReference(draggedObj, objType, finalName);
                });
                evt.StopPropagation();
                return;
            }

            GenericMenu menu = new GenericMenu();
            menu.AddItem(new GUIContent("Create External Reference (Auto-Assign)"), false, () =>
            {
                ShowVariableNamePopup(varName, (finalName) => {
                    CreateExternalReference(draggedObj, objType, finalName);
                });
            });

            if (isComponent)
            {
                menu.AddItem(new GUIContent("Create Local Reference (GetComponent)"), false, () =>
                {
                    ShowVariableNamePopup(varName, (finalName) => {
                        CreateLocalReference(objType, finalName);
                    });
                });
            }

            menu.DropDown(new Rect(evt.mousePosition, Vector2.zero));
            evt.StopPropagation();
        }

        private void ShowVariableNamePopup(string initialName, System.Action<string> onConfirm)
        {
            var overlay = new VisualElement();
            overlay.style.position = Position.Absolute;
            overlay.style.left = 0; overlay.style.right = 0;
            overlay.style.top = 0; overlay.style.bottom = 0;
            overlay.style.backgroundColor = new Color(0, 0, 0, 0.5f);
            overlay.style.alignItems = Align.Center;
            overlay.style.justifyContent = Justify.Center;

            var box = new VisualElement();
            box.style.backgroundColor = new Color(0.2f, 0.2f, 0.2f, 1f);
            box.style.paddingLeft = 10; box.style.paddingRight = 10;
            box.style.paddingTop = 10; box.style.paddingBottom = 10;
            box.style.borderTopLeftRadius = 5; box.style.borderTopRightRadius = 5;
            box.style.borderBottomLeftRadius = 5; box.style.borderBottomRightRadius = 5;

            var label = new Label("Enter Variable Name:");
            label.style.marginBottom = 5;
            label.style.color = Color.white;
            box.Add(label);

            var textField = new TextField();
            textField.value = initialName;
            textField.style.width = 200;
            textField.style.marginBottom = 10;
            box.Add(textField);

            var buttonRow = new VisualElement();
            buttonRow.style.flexDirection = FlexDirection.Row;
            buttonRow.style.justifyContent = Justify.FlexEnd;

            var okButton = new Button(() => {
                string val = textField.value.Trim();
                if (!string.IsNullOrEmpty(val))
                {
                    Remove(overlay);
                    onConfirm(val);
                }
            }) { text = "OK" };

            var cancelButton = new Button(() => { Remove(overlay); }) { text = "Cancel" };

            buttonRow.Add(cancelButton);
            buttonRow.Add(okButton);
            box.Add(buttonRow);
            
            overlay.Add(box);
            Add(overlay);

            textField.RegisterCallback<KeyDownEvent>(e => {
                if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter)
                {
                    string val = textField.value.Trim();
                    if (!string.IsNullOrEmpty(val))
                    {
                        Remove(overlay);
                        onConfirm(val);
                    }
                }
                else if (e.keyCode == KeyCode.Escape)
                {
                    Remove(overlay);
                }
            });

            // Delay focus
            schedule.Execute(() => { textField.Focus(); textField.SelectAll(); }).StartingIn(10);
        }

        private async void CreateExternalReference(UnityEngine.Object draggedObj, string objType, string varName)
        {
            if (!(LanguageService is CSharpLanguageService csharpService)) return;

            int absoluteCaret = GetAbsoluteCaretPosition();
            string fullCode = string.Join("\n", m_Lines);

            var result = await csharpService.Roslyn.ProcessSmartDragDropInjectionAsync(fullCode, absoluteCaret, false, objType, varName);
            if (!result.IsValid) return;

            var cmds = new List<IEditCommand>();

            cmds.Add(new DeleteSelectionCommand(m_Lines, result.OldClassStartLine, result.OldClassStartCol, result.OldClassEndLine, result.OldClassEndCol, SetCaretAction));

            string cleanNewCode = result.NewClassCode.Replace("\r\n", "\n");
            cmds.Add(new InsertTextCommand(m_Lines, cleanNewCode, result.OldClassStartLine, result.OldClassStartCol, SetCaretAction));

            ExecuteHistory(cmds);

            int draggedInstanceId = 0;
            var getInstanceIdMethod = typeof(UnityEngine.Object).GetMethod("GetInstanceID");
            if (getInstanceIdMethod != null) draggedInstanceId = (int)getInstanceIdMethod.Invoke(draggedObj, null);

            AutoAssignIntent intent = new AutoAssignIntent
            {
                ScriptPath = CurrentFilePath,
                VariableName = result.UniqueVariableName,
                DraggedInstanceID = draggedInstanceId,
                TargetGameObjectInstanceID = 0
            };
            SmartDragDropPostprocessor.RegisterIntent(intent);
        }

        private async void CreateLocalReference(string objType, string varName)
        {
            if (!(LanguageService is CSharpLanguageService csharpService)) return;

            int absoluteCaret = GetAbsoluteCaretPosition();
            string fullCode = string.Join("\n", m_Lines);

            var result = await csharpService.Roslyn.ProcessSmartDragDropInjectionAsync(fullCode, absoluteCaret, true, objType, varName);
            if (!result.IsValid) return;

            var cmds = new List<IEditCommand>();

            cmds.Add(new DeleteSelectionCommand(m_Lines, result.OldClassStartLine, result.OldClassStartCol, result.OldClassEndLine, result.OldClassEndCol, SetCaretAction));

            string cleanNewCode = result.NewClassCode.Replace("\r\n", "\n");
            cmds.Add(new InsertTextCommand(m_Lines, cleanNewCode, result.OldClassStartLine, result.OldClassStartCol, SetCaretAction));

            ExecuteHistory(cmds);
        }
    }
}
