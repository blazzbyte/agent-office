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
/// <summary>Openverse image search (api.openverse.org) — the zero-config image source for the search_image tool.</summary>
    internal static class ImageSearch
    {
        private const string ApiRoot = "https://api.openverse.org/v1/images/";
        private const int MaxDownloadBytes = 15 * 1024 * 1024; // same cap as UI attachments

        private static readonly HttpClient Client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30),
        };

        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer
        {
            MaxJsonLength = 8 * 1024 * 1024,
        };

        static ImageSearch()
        {
            // Openverse asks anonymous clients for a descriptive User-Agent.
            Client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "AgentOffice/" + Bridge.Protocol.BridgeVersion +
                " (https://github.com/agent-office)");
        }

        internal sealed class Hit
        {
            public string Url;         // full-size image
            public string Title;
            public string Creator;
            public string License;     // e.g. "cc-by-2.0"
            public string LicenseUrl;
            public string LandingUrl;  // source page (attribution link)
            public int Width;
            public int Height;

            internal string AttributionText()
            {
                var sb = new StringBuilder("Image: ");
                sb.Append(string.IsNullOrWhiteSpace(Title) ? "untitled" : Title);
                if (!string.IsNullOrWhiteSpace(Creator)) sb.Append(" by ").Append(Creator);
                if (!string.IsNullOrWhiteSpace(License))
                    sb.Append(" — ").Append(License.ToUpperInvariant());
                if (!string.IsNullOrWhiteSpace(LandingUrl)) sb.Append(" (").Append(LandingUrl).Append(")");
                return sb.ToString();
            }
        }

/// <summary>Search Openverse; returns up to hits.</summary>
        internal static async Task<List<Hit>> SearchAsync(
            string query, int count, CancellationToken ct)
        {
            var url = ApiRoot + "?q=" + Uri.EscapeDataString(query) +
                "&page_size=" + count + "&license_type=commercial";

            using (var request = new HttpRequestMessage(HttpMethod.Get, url))
            {
                HttpResponseMessage response;
                try
                {
                    response = await Client.SendAsync(request, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    throw new LlmException(0, "Cannot reach Openverse — " + ex.Message);
                }

                using (response)
                {
                    var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        throw new LlmException((int)response.StatusCode,
                            "Openverse search failed: HTTP " + (int)response.StatusCode);
                    }
                    return ParseHits(body);
                }
            }
        }

/// <summary>Download a hit's full image and return it as a data URL.</summary>
        internal static async Task<string> DownloadAsDataUrlAsync(Hit hit, CancellationToken ct)
        {
            using (var request = new HttpRequestMessage(HttpMethod.Get, hit.Url))
            {
                var response = await Client.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                using (response)
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        throw new LlmException((int)response.StatusCode,
                            "Image download failed: HTTP " + (int)response.StatusCode);
                    }
                    var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                    if (bytes.Length > MaxDownloadBytes)
                    {
                        throw new LlmException(0,
                            "Image too large (" + bytes.Length / 1024 / 1024 + " MB).");
                    }
                    var mime = response.Content.Headers.ContentType != null
                        ? response.Content.Headers.ContentType.MediaType
                        : "image/jpeg";
                    return "data:" + mime + ";base64," + Convert.ToBase64String(bytes);
                }
            }
        }

        // ------------------------------------------------------------------

        private static List<Hit> ParseHits(string body)
        {
            var hits = new List<Hit>();
            Dictionary<string, object> root;
            try
            {
                root = Json.Deserialize<Dictionary<string, object>>(body);
            }
            catch
            {
                return hits;
            }

            object resultsRaw;
            var results = root.TryGetValue("results", out resultsRaw)
                ? JsonCompat.ToArrayList(resultsRaw) : null;
            if (results == null) return hits;

            foreach (var item in results)
            {
                var dict = item as Dictionary<string, object>;
                if (dict == null) continue;
                var url = GetStr(dict, "url");
                if (string.IsNullOrEmpty(url)) continue;
                hits.Add(new Hit
                {
                    Url = url,
                    Title = GetStr(dict, "title"),
                    Creator = GetStr(dict, "creator"),
                    License = GetStr(dict, "license"),
                    LicenseUrl = GetStr(dict, "license_url"),
                    LandingUrl = GetStr(dict, "foreign_landing_url"),
                    Width = GetInt(dict, "width"),
                    Height = GetInt(dict, "height"),
                });
            }
            return hits;
        }

        private static string GetStr(Dictionary<string, object> dict, string key)
        {
            object raw;
            return dict.TryGetValue(key, out raw) ? raw as string : null;
        }

        private static int GetInt(Dictionary<string, object> dict, string key)
        {
            object raw;
            if (!dict.TryGetValue(key, out raw) || raw == null) return 0;
            if (raw is int) return (int)raw;
            if (raw is long) return (int)(long)raw;
            return 0;
        }
    }
}
