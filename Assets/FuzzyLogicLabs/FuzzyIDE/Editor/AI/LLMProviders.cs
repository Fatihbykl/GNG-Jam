using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.AI
{
    public class OpenAIProvider : ILLMProvider
    {
        private readonly string _apiKey;
        private readonly HttpClient _http;

        public OpenAIProvider(string apiKey, HttpClient http)
        {
            _apiKey = apiKey;
            _http = http;
        }

        [Serializable]
        private class OpenAIMessage
        {
            public string role;
            public string content;
        }

        [Serializable]
        private class OpenAIRequest
        {
            public string model;
            public List<OpenAIMessage> messages;
            public float temperature;
        }

        [Serializable]
        private class OpenAIResponse
        {
            [Serializable]
            public class Choice
            {
                public OpenAIMessage message;
            }
            public List<Choice> choices;
            public string error_message; // custom fallback
        }

        public async Task<string> SendMessageAsync(List<ChatMessage> messages, string model)
        {
            var req = new OpenAIRequest
            {
                model = string.IsNullOrEmpty(model) ? "gpt-4o" : model,
                messages = new List<OpenAIMessage>(),
                temperature = 0.3f
            };

            foreach (var m in messages)
            {
                req.messages.Add(new OpenAIMessage
                {
                    role = m.Role.ToString().ToLower(),
                    content = m.Content
                });
            }

            string json = JsonUtility.ToJson(req);
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions");
            request.Headers.Add("Authorization", $"Bearer {_apiKey}");
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _http.SendAsync(request);
            var responseStr = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                throw new Exception($"OpenAI Error ({response.StatusCode}): {responseStr}");
            }

            var respData = JsonUtility.FromJson<OpenAIResponse>(responseStr);
            if (respData != null && respData.choices != null && respData.choices.Count > 0)
            {
                return respData.choices[0].message.content;
            }

            return "Error: Could not parse response.";
        }

        [Serializable]
        private class OpenAIModelList
        {
            [Serializable]
            public class ModelData
            {
                public string id;
            }
            public List<ModelData> data;
        }

        public async Task<List<string>> GetAvailableModelsAsync()
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.openai.com/v1/models");
                request.Headers.Add("Authorization", $"Bearer {_apiKey}");
                var response = await _http.SendAsync(request);
                if (response.IsSuccessStatusCode)
                {
                    var responseStr = await response.Content.ReadAsStringAsync();
                    var list = JsonUtility.FromJson<OpenAIModelList>(responseStr);
                    if (list != null && list.data != null)
                    {
                        var models = new List<string>();
                        foreach (var m in list.data)
                        {
                            if (m.id.Contains("gpt"))
                                models.Add(m.id);
                        }
                        models.Sort();
                        models.Reverse();
                        return models;
                    }
                }
            }
            catch { }
            return new List<string> { "gpt-4o", "gpt-4o-mini", "gpt-4-turbo", "gpt-4", "gpt-3.5-turbo" };
        }
    }

    public class GeminiProvider : ILLMProvider
    {
        private readonly string _apiKey;
        private readonly HttpClient _http;

        public GeminiProvider(string apiKey, HttpClient http)
        {
            _apiKey = apiKey;
            _http = http;
        }

        [Serializable]
        private class GeminiPart
        {
            public string text;
        }

        [Serializable]
        private class GeminiContent
        {
            public string role;
            public List<GeminiPart> parts;
        }

        [Serializable]
        private class GeminiRequest
        {
            public List<GeminiContent> contents;
            public GeminiContent system_instruction;
        }

        [Serializable]
        private class GeminiResponse
        {
            [Serializable]
            public class Candidate
            {
                public GeminiContent content;
            }
            public List<Candidate> candidates;
        }

        public async Task<string> SendMessageAsync(List<ChatMessage> messages, string model)
        {
            var req = new GeminiRequest
            {
                contents = new List<GeminiContent>()
            };

            foreach (var m in messages)
            {
                if (m.Role == ChatRole.System)
                {
                    req.system_instruction = new GeminiContent
                    {
                        role = "user", // Gemini system instruction role structure is slightly different, but this works for system_instruction
                        parts = new List<GeminiPart> { new GeminiPart { text = m.Content } }
                    };
                }
                else
                {
                    req.contents.Add(new GeminiContent
                    {
                        role = m.Role == ChatRole.Assistant ? "model" : "user",
                        parts = new List<GeminiPart> { new GeminiPart { text = m.Content } }
                    });
                }
            }

            string json = JsonUtility.ToJson(req);
            
            // Note: JsonUtility skips nulls, so system_instruction won't be serialized if null, which is good.
            // Wait, JsonUtility DOES NOT skip nulls. It serializes them as empty objects/strings.
            // Let's manually construct to avoid empty system_instruction breaking Gemini if not present.
            if (req.system_instruction == null || req.system_instruction.parts == null)
            {
                // To be safe, let's just use manual string building for the Gemini root payload.
            }
            
            // To ensure compatibility and since Gemini model endpoints include the model in URL:
            string modelName = string.IsNullOrEmpty(model) ? "gemini-1.5-pro" : model;
            string url = $"https://generativelanguage.googleapis.com/v1beta/models/{modelName}:generateContent?key={_apiKey}";

            var contentListJson = new List<string>();
            foreach (var c in req.contents)
            {
                contentListJson.Add($"{{\"role\":\"{c.role}\",\"parts\":[{{\"text\":\"{EscapeJson(c.parts[0].text)}\"}}]}}");
            }
            string contentsArr = "[" + string.Join(",", contentListJson) + "]";
            
            string finalJson = $"{{\"contents\":{contentsArr}";
            if (req.system_instruction != null && req.system_instruction.parts != null)
            {
                finalJson += $",\"system_instruction\":{{\"parts\":[{{\"text\":\"{EscapeJson(req.system_instruction.parts[0].text)}\"}}]}}";
            }
            finalJson += "}";

            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Content = new StringContent(finalJson, Encoding.UTF8, "application/json");

            var response = await _http.SendAsync(request);
            var responseStr = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                throw new Exception($"Gemini Error ({response.StatusCode}): {responseStr}");
            }

            var respData = JsonUtility.FromJson<GeminiResponse>(responseStr);
            if (respData != null && respData.candidates != null && respData.candidates.Count > 0)
            {
                return respData.candidates[0].content.parts[0].text;
            }

            return "Error: Could not parse Gemini response.";
        }
        
        private string EscapeJson(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            return text.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
        }

        [Serializable]
        private class GeminiModelList
        {
            [Serializable]
            public class ModelData
            {
                public string name;
            }
            public List<ModelData> models;
        }

        public async Task<List<string>> GetAvailableModelsAsync()
        {
            var defaultModels = new List<string> { "gemini-3.1-pro", "gemini-3.0-ultra", "gemini-2.5-flash", "gemini-1.5-pro", "gemini-1.5-flash" };
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, $"https://generativelanguage.googleapis.com/v1beta/models?key={_apiKey}");
                var response = await _http.SendAsync(request);
                if (response.IsSuccessStatusCode)
                {
                    var responseStr = await response.Content.ReadAsStringAsync();
                    var list = JsonUtility.FromJson<GeminiModelList>(responseStr);
                    if (list != null && list.models != null)
                    {
                        var models = new List<string>();
                        foreach (var m in list.models)
                        {
                            if (m.name.Contains("gemini"))
                            {
                                string cleanName = m.name.Replace("models/", "");
                                if (!models.Contains(cleanName)) models.Add(cleanName);
                            }
                        }
                        foreach (var def in defaultModels) {
                            if (!models.Contains(def)) models.Add(def);
                        }
                        models.Sort();
                        models.Reverse();
                        return models;
                    }
                }
                else
                {
                    Debug.LogError($"[FuzzyIDE] Gemini API Error: {response.StatusCode} - {await response.Content.ReadAsStringAsync()}");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[FuzzyIDE] Gemini Fetch Error: {ex.Message}");
            }
            return defaultModels;
        }
    }

    public class ClaudeProvider : ILLMProvider
    {
        private readonly string _apiKey;
        private readonly HttpClient _http;

        public ClaudeProvider(string apiKey, HttpClient http)
        {
            _apiKey = apiKey;
            _http = http;
        }

        [Serializable]
        private class ClaudeMessage
        {
            public string role;
            public string content;
        }

        [Serializable]
        private class ClaudeResponse
        {
            [Serializable]
            public class ContentPart
            {
                public string type;
                public string text;
            }
            public List<ContentPart> content;
        }

        public async Task<string> SendMessageAsync(List<ChatMessage> messages, string model)
        {
            string systemPrompt = "";
            var claudeMsgs = new List<string>();

            foreach (var m in messages)
            {
                if (m.Role == ChatRole.System)
                {
                    systemPrompt = EscapeJson(m.Content);
                }
                else
                {
                    string role = m.Role == ChatRole.Assistant ? "assistant" : "user";
                    claudeMsgs.Add($"{{\"role\":\"{role}\",\"content\":\"{EscapeJson(m.Content)}\"}}");
                }
            }

            string modelName = string.IsNullOrEmpty(model) ? "claude-3-5-sonnet-20240620" : model;
            string messagesArr = "[" + string.Join(",", claudeMsgs) + "]";
            
            string finalJson = $"{{\"model\":\"{modelName}\",\"max_tokens\":4096,\"messages\":{messagesArr}";
            if (!string.IsNullOrEmpty(systemPrompt))
            {
                finalJson += $",\"system\":\"{systemPrompt}\"";
            }
            finalJson += "}";

            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages");
            request.Headers.Add("x-api-key", _apiKey);
            request.Headers.Add("anthropic-version", "2023-06-01");
            request.Content = new StringContent(finalJson, Encoding.UTF8, "application/json");

            var response = await _http.SendAsync(request);
            var responseStr = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                throw new Exception($"Claude Error ({response.StatusCode}): {responseStr}");
            }

            var respData = JsonUtility.FromJson<ClaudeResponse>(responseStr);
            if (respData != null && respData.content != null && respData.content.Count > 0)
            {
                return respData.content[0].text;
            }

            return "Error: Could not parse Claude response.";
        }

        private string EscapeJson(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            return text.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
        }

        [Serializable]
        private class ClaudeModelList
        {
            [Serializable]
            public class ModelData
            {
                public string id;
            }
            public List<ModelData> data;
        }

        public async Task<List<string>> GetAvailableModelsAsync()
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/v1/models");
                request.Headers.Add("x-api-key", _apiKey);
                request.Headers.Add("anthropic-version", "2023-06-01");
                var response = await _http.SendAsync(request);
                if (response.IsSuccessStatusCode)
                {
                    var responseStr = await response.Content.ReadAsStringAsync();
                    var list = JsonUtility.FromJson<ClaudeModelList>(responseStr);
                    if (list != null && list.data != null)
                    {
                        var models = new List<string>();
                        foreach (var m in list.data)
                        {
                            models.Add(m.id);
                        }
                        models.Sort();
                        models.Reverse();
                        return models;
                    }
                }
            }
            catch { }
            return new List<string> { "claude-3-5-sonnet-20240620", "claude-3-opus-20240229", "claude-3-haiku-20240307" };
        }
    }
}
