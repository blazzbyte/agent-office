using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Excel = Microsoft.Office.Interop.Excel;
using Office = Microsoft.Office.Core;

namespace AgentOffice.Bridge
{
/// <summary>Insert_equation: inserts a REAL native Excel equation (OMML math zone in a textbox shape) — something Office.js cannot.</summary>
    internal static class EquationActions
    {
        public static Dictionary<string, object> InsertEquation(Excel.Application app, BridgeRequest req)
        {
            var linear = Protocol.GetString(req.Params, "linear");
            var latex = Protocol.GetString(req.Params, "latex");

            if (string.IsNullOrWhiteSpace(linear) && string.IsNullOrWhiteSpace(latex))
            {
                return Protocol.Fail(req.Id, "Provide 'linear' (UnicodeMath) or 'latex' (best-effort converted).", "INVALID_PARAMS");
            }
            if (string.IsNullOrWhiteSpace(linear))
            {
                linear = LatexConverter.ToLinear(latex);
                if (string.IsNullOrWhiteSpace(linear))
                {
                    return Protocol.Fail(req.Id, "Could not convert 'latex' to linear format: " + latex, "INVALID_PARAMS");
                }
            }

            // Resolve target worksheet (default: active sheet).
            Excel.Worksheet sheet;
            var sheetName = Protocol.GetString(req.Params, "sheet");
            try
            {
                sheet = string.IsNullOrEmpty(sheetName)
                    ? app.ActiveSheet as Excel.Worksheet
                    : app.Worksheets[sheetName] as Excel.Worksheet;
            }
            catch (COMException)
            {
                return Protocol.Fail(req.Id, "Sheet not found: " + sheetName, "INVALID_PARAMS");
            }
            if (sheet == null)
            {
                return Protocol.Fail(req.Id, "Target sheet is not a worksheet (a chart sheet may be active).", "INVALID_PARAMS");
            }

            // Resolve anchor cell (default: active cell).
            Excel.Range cell;
            var cellAddress = Protocol.GetString(req.Params, "cell");
            try
            {
                cell = string.IsNullOrEmpty(cellAddress) ? app.ActiveCell : sheet.Range[cellAddress];
                if (cell == null) cell = sheet.Range["A1"];
            }
            catch (COMException)
            {
                return Protocol.Fail(req.Id, "Invalid cell address: " + cellAddress, "INVALID_PARAMS");
            }

            // Build the OMML locally (no external processes).
            string omml;
            try
            {
                omml = LinearToOmml.Convert(linear);
            }
            catch (Exception ex)
            {
                return Protocol.Fail(req.Id, "Could not parse the linear format '" + linear + "': " + ex.Message, "INVALID_PARAMS");
            }
            if (string.IsNullOrEmpty(omml))
            {
                return Protocol.Fail(req.Id, "Could not parse the linear format: " + linear, "INVALID_PARAMS");
            }
            var cfHtml = CfHtml.FromOmml(omml);

            // Put the payload on the clipboard (retry while another app holds it).
            string clipError;
            if (!TrySetClipboard(cfHtml, linear, out clipError))
            {
                return Protocol.Fail(req.Id, clipError, "CLIPBOARD_BUSY");
            }

            // Drop it into a borderless textbox via range-level paste.
            Excel.Shape shp = null;
            try
            {
                shp = sheet.Shapes.AddTextbox(
                    Office.MsoTextOrientation.msoTextOrientationHorizontal,
                    (float)cell.Left, (float)cell.Top, 20f, 20f);
                shp.Line.Visible = Office.MsoTriState.msoFalse;
                shp.Fill.Visible = Office.MsoTriState.msoFalse;
                shp.TextFrame2.AutoSize = Office.MsoAutoSize.msoAutoSizeShapeToFitText;
                shp.TextFrame2.WordWrap = Office.MsoTriState.msoFalse;

                var range = shp.TextFrame2.TextRange;
                range.Paste();

                var zones = range.MathZones[-1, -1];
                if (zones == null || zones.Count == 0)
                {
                    try { shp.Delete(); } catch { }
                    return Protocol.Fail(
                        req.Id,
                        "The paste did not produce an equation (the clipboard content was not recognized as math).",
                        "PASTE_FAILED");
                }

                var shapeName = "AgentOfficeEquation_" + Guid.NewGuid().ToString("N").Substring(0, 8);
                shp.Name = shapeName;

                return Protocol.Ok(
                    req.Id,
                    "Inserted equation at " + sheet.Name + "!" +
                    cell.get_Address(false, false, Excel.XlReferenceStyle.xlA1) +
                    " (used the clipboard once, like a manual copy/paste)",
                    new Dictionary<string, object>
                    {
                        ["sheet"] = sheet.Name,
                        ["cell"] = cell.get_Address(false, false, Excel.XlReferenceStyle.xlA1),
                        ["linear"] = linear,
                        ["shape"] = shapeName,
                        ["mathZones"] = zones.Count,
                        ["engine"] = "omml-cfhtml",
                    });
            }
            catch (COMException com)
            {
                if (shp != null)
                {
                    try { shp.Delete(); } catch { }
                }
                return ActionRouter.MapComError(req.Id, com, "Failed to place the equation into Excel");
            }
        }

        // ------------------------------------------------------------------

/// <summary>Publishes the CF_HTML payload (+ plain-text fallback) on the clipboard.</summary>
        private static bool TrySetClipboard(string cfHtml, string plainText, out string error)
        {
            error = null;
            for (var attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    var data = new System.Windows.Forms.DataObject();
                    data.SetData(
                        "HTML Format",
                        new System.IO.MemoryStream(System.Text.Encoding.UTF8.GetBytes(cfHtml)));
                    if (!string.IsNullOrEmpty(plainText))
                    {
                        data.SetData(System.Windows.Forms.DataFormats.UnicodeText, plainText);
                    }
                    System.Windows.Forms.Clipboard.Clear();
                    System.Windows.Forms.Clipboard.SetDataObject(data, true);
                    return true;
                }
                catch (ExternalException ex)
                {
                    error = "The clipboard is busy (another application is holding it): " + ex.Message;
                    Thread.Sleep(120);
                }
            }
            return false;
        }
    }
}
