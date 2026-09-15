using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace AgentOffice.Agent
{
/// <summary>Resolved metadata for one model (all prices USD per 1M tokens).</summary>
    internal sealed class ModelInfo
    {
        public string Id;
        public string Name;
        public int ContextWindow;       // 0 = unknown
        public int MaxOutput;           // 0 = unknown
        public double PriceInput;       // 0 = unknown/free
        public double PriceOutput;
        public double PriceCacheRead;
        public bool Reasoning;
/// <summary>Accepts image inputs (drives capture_range image omission).</summary>
        public bool Vision
        {
            get { return InputImage; }
            set { InputImage = value; }
        }
        // Input modalities beyond text (implied) — models.dev taxonomy
        // (text|audio|image|video|pdf), detected per provider shape.
        public bool InputImage;
        public bool InputVideo;
        public bool InputAudio;
        public bool InputPdf;
        // Output modalities beyond text (implied): image-generation models,
        // TTS. Drives "model replies with images" rendering support.
        public bool OutputImage;
        public bool OutputAudio;
/// <summary>Provider | models.dev | default — where the metadata came from.</summary>
        public string Source;

        private static List<string> ModalitiesList(bool image, bool video, bool audio, bool pdf)
        {
            var list = new List<string>(4);
            if (image) list.Add("image");
            if (video) list.Add("video");
            if (audio) list.Add("audio");
            if (pdf) list.Add("pdf");
            return list;
        }

        public Dictionary<string, object> ToApiJson()
        {
            return new Dictionary<string, object>
            {
                ["id"] = Id,
                ["name"] = Name ?? Id,
                ["context"] = ContextWindow,
                ["output"] = MaxOutput,
                ["price_input"] = PriceInput,
                ["price_output"] = PriceOutput,
                ["price_cache_read"] = PriceCacheRead,
                ["reasoning"] = Reasoning,
                ["vision"] = Vision,
                ["modalities_input"] = ModalitiesList(InputImage, InputVideo, InputAudio, InputPdf),
                ["modalities_output"] = ModalitiesList(OutputImage, false, OutputAudio, false),
                ["source"] = Source,
            };
        }
    }

/// <summary>Model catalog with tiered enrichment (research-backed — the plain OpenAI /models endpoint returns bare IDs on most.</summary>
    internal static class ModelRegistry
    {
        private const string ModelsDevUrl = "https://models.dev/api.json";
        private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(24);
        private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(12);

        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer
        {
            MaxJsonLength = 64 * 1024 * 1024, // the catalog is ~2-4 MB
        };

        private static readonly object _sync = new object();
        private static Dictionary<string, ProviderCatalog> _catalog; // providerId -> catalog
        private static bool _refreshStarted;

/// <summary>Models.dev providers keyed by their api baseUrl host.</summary>
        private static readonly Dictionary<string, string> HostToProvider =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["api.openai.com"] = "openai",
                ["api.deepseek.com"] = "deepseek",
                ["openrouter.ai"] = "openrouter",
                ["api.together.xyz"] = "togetherai",
                ["api.together.ai"] = "togetherai",
                ["api.groq.com"] = "groq",
                ["api.mistral.ai"] = "mistral",
                ["generativelanguage.googleapis.com"] = "google",
                ["api.x.ai"] = "xai",
                ["api.anthropic.com"] = "anthropic",
                ["api.cerebras.ai"] = "cerebras",
                ["api.fireworks.ai"] = "fireworks-ai",
            };

        private sealed class ProviderCatalog
        {
            public string ApiBase; // models.dev provider.api, when present
            public Dictionary<string, Dictionary<string, object>> Models;
        }

        // ==================================================================
        // Public surface
        // ==================================================================

/// <summary>Provider-native enrichment cache (keyed by model id, stamped with its baseUrl).</summary>
        private static readonly Dictionary<string, ModelInfo> _providerCache =
            new Dictionary<string, ModelInfo>(StringComparer.OrdinalIgnoreCase);
        private static string _providerCacheBaseUrl;

/// <summary>Kick a background refresh of the models.dev catalog (disk cache → network) AND of the provider-native cache (pricing!).</summary>
        public static void Warmup()
        {
            lock (_sync)
            {
                if (_refreshStarted) return;
                _refreshStarted = true;
            }
            Task.Run((Action)RefreshCatalog);
            Task.Run((Func<Task>)RefreshProviderCacheAsync);
        }

/// <summary>Background provider /models fetch that fills the pricing cache.</summary>
        public static async Task RefreshProviderCacheAsync()
        {
            if (!ProviderSettings.Configured) return;
            try
            {
                await ListProviderModelsAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Bridge.Log.Info(
                    "Provider /models cache refresh failed (step pricing may be " +
                    "unavailable until the picker connects): " + ex.Message);
            }
        }

/// <summary>Effective metadata for the currently configured model, with manual overrides and defaults applied.</summary>
        public static ModelInfo EffectiveModel()
        {
            return Resolve(ProviderSettings.Model, null);
        }

/// <summary>Resolve metadata for on the configured baseUrl; (from a /models fetch) wins over the catalog when both know a field.</summary>
        public static ModelInfo Resolve(
            string modelId, Dictionary<string, object> providerNative)
        {
            var info = new ModelInfo
            {
                Id = modelId ?? string.Empty,
                Name = modelId ?? string.Empty,
                Source = "default",
            };

            // Tier 1 — models.dev
            var catalogEntry = FindInCatalog(ProviderSettings.BaseUrl, modelId);
            if (catalogEntry != null)
            {
                info.Name = GetStr(catalogEntry, "name") ?? info.Name;
                info.ContextWindow = GetIntAt(catalogEntry, "limit", "context");
                info.MaxOutput = GetIntAt(catalogEntry, "limit", "output");
                info.PriceInput = GetDoubleAt(catalogEntry, "cost", "input");
                info.PriceOutput = GetDoubleAt(catalogEntry, "cost", "output");
                info.PriceCacheRead = GetDoubleAt(catalogEntry, "cost", "cache_read");
                info.Reasoning = GetBool(catalogEntry, "reasoning");
                var catModalities = GetDict(catalogEntry, "modalities");
                ApplyInputModalities(info, GetArray(catModalities, "input"));
                ApplyOutputModalities(info, GetArray(catModalities, "output"));
                info.Source = "models.dev";
            }

            // Tier 0 — provider-native wins per field when present
            if (providerNative != null)
            {
                if (ApplyProviderNative(info, providerNative))
                    info.Source = "provider";
            }
            ModelInfo cached = null;
            lock (_sync)
            {
                if (modelId != null &&
                    _providerCacheBaseUrl == ProviderSettings.BaseUrl)
                {
                    _providerCache.TryGetValue(modelId, out cached);
                }
            }
            if (cached != null)
            {
                info.Name = cached.Name;
                info.ContextWindow = cached.ContextWindow;
                info.MaxOutput = cached.MaxOutput;
                info.PriceInput = cached.PriceInput;
                info.PriceOutput = cached.PriceOutput;
                info.PriceCacheRead = cached.PriceCacheRead;
                info.Reasoning = cached.Reasoning;
                info.InputImage = cached.InputImage;
                info.InputVideo = cached.InputVideo;
                info.InputAudio = cached.InputAudio;
                info.InputPdf = cached.InputPdf;
                info.OutputImage = cached.OutputImage;
                info.OutputAudio = cached.OutputAudio;
                info.Source = cached.Source;
            }

            // Tier 2 — manual overrides win over everything
            if (ProviderSettings.HasExplicit("contextWindow"))
                info.ContextWindow = ProviderSettings.ContextWindow;
            if (ProviderSettings.HasExplicit("maxTokens"))
                info.MaxOutput = ProviderSettings.MaxTokens;

            // Defaults — never 0 (opencode pitfall: 0 disables context mgmt).
            if (info.ContextWindow <= 0) info.ContextWindow = ProviderSettings.DefaultContextWindow;
            return info;
        }

/// <summary>Fetch and enrich the model list of the configured provider (GET {baseUrl}/models).</summary>
        public static async Task<List<ModelInfo>> ListProviderModelsAsync()
        {
            var url = ProviderSettings.BaseUrl.TrimEnd('/') + "/models";
            string body;
            using (var client = CreateHttpClient())
            using (var request = new HttpRequestMessage(HttpMethod.Get, url))
            {
                var apiKey = ProviderSettings.ApiKey;
                if (!string.IsNullOrEmpty(apiKey))
                {
                    request.Headers.Authorization =
                        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
                }
                var response = await client.SendAsync(request).ConfigureAwait(false);
                body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    throw new LlmException(
                        (int)response.StatusCode,
                        "Provider /models failed (" + (int)response.StatusCode + "): " +
                            (body != null && body.Length > 200 ? body.Substring(0, 200) : body));
                }
            }

            var rawModels = ExtractModelEntries(body);
            if (rawModels.Count == 0)
            {
                var snippet = body == null
                    ? "null"
                    : body.Substring(0, Math.Min(160, body.Length));
                Bridge.Log.Info(
                    "Provider /models: WARNING — 0 entries parsed from " +
                    (body == null ? 0 : body.Length) + " bytes — body starts: " +
                    snippet.Replace("\n", "\\n"));
            }
            var result = new List<ModelInfo>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in rawModels)
            {
                var id = GetStr(entry, "id");
                if (string.IsNullOrEmpty(id) || !seen.Add(id)) continue;
                var info = Resolve(id, entry);
                result.Add(info);
            }
            result.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
            lock (_sync)
            {
                _providerCache.Clear();
                _providerCacheBaseUrl = ProviderSettings.BaseUrl;
                foreach (var info in result) _providerCache[info.Id] = info;
            }
            Bridge.Log.Info("Provider /models returned " + result.Count + " model(s)");
            return result;
        }

        // ==================================================================
        // Provider-native parsing (generic, per-field detection)
        // ==================================================================

/// <summary>Accepts both {object:"list", data:[...]} and bare arrays (Together).</summary>
        private static List<Dictionary<string, object>> ExtractModelEntries(string body)
        {
            var entries = new List<Dictionary<string, object>>();
            ArrayList data = null;
            try
            {
                var asDict = Json.Deserialize<Dictionary<string, object>>(body);
                object dataRaw;
                if (asDict != null && asDict.TryGetValue("data", out dataRaw))
                    data = JsonCompat.ToArrayList(dataRaw);
            }
            catch { /* not an object root — try the bare-array shape below */ }

            if (data == null)
            {
                object parsed;
                try { parsed = Json.Deserialize<object>(body); }
                catch { return entries; }
                data = JsonCompat.ToArrayList(parsed);
            }
            if (data == null) return entries;

            foreach (var item in data)
            {
                var dict = item as Dictionary<string, object>;
                if (dict != null) entries.Add(dict);
            }
            return entries;
        }

/// <summary>Pull known fields out of a provider /models entry.</summary>
        private static bool ApplyProviderNative(ModelInfo info, Dictionary<string, object> entry)
        {
            var rich = false;

            var name = GetStr(entry, "name") ?? GetStr(entry, "display_name");
            if (!string.IsNullOrEmpty(name)) { info.Name = name; }
            var context = GetInt(entry, "context_length");
            if (context <= 0) context = GetInt(entry, "context_window");
            if (context <= 0) context = GetInt(entry, "max_context_length");
            if (context <= 0) context = GetInt(entry, "context_size");
            if (context <= 0) context = GetIntAt(entry, "top_provider", "context_length");
            if (context > 0) { info.ContextWindow = context; rich = true; }

            // Max output: OpenRouter top_provider.max_completion_tokens,
            // Groq retrieve max_completion_tokens, Novita max_output_tokens.
            var output = GetIntAt(entry, "top_provider", "max_completion_tokens");
            if (output <= 0) output = GetInt(entry, "max_completion_tokens");
            if (output <= 0) output = GetInt(entry, "max_output_tokens");
            if (output > 0) { info.MaxOutput = output; rich = true; }

            // Pricing — OpenRouter: strings per TOKEN; Together: numbers per 1M;
            // xAI: numbers per token.
            var pricing = GetDict(entry, "pricing");
            if (pricing != null)
            {
                var prompt = GetDoubleLoose(pricing, "prompt");
                var completion = GetDoubleLoose(pricing, "completion");
                if (prompt <= 0) prompt = GetDoubleLoose(pricing, "input");       // Together per-1M
                if (completion <= 0) completion = GetDoubleLoose(pricing, "output");
                var cacheRead = GetDoubleLoose(pricing, "input_cache_read");
                if (cacheRead <= 0) cacheRead = GetDoubleLoose(pricing, "cached_input");

                var perToken = GetStr(entry, "canonical_slug") != null ||
                    GetDict(entry, "top_provider") != null; // OpenRouter signature
                var factor = perToken ? 1000000.0 : 1.0;
                if (prompt > 0) { info.PriceInput = prompt * factor; rich = true; }
                if (completion > 0) { info.PriceOutput = completion * factor; rich = true; }
                if (cacheRead > 0) { info.PriceCacheRead = cacheRead * factor; rich = true; }
                var novitaIn = GetDoubleAt(pricing, "prompt", "price_per_m");
                var novitaOut = GetDoubleAt(pricing, "completion", "price_per_m");
                var novitaCache = GetDoubleAt(pricing, "input_cache_read", "price_per_m");
                if (novitaIn > 0) { info.PriceInput = novitaIn * 0.0001; rich = true; }
                if (novitaOut > 0) { info.PriceOutput = novitaOut * 0.0001; rich = true; }
                if (novitaCache > 0) { info.PriceCacheRead = novitaCache * 0.0001; rich = true; }
            }

            // Novita flat mirrors of the same per-1M prices (1e-4-USD units).
            var flatIn = GetDoubleLoose(entry, "input_token_price_per_m");
            if (flatIn > 0) { info.PriceInput = flatIn * 0.0001; rich = true; }
            var flatOut = GetDoubleLoose(entry, "output_token_price_per_m");
            if (flatOut > 0) { info.PriceOutput = flatOut * 0.0001; rich = true; }

            var xaiPrompt = GetDoubleLoose(entry, "prompt_text_token_price");
            if (xaiPrompt > 0)
            {
                info.PriceInput = xaiPrompt * 1000000.0;
                var xaiOut = GetDoubleLoose(entry, "completion_text_token_price");
                if (xaiOut > 0) info.PriceOutput = xaiOut * 1000000.0;
                var xaiCache = GetDoubleLoose(entry, "cached_prompt_text_token_price");
                if (xaiCache > 0) info.PriceCacheRead = xaiCache * 1000000.0;
                rich = true;
            }

            // Reasoning: OpenRouter `reasoning` object or supported_parameters.
            if (GetDict(entry, "reasoning") != null) { info.Reasoning = true; rich = true; }
            var supported = GetArray(entry, "supported_parameters");
            if (supported != null)
            {
                foreach (var p in supported)
                {
                    var s = p as string;
                    if (s == "reasoning" || s == "include_reasoning" || s == "reasoning_effort")
                    {
                        info.Reasoning = true;
                        rich = true;
                        break;
                    }
                }
            }

            // Modalities: Novita top-level `input_modalities`/`output_modalities`,
            // OpenRouter `architecture.input_modalities`/`output_modalities`.
            var inputModalities = GetArray(entry, "input_modalities");
            if (inputModalities == null)
                inputModalities = GetArray(GetDict(entry, "architecture"), "input_modalities");
            if (ApplyInputModalities(info, inputModalities)) rich = true;
            var outputModalities = GetArray(entry, "output_modalities");
            if (outputModalities == null)
                outputModalities = GetArray(GetDict(entry, "architecture"), "output_modalities");
            if (ApplyOutputModalities(info, outputModalities)) rich = true;

            // Novita `features`: ["serverless","function-calling","reasoning",…]
            var features = GetArray(entry, "features");
            if (ArrayContainsStr(features, "reasoning")) { info.Reasoning = true; rich = true; }
            if (ArrayContainsStr(features, "vision")) { info.Vision = true; rich = true; }

            return rich;
        }

/// <summary>Set input modality flags from a modalities array.</summary>
        private static bool ApplyInputModalities(ModelInfo info, ArrayList modalities)
        {
            if (modalities == null) return false;
            if (ArrayContainsStr(modalities, "image")) info.InputImage = true;
            if (ArrayContainsStr(modalities, "video")) info.InputVideo = true;
            if (ArrayContainsStr(modalities, "audio")) info.InputAudio = true;
            if (ArrayContainsStr(modalities, "pdf")) info.InputPdf = true;
            return true;
        }

        private static bool ApplyOutputModalities(ModelInfo info, ArrayList modalities)
        {
            if (modalities == null) return false;
            if (ArrayContainsStr(modalities, "image")) info.OutputImage = true;
            if (ArrayContainsStr(modalities, "audio")) info.OutputAudio = true;
            return true;
        }

        // ==================================================================
        // models.dev catalog
        // ==================================================================

        private static string CachePath
        {
            get { return Path.Combine(Taskpane.HostSettings.DataDirectory, "models.json"); }
        }

/// <summary>HttpClient WITH automatic gzip/deflate decompression — net48's default handler leaves compressed bodies as raw bytes,</summary>
        private static HttpClient CreateHttpClient()
        {
            var handler = new HttpClientHandler
            {
                AutomaticDecompression =
                    System.Net.DecompressionMethods.GZip |
                    System.Net.DecompressionMethods.Deflate,
            };
            var client = new HttpClient(handler) { Timeout = FetchTimeout };
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "AgentOffice/" + Bridge.Protocol.BridgeVersion);
            client.DefaultRequestHeaders.TryAddWithoutValidation("HTTP-Referer", "https://github.com/blazzbyte/agent-office");
            client.DefaultRequestHeaders.TryAddWithoutValidation("X-Title", "Agent Office");
            return client;
        }

        private static void RefreshCatalog()
        {
            try
            {
                // Disk cache first (fresh enough → load and done).
                var path = CachePath;
                if (File.Exists(path) &&
                    DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < CacheTtl)
                {
                    TryLoadCatalog(File.ReadAllText(path));
                    return;
                }

                using (var client = CreateHttpClient())
                {
                    var body = client.GetStringAsync(ModelsDevUrl).Result;
                    if (TryLoadCatalog(body))
                    {
                        try { File.WriteAllText(path, body); }
                        catch (Exception ex) { Bridge.Log.Error("models.json cache write failed", ex); }
                    }
                }
            }
            catch (Exception ex)
            {
                // Offline etc. — try a stale cache as last resort.
                Bridge.Log.Error("models.dev refresh failed (catalog enrichment degraded)", ex);
                try
                {
                    var path = CachePath;
                    if (_catalog == null && File.Exists(path))
                        TryLoadCatalog(File.ReadAllText(path));
                }
                catch { /* stay uncatalogued — defaults apply */ }
            }
        }

        private static bool TryLoadCatalog(string json)
        {
            try
            {
                var root = Json.Deserialize<Dictionary<string, object>>(json);
                if (root == null) return false;

                var catalog = new Dictionary<string, ProviderCatalog>(StringComparer.Ordinal);
                foreach (var kvp in root)
                {
                    var provider = kvp.Value as Dictionary<string, object>;
                    if (provider == null) continue;
                    var modelsRaw = GetDict(provider, "models");
                    if (modelsRaw == null) continue;

                    var models = new Dictionary<string, Dictionary<string, object>>(
                        StringComparer.Ordinal);
                    foreach (var entry in modelsRaw)
                    {
                        var modelDict = entry.Value as Dictionary<string, object>;
                        if (modelDict != null) models[entry.Key] = modelDict;
                    }

                    catalog[kvp.Key] = new ProviderCatalog
                    {
                        ApiBase = GetStr(provider, "api"),
                        Models = models,
                    };
                }

                lock (_sync) { _catalog = catalog; }
                Bridge.Log.Info("Model catalog loaded (" + catalog.Count + " providers)");
                return true;
            }
            catch (Exception ex)
            {
                Bridge.Log.Error("models.dev parse failed", ex);
                return false;
            }
        }

        private static Dictionary<string, object> FindInCatalog(string baseUrl, string modelId)
        {
            if (string.IsNullOrEmpty(modelId)) return null;
            Dictionary<string, ProviderCatalog> catalog;
            lock (_sync) { catalog = _catalog; }
            if (catalog == null) return null;

            // 1. Host map / provider.api match → search that provider first.
            var providerId = ResolveProviderId(baseUrl, catalog);
            if (providerId != null)
            {
                Dictionary<string, object> hit;
                if (catalog[providerId].Models.TryGetValue(modelId, out hit)) return hit;
            }

            // 2. Global search by exact id (custom gateways serving known models).
            foreach (var provider in catalog.Values)
            {
                Dictionary<string, object> hit;
                if (provider.Models.TryGetValue(modelId, out hit)) return hit;
            }

            // 3. vendor/model ids (OpenRouter style): try the suffix.
            var slash = modelId.IndexOf('/');
            if (slash >= 0 && slash < modelId.Length - 1)
            {
                var suffix = modelId.Substring(slash + 1);
                foreach (var provider in catalog.Values)
                {
                    Dictionary<string, object> hit;
                    if (provider.Models.TryGetValue(suffix, out hit)) return hit;
                }
            }
            return null;
        }

        private static string ResolveProviderId(string baseUrl, Dictionary<string, ProviderCatalog> catalog)
        {
            string host = null;
            try { host = new Uri(baseUrl).Host; } catch { /* fall through */ }

            if (host != null)
            {
                string mapped;
                if (HostToProvider.TryGetValue(host, out mapped) && catalog.ContainsKey(mapped))
                    return mapped;
            }

            var trimmed = (baseUrl ?? string.Empty).TrimEnd('/');
            foreach (var kvp in catalog)
            {
                var api = kvp.Value.ApiBase;
                if (!string.IsNullOrEmpty(api) &&
                    (string.Equals(api.TrimEnd('/'), trimmed, StringComparison.OrdinalIgnoreCase) ||
                     (host != null && string.Equals(
                         SafeHost(api), host, StringComparison.OrdinalIgnoreCase))))
                {
                    return kvp.Key;
                }
            }
            return null;
        }

        private static string SafeHost(string url)
        {
            try { return new Uri(url).Host; } catch { return null; }
        }

        // ==================================================================
        // Typed getters over JavaScriptSerializer object graphs
        // ==================================================================

        internal static string GetStr(Dictionary<string, object> dict, string key)
        {
            object raw;
            return dict.TryGetValue(key, out raw) ? raw as string : null;
        }

        internal static int GetInt(Dictionary<string, object> dict, string key)
        {
            object raw;
            if (!dict.TryGetValue(key, out raw) || raw == null) return 0;
            if (raw is int) return (int)raw;
            if (raw is long) return (int)(long)raw;
            if (raw is double) return (int)(double)raw;
            return 0;
        }

        internal static int GetIntAt(Dictionary<string, object> dict, string key, string subKey)
        {
            var sub = GetDict(dict, key);
            return sub == null ? 0 : GetInt(sub, subKey);
        }

        internal static double GetDoubleAt(Dictionary<string, object> dict, string key, string subKey)
        {
            var sub = GetDict(dict, key);
            return sub == null ? 0 : GetDoubleLoose(sub, subKey);
        }

/// <summary>Numbers arrive as int/double, or strings (OpenRouter pricing).</summary>
        internal static double GetDoubleLoose(Dictionary<string, object> dict, string key)
        {
            object raw;
            if (!dict.TryGetValue(key, out raw) || raw == null) return 0;
            if (raw is double) return (double)raw;
            if (raw is int) return (int)raw;
            if (raw is long) return (long)raw;
            var s = raw as string;
            if (s != null)
            {
                double parsed;
                if (double.TryParse(s, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out parsed))
                {
                    return parsed;
                }
            }
            return 0;
        }

        internal static bool GetBool(Dictionary<string, object> dict, string key)
        {
            object raw;
            return dict.TryGetValue(key, out raw) && raw is bool && (bool)raw;
        }

        internal static Dictionary<string, object> GetDict(Dictionary<string, object> dict, string key)
        {
            object raw;
            return dict.TryGetValue(key, out raw) ? raw as Dictionary<string, object> : null;
        }

        internal static ArrayList GetArray(Dictionary<string, object> dict, string key)
        {
            object raw;
            if (dict == null) return null;
            return dict.TryGetValue(key, out raw) ? JsonCompat.ToArrayList(raw) : null;
        }

/// <summary>Case-insensitive membership test for string arrays (modalities).</summary>
        internal static bool ArrayContainsStr(ArrayList list, string value)
        {
            if (list == null || value == null) return false;
            foreach (var item in list)
            {
                var s = item as string;
                if (s != null && string.Equals(s, value, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
    }
}
