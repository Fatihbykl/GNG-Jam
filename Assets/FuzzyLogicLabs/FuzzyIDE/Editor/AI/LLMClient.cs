using System;
using System.Collections.Generic;
using System.Net.Http;

using System.Threading.Tasks;
using UnityEngine;
using FuzzyLogicLabs.FuzzyIDE.Editor.Core;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.AI
{
    public enum ChatRole
    {
        System,
        User,
        Assistant
    }

    [Serializable]
    public class ChatMessage
    {
        public ChatRole Role;
        public string Content;
    }

    public interface ILLMProvider
    {
        Task<string> SendMessageAsync(List<ChatMessage> messages, string model);
        Task<List<string>> GetAvailableModelsAsync();
    }

    public static class LLMClient
    {
        private static readonly HttpClient s_HttpClient = new HttpClient();

        public static ILLMProvider GetActiveProvider()
        {
            var settings = CodeIDESettings.Current.AI;
            switch (settings.ActiveProvider)
            {
                case AIProvider.OpenAI:
                    if (string.IsNullOrEmpty(settings.OpenAIKey))
                        throw new Exception("OpenAI API Key is missing. Please configure it in settings.");
                    return new OpenAIProvider(settings.OpenAIKey, s_HttpClient);

                case AIProvider.Gemini:
                    if (string.IsNullOrEmpty(settings.GeminiKey))
                        throw new Exception("Gemini API Key is missing. Please configure it in settings.");
                    return new GeminiProvider(settings.GeminiKey, s_HttpClient);

                case AIProvider.Claude:
                    if (string.IsNullOrEmpty(settings.ClaudeKey))
                        throw new Exception("Claude API Key is missing. Please configure it in settings.");
                    return new ClaudeProvider(settings.ClaudeKey, s_HttpClient);

                default:
                    throw new Exception("Unsupported AI Provider selected.");
            }
        }

        public static async Task<string> SendMessageAsync(List<ChatMessage> messages)
        {
            var provider = GetActiveProvider();
            var model = CodeIDESettings.Current.AI.ModelName;
            if (string.IsNullOrEmpty(model))
            {
                var settings = CodeIDESettings.Current.AI;
                model = settings.ActiveProvider switch
                {
                    AIProvider.OpenAI => "gpt-4o",
                    AIProvider.Gemini => "gemini-1.5-pro",
                    AIProvider.Claude => "claude-3-5-sonnet-20240620",
                    _ => "gpt-4o"
                };
            }
            return await provider.SendMessageAsync(messages, model);
        }

        public static async Task<List<string>> GetAvailableModelsAsync()
        {
            var provider = GetActiveProvider();
            return await provider.GetAvailableModelsAsync();
        }
    }
}
