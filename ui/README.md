# Agent Office — chat UI

The Agent Office chat UI (React + TypeScript + Tailwind/shadcn, Vite). It is
**embedded** in the native VSTO add-in: the WebView2 taskpane serves the built
`dist/` under the virtual origin `https://app.agentoffice.local`, and every
network call is answered natively by the host (in-proc agent + in-process COM
executor). There is no Office.js, no proxy and no manifest.

User docs and install: [root README](../README.md).
Dev setup (full loop, PR rules): [CONTRIBUTING.md](../CONTRIBUTING.md).

## Commands

```powershell
pnpm install     # deps
pnpm build       # typecheck (tsc -b) + production bundle to dist/
pnpm dev         # browser-only UI work (actions need the native host)
```

The add-in picks up this repo's `dist/` automatically (see `UiLocator` in the
VSTO project), so the iteration loop is: `pnpm build` → restart the Office app.

## Notes

- **No `window.fetch` assumptions:** backend calls are relative to the page
  origin; the host intercepts them. The SSE chat stream goes over a
  host-injected fetch shim (`chrome.webview.postMessage` chunks) because
  WebView2 buffers intercepted long-lived responses.
- `window.__agentOfficeHost` (injected before page load) carries the workbook
  name + add-in version.
- Vite dev does **not** typecheck — run `npx tsc -b --noEmit` (or `pnpm build`)
  before pushing.
- Connection config (API key + model) is entered in the taskpane's connection
  settings sheet and persisted host-side in `%LOCALAPPDATA%\AgentOffice\settings.json`.
  The backend address is owned by the host (virtual origin), not this UI.
