using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace AgentOffice.Agent
{
/// <summary>Error raised for provider-level failures (non-2xx, network).</summary>
    internal sealed class LlmException : Exception
    {
        public readonly int StatusCode;

        public LlmException(int statusCode, string message)
            : base(message)
        {
            StatusCode = statusCode;
        }

/// <summary>Pre-stream failures worth one retry (opencode executor.ts policy): network/unreachable (0), 408, 409, 429 and 5xx.</summary>
        public static bool IsRetryable(int statusCode)
        {
            return statusCode == 0 || statusCode == 408 || statusCode == 409 ||
                statusCode == 429 || (statusCode >= 500 && statusCode < 600);
        }
    }

/// <summary>One function tool call requested by the model.</summary>
    internal sealed class LlmToolCall
    {
        public string Id;
        public string Name;
/// <summary>Raw JSON arguments string (concatenated from deltas).</summary>
        public string Arguments;
/// <summary>Stream index — defines the model's intended call order.</summary>
        public int Index;
/// <summary>Provider-specific metadata (such as Google Gemini's thought_signature / extra_content).</summary>
        public Dictionary<string, object> ExtraFields;
    }

/// <summary>Token accounting from the provider's final usage frame.</summary>
    internal sealed class LlmUsage
    {
        public int InputTokens;       // raw prompt_tokens (INCLUSIVE of cache reads)
        public int OutputTokens;      // raw completion_tokens (inclusive of reasoning)
        public int TotalTokens;
        public int ReasoningTokens;   // completion_tokens_details.reasoning_tokens
        public int CacheReadTokens;   // prompt_tokens_details.cached_tokens

/// <summary>Non-cached input (opencode normalization, clamped).</summary>
        public int AdjustedInput
        {
            get { return Math.Max(0, InputTokens - CacheReadTokens); }
        }

/// <summary>Visible reply tokens (completion minus thinking, clamped).</summary>
        public int VisibleOutput
        {
            get { return Math.Max(0, OutputTokens - ReasoningTokens); }
        }

/// <summary>Footer footprint: this request's full context pressure.</summary>
        public int Footprint
        {
            get { return InputTokens + VisibleOutput + ReasoningTokens; }
        }
    }

/// <summary>What a completed (or interrupted) provider stream produced.</summary>
    internal sealed class LlmStreamResult
    {
        public readonly StringBuilder Content = new StringBuilder();

/// <summary>Reasoning-model thinking (delta.reasoning_content / delta.reasoning), when the provider exposes it.</summary>
        public readonly StringBuilder Reasoning = new StringBuilder();
        public readonly List<LlmToolCall> ToolCalls = new List<LlmToolCall>();
        public readonly List<string> Images = new List<string>();
        public string FinishReason;
        public LlmUsage Usage;

        public string Text
        {
            get { return Content.ToString(); }
        }

        public string ReasoningText
        {
            get { return Reasoning.ToString(); }
        }
    }

/// <summary>Streaming callbacks — invoked on threadpool threads.</summary>
    internal interface ILlmStreamSink
    {
        void OnTextDelta(string delta);

/// <summary>Reasoning-model thinking chunk (optional per provider).</summary>
        void OnReasoningDelta(string delta);
    }

/// <summary>OpenAI-compatible streaming chat client — the single provider flavor Agent Office supports.</summary>
    internal sealed class LlmClient
    {
        private static readonly HttpClient Client = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression =
                System.Net.DecompressionMethods.GZip |
                System.Net.DecompressionMethods.Deflate,
        })
        {
            // Chat streams are long-lived; per-request cancellation is the
            // caller's concern (page aborts, add-in shutdown).
            Timeout = Timeout.InfiniteTimeSpan,
        };

        static LlmClient()
        {
            try
            {
                System.Net.ServicePointManager.SecurityProtocol |=
                    System.Net.SecurityProtocolType.Tls12 |
                    System.Net.SecurityProtocolType.Tls11 |
                    System.Net.SecurityProtocolType.Tls |
                    (System.Net.SecurityProtocolType)12288;
            }
            catch { }
        }

        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer
        {
            MaxJsonLength = 32 * 1024 * 1024,
        };

/// <summary>Stream one chat completion.</summary>
        public async Task<LlmStreamResult> StreamChatAsync(
            string model,
            IList<Dictionary<string, object>> messages,
            ILlmStreamSink sink,
            CancellationToken ct,
            ArrayList tools = null,
            string toolChoice = null,
            int maxTokens = 0,
            bool requestImageOutput = false)
        {
            var requestBody = new Dictionary<string, object>
            {
                ["model"] = model,
                ["messages"] = messages,
                ["stream"] = true,
                ["stream_options"] = new Dictionary<string, object>
                {
                    ["include_usage"] = true,
                },
            };
            if (maxTokens > 0)
                requestBody["max_tokens"] = maxTokens;
            if (tools != null && tools.Count > 0)
                requestBody["tools"] = tools;
            if (toolChoice != null)
                requestBody["tool_choice"] = toolChoice;
            if (requestImageOutput)
                requestBody["modalities"] = new ArrayList { "image", "text" };

            var url = ProviderSettings.BaseUrl + "/chat/completions";
            using (var request = new HttpRequestMessage(HttpMethod.Post, url))
            {
                var apiKey = ProviderSettings.ApiKey;
                if (!string.IsNullOrEmpty(apiKey))
                    request.Headers.Authorization =
                        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

                request.Headers.TryAddWithoutValidation("User-Agent", "AgentOffice/" + Bridge.Protocol.BridgeVersion);
                request.Headers.TryAddWithoutValidation("HTTP-Referer", "https://github.com/blazzbyte/agent-office");
                request.Headers.TryAddWithoutValidation("X-Title", "Agent Office");

                request.Content = new StringContent(
                    Json.Serialize(requestBody), Encoding.UTF8, "application/json");

                HttpResponseMessage response;
                try
                {
                    response = await Client.SendAsync(
                        request, HttpCompletionOption.ResponseHeadersRead, ct)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    throw new LlmException(0,
                        "Cannot reach the LLM provider at " + ProviderSettings.BaseUrl +
                        " — " + ex.Message);
                }

                using (response)
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        var errorBody = await ReadBodyAsync(response, ct).ConfigureAwait(false);
                        throw new LlmException(
                            (int)response.StatusCode, ExtractErrorMessage(errorBody));
                    }

                    var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                    using (stream)
                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        return await ParseSseAsync(reader, sink, ct).ConfigureAwait(false);
                    }
                }
            }
        }

        // ------------------------------------------------------------------
        // SSE parsing
        // ------------------------------------------------------------------

        private static async Task<LlmStreamResult> ParseSseAsync(
            StreamReader reader, ILlmStreamSink sink, CancellationToken ct)
        {
            var result = new LlmStreamResult();
            // Tool calls are reassembled by the `index` field of each delta —
            // fragments for one call may be interleaved with others.
            var toolBuilders = new Dictionary<int, ToolCallBuilder>();

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var line = await ReadLineAsync(reader, ct).ConfigureAwait(false);
                if (line == null) break; // EOF without [DONE] — tolerate.
                if (line.Length == 0) continue; // frame separator
                if (line.StartsWith(":")) continue; // keepalive comment
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;

                var data = line.Substring(5).Trim();
                if (data == "[DONE]") break;

                Dictionary<string, object> frame;
                try
                {
                    frame = Json.Deserialize<Dictionary<string, object>>(data);
                }
                catch
                {
                    continue; // malformed frame — skip, keep the stream alive
                }
                if (frame == null) continue;

                ReadUsage(frame, result);

                object choicesRaw;
                var choices = frame.TryGetValue("choices", out choicesRaw)
                    ? choicesRaw as ArrayList : null;
                if (choices == null || choices.Count == 0) continue;
                var choice = choices[0] as Dictionary<string, object>;
                if (choice == null) continue;

                object finishRaw;
                if (choice.TryGetValue("finish_reason", out finishRaw) &&
                    finishRaw is string)
                {
                    result.FinishReason = (string)finishRaw;
                }

                object deltaRaw;
                var delta = choice.TryGetValue("delta", out deltaRaw)
                    ? deltaRaw as Dictionary<string, object> : null;
                if (delta == null) continue;

                object contentRaw;
                if (delta.TryGetValue("content", out contentRaw) &&
                    contentRaw is string && ((string)contentRaw).Length > 0)
                {
                    result.Content.Append((string)contentRaw);
                    if (sink != null) sink.OnTextDelta((string)contentRaw);
                }

                // Reasoning models (DeepSeek-R1 style `reasoning_content`,
                // some providers use `reasoning`): thinking streams as plain
                // string deltas on the same channel.
                object reasoningRaw;
                if ((delta.TryGetValue("reasoning_content", out reasoningRaw) ||
                        delta.TryGetValue("reasoning", out reasoningRaw)) &&
                    reasoningRaw is string && ((string)reasoningRaw).Length > 0)
                {
                    result.Reasoning.Append((string)reasoningRaw);
                    if (sink != null) sink.OnReasoningDelta((string)reasoningRaw);
                }

                object toolCallsRaw;
                var toolCalls = delta.TryGetValue("tool_calls", out toolCallsRaw)
                    ? toolCallsRaw as ArrayList : null;
                if (toolCalls != null)
                {
                    for (var i = 0; i < toolCalls.Count; i++)
                    {
                        var entry = toolCalls[i] as Dictionary<string, object>;
                        if (entry != null)
                        {
                            AccumulateToolCall(entry, i, toolCalls.Count, delta, choice, toolBuilders);
                        }
                    }
                }

                // Image-output models (OpenRouter shape): generated images ride
                // delta.images as {type:"image_url", image_url:{url}} entries.
                ReadImages(delta, result);
            }

            var ordered = new List<KeyValuePair<int, ToolCallBuilder>>(toolBuilders);
            ordered.Sort((a, b) => a.Key.CompareTo(b.Key));
            foreach (var kvp in ordered)
            {
                result.ToolCalls.Add(kvp.Value.Build(kvp.Key));
            }
            if (result.ToolCalls.Count > 0 && result.FinishReason == "stop")
                result.FinishReason = "tool_calls";
            return result;
        }

        private sealed class ToolCallBuilder
        {
            public string Id;
            public string Name;
            public readonly StringBuilder Arguments = new StringBuilder();
            public readonly Dictionary<string, object> ExtraFields = new Dictionary<string, object>();

            public LlmToolCall Build(int index)
            {
                return new LlmToolCall
                {
                    Id = Id ?? string.Empty,
                    Name = Name ?? string.Empty,
                    Arguments = Arguments.Length > 0 ? Arguments.ToString() : "{}",
                    Index = index,
                    ExtraFields = ExtraFields.Count > 0 ? ExtraFields : null,
                };
            }
        }

        private static void AccumulateToolCall(
            Dictionary<string, object> entry,
            int arrayIndex,
            int arrayCount,
            Dictionary<string, object> delta,
            Dictionary<string, object> choice,
            Dictionary<int, ToolCallBuilder> builders)
        {
            if (entry == null) return;

            int? explicitIndex = null;
            object indexRaw;
            if (entry.TryGetValue("index", out indexRaw) && indexRaw != null)
            {
                if (indexRaw is int) explicitIndex = (int)indexRaw;
                else if (int.TryParse(indexRaw.ToString(), out var parsed)) explicitIndex = parsed;
            }

            int index;
            if (explicitIndex.HasValue)
            {
                index = explicitIndex.Value;
            }
            else
            {
                // Non-standard providers (e.g. Gemini OpenAI compatibility endpoint) might omit 'index'.
                string id = null;
                object idRaw;
                if (entry.TryGetValue("id", out idRaw) && idRaw is string && !string.IsNullOrEmpty((string)idRaw))
                {
                    id = (string)idRaw;
                }

                if (id != null)
                {
                    int foundIdx = -1;
                    foreach (var kvp in builders)
                    {
                        if (kvp.Value.Id == id)
                        {
                            foundIdx = kvp.Key;
                            break;
                        }
                    }
                    index = (foundIdx >= 0) ? foundIdx : builders.Count;
                }
                else if (arrayCount > 1 && arrayIndex < builders.Count)
                {
                    index = arrayIndex;
                }
                else if (builders.Count > 0)
                {
                    index = builders.Count - 1;
                }
                else
                {
                    index = 0;
                }
            }

            ToolCallBuilder builder;
            if (!builders.TryGetValue(index, out builder))
            {
                builder = new ToolCallBuilder();
                builders[index] = builder;
            }

            object newIdRaw;
            if (entry.TryGetValue("id", out newIdRaw) && newIdRaw is string && !string.IsNullOrEmpty((string)newIdRaw))
                builder.Id = (string)newIdRaw;

            // Preserve provider-specific extra fields (e.g. extra_content, thought_signature, google)
            foreach (var kvp in entry)
            {
                if (kvp.Key == "index" || kvp.Key == "id" || kvp.Key == "type" || kvp.Key == "function")
                    continue;
                builder.ExtraFields[kvp.Key] = kvp.Value;
            }

            object fnRaw;
            var fn = entry.TryGetValue("function", out fnRaw)
                ? fnRaw as Dictionary<string, object> : null;
            if (fn != null)
            {
                object nameRaw;
                if (fn.TryGetValue("name", out nameRaw) && nameRaw is string)
                    builder.Name = (string)nameRaw;
                object argsRaw;
                if (fn.TryGetValue("arguments", out argsRaw) && argsRaw is string)
                    builder.Arguments.Append((string)argsRaw);

                foreach (var kvp in fn)
                {
                    if (kvp.Key == "name" || kvp.Key == "arguments")
                        continue;
                    if (!builder.ExtraFields.ContainsKey(kvp.Key))
                        builder.ExtraFields[kvp.Key] = kvp.Value;
                }
            }

            // Fallback for delta/choice-level extra_content or thought_signature
            if (!builder.ExtraFields.ContainsKey("extra_content"))
            {
                object extra;
                if (delta != null && delta.TryGetValue("extra_content", out extra) && extra != null)
                    builder.ExtraFields["extra_content"] = extra;
                else if (choice != null && choice.TryGetValue("extra_content", out extra) && extra != null)
                    builder.ExtraFields["extra_content"] = extra;
            }
            if (!builder.ExtraFields.ContainsKey("thought_signature"))
            {
                object thought;
                if (delta != null && delta.TryGetValue("thought_signature", out thought) && thought != null)
                    builder.ExtraFields["thought_signature"] = thought;
                else if (choice != null && choice.TryGetValue("thought_signature", out thought) && thought != null)
                    builder.ExtraFields["thought_signature"] = thought;
            }
        }

/// <summary>Collect generated images from a streaming delta.</summary>
        private static void ReadImages(Dictionary<string, object> delta, LlmStreamResult result)
        {
            object imagesRaw;
            var images = delta.TryGetValue("images", out imagesRaw)
                ? imagesRaw as ArrayList : null;
            if (images == null) return;
            foreach (var item in images)
            {
                var dict = item as Dictionary<string, object>;
                if (dict == null) continue;
                string url = null;
                object urlRaw;
                if (dict.TryGetValue("url", out urlRaw)) url = urlRaw as string;
                if (url == null)
                {
                    object iuRaw;
                    var imageUrl = dict.TryGetValue("image_url", out iuRaw)
                        ? iuRaw as Dictionary<string, object> : null;
                    if (imageUrl != null && imageUrl.TryGetValue("url", out urlRaw))
                        url = urlRaw as string;
                }
                if (!string.IsNullOrEmpty(url) && !result.Images.Contains(url))
                    result.Images.Add(url);
            }
        }

        private static void ReadUsage(
            Dictionary<string, object> frame, LlmStreamResult result)
        {
            object usageRaw;
            var usage = frame.TryGetValue("usage", out usageRaw)
                ? usageRaw as Dictionary<string, object> : null;
            if (usage == null) return;

            result.Usage = new LlmUsage
            {
                InputTokens = GetInt(usage, "prompt_tokens"),
                OutputTokens = GetInt(usage, "completion_tokens"),
                TotalTokens = GetInt(usage, "total_tokens"),
                ReasoningTokens = GetDetailInt(usage, "completion_tokens_details", "reasoning_tokens"),
                CacheReadTokens = GetDetailInt(usage, "prompt_tokens_details", "cached_tokens"),
            };
        }

        private static int GetDetailInt(
            Dictionary<string, object> dict, string detailsKey, string key)
        {
            object raw;
            var details = dict.TryGetValue(detailsKey, out raw)
                ? raw as Dictionary<string, object> : null;
            return details == null ? 0 : GetInt(details, key);
        }

        private static int GetInt(Dictionary<string, object> dict, string key)
        {
            object raw;
            return dict.TryGetValue(key, out raw) && raw is int ? (int)raw : 0;
        }

/// <summary>ReadLineAsync with cancellation.</summary>
        private static async Task<string> ReadLineAsync(
            StreamReader reader, CancellationToken ct)
        {
            var readTask = reader.ReadLineAsync();
            var completed = await Task.WhenAny(readTask, Task.Delay(-1, ct))
                .ConfigureAwait(false);
            if (completed != readTask)
            {
                ct.ThrowIfCancellationRequested();
            }
            return await readTask.ConfigureAwait(false);
        }

        private static async Task<string> ReadBodyAsync(
            HttpResponseMessage response, CancellationToken ct)
        {
            try
            {
                return await response.Content
                    .ReadAsStringAsync().ConfigureAwait(false);
            }
            catch
            {
                return null;
            }
        }

        private static string FormatErrorMessage(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return raw;
            if (raw.IndexOf("Missing Authentication header", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "The provider rejected the request with 'Missing Authentication header'. " +
                       "Check that your API key is valid for this provider (OpenRouter keys start with 'sk-or-v1-').";
            }
            return raw;
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
                        return FormatErrorMessage((string)messageRaw);
                    }
                    object topRaw;
                    if (parsed != null &&
                        parsed.TryGetValue("message", out topRaw) &&
                        topRaw is string)
                    {
                        return FormatErrorMessage((string)topRaw);
                    }
                }
                catch { /* fall through to generic */ }
            }
            return "The LLM provider rejected the request" +
                (string.IsNullOrEmpty(body) ? "." : ": " + Truncate(body, 300));
        }

        private static string Truncate(string value, int max)
        {
            return value.Length <= max ? value : value.Substring(0, max) + "…";
        }
    }
}
