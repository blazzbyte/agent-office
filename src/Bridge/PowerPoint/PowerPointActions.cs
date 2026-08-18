using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Office = Microsoft.Office.Core;
using Interop = Microsoft.Office.Interop.PowerPoint;

namespace AgentOffice.Bridge.PowerPoint
{
/// <summary>PowerPoint tool handlers — the Fase 10 toolset.</summary>
    internal static class PowerPointActions
    {
        private const int MaxSnapshotSlides = 100;
        private const int MaxShapeTextChars = 200;

        // ------------------------------------------------------------------
        // read_presentation_snapshot — deck overview for [DECK_CONTEXT]
        // ------------------------------------------------------------------

        internal static Dictionary<string, object> ReadPresentationSnapshot(
            Interop.Application app, BridgeRequest req)
        {
            var pres = app.ActivePresentation;
            var sb = new StringBuilder();

            sb.Append("Presentation: ").Append(pres.Name)
              .Append(" | ").Append(pres.Slides.Count).Append(" slides")
              .Append(" | ").Append((int)pres.PageSetup.SlideWidth)
              .Append("x").Append((int)pres.PageSetup.SlideHeight).Append(" pt")
              .Append(" | Current slide: ").Append(CurrentSlideIndex(app))
              .AppendLine();

            var count = Math.Min(pres.Slides.Count, MaxSnapshotSlides);
            for (var i = 1; i <= count; i++)
            {
                var slide = pres.Slides[i];
                sb.Append('[').Append(i).Append("] (").Append(slide.Layout.ToString()).Append(")");
                var title = TitleText(slide);
                if (title != null) sb.Append(" Title: \"").Append(Truncate(title, 80)).Append("\"");
                sb.Append(" | ").Append(slide.Shapes.Count).Append(" shapes").AppendLine();
            }
            if (pres.Slides.Count > count)
                sb.AppendLine("… (" + (pres.Slides.Count - count) + " more slides)");

            return Protocol.Ok(req.Id, "Presentation snapshot captured",
                new Dictionary<string, object> { ["text"] = sb.ToString() });
        }

        // ------------------------------------------------------------------
        // read_slide — every shape's text on one slide
        // ------------------------------------------------------------------

        internal static Dictionary<string, object> ReadSlide(
            Interop.Application app, BridgeRequest req)
        {
            Interop.Slide slide;
            Dictionary<string, object> error;
            if (!TryResolveSlide(app, req, out slide, out error)) return error;

            var sb = new StringBuilder();
            sb.Append("Slide ").Append(slide.SlideIndex)
              .Append(" (").Append(slide.Layout.ToString()).Append("):")
              .AppendLine();

            foreach (Interop.Shape shape in slide.Shapes)
            {
                sb.Append("  [").Append(shape.Name).Append("] (").Append(shape.Type).Append(")");
                var text = ShapeText(shape);
                if (text != null)
                    sb.Append(" \"").Append(Truncate(text.Replace("\r", " / "), MaxShapeTextChars)).Append("\"");
                if (shape.HasTable == Office.MsoTriState.msoTrue)
                    sb.Append(" TABLE ").Append(shape.Table.Rows.Count).Append("x").Append(shape.Table.Columns.Count);
                sb.AppendLine();
            }

            var notes = NotesText(slide);
            if (notes != null)
                sb.Append("  Speaker notes: \"").Append(Truncate(notes, MaxShapeTextChars)).Append("\"").AppendLine();

            return Protocol.Ok(req.Id, "Read slide " + slide.SlideIndex,
                new Dictionary<string, object> { ["text"] = sb.ToString() });
        }

        // ------------------------------------------------------------------
        // add_slide / duplicate_slide / delete_slide
        // ------------------------------------------------------------------

        internal static Dictionary<string, object> AddSlide(
            Interop.Application app, BridgeRequest req)
        {
            var pres = app.ActivePresentation;
            var layoutName = (Protocol.GetString(req.Params, "layout") ?? "titleAndContent")
                .Trim().ToLowerInvariant();

            Interop.PpSlideLayout layout;
            if (!Layouts.TryGetValue(layoutName, out layout))
            {
                return Protocol.Fail(req.Id,
                    "Unknown layout '" + layoutName + "' — use: " +
                    string.Join(", ", new List<string>(Layouts.Keys).ToArray()),
                    "INVALID_PARAMS");
            }

            var position = (int?)Protocol.GetDouble(req.Params, "position");
            var index = position.HasValue
                ? Math.Max(1, Math.Min(pres.Slides.Count + 1, position.Value))
                : pres.Slides.Count + 1;

            var slide = pres.Slides.Add(index, layout);

            var title = Protocol.GetString(req.Params, "title");
            if (!string.IsNullOrEmpty(title) && slide.Shapes.Title != null)
                slide.Shapes.Title.TextFrame.TextRange.Text = title;

            var body = Protocol.GetString(req.Params, "body");
            if (!string.IsNullOrEmpty(body))
            {
                var bodyShape = BodyPlaceholder(slide);
                if (bodyShape != null)
                    bodyShape.TextFrame.TextRange.Text = body.Replace("\n", "\r");
            }

            return Protocol.Ok(req.Id,
                "Added slide " + index + " (layout " + layoutName + ")" +
                (title != null ? " titled \"" + Truncate(title, 40) + "\"" : ""));
        }

        internal static Dictionary<string, object> DuplicateSlide(
            Interop.Application app, BridgeRequest req)
        {
            Interop.Slide slide;
            Dictionary<string, object> error;
            if (!TryResolveSlide(app, req, out slide, out error)) return error;

            var copy = slide.Duplicate();
            return Protocol.Ok(req.Id,
                "Duplicated slide " + slide.SlideIndex + " → slide " + copy[1].SlideIndex);
        }

        internal static Dictionary<string, object> DeleteSlide(
            Interop.Application app, BridgeRequest req)
        {
            Interop.Slide slide;
            Dictionary<string, object> error;
            if (!TryResolveSlide(app, req, out slide, out error)) return error;

            var index = slide.SlideIndex;
            slide.Delete();
            return Protocol.Ok(req.Id, "Deleted slide " + index);
        }

        // ------------------------------------------------------------------
        // set_slide_text — title and/or body of an existing slide
        // ------------------------------------------------------------------

        internal static Dictionary<string, object> SetSlideText(
            Interop.Application app, BridgeRequest req)
        {
            Interop.Slide slide;
            Dictionary<string, object> error;
            if (!TryResolveSlide(app, req, out slide, out error)) return error;

            var title = Protocol.GetString(req.Params, "title");
            var body = Protocol.GetString(req.Params, "body");
            if (title == null && body == null)
            {
                return Protocol.Fail(req.Id,
                    "Provide 'title' and/or 'body' — nothing to set.", "INVALID_PARAMS");
            }

            var done = new List<string>();
            if (title != null)
            {
                if (slide.Shapes.Title == null)
                    return Protocol.Fail(req.Id,
                        "Slide " + slide.SlideIndex + " has no title placeholder " +
                        "(layout " + slide.Layout + ") — use insert_textbox instead.",
                        "NO_PLACEHOLDER");
                slide.Shapes.Title.TextFrame.TextRange.Text = title;
                done.Add("title");
            }
            if (body != null)
            {
                var bodyShape = BodyPlaceholder(slide);
                if (bodyShape == null)
                    return Protocol.Fail(req.Id,
                        "Slide " + slide.SlideIndex + " has no body placeholder " +
                        "(layout " + slide.Layout + ") — use insert_textbox instead.",
                        "NO_PLACEHOLDER");
                bodyShape.TextFrame.TextRange.Text = body.Replace("\n", "\r");
                done.Add("body");
            }

            return Protocol.Ok(req.Id,
                "Set " + string.Join(" + ", done.ToArray()) + " on slide " + slide.SlideIndex);
        }

        // ------------------------------------------------------------------
        // insert_textbox / insert_shape
        // ------------------------------------------------------------------

        internal static Dictionary<string, object> InsertTextbox(
            Interop.Application app, BridgeRequest req)
        {
            var text = Protocol.GetString(req.Params, "text");
            if (string.IsNullOrEmpty(text))
                return Protocol.Fail(req.Id, "Missing required param 'text'", "INVALID_PARAMS");

            Interop.Slide slide;
            Dictionary<string, object> error;
            if (!TryResolveSlide(app, req, out slide, out error)) return error;

            float left, top, width, height;
            ResolveBox(app, req, out left, out top, out width, out height);

            var box = slide.Shapes.AddTextbox(
                Office.MsoTextOrientation.msoTextOrientationHorizontal,
                left, top, width, height);
            box.TextFrame.TextRange.Text = text.Replace("\n", "\r");

            var fontSize = Protocol.GetDouble(req.Params, "fontSize");
            if (fontSize.HasValue && fontSize.Value > 0)
                box.TextFrame.TextRange.Font.Size = (float)fontSize.Value;
            if (Protocol.GetBool(req.Params, "bold"))
                box.TextFrame.TextRange.Font.Bold = Office.MsoTriState.msoTrue;

            return Protocol.Ok(req.Id,
                "Inserted textbox on slide " + slide.SlideIndex +
                " (" + text.Length + " chars)");
        }

        internal static Dictionary<string, object> InsertShape(
            Interop.Application app, BridgeRequest req)
        {
            var shapeName = (Protocol.GetString(req.Params, "shape") ?? "rectangle")
                .Trim().ToLowerInvariant();

            Office.MsoAutoShapeType shapeType;
            if (!Shapes.TryGetValue(shapeName, out shapeType))
            {
                return Protocol.Fail(req.Id,
                    "Unknown shape '" + shapeName + "' — use: " +
                    string.Join(", ", new List<string>(Shapes.Keys).ToArray()),
                    "INVALID_PARAMS");
            }

            Interop.Slide slide;
            Dictionary<string, object> error;
            if (!TryResolveSlide(app, req, out slide, out error)) return error;

            float left, top, width, height;
            ResolveBox(app, req, out left, out top, out width, out height);

            var shape = slide.Shapes.AddShape(shapeType, left, top, width, height);
            var text = Protocol.GetString(req.Params, "text");
            if (!string.IsNullOrEmpty(text))
                shape.TextFrame.TextRange.Text = text.Replace("\n", "\r");

            return Protocol.Ok(req.Id,
                "Inserted " + shapeName + " on slide " + slide.SlideIndex);
        }

        // ------------------------------------------------------------------
        // insert_table — on a slide, optional 2D data + bold header
        // ------------------------------------------------------------------

        internal static Dictionary<string, object> InsertTable(
            Interop.Application app, BridgeRequest req)
        {
            var rows = (int?)Protocol.GetDouble(req.Params, "rows") ?? 0;
            var cols = (int?)Protocol.GetDouble(req.Params, "cols") ?? 0;
            if (rows < 1 || cols < 1)
                return Protocol.Fail(req.Id,
                    "Params 'rows' and 'cols' must be >= 1", "INVALID_PARAMS");
            if (rows > 50 || cols > 10)
                return Protocol.Fail(req.Id,
                    "Table too large for a slide (max 50 rows x 10 cols)", "INVALID_PARAMS");

            Interop.Slide slide;
            Dictionary<string, object> error;
            if (!TryResolveSlide(app, req, out slide, out error)) return error;

            float left, top, width, height;
            ResolveBox(app, req, out left, out top, out width, out height);

            var shape = slide.Shapes.AddTable(rows, cols, left, top, width, height);
            var table = shape.Table;

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
                        table.Cell(r + 1, c + 1).Shape.TextFrame.TextRange.Text =
                            row[c] == null ? string.Empty : row[c].ToString();
                    }
                }
            }

            if (Protocol.GetBool(req.Params, "headerRow"))
            {
                for (var c = 1; c <= cols; c++)
                    table.Cell(1, c).Shape.TextFrame.TextRange.Font.Bold =
                        Office.MsoTriState.msoTrue;
            }

            return Protocol.Ok(req.Id,
                "Inserted a " + rows + "x" + cols + " table on slide " + slide.SlideIndex);
        }

        // ------------------------------------------------------------------
        // set_speaker_notes
        // ------------------------------------------------------------------

        internal static Dictionary<string, object> SetSpeakerNotes(
            Interop.Application app, BridgeRequest req)
        {
            var notes = Protocol.GetString(req.Params, "notes");
            if (notes == null)
                return Protocol.Fail(req.Id, "Missing required param 'notes'", "INVALID_PARAMS");

            Interop.Slide slide;
            Dictionary<string, object> error;
            if (!TryResolveSlide(app, req, out slide, out error)) return error;

            foreach (Interop.Shape shape in slide.NotesPage.Shapes)
            {
                if (shape.Type == Office.MsoShapeType.msoPlaceholder &&
                    shape.PlaceholderFormat.Type == Interop.PpPlaceholderType.ppPlaceholderBody)
                {
                    shape.TextFrame.TextRange.Text = notes.Replace("\n", "\r");
                    return Protocol.Ok(req.Id,
                        "Set speaker notes on slide " + slide.SlideIndex +
                        " (" + notes.Length + " chars)");
                }
            }
            return Protocol.Fail(req.Id,
                "Slide " + slide.SlideIndex + " has no notes placeholder.",
                "NO_PLACEHOLDER");
        }

        // ------------------------------------------------------------------
        // insert_picture — generated image (host-injected data URL) or file
        // ------------------------------------------------------------------

        internal static Dictionary<string, object> InsertPicture(
            Interop.Application app, BridgeRequest req)
        {
            Interop.Slide slide;
            Dictionary<string, object> error;
            if (!TryResolveSlide(app, req, out slide, out error)) return error;

            // imageData arrives host-injected from the session's generated
            // buffer (see BridgeToolset.InjectGeneratedImage); "path" is an
            // explicit file the model/user named.
            var imageData = Protocol.GetString(req.Params, "imageData");
            var path = Protocol.GetString(req.Params, "path");
            string tempFile = null;

            if (!string.IsNullOrEmpty(imageData))
            {
                if (!TrySaveDataUrl(imageData, out tempFile, out error))
                {
                    error["id"] = req.Id;
                    return error;
                }
                path = tempFile;
            }
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return Protocol.Fail(req.Id,
                    "No image source: pass 'source' (generated image) or a file 'path'.",
                    "INVALID_PARAMS");
            }

            if (Protocol.GetBool(req.Params, "asBackground"))
            {
                // Slide-local background (not the master): picture fill.
                slide.FollowMasterBackground = Office.MsoTriState.msoFalse;
                slide.Background.Fill.UserPicture(path);
                AppendAttributionToNotes(slide, Protocol.GetString(req.Params, "imageAttribution"));
                return Protocol.Ok(req.Id,
                    "Set the picture as slide " + slide.SlideIndex + "'s background");
            }

            // Default placement: fit within 80% of the slide, centered,
            // aspect ratio preserved (explicit left/top/width/height win).
            var setup = app.ActivePresentation.PageSetup;
            float width, height;
            GetPictureSize(path, setup.SlideWidth * 0.8f, setup.SlideHeight * 0.8f,
                out width, out height);
            var left = (float)(Protocol.GetDouble(req.Params, "left") ?? (setup.SlideWidth - width) / 2);
            var top = (float)(Protocol.GetDouble(req.Params, "top") ?? (setup.SlideHeight - height) / 2);
            width = (float)(Protocol.GetDouble(req.Params, "width") ?? width);
            height = (float)(Protocol.GetDouble(req.Params, "height") ?? height);

            slide.Shapes.AddPicture(path,
                Office.MsoTriState.msoFalse,  // LinkToFile: embed a copy
                Office.MsoTriState.msoTrue,   // SaveWithDocument
                left, top, width, height);

            AppendAttributionToNotes(slide, Protocol.GetString(req.Params, "imageAttribution"));

            return Protocol.Ok(req.Id,
                "Inserted picture on slide " + slide.SlideIndex +
                " (" + (int)width + "x" + (int)height + " pt)");
        }

        // ------------------------------------------------------------------
        // list_themes / apply_theme — Office's own design themes (.thmx)
        // ------------------------------------------------------------------

        internal static Dictionary<string, object> ListThemes(
            Interop.Application app, BridgeRequest req)
        {
            var names = new List<string>();
            foreach (var dir in ThemeDirs())
            {
                foreach (var file in Directory.GetFiles(dir, "*.thmx"))
                {
                    var name = Path.GetFileNameWithoutExtension(file);
                    if (!names.Contains(name)) names.Add(name);
                }
            }
            names.Sort(StringComparer.OrdinalIgnoreCase);

            if (names.Count == 0)
            {
                return Protocol.Fail(req.Id,
                    "No .thmx themes found in the Office theme folders.",
                    "NOT_FOUND");
            }
            return Protocol.Ok(req.Id, names.Count + " theme(s) available",
                new Dictionary<string, object>
                {
                    ["text"] = "Available themes: " + string.Join(", ", names.ToArray()),
                    ["themes"] = new ArrayList(names),
                });
        }

        internal static Dictionary<string, object> ApplyTheme(
            Interop.Application app, BridgeRequest req)
        {
            var pres = app.ActivePresentation;
            var path = Protocol.GetString(req.Params, "path");
            var name = Protocol.GetString(req.Params, "theme");

            if (string.IsNullOrWhiteSpace(path))
            {
                if (string.IsNullOrWhiteSpace(name))
                {
                    return Protocol.Fail(req.Id,
                        "Provide 'theme' (a name from list_themes) or an explicit 'path'.",
                        "INVALID_PARAMS");
                }
                foreach (var dir in ThemeDirs())
                {
                    foreach (var file in Directory.GetFiles(dir, "*.thmx"))
                    {
                        if (string.Equals(Path.GetFileNameWithoutExtension(file), name,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            path = file;
                            break;
                        }
                    }
                    if (path != null) break;
                }
                if (path == null)
                {
                    return Protocol.Fail(req.Id,
                        "Theme '" + name + "' not found — call list_themes for the available names.",
                        "NOT_FOUND");
                }
            }

            pres.ApplyTemplate(path);
            return Protocol.Ok(req.Id,
                "Applied theme '" + Path.GetFileNameWithoutExtension(path) +
                "' to the whole deck");
        }

/// <summary>Office's built-in theme folders + the user's custom one.</summary>
        private static IEnumerable<string> ThemeDirs()
        {
            var candidates = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    "Microsoft Office", "root", "Document Themes 16"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                    "Microsoft Office", "root", "Document Themes 16"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Microsoft", "Templates", "Document Themes"),
            };
            foreach (var dir in candidates)
            {
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) yield return dir;
            }
        }

/// <summary>CC attribution for searched images: appended to the slide's speaker notes (never overwriting existing notes).</summary>
        private static void AppendAttributionToNotes(Interop.Slide slide, string attribution)
        {
            if (string.IsNullOrWhiteSpace(attribution)) return;
            try
            {
                foreach (Interop.Shape shape in slide.NotesPage.Shapes)
                {
                    if (shape.Type == Office.MsoShapeType.msoPlaceholder &&
                        shape.PlaceholderFormat.Type == Interop.PpPlaceholderType.ppPlaceholderBody)
                    {
                        var range = shape.TextFrame.TextRange;
                        var existing = range.Text ?? string.Empty;
                        range.Text = existing.Trim().Length > 0
                            ? existing.TrimEnd('\r') + "\r" + attribution
                            : attribution;
                        return;
                    }
                }
            }
            catch
            {
                // Attribution is best-effort — never fail the insert over it.
            }
        }

        // ------------------------------------------------------------------
        // Image helpers
        // ------------------------------------------------------------------

/// <summary>Decode a data URL into a temp file the COM APIs can eat.</summary>
        private static bool TrySaveDataUrl(
            string dataUrl, out string tempFile, out Dictionary<string, object> error)
        {
            tempFile = null;
            error = null;
            try
            {
                // "data:image/png;base64,...."
                var comma = dataUrl.IndexOf(',');
                if (comma < 0) throw new FormatException("not a data URL");
                var header = dataUrl.Substring(0, comma);
                var ext = header.IndexOf("png", StringComparison.OrdinalIgnoreCase) >= 0 ? ".png"
                    : header.IndexOf("jpeg", StringComparison.OrdinalIgnoreCase) >= 0 ||
                      header.IndexOf("jpg", StringComparison.OrdinalIgnoreCase) >= 0 ? ".jpg"
                    : ".png";
                var bytes = Convert.FromBase64String(dataUrl.Substring(comma + 1));

                var dir = Path.Combine(Path.GetTempPath(), "AgentOffice");
                Directory.CreateDirectory(dir);
                tempFile = Path.Combine(dir, "img_" + Guid.NewGuid().ToString("N") + ext);
                File.WriteAllBytes(tempFile, bytes);
                return true;
            }
            catch (Exception ex)
            {
                error = Protocol.Fail("", "Could not decode the generated image: " + ex.Message,
                    "INVALID_PARAMS");
                return false;
            }
        }

/// <summary>Fit the picture inside maxW x maxH keeping its aspect ratio.</summary>
        private static void GetPictureSize(
            string path, float maxW, float maxH, out float width, out float height)
        {
            float ratio = 1.5f; // fallback when the file won't decode
            try
            {
                using (var img = System.Drawing.Image.FromFile(path))
                {
                    if (img.Height > 0) ratio = (float)img.Width / img.Height;
                }
            }
            catch { }

            width = maxW;
            height = width / ratio;
            if (height > maxH)
            {
                height = maxH;
                width = height * ratio;
            }
        }

        // ------------------------------------------------------------------
        // export_pdf
        // ------------------------------------------------------------------

        internal static Dictionary<string, object> ExportPdf(
            Interop.Application app, BridgeRequest req)
        {
            var pres = app.ActivePresentation;
            // Explicit path > settings.json exportDir > Documents\AgentOffice.
            var path = ExportPath.Resolve(
                Protocol.GetString(req.Params, "path"),
                System.IO.Path.GetFileNameWithoutExtension(pres.Name),
                ".pdf");

            pres.ExportAsFixedFormat(path, Interop.PpFixedFormatType.ppFixedFormatTypePDF);
            return Protocol.Ok(req.Id, "Exported PDF to " + path,
                new Dictionary<string, object> { ["path"] = path });
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

/// <summary>1-based slide from the "slide" param (default: current).</summary>
        private static bool TryResolveSlide(
            Interop.Application app, BridgeRequest req,
            out Interop.Slide slide, out Dictionary<string, object> error)
        {
            slide = null;
            error = null;

            var pres = app.ActivePresentation;
            var index = (int?)Protocol.GetDouble(req.Params, "slide")
                ?? CurrentSlideIndex(app);

            if (index < 1 || index > pres.Slides.Count)
            {
                error = Protocol.Fail(req.Id,
                    "Slide " + index + " is out of range — the deck has " +
                    pres.Slides.Count + " slide(s).", "INVALID_PARAMS");
                return false;
            }
            slide = pres.Slides[index];
            return true;
        }

        private static int CurrentSlideIndex(Interop.Application app)
        {
            try
            {
                var range = app.ActiveWindow.Selection.SlideRange;
                if (range != null && range.Count > 0) return range[1].SlideIndex;
            }
            catch
            {
                // No window/selection (e.g. slideshow) — fall back to slide 1.
            }
            return 1;
        }

        private static string TitleText(Interop.Slide slide)
        {
            try
            {
                if (slide.Shapes.Title == null) return null;
                var text = ShapeText(slide.Shapes.Title);
                return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
            }
            catch { return null; }
        }

        private static Interop.Shape BodyPlaceholder(Interop.Slide slide)
        {
            foreach (Interop.Shape shape in slide.Shapes)
            {
                if (shape.Type == Office.MsoShapeType.msoPlaceholder &&
                    shape.PlaceholderFormat.Type == Interop.PpPlaceholderType.ppPlaceholderBody)
                {
                    return shape;
                }
            }
            return null;
        }

        private static string ShapeText(Interop.Shape shape)
        {
            try
            {
                if (shape.HasTextFrame == Office.MsoTriState.msoTrue &&
                    shape.TextFrame.HasText == Office.MsoTriState.msoTrue)
                {
                    return shape.TextFrame.TextRange.Text;
                }
            }
            catch { }
            return null;
        }

        private static string NotesText(Interop.Slide slide)
        {
            try
            {
                foreach (Interop.Shape shape in slide.NotesPage.Shapes)
                {
                    if (shape.Type == Office.MsoShapeType.msoPlaceholder &&
                        shape.PlaceholderFormat.Type == Interop.PpPlaceholderType.ppPlaceholderBody)
                    {
                        var text = ShapeText(shape);
                        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
                    }
                }
            }
            catch { }
            return null;
        }

/// <summary>Box params in points; defaults: centered block on the slide.</summary>
        private static void ResolveBox(
            Interop.Application app, BridgeRequest req,
            out float left, out float top, out float width, out float height)
        {
            var setup = app.ActivePresentation.PageSetup;
            var slideW = setup.SlideWidth;
            var slideH = setup.SlideHeight;

            width = (float)(Protocol.GetDouble(req.Params, "width") ?? slideW * 0.6);
            height = (float)(Protocol.GetDouble(req.Params, "height") ?? slideH * 0.25);
            left = (float)(Protocol.GetDouble(req.Params, "left") ?? (slideW - width) / 2);
            top = (float)(Protocol.GetDouble(req.Params, "top") ?? slideH * 0.35);
        }

        private static readonly Dictionary<string, Interop.PpSlideLayout> Layouts =
            new Dictionary<string, Interop.PpSlideLayout>(StringComparer.OrdinalIgnoreCase)
            {
                ["titleandcontent"] = Interop.PpSlideLayout.ppLayoutText,
                ["titleonly"] = Interop.PpSlideLayout.ppLayoutTitleOnly,
                ["title"] = Interop.PpSlideLayout.ppLayoutTitle,
                ["sectionheader"] = Interop.PpSlideLayout.ppLayoutSectionHeader,
                ["twocontent"] = Interop.PpSlideLayout.ppLayoutTwoObjects,
                ["blank"] = Interop.PpSlideLayout.ppLayoutBlank,
            };

        private static readonly Dictionary<string, Office.MsoAutoShapeType> Shapes =
            new Dictionary<string, Office.MsoAutoShapeType>(StringComparer.OrdinalIgnoreCase)
            {
                ["rectangle"] = Office.MsoAutoShapeType.msoShapeRectangle,
                ["roundedrectangle"] = Office.MsoAutoShapeType.msoShapeRoundedRectangle,
                ["oval"] = Office.MsoAutoShapeType.msoShapeOval,
                ["triangle"] = Office.MsoAutoShapeType.msoShapeIsoscelesTriangle,
                ["rightarrow"] = Office.MsoAutoShapeType.msoShapeRightArrow,
                ["star"] = Office.MsoAutoShapeType.msoShape5pointStar,
            };

        private static string Truncate(string text, int max)
        {
            return text.Length <= max ? text : text.Substring(0, max - 1) + "…";
        }
    }
}
