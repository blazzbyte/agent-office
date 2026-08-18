# Security Policy

## Reporting a vulnerability

Please **do not open a public issue** for security reports.

Use GitHub's **Private vulnerability reporting** (repo → Security → Advisories →
*Report a vulnerability*) or contact the maintainer through their GitHub profile.
Include: affected component (add-in / chat UI / installer / docs),
repro steps, and impact. You'll get an acknowledgement as soon as possible.

## Scope & threat model notes

Agent Office is a **local-first** system: the add-in opens **zero network
listeners** — all UI↔host traffic is in-process (WebView2 request interception
on the virtual origin `https://app.agentoffice.local` + WebView2 messages), and
the only outbound traffic is the LLM provider API the user configured
(OpenAI-compatible endpoint, Bearer API key entered by the user, stored in the
WebView2 profile's localStorage).

Notable surface:

- The request interceptor answers any request the *page* makes — page content
  is the bundled UI only (no remote content is ever navigated to).
- The injected `window.__agentOfficeHost` / fetch-shim scripts run before page
  scripts and are fully host-controlled.
- COM actions execute with the user's own Excel privileges, on the workbook the
  user has open (a workbook-name hint guards against multi-instance mix-ups).
- The provider API key never leaves the local WebView2 profile.

Reports about weaknesses in this surface (e.g. ways to drive the action
executor from outside the taskpane, request-interception escapes, unsafe COM
action parameters, secret leakage) are especially welcome.

## Supported versions

| Version | Supported |
|---|---|
| latest release | ✅ |
| older releases | ❌ (upgrade first) |
