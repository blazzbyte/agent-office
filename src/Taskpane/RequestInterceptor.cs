using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;

namespace AgentOffice.Taskpane
{
/// <summary>Intercepts every request on the virtual host (https://app.agentoffice.local) and answers it natively.</summary>
    internal sealed class RequestInterceptor
    {
        private const string JsonHeaders = "Content-Type: application/json; charset=utf-8\r\n";

        private static readonly Dictionary<string, string> MimeTypes =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [".html"] = "text/html; charset=utf-8",
                [".js"] = "application/javascript; charset=utf-8",
                [".css"] = "text/css; charset=utf-8",
                [".json"] = "application/json; charset=utf-8",
                [".png"] = "image/png",
                [".svg"] = "image/svg+xml",
                [".ico"] = "image/x-icon",
                [".woff"] = "font/woff",
                [".woff2"] = "font/woff2",
                [".map"] = "application/json; charset=utf-8",
            };

        private readonly string _uiDir;
        private readonly Control _marshaller; // WebView2 control: its thread = UI thread
        private CoreWebView2Environment _environment;

        public RequestInterceptor(string uiDir, Control marshaller)
        {
            _uiDir = Path.GetFullPath(uiDir);
            _marshaller = marshaller;
        }

        public void Attach(CoreWebView2 core)
        {
            _environment = core.Environment;
            var host = "https://" + AgentTaskpaneControl.VirtualHost;
            // Catch-all: every request on the virtual host goes through us.
            core.AddWebResourceRequestedFilter(host + "/*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += OnWebResourceRequested;
        }

/// <summary>A fully prepared answer, built on any thread.</summary>
        private sealed class Answered
        {
            public int Status;
            public string Reason;
            public string Headers;
            public Stream Body;
        }

        private enum Route { Static, Agent, Bridge }

        private async void OnWebResourceRequested(
            object sender, CoreWebView2WebResourceRequestedEventArgs e)
        {
            // ---- Snapshot phase: raising thread only (WebView2 objects) ----
            CoreWebView2Deferral deferral;
            string method, path;
            byte[] body = null;
            Route route;
            try
            {
                deferral = e.GetDeferral();
                method = e.Request.Method;
                path = new Uri(e.Request.Uri).PathAndQuery;

                if (path.StartsWith("/bridge/", StringComparison.OrdinalIgnoreCase))
                    route = Route.Bridge;
                else if (
                    path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) ||
                    path.StartsWith("/v1/", StringComparison.OrdinalIgnoreCase) ||
                    path.Equals("/health", StringComparison.OrdinalIgnoreCase))
                    route = Route.Agent;
                else
                    route = Route.Static;

                if (route != Route.Static)
                {
                    body = ReadBody(e.Request.Content);
                }
            }
            catch (Exception snapshotEx)
            {
                // If even the snapshot fails we cannot safely touch `e` again;
                // log and let the request die rather than crash Excel.
                Bridge.Log.Error("Interception snapshot failed", snapshotEx);
                return;
            }

            // ---- Work phase: results are plain .NET objects, thread-agnostic ----
            Answered answer;
            try
            {
                switch (route)
                {
                    case Route.Bridge:
                        answer = await ServeBridgeAsync(method, path, body)
                            .ConfigureAwait(false);
                        break;
                    case Route.Agent:
                        answer = await ServeAgentAsync(method, path, body)
                            .ConfigureAwait(false);
                        break;
                    default:
                        answer = ServeStatic(path);
                        break;
                }
            }
            catch (Exception ex)
            {
                Bridge.Log.Error("Interception failed for " + method + " " + path, ex);
                answer = new Answered
                {
                    Status = 502,
                    Reason = "Bad Gateway",
                    Headers = JsonHeaders,
                    Body = JsonStream("{\"summary\":\"Host proxy error: " +
                        Escape(ex.Message) + "\"}"),
                };
            }

            // ---- Completion phase: back on the UI thread ----
            Complete(e, deferral, answer);
        }

        // ------------------------------------------------------------------
        // Static UI files (+ SPA fallback)
        // ------------------------------------------------------------------

        private Answered ServeStatic(string pathAndQuery)
        {
            var pathOnly = pathAndQuery.Split('?')[0];
            var relative = (pathOnly ?? "/").TrimStart('/');
            if (relative.Length == 0) relative = "index.html";

            var fullPath = Path.GetFullPath(Path.Combine(_uiDir, relative));

            // Path traversal guard: resolved path must stay under the UI dir.
            if (!fullPath.StartsWith(_uiDir, StringComparison.OrdinalIgnoreCase))
            {
                return new Answered
                {
                    Status = 403, Reason = "Forbidden", Headers = JsonHeaders,
                    Body = JsonStream("{\"summary\":\"Forbidden\"}"),
                };
            }

            if (!File.Exists(fullPath))
            {
                // SPA fallback: extension-less paths serve the shell.
                var index = Path.Combine(_uiDir, "index.html");
                if (string.IsNullOrEmpty(Path.GetExtension(relative)) && File.Exists(index))
                {
                    fullPath = index;
                }
                else
                {
                    return new Answered
                    {
                        Status = 404, Reason = "Not Found", Headers = JsonHeaders,
                        Body = JsonStream("{\"summary\":\"Not found\"}"),
                    };
                }
            }

            var ext = Path.GetExtension(fullPath);
            string mime;
            if (!MimeTypes.TryGetValue(ext, out mime))
                mime = "application/octet-stream";

            return new Answered
            {
                Status = 200,
                Reason = "OK",
                Headers = "Content-Type: " + mime + "\r\n",
                Body = new MemoryStream(File.ReadAllBytes(fullPath)),
            };
        }

        // ------------------------------------------------------------------
        // /bridge/* → in-process bridge
        // ------------------------------------------------------------------

        private static async System.Threading.Tasks.Task<Answered> ServeBridgeAsync(
            string method, string path, byte[] body)
        {
            var host = Bridge.BridgeHost.Current;
            if (host == null)
            {
                return new Answered
                {
                    Status = 503, Reason = "Service Unavailable", Headers = JsonHeaders,
                    Body = JsonStream(
                        "{\"summary\":\"Host add-in not initialized\",\"code\":\"NO_HOST\"}"),
                };
            }

            var bodyText = body == null ? null : Encoding.UTF8.GetString(body);
            var result = await host.HandleInProcAsync(method, path, bodyText)
                .ConfigureAwait(false);

            return new Answered
            {
                Status = result.Key,
                Reason = result.Key == 200 ? "OK" : "Error",
                Headers = JsonHeaders,
                Body = JsonStream(result.Value),
            };
        }

        // ------------------------------------------------------------------
        // /api/*, /v1/*, /health → in-proc agent
        // ------------------------------------------------------------------

        private static async System.Threading.Tasks.Task<Answered> ServeAgentAsync(
            string method, string path, byte[] body)
        {
            var host = Agent.AgentHost.Current;
            if (host == null)
            {
                return new Answered
                {
                    Status = 503, Reason = "Service Unavailable", Headers = JsonHeaders,
                    Body = JsonStream(
                        "{\"message\":\"Agent host not initialized\",\"error\":\"no_host\"}"),
                };
            }

            var bodyText = body == null ? null : Encoding.UTF8.GetString(body);
            var result = await host.HandleInProcAsync(method, path, bodyText)
                .ConfigureAwait(false);

            return new Answered
            {
                Status = result.Key,
                Reason = result.Key >= 200 && result.Key < 300 ? "OK" : "Error",
                Headers = JsonHeaders,
                Body = JsonStream(result.Value),
            };
        }

        // ------------------------------------------------------------------
        // Completion on the UI thread
        // ------------------------------------------------------------------

        private void Complete(
            CoreWebView2WebResourceRequestedEventArgs e,
            CoreWebView2Deferral deferral,
            Answered answer)
        {
            Action finish = () =>
            {
                try
                {
                    e.Response = _environment.CreateWebResourceResponse(
                        answer.Body, answer.Status, answer.Reason, answer.Headers);
                }
                catch (Exception ex)
                {
                    Bridge.Log.Error("Failed to set intercepted response", ex);
                }
                finally
                {
                    try { deferral.Complete(); }
                    catch { /* shutdown */ }
                }
            };

            try
            {
                if (_marshaller.IsDisposed || !_marshaller.IsHandleCreated)
                {
                    // Taskpane is gone — nothing sensible left to do.
                    try { answer.Body.Dispose(); } catch { /* ignore */ }
                    return;
                }
                if (_marshaller.InvokeRequired)
                    _marshaller.Invoke(finish);
                else
                    finish();
            }
            catch (Exception ex)
            {
                Bridge.Log.Error("Failed to complete intercepted response", ex);
                try { answer.Body.Dispose(); } catch { /* ignore */ }
            }
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        private static byte[] ReadBody(Stream content)
        {
            if (content == null) return null;
            using (var ms = new MemoryStream())
            {
                content.CopyTo(ms);
                return ms.ToArray();
            }
        }

        private static MemoryStream JsonStream(string json)
        {
            return new MemoryStream(Encoding.UTF8.GetBytes(json));
        }

        private static string Escape(string value)
        {
            return (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }
}
