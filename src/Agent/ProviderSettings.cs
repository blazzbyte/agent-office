using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace AgentOffice.Agent
{
/// <summary>LLM provider configuration — the ONLY provider Agent Office speaks to: any OpenAI-compatible chat-completions endpoint.</summary>
    internal static class ProviderSettings
    {
        public const string DefaultBaseUrl = "https://api.openai.com/v1";
        public const int DefaultContextWindow = 128000;

/// <summary>0 = do NOT send max_tokens at all (the provider applies the model's own cap).</summary>
        public const int DefaultMaxTokens = 0;

/// <summary>Safety cap on tool-use steps per user turn.</summary>
        public const int DefaultMaxSteps = 50;
        public const int MaxStepsCeiling = 500;

        public static string ApiKey
        {
            get { return GetSecret("apiKey"); }
        }

        public static string BaseUrl
        {
            get
            {
                var url = GetString("baseUrl");
                return string.IsNullOrEmpty(url) ? DefaultBaseUrl : url.TrimEnd('/');
            }
        }

        public static string Model
        {
            get { return GetString("model"); }
        }

/// <summary>Optional image-generation model id for the generate_image tool.</summary>
        public static string ImageModel
        {
            get { return GetString("imageModel"); }
        }

/// <summary>Wire shape for image calls (settings.json "imageProvider").</summary>
        public static string ImageProvider
        {
            get { return GetString("imageProvider"); }
        }

        public static string ImageBaseUrl
        {
            get
            {
                var url = GetString("imageBaseUrl");
                return string.IsNullOrEmpty(url) ? BaseUrl : url.TrimEnd('/');
            }
        }

        public static string ImageApiKey
        {
            get
            {
                var key = GetSecret("imageApiKey");
                return string.IsNullOrEmpty(key) ? ApiKey : key;
            }
        }

/// <summary>True when generate_image can run.</summary>
        public static bool ImageConfigured
        {
            get
            {
                if (ImageGeneration.ResolveProvider() == "a1111")
                {
                    // Chat BaseUrl fallback would point txt2img at an LLM
                    // endpoint — require an explicit imageBaseUrl.
                    return !string.IsNullOrEmpty(GetString("imageBaseUrl"));
                }
                return !string.IsNullOrEmpty(ImageModel) &&
                       !string.IsNullOrEmpty(ImageApiKey);
            }
        }

        public static int ContextWindow
        {
            get { return GetInt("contextWindow", DefaultContextWindow); }
        }

        public static int MaxTokens
        {
            get { return GetInt("maxTokens", DefaultMaxTokens); }
        }

        public static int MaxSteps
        {
            get
            {
                var value = GetInt("maxSteps", DefaultMaxSteps);
                return value > MaxStepsCeiling ? MaxStepsCeiling : value;
            }
        }

/// <summary>True when the key exists explicitly in settings.json (vs.</summary>
        public static bool HasExplicit(string key)
        {
            var block = ReadBlock();
            return block != null && block.ContainsKey(key);
        }

/// <summary>True when the agent can actually call the provider.</summary>
        public static bool Configured
        {
            get { return !string.IsNullOrEmpty(ApiKey) && !string.IsNullOrEmpty(Model); }
        }

/// <summary>Merge a partial update into the "provider" block.</summary>
        public static void Save(
            string apiKey, string baseUrl, string model,
            int? contextWindow, int? maxTokens, int? maxSteps,
            string imageModel = null, string imageBaseUrl = null, string imageApiKey = null,
            string imageProvider = null)
        {
            Taskpane.HostSettings.SaveMutate(root =>
            {
                object raw;
                Dictionary<string, object> provider;
                if (root.TryGetValue("provider", out raw) &&
                    raw is Dictionary<string, object>)
                {
                    provider = (Dictionary<string, object>)raw;
                }
                else
                {
                    provider = new Dictionary<string, object>();
                    root["provider"] = provider;
                }

                if (apiKey != null)
                {
                    var clean = apiKey.Trim();
                    if (clean.StartsWith("Bearer ", System.StringComparison.OrdinalIgnoreCase))
                        clean = clean.Substring(7).Trim();
                    provider["apiKey"] = Protect(clean);
                }
                if (baseUrl != null) provider["baseUrl"] = baseUrl.Trim();
                if (model != null) provider["model"] = model.Trim();
                if (contextWindow.HasValue) provider["contextWindow"] = contextWindow.Value;
                if (maxTokens.HasValue) provider["maxTokens"] = maxTokens.Value;
                if (maxSteps.HasValue) provider["maxSteps"] = maxSteps.Value;
                // Empty string CLEARS the image settings (they're optional);
                // null leaves them untouched (partial update).
                if (imageModel != null) provider["imageModel"] = imageModel.Trim();
                if (imageBaseUrl != null) provider["imageBaseUrl"] = imageBaseUrl.Trim();
                if (imageApiKey != null) provider["imageApiKey"] = Protect(imageApiKey.Trim());
                if (imageProvider != null) provider["imageProvider"] = imageProvider.Trim();
            });
        }

/// <summary>API shape for GET /api/settings/provider.</summary>
        public static Dictionary<string, object> ToApiJson()
        {
            return new Dictionary<string, object>
            {
                ["apiKey"] = ApiKey ?? string.Empty,
                ["baseUrl"] = BaseUrl,
                ["model"] = Model ?? string.Empty,
                ["contextWindow"] = ContextWindow,
                ["maxTokens"] = MaxTokens,
                ["maxSteps"] = MaxSteps,
                ["imageModel"] = ImageModel ?? string.Empty,
                ["imageBaseUrl"] = GetString("imageBaseUrl") ?? string.Empty,
                ["imageApiKey"] = GetSecret("imageApiKey") ?? string.Empty,
                ["imageProvider"] = ImageGeneration.ResolveProvider(),
                ["imageConfigured"] = ImageConfigured,
                ["configured"] = Configured,
            };
        }

        // ------------------------------------------------------------------

        private static Dictionary<string, object> ReadBlock()
        {
            var root = Taskpane.HostSettings.LoadFresh();
            object raw;
            if (root != null &&
                root.TryGetValue("provider", out raw) &&
                raw is Dictionary<string, object>)
            {
                return (Dictionary<string, object>)raw;
            }
            return null;
        }

        private static string GetString(string key)
        {
            var block = ReadBlock();
            object raw;
            if (block != null &&
                block.TryGetValue(key, out raw) &&
                raw is string)
            {
                var value = ((string)raw).Trim();
                if (value.Length > 0) return value;
            }
            return null;
        }

/// <summary>Secret value: DPAPI-decrypted, or a legacy plain-text value (encrypted in place on first read).</summary>
        private static string GetSecret(string key)
        {
            var value = GetString(key);
            if (value == null) return null;
            if (value.StartsWith(SecretPrefix, StringComparison.Ordinal))
                return Unprotect(value.Substring(SecretPrefix.Length));
            MigrateSecret(key, value);
            return value;
        }

        private static void MigrateSecret(string key, string plain)
        {
            Taskpane.HostSettings.SaveMutate(root =>
            {
                object raw;
                if (!root.TryGetValue("provider", out raw)) return;
                var provider = raw as Dictionary<string, object>;
                if (provider == null || !provider.TryGetValue(key, out raw)) return;
                // Another process may have migrated or replaced it meanwhile.
                var current = raw as string;
                if (current != null && current.Trim() == plain)
                    provider[key] = Protect(plain);
            });
        }

        // ------------------------------------------------------------------
        // DPAPI (current Windows user): copying settings.json to another
        // account or machine does not expose the keys.
        // ------------------------------------------------------------------

        private const string SecretPrefix = "dpapi:";

        private static readonly byte[] Entropy =
            Encoding.UTF8.GetBytes("AgentOffice.provider.v1");

/// <summary>Encrypt a secret for settings.json; empty stays empty (clears the key).</summary>
        private static string Protect(string plain)
        {
            if (string.IsNullOrEmpty(plain)) return string.Empty;
            var cipher = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser);
            return SecretPrefix + Convert.ToBase64String(cipher);
        }

        private static string Unprotect(string base64)
        {
            try
            {
                var plain = ProtectedData.Unprotect(
                    Convert.FromBase64String(base64), Entropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(plain);
            }
            catch (Exception ex)
            {
                // Different user/machine or corrupted value: treat as unset.
                Bridge.Log.Error("Could not decrypt stored API key — re-enter it in Settings", ex);
                return null;
            }
        }

        private static int GetInt(string key, int fallback)
        {
            var block = ReadBlock();
            object raw;
            if (block != null && block.TryGetValue(key, out raw) && raw is int)
            {
                var value = (int)raw;
                if (value > 0) return value;
            }
            return fallback;
        }
    }
}
