using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Interop = Microsoft.Office.Interop.Word;

namespace AgentOffice.Bridge.Word
{
/// <summary>Word tool handlers — the Fase 9 toolset.</summary>
    internal static class WordActions
    {
/// <summary>Caps for snapshot/read output — COM paragraph walks are chatty.</summary>
        private const int MaxOutlineEntries = 50;
        private const int MaxSnapshotParagraphs = 500;
        private const int MaxReadParagraphs = 300;
        private const int MaxReadChars = 45000;

        // ------------------------------------------------------------------
        // read_document_snapshot — structure overview for [WORD_CONTEXT]
        // ------------------------------------------------------------------

        internal static Dictionary<string, object> ReadDocumentSnapshot(
            Interop.Application app, BridgeRequest req)
        {
            var doc = app.ActiveDocument;
            var sb = new StringBuilder();

            var words = doc.Content.ComputeStatistics(Interop.WdStatistic.wdStatisticWords);
            var paragraphs = doc.Paragraphs.Count;
            sb.Append("Document: ").Append(doc.Name)
              .Append(" | ").Append(words).Append(" words")
              .Append(" | ").Append(paragraphs).Append(" paragraphs");

            var selection = app.Selection;
            var selText = selection != null ? (selection.Text ?? string.Empty).Trim() : string.Empty;
            if (selText.Length > 0)
            {
                sb.Append(" | Selection: \"")
                  .Append(Truncate(selText.Replace("\r", " "), 80))
                  .Append("\"");
            }
            sb.AppendLine();

            // Outline: paragraphs whose outline level is not body text —
            // locale-safe (no style-name matching).
            var outline = 0;
            var walked = 0;
            foreach (Interop.Paragraph para in doc.Paragraphs)
            {
                if (++walked > MaxSnapshotParagraphs) break;
                var level = para.Format.OutlineLevel;
                if (level == Interop.WdOutlineLevel.wdOutlineLevelBodyText) continue;

                var text = (para.Range.Text ?? string.Empty)
                    .TrimEnd('\r', '\a').Trim();
                if (text.Length == 0) continue;

                var depth = (int)level; // wdOutlineLevel1..9 → 0..8
                sb.AppendLine(new string(' ', Math.Min(depth, 8) * 2) +
                    "H" + (depth + 1) + " " + Truncate(text, 100));
                if (++outline >= MaxOutlineEntries) break;
            }
            if (outline == 0) sb.AppendLine("(no headings — flat document)");

            return Protocol.Ok(req.Id, "Document snapshot captured",
                new Dictionary<string, object> { ["text"] = sb.ToString() });
        }

        // ------------------------------------------------------------------
        // read_text — numbered paragraphs, 1-based window
        // ------------------------------------------------------------------

        internal static Dictionary<string, object> ReadText(
            Interop.Application app, BridgeRequest req)
        {
            var doc = app.ActiveDocument;
            var total = doc.Paragraphs.Count;

            var start = (int?)Protocol.GetDouble(req.Params, "startParagraph") ?? 1;
            var count = (int?)Protocol.GetDouble(req.Params, "maxParagraphs") ?? 100;
            if (start < 1) start = 1;
            if (count < 1) count = 1;
            if (count > MaxReadParagraphs) count = MaxReadParagraphs;

            var sb = new StringBuilder();
            var end = Math.Min(total, start + count - 1);
            for (var i = start; i <= end; i++)
            {
                var text = (doc.Paragraphs[i].Range.Text ?? string.Empty)
                    .TrimEnd('\r', '\a');
                sb.Append('[').Append(i).Append("] ").AppendLine(text);
                if (sb.Length > MaxReadChars)
                {
                    sb.AppendLine("… (truncated — narrow the window)");
                    break;
                }
            }

            return Protocol.Ok(req.Id,
                "Read paragraphs " + start + "-" + end + " of " + total,
                new Dictionary<string, object>
                {
                    ["text"] = sb.ToString(),
                    ["totalParagraphs"] = total,
                });
        }

        // ------------------------------------------------------------------
        // insert_text — at end / start / replacing the selection
        // ------------------------------------------------------------------

        internal static Dictionary<string, object> InsertText(
            Interop.Application app, BridgeRequest req)
        {
            var text = Protocol.GetString(req.Params, "text");
            if (string.IsNullOrEmpty(text))
                return Protocol.Fail(req.Id, "Missing required param 'text'", "INVALID_PARAMS");
            text = NormalizeNewlines(text);

            var position = (Protocol.GetString(req.Params, "position") ?? "end")
                .Trim().ToLowerInvariant();
            var style = Protocol.GetString(req.Params, "style");

            var doc = app.ActiveDocument;
            Interop.Range range;
            if (position == "selection")
            {
                range = app.Selection.Range;
                range.Text = text; // replaces the selection
            }
            else
            {
                range = doc.Content;
                range.Collapse(position == "start"
                    ? Interop.WdCollapseDirection.wdCollapseStart
                    : Interop.WdCollapseDirection.wdCollapseEnd);
                range.InsertAfter(text);
            }

            if (!string.IsNullOrWhiteSpace(style))
            {
                var fail = TryApplyStyle(doc, range, style, req.Id);
                if (fail != null) return fail;
            }

            return Protocol.Ok(req.Id,
                "Inserted " + text.Length + " chars at " + position +
                (string.IsNullOrWhiteSpace(style) ? "" : " with style '" + style + "'"));
        }

        // ------------------------------------------------------------------
        // replace_text — find & replace (all by default)
        // ------------------------------------------------------------------

        internal static Dictionary<string, object> ReplaceText(
            Interop.Application app, BridgeRequest req)
        {
            var find = Protocol.GetString(req.Params, "find");
            var replace = Protocol.GetString(req.Params, "replace");
            if (string.IsNullOrEmpty(find))
                return Protocol.Fail(req.Id, "Missing required param 'find'", "INVALID_PARAMS");
            if (replace == null) replace = string.Empty;

            var matchCase = Protocol.GetBool(req.Params, "matchCase");
            var all = !req.Params.ContainsKey("all") || Protocol.GetBool(req.Params, "all");

            var doc = app.ActiveDocument;
            var finder = doc.Content.Find;
            finder.ClearFormatting();
            finder.Replacement.ClearFormatting();

            var found = finder.Execute(
                FindText: find,
                MatchCase: matchCase,
                ReplaceWith: NormalizeNewlines(replace),
                Replace: all
                    ? Interop.WdReplace.wdReplaceAll
                    : Interop.WdReplace.wdReplaceOne);

            return found
                ? Protocol.Ok(req.Id, all
                    ? "Replaced every occurrence of \"" + Truncate(find, 40) + "\""
                    : "Replaced the first occurrence of \"" + Truncate(find, 40) + "\"")
                : Protocol.Fail(req.Id,
                    "No occurrences of \"" + Truncate(find, 40) + "\" found",
                    "NOT_FOUND");
        }

        // ------------------------------------------------------------------
        // apply_style — to a paragraph window or the selection
        // ------------------------------------------------------------------

        internal static Dictionary<string, object> ApplyStyle(
            Interop.Application app, BridgeRequest req)
        {
            var style = Protocol.GetString(req.Params, "style");
            if (string.IsNullOrWhiteSpace(style))
                return Protocol.Fail(req.Id, "Missing required param 'style'", "INVALID_PARAMS");

            var doc = app.ActiveDocument;
            var start = (int?)Protocol.GetDouble(req.Params, "startParagraph");
            var end = (int?)Protocol.GetDouble(req.Params, "endParagraph");

            Interop.Range range;
            string target;
            if (start.HasValue)
            {
                var total = doc.Paragraphs.Count;
                var s = Math.Max(1, start.Value);
                var e = Math.Min(total, Math.Max(s, end ?? start.Value));
                range = doc.Range(
                    doc.Paragraphs[s].Range.Start,
                    doc.Paragraphs[e].Range.End);
                target = "paragraphs " + s + "-" + e;
            }
            else
            {
                range = app.Selection.Range;
                target = "the selection";
            }

            var fail = TryApplyStyle(doc, range, style, req.Id);
            if (fail != null) return fail;
            return Protocol.Ok(req.Id, "Applied style '" + style + "' to " + target);
        }

        // ------------------------------------------------------------------
        // insert_table — rows x cols, optional 2D data, optional header row
        // ------------------------------------------------------------------

        internal static Dictionary<string, object> InsertTable(
            Interop.Application app, BridgeRequest req)
        {
            var rows = (int?)Protocol.GetDouble(req.Params, "rows") ?? 0;
            var cols = (int?)Protocol.GetDouble(req.Params, "cols") ?? 0;
            if (rows < 1 || cols < 1)
                return Protocol.Fail(req.Id,
                    "Params 'rows' and 'cols' must be >= 1", "INVALID_PARAMS");
            if (rows > 100 || cols > 20)
                return Protocol.Fail(req.Id,
                    "Table too large (max 100 rows x 20 cols)", "INVALID_PARAMS");

            var position = (Protocol.GetString(req.Params, "position") ?? "end")
                .Trim().ToLowerInvariant();
            var headerRow = Protocol.GetBool(req.Params, "headerRow");

            var doc = app.ActiveDocument;
            Interop.Range range;
            if (position == "selection")
            {
                range = app.Selection.Range;
                range.Collapse(Interop.WdCollapseDirection.wdCollapseEnd);
            }
            else
            {
                range = doc.Content;
                range.Collapse(Interop.WdCollapseDirection.wdCollapseEnd);
            }

            var table = doc.Tables.Add(range, rows, cols);
            table.Borders.Enable = 1;

            object dataRaw;
            var data = req.Params.TryGetValue("data", out dataRaw)
                ? dataRaw as ArrayList : null;
            if (data != null)
            {
                for (var r = 0; r < Math.Min(rows, data.Count); r++)
                {
                    var row = data[r] as ArrayList;
                    if (row == null) continue;
                    for (var c = 0; c < Math.Min(cols, row.Count); c++)
                    {
                        table.Cell(r + 1, c + 1).Range.Text =
                            row[c] == null ? string.Empty : row[c].ToString();
                    }
                }
            }

            if (headerRow) table.Rows[1].Range.Bold = 1;

            return Protocol.Ok(req.Id,
                "Inserted a " + rows + "x" + cols + " table at " + position +
                (headerRow ? " (header row bolded)" : ""));
        }

        // ------------------------------------------------------------------
        // export_pdf — beside the document (or temp dir when unsaved)
        // ------------------------------------------------------------------

        internal static Dictionary<string, object> ExportPdf(
            Interop.Application app, BridgeRequest req)
        {
            var doc = app.ActiveDocument;
            // Explicit path > settings.json exportDir > Documents\AgentOffice.
            var path = ExportPath.Resolve(
                Protocol.GetString(req.Params, "path"),
                Path.GetFileNameWithoutExtension(doc.Name),
                ".pdf");

            doc.ExportAsFixedFormat(path, Interop.WdExportFormat.wdExportFormatPDF);
            return Protocol.Ok(req.Id, "Exported PDF to " + path,
                new Dictionary<string, object> { ["path"] = path });
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

/// <summary>Apply a style by name, mapping common English built-ins to WdBuiltinStyle first (locale-safe).</summary>
        private static Dictionary<string, object> TryApplyStyle(
            Interop.Document doc, Interop.Range range, string style, string id)
        {
            try
            {
                object builtin;
                if (BuiltinStyles.TryGetValue(NormalizeStyleName(style), out builtin))
                    range.set_Style(builtin);
                else
                    range.set_Style(style);
                return null;
            }
            catch (Exception)
            {
                return Protocol.Fail(id,
                    "Unknown style: '" + style + "' — use a built-in (Heading 1-3, Title, " +
                    "Subtitle, Quote, Normal) or an existing custom style name.",
                    "INVALID_PARAMS");
            }
        }

        private static readonly Dictionary<string, object> BuiltinStyles =
            new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["normal"] = Interop.WdBuiltinStyle.wdStyleNormal,
                ["title"] = Interop.WdBuiltinStyle.wdStyleTitle,
                ["subtitle"] = Interop.WdBuiltinStyle.wdStyleSubtitle,
                ["heading1"] = Interop.WdBuiltinStyle.wdStyleHeading1,
                ["heading2"] = Interop.WdBuiltinStyle.wdStyleHeading2,
                ["heading3"] = Interop.WdBuiltinStyle.wdStyleHeading3,
                ["heading4"] = Interop.WdBuiltinStyle.wdStyleHeading4,
                ["heading5"] = Interop.WdBuiltinStyle.wdStyleHeading5,
                ["heading6"] = Interop.WdBuiltinStyle.wdStyleHeading6,
                ["quote"] = Interop.WdBuiltinStyle.wdStyleQuote,
            };

        private static string NormalizeStyleName(string style)
        {
            return style.Replace(" ", string.Empty).Replace("-", string.Empty);
        }

/// <summary>Word COM wants \r for paragraphs; \n alone renders oddly.</summary>
        private static string NormalizeNewlines(string text)
        {
            return text.Replace("\r\n", "\n").Replace("\n", "\r");
        }

        private static string Truncate(string text, int max)
        {
            return text.Length <= max ? text : text.Substring(0, max - 1) + "…";
        }
    }
}
