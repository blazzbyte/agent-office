using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Excel = Microsoft.Office.Interop.Excel;

namespace AgentOffice.Bridge
{
/// <summary>Phase 2 analysis actions (see docs/plan/VSTO_BRIDGE_PLAN.md §9).</summary>
    internal static class AnalysisActions
    {
        // ------------------------------------------------------------------
        // goal_seek { sheet?, cell, goal, changingCell, changingSheet? }
        // ------------------------------------------------------------------

        public static Dictionary<string, object> GoalSeek(Excel.Application app, BridgeRequest req)
        {
            var cellAddress = Protocol.GetString(req.Params, "cell");
            var changingAddress = Protocol.GetString(req.Params, "changingCell");
            var goal = Protocol.GetDouble(req.Params, "goal");

            if (string.IsNullOrWhiteSpace(cellAddress))
            {
                return Protocol.Fail(req.Id, "Missing required param 'cell' (the formula cell)", "INVALID_PARAMS");
            }
            if (string.IsNullOrWhiteSpace(changingAddress))
            {
                return Protocol.Fail(req.Id, "Missing required param 'changingCell' (the input cell to vary)", "INVALID_PARAMS");
            }
            if (!goal.HasValue)
            {
                return Protocol.Fail(req.Id, "Missing or invalid required param 'goal' (target number)", "INVALID_PARAMS");
            }

            Excel.Worksheet sheet;
            Dictionary<string, object> error;
            if (!TryResolveSheet(app, Protocol.GetString(req.Params, "sheet"), req.Id, out sheet, out error))
            {
                return error;
            }

            Excel.Range cell;
            try
            {
                cell = sheet.Range[cellAddress];
            }
            catch (COMException)
            {
                return Protocol.Fail(req.Id, "Invalid cell address: " + cellAddress, "INVALID_PARAMS");
            }

            Excel.Worksheet changingSheet;
            var changingSheetName = Protocol.GetString(req.Params, "changingSheet");
            if (string.IsNullOrEmpty(changingSheetName))
            {
                changingSheet = sheet;
            }
            else if (!TryResolveSheet(app, changingSheetName, req.Id, out changingSheet, out error))
            {
                return error;
            }

            Excel.Range changing;
            try
            {
                changing = changingSheet.Range[changingAddress];
            }
            catch (COMException)
            {
                return Protocol.Fail(req.Id, "Invalid cell address: " + changingAddress, "INVALID_PARAMS");
            }

            bool converged;
            try
            {
                converged = cell.GoalSeek(goal.Value, changing);
            }
            catch (COMException com)
            {
                return ActionRouter.MapComError(
                    req.Id, com,
                    "Goal Seek failed (the formula cell must contain a formula and the changing cell a constant)");
            }

            var achieved = ActionRouter.ConvertResult(cell.Value2);
            var newInput = ActionRouter.ConvertResult(changing.Value2);
            var data = new Dictionary<string, object>
            {
                ["sheet"] = sheet.Name,
                ["cell"] = cellAddress,
                ["goal"] = goal.Value,
                ["achieved"] = achieved,
                ["changingSheet"] = changingSheet.Name,
                ["changingCell"] = changingAddress,
                ["newValue"] = newInput,
            };

            if (!converged)
            {
                return Protocol.Fail(
                    req.Id,
                    "Goal Seek did not converge on " + goal.Value + " — closest value achieved: " + achieved +
                    " (changing cell left at " + newInput + ")",
                    "GOAL_SEEK_FAILED",
                    data);
            }

            return Protocol.Ok(
                req.Id,
                "Goal Seek converged: " + sheet.Name + "!" + cellAddress + " = " + achieved +
                " by setting " + changingSheet.Name + "!" + changingAddress + " to " + newInput,
                data);
        }

        // ------------------------------------------------------------------
        // create_pivot_table { sourceSheet?, sourceRange, destSheet?, destCell?,
        //                      name?, rows?, columns?, values?, filters? }
        // ------------------------------------------------------------------

        public static Dictionary<string, object> CreatePivotTable(Excel.Application app, BridgeRequest req)
        {
            var sourceRangeAddress = Protocol.GetString(req.Params, "sourceRange");
            if (string.IsNullOrWhiteSpace(sourceRangeAddress))
            {
                return Protocol.Fail(
                    req.Id,
                    "Missing required param 'sourceRange' (e.g. \"A1:D100\" — first row must be the headers)",
                    "INVALID_PARAMS");
            }

            Excel.Worksheet srcSheet;
            Dictionary<string, object> error;
            if (!TryResolveSheet(app, Protocol.GetString(req.Params, "sourceSheet"), req.Id, out srcSheet, out error))
            {
                return error;
            }

            Excel.Range src;
            try
            {
                src = srcSheet.Range[sourceRangeAddress];
            }
            catch (COMException)
            {
                return Protocol.Fail(req.Id, "Invalid source range: " + sourceRangeAddress, "INVALID_PARAMS");
            }

            var wb = app.ActiveWorkbook;
            if (wb == null)
            {
                return Protocol.Fail(req.Id, "No active workbook", "NO_ACTIVE_WORKBOOK");
            }

            // Destination: an existing sheet, or a brand-new one (Excel's dialog default).
            Excel.Worksheet destSheet;
            var destSheetName = Protocol.GetString(req.Params, "destSheet");
            try
            {
                if (string.IsNullOrEmpty(destSheetName))
                {
                    destSheet = (Excel.Worksheet)wb.Worksheets.Add(Type.Missing, wb.Worksheets[wb.Worksheets.Count]);
                    destSheet.Name = UniqueSheetName(wb, "Pivot");
                }
                else if (!TryResolveSheet(app, destSheetName, req.Id, out destSheet, out error))
                {
                    return error;
                }
            }
            catch (COMException com)
            {
                return ActionRouter.MapComError(req.Id, com, "Could not prepare the destination sheet");
            }

            var destCellAddress = Protocol.GetString(req.Params, "destCell");
            if (string.IsNullOrWhiteSpace(destCellAddress)) destCellAddress = "A3";
            Excel.Range dest;
            try
            {
                dest = destSheet.Range[destCellAddress];
            }
            catch (COMException)
            {
                return Protocol.Fail(req.Id, "Invalid destination cell: " + destCellAddress, "INVALID_PARAMS");
            }

            var ptName = Protocol.GetString(req.Params, "name");
            if (string.IsNullOrWhiteSpace(ptName))
            {
                ptName = "AgentOfficePivot_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            }

            Excel.PivotTable pt;
            try
            {
                var cache = wb.PivotCaches().Create(Excel.XlPivotTableSourceType.xlDatabase, src);
                pt = cache.CreatePivotTable(dest, ptName);
            }
            catch (COMException com)
            {
                return ActionRouter.MapComError(req.Id, com, "Could not create the PivotTable");
            }

            // Field layout: page filters, rows, columns — data fields last.
            var filters = Protocol.GetStringList(req.Params, "filters") ?? new List<string>();
            var rows = Protocol.GetStringList(req.Params, "rows") ?? new List<string>();
            var columns = Protocol.GetStringList(req.Params, "columns") ?? new List<string>();

            Dictionary<string, object> fieldError;
            foreach (var f in filters)
            {
                fieldError = OrientField(req.Id, pt, f, Excel.XlPivotFieldOrientation.xlPageField);
                if (fieldError != null) return fieldError;
            }
            foreach (var f in rows)
            {
                fieldError = OrientField(req.Id, pt, f, Excel.XlPivotFieldOrientation.xlRowField);
                if (fieldError != null) return fieldError;
            }
            foreach (var f in columns)
            {
                fieldError = OrientField(req.Id, pt, f, Excel.XlPivotFieldOrientation.xlColumnField);
                if (fieldError != null) return fieldError;
            }

            var valueDescriptions = new List<string>();
            object valuesParam;
            var values = req.Params != null && req.Params.TryGetValue("values", out valuesParam)
                ? valuesParam as IEnumerable
                : null;
            if (values != null)
            {
                foreach (var item in values)
                {
                    var spec = item as IDictionary<string, object>;
                    if (spec == null)
                    {
                        return Protocol.Fail(
                            req.Id,
                            "Each entry of 'values' must be an object like { \"field\": \"Amount\", \"function\": \"sum\" }",
                            "INVALID_PARAMS");
                    }
                    var field = Protocol.GetString(spec, "field");
                    if (string.IsNullOrWhiteSpace(field))
                    {
                        return Protocol.Fail(req.Id, "Each entry of 'values' needs a 'field' name", "INVALID_PARAMS");
                    }

                    Excel.XlConsolidationFunction fn;
                    try
                    {
                        fn = MapFunction(Protocol.GetString(spec, "function"));
                    }
                    catch (BridgeException bex)
                    {
                        return Protocol.Fail(req.Id, bex.Message, bex.Code);
                    }

                    var label = Protocol.GetString(spec, "name");
                    try
                    {
                        pt.AddDataField(
                            pt.PivotFields(field),
                            string.IsNullOrWhiteSpace(label) ? Type.Missing : (object)label,
                            fn);
                    }
                    catch (COMException)
                    {
                        return Protocol.Fail(
                            req.Id,
                            "Field not found in the source headers (or could not be added as a value): " + field,
                            "INVALID_PARAMS");
                    }
                    valueDescriptions.Add(fn.ToString().Substring(2).ToLowerInvariant() + "(" + field + ")");
                }
            }

            return Protocol.Ok(
                req.Id,
                "Created PivotTable '" + ptName + "' at " + destSheet.Name + "!" + destCellAddress,
                new Dictionary<string, object>
                {
                    ["pivotTable"] = ptName,
                    ["sheet"] = destSheet.Name,
                    ["cell"] = destCellAddress,
                    ["sourceRange"] = srcSheet.Name + "!" + sourceRangeAddress,
                    ["rows"] = rows,
                    ["columns"] = columns,
                    ["filters"] = filters,
                    ["values"] = valueDescriptions,
                });
        }

        // ------------------------------------------------------------------
        // Shared helpers
        // ------------------------------------------------------------------

/// <summary>Resolves a worksheet by name (default: the active sheet) with a uniform error.</summary>
        internal static bool TryResolveSheet(
            Excel.Application app,
            string sheetName,
            string id,
            out Excel.Worksheet sheet,
            out Dictionary<string, object> error)
        {
            sheet = null;
            error = null;
            try
            {
                sheet = string.IsNullOrEmpty(sheetName)
                    ? app.ActiveSheet as Excel.Worksheet
                    : app.Worksheets[sheetName] as Excel.Worksheet;
            }
            catch (COMException)
            {
                error = Protocol.Fail(id, "Sheet not found: " + sheetName, "INVALID_PARAMS");
                return false;
            }
            if (sheet == null)
            {
                error = Protocol.Fail(
                    id,
                    "Sheet not found (or the active sheet is not a worksheet): " + (sheetName ?? "(active)"),
                    "INVALID_PARAMS");
                return false;
            }
            return true;
        }

        private static Dictionary<string, object> OrientField(
            string id, Excel.PivotTable pt, string field, Excel.XlPivotFieldOrientation orientation)
        {
            try
            {
                var pf = (Excel.PivotField)pt.PivotFields(field);
                pf.Orientation = orientation;
                return null;
            }
            catch (COMException)
            {
                return Protocol.Fail(id, "Field not found in the source headers: " + field, "INVALID_PARAMS");
            }
        }

        private static Excel.XlConsolidationFunction MapFunction(string name)
        {
            switch ((name ?? "sum").Trim().ToLowerInvariant())
            {
                case "sum": return Excel.XlConsolidationFunction.xlSum;
                case "count": return Excel.XlConsolidationFunction.xlCount;
                case "countnums":
                case "count_nums": return Excel.XlConsolidationFunction.xlCountNums;
                case "average":
                case "avg":
                case "mean": return Excel.XlConsolidationFunction.xlAverage;
                case "max": return Excel.XlConsolidationFunction.xlMax;
                case "min": return Excel.XlConsolidationFunction.xlMin;
                case "product": return Excel.XlConsolidationFunction.xlProduct;
                case "stdev": return Excel.XlConsolidationFunction.xlStDev;
                case "stdevp": return Excel.XlConsolidationFunction.xlStDevP;
                case "var": return Excel.XlConsolidationFunction.xlVar;
                case "varp": return Excel.XlConsolidationFunction.xlVarP;
                default:
                    throw new BridgeException(
                        "INVALID_PARAMS",
                        "Unknown aggregation function '" + name +
                        "' — use sum|count|countnums|average|max|min|product|stdev|stdevp|var|varp");
            }
        }

        private static string UniqueSheetName(Excel.Workbook wb, string baseName)
        {
            var name = baseName;
            var n = 1;
            while (true)
            {
                var exists = false;
                foreach (Excel.Worksheet ws in wb.Worksheets)
                {
                    if (string.Equals(ws.Name, name, StringComparison.OrdinalIgnoreCase))
                    {
                        exists = true;
                        break;
                    }
                }
                if (!exists) return name;
                n++;
                name = baseName + n;
            }
        }
    }
}
