using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Excel = Microsoft.Office.Interop.Excel;

namespace AgentOffice.Bridge
{
/// <summary>Export actions: export_pdf { path?, sheet? }</summary>
    internal static class ExportActions
    {
        public static Dictionary<string, object> ExportPdf(Excel.Application app, BridgeRequest req)
        {
            var wb = app.ActiveWorkbook;
            if (wb == null)
            {
                return Protocol.Fail(req.Id, "No active workbook", "NO_ACTIVE_WORKBOOK");
            }

            // Explicit path > settings.json exportDir > Documents\AgentOffice
            // (also covers never-saved workbooks, which used to fail here).
            var path = ExportPath.Resolve(
                Protocol.GetString(req.Params, "path"),
                Path.GetFileNameWithoutExtension(wb.Name),
                ".pdf");

            string directory;
            try
            {
                directory = Path.GetDirectoryName(Path.GetFullPath(path));
            }
            catch (Exception)
            {
                return Protocol.Fail(req.Id, "Invalid path: " + path, "INVALID_PARAMS");
            }
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                return Protocol.Fail(req.Id, "Folder does not exist: " + directory, "INVALID_PARAMS");
            }

            // Optional single-sheet scope.
            Excel.Worksheet sheet = null;
            var sheetName = Protocol.GetString(req.Params, "sheet");
            if (!string.IsNullOrEmpty(sheetName))
            {
                Dictionary<string, object> error;
                if (!AnalysisActions.TryResolveSheet(app, sheetName, req.Id, out sheet, out error))
                {
                    return error;
                }
            }

            var alerts = app.DisplayAlerts;
            try
            {
                // Silent overwrite — a modal "replace file?" prompt would hang the UI thread.
                app.DisplayAlerts = false;
                if (sheet != null)
                {
                    sheet.ExportAsFixedFormat(Excel.XlFixedFormatType.xlTypePDF, path);
                }
                else
                {
                    wb.ExportAsFixedFormat(Excel.XlFixedFormatType.xlTypePDF, path);
                }
            }
            catch (COMException com)
            {
                return ActionRouter.MapComError(req.Id, com, "PDF export failed");
            }
            finally
            {
                try { app.DisplayAlerts = alerts; } catch { }
            }

            return Protocol.Ok(
                req.Id,
                "Exported " + (sheet != null ? "sheet '" + sheet.Name + "'" : "workbook") + " to PDF: " + path,
                new Dictionary<string, object>
                {
                    ["path"] = path,
                    ["scope"] = sheet != null ? "sheet" : "workbook",
                });
        }
        // ------------------------------------------------------------------
        // page_setup { sheet?, orientation?, printArea?, fitToPagesWide?,
        //              fitToPagesTall?, paperSize?, margins? (cm), printGridlines?,
        //              printHeadings?, centerHorizontally? }
        // ------------------------------------------------------------------

        public static Dictionary<string, object> PageSetup(Excel.Application app, BridgeRequest req)
        {
            Excel.Worksheet sheet;
            Dictionary<string, object> error;
            if (!AnalysisActions.TryResolveSheet(app, Protocol.GetString(req.Params, "sheet"), req.Id, out sheet, out error))
            {
                return error;
            }

            var applied = new List<string>();
            try
            {
                var ps = sheet.PageSetup;

                var orientation = Protocol.GetString(req.Params, "orientation");
                if (!string.IsNullOrWhiteSpace(orientation))
                {
                    switch (orientation.Trim().ToLowerInvariant())
                    {
                        case "landscape":
                            ps.Orientation = Excel.XlPageOrientation.xlLandscape;
                            applied.Add("orientation=landscape");
                            break;
                        case "portrait":
                            ps.Orientation = Excel.XlPageOrientation.xlPortrait;
                            applied.Add("orientation=portrait");
                            break;
                        default:
                            return Protocol.Fail(req.Id, "Invalid 'orientation': use \"portrait\" or \"landscape\"", "INVALID_PARAMS");
                    }
                }

                var printArea = Protocol.GetString(req.Params, "printArea");
                if (printArea != null)
                {
                    if (string.IsNullOrWhiteSpace(printArea))
                    {
                        ps.PrintArea = string.Empty; // clears the print area
                        applied.Add("printArea cleared");
                    }
                    else
                    {
                        // Normalize to absolute ($A$1:$F$50) — also validates the address.
                        var abs = sheet.Range[printArea]
                            .get_Address(true, true, Excel.XlReferenceStyle.xlA1);
                        ps.PrintArea = abs;
                        applied.Add("printArea=" + abs);
                    }
                }

                var fitWide = Protocol.GetDouble(req.Params, "fitToPagesWide");
                var fitTall = Protocol.GetDouble(req.Params, "fitToPagesTall");
                if (fitWide.HasValue || fitTall.HasValue)
                {
                    ps.Zoom = false; // required, or FitToPages* are ignored
                    if (fitWide.HasValue) { ps.FitToPagesWide = (int)fitWide.Value; applied.Add("fitToPagesWide=" + (int)fitWide.Value); }
                    if (fitTall.HasValue) { ps.FitToPagesTall = (int)fitTall.Value; applied.Add("fitToPagesTall=" + (int)fitTall.Value); }
                }

                var paperSize = Protocol.GetString(req.Params, "paperSize");
                if (!string.IsNullOrWhiteSpace(paperSize))
                {
                    switch (paperSize.Trim().ToLowerInvariant())
                    {
                        case "letter": ps.PaperSize = Excel.XlPaperSize.xlPaperLetter; break;
                        case "legal": ps.PaperSize = Excel.XlPaperSize.xlPaperLegal; break;
                        case "a4": ps.PaperSize = Excel.XlPaperSize.xlPaperA4; break;
                        case "a3": ps.PaperSize = Excel.XlPaperSize.xlPaperA3; break;
                        case "a5": ps.PaperSize = Excel.XlPaperSize.xlPaperA5; break;
                        default:
                            return Protocol.Fail(req.Id, "Invalid 'paperSize': use letter|legal|a4|a3|a5", "INVALID_PARAMS");
                    }
                    applied.Add("paperSize=" + paperSize.Trim().ToLowerInvariant());
                }

                object marginsParam;
                var margins = req.Params != null && req.Params.TryGetValue("margins", out marginsParam)
                    ? marginsParam as IDictionary<string, object>
                    : null;
                if (margins != null)
                {
                    applied.AddRange(ApplyMargins(app, ps, margins, req.Id));
                }

                if (req.Params != null && req.Params.ContainsKey("printGridlines"))
                {
                    var v = Protocol.GetBool(req.Params, "printGridlines");
                    ps.PrintGridlines = v;
                    applied.Add("printGridlines=" + v);
                }
                if (req.Params != null && req.Params.ContainsKey("printHeadings"))
                {
                    var v = Protocol.GetBool(req.Params, "printHeadings");
                    ps.PrintHeadings = v;
                    applied.Add("printHeadings=" + v);
                }
                if (req.Params != null && req.Params.ContainsKey("centerHorizontally"))
                {
                    var v = Protocol.GetBool(req.Params, "centerHorizontally");
                    ps.CenterHorizontally = v;
                    applied.Add("centerHorizontally=" + v);
                }
            }
            catch (COMException com)
            {
                return ActionRouter.MapComError(req.Id, com, "Page setup failed");
            }

            if (applied.Count == 0)
            {
                return Protocol.Fail(req.Id, "Nothing to do — pass at least one page-setup option", "INVALID_PARAMS");
            }

            return Protocol.Ok(
                req.Id,
                "Page setup on '" + sheet.Name + "': " + string.Join(", ", applied),
                new Dictionary<string, object>
                {
                    ["sheet"] = sheet.Name,
                    ["applied"] = applied,
                });
        }

/// <summary>Margins arrive in CENTIMETERS; PageSetup wants points.</summary>
        private static List<string> ApplyMargins(
            Excel.Application app, Excel.PageSetup ps, IDictionary<string, object> margins, string id)
        {
            var applied = new List<string>();
            var pairs = new[]
            {
                new KeyValuePair<string, Action<double>>("left", v => ps.LeftMargin = v),
                new KeyValuePair<string, Action<double>>("right", v => ps.RightMargin = v),
                new KeyValuePair<string, Action<double>>("top", v => ps.TopMargin = v),
                new KeyValuePair<string, Action<double>>("bottom", v => ps.BottomMargin = v),
            };
            foreach (var pair in pairs)
            {
                var cm = Protocol.GetDouble(margins, pair.Key);
                if (cm.HasValue)
                {
                    pair.Value(app.CentimetersToPoints(cm.Value));
                    applied.Add("margin " + pair.Key + "=" + cm.Value + "cm");
                }
            }
            return applied;
        }
    }
}
