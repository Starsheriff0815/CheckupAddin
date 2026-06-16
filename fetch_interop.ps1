<#
.SYNOPSIS
  Populates lib\<year>\Autodesk.Inventor.Interop.dll from the locally installed
  Autodesk Inventor versions.

.DESCRIPTION
  The Inventor interop PIA is part of each Autodesk Inventor install, not our
  source, so it is gitignored (see .gitignore). Run this once on any dev machine
  to copy each installed version's interop into lib\<year>\ so the matching
  CheckupAddin<year> variant can build. Versions that are not installed are
  skipped with a warning.

.EXAMPLE
  pwsh ./fetch_interop.ps1
#>
[CmdletBinding()]
param(
    [int[]] $Years = @(2024, 2025, 2026, 2027)
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

foreach ($y in $Years) {
    $src = "C:\Program Files\Autodesk\Inventor $y\Bin\Public Assemblies\Autodesk.Inventor.Interop.dll"
    $dstDir = Join-Path $root "lib\$y"
    $dst = Join-Path $dstDir 'Autodesk.Inventor.Interop.dll'

    if (-not (Test-Path $src)) {
        Write-Warning "Inventor $y not installed (missing: $src) - skipped."
        continue
    }

    New-Item -ItemType Directory -Force $dstDir | Out-Null
    Copy-Item $src $dst -Force
    $ver = (Get-Item $dst).VersionInfo.FileVersion
    Write-Host "lib\$y\Autodesk.Inventor.Interop.dll  <-  Inventor $y  ($ver)"
}
