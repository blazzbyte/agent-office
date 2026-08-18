using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Excel = Microsoft.Office.Interop.Excel;

namespace AgentOffice.Bridge
{
/// <summary>Dispatches bridge actions against the live COM object model.</summary>
    internal sealed class ActionRouter
    {
        private static readonly TimeSpan ActionTimeout = TimeSpan.FromSeconds(60);

/// <summary>Classic Excel error values arrive via COM interop as Int32 codes.</summary>
        private static readonly Dictionary<int, string> ExcelErrorCodes = new Dictionary<int, string>
        {
            { -2146826288, "#NULL!" },
            { -2146826281, "#DIV/0!" },
            { -2146826273, "#VALUE!" },
            { -2146826265, "#REF!" },
            { -2146826260, "#NAME?" },
            { -2146826255, "#NUM!" },
            { -2146826246, "#N/A" },
        };

        private readonly ExcelSynchronizer _sync;
        private readonly Func<Excel.Application> _getApp;

        public ActionRouter(ExcelSynchronizer sync, Func<Excel.Application> getApp)
        {
            _sync = sync;
            _getApp = getApp;
        }

/// <summary>Advertised in GET /bridge/v1/health — the web add-in uses this for capability gating.</summary>
        public string[] ActionNames
        {
            get
            {
                return new[]
                {
                    // Core actions (ported from the retired Office.js handlers)
                    "read_range", "read_sheet_names", "read_table", "read_selection",
                    "write_range", "write_formula", "create_sheet", "rename_sheet",
                    "delete_sheet", "format_range", "create_table", "add_table_row",
                    "sort_table", "create_chart", "configure_chart", "auto_fit",
                    "calculate",
                    // Internal context readers (not agent-facing)
                    "read_workbook_context", "read_workbook_snapshot",
                    // Pro actions
                    "ping", "undo", "evaluate", "insert_equation", "execute_mso",
                    "goal_seek", "export_pdf", "create_pivot_table", "capture_range",
                    "add_power_query", "configure_chart_pro", "create_slicer",
                    "protect_sheet", "protect_workbook", "page_setup",
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
                Log.Error("ActionRouter: unexpected failure for action '" + req.Action + "'", ex);
                return Protocol.Fail(req.Id, "Bridge internal error: " + ex.Message, "INTERNAL");
            }
        }

        // ------------------------------------------------------------------
        // Dispatch (runs on Excel's UI thread)
        // ------------------------------------------------------------------

        private Dictionary<string, object> Dispatch(BridgeRequest req)
        {
            switch (req.Action)
            {
                // ---- Core actions (native COM, ex-Office.js) ----
                case "read_range":
                    return Guarded(req, CoreActions.ReadRange);

                case "read_sheet_names":
                    return Guarded(req, CoreActions.ReadSheetNames);

                case "read_table":
                    return Guarded(req, CoreActions.ReadTable);

                case "read_selection":
                    return Guarded(req, CoreActions.ReadSelection);

                case "write_range":
                    return Guarded(req, CoreActions.WriteRange);

                case "write_formula":
                    return Guarded(req, CoreActions.WriteFormula);

                case "create_sheet":
                    return Guarded(req, CoreActions.CreateSheet);

                case "rename_sheet":
                    return Guarded(req, CoreActions.RenameSheet);

                case "delete_sheet":
                    return Guarded(req, CoreActions.DeleteSheet);

                case "format_range":
                    return Guarded(req, CoreActions.FormatRange);

                case "auto_fit":
                    return Guarded(req, CoreActions.AutoFit);

                case "create_table":
                    return Guarded(req, CoreActions.CreateTable);

                case "add_table_row":
                    return Guarded(req, CoreActions.AddTableRow);

                case "sort_table":
                    return Guarded(req, CoreActions.SortTable);

                case "create_chart":
                    return Guarded(req, CoreActions.CreateChart);

                case "configure_chart":
                    return Guarded(req, CoreActions.ConfigureChart);

                case "calculate":
                    return Guarded(req, CoreActions.Calculate);

                case "read_workbook_context":
                    return Guarded(req, CoreActions.ReadWorkbookContext);

                case "read_workbook_snapshot":
                    return Guarded(req, CoreActions.ReadWorkbookSnapshot);

                // ---- Pro actions ----
                case "ping":
                    return Protocol.Ok(
                        req.Id,
                        "pong — Agent Office reachable on the Excel UI thread",
                        new Dictionary<string, object> { ["excel"] = SafeVersion() });

                case "undo":
                    return Undo(req);

                case "evaluate":
                    return Evaluate(req);

                case "insert_equation":
                    return InsertEquation(req);

                case "execute_mso":
                    return ExecuteMso(req);

                case "goal_seek":
                    return GoalSeek(req);

                case "export_pdf":
                    return ExportPdf(req);

                case "create_pivot_table":
                    return CreatePivotTable(req);

                case "capture_range":
                    return CaptureRange(req);

                case "add_power_query":
                    return AddPowerQuery(req);

                case "configure_chart_pro":
                    return ConfigureChartPro(req);

                case "create_slicer":
                    return CreateSlicer(req);

                case "protect_sheet":
                    return ProtectSheet(req);

                case "protect_workbook":
                    return ProtectWorkbook(req);

                case "page_setup":
                    return PageSetup(req);

                default:
                    return Protocol.Fail(req.Id, "Unsupported bridge action: '" + req.Action + "'", "UNSUPPORTED_ACTION");
            }
        }

/// <summary>Workbook guard + invoke — the shared prelude of every core action.</summary>
        private Dictionary<string, object> Guarded(
            BridgeRequest req,
            Func<Excel.Application, BridgeRequest, Dictionary<string, object>> handler)
        {
            var guard = GuardWorkbook(req);
            if (guard != null) return guard;
            return handler(_getApp(), req);
        }

        private Dictionary<string, object> Undo(BridgeRequest req)        {
            var guard = GuardWorkbook(req);
            if (guard != null) return guard;

            try
            {
                _getApp().Undo();
                return Protocol.Ok(req.Id, "Undid the last action in the active workbook");
            }
            catch (COMException com)
            {
                // 0x800A03EC: Excel rejects Undo when the stack is empty (any
                // programmatic change — including ours — clears it) or when the
                // application is not interactive.
                if (com.ErrorCode == unchecked((int)0x800A03EC))
                {
                    return Protocol.Fail(
                        req.Id,
                        "Undo failed: there is nothing to undo. Note that programmatic changes " +
                        "(this bridge included) clear Excel's undo stack — only the user's own " +
                        "edits are undoable.",
                        "NOTHING_TO_UNDO");
                }
                return MapComError(req.Id, com, "Failed to undo");
            }
        }

        private Dictionary<string, object> Evaluate(BridgeRequest req)
        {
            var guard = GuardWorkbook(req);
            if (guard != null) return guard;

            var expression = Protocol.GetString(req.Params, "expression");
            if (string.IsNullOrWhiteSpace(expression))
            {
                return Protocol.Fail(req.Id, "Missing required param 'expression'", "INVALID_PARAMS");
            }

            try
            {
                var result = _getApp().Evaluate(expression);
                return Protocol.Ok(
                    req.Id,
                    "Evaluated: " + expression,
                    new Dictionary<string, object>
                    {
                        ["expression"] = expression,
                        ["value"] = ConvertResult(result),
                    });
            }
            catch (COMException com)
            {
                return MapComError(req.Id, com, "Failed to evaluate '" + expression + "'");
            }
        }

        private Dictionary<string, object> InsertEquation(BridgeRequest req)
        {
            var guard = GuardWorkbook(req);
            if (guard != null) return guard;
            return EquationActions.InsertEquation(_getApp(), req);
        }

/// <summary>Execute_mso { idMso } — the generic escape hatch.</summary>
        private Dictionary<string, object> ExecuteMso(BridgeRequest req)
        {
            var guard = GuardWorkbook(req);
            if (guard != null) return guard;

            var idMso = Protocol.GetString(req.Params, "idMso");
            if (string.IsNullOrWhiteSpace(idMso))
            {
                return Protocol.Fail(req.Id, "Missing required param 'idMso'", "INVALID_PARAMS");
            }

            try
            {
                _getApp().CommandBars.ExecuteMso(idMso);
                return Protocol.Ok(req.Id, "Executed Ribbon command: " + idMso);
            }
            catch (ArgumentException)
            {
                // ExecuteMso throws ArgumentException for unknown/contextually
                // unavailable idMso values.
                return Protocol.Fail(
                    req.Id,
                    "Unknown or unavailable Ribbon command in this context: '" + idMso + "'",
                    "INVALID_PARAMS");
            }
            catch (COMException com)
            {
                return MapComError(req.Id, com, "Failed to execute Ribbon command '" + idMso + "'");
            }
        }

        private Dictionary<string, object> GoalSeek(BridgeRequest req)
        {
            var guard = GuardWorkbook(req);
            if (guard != null) return guard;
            return AnalysisActions.GoalSeek(_getApp(), req);
        }

        private Dictionary<string, object> ExportPdf(BridgeRequest req)
        {
            var guard = GuardWorkbook(req);
            if (guard != null) return guard;
            return ExportActions.ExportPdf(_getApp(), req);
        }

        private Dictionary<string, object> CreatePivotTable(BridgeRequest req)
        {
            var guard = GuardWorkbook(req);
            if (guard != null) return guard;
            return AnalysisActions.CreatePivotTable(_getApp(), req);
        }

        private Dictionary<string, object> CaptureRange(BridgeRequest req)
        {
            var guard = GuardWorkbook(req);
            if (guard != null) return guard;
            return CaptureActions.CaptureRange(_getApp(), req);
        }

        private Dictionary<string, object> AddPowerQuery(BridgeRequest req)
        {
            var guard = GuardWorkbook(req);
            if (guard != null) return guard;
            return PowerQueryActions.AddPowerQuery(_getApp(), req);
        }

        private Dictionary<string, object> ConfigureChartPro(BridgeRequest req)
        {
            var guard = GuardWorkbook(req);
            if (guard != null) return guard;
            return ChartActions.ConfigureChartPro(_getApp(), req);
        }

        private Dictionary<string, object> CreateSlicer(BridgeRequest req)
        {
            var guard = GuardWorkbook(req);
            if (guard != null) return guard;
            return SlicerActions.CreateSlicer(_getApp(), req);
        }

        private Dictionary<string, object> ProtectSheet(BridgeRequest req)
        {
            var guard = GuardWorkbook(req);
            if (guard != null) return guard;
            return ProtectionActions.ProtectSheet(_getApp(), req);
        }

        private Dictionary<string, object> ProtectWorkbook(BridgeRequest req)
        {
            var guard = GuardWorkbook(req);
            if (guard != null) return guard;
            return ProtectionActions.ProtectWorkbook(_getApp(), req);
        }

        private Dictionary<string, object> PageSetup(BridgeRequest req)
        {
            var guard = GuardWorkbook(req);
            if (guard != null) return guard;
            return ExportActions.PageSetup(_getApp(), req);
        }

        // ------------------------------------------------------------------
        // Guards & error mapping
        // ------------------------------------------------------------------

/// <summary>Multi-process safety: refuses to act when the active workbook of this Excel instance does not match the one the web.</summary>
        private Dictionary<string, object> GuardWorkbook(BridgeRequest req)
        {
            if (string.IsNullOrEmpty(req.WorkbookHint)) return null; // cannot verify → allow

            Excel.Workbook wb = null;
            try
            {
                wb = _getApp().ActiveWorkbook;
            }
            catch (COMException)
            {
                // Fall through to the null check.
            }

            if (wb == null)
            {
                return Protocol.Fail(req.Id, "No active workbook in this Excel instance", "NO_ACTIVE_WORKBOOK");
            }

            string actual = null;
            try
            {
                actual = wb.Name;
            }
            catch (COMException)
            {
                // If we cannot read the name, do not block the action.
            }

            if (actual != null && !string.Equals(actual, req.WorkbookHint, StringComparison.OrdinalIgnoreCase))
            {
                return Protocol.Fail(
                    req.Id,
                    "Bridge is attached to workbook '" + actual + "' but the request targets '" + req.WorkbookHint + "'",
                    "WORKBOOK_MISMATCH",
                    new Dictionary<string, object> { ["activeWorkbook"] = actual });
            }

            return null;
        }

        internal static Dictionary<string, object> MapComError(string id, COMException com, string prefix)
        {
            string message;
            try
            {
                message = prefix + ": " + com.Message;
            }
            catch
            {
                message = prefix;
            }

            var hr = com.ErrorCode;
            var busy =
                hr == unchecked((int)0x8001010A) || // RPC_E_CALL_REJECTED
                hr == unchecked((int)0x800AC472) || // Excel message-filter busy
                message.IndexOf("busy", StringComparison.OrdinalIgnoreCase) >= 0 ||
                message.IndexOf("rejected", StringComparison.OrdinalIgnoreCase) >= 0;

            if (busy)
            {
                return Protocol.Fail(
                    id,
                    message + " — Excel is busy (the user may be editing a cell or a dialog is open). Retry shortly.",
                    "EXCEL_BUSY");
            }

            return Protocol.Fail(id, message, "COM_ERROR");
        }

        internal static object ConvertResult(object result)
        {
            if (result == null) return null;
            if (result is double || result is string || result is bool) return result;
            if (result is DateTime) return ((DateTime)result).ToString("o");

            var intValue = result as int?;
            if (intValue.HasValue)
            {
                string errorText;
                if (ExcelErrorCodes.TryGetValue(intValue.Value, out errorText)) return errorText;
                return intValue.Value;
            }

            var matrix = result as object[,];
            if (matrix != null)
            {
                var rows = matrix.GetLength(0);
                var cols = matrix.GetLength(1);
                var list = new List<object>(rows);
                for (var r = 1; r <= rows; r++)
                {
                    var row = new List<object>(cols);
                    for (var c = 1; c <= cols; c++)
                    {
                        row.Add(ConvertResult(matrix[r, c]));
                    }
                    list.Add(row);
                }
                return list;
            }

            return Convert.ToString(result);
        }

        private string SafeVersion()
        {
            try
            {
                return _getApp().Version;
            }
            catch
            {
                return "unknown";
            }
        }
    }
}
