<#
.SYNOPSIS
  First-run dev environment bootstrap for Agent Office.

.DESCRIPTION
  1. Installs the chat UI dependencies (pnpm).
  2. Ensures the VSTO signing certificate exists (New-DevCertificate.ps1).
  3. Verifies build tools (pnpm, MSBuild).
  4. Prints the manual steps that remain.

  Safe to re-run: every step is idempotent.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
# pnpm writes informational output to stderr; don't treat that as failure.
if ($PSVersionTable.PSVersion.Major -ge 7) { $PSNativeCommandUseErrorActionPreference = $false }
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$webApp   = Join-Path $repoRoot "ui"

function Write-Step($msg) { Write-Host "`n==> $msg" -ForegroundColor Cyan }

# --- 1. Prerequisites -------------------------------------------------------
Write-Step "Checking prerequisites"
$missing = @()

if (-not (Get-Command pnpm -ErrorAction SilentlyContinue)) { $missing += "pnpm  (https://pnpm.io/installation)" }
else { Write-Host "  pnpm          $((pnpm --version))" }

$msbuild = "C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
if (Test-Path $msbuild) { Write-Host "  MSBuild       found (VS BuildTools 18)" }
else {
  $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
  if (Test-Path $vswhere) {
    $found = & $vswhere -latest -requires Microsoft.Component.MSBuild -find MSBuild\**\Bin\MSBuild.exe | Select-Object -First 1
    if ($found) { $msbuild = $found; Write-Host "  MSBuild       $msbuild" }
  }
  if (-not (Test-Path $msbuild)) { $missing += "MSBuild (Visual Studio Build Tools with Office/SharePoint workload)" }
}

if ($missing.Count) {
  Write-Host "`nMissing prerequisites:" -ForegroundColor Red
  $missing | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
  exit 1
}

# --- 2. Chat UI dependencies -------------------------------------------------
Write-Step "Installing chat UI dependencies (pnpm install)"
Push-Location $webApp
try {
  pnpm install --frozen-lockfile
  if ($LASTEXITCODE -ne 0) { throw "pnpm install failed (exit $LASTEXITCODE)" }
} finally { Pop-Location }

# --- 3. VSTO dev certificate ------------------------------------------------
Write-Step "Ensuring VSTO dev certificate"
& (Join-Path $repoRoot "scripts\New-DevCertificate.ps1")

# --- 3b. Multi-host registration (Word + PowerPoint) ------------------------
# The VSTO build only registers the add-in under Excel; Agent Office is
# multi-host, so mirror the same key (idempotent) into Word and PowerPoint.
Write-Step "Mirroring add-in registration to Word and PowerPoint"
$excelKey = "HKCU:\Software\Microsoft\Office\Excel\Addins\AgentOffice"
if (Test-Path $excelKey) {
  $values = Get-ItemProperty $excelKey
  foreach ($app in "Word", "PowerPoint") {
    $dst = "HKCU:\Software\Microsoft\Office\$app\Addins\AgentOffice"
    New-Item -Path $dst -Force | Out-Null
    Set-ItemProperty -Path $dst -Name Description  -Value $values.Description
    Set-ItemProperty -Path $dst -Name FriendlyName -Value $values.FriendlyName
    Set-ItemProperty -Path $dst -Name LoadBehavior -Value $values.LoadBehavior
    Set-ItemProperty -Path $dst -Name Manifest     -Value $values.Manifest
    Write-Host "  registered under $app"
  }
} else {
  Write-Host "  Excel key not found yet — build the add-in (step 2 below), then re-run."
}

# --- 4. Remaining manual steps ----------------------------------------------
Write-Step "Bootstrap complete. Daily dev loop"
Write-Host @"

  1. Build the chat UI (the add-in serves the repo dist/ automatically):
       pnpm --dir ui build

  2. Build the add-in (registers the VSTO add-in in HKCU):
       & "$msbuild" AgentOffice.sln /p:Configuration=Debug

  3. (Re)start Excel — the Agent Office taskpane opens on the right.
     The VSTO loader only reads add-ins at startup, so restart Excel
     after each add-in build. Word and PowerPoint load the same add-in
     (chat-only there until their toolsets land).

  Useful overrides (env vars):
    AGENT_OFFICE_UI_DIR        serve a different UI folder in the taskpane

  Logs: %LOCALAPPDATA%\AgentOffice\host.log

  See CONTRIBUTING.md for the full architecture walkthrough.
"@
