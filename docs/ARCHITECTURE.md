# Agent Office — native Office VSTO add-in

The all-in-one Office assistant for **Excel, Word and PowerPoint**. It embeds the
chat UI ([ui/](../ui), built React app) in a WebView2 taskpane and executes
actions in-process through the host application's COM object model.

There is **no loopback HTTP server**, no external gateway and no Office.js: the
taskpane intercepts every request in-process, and the LLM agent runs natively
inside the add-in.

```
WebView2 taskpane (https://app.agentoffice.local — virtual origin)
        │  every request intercepted (WebResourceRequested)
        ├─ static files          → bundled ui/ folder
        ├─ /v1/* /health         → AgentHost in-proc (LLM, sessions, tool calling)
        └─ /bridge/*             → BridgeHost in-proc → app-specific action router
                                           │
                           ExcelSynchronizer → Office UI thread → COM OM
```

## Prerequisites

- **Visual Studio 2022+** with the **Office/SharePoint development** workload —
  or **Visual Studio Build Tools** (MSBuild-only; the Office workload component
  installs the required `OfficeTools` targets + VSTO reference assemblies).
- **.NET Framework 4.8 targeting pack**.
- **WebView2 Runtime** on the machine (the MSI also checks for it).
- Windows + Office desktop (Excel, Word or PowerPoint; VSTO is Windows-only).

## Quick start (dev loop)

1. **One-time:** generate the development signing key (git-ignored,
   machine-specific; required by the VSTO build targets — also syncs its
   thumbprint into the `.csproj`):

   ```powershell
   powershell -ExecutionPolicy Bypass -File scripts\New-DevCertificate.ps1
   ```

2. **Build the chat UI** (the add-in serves the repo `ui/dist/` automatically
   in dev — see `UiLocator`):

   ```powershell
   cd ui; pnpm build; cd ..
   ```

3. **Build the add-in** (auto-registers it for the current user):

   ```powershell
   & "C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\MSBuild\Current\Bin\MSBuild.exe" `
     AgentOffice.sln /p:Configuration=Debug
   ```

4. **Start Office** — the Agent Office taskpane opens; the lightning badge is
   always on (the executor is built in). Verify actions by chatting:
   *"lee A1:B5"* in Excel, *"lee el primer párrafo"* in Word, or
   *"lista las diapositivas"* in PowerPoint.

## Installer (MSI)

See [installer/](../installer/): `Build-Msi.ps1` produces a per-user WiX MSI
(HKCU registration, VSTO + WebView2 runtime checks, bundled UI, MajorUpgrade
between Agent Office versions — own UpgradeCode, never touches other products).

## Virtual API (answered in-process)

| Method | Route | Purpose |
|---|---|---|
| `GET` | `/bridge/v1/health` | Discovery + capability advertisement (`actions[]`). Never touches COM. |
| `GET` | `/bridge/v1/token` | Protocol compatibility shim (returns `in-proc`). |
| `POST` | `/bridge/v1/execute` | Execute an action. Body: `{ id, action, params, workbookHint? }`. Returns a `StepResult`-shaped JSON; `data.code` carries machine-readable errors (`EXCEL_BUSY`, `WORKBOOK_MISMATCH`, `NO_ACTIVE_WORKBOOK`, `INVALID_PARAMS`, `UNSUPPORTED_ACTION`, `TIMEOUT`, `COM_ERROR`). |
| `GET` | `/v1/toolsets` | Agent-facing capability list. |
| `POST` | `/v1/chat/stream` | SSE chat stream (injected fetch shim + `postMessage` chunk pump to avoid response buffering). |

Actions are grouped per Office app. See the implementations under
`src/Bridge/Actions/` (Excel), `src/Bridge/Word/` and `src/Bridge/PowerPoint/`.

## Troubleshooting

- **Log file:** `%LOCALAPPDATA%\AgentOffice\host.log`.
- **Settings:** `%LOCALAPPDATA%\AgentOffice\settings.json`.
- **Add-in not listed in Office:** check **File → Options → Add-ins → Disabled
  Items** and re-enable *Agent Office* if it was moved there after a crash.
- **No ports at all:** the add-in opens zero sockets.

## Layout

```
src/                                (AgentOffice.csproj — the whole add-in)
├── ThisAddIn.cs / .Designer.cs     VSTO entry point (host + taskpane lifecycle)
├── Properties/AssemblyInfo.cs
├── Taskpane/
│   ├── AgentTaskpaneControl.cs     WebView2 host control (virtual origin, host info)
│   ├── RequestInterceptor.cs       Static files + in-proc API routing
│   ├── StreamBridge.cs             SSE chat over postMessage (+ injected fetch shim)
│   ├── HostSettings.cs             settings.json + env overrides
│   └── UiLocator.cs                Finds the bundled UI (ui/ → repo dist/ fallback)
├── Bridge/
│   ├── BridgeHost.cs               Composition root + /bridge/* routing
│   ├── ExcelSynchronizer.cs        Marshals work onto the Office UI thread
│   ├── Log.cs                      %LOCALAPPDATA% file logger
│   ├── Protocol/Protocol.cs        JSON wire format (StepResult-shaped)
│   ├── Actions/                    Excel action router + implementations
│   ├── Word/                       Word action router + implementations
│   └── PowerPoint/                 PowerPoint action router + implementations
└── Agent/
    ├── AgentHost.cs                In-proc LLM host: chat, sessions, tool calling
    ├── AgentRunner.cs              Turn execution loop
    ├── LlmClient.cs                Provider-specific LLM clients
    ├── ModelRegistry.cs            Available models, modalities and pricing hints
    ├── ProviderSettings.cs         Provider + model configuration
    ├── SessionStore.cs             Persistent session storage
    ├── ImageGeneration.cs          Image generation dispatcher
    ├── ImageSearch.cs              Openverse image search
    └── Tools/                      Native tool schemas and invocations
```
