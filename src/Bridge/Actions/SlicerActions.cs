using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Excel = Microsoft.Office.Interop.Excel;

namespace AgentOffice.Bridge
{
/// <summary>Phase 2 action (see docs/plan/VSTO_BRIDGE_PLAN.md §9).</summary>
    internal static class SlicerActions
    {
        public static Dictionary<string, object> CreateSlicer(Excel.Application app, BridgeRequest req)
        {
            var sourceType = (Protocol.GetString(req.Params, "sourceType") ?? "table").Trim().ToLowerInvariant();
            var sourceName = Protocol.GetString(req.Params, "sourceName");
            var field = Protocol.GetString(req.Params, "field");

            if (sourceType != "table" && sourceType != "pivot")
            {
                return Protocol.Fail(req.Id, "Invalid 'sourceType': use \"table\" (default) or \"pivot\"", "INVALID_PARAMS");
            }
            if (string.IsNullOrWhiteSpace(sourceName))
            {
                return Protocol.Fail(req.Id, "Missing required param 'sourceName' (the table or PivotTable name)", "INVALID_PARAMS");
            }
            if (string.IsNullOrWhiteSpace(field))
            {
                return Protocol.Fail(req.Id, "Missing required param 'field' (the column/field to filter)", "INVALID_PARAMS");
            }

            var wb = app.ActiveWorkbook;
            if (wb == null)
            {
                return Protocol.Fail(req.Id, "No active workbook", "NO_ACTIVE_WORKBOOK");
            }

            // --- Resolve the source object and its home sheet/anchor.
            object source = null;          // ListObject or PivotTable
            Excel.Worksheet sourceSheet = null;
            Excel.Range sourceRange = null;

            if (sourceType == "table")
            {
                Excel.ListObject lo;
                if (!TryFindTable(wb, sourceName, out lo, out sourceSheet))
                {
                    return Protocol.Fail(req.Id, "Table not found in this workbook: " + sourceName, "INVALID_PARAMS");
                }
                source = lo;
                sourceRange = lo.Range;
            }
            else
            {
                Excel.Worksheet pivotSheet;
                Dictionary<string, object> error;
                if (!AnalysisActions.TryResolveSheet(app, Protocol.GetString(req.Params, "sourceSheet"), req.Id, out pivotSheet, out error))
                {
                    return error;
                }
                Excel.PivotTable pt = null;
                try
                {
                    pt = pivotSheet.PivotTables(sourceName);
                }
                catch (COMException) { }
                if (pt == null)
                {
                    return Protocol.Fail(req.Id, "PivotTable not found on sheet '" + pivotSheet.Name + "': " + sourceName, "INVALID_PARAMS");
                }
                source = pt;
                sourceSheet = pivotSheet;
                try { sourceRange = pt.TableRange1; } catch (COMException) { }
            }

            // --- Create the slicer cache.
            Excel.SlicerCache cache;
            try
            {
                cache = wb.SlicerCaches.Add2(source, field);
            }
            catch (COMException com)
            {
                return ActionRouter.MapComError(
                    req.Id, com,
                    "Could not create the slicer cache (does the field '" + field + "' exist in " + sourceName + "?)");
            }

            // --- Place the slicer.
            Excel.Worksheet destSheet;
            Dictionary<string, object> sheetError;
            var destSheetName = Protocol.GetString(req.Params, "destSheet");
            if (string.IsNullOrEmpty(destSheetName))
            {
                destSheet = sourceSheet;
            }
            else if (!AnalysisActions.TryResolveSheet(app, destSheetName, req.Id, out destSheet, out sheetError))
            {
                return sheetError;
            }

            float top = 20f, left = 20f;
            var destCellAddress = Protocol.GetString(req.Params, "destCell");
            try
            {
                Excel.Range anchor = null;
                if (!string.IsNullOrWhiteSpace(destCellAddress))
                {
                    anchor = destSheet.Range[destCellAddress];
                }
                else if (sourceRange != null && destSheet.Name == sourceSheet.Name)
                {
                    // Default: one column to the right of the source, top-aligned.
                    anchor = sourceSheet.Cells[sourceRange.Row, sourceRange.Column + sourceRange.Columns.Count + 1];
                }
                if (anchor != null)
                {
                    top = (float)anchor.Top;
                    left = (float)anchor.Left;
                }
            }
            catch (COMException)
            {
                return Protocol.Fail(req.Id, "Invalid destination cell: " + destCellAddress, "INVALID_PARAMS");
            }

            var width = (float)(Protocol.GetDouble(req.Params, "width") ?? 150.0);
            var height = (float)(Protocol.GetDouble(req.Params, "height") ?? 200.0);
            var slicerName = Protocol.GetString(req.Params, "name");
            if (string.IsNullOrWhiteSpace(slicerName))
            {
                slicerName = "AgentOfficeSlicer_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            }

            try
            {
                var slicer = cache.Slicers.Add(destSheet);
                slicer.Top = top;
                slicer.Left = left;
                slicer.Width = width;
                slicer.Height = height;
                try { slicer.Name = slicerName; }
                catch (COMException) { /* name collision — keep the generated one */ }

                var caption = Protocol.GetString(req.Params, "caption");
                if (!string.IsNullOrWhiteSpace(caption))
                {
                    slicer.Caption = caption;
                }

                string finalName;
                try { finalName = slicer.Name; }
                catch (COMException) { finalName = slicerName; }

                return Protocol.Ok(
                    req.Id,
                    "Created slicer '" + finalName + "' filtering " + sourceName + " by '" + field + "' on " + destSheet.Name,
                    new Dictionary<string, object>
                    {
                        ["slicer"] = finalName,
                        ["sourceType"] = sourceType,
                        ["source"] = sourceName,
                        ["field"] = field,
                        ["sheet"] = destSheet.Name,
                    });
            }
            catch (COMException com)
            {
                // The slicer itself failed — drop the cache so nothing half-built remains.
                try { cache.Delete(); } catch { }
                return ActionRouter.MapComError(req.Id, com, "Could not place the slicer");
            }
        }

        // ------------------------------------------------------------------

        private static bool TryFindTable(
            Excel.Workbook wb, string name, out Excel.ListObject table, out Excel.Worksheet sheet)
        {
            table = null;
            sheet = null;
            foreach (Excel.Worksheet ws in wb.Worksheets)
            {
                Excel.ListObject lo = null;
                try
                {
                    lo = ws.ListObjects[name];
                }
                catch (COMException) { }
                if (lo != null)
                {
                    table = lo;
                    sheet = ws;
                    return true;
                }
            }
            return false;
        }
    }
}
