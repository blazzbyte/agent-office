using System;
using System.Collections;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace AgentOffice.Agent
{
/// <summary>Image generation for the generate_image tool — one non-streaming call against the configured IMAGE provider.</summary>
    internal static class ImageGeneration
    {
/// <summary>Image jobs are slow (local SD especially) — 5 min cap.</summary>
        private static readonly HttpClient Client = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression =
                System.Net.DecompressionMethods.GZip |
                System.Net.DecompressionMethods.Deflate,
        })
        {
            Timeout = TimeSpan.FromMinutes(5),
        };

        static ImageGeneration()
        {
            Client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "AgentOffice/" + Bridge.Protocol.BridgeVersion);
            Client.DefaultRequestHeaders.TryAddWithoutValidation("HTTP-Referer", "https://github.com/blazzbyte/agent-office");
            Client.DefaultRequestHeaders.TryAddWithoutValidation("X-Title", "Agent Office");
        }

        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer
        {
            MaxJsonLength = 32 * 1024 * 1024,
        };

/// <summary>Resolved provider id for the current settings (never null).</summary>
        public static string ResolveProvider()
        {
            var explicitProvider = ProviderSettings.ImageProvider;
            if (!string.IsNullOrEmpty(explicitProvider)) return explicitProvider;
            var baseUrl = ProviderSettings.ImageBaseUrl ?? string.Empty;
            if (baseUrl.IndexOf(
                    "generativelanguage.googleapis.com",
                    StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "gemini";
            }
            return "openai-images";
        }

/// <summary>Generate images for → data URLs.</summary>
        public static async Task<List<string>> GenerateAsync(
            string prompt, CancellationToken ct)
        {
            switch (ResolveProvider())
            {
                case "gemini":
                    return await GenerateGeminiAsync(prompt, ct).ConfigureAwait(false);
                case "a1111":
                    return await GenerateA1111Async(prompt, ct).ConfigureAwait(false);
                default:
                    return await GenerateOpenAiImagesAsync(prompt, ct).ConfigureAwait(false);
            }
        }

        // ------------------------------------------------------------------
        // openai-images: the standard Images API, with chat-modalities fallback
        // ------------------------------------------------------------------

        private static async Task<List<string>> GenerateOpenAiImagesAsync(
            string prompt, CancellationToken ct)
        {
            var baseUrl = ProviderSettings.ImageBaseUrl;
            // OpenRouter's dedicated Image API lives at /api/v1/images (same
            // response shape as OpenAI's /images/generations).
            var isOpenRouter = baseUrl.IndexOf(
                "openrouter.ai", StringComparison.OrdinalIgnoreCase) >= 0;
            var url = baseUrl + (isOpenRouter ? "/images" : "/images/generations");

            var requestBody = new Dictionary<string, object>
            {
                ["model"] = ProviderSettings.ImageModel,
                ["prompt"] = prompt,
                ["n"] = 1,
            };

            var response = await PostAsync(url, requestBody, useBearer: true, ct: ct)
                .ConfigureAwait(false);
            using (response)
            {
                var body = await ReadBodyAsync(response).ConfigureAwait(false);
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound && !isOpenRouter)
                {
                    // Gateway without an images endpoint — the previous (and
                    // still valid) OpenRouter/Gemini-style chat shape.
                    Bridge.Log.Info("ImageGeneration: no images endpoint at " +
                        baseUrl + " — falling back to chat-completions modalities");
                    return await GenerateChatModalitiesAsync(prompt, ct).ConfigureAwait(false);
                }
                if (!response.IsSuccessStatusCode)
                {
                    throw new LlmException(
                        (int)response.StatusCode, ExtractErrorMessage(body));
                }
                var images = ParseImagesApi(body);
                // Some deployments answer with URLs instead of b64 — fetch them.
                for (var i = 0; i < images.Count; i++)
                {
                    if (images[i].StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    {
                        images[i] = await DownloadAsDataUrlAsync(images[i], ct)
                            .ConfigureAwait(false);
                    }
                }
                images.RemoveAll(string.IsNullOrEmpty);
                return images;
            }
        }

/// <summary>Legacy gateway shape: chat/completions + modalities.</summary>
        private static async Task<List<string>> GenerateChatModalitiesAsync(
            string prompt, CancellationToken ct)
        {
            var url = ProviderSettings.ImageBaseUrl + "/chat/completions";
            var requestBody = new Dictionary<string, object>
            {
                ["model"] = ProviderSettings.ImageModel,
                ["messages"] = new ArrayList
                {
                    new Dictionary<string, object>
                    {
                        ["role"] = "user",
                        ["content"] = prompt,
                    },
                },
                ["modalities"] = new ArrayList { "image", "text" },
            };

            var response = await PostAsync(url, requestBody, useBearer: true, ct: ct)
                .ConfigureAwait(false);
            using (response)
            {
                var body = await ReadBodyAsync(response).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    throw new LlmException(
                        (int)response.StatusCode, ExtractErrorMessage(body));
                }
                return ParseImagesFromCompletion(body);
            }
        }

        // ------------------------------------------------------------------
        // gemini: native generateContent
        // ------------------------------------------------------------------

        private static async Task<List<string>> GenerateGeminiAsync(
            string prompt, CancellationToken ct)
        {
            var baseUrl = ProviderSettings.ImageBaseUrl;
            var url = baseUrl + "/models/" + ProviderSettings.ImageModel + ":generateContent";
            var requestBody = new Dictionary<string, object>
            {
                ["contents"] = new ArrayList
                {
                    new Dictionary<string, object>
                    {
                        ["parts"] = new ArrayList
                        {
                            new Dictionary<string, object> { ["text"] = prompt },
                        },
                    },
                },
                ["generationConfig"] = new Dictionary<string, object>
                {
                    ["responseModalities"] = new ArrayList { "IMAGE", "TEXT" },
                },
            };

            var response = await PostAsync(url, requestBody, useBearer: false, ct: ct)
                .ConfigureAwait(false);
            using (response)
            {
                var body = await ReadBodyAsync(response).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    throw new LlmException(
                        (int)response.StatusCode, ExtractErrorMessage(body));
                }
                return ParseImagesFromGemini(body);
            }
        }

        // ------------------------------------------------------------------
        // a1111: local Automatic1111 / SD.Next
        // ------------------------------------------------------------------

        private static async Task<List<string>> GenerateA1111Async(
            string prompt, CancellationToken ct)
        {
            var baseUrl = ProviderSettings.ImageBaseUrl;
            var url = baseUrl + "/sdapi/v1/txt2img";
            var requestBody = new Dictionary<string, object>
            {
                ["prompt"] = prompt,
                ["negative_prompt"] = string.Empty,
                ["steps"] = 20,
                ["width"] = 1024,
                ["height"] = 1024,
            };

            var response = await PostAsync(url, requestBody, useBearer: false, ct: ct)
                .ConfigureAwait(false);
            using (response)
            {
                var body = await ReadBodyAsync(response).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    throw new LlmException(
                        (int)response.StatusCode, ExtractErrorMessage(body));
                }

                var images = new List<string>();
                Dictionary<string, object> root;
                try
                {
                    root = Json.Deserialize<Dictionary<string, object>>(body);
                }
                catch
                {
                    return images;
                }
                object imagesRaw;
                var list = root != null && root.TryGetValue("images", out imagesRaw)
                    ? JsonCompat.ToArrayList(imagesRaw) : null;
                if (list == null) return images;
                foreach (var item in list)
                {
                    var b64 = item as string;
                    if (string.IsNullOrEmpty(b64)) continue;
                    images.Add("data:image/png;base64," + b64);
                }
                return images;
            }
        }

        // ------------------------------------------------------------------
        // Wire helpers
        // ------------------------------------------------------------------

        private static async Task<HttpResponseMessage> PostAsync(
            string url, Dictionary<string, object> requestBody, bool useBearer,
            CancellationToken ct)
        {
            using (var request = new HttpRequestMessage(HttpMethod.Post, url))
            {
                var apiKey = ProviderSettings.ImageApiKey;
                if (!string.IsNullOrEmpty(apiKey))
                {
                    if (useBearer)
                    {
                        request.Headers.Authorization =
                            new System.Net.Http.Headers.AuthenticationHeaderValue(
                                "Bearer", apiKey);
                    }
                    else
                    {
                        // Gemini native auth header.
                        request.Headers.TryAddWithoutValidation("x-goog-api-key", apiKey);
                    }
                }
                request.Content = new StringContent(
                    Json.Serialize(requestBody), Encoding.UTF8, "application/json");
                try
                {
                    return await Client.SendAsync(
                        request, HttpCompletionOption.ResponseContentRead, ct)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    throw new LlmException(0,
                        "Cannot reach the image provider at " + url + " — " + ex.Message);
                }
            }
        }

        private static async Task<string> ReadBodyAsync(HttpResponseMessage response)
        {
            try
            {
                return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            }
            catch
            {
                return null;
            }
        }

/// <summary>Download an http(s) image URL → data URL (null on failure).</summary>
        private static async Task<string> DownloadAsDataUrlAsync(
            string url, CancellationToken ct)
        {
            try
            {
                var bytes = await Client.GetByteArrayAsync(url).ConfigureAwait(false);
                if (bytes == null || bytes.Length == 0) return null;
                var mime = url.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                           url.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
                    ? "image/jpeg" : "image/png";
                return "data:" + mime + ";base64," + Convert.ToBase64String(bytes);
            }
            catch (Exception ex)
            {
                Bridge.Log.Info("ImageGeneration: image download failed — " + ex.Message);
                return null;
            }
        }

        // ------------------------------------------------------------------
        // Response parsers
        // ------------------------------------------------------------------

/// <summary>OpenAI Images shape: data[].{b64_json|url, media_type?}.</summary>
        private static List<string> ParseImagesApi(string body)
        {
            var images = new List<string>();
            Dictionary<string, object> root;
            try
            {
                root = Json.Deserialize<Dictionary<string, object>>(body);
            }
            catch
            {
                return images;
            }

            object dataRaw;
            var data = root != null && root.TryGetValue("data", out dataRaw)
                ? JsonCompat.ToArrayList(dataRaw) : null;
            if (data == null) return images;

            foreach (var itemRaw in data)
            {
                var item = itemRaw as Dictionary<string, object>;
                if (item == null) continue;
                object b64Raw;
                var b64 = item.TryGetValue("b64_json", out b64Raw)
                    ? b64Raw as string : null;
                if (!string.IsNullOrEmpty(b64))
                {
                    object mimeRaw;
                    var mime = item.TryGetValue("media_type", out mimeRaw)
                        ? mimeRaw as string : null;
                    images.Add("data:" + (mime ?? "image/png") + ";base64," + b64);
                    continue;
                }
                object urlRaw;
                var url = item.TryGetValue("url", out urlRaw) ? urlRaw as string : null;
                if (!string.IsNullOrEmpty(url)) images.Add(url); // fetched by caller
            }
            return images;
        }

/// <summary>Candidates[].content.parts[].inlineData{mimeType,data} → data URLs.</summary>
        private static List<string> ParseImagesFromGemini(string body)
        {
            var images = new List<string>();
            Dictionary<string, object> root;
            try
            {
                root = Json.Deserialize<Dictionary<string, object>>(body);
            }
            catch
            {
                return images;
            }

            object candidatesRaw;
            var candidates = root != null && root.TryGetValue("candidates", out candidatesRaw)
                ? JsonCompat.ToArrayList(candidatesRaw) : null;
            if (candidates == null) return images;

            foreach (var candidateRaw in candidates)
            {
                var candidate = candidateRaw as Dictionary<string, object>;
                object contentRaw, partsRaw;
                var content = candidate != null && candidate.TryGetValue("content", out contentRaw)
                    ? contentRaw as Dictionary<string, object> : null;
                var parts = content != null && content.TryGetValue("parts", out partsRaw)
                    ? JsonCompat.ToArrayList(partsRaw) : null;
                if (parts == null) continue;

                foreach (var partRaw in parts)
                {
                    var part = partRaw as Dictionary<string, object>;
                    object inlineRaw;
                    var inline = part != null && part.TryGetValue("inlineData", out inlineRaw)
                        ? inlineRaw as Dictionary<string, object> : null;
                    if (inline == null) continue;
                    object dataRaw, mimeRaw;
                    var data = inline.TryGetValue("data", out dataRaw) ? dataRaw as string : null;
                    var mime = inline.TryGetValue("mimeType", out mimeRaw)
                        ? mimeRaw as string : null;
                    if (string.IsNullOrEmpty(data)) continue;
                    var dataUrl = "data:" + (mime ?? "image/png") + ";base64," + data;
                    if (!images.Contains(dataUrl)) images.Add(dataUrl);
                }
            }
            return images;
        }

/// <summary>Choices[0].message.images[].image_url.url → data URLs.</summary>
        private static List<string> ParseImagesFromCompletion(string body)
        {
            var images = new List<string>();
            Dictionary<string, object> root;
            try
            {
                root = Json.Deserialize<Dictionary<string, object>>(body);
            }
            catch
            {
                return images;
            }

            object choicesRaw;
            var choices = root != null && root.TryGetValue("choices", out choicesRaw)
                ? JsonCompat.ToArrayList(choicesRaw) : null;
            if (choices == null || choices.Count == 0) return images;

            var first = choices[0] as Dictionary<string, object>;
            object messageRaw;
            var message = first != null && first.TryGetValue("message", out messageRaw)
                ? messageRaw as Dictionary<string, object> : null;
            if (message == null) return images;

            object imagesRaw;
            var list = message.TryGetValue("images", out imagesRaw)
                ? JsonCompat.ToArrayList(imagesRaw) : null;
            if (list == null) return images;

            foreach (var imageRaw in list)
            {
                var image = imageRaw as Dictionary<string, object>;
                object imageUrlRaw, urlRaw;
                var imageUrl = image != null && image.TryGetValue("image_url", out imageUrlRaw)
                    ? imageUrlRaw as Dictionary<string, object> : null;
                var url = imageUrl != null && imageUrl.TryGetValue("url", out urlRaw)
                    ? urlRaw as string : null;
                if (!string.IsNullOrEmpty(url) && !images.Contains(url))
                {
                    images.Add(url);
                }
            }
            return images;
        }

/// <summary>Pull "error.message" out of an OpenAI-shaped error body.</summary>
        private static string ExtractErrorMessage(string body)
        {
            if (!string.IsNullOrEmpty(body))
            {
                try
                {
                    var parsed = Json.Deserialize<Dictionary<string, object>>(body);
                    object errorRaw;
                    var error = parsed != null &&
                        parsed.TryGetValue("error", out errorRaw)
                            ? errorRaw as Dictionary<string, object> : null;
                    object messageRaw;
                    if (error != null &&
                        error.TryGetValue("message", out messageRaw) &&
                        messageRaw is string)
                    {
                        return (string)messageRaw;
                    }
                    object topRaw;
                    if (parsed != null &&
                        parsed.TryGetValue("message", out topRaw) &&
                        topRaw is string)
                    {
                        return (string)topRaw;
                    }
                }
                catch { /* fall through to generic */ }
            }
            return "The image provider rejected the request" +
                (string.IsNullOrEmpty(body)
                    ? "."
                    : ": " + (body.Length <= 300 ? body : body.Substring(0, 300) + "…"));
        }
    }
}
