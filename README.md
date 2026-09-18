<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset=".github/assets/logo-dark-512.png">
    <img src=".github/assets/logo-512.png" alt="Agent Office logo" width="180" />
  </picture>
</p>

# Agent Office

An open-source, agentic AI add-in for **Microsoft Excel, Word and PowerPoint**.
Agent Office doesn't just answer questions — it executes actions on your
**live document** in a closed loop: it reads, writes, formats, charts,
**looks at the results**, and iterates until the job is done.

The add-in is **fully native and self-contained**: a single VSTO add-in embeds
the chat UI (WebView2) and executes every action through the host Office
application's COM object model — no manifests, no sideloading, no Office.js,
no local servers. You point it at any **OpenAI-compatible** LLM endpoint
(API key + model) and everything else stays on your machine.
**One MSI installs everything.**

## Capabilities

The agent exposes actions tailored to the active Office app:

- **Excel** — read/write ranges, tables, charts, formatting, pivot tables,
  Power Query, goal seek, sheet protection, PDF export, native equation
  insertion, range capture (vision), and Ribbon command execution.
- **Word** — read/write paragraphs, styles, tables, comments, bookmarks, find
  & replace, and document export.
- **PowerPoint** — list slides, add shapes, edit text, apply themes, export,
  and capture slide images.

Everything runs **locally and in-process**: no open ports, no proxy — the only
traffic that leaves your machine is the LLM API you configured yourself.

> **Platform:** Windows desktop Office (Excel/Word/PowerPoint 2016+ / Microsoft 365).

---

## Quick start (users)

Everything installs per-user — **no admin rights needed**.

### 0. Prerequisites

- Windows 10/11 with Office 2016+ or Microsoft 365 **desktop**.
- [.NET Framework 4.8](https://dotnet.microsoft.com/download/dotnet-framework/net48) (inbox on Windows 10/11).
- [VSTO Runtime](https://www.microsoft.com/en-us/download/details.aspx?id=105522) and the
  [WebView2 Runtime](https://go.microsoft.com/fwlink/p/?LinkId=2124703) — both are
  present on most Windows 11 machines; the MSI checks for them and tells you if
  either is missing.
- An **OpenAI-compatible LLM endpoint** (API key + model id). OpenRouter, Gemini
  and local providers are also supported.

### 1. Install the add-in

Run `AgentOffice-x.y.z.msi` from [Releases](https://github.com/blazzbyte/agent-office/releases). On the **first Office
start** afterwards you'll see *"Microsoft Office Customization Installer —
Publisher cannot be verified"* → click **Install**. This prompt is expected for
unsigned builds (see [Roadmap](#roadmap)); it appears only once.

### 2. Open Excel, Word or PowerPoint

The **Agent Office** taskpane opens on the right.

### 3. Configure the model

1. Click the **settings icon** in the taskpane header.
2. Select your provider preset (or enter a custom base URL).
3. Enter your **API key** and **model**, then save.

### 4. Try it out

- Excel: *"read A1:B5"*, *"create a sales table with a column chart"*.
- Word: *"read the first paragraph"*, *"make the title bold"*.
- PowerPoint: *"add a summary slide with three key takeaways"*.

## Troubleshooting

- **Taskpane doesn't load / shows an error label:** check
  `%LOCALAPPDATA%\AgentOffice\host.log` — the add-in logs every failure there.
- **Add-in not listed in Office:** check **File → Options → Add-ins → Disabled
  Items** and re-enable *Agent Office* if it was moved there after a crash.

## Roadmap

- **Code signing** (removes the VSTO trust prompt + SmartScreen).
- **Auto-update** for the MSI.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) — dev setup, architecture notes and house
rules. Release history lives in [CHANGELOG.md](CHANGELOG.md); to report a
vulnerability use the private flow in [SECURITY.md](SECURITY.md) (never a public
issue).

## License

[AGPL-3.0](LICENSE) — free for the community; any derivative work (even offered as
a network service) must stay open under the same terms.
