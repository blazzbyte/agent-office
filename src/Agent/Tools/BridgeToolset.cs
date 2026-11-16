using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace AgentOffice.Agent.Tools
{
/// <summary>The agent-facing tool surface: every Excel bridge action exposed as an OpenAI function tool.</summary>
    internal static class BridgeToolset
    {
/// <summary>Cap on tool-result text fed to the model (opencode: ~50KB).</summary>
        private const int MaxResultChars = 50000;

        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer
        {
            // Tool args are small, but results carry capture_range data URLs.
            MaxJsonLength = 64 * 1024 * 1024,
        };

        // ==================================================================
        // Execution
        // ==================================================================

/// <summary>Execute one model-requested tool call.</summary>
        public static async Task<Dictionary<string, object>> ExecuteAsync(
            LlmToolCall call, AgentSession session = null,
            System.Threading.CancellationToken ct = default(System.Threading.CancellationToken))
        {
            Dictionary<string, object> args;
            try
            {
                args = string.IsNullOrWhiteSpace(call.Arguments)
                    ? new Dictionary<string, object>()
                    : Json.Deserialize<Dictionary<string, object>>(call.Arguments);
            }
            catch (Exception ex)
            {
                return ErrorPayload(call, "Invalid JSON arguments: " + ex.Message +
                    " — fix the arguments and call the tool again.", "INVALID_PARAMS");
            }
            if (args == null)
            {
                return ErrorPayload(call, "Invalid arguments: expected a JSON object.",
                    "INVALID_PARAMS");
            }

            // search_image / generate_image: host-side tools — they call web
            // APIs and feed the session buffer; never reach the app bridge.
            if (call.Name == "search_image")
            {
                return await ExecuteSearchImageAsync(call, args, session, ct)
                    .ConfigureAwait(false);
            }
            if (call.Name == "generate_image")
            {
                return await ExecuteGenerateImageAsync(call, args, session, ct)
                    .ConfigureAwait(false);
            }
            if (call.Name == "insert_picture" && session != null)
            {
                var injectError = InjectGeneratedImage(session, args, call);
                if (injectError != null) return injectError;
            }

            var host = Bridge.BridgeHost.Current;
            if (host == null)
            {
                return ErrorPayload(call, "The app bridge is not running.", "NO_HOST");
            }

            try
            {
                return await host.ExecuteActionAsync(call.Name, args).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // ExecuteActionAsync is designed not to throw — belt and braces.
                return ErrorPayload(call, "Tool execution failed: " + ex.Message, "INTERNAL");
            }
        }

/// <summary>Search_image execution.</summary>
        private static async Task<Dictionary<string, object>> ExecuteSearchImageAsync(
            LlmToolCall call, Dictionary<string, object> args, AgentSession session,
            System.Threading.CancellationToken ct)
        {
            object queryRaw;
            var query = args.TryGetValue("query", out queryRaw) ? queryRaw as string : null;
            if (string.IsNullOrWhiteSpace(query))
            {
                return ErrorPayload(call, "Missing required param 'query'", "INVALID_PARAMS");
            }
            object countRaw;
            var count = 1;
            if (args.TryGetValue("count", out countRaw) && countRaw is int)
                count = Math.Max(1, Math.Min(3, (int)countRaw));

            List<ImageSearch.Hit> hits;
            try
            {
                hits = await ImageSearch.SearchAsync(query, count, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return ErrorPayload(call, "Image search failed: " + ex.Message, "PROVIDER_ERROR");
            }
            if (hits.Count == 0)
            {
                return ErrorPayload(call,
                    "No images found for \"" + query + "\" — try a broader query.",
                    "NOT_FOUND");
            }

            // Download each hit into the buffer; a failed download skips that
            // hit (the others still land).
            var downloaded = new List<KeyValuePair<ImageSearch.Hit, string>>();
            foreach (var hit in hits)
            {
                try
                {
                    var dataUrl = await ImageSearch.DownloadAsDataUrlAsync(hit, ct)
                        .ConfigureAwait(false);
                    downloaded.Add(new KeyValuePair<ImageSearch.Hit, string>(hit, dataUrl));
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Bridge.Log.Error("search_image: download failed for " + hit.Url, ex);
                }
            }
            if (downloaded.Count == 0)
            {
                return ErrorPayload(call,
                    "Found " + hits.Count + " image(s) but every download failed — " +
                    "try another query.", "PROVIDER_ERROR");
            }

            var sb = new StringBuilder();
            sb.Append("Downloaded ").Append(downloaded.Count).Append(" image(s) for \"")
              .Append(query).Append("\" (newest = source \"last\"):");
            if (session != null)
            {
                lock (session.Sync)
                {
                    foreach (var pair in downloaded)
                    {
                        session.GeneratedImages.Add(new AgentSession.GeneratedImage
                        {
                            DataUrl = pair.Value,
                            Attribution = pair.Key.AttributionText(),
                        });
                        while (session.GeneratedImages.Count > AgentSession.MaxGeneratedImages)
                            session.GeneratedImages.RemoveAt(0);
                    }
                }
            }
            foreach (var pair in downloaded)
            {
                var hit = pair.Key;
                sb.Append("\n- ").Append(hit.AttributionText());
                if (hit.Width > 0 && hit.Height > 0)
                    sb.Append(" [").Append(hit.Width).Append("x").Append(hit.Height).Append("]");
            }
            sb.Append("\nAttribution is added to the slide's speaker notes automatically on insert.");

            return new Dictionary<string, object>
            {
                ["id"] = call.Id,
                ["status"] = "ok",
                ["summary"] = sb.ToString(),
                ["data"] = new Dictionary<string, object>
                {
                    ["image"] = downloaded[downloaded.Count - 1].Value,
                    ["count"] = downloaded.Count,
                },
            };
        }

/// <summary>Generate_image execution.</summary>
        private static async Task<Dictionary<string, object>> ExecuteGenerateImageAsync(
            LlmToolCall call, Dictionary<string, object> args, AgentSession session,
            System.Threading.CancellationToken ct)
        {
            if (!ProviderSettings.ImageConfigured)
            {
                return ErrorPayload(call,
                    "No image provider configured — set one in Connection settings " +
                    "(Image generation section) or settings.json (imageModel/imageProvider).",
                    "NOT_CONFIGURED");
            }

            object promptRaw;
            var prompt = args.TryGetValue("prompt", out promptRaw)
                ? promptRaw as string : null;
            if (string.IsNullOrWhiteSpace(prompt))
            {
                return ErrorPayload(call, "Missing required param 'prompt'", "INVALID_PARAMS");
            }

            List<string> images;
            try
            {
                images = await ImageGeneration
                    .GenerateAsync(prompt, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return ErrorPayload(call,
                    "Image generation failed: " + ex.Message, "PROVIDER_ERROR");
            }

            if (images == null || images.Count == 0)
            {
                return ErrorPayload(call,
                    "The image provider (" + ImageGeneration.ResolveProvider() +
                    ") returned no images — check the model/endpoint in " +
                    "Connection settings › Image generation.",
                    "NO_IMAGE");
            }

            if (session != null)
            {
                lock (session.Sync)
                {
                    foreach (var image in images)
                    {
                        session.GeneratedImages.Add(new AgentSession.GeneratedImage
                        {
                            DataUrl = image,
                        });
                        while (session.GeneratedImages.Count > AgentSession.MaxGeneratedImages)
                            session.GeneratedImages.RemoveAt(0);
                    }
                }
            }

            return new Dictionary<string, object>
            {
                ["id"] = call.Id,
                ["status"] = "ok",
                ["summary"] = "Generated " + images.Count + " image(s) with " +
                    ProviderSettings.ImageModel +
                    " — place them with insert_picture (source \"last\"" +
                    (images.Count > 1 ? " or a recency index" : "") + ").",
                ["data"] = new Dictionary<string, object>
                {
                    ["image"] = images[0],
                    ["count"] = images.Count,
                },
            };
        }

/// <summary>Insert_picture arg resolution: an explicit file "path" passes through untouched.</summary>
        private static Dictionary<string, object> InjectGeneratedImage(
            AgentSession session, Dictionary<string, object> args, LlmToolCall call)
        {
            object pathRaw;
            if (args.TryGetValue("path", out pathRaw) &&
                pathRaw is string && !string.IsNullOrWhiteSpace((string)pathRaw))
            {
                return null; // explicit file path — the action uses it directly
            }

            string dataUrl = null;
            string attribution = null;
            lock (session.Sync)
            {
                var count = session.GeneratedImages.Count;
                if (count > 0)
                {
                    var source = args.ContainsKey("source")
                        ? (args["source"] == null ? "last" : args["source"].ToString())
                        : "last";
                    var recency = 1; // "last"
                    int parsed;
                    if (int.TryParse(source, out parsed) && parsed >= 1) recency = parsed;
                    if (recency <= count)
                    {
                        var entry = session.GeneratedImages[count - recency];
                        dataUrl = entry.DataUrl;
                        attribution = entry.Attribution;
                    }
                }
            }

            if (dataUrl == null)
            {
                return ErrorPayload(call,
                    "No image available in this session — search_image or generate_image " +
                    "first, or pass an explicit file 'path'.",
                    "NO_IMAGE");
            }
            args["imageData"] = dataUrl;
            if (!string.IsNullOrEmpty(attribution))
                args["imageAttribution"] = attribution;
            return null;
        }

/// <summary>Serialize a StepResult for the model's tool message: the summary plus truncated data.</summary>
        public static string FormatResultContent(Dictionary<string, object> result)
        {
            var status = GetString(result, "status") ?? "error";
            var summary = GetString(result, "summary") ?? string.Empty;

            object dataRaw;
            var data = result.TryGetValue("data", out dataRaw) ? dataRaw : null;
            if (data is Dictionary<string, object> &&
                ((Dictionary<string, object>)data).ContainsKey("image"))
            {
                // Never inline a base64 PNG into text content.
                data = StripImage((Dictionary<string, object>)data);
            }

            var sb = new StringBuilder(summary);
            if (data != null)
            {
                var dataJson = Json.Serialize(data);
                if (sb.Length > 0) sb.Append('\n');
                if (dataJson.Length > MaxResultChars)
                {
                    dataJson = dataJson.Substring(0, MaxResultChars) +
                        "\n…[truncated " + (dataJson.Length - MaxResultChars) +
                        " chars — read a smaller range]";
                }
                sb.Append(dataJson);
            }

            var text = sb.ToString();
            if (status == "error") return "ERROR: " + text;
            return text.Length > 0 ? text : "OK";
        }

/// <summary>When the result carries a capture_range image, extract it as an OpenAI image_url part so the tool message goes.</summary>
        public static ArrayList ExtractImagePart(Dictionary<string, object> result)
        {
            object dataRaw;
            var data = result.TryGetValue("data", out dataRaw)
                ? dataRaw as Dictionary<string, object> : null;
            if (data == null) return null;

            object imageRaw;
            var image = data.TryGetValue("image", out imageRaw) ? imageRaw as string : null;
            if (string.IsNullOrEmpty(image) || !image.StartsWith("data:image/")) return null;

            return new ArrayList
            {
                new Dictionary<string, object>
                {
                    ["type"] = "image_url",
                    ["image_url"] = new Dictionary<string, object> { ["url"] = image },
                },
            };
        }

/// <summary>A short, human-readable line for the UI's tool bubble.</summary>
        public static string ResultPreview(Dictionary<string, object> result)
        {
            var summary = GetString(result, "summary") ?? "(no summary)";
            if (GetString(result, "status") == "error") return "Error: " + summary;
            return summary;
        }

        public static bool IsError(Dictionary<string, object> result)
        {
            return GetString(result, "status") != "success";
        }

        private static Dictionary<string, object> ErrorPayload(
            LlmToolCall call, string summary, string code)
        {
            return new Dictionary<string, object>
            {
                ["id"] = call.Id ?? string.Empty,
                ["status"] = "error",
                ["summary"] = summary,
                ["data"] = new Dictionary<string, object> { ["code"] = code },
            };
        }

        private static Dictionary<string, object> StripImage(Dictionary<string, object> data)
        {
            var copy = new Dictionary<string, object>(data);
            copy.Remove("image");
            copy["image_attached"] = true;
            return copy;
        }

        private static string GetString(Dictionary<string, object> dict, string key)
        {
            object raw;
            return dict.TryGetValue(key, out raw) ? raw as string : null;
        }

        // ==================================================================
        // Tool definitions (OpenAI function schemas)
        // ==================================================================

        private static ArrayList _definitions;
        private static ArrayList _wordDefinitions;
        private static ArrayList _powerPointDefinitions;

/// <summary>The OpenAI `tools` array — all 34 bridge actions.</summary>
        public static ArrayList Definitions
        {
            get { return _definitions ?? (_definitions = Build()); }
        }

/// <summary>Tool surface for a host app.</summary>
        public static ArrayList DefinitionsFor(HostApp app)
        {
            ArrayList baseList;
            if (app == HostApp.Excel) baseList = Definitions;
            else if (app == HostApp.Word)
                baseList = _wordDefinitions ?? (_wordDefinitions = BuildWord());
            else
                baseList = _powerPointDefinitions ?? (_powerPointDefinitions = BuildPowerPoint());

            // search_image is always available (zero config); generate_image
            // only when an image model is configured.
            var withImage = new ArrayList(baseList);
            withImage.Add(SearchImageDefinition());
            if (ProviderSettings.ImageConfigured)
                withImage.Add(GenerateImageDefinition());
            return withImage;
        }

/// <summary>Search_image — zero-config Openverse search, always available in every app.</summary>
        private static Dictionary<string, object> SearchImageDefinition()
        {
            var function = new Dictionary<string, object>
            {
                ["name"] = "search_image",
                ["description"] =
                    "Search the web for a real, commercially-licensed photo (Openverse, " +
                    "no config needed) and download it into the session. Prefer this over " +
                    "generate_image for realistic photos/backgrounds; use generate_image " +
                    "for custom art. Then place it with insert_picture (source \"last\"). " +
                    "Attribution is added to speaker notes automatically.",
                ["parameters"] = new Dictionary<string, object>
                {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object>
                    {
                        ["query"] = new Dictionary<string, object>
                        {
                            ["type"] = "string",
                            ["description"] = "What to search for (e.g. \"solar panels sunset aerial\")",
                        },
                        ["count"] = new Dictionary<string, object>
                        {
                            ["type"] = "number",
                            ["description"] = "How many images to download, 1-3 (default: 1)",
                        },
                    },
                    ["required"] = new ArrayList { "query" },
                },
            };
            return new Dictionary<string, object>
            {
                ["type"] = "function",
                ["function"] = function,
            };
        }

/// <summary>Generate_image — the text model orchestrates, the configured image model draws.</summary>
        private static Dictionary<string, object> GenerateImageDefinition()
        {
            var function = new Dictionary<string, object>
            {
                ["name"] = "generate_image",
                ["description"] =
                    "Generate an image with the configured image model. Write a detailed, " +
                    "visual prompt (subject, style, colors, composition, aspect). The image " +
                    "is stored in the session — place it with insert_picture (source \"last\").",
                ["parameters"] = new Dictionary<string, object>
                {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object>
                    {
                        ["prompt"] = new Dictionary<string, object>
                        {
                            ["type"] = "string",
                            ["description"] = "Detailed visual description of the image to generate",
                        },
                    },
                    ["required"] = new ArrayList { "prompt" },
                },
            };
            return new Dictionary<string, object>
            {
                ["type"] = "function",
                ["function"] = function,
            };
        }

        // ==================================================================
        // PowerPoint toolset (Fase 10) — mirrors src/Bridge/PowerPoint/
        // ==================================================================

        private static ArrayList BuildPowerPoint()
        {
            var list = new ArrayList();

            Add(list, "read_presentation_snapshot",
                "Overview of the active presentation: name, slide count, slide size, current slide, and a per-slide outline (layout, title, shape count). Cheap — call it to orient yourself before reading or editing.",
                Props(), NoRequired());
            Add(list, "read_slide",
                "Read one slide in detail: every shape with its type and text, tables with dimensions, and the speaker notes.",
                Props(
                    P("slide", "number", "Slide number, 1-based (default: the current slide)")),
                NoRequired());
            Add(list, "add_slide",
                "Add a slide with a layout, optionally setting its title and body text.",
                Props(
                    P("layout", "string", "\"titleAndContent\" (default), \"titleOnly\", \"title\", \"sectionHeader\", \"twoContent\", \"blank\""),
                    P("title", "string", "Title text (when the layout has a title placeholder)"),
                    P("body", "string", "Body text (when the layout has a body placeholder; newlines = bullet lines)"),
                    P("position", "number", "Insert position, 1-based (default: at the end)")),
                NoRequired());
            Add(list, "duplicate_slide",
                "Duplicate a slide (copy lands right after it).",
                Props(
                    P("slide", "number", "Slide to duplicate, 1-based (default: the current slide)")),
                NoRequired());
            Add(list, "delete_slide",
                "Delete a slide. Destructive — confirm with the user first.",
                Props(
                    P("slide", "number", "Slide to delete, 1-based (default: the current slide)")),
                NoRequired());
            Add(list, "set_slide_text",
                "Set the title and/or body text of an existing slide (the layout must have those placeholders).",
                Props(
                    P("slide", "number", "Slide number, 1-based (default: the current slide)"),
                    P("title", "string", "New title text"),
                    P("body", "string", "New body text (newlines = bullet lines)")),
                NoRequired());
            Add(list, "insert_textbox",
                "Insert a text box on a slide. Positions/sizes are in points (1/72 inch); defaults center a block on the slide.",
                Props(
                    P("text", "string", "Text content (newlines = new lines)"),
                    P("slide", "number", "Slide number, 1-based (default: the current slide)"),
                    P("left", "number", "Left edge in points (default: centered)"),
                    P("top", "number", "Top edge in points (default: ~35% down)"),
                    P("width", "number", "Width in points (default: 60% of slide width)"),
                    P("height", "number", "Height in points (default: 25% of slide height)"),
                    P("fontSize", "number", "Font size in points (optional)"),
                    P("bold", "boolean", "Bold text (default: false)")),
                Required("text"));
            Add(list, "insert_shape",
                "Insert an auto shape on a slide, optionally with text inside.",
                Props(
                    P("shape", "string", "\"rectangle\" (default), \"roundedRectangle\", \"oval\", \"triangle\", \"rightArrow\", \"star\""),
                    P("text", "string", "Optional text inside the shape"),
                    P("slide", "number", "Slide number, 1-based (default: the current slide)"),
                    P("left", "number", "Left edge in points (default: centered)"),
                    P("top", "number", "Top edge in points (default: ~35% down)"),
                    P("width", "number", "Width in points (default: 60% of slide width)"),
                    P("height", "number", "Height in points (default: 25% of slide height)")),
                NoRequired());
            Add(list, "insert_table",
                "Insert a table on a slide, optionally pre-filled with 2D data and a bold header row.",
                Props(
                    P("rows", "number", "Row count (max 50)"),
                    P("cols", "number", "Column count (max 10)"),
                    P("slide", "number", "Slide number, 1-based (default: the current slide)"),
                    P("data", "array", "Optional 2D array of cell values (array of row arrays)"),
                    P("headerRow", "boolean", "Bold the first row (default: false)"),
                    P("left", "number", "Left edge in points (default: centered)"),
                    P("top", "number", "Top edge in points (default: ~35% down)"),
                    P("width", "number", "Width in points (default: 60% of slide width)"),
                    P("height", "number", "Height in points (default: 25% of slide height)")),
                Required("rows", "cols"));
            Add(list, "insert_picture",
                "Insert a picture on a slide — either an image YOU generated earlier this session (via 'source') or a file from disk (via 'path'). As a slide background it fills the whole slide; otherwise it is centered and aspect-fit (explicit left/top/width/height in points win).",
                Props(
                    P("source", "string", "Which generated image: \"last\" (default, most recent) or a recency index (\"2\" = the one before last)"),
                    P("path", "string", "Explicit image file path (overrides 'source')"),
                    P("slide", "number", "Slide number, 1-based (default: the current slide)"),
                    P("asBackground", "boolean", "Set the picture as the slide's background fill (default: false)"),
                    P("left", "number", "Left edge in points (default: centered)"),
                    P("top", "number", "Top edge in points (default: centered)"),
                    P("width", "number", "Width in points (default: aspect-fit within 80% of the slide)"),
                    P("height", "number", "Height in points (default: aspect-fit)")),
                NoRequired());
            Add(list, "list_themes",
                "List the Office design themes (.thmx) available on this machine — built-in ones (Facet, Ion, …) plus the user's custom themes.",
                Props(), NoRequired());
            Add(list, "apply_theme",
                "Apply an Office design theme to the WHOLE deck — instant professional look (colors, fonts, backgrounds). Strongly recommended before/after building slides.",
                Props(
                    P("theme", "string", "Theme name from list_themes (e.g. \"Facet\", \"Ion\")"),
                    P("path", "string", "Explicit .thmx/.potx path (overrides 'theme')")),
                NoRequired());
            Add(list, "set_speaker_notes",
                "Set the speaker notes of a slide.",
                Props(
                    P("notes", "string", "Notes text (newlines = new lines)"),
                    P("slide", "number", "Slide number, 1-based (default: the current slide)")),
                Required("notes"));
            Add(list, "export_pdf",
                "Export the presentation as PDF. Default target: the configured exportDir, else the user's Documents\\AgentOffice folder, named after the presentation.",
                Props(
                    P("path", "string", "Output .pdf path (optional — overrides the default export folder)")),
                NoRequired());

            return list;
        }

        // ==================================================================
        // Word toolset (Fase 9) — mirrors src/Bridge/Word/WordActions.cs
        // ==================================================================

        private static ArrayList BuildWord()
        {
            var list = new ArrayList();

            Add(list, "read_document_snapshot",
                "Overview of the active Word document: name, word/paragraph counts, current selection and the heading outline. Cheap — call it to orient yourself before reading or editing.",
                Props(), NoRequired());
            Add(list, "read_text",
                "Read document paragraphs as numbered lines ([n] text). Use startParagraph/maxParagraphs to window into long documents.",
                Props(
                    P("startParagraph", "number", "First paragraph to read, 1-based (default: 1)"),
                    P("maxParagraphs", "number", "How many paragraphs to read (default: 100, max: 300)")),
                NoRequired());
            Add(list, "insert_text",
                "Insert text into the document. Newlines become Word paragraphs. Optionally apply a style to the inserted text.",
                Props(
                    P("text", "string", "Text to insert (newlines = new paragraphs)"),
                    P("position", "string", "Where to insert: \"end\" (default), \"start\", or \"selection\" (replaces the current selection)"),
                    P("style", "string", "Optional style for the inserted text: \"Title\", \"Subtitle\", \"Heading 1\"-\"Heading 6\", \"Quote\", \"Normal\", or a custom style name")),
                Required("text"));
            Add(list, "replace_text",
                "Find and replace text across the whole document.",
                Props(
                    P("find", "string", "Text to find"),
                    P("replace", "string", "Replacement text (may be empty to delete)"),
                    P("matchCase", "boolean", "Case-sensitive search (default: false)"),
                    P("all", "boolean", "Replace every occurrence (default: true) or just the first")),
                Required("find", "replace"));
            Add(list, "apply_style",
                "Apply a paragraph style to a paragraph range, or to the current selection when no range is given.",
                Props(
                    P("style", "string", "Style name: \"Title\", \"Subtitle\", \"Heading 1\"-\"Heading 6\", \"Quote\", \"Normal\", or a custom style name"),
                    P("startParagraph", "number", "First paragraph, 1-based (default: current selection)"),
                    P("endParagraph", "number", "Last paragraph, 1-based inclusive (default: same as startParagraph)")),
                Required("style"));
            Add(list, "insert_table",
                "Insert a table with borders, optionally pre-filled with 2D data and a bold header row.",
                Props(
                    P("rows", "number", "Row count (max 100)"),
                    P("cols", "number", "Column count (max 20)"),
                    P("position", "string", "Where to insert: \"end\" (default) or \"selection\""),
                    P("data", "array", "Optional 2D array of cell values (array of row arrays)"),
                    P("headerRow", "boolean", "Bold the first row (default: false)")),
                Required("rows", "cols"));
            Add(list, "export_pdf",
                "Export the active document as PDF. Default target: the configured exportDir, else the user's Documents\\AgentOffice folder, named after the document.",
                Props(
                    P("path", "string", "Output .pdf path (optional — overrides the default export folder)")),
                NoRequired());

            return list;
        }

        private static ArrayList Build()
        {
            var list = new ArrayList();

            // ---- Core (17) ----
            Add(list, "read_range",
                "Read cell values from a range (2D array). Omit sheet/range to read the active sheet's used range.",
                Props(
                    P("sheet", "string", "Worksheet name (default: active sheet)"),
                    P("range", "string", "A1-style range, e.g. \"A1:D20\" (default: used range)")),
                NoRequired());
            Add(list, "read_sheet_names",
                "List all worksheet names in the active workbook.",
                Props(), NoRequired());
            Add(list, "read_table",
                "Read the rows of a named Excel table.",
                Props(
                    P("sheet", "string", "Worksheet name (default: active sheet)"),
                    P("tableName", "string", "Name of the table to read")),
                Required("tableName"));
            Add(list, "read_selection",
                "Read the currently selected cells (values + address).",
                Props(), NoRequired());
            Add(list, "write_range",
                "Write a 2D array of values into a range (dimensions must match).",
                Props(
                    P("sheet", "string", "Worksheet name (default: active sheet)"),
                    P("range", "string", "Target range, e.g. \"B2:E5\""),
                    P("values", "array", "2D array of row arrays with values to write")),
                Required("range", "values"));
            Add(list, "write_formula",
                "Set a formula in a single cell.",
                Props(
                    P("sheet", "string", "Worksheet name (default: active sheet)"),
                    P("cell", "string", "Cell address, e.g. \"B10\""),
                    P("formula", "string", "Excel formula, e.g. \"=SUM(A1:A9)\"")),
                Required("cell", "formula"));
            Add(list, "create_sheet",
                "Add a new worksheet.",
                Props(P("name", "string", "Name for the new sheet (optional)")),
                NoRequired());
            Add(list, "rename_sheet",
                "Rename a worksheet.",
                Props(
                    P("currentName", "string", "Current sheet name (default: active sheet)"),
                    P("newName", "string", "New name for the sheet")),
                Required("newName"));
            Add(list, "delete_sheet",
                "Remove a worksheet. Destructive — confirm with the user first.",
                Props(P("name", "string", "Name of the sheet to delete")),
                Required("name"));
            Add(list, "format_range",
                "Apply formatting to a range (any combination of the options).",
                Props(
                    P("sheet", "string", "Worksheet name (default: active sheet)"),
                    P("range", "string", "Target range, e.g. \"A1:F1\""),
                    P("bold", "boolean", "Bold on/off"),
                    P("italic", "boolean", "Italic on/off"),
                    P("fill", "string", "Fill color as hex, e.g. \"#FFFF00\""),
                    P("font_color", "string", "Font color as hex, e.g. \"#FF0000\""),
                    P("font_size", "number", "Font size in points"),
                    P("number_format", "string", "Excel number format, e.g. \"0.00%\" or \"#,##0\"")),
                Required("range"));
            Add(list, "create_table",
                "Convert a range into a named Excel table.",
                Props(
                    P("sheet", "string", "Worksheet name (default: active sheet)"),
                    P("range", "string", "Range to convert (first row = headers when has_headers)"),
                    P("name", "string", "Table name (optional)"),
                    P("has_headers", "boolean", "First row holds headers (default true)")),
                Required("range"));
            Add(list, "add_table_row",
                "Append row(s) to a named table.",
                Props(
                    P("sheet", "string", "Worksheet name (default: active sheet)"),
                    P("tableName", "string", "Target table"),
                    P("values", "array", "2D array of row arrays to append")),
                Required("tableName", "values"));
            Add(list, "sort_table",
                "Sort a table by one column.",
                Props(
                    P("sheet", "string", "Worksheet name (default: active sheet)"),
                    P("tableName", "string", "Table to sort"),
                    P("column", "string", "Column name to sort by"),
                    P("ascending", "boolean", "Sort ascending (default true)")),
                Required("tableName", "column"));
            Add(list, "create_chart",
                "Create a chart from a data range.",
                Props(
                    P("sheet", "string", "Worksheet name (default: active sheet)"),
                    P("type", "string", "bar | column | line | pie | doughnut | area | scatter | radar"),
                    P("data_range", "string", "Source range including headers, e.g. \"A1:B10\""),
                    P("title", "string", "Chart title (optional)"),
                    P("position", "string", "Anchor range for the chart, e.g. \"H1:N15\"")),
                Required("type", "data_range"));
            Add(list, "configure_chart",
                "Basic chart tweaks (title, legend).",
                Props(
                    P("sheet", "string", "Worksheet name (default: active sheet)"),
                    P("chartName", "string", "Chart name"),
                    P("title", "string", "New title (optional)"),
                    P("legend", "boolean", "Show/hide the legend (optional)")),
                Required("chartName"));
            Add(list, "auto_fit",
                "Auto-fit column widths / row heights of a range.",
                Props(
                    P("sheet", "string", "Worksheet name (default: active sheet)"),
                    P("range", "string", "Range to auto-fit")),
                Required("range"));
            Add(list, "calculate",
                "Force a full recalculation of the workbook.",
                Props(), NoRequired());

            // ---- Context readers (2) ----
            Add(list, "read_workbook_context",
                "Lightweight workbook context (active sheet, selection, names).",
                Props(), NoRequired());
            Add(list, "read_workbook_snapshot",
                "Text snapshot of the active workbook (sheets, used ranges, value previews). Use it to orient yourself before choosing ranges.",
                Props(), NoRequired());

            // ---- Pro (15) ----
            Add(list, "ping",
                "Check the Excel bridge is alive.",
                Props(), NoRequired());
            Add(list, "undo",
                "Undo the last action in the workbook. Only the user's own edits are undoable — programmatic changes clear Excel's undo stack.",
                Props(), NoRequired());
            Add(list, "evaluate",
                "Evaluate any Excel expression with the full desktop engine and return its value.",
                Props(P("expression", "string", "Excel expression, e.g. \"SUM(A1:A10)\"")),
                Required("expression"));
            Add(list, "insert_equation",
                "Insert a REAL native Excel equation (Office Math) anchored at a cell. Provide LaTeX (auto-converted) or Word UnicodeMath linear format. Uses the clipboard once.",
                Props(
                    P("sheet", "string", "Worksheet name (default: active sheet)"),
                    P("cell", "string", "Anchor cell, e.g. \"B2\""),
                    P("latex", "string", "LaTeX source, e.g. \"x=\\\\frac{-b\\\\pm\\\\sqrt{b^2-4ac}}{2a}\". Prefer conventional textbook form; nested fractions are fine."),
                    P("linear", "string", "Word UnicodeMath, e.g. \"E=mc^2\"")),
                NoRequired());
            Add(list, "execute_mso",
                "Run any built-in Excel Ribbon command by its idMso. Escape hatch for advanced operations; fails gracefully if unavailable in context.",
                Props(P("idMso", "string", "Ribbon command id, e.g. \"CalculateNow\"")),
                Required("idMso"));
            Add(list, "goal_seek",
                "Solve for the input value that makes a formula cell reach a numeric target (e.g. make profit B10 hit 50000 by changing price B5).",
                Props(
                    P("sheet", "string", "Sheet of the formula cell (default: active)"),
                    P("cell", "string", "Formula cell, e.g. \"B10\""),
                    P("goal", "number", "Target value"),
                    P("changingCell", "string", "Input cell to vary, e.g. \"B5\""),
                    P("changingSheet", "string", "Sheet of the input cell (default: same as formula cell)")),
                Required("cell", "goal", "changingCell"));
            Add(list, "export_pdf",
                "Export the whole workbook (or one sheet) to PDF. Default target: the configured exportDir, else the user's Documents\\AgentOffice folder, named after the workbook.",
                Props(
                    P("path", "string", "Output .pdf path (optional — overrides the default export folder)"),
                    P("sheet", "string", "Export only this sheet (optional)")),
                NoRequired());
            Add(list, "create_pivot_table",
                "Create a REAL PivotTable from a range whose first row holds the headers. Omitting destSheet creates a new sheet.",
                Props(
                    P("sourceSheet", "string", "Source worksheet (default: active sheet)"),
                    P("sourceRange", "string", "Source range, e.g. \"A1:D500\""),
                    P("destSheet", "string", "Destination sheet (optional)"),
                    P("destCell", "string", "Destination top-left cell (default \"A3\")"),
                    P("name", "string", "PivotTable name (optional)"),
                    P("rows", "array", "Row field names"),
                    P("columns", "array", "Column field names"),
                    P("values", "array", "Value fields: [{field, function?}] with function sum|count|countnums|average|max|min|product|stdev|stdevp|var|varp (default sum)"),
                    P("filters", "array", "Filter field names")),
                Required("sourceRange"));
            Add(list, "capture_range",
                "Capture a screenshot of cells as a PNG you can SEE (formatting, drawn shapes, embedded images). Default mode covers values plus drawn shapes capped at maxRows×maxCols (80×30); or pass an explicit range, or selection:true. Capture generously — drawn entities extend further than values. Uses the clipboard once.",
                Props(
                    P("sheet", "string", "Worksheet name (default: active sheet)"),
                    P("range", "string", "Explicit range to capture"),
                    P("selection", "boolean", "Capture the user's current selection"),
                    P("maxRows", "number", "Max rows in default mode (default 80)"),
                    P("maxCols", "number", "Max columns in default mode (default 30)")),
                NoRequired());
            Add(list, "add_power_query",
                "Create a REAL Power Query from M code. loadTo \"table\" (default) materializes into a sheet; \"connection\" stores the definition only. On load failure everything is rolled back and the M error returned so you can fix and retry.",
                Props(
                    P("name", "string", "Query name"),
                    P("formula", "string", "M code, e.g. \"let Source = Excel.CurrentWorkbook(){[Name=\"\"Sales\"\"]}[Content] in Source\""),
                    P("loadTo", "string", "table | connection (default table)"),
                    P("destSheet", "string", "Load destination sheet (default: new sheet)"),
                    P("destCell", "string", "Load destination cell (default \"A1\")")),
                Required("name", "formula"));
            Add(list, "configure_chart_pro",
                "Advanced chart options beyond create/configure_chart: combo charts (per-series type), secondary axis, trendlines. `chart` is name or 1-based index (omittable when the sheet has exactly one); `series` is a 1-based index or series name.",
                Props(
                    P("sheet", "string", "Worksheet name (default: active sheet)"),
                    P("chart", "string", "Chart name or 1-based index"),
                    P("seriesTypes", "array", "Per-series type: [{series, type}] with type line|lineMarkers|column|bar|area|pie|scatter"),
                    P("secondaryAxis", "array", "Series to move to the secondary axis"),
                    P("trendlines", "array", "[{series, type, order?, period?, forward?, backward?, displayEquation?, displayRSquared?, name?}] with type linear|exponential|logarithmic|polynomial|power|movingAverage")),
                NoRequired());
            Add(list, "create_slicer",
                "Add a REAL slicer filtering a table column (default) or a PivotTable field. Default placement: on the source sheet, one column right of the source range.",
                Props(
                    P("sourceType", "string", "table | pivot (default table)"),
                    P("sourceName", "string", "Table or PivotTable name"),
                    P("field", "string", "Column/field to filter"),
                    P("sourceSheet", "string", "Sheet holding the source (default: active)"),
                    P("destSheet", "string", "Sheet to place the slicer (default: source sheet)"),
                    P("destCell", "string", "Placement cell (default: right of source)"),
                    P("name", "string", "Slicer name (optional)"),
                    P("caption", "string", "Slicer caption (optional)"),
                    P("width", "number", "Width in points (optional)"),
                    P("height", "number", "Height in points (optional)")),
                Required("sourceName", "field"));
            Add(list, "protect_sheet",
                "Protect (or unprotect) a worksheet with granular allowances. Without a password the protection is casual.",
                Props(
                    P("sheet", "string", "Worksheet name (default: active sheet)"),
                    P("password", "string", "Protection password (optional)"),
                    P("allow", "array", "Allowances: formatCells|formatColumns|formatRows|insertColumns|insertRows|insertHyperlinks|deleteColumns|deleteRows|sort|autofilter|pivottables"),
                    P("unprotect", "boolean", "Set true to remove protection")),
                NoRequired());
            Add(list, "protect_workbook",
                "Protect the workbook structure (blocks adding/moving/deleting sheets) or its window layout.",
                Props(
                    P("password", "string", "Protection password (optional)"),
                    P("structure", "boolean", "Protect structure (default true)"),
                    P("windows", "boolean", "Protect windows (default false)"),
                    P("unprotect", "boolean", "Set true to remove protection")),
                NoRequired());
            Add(list, "page_setup",
                "Print/page layout settings (pairs with export_pdf).",
                Props(
                    P("sheet", "string", "Worksheet name (default: active sheet)"),
                    P("orientation", "string", "portrait | landscape"),
                    P("printArea", "string", "Print area, e.g. \"A1:F50\" (empty string clears)"),
                    P("fitToPagesWide", "number", "Scale to N pages wide"),
                    P("fitToPagesTall", "number", "Scale to N pages tall"),
                    P("paperSize", "string", "letter | legal | a4 | a3 | a5"),
                    P("margins", "object", "{left?, right?, top?, bottom?} in centimeters"),
                    P("printGridlines", "boolean", "Print gridlines"),
                    P("printHeadings", "boolean", "Print row/column headings"),
                    P("centerHorizontally", "boolean", "Center horizontally on the page")),
                NoRequired());

            return list;
        }

        // ==================================================================
        // Schema-building helpers
        // ==================================================================

        private static void Add(
            ArrayList list, string name, string description,
            Dictionary<string, object> properties, ArrayList required)
        {
            var function = new Dictionary<string, object>
            {
                ["name"] = name,
                ["description"] = description,
                ["parameters"] = new Dictionary<string, object>
                {
                    ["type"] = "object",
                    ["properties"] = properties,
                    ["required"] = required,
                },
            };
            list.Add(new Dictionary<string, object>
            {
                ["type"] = "function",
                ["function"] = function,
            });
        }

        private static Dictionary<string, object> Props(
            params Dictionary<string, object>[] props)
        {
            var dict = new Dictionary<string, object>();
            foreach (var prop in props)
            {
                foreach (var kvp in prop) dict[kvp.Key] = kvp.Value;
            }
            return dict;
        }

        private static Dictionary<string, object> P(
            string name, string type, string description)
        {
            return new Dictionary<string, object>
            {
                [name] = new Dictionary<string, object>
                {
                    ["type"] = type,
                    ["description"] = description,
                },
            };
        }

        private static ArrayList Required(params string[] names)
        {
            return new ArrayList(names);
        }

        private static ArrayList NoRequired()
        {
            return new ArrayList();
        }
    }
}
