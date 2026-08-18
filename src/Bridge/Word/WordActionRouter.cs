using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Interop = Microsoft.Office.Interop.Word;

namespace AgentOffice.Bridge.Word
{
/// <summary>Word counterpart of : dispatches the Fase 9 toolset against the live Word COM object model.</summary>
    internal sealed class WordActionRouter
    {
        private static readonly TimeSpan ActionTimeout = TimeSpan.FromSeconds(60);

        private readonly ExcelSynchronizer _sync;
        private readonly Func<Interop.Application> _getApp;

        public WordActionRouter(ExcelSynchronizer sync, Func<Interop.Application> getApp)
        {
            _sync = sync;
            _getApp = getApp;
        }

/// <summary>Advertised in /v1/toolsets and /bridge/v1/health.</summary>
        public string[] ActionNames
        {
            get
            {
                return new[]
                {
                    "read_document_snapshot", "read_text", "insert_text",
                    "replace_text", "apply_style", "insert_table", "export_pdf",
                };
            }
        }

/// <summary>Execute a bridge request; always resolves to a StepResult-shaped dictionary.</summary>
        public async Task<Dictionary<string, object>> ExecuteAsync(BridgeRequest req)
        {
            try
            {
                return await _sync.RunAsync(() => Dispatch(req), ActionTimeout).ConfigureAwait(false);
            }
            catch (BridgeException bex)
            {
                return Protocol.Fail(req.Id, bex.Message, bex.Code);
            }
            catch (Exception ex)
            {
                Log.Error("WordActionRouter: unexpected failure for action '" + req.Action + "'", ex);
                return Protocol.Fail(req.Id, "Bridge internal error: " + ex.Message, "INTERNAL");
            }
        }

        // ------------------------------------------------------------------
        // Dispatch (runs on Word's UI thread)
        // ------------------------------------------------------------------

        private Dictionary<string, object> Dispatch(BridgeRequest req)
        {
            switch (req.Action)
            {
                case "read_document_snapshot":
                    return Guarded(req, WordActions.ReadDocumentSnapshot);
                case "read_text":
                    return Guarded(req, WordActions.ReadText);
                case "insert_text":
                    return Guarded(req, WordActions.InsertText);
                case "replace_text":
                    return Guarded(req, WordActions.ReplaceText);
                case "apply_style":
                    return Guarded(req, WordActions.ApplyStyle);
                case "insert_table":
                    return Guarded(req, WordActions.InsertTable);
                case "export_pdf":
                    return Guarded(req, WordActions.ExportPdf);
                default:
                    return Protocol.Fail(req.Id,
                        "Unsupported bridge action: '" + req.Action + "'", "UNSUPPORTED_ACTION");
            }
        }

/// <summary>Active-document guard + invoke — the shared prelude of every action.</summary>
        private Dictionary<string, object> Guarded(
            BridgeRequest req,
            Func<Interop.Application, BridgeRequest, Dictionary<string, object>> handler)
        {
            try
            {
                var app = _getApp();
                if (app.ActiveDocument == null)
                {
                    return Protocol.Fail(req.Id,
                        "No document is open in Word — open or create one first.",
                        "NO_DOCUMENT");
                }
                return handler(app, req);
            }
            catch (COMException com)
            {
                // Word throws COMException when no document window exists.
                return Protocol.Fail(req.Id,
                    "No document is open in Word — open or create one first. (" +
                    com.Message + ")", "NO_DOCUMENT");
            }
        }
    }
}
