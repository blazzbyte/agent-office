using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Excel = Microsoft.Office.Interop.Excel;

namespace AgentOffice.Bridge
{
/// <summary>Capture_range { sheet?, range?, selection?, maxRows?, maxCols?, maxDimension?</summary>
    internal static class CaptureActions
    {
        private const int DefaultMaxRows = 80;
        private const int DefaultMaxCols = 30;
        private const int DefaultMaxDimension = 1568; // long-edge cap, vision-model friendly
        private const int ClipboardRetryCount = 5;
        private const int ClipboardRetryDelayMs = 120;

        public static Dictionary<string, object> CaptureRange(Excel.Application app, BridgeRequest req)
        {
            Excel.Worksheet sheet;
            Dictionary<string, object> error;
            if (!AnalysisActions.TryResolveSheet(app, Protocol.GetString(req.Params, "sheet"), req.Id, out sheet, out error))
            {
                return error;
            }

            var rangeAddress = Protocol.GetString(req.Params, "range");
            var useSelection = Protocol.GetBool(req.Params, "selection");
            var maxRows = Math.Max(1, (int)(Protocol.GetDouble(req.Params, "maxRows") ?? DefaultMaxRows));
            var maxCols = Math.Max(1, (int)(Protocol.GetDouble(req.Params, "maxCols") ?? DefaultMaxCols));
            var maxDimension = Math.Max(256, (int)(Protocol.GetDouble(req.Params, "maxDimension") ?? DefaultMaxDimension));

            // --- Resolve the target range: explicit > selection > UsedRange (capped).
            Excel.Range target;
            string mode;
            var truncated = false;
            string usedRangeAddress = null;

            if (!string.IsNullOrWhiteSpace(rangeAddress))
            {
                try
                {
                    target = sheet.Range[rangeAddress];
                }
                catch (COMException)
                {
                    return Protocol.Fail(req.Id, "Invalid range: " + rangeAddress, "INVALID_PARAMS");
                }
                mode = "range";
            }
            else if (useSelection)
            {
                Excel.Range sel = null;
                try { sel = app.Selection as Excel.Range; }
                catch (COMException) { }
                if (sel == null)
                {
                    return Protocol.Fail(
                        req.Id,
                        "The current selection is not a cell range — select the area to capture first.",
                        "INVALID_PARAMS");
                }
                target = sel;
                // The selection may live on another sheet — capture from its own sheet.
                var selSheet = sel.Worksheet as Excel.Worksheet;
                if (selSheet != null) sheet = selSheet;
                mode = "selection";
            }
            else
            {
                Excel.Range used;
                try
                {
                    used = sheet.UsedRange;
                }
                catch (COMException com)
                {
                    return ActionRouter.MapComError(req.Id, com, "Could not read the used range");
                }
                var minRow = used.Row;
                var minCol = used.Column;
                var maxRow = used.Row + used.Rows.Count - 1;
                var maxCol = used.Column + used.Columns.Count - 1;
                try
                {
                    foreach (Excel.Shape shp in sheet.Shapes)
                    {
                        try
                        {
                            var tl = shp.TopLeftCell;
                            var br = shp.BottomRightCell;
                            if (tl != null)
                            {
                                minRow = Math.Min(minRow, tl.Row);
                                minCol = Math.Min(minCol, tl.Column);
                            }
                            if (br != null)
                            {
                                maxRow = Math.Max(maxRow, br.Row + br.Rows.Count - 1);
                                maxCol = Math.Max(maxCol, br.Column + br.Columns.Count - 1);
                            }
                        }
                        catch (COMException) { /* skip unreadable shape */ }
                    }
                }
                catch (COMException) { /* shapes collection unavailable — keep UsedRange */ }

                var totalRows = maxRow - minRow + 1;
                var totalCols = maxCol - minCol + 1;
                // Note: the two-arg Range indexer returns dynamic — bind to a typed
                // local before calling get_Address (dynamic COM lookup fails on it).
                var visualRange = (Excel.Range)sheet.Range[
                    sheet.Cells[minRow, minCol],
                    sheet.Cells[maxRow, maxCol]];
                usedRangeAddress = visualRange.get_Address(false, false, Excel.XlReferenceStyle.xlA1);

                target = sheet.Range[
                    sheet.Cells[minRow, minCol],
                    sheet.Cells[minRow + Math.Min(totalRows, maxRows) - 1, minCol + Math.Min(totalCols, maxCols) - 1]];
                truncated = totalRows > maxRows || totalCols > maxCols;
                mode = "usedRange";
            }

            Image image;
            string clipError;
            if (!TryCopyPicture(target, out image, out clipError))
            {
                return Protocol.Fail(req.Id, clipError, "CAPTURE_FAILED");
            }

            try
            {
                using (image)
                {
                    var scaled = Downscale(image, maxDimension);
                    try
                    {
                        byte[] pngBytes;
                        using (var ms = new MemoryStream())
                        {
                            scaled.Save(ms, ImageFormat.Png);
                            pngBytes = ms.ToArray();
                        }

                        var address = target.get_Address(false, false, Excel.XlReferenceStyle.xlA1);
                        var data = new Dictionary<string, object>
                        {
                            ["image"] = "data:image/png;base64," + Convert.ToBase64String(pngBytes),
                            ["mime"] = "image/png",
                            ["width"] = scaled.Width,
                            ["height"] = scaled.Height,
                            ["sheet"] = sheet.Name,
                            ["range"] = address,
                            ["mode"] = mode,
                            ["truncated"] = truncated,
                        };
                        if (usedRangeAddress != null) data["usedRange"] = usedRangeAddress;

                        var summary = "Captured " + sheet.Name + "!" + address + " as a " +
                            scaled.Width + "×" + scaled.Height + " PNG (mode: " + mode + ")";
                        if (truncated)
                        {
                            summary += " — TRUNCATED to the top-left " + maxRows + "×" + maxCols +
                                " block of " + usedRangeAddress + "; ask for a specific 'range' to see the rest";
                        }
                        summary += ". The image is attached to this result — you can see it. " +
                            "(Used the clipboard once, like a manual copy/paste.)";

                        return Protocol.Ok(req.Id, summary, data);
                    }
                    finally
                    {
                        if (!ReferenceEquals(scaled, image)) scaled.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                return Protocol.Fail(req.Id, "Could not encode the capture: " + ex.Message, "CAPTURE_FAILED");
            }
        }

        // ------------------------------------------------------------------

/// <summary>CopyPicture + clipboard read with a retry loop for clipboard contention.</summary>
        private static bool TryCopyPicture(Excel.Range target, out Image image, out string error)
        {
            image = null;
            error = null;

            Exception lastError = null;
            for (var attempt = 0; attempt < ClipboardRetryCount; attempt++)
            {
                try
                {
                    target.CopyPicture(
                        Excel.XlPictureAppearance.xlScreen,
                        Excel.XlCopyPictureFormat.xlBitmap);

                    var data = Clipboard.GetDataObject();
                    var img = data != null ? data.GetData(DataFormats.Bitmap) as Image : null;
                    if (img != null)
                    {
                        image = new Bitmap(img); // detach from the clipboard
                        return true;
                    }
                    lastError = new InvalidOperationException("the clipboard held no bitmap after CopyPicture");
                }
                catch (COMException ex) // Excel-side failure (must come first: derives from ExternalException)
                {
                    lastError = ex;
                }
                catch (ExternalException ex) // WinForms clipboard-open contention
                {
                    lastError = ex;
                }
                System.Threading.Thread.Sleep(ClipboardRetryDelayMs);
            }

            error = "Could not capture the range as a picture (" +
                (lastError != null ? lastError.Message : "unknown error") +
                "). Retry shortly — the capture uses the clipboard once.";
            return false;
        }

/// <summary>Returns a copy scaled so the long edge is ≤ maxDimension (or a plain copy).</summary>
        private static Bitmap Downscale(Image image, int maxDimension)
        {
            var longEdge = Math.Max(image.Width, image.Height);
            if (longEdge <= maxDimension) return new Bitmap(image);

            var scale = (double)maxDimension / longEdge;
            var nw = Math.Max(1, (int)Math.Round(image.Width * scale));
            var nh = Math.Max(1, (int)Math.Round(image.Height * scale));
            var bmp = new Bitmap(nw, nh);
            using (var g = Graphics.FromImage(bmp))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(image, 0, 0, nw, nh);
            }
            return bmp;
        }
    }
}
