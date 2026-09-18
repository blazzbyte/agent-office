using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Excel = Microsoft.Office.Interop.Excel;

namespace AgentOffice.Bridge
{
/// <summary>Advanced chart actions beyond Office.js.</summary>
    internal static class ChartActions
    {
        public static Dictionary<string, object> ConfigureChartPro(Excel.Application app, BridgeRequest req)
        {
            Excel.Worksheet sheet;
            Dictionary<string, object> error;
            if (!AnalysisActions.TryResolveSheet(app, Protocol.GetString(req.Params, "sheet"), req.Id, out sheet, out error))
            {
                return error;
            }

            Excel.Chart chart;
            if (!TryResolveChart(sheet, req, out chart, out error))
            {
                return error;
            }

            var applied = new List<string>();

            // --- Combo: per-series chart type.
            object seriesTypesParam;
            var seriesTypes = req.Params != null && req.Params.TryGetValue("seriesTypes", out seriesTypesParam)
                ? seriesTypesParam as IEnumerable
                : null;
            if (seriesTypes != null)
            {
                foreach (var item in seriesTypes)
                {
                    var spec = item as IDictionary<string, object>;
                    if (spec == null)
                    {
                        return Protocol.Fail(req.Id, "Each entry of 'seriesTypes' must be an object like { \"series\": 1, \"type\": \"line\" }", "INVALID_PARAMS");
                    }
                    Excel.Series series;
                    if (!TryResolveSeries(req.Id, chart, spec, out series, out error)) return error;

                    Excel.XlChartType chartType;
                    try
                    {
                        chartType = MapChartType(Protocol.GetString(spec, "type"));
                    }
                    catch (BridgeException bex)
                    {
                        return Protocol.Fail(req.Id, bex.Message, bex.Code);
                    }
                    try
                    {
                        series.ChartType = chartType;
                        applied.Add("series " + SeriesLabel(series) + " → " + chartType);
                    }
                    catch (COMException com)
                    {
                        return ActionRouter.MapComError(req.Id, com, "Could not change the chart type of series " + SeriesLabel(series));
                    }
                }
            }

            // --- Secondary axis.
            var secondary = Protocol.GetStringList(req.Params, "secondaryAxis");
            if (secondary != null)
            {
                foreach (var s in secondary)
                {
                    Excel.Series series;
                    if (!TryResolveSeries(req.Id, chart, s, out series, out error)) return error;
                    try
                    {
                        series.AxisGroup = Excel.XlAxisGroup.xlSecondary;
                        applied.Add("series " + SeriesLabel(series) + " → secondary axis");
                    }
                    catch (COMException com)
                    {
                        return ActionRouter.MapComError(req.Id, com, "Could not move series " + SeriesLabel(series) + " to the secondary axis");
                    }
                }
            }

            // --- Trendlines.
            object trendlinesParam;
            var trendlines = req.Params != null && req.Params.TryGetValue("trendlines", out trendlinesParam)
                ? trendlinesParam as IEnumerable
                : null;
            if (trendlines != null)
            {
                foreach (var item in trendlines)
                {
                    var spec = item as IDictionary<string, object>;
                    if (spec == null)
                    {
                        return Protocol.Fail(req.Id, "Each entry of 'trendlines' must be an object like { \"series\": 1, \"type\": \"linear\" }", "INVALID_PARAMS");
                    }
                    Excel.Series series;
                    if (!TryResolveSeries(req.Id, chart, spec, out series, out error)) return error;

                    Excel.XlTrendlineType tlType;
                    try
                    {
                        tlType = MapTrendlineType(Protocol.GetString(spec, "type"));
                    }
                    catch (BridgeException bex)
                    {
                        return Protocol.Fail(req.Id, bex.Message, bex.Code);
                    }

                    var order = Protocol.GetDouble(spec, "order");                   // polynomial
                    var period = Protocol.GetDouble(spec, "period");                 // moving average
                    var forward = Protocol.GetDouble(spec, "forward");
                    var backward = Protocol.GetDouble(spec, "backward");
                    var displayEquation = Protocol.GetBool(spec, "displayEquation");
                    var displayRSquared = Protocol.GetBool(spec, "displayRSquared");
                    var tlName = Protocol.GetString(spec, "name");

                    try
                    {
                        // Minimal Add + property sets: passing the optional args
                        // positionally marshals them into the wrong slots (a linear
                        // trendline came out polynomial — verified live).
                        var tl = (Excel.Trendline)series.Trendlines().Add(tlType);
                        if (order.HasValue) tl.Order = (int)order.Value;
                        if (period.HasValue) tl.Period = (int)period.Value;
                        if (forward.HasValue) tl.Forward = (int)forward.Value;
                        if (backward.HasValue) tl.Backward = (int)backward.Value;
                        if (displayEquation) tl.DisplayEquation = true;
                        if (displayRSquared) tl.DisplayRSquared = true;
                        if (!string.IsNullOrWhiteSpace(tlName))
                        {
                            try { tl.Name = tlName; } catch (COMException) { /* custom names can collide — non-fatal */ }
                        }
                        applied.Add("trendline " + tlType + " on series " + SeriesLabel(series));
                    }
                    catch (COMException com)
                    {
                        return ActionRouter.MapComError(req.Id, com, "Could not add a trendline to series " + SeriesLabel(series));
                    }
                }
            }

            if (applied.Count == 0)
            {
                return Protocol.Fail(
                    req.Id,
                    "Nothing to do — pass at least one of 'seriesTypes', 'secondaryAxis' or 'trendlines'",
                    "INVALID_PARAMS");
            }

            return Protocol.Ok(
                req.Id,
                "Chart configured: " + string.Join("; ", applied),
                new Dictionary<string, object>
                {
                    ["sheet"] = sheet.Name,
                    ["applied"] = applied,
                });
        }

        // ------------------------------------------------------------------
        // Resolution helpers
        // ------------------------------------------------------------------

        private static bool TryResolveChart(
            Excel.Worksheet sheet, BridgeRequest req, out Excel.Chart chart, out Dictionary<string, object> error)
        {
            chart = null;
            error = null;
            var chartParam = Protocol.GetString(req.Params, "chart");

            try
            {
                if (string.IsNullOrWhiteSpace(chartParam))
                {
                    var count = sheet.ChartObjects().Count;
                    if (count == 0)
                    {
                        error = Protocol.Fail(req.Id, "No charts on sheet '" + sheet.Name + "'", "INVALID_PARAMS");
                        return false;
                    }
                    if (count > 1)
                    {
                        error = Protocol.Fail(
                            req.Id,
                            "The sheet has " + count + " charts — pass 'chart' (name or 1-based index). Available: " + ChartNames(sheet),
                            "INVALID_PARAMS");
                        return false;
                    }
                    chart = ((Excel.ChartObject)sheet.ChartObjects(1)).Chart;
                    return true;
                }

                int index;
                if (int.TryParse(chartParam, out index))
                {
                    chart = ((Excel.ChartObject)sheet.ChartObjects(index)).Chart;
                }
                else
                {
                    chart = ((Excel.ChartObject)sheet.ChartObjects(chartParam)).Chart;
                }
                return chart != null;
            }
            catch (COMException)
            {
                error = Protocol.Fail(
                    req.Id,
                    "Chart not found on sheet '" + sheet.Name + "': " + chartParam + ". Available: " + ChartNames(sheet),
                    "INVALID_PARAMS");
                return false;
            }
        }

        private static string ChartNames(Excel.Worksheet sheet)
        {
            var names = new List<string>();
            try
            {
                var count = sheet.ChartObjects().Count;
                for (var i = 1; i <= count; i++)
                {
                    names.Add(i + "=\"" + ((Excel.ChartObject)sheet.ChartObjects(i)).Name + "\"");
                }
            }
            catch (COMException) { }
            return names.Count > 0 ? string.Join(", ", names) : "(none)";
        }

/// <summary>Series from a spec dictionary (key "series") or a bare string/int.</summary>
        private static bool TryResolveSeries(
            string id, Excel.Chart chart, IDictionary<string, object> spec, out Excel.Series series, out Dictionary<string, object> error)
        {
            object raw;
            spec.TryGetValue("series", out raw);
            return TryResolveSeries(id, chart, raw != null ? Convert.ToString(raw) : null, out series, out error);
        }

        private static bool TryResolveSeries(
            string id, Excel.Chart chart, string seriesRef, out Excel.Series series, out Dictionary<string, object> error)
        {
            series = null;
            error = null;
            if (string.IsNullOrWhiteSpace(seriesRef))
            {
                error = Protocol.Fail(id, "Missing 'series' (1-based index or series name)", "INVALID_PARAMS");
                return false;
            }

            try
            {
                int index;
                if (int.TryParse(seriesRef, out index))
                {
                    series = (Excel.Series)chart.SeriesCollection(index);
                }
                else
                {
                    foreach (Excel.Series s in chart.SeriesCollection())
                    {
                        if (string.Equals(s.Name, seriesRef, StringComparison.OrdinalIgnoreCase))
                        {
                            series = s;
                            break;
                        }
                    }
                }
            }
            catch (COMException) { }

            if (series == null)
            {
                error = Protocol.Fail(id, "Series not found: " + seriesRef + ". Available: " + SeriesNames(chart), "INVALID_PARAMS");
                return false;
            }
            return true;
        }

        private static string SeriesNames(Excel.Chart chart)
        {
            var names = new List<string>();
            try
            {
                var i = 1;
                foreach (Excel.Series s in chart.SeriesCollection())
                {
                    names.Add(i + "=\"" + s.Name + "\"");
                    i++;
                }
            }
            catch (COMException) { }
            return names.Count > 0 ? string.Join(", ", names) : "(none)";
        }

        private static string SeriesLabel(Excel.Series series)
        {
            try { return "\"" + series.Name + "\""; }
            catch { return "(unknown)"; }
        }

        // ------------------------------------------------------------------
        // Enum maps
        // ------------------------------------------------------------------

        private static Excel.XlChartType MapChartType(string type)
        {
            switch ((type ?? "").Trim().ToLowerInvariant())
            {
                case "line": return Excel.XlChartType.xlLine;
                case "linemarkers": case "line_markers": return Excel.XlChartType.xlLineMarkers;
                case "column": case "columnclustered": return Excel.XlChartType.xlColumnClustered;
                case "bar": case "barclustered": return Excel.XlChartType.xlBarClustered;
                case "area": return Excel.XlChartType.xlArea;
                case "pie": return Excel.XlChartType.xlPie;
                case "scatter": case "xyscatter": return Excel.XlChartType.xlXYScatter;
                default:
                    throw new BridgeException(
                        "INVALID_PARAMS",
                        "Unknown chart type '" + type + "' — use line|lineMarkers|column|bar|area|pie|scatter");
            }
        }

        private static Excel.XlTrendlineType MapTrendlineType(string type)
        {
            switch ((type ?? "").Trim().ToLowerInvariant())
            {
                case "linear": return Excel.XlTrendlineType.xlLinear;
                case "exponential": return Excel.XlTrendlineType.xlExponential;
                case "logarithmic": return Excel.XlTrendlineType.xlLogarithmic;
                case "polynomial": return Excel.XlTrendlineType.xlPolynomial;
                case "power": return Excel.XlTrendlineType.xlPower;
                case "movingaverage": case "moving_average": case "movingavg": return Excel.XlTrendlineType.xlMovingAvg;
                default:
                    throw new BridgeException(
                        "INVALID_PARAMS",
                        "Unknown trendline type '" + type + "' — use linear|exponential|logarithmic|polynomial|power|movingAverage");
            }
        }
    }
}
