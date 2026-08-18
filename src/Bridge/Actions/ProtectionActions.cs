using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Excel = Microsoft.Office.Interop.Excel;

namespace AgentOffice.Bridge
{
/// <summary>Phase 2 actions (see docs/plan/VSTO_BRIDGE_PLAN.md §9) — granular protection.</summary>
    internal static class ProtectionActions
    {
        public static Dictionary<string, object> ProtectSheet(Excel.Application app, BridgeRequest req)
        {
            Excel.Worksheet sheet;
            Dictionary<string, object> error;
            if (!AnalysisActions.TryResolveSheet(app, Protocol.GetString(req.Params, "sheet"), req.Id, out sheet, out error))
            {
                return error;
            }

            var password = Protocol.GetString(req.Params, "password");
            var passwordArg = string.IsNullOrEmpty(password) ? Type.Missing : (object)password;

            try
            {
                if (Protocol.GetBool(req.Params, "unprotect"))
                {
                    sheet.Unprotect(passwordArg);
                    return Protocol.Ok(req.Id, "Unprotected sheet '" + sheet.Name + "'", new Dictionary<string, object>
                    {
                        ["sheet"] = sheet.Name,
                        ["protected"] = false,
                    });
                }

                var allow = Protocol.GetStringList(req.Params, "allow") ?? new List<string>();
                sheet.Protect(
                    passwordArg,
                    Type.Missing, // DrawingObjects
                    Type.Missing, // Contents
                    Type.Missing, // Scenarios
                    Type.Missing, // UserInterfaceOnly
                    Has(allow, "formatCells"),
                    Has(allow, "formatColumns"),
                    Has(allow, "formatRows"),
                    Has(allow, "insertColumns"),
                    Has(allow, "insertRows"),
                    Has(allow, "insertHyperlinks"),
                    Has(allow, "deleteColumns"),
                    Has(allow, "deleteRows"),
                    Has(allow, "sort"),
                    Has(allow, "autofilter"),
                    Has(allow, "pivottables"));

                return Protocol.Ok(
                    req.Id,
                    "Protected sheet '" + sheet.Name + "'" +
                        (password != null ? " (password set)" : " (no password — casual protection)") +
                        (allow.Count > 0 ? " — allowed: " + string.Join(", ", allow) : string.Empty),
                    new Dictionary<string, object>
                    {
                        ["sheet"] = sheet.Name,
                        ["protected"] = true,
                        ["passwordSet"] = password != null,
                        ["allow"] = allow,
                    });
            }
            catch (COMException com)
            {
                return ActionRouter.MapComError(req.Id, com, "Protection failed (wrong password? sheet already in that state?)");
            }
        }

        public static Dictionary<string, object> ProtectWorkbook(Excel.Application app, BridgeRequest req)
        {
            var wb = app.ActiveWorkbook;
            if (wb == null)
            {
                return Protocol.Fail(req.Id, "No active workbook", "NO_ACTIVE_WORKBOOK");
            }

            var password = Protocol.GetString(req.Params, "password");
            var passwordArg = string.IsNullOrEmpty(password) ? Type.Missing : (object)password;

            try
            {
                if (Protocol.GetBool(req.Params, "unprotect"))
                {
                    wb.Unprotect(passwordArg);
                    return Protocol.Ok(req.Id, "Unprotected workbook '" + wb.Name + "'", new Dictionary<string, object>
                    {
                        ["protected"] = false,
                    });
                }

                // Defaults mirror Excel's dialog: structure on, windows off.
                var structure = req.Params == null || !req.Params.ContainsKey("structure") || Protocol.GetBool(req.Params, "structure");
                var windows = Protocol.GetBool(req.Params, "windows");
                wb.Protect(passwordArg, structure, windows);

                return Protocol.Ok(
                    req.Id,
                    "Protected workbook '" + wb.Name + "' (structure: " + structure + ", windows: " + windows + ")",
                    new Dictionary<string, object>
                    {
                        ["protected"] = true,
                        ["structure"] = structure,
                        ["windows"] = windows,
                        ["passwordSet"] = password != null,
                    });
            }
            catch (COMException com)
            {
                return ActionRouter.MapComError(req.Id, com, "Protection failed (wrong password? workbook already in that state?)");
            }
        }

        private static bool Has(List<string> allow, string key)
        {
            foreach (var a in allow)
            {
                if (string.Equals(a, key, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
    }
}
