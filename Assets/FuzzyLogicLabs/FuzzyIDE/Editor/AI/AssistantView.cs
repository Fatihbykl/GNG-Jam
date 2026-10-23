using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.AI
{
    [Serializable]
    public class ChatHistoryWrapper
    {
        public List<ChatMessage> history;
    }

    public class AssistantView : VisualElement
    {
        private ScrollView m_MessageScroll;
        private TextField m_InputField;
        private Button m_SendBtn;
        private Label m_LoadingIndicator;
        
        private List<ChatMessage> m_History = new List<ChatMessage>();
        private bool m_IsWaitingForResponse = false;
        
        // This event can be subscribed to by VirtualIDEWindow to handle applying code to the active file.
        public event Action<string> OnApplyCodeRequested;
        public event System.Action<string, string, string, System.Action, System.Action> OnApplyDiffRequested;
        public event Action<string, string> OnCreateFileRequested;
        public event Func<string> OnGetContextRequested;

        public AssistantView()
        {
            this.style.flexGrow = 1;
            this.style.flexDirection = FlexDirection.Column;
            this.style.backgroundColor = new Color(0.12f, 0.12f, 0.12f);
            
            var configHeader = new VisualElement();
            configHeader.style.flexDirection = FlexDirection.Row;
            configHeader.style.flexWrap = Wrap.Wrap;
            configHeader.style.paddingLeft = 12;
            configHeader.style.paddingRight = 12;
            configHeader.style.paddingTop = 8;
            configHeader.style.paddingBottom = 8;
            configHeader.style.backgroundColor = new Color(0.18f, 0.2f, 0.22f); // Slightly distinct bluish-grey
            configHeader.style.borderBottomWidth = 2;
            configHeader.style.borderBottomColor = new Color(0.1f, 0.12f, 0.15f);
            configHeader.style.alignItems = Align.Center;
            configHeader.style.justifyContent = Justify.SpaceBetween;
            configHeader.style.flexShrink = 0;

            var modelDropdown = new DropdownField("Model");
            modelDropdown.style.flexGrow = 1;
            modelDropdown.style.minWidth = 120;
            modelDropdown.style.marginRight = 10;
            modelDropdown.style.marginBottom = 5;
            
            FuzzyLogicLabs.FuzzyIDE.Editor.Core.AIProvider lastProvider = (FuzzyLogicLabs.FuzzyIDE.Editor.Core.AIProvider)(-1);
            string lastOpenAIKey = null;
            string lastGeminiKey = null;
            string lastClaudeKey = null;

            Action updateModelChoices = async () => {
                var aiSettings = FuzzyLogicLabs.FuzzyIDE.Editor.Core.CodeIDESettings.Current.AI;
                var providerSettings = aiSettings.ActiveProvider;

                bool needsRefetch = (modelDropdown.choices.Count <= 1) || 
                                    (lastProvider != providerSettings) ||
                                    (lastOpenAIKey != aiSettings.OpenAIKey) ||
                                    (lastGeminiKey != aiSettings.GeminiKey) ||
                                    (lastClaudeKey != aiSettings.ClaudeKey);
                
                lastProvider = providerSettings;
                lastOpenAIKey = aiSettings.OpenAIKey;
                lastGeminiKey = aiSettings.GeminiKey;
                lastClaudeKey = aiSettings.ClaudeKey;

                if (!needsRefetch) {
                    if (modelDropdown.choices.Contains(aiSettings.ModelName)) {
                        modelDropdown.SetValueWithoutNotify(aiSettings.ModelName);
                    }
                    return;
                }

                modelDropdown.SetEnabled(false);

                bool hasKey = true;
                if (providerSettings == FuzzyLogicLabs.FuzzyIDE.Editor.Core.AIProvider.OpenAI && string.IsNullOrEmpty(aiSettings.OpenAIKey)) hasKey = false;
                if (providerSettings == FuzzyLogicLabs.FuzzyIDE.Editor.Core.AIProvider.Gemini && string.IsNullOrEmpty(aiSettings.GeminiKey)) hasKey = false;
                if (providerSettings == FuzzyLogicLabs.FuzzyIDE.Editor.Core.AIProvider.Claude && string.IsNullOrEmpty(aiSettings.ClaudeKey)) hasKey = false;

                if (!hasKey)
                {
                    modelDropdown.choices = new List<string> { "API Key Required" };
                    modelDropdown.value = "API Key Required";
                }
                else
                {
                    modelDropdown.choices = new List<string> { "Loading..." };
                    modelDropdown.value = "Loading...";

                    List<string> models = new List<string>();
                    try
                    {
                        models = await LLMClient.GetAvailableModelsAsync();
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError("[FuzzyIDE] Failed to fetch models: " + ex.Message);
                        models = new List<string> { "Error fetching models" };
                    }

                    modelDropdown.choices = models;
                    
                    string currentModel = aiSettings.ModelName;
                    if (!modelDropdown.choices.Contains(currentModel) && modelDropdown.choices.Count > 0)
                    {
                        aiSettings.ModelName = modelDropdown.choices[0];
                        FuzzyLogicLabs.FuzzyIDE.Editor.Core.CodeIDESettings.Save();
                    }
                    modelDropdown.value = aiSettings.ModelName;
                }
                
                modelDropdown.SetEnabled(true);
            };

            updateModelChoices();
            FuzzyLogicLabs.FuzzyIDE.Editor.Core.CodeIDESettings.OnSettingsChanged += updateModelChoices;

            modelDropdown.RegisterValueChangedCallback(e => {
                FuzzyLogicLabs.FuzzyIDE.Editor.Core.CodeIDESettings.Current.AI.ModelName = e.newValue;
                FuzzyLogicLabs.FuzzyIDE.Editor.Core.CodeIDESettings.Save();
            });

            var autoApplyToggle = new Toggle("Auto Apply");
            autoApplyToggle.value = FuzzyLogicLabs.FuzzyIDE.Editor.Core.CodeIDESettings.Current.AI.AutoApplyDiffs;
            autoApplyToggle.style.marginBottom = 5;
            autoApplyToggle.RegisterValueChangedCallback(e => {
                FuzzyLogicLabs.FuzzyIDE.Editor.Core.CodeIDESettings.Current.AI.AutoApplyDiffs = e.newValue;
                FuzzyLogicLabs.FuzzyIDE.Editor.Core.CodeIDESettings.Save();
            });

            var clearHistoryBtn = new Button(ClearHistory) { text = "Clear History" };
            clearHistoryBtn.style.backgroundColor = new Color(0.6f, 0.2f, 0.2f);
            clearHistoryBtn.style.color = Color.white;
            clearHistoryBtn.style.unityFontStyleAndWeight = FontStyle.Bold;
            clearHistoryBtn.style.marginLeft = 5;
            clearHistoryBtn.style.marginBottom = 5;

            configHeader.Add(modelDropdown);
            configHeader.Add(autoApplyToggle);
            configHeader.Add(clearHistoryBtn);
            this.Add(configHeader);

            m_MessageScroll = new ScrollView(ScrollViewMode.Vertical);
            m_MessageScroll.style.flexGrow = 1;
            m_MessageScroll.style.paddingLeft = 10;
            m_MessageScroll.style.paddingRight = 10;
            m_MessageScroll.style.paddingTop = 10;
            m_MessageScroll.style.paddingBottom = 10;
            this.Add(m_MessageScroll);
            
            m_LoadingIndicator = new Label("Agent is thinking...");
            m_LoadingIndicator.style.color = new Color(0.6f, 0.8f, 1f);
            m_LoadingIndicator.style.alignSelf = Align.Center;
            m_LoadingIndicator.style.marginTop = 10;
            m_LoadingIndicator.style.marginBottom = 10;
            m_LoadingIndicator.style.display = DisplayStyle.None;
            this.Add(m_LoadingIndicator);
            
            m_LoadingIndicator.schedule.Execute(() => {
                if (m_LoadingIndicator.style.display == DisplayStyle.Flex)
                {
                    string text = m_LoadingIndicator.text;
                    if (text == "Agent is thinking...") m_LoadingIndicator.text = "Agent is thinking";
                    else if (text == "Agent is thinking") m_LoadingIndicator.text = "Agent is thinking.";
                    else if (text == "Agent is thinking.") m_LoadingIndicator.text = "Agent is thinking..";
                    else if (text == "Agent is thinking..") m_LoadingIndicator.text = "Agent is thinking...";
                }
            }).Every(400);
            
            var inputArea = new VisualElement();
            inputArea.style.flexDirection = FlexDirection.Row;
            inputArea.style.paddingLeft = 10;
            inputArea.style.paddingRight = 10;
            inputArea.style.paddingTop = 10;
            inputArea.style.paddingBottom = 10;
            inputArea.style.backgroundColor = new Color(0.15f, 0.15f, 0.15f);
            inputArea.style.borderTopWidth = 1;
            inputArea.style.borderTopColor = new Color(0.2f, 0.2f, 0.2f);
            inputArea.style.flexShrink = 0;
            inputArea.style.alignItems = Align.Center;
            
            m_InputField = new TextField();
            m_InputField.isDelayed = false;
            m_InputField.multiline = true;
            m_InputField.style.flexGrow = 1;
            m_InputField.style.flexShrink = 1;
            m_InputField.style.height = 50;
            m_InputField.style.minHeight = 50;
            m_InputField.style.maxHeight = 50;
            m_InputField.style.whiteSpace = WhiteSpace.PreWrap;

            Action applyWordWrap = () => {
                var textInput = m_InputField.Q("unity-text-input");
                if (textInput != null)
                {
                    textInput.style.whiteSpace = WhiteSpace.PreWrap;
                }
            };

            m_InputField.RegisterCallback<AttachToPanelEvent>(e => applyWordWrap());
            applyWordWrap();
            
            m_InputField.RegisterCallback<KeyDownEvent>(e =>
            {
                if ((e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter) && e.actionKey)
                {
                    e.StopPropagation();
                    SendUserInput();
                }
            });
            
            m_SendBtn = new Button(SendUserInput);
            m_SendBtn.text = "Send";
            m_SendBtn.style.width = 60;
            m_SendBtn.style.height = 50;
            m_SendBtn.style.flexShrink = 0;
            m_SendBtn.style.marginLeft = 10;
            m_SendBtn.style.backgroundColor = new Color(0.2f, 0.5f, 0.8f);
            m_SendBtn.style.color = Color.white;
            m_SendBtn.style.unityFontStyleAndWeight = FontStyle.Bold;
            
            inputArea.Add(m_InputField);
            inputArea.Add(m_SendBtn);
            
            this.Add(inputArea);

            LoadHistory();
        }

        private void SaveHistory()
        {
            var wrapper = new ChatHistoryWrapper { history = m_History };
            EditorPrefs.SetString("FuzzyIDE_AIHistory", JsonUtility.ToJson(wrapper));
        }

        private void LoadHistory()
        {
            if (EditorPrefs.HasKey("FuzzyIDE_AIHistory"))
            {
                try
                {
                    var json = EditorPrefs.GetString("FuzzyIDE_AIHistory");
                    var wrapper = JsonUtility.FromJson<ChatHistoryWrapper>(json);
                    if (wrapper != null && wrapper.history != null && wrapper.history.Count > 0)
                    {
                        m_History = wrapper.history;
                        foreach (var msg in m_History)
                        {
                            if (msg.Role != ChatRole.System)
                            {
                                AddMessageToUI(msg.Role, msg.Content);
                            }
                        }
                        return;
                    }
                }
                catch {}
            }
            
            ClearHistory();
        }

        private void ClearHistory()
        {
            m_History.Clear();
            m_MessageScroll?.Clear();

            // Add system prompt
            m_History.Add(new ChatMessage
            {
                Role = ChatRole.System,
                Content = @"You are an expert AI programming assistant integrated directly into a Unity IDE. You provide concise, correct, and helpful C# code. 

Pay close attention to whether the user wants to CREATE A NEW SCRIPT or MODIFY THE ACTIVE SCRIPT.

1) If the user asks to create a NEW script or file, use this exact format to specify the file path and its content:
<create path=""Assets/Scripts/NewScript.cs"">
<code>
using UnityEngine;
public class NewScript : MonoBehaviour { }
</code>
</create>

2) If the user asks to modify an existing file (whether it is the CURRENTLY ACTIVE file or any other file you know the path for), use this exact format to replace existing lines. You MUST include the path attribute:
<replace path=""Assets/Scripts/Player.cs"">
<search>
the exact code lines to be replaced (must match exactly)
</search>
<code>
the new code to replace it with
</code>
</replace>

3) If you are just answering a question or providing general examples, use standard markdown code blocks (```csharp).

4) If you need to read a file from the project that is not in your context, you can read its content by outputting EXACTLY this format:
<call_tool name=""read_file"" path=""Assets/Scripts/SomeFile.cs"" />
Do not provide any other text when calling a tool. The system will automatically reply with the file content, and then you can continue."
            });
            
            AddMessageToUI(ChatRole.Assistant, "Hello! I am your AI Assistant. How can I help you with your code today?");
            SaveHistory();
        }

        public void SendDirectRequest(string prompt)
        {
            if (m_IsWaitingForResponse) return;
            m_InputField.value = prompt;
            SendUserInput();
        }

        private async void SendUserInput()
        {
            if (m_IsWaitingForResponse) return;
            
            string text = m_InputField.value.Trim();
            if (string.IsNullOrEmpty(text)) return;
            
            m_InputField.value = "";
            m_IsWaitingForResponse = true;
            m_SendBtn.SetEnabled(false);
            m_LoadingIndicator.style.display = DisplayStyle.Flex;
            
            string context = OnGetContextRequested?.Invoke();
            string finalUserPrompt = text;
            
            if (!string.IsNullOrEmpty(context))
            {
                finalUserPrompt += "\n\n--- Context ---\n" + context;
            }

            m_History.Add(new ChatMessage { Role = ChatRole.User, Content = finalUserPrompt });
            SaveHistory();
            AddMessageToUI(ChatRole.User, text); // show original text without context metadata

            try
            {
                string response = await LLMClient.SendMessageAsync(m_History);
                
                int maxLoops = 5;
                while (response.Contains("<call_tool") && maxLoops > 0)
                {
                    m_History.Add(new ChatMessage { Role = ChatRole.Assistant, Content = response });
                    AddMessageToUI(ChatRole.Assistant, response); // So user sees what the agent is doing

                    int tagStart = response.IndexOf("<call_tool");
                    int tagEnd = response.IndexOf("/>", tagStart);
                    if (tagEnd == -1) tagEnd = response.IndexOf(">", tagStart);
                    
                    if (tagEnd != -1)
                    {
                        string tag = response.Substring(tagStart, tagEnd - tagStart + 2); // safe approximation
                        string path = "";
                        int pathIdx = tag.IndexOf("path=\"");
                        if (pathIdx != -1)
                        {
                            pathIdx += 6;
                            int endQuote = tag.IndexOf("\"", pathIdx);
                            if (endQuote != -1) path = tag.Substring(pathIdx, endQuote - pathIdx);
                        }

                        string toolOutput = "";
                        if (string.IsNullOrEmpty(path))
                        {
                            toolOutput = "Error: path attribute not specified.";
                        }
                        else
                        {
                            if (System.IO.File.Exists(path))
                                toolOutput = System.IO.File.ReadAllText(path);
                            else
                                toolOutput = $"Error: File '{path}' not found.";
                        }

                        string systemResponse = $"[Tool Result - read_file: {path}]\n```csharp\n{toolOutput}\n```";
                        m_History.Add(new ChatMessage { Role = ChatRole.User, Content = systemResponse });
                        SaveHistory();
                        AddMessageToUI(ChatRole.User, $"[System]: Read file {path}");

                        response = await LLMClient.SendMessageAsync(m_History);
                    }
                    else
                    {
                        break; // malformed tag
                    }
                    
                    maxLoops--;
                }

                m_History.Add(new ChatMessage { Role = ChatRole.Assistant, Content = response });
                SaveHistory();
                AddMessageToUI(ChatRole.Assistant, response);
            }
            catch (Exception ex)
            {
                AddMessageToUI(ChatRole.Assistant, $"Error: {ex.Message}");
            }
            finally
            {
                m_IsWaitingForResponse = false;
                m_SendBtn.SetEnabled(true);
                m_LoadingIndicator.style.display = DisplayStyle.None;
                ScrollToBottom();
            }
        }

        private void AddMessageToUI(ChatRole role, string content)
        {
            var msgContainer = new VisualElement();
            msgContainer.style.marginBottom = 15;
            msgContainer.style.paddingLeft = 10;
            msgContainer.style.paddingRight = 10;
            msgContainer.style.paddingTop = 10;
            msgContainer.style.paddingBottom = 10;
            msgContainer.style.borderBottomLeftRadius = 8;
            msgContainer.style.borderBottomRightRadius = 8;
            msgContainer.style.borderTopLeftRadius = 8;
            msgContainer.style.borderTopRightRadius = 8;

            if (role == ChatRole.User)
            {
                msgContainer.style.backgroundColor = new Color(0.15f, 0.25f, 0.35f);
                msgContainer.style.marginLeft = 30; // indent from left
                
                var label = new Label(content);
                label.style.whiteSpace = WhiteSpace.Normal;
                label.style.color = new Color(0.9f, 0.9f, 0.9f);
                msgContainer.Add(label);
            }
            else if (role == ChatRole.Assistant)
            {
                msgContainer.style.backgroundColor = new Color(0.12f, 0.12f, 0.12f);
                msgContainer.style.marginRight = 30; // indent from right
                
                RenderContent(msgContainer, content);
            }

            m_MessageScroll.Add(msgContainer);
            ScrollToBottom();
        }

        private void RenderContent(VisualElement container, string content)
        {
            int currentIndex = 0;
            while (currentIndex < content.Length)
            {
                int replaceIdx = content.IndexOf("<replace", currentIndex);
                int createIdx = content.IndexOf("<create path=\"", currentIndex);
                
                int nextTagIdx = -1;
                string tagType = "";
                
                if (replaceIdx != -1 && (createIdx == -1 || replaceIdx < createIdx))
                {
                    nextTagIdx = replaceIdx;
                    tagType = "replace";
                }
                else if (createIdx != -1)
                {
                    nextTagIdx = createIdx;
                    tagType = "create";
                }

                if (nextTagIdx == -1)
                {
                    RenderMarkdown(container, content.Substring(currentIndex));
                    break;
                }

                if (nextTagIdx > currentIndex)
                {
                    RenderMarkdown(container, content.Substring(currentIndex, nextTagIdx - currentIndex));
                }

                if (tagType == "replace")
                {
                    int endReplaceIdx = content.IndexOf("</replace>", nextTagIdx);
                    if (endReplaceIdx == -1)
                    {
                        RenderMarkdown(container, content.Substring(nextTagIdx));
                        break;
                    }
                    string replaceBlock = content.Substring(nextTagIdx, endReplaceIdx + 10 - nextTagIdx);
                    RenderReplaceBlock(container, replaceBlock);
                    currentIndex = endReplaceIdx + 10;
                }
                else
                {
                    int endCreateIdx = content.IndexOf("</create>", nextTagIdx);
                    if (endCreateIdx == -1)
                    {
                        RenderMarkdown(container, content.Substring(nextTagIdx));
                        break;
                    }
                    string createBlock = content.Substring(nextTagIdx, endCreateIdx + 9 - nextTagIdx);
                    RenderCreateBlock(container, createBlock);
                    currentIndex = endCreateIdx + 9;
                }
            }
        }

        private void RenderCreateBlock(VisualElement container, string createBlock)
        {
            int pathStart = createBlock.IndexOf("path=\"") + 6;
            int pathEnd = createBlock.IndexOf("\"", pathStart);
            string path = "";
            if (pathStart >= 6 && pathEnd > pathStart)
            {
                path = createBlock.Substring(pathStart, pathEnd - pathStart);
            }
            string newCode = ExtractTag(createBlock, "<code>", "</code>").Trim('\n', '\r');

            var box = new VisualElement();
            box.style.backgroundColor = new Color(0.1f, 0.2f, 0.1f);
            box.style.marginTop = 5;
            box.style.marginBottom = 5;
            box.style.paddingLeft = 8;
            box.style.paddingRight = 8;
            box.style.paddingTop = 8;
            box.style.paddingBottom = 8;
            box.style.borderTopLeftRadius = 4;
            box.style.borderTopRightRadius = 4;
            box.style.borderBottomLeftRadius = 4;
            box.style.borderBottomRightRadius = 4;
            
            var title = new Label("Create File: " + path);
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.color = new Color(0.4f, 0.8f, 0.4f);
            box.Add(title);

            var codeScroll = new ScrollView(ScrollViewMode.Horizontal);
            var codeLabel = new Label(newCode);
            codeLabel.style.whiteSpace = WhiteSpace.Pre;
            codeLabel.style.color = new Color(0.85f, 0.85f, 0.85f);
            codeLabel.style.marginTop = 10;
            codeScroll.Add(codeLabel);
            box.Add(codeScroll);

            Button applyBtn = null;
            applyBtn = new Button(() => 
            { 
                OnCreateFileRequested?.Invoke(path, newCode); 
                applyBtn.text = "✓ Approved & Created";
                applyBtn.SetEnabled(false);
                applyBtn.style.backgroundColor = new Color(0.15f, 0.4f, 0.15f);
                applyBtn.style.color = new Color(0.7f, 0.95f, 0.7f);
            }) { text = "Create & Open File" };
            applyBtn.style.height = 25;
            applyBtn.style.marginTop = 10;
            applyBtn.style.backgroundColor = new Color(0.2f, 0.5f, 0.2f);
            applyBtn.style.color = Color.white;
            applyBtn.style.unityFontStyleAndWeight = FontStyle.Bold;
            box.Add(applyBtn);

            container.Add(box);
            
            // Note: Auto-apply for file creation could be dangerous, so it requires manual click.
        }

        private void RenderReplaceBlock(VisualElement container, string replaceBlock)
        {
            int pathStart = replaceBlock.IndexOf("path=\"");
            string path = "";
            if (pathStart != -1)
            {
                pathStart += 6;
                int pathEnd = replaceBlock.IndexOf("\"", pathStart);
                if (pathEnd > pathStart)
                {
                    path = replaceBlock.Substring(pathStart, pathEnd - pathStart);
                }
            }

            string searchCode = ExtractTag(replaceBlock, "<search>", "</search>").Trim('\n', '\r');
            string newCode = ExtractTag(replaceBlock, "<code>", "</code>").Trim('\n', '\r');

            var diffBox = new VisualElement();
            diffBox.style.backgroundColor = new Color(0.2f, 0.15f, 0.1f);
            diffBox.style.marginTop = 5;
            diffBox.style.marginBottom = 5;
            diffBox.style.paddingLeft = 8;
            diffBox.style.paddingRight = 8;
            diffBox.style.paddingTop = 8;
            diffBox.style.paddingBottom = 8;
            diffBox.style.borderTopLeftRadius = 4;
            diffBox.style.borderTopRightRadius = 4;
            diffBox.style.borderBottomLeftRadius = 4;
            diffBox.style.borderBottomRightRadius = 4;
            
            var title = new Label("Code Modification Proposed" + (string.IsNullOrEmpty(path) ? ":" : $" for {path}:"));
            title.style.whiteSpace = WhiteSpace.Normal;
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.color = new Color(0.9f, 0.7f, 0.3f);
            diffBox.Add(title);

            if (!string.IsNullOrWhiteSpace(searchCode))
            {
                var delBox = new VisualElement();
                delBox.style.backgroundColor = new Color(0.3f, 0.1f, 0.1f, 0.3f);
                delBox.style.borderLeftWidth = 3;
                delBox.style.borderLeftColor = new Color(0.8f, 0.2f, 0.2f);
                delBox.style.marginTop = 5;
                delBox.style.paddingLeft = 5;
                
                var delScroll = new ScrollView(ScrollViewMode.Horizontal);
                var delLabel = new Label(searchCode);
                delLabel.style.whiteSpace = WhiteSpace.Pre;
                delLabel.style.color = new Color(0.9f, 0.6f, 0.6f);
                delScroll.Add(delLabel);
                delBox.Add(delScroll);
                diffBox.Add(delBox);
            }

            if (!string.IsNullOrWhiteSpace(newCode))
            {
                var addBox = new VisualElement();
                addBox.style.backgroundColor = new Color(0.1f, 0.3f, 0.1f, 0.3f);
                addBox.style.borderLeftWidth = 3;
                addBox.style.borderLeftColor = new Color(0.2f, 0.8f, 0.2f);
                addBox.style.marginTop = 5;
                addBox.style.paddingLeft = 5;
                
                var addScroll = new ScrollView(ScrollViewMode.Horizontal);
                var addLabel = new Label(newCode);
                addLabel.style.whiteSpace = WhiteSpace.Pre;
                addLabel.style.color = new Color(0.6f, 0.9f, 0.6f);
                addScroll.Add(addLabel);
                addBox.Add(addScroll);
                diffBox.Add(addBox);
            }

            Button applyBtn = null;
            applyBtn = new Button(() => 
            { 
                OnApplyDiffRequested?.Invoke(path, searchCode, newCode, 
                    () => {
                        applyBtn.text = "✓ Approved & Applied";
                        applyBtn.style.backgroundColor = new Color(0.15f, 0.4f, 0.15f);
                        applyBtn.style.color = new Color(0.7f, 0.95f, 0.7f);
                        applyBtn.SetEnabled(false);
                    },
                    () => {
                        applyBtn.text = "Review";
                        applyBtn.style.backgroundColor = new Color(0.6f, 0.4f, 0.1f);
                        applyBtn.SetEnabled(true);
                    }
                ); 
            }) { text = "Review" };
            applyBtn.style.height = 25;
            applyBtn.style.marginTop = 10;
            applyBtn.style.backgroundColor = new Color(0.6f, 0.4f, 0.1f);
            applyBtn.style.color = Color.white;
            applyBtn.style.unityFontStyleAndWeight = FontStyle.Bold;
            diffBox.Add(applyBtn);

            container.Add(diffBox);

            if (FuzzyLogicLabs.FuzzyIDE.Editor.Core.CodeIDESettings.Current.AI.AutoApplyDiffs)
            {
                applyBtn.text = "Applied Automatically";
                applyBtn.style.backgroundColor = new Color(0.2f, 0.6f, 0.2f);
                applyBtn.SetEnabled(false);
                // Defer slightly to ensure UI is ready, though not strictly required
                EditorApplication.delayCall += () => {
                    OnApplyDiffRequested?.Invoke(path, searchCode, newCode, null, null);
                };
            }
        }

        private string ExtractTag(string text, string startTag, string endTag)
        {
            int start = text.IndexOf(startTag);
            if (start == -1) return "";
            start += startTag.Length;
            int end = text.IndexOf(endTag, start);
            if (end == -1) return "";
            return text.Substring(start, end - start);
        }

        private void RenderMarkdown(VisualElement container, string content)
        {
            // Simple markdown parser for code blocks
            string[] parts = content.Split(new[] { "```" }, StringSplitOptions.None);
            
            for (int i = 0; i < parts.Length; i++)
            {
                bool isCodeBlock = i % 2 != 0;
                
                if (isCodeBlock)
                {
                    string codeContent = parts[i];
                    // Skip the language identifier line (e.g. "csharp")
                    int firstNewline = codeContent.IndexOf('\n');
                    if (firstNewline != -1 && firstNewline < 15) // heuristic for language tag
                    {
                        codeContent = codeContent.Substring(firstNewline + 1);
                    }
                    
                    var codeBox = new VisualElement();
                    codeBox.style.backgroundColor = new Color(0.08f, 0.08f, 0.08f);
                    codeBox.style.marginTop = 5;
                    codeBox.style.marginBottom = 5;
                    codeBox.style.paddingLeft = 8;
                    codeBox.style.paddingRight = 8;
                    codeBox.style.paddingTop = 8;
                    codeBox.style.paddingBottom = 8;
                    codeBox.style.borderTopLeftRadius = 4;
                    codeBox.style.borderTopRightRadius = 4;
                    codeBox.style.borderBottomLeftRadius = 4;
                    codeBox.style.borderBottomRightRadius = 4;
                    
                    var codeLabel = new Label(codeContent);
                    codeLabel.style.whiteSpace = WhiteSpace.Pre; // Preserve whitespace
                    codeLabel.style.color = new Color(0.85f, 0.85f, 0.85f);
                    
                    var actionsRow = new VisualElement();
                    actionsRow.style.flexDirection = FlexDirection.Row;
                    actionsRow.style.justifyContent = Justify.FlexEnd;
                    actionsRow.style.marginTop = 5;

                    var copyBtn = new Button(() => { GUIUtility.systemCopyBuffer = codeContent; }) { text = "Copy" };
                    copyBtn.style.height = 20;
                    
                    Button applyBtn = null;
                    applyBtn = new Button(() => { 
                        OnApplyCodeRequested?.Invoke(codeContent); 
                        applyBtn.text = "✓ Applied";
                        applyBtn.SetEnabled(false);
                        applyBtn.style.backgroundColor = new Color(0.15f, 0.4f, 0.15f);
                        applyBtn.style.color = new Color(0.7f, 0.95f, 0.7f);
                    }) { text = "Apply to Active File" };
                    applyBtn.style.height = 20;
                    applyBtn.style.backgroundColor = new Color(0.2f, 0.5f, 0.3f);
                    applyBtn.style.color = Color.white;

                    actionsRow.Add(copyBtn);
                    actionsRow.Add(applyBtn);

                    var codeScroll = new ScrollView(ScrollViewMode.Horizontal);
                    codeScroll.Add(codeLabel);

                    codeBox.Add(codeScroll);
                    codeBox.Add(actionsRow);
                    container.Add(codeBox);
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(parts[i])) continue;
                    
                    string txt = parts[i];
                    txt = System.Text.RegularExpressions.Regex.Replace(txt, @"\*\*(.+?)\*\*", "<b>$1</b>");
                    txt = System.Text.RegularExpressions.Regex.Replace(txt, @"\*(.+?)\*", "<i>$1</i>");
                    txt = System.Text.RegularExpressions.Regex.Replace(txt, @"`(.+?)`", "<color=#80c0ff>$1</color>");
                    
                    var textLabel = new Label(txt);
                    textLabel.enableRichText = true;
                    textLabel.style.whiteSpace = WhiteSpace.Normal;
                    textLabel.style.color = new Color(0.9f, 0.9f, 0.9f);
                    textLabel.style.marginTop = 5;
                    textLabel.style.marginBottom = 5;
                    container.Add(textLabel);
                }
            }
        }

        private async void ScrollToBottom()
        {
            await Task.Delay(100); // Wait for UI layout update
            m_MessageScroll.scrollOffset = new Vector2(0, float.MaxValue);
        }
    }
}
