# Contributing to Agent Office

Thanks for helping out! This is the full development setup. If you just want to
*use* the product, see [README.md](README.md) instead.

## Repository layout

```
agent-office/                      (this repo = the product)
├─ AgentOffice.sln                 The single solution
├─ src/                           C# VSTO add-in (.NET Framework 4.8) — AgentOffice.csproj
│                                (taskpane + in-proc agent + COM executor)
├─ ui/                            React + TS + Tailwind/shadcn chat UI (Vite)
│                                → built to dist/ and EMBEDDED in the add-in
├─ installer/                     WiX v3 MSI packaging (Build-Msi.ps1)
├─ libs/webview2/                 Vendored WebView2 SDK (no NuGet)
├─ scripts/                       setup-dev.ps1, New-DevCertificate.ps1
└─ docs/                          ARCHITECTURE.md
```

## Architecture in one paragraph

The VSTO add-in hosts a WebView2 taskpane that serves the built React app under
the virtual origin `https://app.agentoffice.local`. A `WebResourceRequested`
interceptor answers **every** request on that origin natively: static files from
the bundled `ui/` folder, `/api/*` + `/v1/*` + `/health` (answered by the
**in-proc agent**), and `/bridge/*` answered **in-process** by the COM action
executor (Excel, Word or PowerPoint tools depending on the host app). The one
exception is the SSE chat stream: WebView2 buffers intercepted responses, so
`POST /api/sessions/{id}/chat/stream` is handled by a host-injected `fetch` shim
+ a `postMessage` chunk pump (`StreamBridge`). There is no HTTP server, no proxy,
no Office.js anywhere.

## Prerequisites

- Windows 10/11 + Office 2016/Microsoft 365 **desktop** (Excel, Word or PowerPoint)
- Node.js 22+ and pnpm
- **Visual Studio Build Tools** with the *Office/SharePoint development* workload
  (MSBuild + VSTO targets — full VS not required)
- .NET Framework 4.8 Developer Pack (usually inbox)
- WebView2 Runtime (present on any up-to-date Windows 10/11)
- An OpenAI-compatible LLM endpoint (API key + model id)

## Dev loop

1. **Chat UI** —
   ```powershell
   cd ui
   pnpm install      # first time
   pnpm build        # → dist/
   ```
   The add-in picks up the repo `dist/` automatically (see `UiLocator`), so the
   loop is: `pnpm build` → restart Excel → look. For pure UI work you can also
   `pnpm dev` and open `http://localhost:5173` in a browser (actions need the
   host, so execution won't work there — UI only).
2. **Add-in** — if this is your first build, generate the dev signing key
   (git-ignored, machine-specific — the VSTO build needs it to sign the ClickOnce
   manifests):
   ```powershell
   powershell -ExecutionPolicy Bypass -File scripts\New-DevCertificate.ps1
   ```
    Then build (auto-registers the add-in in
    `HKCU\Software\Microsoft\Office\{Excel,Word,PowerPoint}\Addins\AgentOffice`
    — nothing else to install):
    ```powershell
    & "C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\MSBuild\Current\Bin\MSBuild.exe" `
      AgentOffice.sln /p:Configuration=Debug
    ```
    (Re)start the Office app after each add-in build; the VSTO loader only reads add-ins at startup.

### Useful overrides (env vars)

| Variable | Purpose |
|---|---|
| `AGENT_OFFICE_UI_DIR` | Serve a different UI folder in the taskpane (e.g. a hot `dist/` elsewhere) |

### Logs

`%LOCALAPPDATA%\AgentOffice\host.log` — startup, interception, and action errors
all land here. The WebView2 profile lives next to it (`...\AgentOffice\WebView2`).

## Building the MSI

```powershell
# 1. Chat UI
cd ui; pnpm build; cd ..
# 2. Add-in in Release
& ".../MSBuild.exe" AgentOffice.sln /p:Configuration=Release
# 3. Package
installer\Build-Msi.ps1 -ProductVersion x.y.z
# → installer\out\AgentOffice-x.y.z.msi
```

The MSI is per-user (HKCU registration, no admin), checks for the VSTO Runtime
and the WebView2 Runtime, and bundles the UI + WebView2 assemblies + both native
loaders. Agent Office has its own UpgradeCode and add-in key — it never touches
other add-in installs (upgrades between Agent Office versions work via
MajorUpgrade).
**Note:** installing the MSI on a dev machine repoints the add-in registration to
`%LOCALAPPDATA%` — rebuild the Debug config to restore the dev loop afterwards.

## House rules for PRs

- Web UI: `npx tsc -b --noEmit` must be clean (Vite dev doesn't typecheck —
  always verify before pushing); follow the existing React/TS style.
- Add-in: MSBuild Debug + Release must compile; test actions against real Office
  apps.
- Don't commit secrets.

## When things break

Excel/Word/PowerPoint add-in not loading? Taskpane blank? Check
`%LOCALAPPDATA%\AgentOffice\host.log` first, then verify the add-in is not in
**File → Options → Add-ins → Disabled Items**.
