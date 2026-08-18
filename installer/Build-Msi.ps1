# Builds AgentOffice-<Version>.msi into installer\out\ using WiX v3
# (repo-local tools\wix314 — no .NET SDK or VS extension needed).
#
# Usage (from anywhere):
#   installer\Build-Msi.ps1 [-Configuration Release] [-ProductVersion 0.1.0]
#
# Prereqs:
#   1. The VSTO add-in built:  MSBuild /p:Configuration=<same>
#   2. The chat UI built:      pnpm build  (ui\dist)
[CmdletBinding()]
param(
  [string]$Configuration = "Release",
  [string]$ProductVersion = "1.0.0"
)
$ErrorActionPreference = "Stop"

$repoRoot  = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$wixDir    = Join-Path $repoRoot "tools\wix314"
$sourceDir = Join-Path $repoRoot "src\bin\$Configuration"
$uiDir     = Join-Path $repoRoot "ui\dist"
$iconSource = Join-Path $repoRoot "src\Resources\agent-office.ico"
$outDir    = Join-Path $PSScriptRoot "out"
$wxs       = Join-Path $PSScriptRoot "Product.wxs"
$uiWxs     = Join-Path $outDir "UIComponents.wxs"

if (-not (Test-Path $iconSource)) { throw "Missing product icon: $iconSource" }

foreach ($f in @("AgentOffice.dll", "AgentOffice.dll.manifest", "AgentOffice.vsto",
                 "Microsoft.Office.Tools.Common.v4.0.Utilities.dll", "Microsoft.Office.Tools.Excel.v4.0.Utilities.dll",
                 "Microsoft.Web.WebView2.Core.dll", "Microsoft.Web.WebView2.WinForms.dll")) {
  if (-not (Test-Path (Join-Path $sourceDir $f))) {
    throw "Missing '$f' in $sourceDir — build the add-in first (MSBuild /p:Configuration=$Configuration)."
  }
}
foreach ($f in @("runtimes\win-x64\native\WebView2Loader.dll", "runtimes\win-x86\native\WebView2Loader.dll")) {
  if (-not (Test-Path (Join-Path $sourceDir $f))) {
    throw "Missing '$f' in $sourceDir — build the add-in first (MSBuild /p:Configuration=$Configuration)."
  }
}
if (-not (Test-Path (Join-Path $uiDir "index.html"))) {
  throw "Missing chat UI at $uiDir — build the web UI first (pnpm build in ui)."
}

New-Item -ItemType Directory -Path $outDir -Force | Out-Null

# Harvest the UI bundle (hashed asset filenames change every build — heat
# regenerates the component list each time; -gg makes per-build GUIDs, which
# is safe here because upgrades are full MajorUpgrade remove-then-install).
& (Join-Path $wixDir "heat.exe") dir $uiDir -nologo `
  -cg UIComponents -dr UIFolder -gg -g1 -sfrag -sreg -srd `
  -var var.UiSourceDir -out $uiWxs
if ($LASTEXITCODE -ne 0) { throw "heat failed ($LASTEXITCODE)" }

& (Join-Path $wixDir "candle.exe") -nologo -arch x86 `
  "-dSourceDir=$sourceDir" "-dUiSourceDir=$uiDir" `
  "-dProductVersion=$ProductVersion" "-dIconSource=$iconSource" `
  $wxs $uiWxs -out "$outDir\"
if ($LASTEXITCODE -ne 0) { throw "candle failed ($LASTEXITCODE)" }

$msi = Join-Path $outDir "AgentOffice-$ProductVersion.msi"
# ICE61: same-version upgrades intentionally allowed via MajorUpgrade.
# ICE91: benign — this package is per-user ONLY by design (locked decision).
# ICE38: heat-harvested UI files live under %LOCALAPPDATA% with File keypaths
#        (per-user payload by design; the add-in's own components carry HKCU
#        registry keypaths).
# ICE64: the heat-generated ui\assets subdirectory has no RemoveFolder row;
#        worst case an EMPTY folder shell survives uninstall — user data
#        (settings.json, host.log, WebView2 profile) intentionally survives
#        anyway (current convention).
& (Join-Path $wixDir "light.exe") -nologo -sice:ICE61 -sice:ICE91 -sice:ICE38 -sice:ICE64 `
  "$outDir\Product.wixobj" "$outDir\UIComponents.wixobj" -out $msi
if ($LASTEXITCODE -ne 0) { throw "light failed ($LASTEXITCODE)" }

Write-Host "Built $msi"
