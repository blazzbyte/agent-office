using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace AgentOffice.Taskpane
{
/// <summary>Taskpane content: the Agent Office chat UI (built React app) hosted in WebView2.</summary>
    public sealed class AgentTaskpaneControl : UserControl
    {
        internal const string VirtualHost = "app.agentoffice.local";

        private readonly WebView2 _webView;
        private readonly Label _fallback;
        private RequestInterceptor _interceptor;
        private StreamBridge _streamBridge;
        private bool _initialized;

        public AgentTaskpaneControl()
        {
            _webView = new WebView2 { Dock = DockStyle.Fill, Visible = false };
            _fallback = new Label
            {
                Dock = DockStyle.Fill,
                TextAlign = System.Drawing.ContentAlignment.MiddleCenter,
                Text = "Agent Office is starting…",
            };
            Controls.Add(_webView);
            Controls.Add(_fallback);
            _fallback.BringToFront();

            Load += AgentTaskpaneControl_Load;
        }

/// <summary>CoreWebView2 once initialized; null until then.</summary>
        public CoreWebView2 Core => _initialized ? _webView.CoreWebView2 : null;

        private async void AgentTaskpaneControl_Load(object sender, EventArgs e)
        {
            try
            {
                PreloadWebView2Loader();
                var baseDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "AgentOffice");
                var env = await CreateEnvironmentAsync(Path.Combine(baseDir, "WebView2"))
                    .ConfigureAwait(true);
                if (env == null)
                {
                    env = await CreateEnvironmentAsync(Path.Combine(
                        baseDir, "WebView2-pid" + Process.GetCurrentProcess().Id))
                        .ConfigureAwait(true);
                    if (env == null)
                        throw new InvalidOperationException(
                            "WebView2 could not start (shared profile locked and per-process fallback failed).");
                    Bridge.Log.Info("WebView2 shared profile in use by another Excel " +
                        "instance — this pane runs on a per-process profile " +
                        "(separate localStorage: re-enter the API key here).");
                }
                else
                {
                    // We own the shared profile → garbage-collect per-PID
                    // folders from dead processes (locked ones just skip).
                    CleanupStaleProcessProfiles(baseDir);
                }
                await _webView.EnsureCoreWebView2Async(env).ConfigureAwait(true);

                var uiDir = UiLocator.Find();
                if (!File.Exists(Path.Combine(uiDir, "index.html")))
                    throw new FileNotFoundException("Agent Office UI not found at " + uiDir);

                // NOTE: no SetVirtualHostNameToFolderMapping — requests to a
                // folder-mapped origin are resolved locally and never fire
                // WebResourceRequested. Instead the interceptor answers the
                // whole origin: static files from uiDir, /api/* + /v1/* +
                // /health → agent, /bridge/* → in-proc.
                _interceptor = new RequestInterceptor(uiDir, _webView);
                _interceptor.Attach(_webView.CoreWebView2);

                // SSE streaming over postMessage — interception buffers
                // long-lived responses, so the chat stream is proxied here.
                _streamBridge = new StreamBridge(_webView);
                _streamBridge.Attach(_webView.CoreWebView2);
                await _webView.CoreWebView2
                    .AddScriptToExecuteOnDocumentCreatedAsync(StreamBridge.InjectScript)
                    .ConfigureAwait(true);

                await _webView.CoreWebView2
                    .AddScriptToExecuteOnDocumentCreatedAsync(BuildHostInfoScript())
                    .ConfigureAwait(true);

                _webView.CoreWebView2.Navigate("https://" + VirtualHost + "/index.html");
                _webView.CoreWebView2.DocumentTitleChanged += (s, ev) =>
                {
                    if (_webView.CoreWebView2.DocumentTitle.Length > 0)
                    {
                        _webView.Visible = true;
                        _fallback.Visible = false;
                    }
                };

                _initialized = true;
            }
            catch (Exception ex)
            {
                Bridge.Log.Error("Agent Office taskpane failed to initialize", ex);
                _fallback.Text = "Agent Office failed to start.\r\nSee host.log for details.";
            }
        }

/// <summary>JSON payload for window.__agentOfficeHost.</summary>
        private static string BuildHostInfoScript()
        {
            string workbook = null;
            try
            {
                // Application is late-bound (multi-host): the workbook hint is
                // an Excel-only concept — other hosts leave it null.
                if (AppContext.Current == HostApp.Excel)
                {
                    var wb = ((Microsoft.Office.Interop.Excel.Application)
                        Globals.ThisAddIn.Application).ActiveWorkbook;
                    if (wb != null) workbook = wb.Name;
                }
            }
            catch
            {
                // No workbook open (e.g. Excel Start screen) — hint stays null;
                // the host skips the workbook-mismatch check in that case.
            }

            var version = Assembly.GetExecutingAssembly().GetName().Version.ToString();
            var workbookJson = workbook == null ? "null" : JsonString(workbook);
            return "window.__agentOfficeHost = { workbook: " + workbookJson +
                   ", version: " + JsonString(version) + " };";
        }

        private static string JsonString(string value)
        {
            return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

/// <summary>Try to create the WebView2 environment on the given user-data folder.</summary>
        private static async System.Threading.Tasks.Task<CoreWebView2Environment> CreateEnvironmentAsync(string udf)
        {
            try
            {
                Directory.CreateDirectory(udf);
                return await CoreWebView2Environment.CreateAsync(userDataFolder: udf)
                    .ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                Bridge.Log.Info("WebView2 profile unavailable (" + udf + "): " + ex.Message);
                return null;
            }
        }

/// <summary>Best-effort delete of WebView2-pid* profiles left by dead processes.</summary>
        private static void CleanupStaleProcessProfiles(string baseDir)
        {
            try
            {
                foreach (var dir in Directory.GetDirectories(baseDir, "WebView2-pid*"))
                {
                    try { Directory.Delete(dir, true); }
                    catch { /* locked (live process) or ACL — leave it */ }
                }
            }
            catch
            {
                // Best effort only.
            }
        }

/// <summary>Pre-load the native WebView2Loader.dll from our own folder.</summary>
        private static void PreloadWebView2Loader()
        {
            var arch = Environment.Is64BitProcess ? "win-x64" : "win-x86";
            var path = Path.Combine(
                UiLocator.AddinDirectory(), "runtimes", arch, "native", "WebView2Loader.dll");
            if (File.Exists(path))
                LoadLibrary(path);
        }

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibrary(string lpFileName);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try
                {
                    _streamBridge?.CancelAll();
                    _webView?.Dispose();
                    _fallback?.Dispose();
                }
                catch
                {
                    // Shutdown — swallow.
                }
            }
            base.Dispose(disposing);
        }
    }
}
