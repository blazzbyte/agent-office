using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Excel = Microsoft.Office.Interop.Excel;

namespace AgentOffice.Bridge
{
/// <summary>The 17 core Excel actions (plus the two internal context readers), ported from the retired Office.js handlers.</summary>
    internal static class CoreActions
    {
        // ==================================================================
        // Reads
        // ==================================================================

/// <summary>Read_range { sheet?, range?</summary>
        public static Dictionary<string, object> ReadRange(Excel.Application app, BridgeRequest req)
        {
            try
            {
                var sheet = ResolveSheet(app, Protocol.GetString(req.Params, "sheet"));
                var rangeAddress = Protocol.GetString(req.Params, "range");

                Excel.Range range;
                if (!string.IsNullOrWhiteSpace(rangeAddress))
                {
                    range = ResolveRange(app, sheet, rangeAddress);
                }
                else
                {
                    // Office.js parity: getUsedRange(), falling back to A1.
                    try { range = sheet.UsedRange; }
                    catch (COMException) { range = sheet.Range["A1"]; }
                }

                var rows = (int)range.Rows.Count;
                var cols = (int)range.Columns.Count;
                var address = FriendlyAddress(range);

                return Protocol.Ok(
                    req.Id,
                    "Successfully read " + (rows * cols) + " cell(s) from " + address,
                    new Dictionary<string, object>
                    {
                        ["address"] = address,
                        ["rowCount"] = rows,
                        ["columnCount"] = cols,
                        ["values"] = Normalize2D(ActionRouter.ConvertResult(range.Value2)),
                    });
            }
            catch (BridgeException bex) { return Protocol.Fail(req.Id, bex.Message, bex.Code); }
            catch (COMException com) { return ActionRouter.MapComError(req.Id, com, "Failed to read range"); }
        }

/// <summary>Read_sheet_names {} → { sheets: [name] }.</summary>
        public static Dictionary<string, object> ReadSheetNames(Excel.Application app, BridgeRequest req)
        {
            try
            {
                var wb = RequireWorkbook(app);
                var names = new List<object>();
                foreach (Excel.Worksheet ws in wb.Worksheets)
                {
                    names.Add(ws.Name);
                }

                var flat = new List<string>();
                foreach (var n in names) flat.Add((string)n);

                return Protocol.Ok(
                    req.Id,
                    "Found " + names.Count + " worksheet(s): " + string.Join(", ", flat.ToArray()),
                    new Dictionary<string, object> { ["sheets"] = names });
            }
            catch (BridgeException bex) { return Protocol.Fail(req.Id, bex.Message, bex.Code); }
            catch (COMException com) { return ActionRouter.MapComError(req.Id, com, "Failed to read sheet names"); }
        }

/// <summary>Read_table { sheet?, tableName } → { tableName, address, rowCount, columnCount, values }.</summary>
        public static Dictionary<string, object> ReadTable(Excel.Application app, BridgeRequest req)
        {
            try
            {
                var tableName = Protocol.GetString(req.Params, "tableName");
                if (string.IsNullOrWhiteSpace(tableName))
                    return Protocol.Fail(req.Id, "Missing parameter: tableName", "INVALID_PARAMS");

                var table = ResolveTable(app, Protocol.GetString(req.Params, "sheet"), tableName);
                var range = table.Range;
                var rows = (int)range.Rows.Count;
                var cols = (int)range.Columns.Count;

                return Protocol.Ok(
                    req.Id,
                    "Successfully read table '" + tableName + "' (" + rows + " rows, " + cols + " columns)",
                    new Dictionary<string, object>
                    {
                        ["tableName"] = tableName,
                        ["address"] = FriendlyAddress(range),
                        ["rowCount"] = rows,
                        ["columnCount"] = cols,
                        ["values"] = Normalize2D(ActionRouter.ConvertResult(range.Value2)),
                    });
            }
            catch (BridgeException bex) { return Protocol.Fail(req.Id, bex.Message, bex.Code); }
            catch (COMException com) { return ActionRouter.MapComError(req.Id, com, "Failed to read table"); }
        }

/// <summary>Read_selection {} → { address, rowCount, columnCount, values }.</summary>
        public static Dictionary<string, object> ReadSelection(Excel.Application app, BridgeRequest req)
        {
            try
            {
                var range = app.Selection as Excel.Range;
                if (range == null)
                {
                    return Protocol.Fail(req.Id,
                        "Failed to read selection: the current selection is not a range of cells",
                        "INVALID_SELECTION");
                }

                var rows = (int)range.Rows.Count;
                var cols = (int)range.Columns.Count;
                var address = FriendlyAddress(range);

                return Protocol.Ok(
                    req.Id,
                    "Successfully read selection (" + (rows * cols) + " cell(s) from " + address + ")",
                    new Dictionary<string, object>
                    {
                        ["address"] = address,
                        ["rowCount"] = rows,
                        ["columnCount"] = cols,
                        ["values"] = Normalize2D(ActionRouter.ConvertResult(range.Value2)),
                    });
            }
            catch (COMException com) { return ActionRouter.MapComError(req.Id, com, "Failed to read selection"); }
        }

        // ==================================================================
        // Writes
        // ==================================================================

/// <summary>Write_range { sheet?, range, values } → { address, rowCount, columnCount }.</summary>
        public static Dictionary<string, object> WriteRange(Excel.Application app, BridgeRequest req)
        {
            try
            {
                var rangeAddress = Protocol.GetString(req.Params, "range");
                if (string.IsNullOrWhiteSpace(rangeAddress))
                    return Protocol.Fail(req.Id, "Missing parameter: range", "INVALID_PARAMS");

                object rawValues;
                if (req.Params == null ||
                    !req.Params.TryGetValue("values", out rawValues) ||
                    !(rawValues is IList) || ((IList)rawValues).Count == 0)
                {
                    return Protocol.Fail(req.Id,
                        "Missing or invalid parameter: values (must be a non-empty 2D array)",
                        "INVALID_PARAMS");
                }

                int rows, cols;
                var matrix = ToMatrix(rawValues, out rows, out cols);

                var sheet = ResolveSheet(app, Protocol.GetString(req.Params, "sheet"));
                var topLeft = (Excel.Range)ResolveRange(app, sheet, rangeAddress).Cells[1, 1];
                var target = topLeft.Resize[rows, cols];
                target.Value2 = matrix;

                var address = FriendlyAddress(target);
                return Protocol.Ok(
                    req.Id,
                    "Successfully wrote " + rows + "x" + cols + " cells to " + address,
                    new Dictionary<string, object>
                    {
                        ["address"] = address,
                        ["rowCount"] = rows,
                        ["columnCount"] = cols,
                    });
            }
            catch (BridgeException bex) { return Protocol.Fail(req.Id, bex.Message, bex.Code); }
            catch (COMException com) { return ActionRouter.MapComError(req.Id, com, "Failed to write range"); }
        }

/// <summary>Write_formula { sheet?, cell, formula } → { address, formula }.</summary>
        public static Dictionary<string, object> WriteFormula(Excel.Application app, BridgeRequest req)
        {
            try
            {
                var cell = Protocol.GetString(req.Params, "cell");
                if (string.IsNullOrWhiteSpace(cell))
                    return Protocol.Fail(req.Id, "Missing parameter: cell", "INVALID_PARAMS");

                var formula = Protocol.GetString(req.Params, "formula");
                if (string.IsNullOrWhiteSpace(formula))
                    return Protocol.Fail(req.Id, "Missing parameter: formula", "INVALID_PARAMS");

                if (!formula.StartsWith("=", StringComparison.Ordinal))
                    formula = "=" + formula;

                var sheet = ResolveSheet(app, Protocol.GetString(req.Params, "sheet"));
                var range = ResolveRange(app, sheet, cell);
                SetFormula(range, formula);

                var address = FriendlyAddress(range);
                return Protocol.Ok(
                    req.Id,
                    "Successfully wrote formula '" + formula + "' to " + address,
                    new Dictionary<string, object>
                    {
                        ["address"] = address,
                        ["formula"] = formula,
                    });
            }
            catch (BridgeException bex) { return Protocol.Fail(req.Id, bex.Message, bex.Code); }
            catch (COMException com) { return ActionRouter.MapComError(req.Id, com, "Failed to write formula"); }
        }

        // ==================================================================
        // Sheets
        // ==================================================================

/// <summary>Create_sheet { name?</summary>
        public static Dictionary<string, object> CreateSheet(Excel.Application app, BridgeRequest req)
        {
            try
            {
                var wb = RequireWorkbook(app);
                var name = Protocol.GetString(req.Params, "name");

                // Office.js adds at the end; without a name Excel auto-names.
                var newSheet = (Excel.Worksheet)wb.Worksheets.Add(
                    Type.Missing, wb.Worksheets[wb.Worksheets.Count]);

                if (!string.IsNullOrWhiteSpace(name))
                {
                    // Throws COMException on duplicates — mirrors Office.js.
                    newSheet.Name = name.Trim();
                }

                return Protocol.Ok(
                    req.Id,
                    "Successfully created worksheet '" + newSheet.Name + "'",
                    new Dictionary<string, object> { ["name"] = newSheet.Name });
            }
            catch (BridgeException bex) { return Protocol.Fail(req.Id, bex.Message, bex.Code); }
            catch (COMException com) { return ActionRouter.MapComError(req.Id, com, "Failed to create sheet"); }
        }

/// <summary>Rename_sheet { currentName?, newName } → { oldName, newName }.</summary>
        public static Dictionary<string, object> RenameSheet(Excel.Application app, BridgeRequest req)
        {
            try
            {
                var newName = Protocol.GetString(req.Params, "newName");
                if (string.IsNullOrWhiteSpace(newName))
                    return Protocol.Fail(req.Id, "Missing parameter: newName", "INVALID_PARAMS");

                var sheet = ResolveSheet(app, Protocol.GetString(req.Params, "currentName"));
                var oldName = sheet.Name;
                sheet.Name = newName.Trim();

                return Protocol.Ok(
                    req.Id,
                    "Successfully renamed worksheet '" + oldName + "' to '" + newName.Trim() + "'",
                    new Dictionary<string, object>
                    {
                        ["oldName"] = oldName,
                        ["newName"] = newName.Trim(),
                    });
            }
            catch (BridgeException bex) { return Protocol.Fail(req.Id, bex.Message, bex.Code); }
            catch (COMException com) { return ActionRouter.MapComError(req.Id, com, "Failed to rename sheet"); }
        }

/// <summary>Delete_sheet { name } → { name }.</summary>
        public static Dictionary<string, object> DeleteSheet(Excel.Application app, BridgeRequest req)
        {
            try
            {
                var name = Protocol.GetString(req.Params, "name");
                if (string.IsNullOrWhiteSpace(name))
                    return Protocol.Fail(req.Id, "Missing parameter: name", "INVALID_PARAMS");

                var sheet = ResolveSheet(app, name);

                var alerts = app.DisplayAlerts;
                try
                {
                    app.DisplayAlerts = false;
                    sheet.Delete();
                }
                finally
                {
                    app.DisplayAlerts = alerts;
                }

                return Protocol.Ok(
                    req.Id,
                    "Successfully deleted worksheet '" + name + "'",
                    new Dictionary<string, object> { ["name"] = name });
            }
            catch (BridgeException bex) { return Protocol.Fail(req.Id, bex.Message, bex.Code); }
            catch (COMException com) { return ActionRouter.MapComError(req.Id, com, "Failed to delete sheet"); }
        }

        // ==================================================================
        // Formatting
        // ==================================================================

/// <summary>Format_range { sheet?, range, bold?, italic?, fill?, font_color?, font_size?, number_format?</summary>
        public static Dictionary<string, object> FormatRange(Excel.Application app, BridgeRequest req)
        {
            try
            {
                var rangeAddress = Protocol.GetString(req.Params, "range");
                if (string.IsNullOrWhiteSpace(rangeAddress))
                    return Protocol.Fail(req.Id, "Missing parameter: range", "INVALID_PARAMS");

                var sheet = ResolveSheet(app, Protocol.GetString(req.Params, "sheet"));
                var range = ResolveRange(app, sheet, rangeAddress);

                var applied = new List<object>();

                if (HasKey(req.Params, "bold"))
                {
                    var v = Protocol.GetBool(req.Params, "bold");
                    range.Font.Bold = v;
                    applied.Add("bold=" + (v ? "true" : "false"));
                }
                if (HasKey(req.Params, "italic"))
                {
                    var v = Protocol.GetBool(req.Params, "italic");
                    range.Font.Italic = v;
                    applied.Add("italic=" + (v ? "true" : "false"));
                }
                var fill = Protocol.GetString(req.Params, "fill");
                if (fill != null)
                {
                    var color = ParseOleColor(fill);
                    if (color.HasValue)
                    {
                        range.Interior.Color = color.Value;
                        applied.Add("fill=" + fill);
                    }
                }
                var fontColor = Protocol.GetString(req.Params, "font_color");
                if (fontColor != null)
                {
                    var color = ParseOleColor(fontColor);
                    if (color.HasValue)
                    {
                        range.Font.Color = color.Value;
                        applied.Add("font_color=" + fontColor);
                    }
                }
                var fontSize = Protocol.GetDouble(req.Params, "font_size");
                if (fontSize.HasValue)
                {
                    range.Font.Size = fontSize.Value;
                    applied.Add("font_size=" + fontSize.Value);
                }
                var numberFormat = Protocol.GetString(req.Params, "number_format");
                if (numberFormat != null)
                {
                    range.NumberFormat = numberFormat;
                    applied.Add("number_format=" + numberFormat);
                }

                var changes = applied.Count > 0 ? string.Join(", ", ToStringArray(applied)) : "no visible changes";
                var address = FriendlyAddress(range);

                return Protocol.Ok(
                    req.Id,
                    "Successfully formatted range " + address + " (" + changes + ")",
                    new Dictionary<string, object>
                    {
                        ["address"] = address,
                        ["formatted"] = applied,
                    });
            }
            catch (BridgeException bex) { return Protocol.Fail(req.Id, bex.Message, bex.Code); }
            catch (COMException com) { return ActionRouter.MapComError(req.Id, com, "Failed to format range"); }
        }

/// <summary>Auto_fit { sheet?, range } → { address }.</summary>
        public static Dictionary<string, object> AutoFit(Excel.Application app, BridgeRequest req)
        {
            try
            {
                var rangeAddress = Protocol.GetString(req.Params, "range");
                if (string.IsNullOrWhiteSpace(rangeAddress))
                    return Protocol.Fail(req.Id, "Missing parameter: range", "INVALID_PARAMS");

                var sheet = ResolveSheet(app, Protocol.GetString(req.Params, "sheet"));
                var range = ResolveRange(app, sheet, rangeAddress);

                range.EntireColumn.AutoFit();
                range.EntireRow.AutoFit();

                var address = FriendlyAddress(range);
                return Protocol.Ok(
                    req.Id,
                    "Successfully auto-fitted columns and rows for " + address,
                    new Dictionary<string, object> { ["address"] = address });
            }
            catch (BridgeException bex) { return Protocol.Fail(req.Id, bex.Message, bex.Code); }
            catch (COMException com) { return ActionRouter.MapComError(req.Id, com, "Failed to auto-fit range"); }
        }

        // ==================================================================
        // Tables
        // ==================================================================

/// <summary>Create_table { sheet?, range, name?, has_headers?</summary>
        public static Dictionary<string, object> CreateTable(Excel.Application app, BridgeRequest req)
        {
            try
            {
                var rangeAddress = Protocol.GetString(req.Params, "range");
                if (string.IsNullOrWhiteSpace(rangeAddress))
                    return Protocol.Fail(req.Id, "Missing parameter: range", "INVALID_PARAMS");

                var tableName = Protocol.GetString(req.Params, "name");
                // Office.js: has_headers !== false → default true.
                var hasHeaders = !HasKey(req.Params, "has_headers") ||
                    Protocol.GetBool(req.Params, "has_headers");

                var sheet = ResolveSheet(app, Protocol.GetString(req.Params, "sheet"));
                var range = ResolveRange(app, sheet, rangeAddress);

                var table = sheet.ListObjects.Add(
                    Excel.XlListObjectSourceType.xlSrcRange,
                    range,
                    Type.Missing,
                    hasHeaders ? Excel.XlYesNoGuess.xlYes : Excel.XlYesNoGuess.xlNo);

                if (!string.IsNullOrWhiteSpace(tableName))
                {
                    // Throws COMException on duplicates — mirrors Office.js.
                    table.Name = tableName.Trim();
                }

                return Protocol.Ok(
                    req.Id,
                    "Successfully created table '" + table.Name + "' at " + FriendlyAddress(table.Range),
                    new Dictionary<string, object>
                    {
                        ["tableName"] = table.Name,
                        ["address"] = FriendlyAddress(table.Range),
                    });
            }
            catch (BridgeException bex) { return Protocol.Fail(req.Id, bex.Message, bex.Code); }
            catch (COMException com) { return ActionRouter.MapComError(req.Id, com, "Failed to create table"); }
        }

/// <summary>Add_table_row { sheet?, tableName, values } → { tableName, rowsAdded }.</summary>
        public static Dictionary<string, object> AddTableRow(Excel.Application app, BridgeRequest req)
        {
            try
            {
                var tableName = Protocol.GetString(req.Params, "tableName");
                if (string.IsNullOrWhiteSpace(tableName))
                    return Protocol.Fail(req.Id, "Missing parameter: tableName", "INVALID_PARAMS");

                object rawValues;
                if (req.Params == null ||
                    !req.Params.TryGetValue("values", out rawValues) || rawValues == null)
                {
                    return Protocol.Fail(req.Id, "Missing parameter: values", "INVALID_PARAMS");
                }

                // Office.js: a flat array means a single row.
                int rows, cols;
                var matrix = ToMatrix(rawValues, out rows, out cols);

                var table = ResolveTable(app, Protocol.GetString(req.Params, "sheet"), tableName);

                for (var r = 0; r < rows; r++)
                {
                    var listRow = table.ListRows.Add();
                    var rowMatrix = new object[1, cols];
                    for (var c = 0; c < cols; c++) rowMatrix[0, c] = matrix[r, c];
                    listRow.Range.Value2 = rowMatrix;
                }

                return Protocol.Ok(
                    req.Id,
                    "Successfully added " + rows + " row(s) to table '" + tableName + "'",
                    new Dictionary<string, object>
                    {
                        ["tableName"] = tableName,
                        ["rowsAdded"] = rows,
                    });
            }
            catch (BridgeException bex) { return Protocol.Fail(req.Id, bex.Message, bex.Code); }
            catch (COMException com) { return ActionRouter.MapComError(req.Id, com, "Failed to add rows to table"); }
        }

/// <summary>Sort_table { sheet?, tableName, column, ascending?</summary>
        public static Dictionary<string, object> SortTable(Excel.Application app, BridgeRequest req)
        {
            try
            {
                var tableName = Protocol.GetString(req.Params, "tableName");
                if (string.IsNullOrWhiteSpace(tableName))
                    return Protocol.Fail(req.Id, "Missing parameter: tableName", "INVALID_PARAMS");

                object columnRaw = null;
                if (req.Params != null) req.Params.TryGetValue("column", out columnRaw);
                if (columnRaw == null)
                {
                    return Protocol.Fail(req.Id,
                        "Missing parameter: column (name or index)", "INVALID_PARAMS");
                }

                var ascending = !HasKey(req.Params, "ascending") ||
                    Protocol.GetBool(req.Params, "ascending");

                var table = ResolveTable(app, Protocol.GetString(req.Params, "sheet"), tableName);

                // Column by name (case-insensitive) or 0-based index (Office.js parity).
                Excel.ListColumn column = null;
                var columnName = columnRaw as string;
                var available = new List<string>();
                foreach (Excel.ListColumn lc in table.ListColumns) available.Add(lc.Name);

                if (columnName != null)
                {
                    foreach (Excel.ListColumn lc in table.ListColumns)
                    {
                        if (string.Equals(lc.Name, columnName.Trim(), StringComparison.OrdinalIgnoreCase))
                        {
                            column = lc;
                            break;
                        }
                    }
                    if (column == null)
                    {
                        return Protocol.Fail(req.Id,
                            "Column '" + columnName + "' not found in table '" + tableName +
                            "' (available: " + string.Join(", ", available.ToArray()) + ")",
                            "INVALID_PARAMS");
                    }
                }
                else
                {
                    var index = Convert.ToInt32(columnRaw); // JSON number; 0-based
                    if (index < 0 || index >= available.Count)
                    {
                        return Protocol.Fail(req.Id,
                            "Column index " + index + " out of range in table '" + tableName +
                            "' (0.." + (available.Count - 1) + ")",
                            "INVALID_PARAMS");
                    }
                    column = table.ListColumns[index + 1]; // COM is 1-based
                }

                var sort = table.Sort;
                sort.SortFields.Clear();
                var keyRange = column.DataBodyRange ?? column.Range;
                sort.SortFields.Add(
                    keyRange,
                    Excel.XlSortOn.xlSortOnValues,
                    ascending ? Excel.XlSortOrder.xlAscending : Excel.XlSortOrder.xlDescending);
                sort.Header = Excel.XlYesNoGuess.xlYes;
                sort.Apply();

                return Protocol.Ok(
                    req.Id,
                    "Successfully sorted table '" + tableName + "' by column '" + column.Name +
                    "' (" + (ascending ? "ascending" : "descending") + ")",
                    new Dictionary<string, object>
                    {
                        ["tableName"] = tableName,
                        ["sortedBy"] = column.Name,
                        ["ascending"] = ascending,
                    });
            }
            catch (BridgeException bex) { return Protocol.Fail(req.Id, bex.Message, bex.Code); }
            catch (COMException com) { return ActionRouter.MapComError(req.Id, com, "Failed to sort table"); }
        }

        // ==================================================================
        // Charts
        // ==================================================================

/// <summary>Create_chart { sheet?, type, data_range, title?, position?</summary>
        public static Dictionary<string, object> CreateChart(Excel.Application app, BridgeRequest req)
        {
            try
            {
                var typeStr = Protocol.GetString(req.Params, "type");
                if (string.IsNullOrWhiteSpace(typeStr))
                    return Protocol.Fail(req.Id, "Missing parameter: type", "INVALID_PARAMS");

                var dataRangeAddress = Protocol.GetString(req.Params, "data_range");
                if (string.IsNullOrWhiteSpace(dataRangeAddress))
                    return Protocol.Fail(req.Id, "Missing parameter: data_range", "INVALID_PARAMS");

                var targetSheet = ResolveSheet(app, Protocol.GetString(req.Params, "sheet"));

                // data_range may carry a sheet prefix ("Sales!A1:B5").
                var dataSheet = targetSheet;
                var dataAddr = dataRangeAddress;
                var bang = dataRangeAddress.IndexOf('!');
                if (bang > 0)
                {
                    var prefix = dataRangeAddress.Substring(0, bang).Trim('\'');
                    dataSheet = ResolveSheet(app, prefix);
                    dataAddr = dataRangeAddress.Substring(bang + 1);
                }
                var dataRange = dataSheet.Range[dataAddr];

                // Anchor: explicit "H1:N15" position, or just right of the used range.
                double left, top, width = 480, height = 288;
                var position = Protocol.GetString(req.Params, "position");
                if (!string.IsNullOrWhiteSpace(position))
                {
                    try
                    {
                        var parts = position.Split(':');
                        var startRef = parts[0];
                        var endRef = parts.Length > 1 ? parts[1] : parts[0];
                        var b1 = startRef.IndexOf('!'); if (b1 >= 0) startRef = startRef.Substring(b1 + 1);
                        var b2 = endRef.IndexOf('!'); if (b2 >= 0) endRef = endRef.Substring(b2 + 1);
                        var startCell = targetSheet.Range[startRef];
                        var endCell = targetSheet.Range[endRef];
                        left = (double)startCell.Left;
                        top = (double)startCell.Top;
                        width = Math.Max(50, (double)endCell.Left + (double)endCell.Width - left);
                        height = Math.Max(50, (double)endCell.Top + (double)endCell.Height - top);
                    }
                    catch (COMException)
                    {
                        // Fall back to default placement (Office.js behavior).
                        DefaultAnchor(targetSheet, out left, out top);
                    }
                }
                else
                {
                    DefaultAnchor(targetSheet, out left, out top);
                }

                var chartObjects = (Excel.ChartObjects)targetSheet.ChartObjects();
                var chartObject = chartObjects.Add(left, top, width, height);
                var chart = chartObject.Chart;
                chart.ChartType = MapChartType(typeStr);
                chart.SetSourceData(dataRange);

                var title = Protocol.GetString(req.Params, "title");
                if (!string.IsNullOrEmpty(title))
                {
                    chart.HasTitle = true;
                    chart.ChartTitle.Text = title;
                }

                return Protocol.Ok(
                    req.Id,
                    "Successfully created '" + typeStr + "' chart named '" + chartObject.Name +
                    "' using data " + dataRangeAddress,
                    new Dictionary<string, object>
                    {
                        ["chartName"] = chartObject.Name,
                        ["type"] = typeStr,
                    });
            }
            catch (BridgeException bex) { return Protocol.Fail(req.Id, bex.Message, bex.Code); }
            catch (COMException com) { return ActionRouter.MapComError(req.Id, com, "Failed to create chart"); }
        }

/// <summary>Configure_chart { sheet?, chartName, title?, legend?</summary>
        public static Dictionary<string, object> ConfigureChart(Excel.Application app, BridgeRequest req)
        {
            try
            {
                var chartName = Protocol.GetString(req.Params, "chartName");
                if (string.IsNullOrWhiteSpace(chartName))
                    return Protocol.Fail(req.Id, "Missing parameter: chartName", "INVALID_PARAMS");

                var sheet = ResolveSheet(app, Protocol.GetString(req.Params, "sheet"));

                Excel.ChartObject found = null;
                foreach (Excel.ChartObject co in sheet.ChartObjects())
                {
                    if (string.Equals(co.Name, chartName, StringComparison.OrdinalIgnoreCase))
                    {
                        found = co;
                        break;
                    }
                }
                if (found == null)
                {
                    return Protocol.Fail(req.Id,
                        "Chart '" + chartName + "' not found on sheet '" + sheet.Name + "'",
                        "INVALID_PARAMS");
                }

                var chart = found.Chart;
                var parts = new List<string>();

                var title = Protocol.GetString(req.Params, "title");
                if (title != null)
                {
                    chart.HasTitle = true;
                    chart.ChartTitle.Text = title;
                    parts.Add("title='" + title + "'");
                }
                if (HasKey(req.Params, "legend"))
                {
                    var legend = Protocol.GetBool(req.Params, "legend");
                    chart.HasLegend = legend;
                    parts.Add("legend=" + (legend ? "true" : "false"));
                }

                return Protocol.Ok(
                    req.Id,
                    "Successfully configured chart '" + chartName + "' (" +
                    string.Join(", ", parts.ToArray()) + ")",
                    new Dictionary<string, object> { ["chartName"] = chartName });
            }
            catch (BridgeException bex) { return Protocol.Fail(req.Id, bex.Message, bex.Code); }
            catch (COMException com) { return ActionRouter.MapComError(req.Id, com, "Failed to configure chart"); }
        }

        // ==================================================================
        // Calculation
        // ==================================================================

/// <summary>Calculate {} → force recalculation.</summary>
        public static Dictionary<string, object> Calculate(Excel.Application app, BridgeRequest req)
        {
            try
            {
                app.Calculate();
                return Protocol.Ok(req.Id, "Successfully recalculated the workbook");
            }
            catch (COMException com) { return ActionRouter.MapComError(req.Id, com, "Failed to recalculate"); }
        }

        // ==================================================================
        // Internal context readers (not agent-facing; used by the UI shell)
        // ==================================================================

/// <summary>Read_workbook_context {} → { activeSheet, sheetNames, selectionAddress }.</summary>
        public static Dictionary<string, object> ReadWorkbookContext(Excel.Application app, BridgeRequest req)
        {
            try
            {
                var wb = RequireWorkbook(app);

                var activeSheet = "unknown";
                var ws = app.ActiveSheet as Excel.Worksheet;
                if (ws != null) activeSheet = ws.Name;

                var sheetNames = new List<object>();
                foreach (Excel.Worksheet s in wb.Worksheets) sheetNames.Add(s.Name);

                var selectionAddress = "unknown";
                try
                {
                    var sel = app.Selection as Excel.Range;
                    if (sel != null) selectionAddress = FriendlyAddress(sel);
                }
                catch (COMException)
                {
                    // Cell-edit mode etc. — leave "unknown".
                }

                return Protocol.Ok(
                    req.Id,
                    "Workbook context",
                    new Dictionary<string, object>
                    {
                        ["activeSheet"] = activeSheet,
                        ["sheetNames"] = sheetNames,
                        ["selectionAddress"] = selectionAddress,
                    });
            }
            catch (BridgeException bex) { return Protocol.Fail(req.Id, bex.Message, bex.Code); }
            catch (COMException com) { return ActionRouter.MapComError(req.Id, com, "Failed to read workbook context"); }
        }

/// <summary>Read_workbook_snapshot { maxRows?</summary>
        public static Dictionary<string, object> ReadWorkbookSnapshot(Excel.Application app, BridgeRequest req)
        {
            try
            {
                var wb = RequireWorkbook(app);
                var sheet = app.ActiveSheet as Excel.Worksheet;
                if (sheet == null)
                {
                    return Protocol.Ok(req.Id, "Snapshot", new Dictionary<string, object>
                    {
                        ["text"] = "[Active sheet context unavailable]",
                    });
                }

                var maxRows = 100;
                var maxRowsParam = Protocol.GetDouble(req.Params, "maxRows");
                if (maxRowsParam.HasValue && maxRowsParam.Value > 0) maxRows = (int)maxRowsParam.Value;

                Excel.Range used;
                try { used = sheet.UsedRange; }
                catch (COMException)
                {
                    return Protocol.Ok(req.Id, "Snapshot", new Dictionary<string, object>
                    {
                        ["text"] = "[Active sheet \"" + sheet.Name + "\" is empty.]",
                    });
                }

                var rowCount = (int)used.Rows.Count;
                var colCount = (int)used.Columns.Count;

                double nonEmpty = 0;
                try { nonEmpty = app.WorksheetFunction.CountA(used); } catch { /* tolerate */ }
                if (nonEmpty == 0)
                {
                    return Protocol.Ok(req.Id, "Snapshot", new Dictionary<string, object>
                    {
                        ["text"] = "[Active sheet \"" + sheet.Name + "\" is empty.]",
                    });
                }

                var values = Normalize2D(ActionRouter.ConvertResult(used.Value2));
                var truncated = values.Count > maxRows;
                var sample = truncated ? values.GetRange(0, maxRows) : values;

                var note = "[Active sheet \"" + sheet.Name + "\" has " + rowCount +
                    " rows, " + colCount + " columns: " + FriendlyAddress(used) + ".";
                note += "\nData values:\n" + Protocol.Serialize(sample);
                if (truncated)
                {
                    note += "\n(Truncated: showing first " + maxRows +
                        " rows out of " + rowCount + " total)";
                }
                note += "\n]";

                return Protocol.Ok(req.Id, "Snapshot", new Dictionary<string, object>
                {
                    ["text"] = note,
                });
            }
            catch (BridgeException bex) { return Protocol.Fail(req.Id, bex.Message, bex.Code); }
            catch (COMException com) { return ActionRouter.MapComError(req.Id, com, "Failed to read workbook snapshot"); }
        }

        // ==================================================================
        // Shared helpers
        // ==================================================================

        private static Excel.Workbook RequireWorkbook(Excel.Application app)
        {
            Excel.Workbook wb = null;
            try { wb = app.ActiveWorkbook; } catch (COMException) { /* fall through */ }
            if (wb == null)
                throw new BridgeException("NO_ACTIVE_WORKBOOK", "No active workbook in this Excel instance");
            return wb;
        }

/// <summary>Resolve a worksheet by name, or the active sheet when omitted.</summary>
        private static Excel.Worksheet ResolveSheet(Excel.Application app, string name)
        {
            var wb = RequireWorkbook(app);

            if (string.IsNullOrWhiteSpace(name))
            {
                var active = app.ActiveSheet as Excel.Worksheet;
                if (active == null)
                    throw new BridgeException("NO_ACTIVE_SHEET", "The active sheet is not a worksheet");
                return active;
            }

            try
            {
                return (Excel.Worksheet)wb.Worksheets[name.Trim()];
            }
            catch (COMException)
            {
                throw new BridgeException("SHEET_NOT_FOUND", "Worksheet '" + name + "' not found");
            }
        }

/// <summary>Resolve a range on a sheet.</summary>
        private static Excel.Range ResolveRange(Excel.Application app, Excel.Worksheet sheet, string address)
        {
            var addr = (address ?? string.Empty).Trim();
            var bang = addr.IndexOf('!');
            if (bang > 0)
            {
                var prefix = addr.Substring(0, bang).Trim('\'');
                sheet = ResolveSheet(app, prefix);
                addr = addr.Substring(bang + 1);
            }

            try
            {
                return sheet.Range[addr];
            }
            catch (COMException)
            {
                throw new BridgeException("INVALID_RANGE", "Invalid range address: '" + address + "'");
            }
        }

/// <summary>Find a table by name — on the given sheet, or across the workbook.</summary>
        private static Excel.ListObject ResolveTable(Excel.Application app, string sheetName, string tableName)
        {
            var wanted = tableName.Trim();

            if (!string.IsNullOrWhiteSpace(sheetName))
            {
                var sheet = ResolveSheet(app, sheetName);
                try { return sheet.ListObjects[wanted]; }
                catch (COMException)
                {
                    throw new BridgeException("TABLE_NOT_FOUND",
                        "Table '" + tableName + "' not found on sheet '" + sheet.Name + "'");
                }
            }

            var wb = RequireWorkbook(app);
            foreach (Excel.Worksheet ws in wb.Worksheets)
            {
                foreach (Excel.ListObject lo in ws.ListObjects)
                {
                    if (string.Equals(lo.Name, wanted, StringComparison.OrdinalIgnoreCase))
                        return lo;
                }
            }
            throw new BridgeException("TABLE_NOT_FOUND", "Table '" + tableName + "' not found in the workbook");
        }

/// <summary>Office.js-style address: "Sheet1!A1:C10" (relative refs).</summary>
        private static string FriendlyAddress(Excel.Range range)
        {
            var addr = range.get_Address(false, false);
            var ws = range.Parent as Excel.Worksheet;
            return ws != null ? ws.Name + "!" + addr : addr;
        }

/// <summary>Office.js `values` is always a 2D array — wrap scalars as [[v]].</summary>
        private static List<object> Normalize2D(object converted)
        {
            var rows = converted as List<object>;
            if (rows != null) return rows;
            return new List<object> { new List<object> { converted } };
        }

/// <summary>Convert the params `values` payload (JSON array of arrays; a flat array means a single row) to a COM-safe object[,].</summary>
        private static object[,] ToMatrix(object raw, out int rows, out int cols)
        {
            var outer = raw as IList;
            if (outer == null || outer.Count == 0)
                throw new BridgeException("INVALID_PARAMS",
                    "Missing or invalid parameter: values (must be a non-empty 2D array)");

            // Flat array → single row (Office.js add_table_row parity).
            IList firstRow = outer[0] as IList;
            var rowLists = new List<IList>();
            if (firstRow == null)
            {
                rowLists.Add(outer);
            }
            else
            {
                foreach (var item in outer)
                {
                    var row = item as IList;
                    if (row == null)
                        throw new BridgeException("INVALID_PARAMS",
                            "values must be a 2D array (inconsistent row shapes)");
                    rowLists.Add(row);
                }
            }

            rows = rowLists.Count;
            cols = rowLists[0].Count;
            var matrix = new object[rows, cols];
            for (var r = 0; r < rows; r++)
            {
                for (var c = 0; c < cols && c < rowLists[r].Count; c++)
                {
                    matrix[r, c] = rowLists[r][c];
                }
            }
            return matrix;
        }

/// <summary>Formula2 (dynamic arrays) when available, else classic Formula.</summary>
        private static void SetFormula(Excel.Range range, string formula)
        {
            try
            {
                range.GetType().InvokeMember(
                    "Formula2",
                    System.Reflection.BindingFlags.SetProperty,
                    null, range, new object[] { formula });
            }
            catch
            {
                range.Formula = formula;
            }
        }

/// <summary>"#RRGGBB" / CSS color name → OLE_COLOR (R | G&lt;&lt;8 | B&lt;&lt;16).</summary>
        private static int? ParseOleColor(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            try
            {
                var c = System.Drawing.ColorTranslator.FromHtml(value.Trim());
                return c.R | (c.G << 8) | (c.B << 16);
            }
            catch
            {
                return null;
            }
        }

        private static Excel.XlChartType MapChartType(string type)
        {
            switch ((type ?? string.Empty).ToLowerInvariant().Trim())
            {
                case "bar": return Excel.XlChartType.xlBarClustered;
                case "bar_stacked": return Excel.XlChartType.xlBarStacked;
                case "column_stacked": return Excel.XlChartType.xlColumnStacked;
                case "line": return Excel.XlChartType.xlLine;
                case "line_markers": return Excel.XlChartType.xlLineMarkers;
                case "pie": return Excel.XlChartType.xlPie;
                case "pie_exploded": return Excel.XlChartType.xlPieExploded;
                case "doughnut": return Excel.XlChartType.xlDoughnut;
                case "area": return Excel.XlChartType.xlArea;
                case "area_stacked": return Excel.XlChartType.xlAreaStacked;
                case "scatter":
                case "xyscatter": return Excel.XlChartType.xlXYScatter;
                case "radar": return Excel.XlChartType.xlRadar;
                case "column":
                case "column_clustered":
                default: return Excel.XlChartType.xlColumnClustered;
            }
        }

/// <summary>Default chart anchor: one column right of the used range.</summary>
        private static void DefaultAnchor(Excel.Worksheet sheet, out double left, out double top)
        {
            try
            {
                var used = sheet.UsedRange;
                left = (double)used.Left + (double)used.Width + 20;
                top = (double)used.Top;
            }
            catch (COMException)
            {
                left = 200;
                top = 20;
            }
        }

        private static bool HasKey(IDictionary<string, object> parameters, string key)
        {
            return parameters != null && parameters.ContainsKey(key) && parameters[key] != null;
        }

        private static string[] ToStringArray(List<object> items)
        {
            var result = new string[items.Count];
            for (var i = 0; i < items.Count; i++) result[i] = Convert.ToString(items[i]);
            return result;
        }
    }
}
