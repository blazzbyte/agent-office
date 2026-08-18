using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Interop = Microsoft.Office.Interop.PowerPoint;

namespace AgentOffice.Bridge.PowerPoint
{
/// <summary>PowerPoint counterpart of : dispatches the Fase 10 toolset against the live PowerPoint COM object model.</summary>
    internal sealed class PowerPointActionRouter
    {
        private static readonly TimeSpan ActionTimeout = TimeSpan.FromSeconds(60);

        private readonly ExcelSynchronizer _sync;
        private readonly Func<Interop.Application> _getApp;

        public PowerPointActionRouter(ExcelSynchronizer sync, Func<Interop.Application> getApp)
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
                    "read_presentation_snapshot", "read_slide",
                    "add_slide", "duplicate_slide", "delete_slide",
                    "set_slide_text", "insert_textbox", "insert_shape",
                    "insert_table", "insert_picture", "list_themes",
                    "apply_theme", "set_speaker_notes", "export_pdf",
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
                Log.Error("PowerPointActionRouter: unexpected failure for action '" + req.Action + "'", ex);
                return Protocol.Fail(req.Id, "Bridge internal error: " + ex.Message, "INTERNAL");
            }
        }

        // ------------------------------------------------------------------
        // Dispatch (runs on PowerPoint's UI thread)
        // ------------------------------------------------------------------

        private Dictionary<string, object> Dispatch(BridgeRequest req)
        {
            switch (req.Action)
            {
                case "read_presentation_snapshot":
                    return Guarded(req, PowerPointActions.ReadPresentationSnapshot);
                case "read_slide":
                    return Guarded(req, PowerPointActions.ReadSlide);
                case "add_slide":
                    return Guarded(req, PowerPointActions.AddSlide);
                case "duplicate_slide":
                    return Guarded(req, PowerPointActions.DuplicateSlide);
                case "delete_slide":
                    return Guarded(req, PowerPointActions.DeleteSlide);
                case "set_slide_text":
                    return Guarded(req, PowerPointActions.SetSlideText);
                case "insert_textbox":
                    return Guarded(req, PowerPointActions.InsertTextbox);
                case "insert_shape":
                    return Guarded(req, PowerPointActions.InsertShape);
                case "insert_table":
                    return Guarded(req, PowerPointActions.InsertTable);
                case "insert_picture":
                    return Guarded(req, PowerPointActions.InsertPicture);
                case "list_themes":
                    return Guarded(req, PowerPointActions.ListThemes);
                case "apply_theme":
                    return Guarded(req, PowerPointActions.ApplyTheme);
                case "set_speaker_notes":
                    return Guarded(req, PowerPointActions.SetSpeakerNotes);
                case "export_pdf":
                    return Guarded(req, PowerPointActions.ExportPdf);
                default:
                    return Protocol.Fail(req.Id,
                        "Unsupported bridge action: '" + req.Action + "'", "UNSUPPORTED_ACTION");
            }
        }

/// <summary>Active-presentation guard + invoke — the shared prelude of every action.</summary>
        private Dictionary<string, object> Guarded(
            BridgeRequest req,
            Func<Interop.Application, BridgeRequest, Dictionary<string, object>> handler)
        {
            try
            {
                var app = _getApp();
                if (app.Presentations.Count == 0 || app.ActivePresentation == null)
                {
                    return Protocol.Fail(req.Id,
                        "No presentation is open in PowerPoint — open or create one first.",
                        "NO_PRESENTATION");
                }
                return handler(app, req);
            }
            catch (COMException com)
            {
                // PowerPoint throws COMException when no presentation window exists.
                return Protocol.Fail(req.Id,
                    "No presentation is open in PowerPoint — open or create one first. (" +
                    com.Message + ")", "NO_PRESENTATION");
            }
        }
    }
}
