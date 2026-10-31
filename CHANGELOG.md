# Changelog

All notable changes to **Agent Office** are documented here.

Format: [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
versioning: [Semantic Versioning](https://semver.org/).

## [1.1.0] - 2026-10-31

### Added

- `customInstructions` in `settings.json` (string or array of strings):
  standing instructions appended to the system prompt on every request,
  including existing sessions (#3).
- UI available in English and Spanish, with a Language selector in Settings
  (automatic follows Office/Windows). `settings.json` accepts `"language"`
  and a `"translations"` map to override any UI text (#2).
- Welcome screen with its own subtitle and suggestions for Excel, Word and
  PowerPoint.

### Fixed

- The UI never received the host app from `/health` (dropped by schema
  validation), so the header never showed it and Word/PowerPoint got Excel's
  welcome screen.

### Security

- API keys (`apiKey`, `imageApiKey`) are now stored encrypted with Windows
  DPAPI, bound to the current Windows user. Existing plain-text keys are
  encrypted automatically on first read (#1). Downgrading to 1.0.0 requires
  re-entering the key.

## [1.0.0] - 2026-09-18

### Added

- Initial release of **Agent Office**: a native VSTO add-in for Excel, Word and
  PowerPoint with an embedded WebView2 chat UI and a fully in-process LLM agent.
- **In-proc LLM agent** (`src/Agent/`): OpenAI-compatible streaming client,
  native function-calling loop, local SQLite session persistence, context
  compaction, model registry with models.dev enrichment, cost and context
  metrics.
- **Multi-app support**: the same assembly loads in Excel, Word and PowerPoint.
  `AppContext` detects the host from the process name and composes the persona:
  per-app action routers, system prompts and toolsets; `/health` reports
  `host_app`.
- **Excel action set**: read/write ranges and tables, formatting, charts,
  pivot tables, Power Query, goal seek, sheet/workbook protection, PDF export,
  native OMML equation insertion, range capture (vision) and arbitrary Ribbon
  command execution.
- **Word toolset**: read document snapshot/paragraphs, insert and replace text,
  apply styles, insert tables, export PDF.
- **PowerPoint toolset**: read presentation/slide snapshots, add/duplicate/delete
  slides, set title/body text, insert text boxes, shapes and tables, manage
  speaker notes, list/apply Office themes, insert pictures (including as
  full-slide background), export PDF.
- **Image generation**: preset-driven provider section (Off / OpenAI / OpenRouter
  / Google Gemini / Local A1111-SD.Next / Custom) with its own model id, API key
  and base URL. Host-side dispatcher supports `openai-images`, `gemini` and
  `a1111` wire shapes; generated images feed a session buffer and render in chat.
- **`search_image` tool (all apps, zero config)**: web photo search via Openverse
  (~300M commercially-licensed images, no API key). Top hits download host-side
  with title/author/license/dimensions metadata; `insert_picture` places them and
  appends CC attribution to PowerPoint speaker notes automatically.
- **Image-output requests**: models marked `out:img` receive
  `modalities: ["image","text"]`. Generated images render in chat and feed the
  `insert_picture` buffer (rolling cap of 8 per session).
- **Provider settings sheet**: API key, base URL, model, context window and max
  tokens; persisted host-side in `%LOCALAPPDATA%\AgentOffice\settings.json`.
- **Multimodal chat**: image and file attachments, PDF support (native for
  capable models or page-rendered via pdf.js), image outputs, and wire gating
  that drops parts the active model cannot process.
- **Session history** with infinite scroll, automatic session titles,
  per-message run metadata (model, tokens, cost, duration) and a run summary
  line under the final assistant bubble.
- **Metrics footer**: last-step context footprint vs. model window plus running
  session cost.
- **Configurable export folder**: `export_pdf` resolves the target as explicit
  `path` → `exportDir` in settings.json → `%USERPROFILE%\Documents\AgentOffice`
  (created on demand). Exporting an unsaved document no longer fails.

### Fixed

- **Google Gemini tool calling and reasoning support**: resolved parameter
  concatenation when streaming deltas omit `index`, and preserved
  `thought_signature` / `extra_content` required by Gemini 2.5 / 3.x reasoning models.
- **OpenRouter & Cloudflare TLS handshake**: forced modern TLS 1.2/1.3 protocols
  in the .NET host and added standard `User-Agent`, `HTTP-Referer`, and `X-Title`
  headers to prevent connection resets and 502 errors when fetching the model catalog.
- **Word/PowerPoint load failure on non-Excel hosts**: the VSTO-generated
  designer requested Excel-typed host items that the runtime could not supply
  outside Excel. The designer is now host-agnostic: the constructor takes the
  `Microsoft.Office.Tools.Factory` interface and `Application` is late-bound,
  cast only at Excel-guarded use sites.
- **PowerPoint smart-tag disposal crash**: `SmartTagCollectionImpl` arrives
  already disposed in PowerPoint. The designer's smart-tag touch points now
  swallow `ObjectDisposedException` so load continues cleanly.
