using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;

namespace AgentOffice.Bridge
{
/// <summary>Composition root of the native host: owns the UI-thread synchronizer and the action router.</summary>
    internal sealed class BridgeHost : IDisposable
    {
        private ExcelSynchronizer _sync;
        private ActionRouter _router;
        private Word.WordActionRouter _wordRouter;
        private PowerPoint.PowerPointActionRouter _powerPointRouter;
        private string _appVersion = "unknown";

/// <summary>The single per-process instance, set during Start().</summary>
        public static BridgeHost Current { get; private set; }

/// <summary>Advertised action names (for /v1/toolsets in AgentHost).</summary>
        internal string[] ActionNames
        {
            get
            {
                if (_router != null) return _router.ActionNames;
                if (_wordRouter != null) return _wordRouter.ActionNames;
                if (_powerPointRouter != null) return _powerPointRouter.ActionNames;
                return new string[0];
            }
        }

/// <summary>Execute one action for the in-proc agent (Phase 4 tool calling).</summary>
        internal Task<Dictionary<string, object>> ExecuteActionAsync(
            string action, Dictionary<string, object> args)
        {
            var req = new BridgeRequest
            {
                Id = "agent_" + Guid.NewGuid().ToString("N"),
                Action = action,
                Params = args ?? new Dictionary<string, object>(),
                WorkbookHint = null,
            };
            if (_router != null) return _router.ExecuteAsync(req);
            if (_wordRouter != null) return _wordRouter.ExecuteAsync(req);
            if (_powerPointRouter != null) return _powerPointRouter.ExecuteAsync(req);
            // No router for this host — should not happen (the agent is only
            // offered tools the host advertised), but guard anyway.
            return Task.FromResult(Protocol.Fail(req.Id,
                "This app has no tools yet — chat only.", "UNSUPPORTED_ACTION"));
        }

/// <summary>Must be called on Excel's main thread.</summary>
        public void Start()
        {
            try
            {
                // Application is late-bound (multi-host): cast per app.
                if (AppContext.Current == HostApp.Excel)
                {
                    _appVersion = ((Microsoft.Office.Interop.Excel.Application)
                        Globals.ThisAddIn.Application).Version;
                }
                else if (AppContext.Current == HostApp.Word)
                {
                    _appVersion = ((Microsoft.Office.Interop.Word.Application)
                        Globals.ThisAddIn.Application).Version;
                }
                else if (AppContext.Current == HostApp.PowerPoint)
                {
                    _appVersion = ((Microsoft.Office.Interop.PowerPoint.Application)
                        Globals.ThisAddIn.Application).Version;
                }
            }
            catch
            {
                // Non-fatal — health simply reports "unknown".
            }

            _sync = new ExcelSynchronizer();
            _sync.Initialize();

            if (AppContext.Current == HostApp.Word)
            {
                _wordRouter = new Word.WordActionRouter(_sync,
                    () => (Microsoft.Office.Interop.Word.Application)Globals.ThisAddIn.Application);
            }
            else if (AppContext.Current == HostApp.PowerPoint)
            {
                _powerPointRouter = new PowerPoint.PowerPointActionRouter(_sync,
                    () => (Microsoft.Office.Interop.PowerPoint.Application)Globals.ThisAddIn.Application);
            }
            else
            {
                _router = new ActionRouter(_sync,
                    () => (Microsoft.Office.Interop.Excel.Application)Globals.ThisAddIn.Application);
            }
            Current = this;

            Log.Info(
                "Agent Office host v" + Protocol.BridgeVersion +
                " ready in-process (pid " + Process.GetCurrentProcess().Id +
                ", " + AppContext.CurrentId + " " + _appVersion + ", " +
                ActionNames.Length + " actions)");
        }

        // ------------------------------------------------------------------
        // In-proc routing (taskpane interception)
        // ------------------------------------------------------------------

/// <summary>Answer a /bridge/* request in-process.</summary>
        public async Task<KeyValuePair<int, string>> HandleInProcAsync(
            string method, string path, string body)
        {
            try
            {
                if (method == "GET" && path == "/bridge/v1/health")
                {
                    return Json(200, BuildHealth());
                }

                if (method == "GET" && path == "/bridge/v1/token")
                {
                    // Protocol compatibility with the web client; enforcement
                    // is pointless in-proc, so always answer a fixed token.
                    return Json(200, new Dictionary<string, object>
                    {
                        ["token"] = "in-proc",
                    });
                }

                if (path == "/bridge/v1/execute")
                {
                    if (method != "POST")
                    {
                        return Json(405, Protocol.Fail("",
                            "Use POST for /bridge/v1/execute", "METHOD_NOT_ALLOWED"));
                    }

                    BridgeRequest req;
                    string parseError;
                    if (!Protocol.TryParseRequest(body ?? "", out req, out parseError))
                    {
                        return Json(400, Protocol.Fail("",
                            "Invalid request: " + parseError, "INVALID_JSON"));
                    }

                    var result = await ExecuteActionAsync(req.Action, req.Params)
                        .ConfigureAwait(false);
                    result["id"] = req.Id;
                    return Json(200, result);
                }

                return Json(404, Protocol.Fail("", "Unknown route: " + path, "NOT_FOUND"));
            }
            catch (Exception ex)
            {
                Log.Error("In-proc request handling failed: " + method + " " + path, ex);
                return Json(500, Protocol.Fail("",
                    "Bridge internal error: " + ex.Message, "INTERNAL"));
            }
        }

        private static KeyValuePair<int, string> Json(int status, object payload)
        {
            return new KeyValuePair<int, string>(status, Protocol.Serialize(payload));
        }

        private object BuildHealth()
        {
            return new Dictionary<string, object>
            {
                ["name"] = Protocol.BridgeName,
                ["version"] = Protocol.BridgeVersion,
                ["protocol"] = Protocol.ProtocolVersion,
                ["instance"] = Process.GetCurrentProcess().ProcessName + ":" +
                    Process.GetCurrentProcess().Id,
                ["app"] = AppContext.CurrentId,
                ["app_version"] = _appVersion,
                ["excel"] = _appVersion, // legacy key — kept for older UIs
                ["actions"] = ActionNames,
            };
        }

        // ------------------------------------------------------------------

        public void Dispose()
        {
            Current = null;

            try
            {
                _sync?.Dispose();
                _sync = null;
            }
            catch
            {
                // Ignore.
            }

            Log.Info("Agent Office host stopped (pid " + Process.GetCurrentProcess().Id + ")");
        }
    }
}
