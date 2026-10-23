using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using FuzzyLogicLabs.FuzzyIDE.Editor.Core;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.UI
{
    public class LiveWatchWindow : EditorWindow
    {
        public static LiveWatchWindow Instance { get; private set; }
        public static bool IsOpen => Instance != null;

        private ListView m_WatchListView;
        [MenuItem("Window/Fuzzy Logic Labs/Fuzzy IDE/Live Watch", false, 21)]
        public static LiveWatchWindow ShowWindow()
        {
            var window = GetWindow<LiveWatchWindow>("Live Watch");
            window.minSize = new Vector2(350, 200);
            window.Show();
            return window;
        }

        private void OnEnable()
        {
            Instance = this;
            Texture icon = EditorGUIUtility.IconContent("d_console.infoicon.sml")?.image;
            titleContent = new GUIContent("Live Watch", icon);
            InitUI();
            EditorApplication.update += RefreshData;
        }

        private void OnDisable()
        {
            if (Instance == this) Instance = null;
            EditorApplication.update -= RefreshData;
        }

        private void InitUI()
        {
            rootVisualElement.Clear();
            rootVisualElement.style.backgroundColor = new Color(0.145f, 0.145f, 0.149f);
            rootVisualElement.style.flexDirection = FlexDirection.Column;

            var header = new VisualElement();
            header.style.flexDirection = FlexDirection.Row;
            header.style.backgroundColor = new Color(0.176f, 0.176f, 0.176f);
            header.style.height = 25;
            header.style.minHeight = 25;
            header.style.alignItems = Align.Center;
            header.style.justifyContent = Justify.SpaceBetween;
            header.style.paddingLeft = 10;
            header.style.paddingRight = 10;

            var title = new Label("LIVE WATCH");
            title.style.color = new Color(0.7f, 0.7f, 0.7f);
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.fontSize = 11;

            header.Add(title);
            rootVisualElement.Add(header);

            m_WatchListView = new ListView();
            m_WatchListView.style.flexGrow = 1;
            m_WatchListView.fixedItemHeight = 25;
            m_WatchListView.makeItem = MakeWatchItem;
            m_WatchListView.bindItem = BindWatchItem;
            m_WatchListView.itemsSource = new List<EditorTrackedVariableContext>();
            m_WatchListView.selectionType = SelectionType.Single;

            m_WatchListView.RegisterCallback<PointerDownEvent>(e =>
            {
                if (e.clickCount == 2 && m_WatchListView.selectedIndex >= 0)
                {
                    var ctx = (EditorTrackedVariableContext)m_WatchListView.itemsSource[m_WatchListView.selectedIndex];
                    NavigateToWatch(ctx);
                }
            });

            rootVisualElement.Add(m_WatchListView);
            RefreshData();
        }

        private VisualElement MakeWatchItem()
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.justifyContent = Justify.SpaceBetween;
            row.style.alignItems = Align.Center;
            row.style.paddingLeft = 5;
            row.style.paddingRight = 5;

            var leftBox = new VisualElement();
            leftBox.style.flexDirection = FlexDirection.Row;
            leftBox.style.flexGrow = 1;

            var scriptLabel = new Label();
            scriptLabel.name = "scriptLabel";
            scriptLabel.style.width = 150;
            scriptLabel.style.color = new Color(0.3f, 0.6f, 0.9f);

            var varLabel = new Label();
            varLabel.name = "varLabel";
            varLabel.style.width = 150;
            varLabel.style.color = new Color(0.8f, 0.8f, 0.8f);

            var valLabel = new Label();
            valLabel.name = "valLabel";
            valLabel.style.flexGrow = 1;
            valLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            valLabel.style.color = new Color(0.8f, 0.9f, 0.5f);

            leftBox.Add(scriptLabel);
            leftBox.Add(varLabel);
            leftBox.Add(valLabel);

            var unsubBtn = new Button();
            unsubBtn.name = "unsubBtn";
            unsubBtn.text = "X";
            unsubBtn.tooltip = "Unsubscribe & Remove Tracking Code";
            unsubBtn.style.backgroundColor = Color.clear;
            unsubBtn.style.color = Color.red;
            unsubBtn.style.borderTopWidth = 0; unsubBtn.style.borderBottomWidth = 0;
            unsubBtn.style.borderLeftWidth = 0; unsubBtn.style.borderRightWidth = 0;

            row.Add(leftBox);
            row.Add(unsubBtn);

            return row;
        }

        private void BindWatchItem(VisualElement element, int index)
        {
            var list = (List<EditorTrackedVariableContext>)m_WatchListView.itemsSource;
            if (index < 0 || index >= list.Count) return;

            var ctx = list[index];
            var scriptLabel = element.Q<Label>("scriptLabel");
            var varLabel = element.Q<Label>("varLabel");
            var valLabel = element.Q<Label>("valLabel");
            var unsubBtn = element.Q<Button>("unsubBtn");

            scriptLabel.text = $"{System.IO.Path.GetFileName(ctx.ScriptPath)} : {ctx.LineNumber}";
            varLabel.text = ctx.VariableName;
            valLabel.text = ctx.CurrentValue;

            element.userData = ctx.Id;
            valLabel.userData = ctx.Id;

            unsubBtn.clickable = new Clickable(() =>
            {
                RemoveWatch(ctx);
            });
        }

        private void RefreshData()
        {
            if (m_WatchListView == null) return;
            var stateDict = GetLiveStateDict();
            if (stateDict == null) return;

            var list = (List<EditorTrackedVariableContext>)m_WatchListView.itemsSource;
            bool layoutChanged = false;

            if (list.Count != stateDict.Count)
            {
                layoutChanged = true;
            }
            else
            {
                foreach (System.Collections.DictionaryEntry kvp in stateDict)
                {
                    if (!list.Exists(x => x.Id == (string)kvp.Key))
                    {
                        layoutChanged = true;
                        break;
                    }
                }
            }

            if (layoutChanged)
            {
                list.Clear();
                foreach (System.Collections.DictionaryEntry kvp in stateDict)
                {
                    object val = kvp.Value;
                    var type = val.GetType();
                    try
                    {
                        list.Add(new EditorTrackedVariableContext
                        {
                            Id = (string)kvp.Key,
                            ScriptPath = (string)type.GetField("ScriptPath").GetValue(val),
                            VariableName = (string)type.GetField("VariableName").GetValue(val),
                            CurrentValue = (string)type.GetField("CurrentValue").GetValue(val)
                        });
                    }
                    catch { }
                }
                m_WatchListView.RefreshItems();
                Repaint();
            }
            else
            {
                bool needsRepaint = false;
                m_WatchListView.Query<VisualElement>().ForEach(el =>
                {
                    if (el.userData is string trackId && stateDict.Contains(trackId))
                    {
                        object val = stateDict[trackId];
                        string liveValue = (string)val.GetType().GetField("CurrentValue").GetValue(val);
                        var valLabel = el.Q<Label>("valLabel");
                        if (valLabel != null && valLabel.text != liveValue)
                        {
                            valLabel.text = liveValue;
                            needsRepaint = true;
                        }
                    }
                });
                if (needsRepaint) Repaint();
            }
        }

        private System.Collections.IDictionary GetLiveStateDict()
        {
            var t = UnityCompat.FindType("FuzzyLogicLabs.LiveStateTracker");
            if (t != null)
            {
                var f = t.GetField("State", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (f != null) return f.GetValue(null) as System.Collections.IDictionary;
            }
            return null;
        }

        private void NavigateToWatch(EditorTrackedVariableContext ctx)
        {
            var ide = VirtualIDEWindow.ActiveInstance;
            if (ide != null)
            {
                ide.LoadFileAndJumpToLine(ctx.ScriptPath, ctx.LineNumber - 1);
            }
        }

        private void RemoveWatch(EditorTrackedVariableContext ctx)
        {
            var stateDict = GetLiveStateDict();
            if (stateDict != null) stateDict.Remove(ctx.Id);
            RefreshData();
        }
    }
}
