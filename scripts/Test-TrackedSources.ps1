<#
.SYNOPSIS
  Fails when a project file compiles or embeds a source that git does not track.
#>

$ErrorActionPreference = 'Stop'

$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$tracked = @{}
git -C $root ls-files | ForEach-Object { $tracked[$_.ToLowerInvariant()] = $true }

$missing = @()
$projects = git -C $root ls-files '*.csproj'
foreach ($project in $projects) {
    $projectPath = Join-Path $root $project
    $projectDir = Split-Path $projectPath -Parent
    [xml]$xml = Get-Content $projectPath -Raw

    $items = @($xml.Project.ItemGroup.Compile) + @($xml.Project.ItemGroup.EmbeddedResource)
    foreach ($item in $items) {
        if (-not $item -or -not $item.Include) { continue }
        $full = [System.IO.Path]::GetFullPath((Join-Path $projectDir $item.Include))
        $relative = $full.Substring($root.Path.Length + 1).Replace('\', '/')
        if (-not $tracked.ContainsKey($relative.ToLowerInvariant())) {
            $missing += "$project -> $relative"
        }
    }
}

if ($missing.Count -gt 0) {
    Write-Host 'Sources referenced by a project but not tracked by git:'
    $missing | ForEach-Object { Write-Host "  $_" }
    exit 1
}

Write-Host "All project sources are tracked ($($projects.Count) project(s))."
