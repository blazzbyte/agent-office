using System;

namespace AgentOffice.Agent
{
/// <summary>Image-generation defaults derived from the chat endpoint, so the same account and key serve both.</summary>
    internal static class ImageDefaults
    {
/// <summary>Image model for a chat endpoint; null when the provider has none known.</summary>
        internal static string Model(string chatBaseUrl)
        {
            var url = chatBaseUrl ?? string.Empty;
            if (Contains(url, "generativelanguage.googleapis.com")) return "gemini-2.5-flash-image";
            if (Contains(url, "openrouter.ai")) return "google/gemini-2.5-flash-image";
            if (Contains(url, "api.openai.com")) return "gpt-image-1";
            return null;
        }

/// <summary>Image endpoint for a chat endpoint: Gemini's native API sits one level above its OpenAI-compatible path.</summary>
        internal static string BaseUrl(string chatBaseUrl)
        {
            var url = (chatBaseUrl ?? string.Empty).TrimEnd('/');
            if (Contains(url, "generativelanguage.googleapis.com") &&
                url.EndsWith("/openai", StringComparison.OrdinalIgnoreCase))
            {
                return url.Substring(0, url.Length - "/openai".Length);
            }
            return url;
        }

        private static bool Contains(string text, string value)
        {
            return text.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
