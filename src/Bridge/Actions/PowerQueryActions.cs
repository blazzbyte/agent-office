using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Excel = Microsoft.Office.Interop.Excel;

namespace AgentOffice.Bridge
{
/// <summary>Power Query action: add_power_query { name, formula, loadTo?, destSheet?, destCell? }</summary>
    internal static class PowerQueryActions
    {
        public static Dictionary<string, object> AddPowerQuery(Excel.Application app, BridgeRequest req)
        {
            var name = Protocol.GetString(req.Params, "name");
            var formula = Protocol.GetString(req.Params, "formula");
            var loadTo = (Protocol.GetString(req.Params, "loadTo") ?? "table").Trim().ToLowerInvariant();

            if (string.IsNullOrWhiteSpace(name))
            {
                return Protocol.Fail(req.Id, "Missing required param 'name' (query name)", "INVALID_PARAMS");
            }
            if (string.IsNullOrWhiteSpace(formula))
            {
                return Protocol.Fail(req.Id, "Missing required param 'formula' (M code, e.g. \"let Source = Excel.CurrentWorkbook() ... in Source\")", "INVALID_PARAMS");
            }
            if (loadTo != "table" && loadTo != "connection")
            {
                return Protocol.Fail(req.Id, "Invalid 'loadTo': use \"table\" (default) or \"connection\"", "INVALID_PARAMS");
            }

            var wb = app.ActiveWorkbook;
            if (wb == null)
            {
                return Protocol.Fail(req.Id, "No active workbook", "NO_ACTIVE_WORKBOOK");
            }
            try
            {
                dynamic wbDynamic = wb;
                dynamic queries = wbDynamic.Queries;
                queries.Add(name, formula);
            }
            catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
            {
                return Protocol.Fail(
                    req.Id,
                    "Power Query is not available in this Excel version (requires Excel 2016+)",
                    "UNSUPPORTED_ACTION");
            }
            catch (COMException com)
            {
                return Protocol.Fail(
                    req.Id,
                    "Could not create the query (M syntax error or the name already exists): " + com.Message,
                    "INVALID_PARAMS");
            }

            if (loadTo == "connection")
            {
                return Protocol.Ok(
                    req.Id,
                    "Created connection-only Power Query '" + name + "'",
                    new Dictionary<string, object>
                    {
                        ["name"] = name,
                        ["loadTo"] = "connection",
                    });
            }
            Excel.Worksheet destSheet = null;
            Excel.Worksheet createdSheet = null;
            Excel.ListObject table = null;

            try
            {
                Dictionary<string, object> error;
                var destSheetName = Protocol.GetString(req.Params, "destSheet");
                if (string.IsNullOrEmpty(destSheetName))
                {
                    destSheet = (Excel.Worksheet)wb.Worksheets.Add(Type.Missing, wb.Worksheets[wb.Worksheets.Count]);
                    destSheet.Name = UniqueSheetName(wb, "Query");
                    createdSheet = destSheet;
                }
                else if (!AnalysisActions.TryResolveSheet(app, destSheetName, req.Id, out destSheet, out error))
                {
                    // Destination resolution failure also rolls back.
                    Rollback(wb, name, table, createdSheet);
                    return error;
                }

                var destCellAddress = Protocol.GetString(req.Params, "destCell");
                if (string.IsNullOrWhiteSpace(destCellAddress)) destCellAddress = "A1";
                var dest = destSheet.Range[destCellAddress];

                table = destSheet.ListObjects.Add(
                    Excel.XlListObjectSourceType.xlSrcExternal,
                    "OLEDB;Provider=Microsoft.Mashup.OleDb.1;Data Source=$Workbook$;Location=" + name + ";Extended Properties=\"\"",
                    Type.Missing,
                    Excel.XlYesNoGuess.xlYes,
                    dest);
                table.QueryTable.CommandText = "SELECT * FROM [" + name + "]";
                table.QueryTable.Refresh(false);

                var rows = 0;
                var cols = 0;
                try
                {
                    rows = table.QueryTable.ResultRange.Rows.Count;
                    cols = table.QueryTable.ResultRange.Columns.Count;
                }
                catch (COMException) { /* result range unavailable — leave zeros */ }

                return Protocol.Ok(
                    req.Id,
                    "Created Power Query '" + name + "' and loaded " + rows + " rows × " + cols +
                    " cols to " + destSheet.Name + "!" + destCellAddress,
                    new Dictionary<string, object>
                    {
                        ["name"] = name,
                        ["loadTo"] = "table",
                        ["sheet"] = destSheet.Name,
                        ["cell"] = destCellAddress,
                        ["rows"] = rows,
                        ["columns"] = cols,
                    });
            }
            catch (Exception ex)
            {
                // Broad catch on purpose: some COM failures surface as ArgumentException
                // (E_INVALIDARG) instead of COMException. Always roll back.
                Rollback(wb, name, table, createdSheet);
                var com = ex as COMException;
                if (com != null)
                {
                    return ActionRouter.MapComError(
                        req.Id, com,
                        "The query '" + name + "' was rolled back because loading it failed (check the M code)");
                }
                return Protocol.Fail(
                    req.Id,
                    "The query '" + name + "' was rolled back because loading it failed (check the M code): " + ex.Message,
                    "REFRESH_FAILED");
            }
        }

        // ------------------------------------------------------------------

/// <summary>Best-effort cleanup of a partially materialized query.</summary>
        private static void Rollback(
            Excel.Workbook wb, string queryName, Excel.ListObject table, Excel.Worksheet createdSheet)
        {
            try { if (table != null) table.Delete(); } catch { }
            try
            {
                dynamic wbDynamic = wb; // late-bound receiver (see AddPowerQuery)
                dynamic q = wbDynamic.Queries.Item(queryName);
                q.Delete();
            }
            catch { }
            try
            {
                if (createdSheet != null)
                {
                    var alerts = wb.Application.DisplayAlerts;
                    wb.Application.DisplayAlerts = false;
                    createdSheet.Delete();
                    wb.Application.DisplayAlerts = alerts;
                }
            }
            catch { }
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
