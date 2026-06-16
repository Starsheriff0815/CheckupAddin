<#
.SYNOPSIS
  Builds, packages, and (optionally) publishes the CheckupAddin release bundles
  for each Inventor year variant. Run locally on a machine that has the matching
  Inventor versions installed (the interop PIA is gitignored and supplied from
  each local install -- run fetch_interop.ps1 first if lib\<year>\ is empty).

.DESCRIPTION
  Replaces the former GitHub Actions auto-build: windows-latest has no Inventor,
  so releases are built locally. For each requested year this script:
    1. msbuild Restore + Build (Release, x64)
    2. Stages the deployable files into dist\release_<year>\
    3. Zips it to dist\CheckupAddin<year>_<Tag>.zip
  With -Publish it then creates the GitHub Release and uploads the zips via gh.

  Packaging differs by framework:
    net48  (2024, 2025): CheckupAddIn<year>.dll + Newtonsoft.Json.dll
    net8   (2026, 2027): CheckupAddIn.dll + .comhost.dll + .runtimeconfig.json

.PARAMETER Tag
  Release tag used in the zip names and (with -Publish) the GitHub release.
  Defaults to "v" + the <Version> read from the 2026 csproj.

.PARAMETER Years
  Which variants to build. Default: 2024, 2025, 2026, 2027.

.PARAMETER Publish
  After building, create the GitHub release for $Tag and upload the zips (gh CLI).

.EXAMPLE
  pwsh ./build_release.ps1                 # build + zip all four into dist\
.EXAMPLE
  pwsh ./build_release.ps1 -Tag v0.14.0 -Publish
#>
[CmdletBinding()]
param(
    [string]   $Tag,
    [int[]]    $Years = @(2024, 2025, 2026, 2027),
    [switch]   $Publish,
    [string]   $Repo = 'Starsheriff0815/CheckupAddin'
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$dist = Join-Path $root 'dist'

# --- Locate MSBuild (VS install, any edition) ---------------------------------
$msbuild = (Get-Command msbuild -ErrorAction SilentlyContinue).Source
if (-not $msbuild) {
    $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    if (Test-Path $vswhere) {
        $msbuild = & $vswhere -latest -requires Microsoft.Component.MSBuild `
            -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
    }
}
if (-not $msbuild) { throw 'MSBuild not found. Install Visual Studio or the Build Tools.' }

# --- Locate 7-Zip (preferred archiver; falls back to Compress-Archive) ---------
$sevenZip = @(
    (Get-Command 7z.exe -ErrorAction SilentlyContinue).Source
    "$env:ProgramFiles\7-Zip\7z.exe"
    "${env:ProgramFiles(x86)}\7-Zip\7z.exe"
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1

function New-Bundle {
    param([string] $SourceDir, [string] $ZipPath)
    if (Test-Path $ZipPath) { Remove-Item $ZipPath -Force }
    if ($sevenZip) {
        # Run from inside the staging dir so entries are stored at the zip root.
        Push-Location $SourceDir
        try {
            & $sevenZip a -tzip -mx=9 -bso0 -bsp0 -- $ZipPath '*' | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "7-Zip failed for $ZipPath (exit $LASTEXITCODE)." }
        } finally { Pop-Location }
    } else {
        Compress-Archive -Path (Join-Path $SourceDir '*') -DestinationPath $ZipPath -Force
    }
}
Write-Host ("Archiver: {0}" -f ($(if ($sevenZip) { $sevenZip } else { 'Compress-Archive (built-in)' }))) -ForegroundColor Cyan

# --- Default tag from the 2026 project version --------------------------------
if (-not $Tag) {
    $csproj = Join-Path $root 'CheckupAddin2026\CheckupAddin2026\CheckupAddin2026.csproj'
    $ver = ([xml](Get-Content $csproj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
    if (-not $ver) { throw 'Could not read <Version> from the 2026 csproj; pass -Tag explicitly.' }
    $Tag = "v$ver"
}
Write-Host "Release tag: $Tag" -ForegroundColor Cyan

# --- Fresh dist\ --------------------------------------------------------------
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
New-Item -ItemType Directory $dist | Out-Null

$zips = @()

foreach ($y in $Years) {
    Write-Host "==================== $y ====================" -ForegroundColor Cyan
    $proj = Join-Path $root "CheckupAddin$y\CheckupAddin$y\CheckupAddin$y.csproj"
    if (-not (Test-Path $proj)) { Write-Warning "$proj not found - skipped."; continue }

    $interop = Join-Path $root "lib\$y\Autodesk.Inventor.Interop.dll"
    if (-not (Test-Path $interop)) {
        Write-Warning "lib\$y\Autodesk.Inventor.Interop.dll missing - run fetch_interop.ps1. Skipping $y."
        continue
    }

    & $msbuild $proj /t:Restore,Build /p:Configuration=Release /p:Platform=x64 `
        /p:WarningLevel=0 /v:minimal /nologo
    if ($LASTEXITCODE -ne 0) { throw "Build failed for $y (exit $LASTEXITCODE)." }

    $bin = Join-Path $root "CheckupAddin$y\CheckupAddin$y\bin"
    $pkg = Join-Path $dist "release_$y"
    New-Item -ItemType Directory $pkg | Out-Null

    $isNet8 = (Select-String -Path $proj -Pattern 'net8\.0-windows' -Quiet)
    if ($isNet8) {
        Copy-Item "$bin\CheckupAddIn.dll"                $pkg
        Copy-Item "$bin\CheckupAddIn.comhost.dll"        $pkg
        Copy-Item "$bin\CheckupAddIn.runtimeconfig.json" $pkg
    } else {
        Copy-Item "$bin\CheckupAddIn$y.dll"              $pkg
        Copy-Item "$bin\Newtonsoft.Json.dll"             $pkg
    }

    # Manifest template + factory settings seed
    Copy-Item (Join-Path $root "CheckupAddin$y\CheckupAddin$y\Autodesk.CheckupAddIn$y.addin.template") $pkg
    Copy-Item "$bin\Checkup_Settings.json" $pkg

    # Language files
    New-Item -ItemType Directory "$pkg\Languages" | Out-Null
    Copy-Item "$bin\Languages\*" "$pkg\Languages\" -Recurse

    # Optional seed data
    foreach ($sub in 'Catalogs', 'Capabilities') {
        if (Test-Path "$bin\$sub") {
            New-Item -ItemType Directory "$pkg\$sub" | Out-Null
            Copy-Item "$bin\$sub\*" "$pkg\$sub\" -Recurse
        }
    }

    $zip = Join-Path $dist "CheckupAddin${y}_$Tag.zip"
    New-Bundle -SourceDir $pkg -ZipPath $zip
    $zips += $zip
    Write-Host "  packaged -> $zip" -ForegroundColor Green
}

if (-not $zips) { throw 'No bundles produced.' }

Write-Host "`nBundles in $dist :" -ForegroundColor Cyan
$zips | ForEach-Object { Write-Host "  $_" }

if ($Publish) {
    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { throw 'gh CLI not found; cannot publish.' }
    Write-Host "`nPublishing GitHub release $Tag to $Repo ..." -ForegroundColor Cyan
    # --repo is explicit so the release never lands on the wrong remote (e.g. the internal archive).
    gh release create $Tag @zips --repo $Repo --title $Tag --generate-notes
    if ($LASTEXITCODE -ne 0) { throw "gh release create failed (exit $LASTEXITCODE)." }
    Write-Host "Published $Tag to $Repo with $($zips.Count) asset(s)." -ForegroundColor Green
} else {
    Write-Host "`nNot published. Review dist\, then either:" -ForegroundColor Yellow
    Write-Host "  pwsh ./build_release.ps1 -Tag $Tag -Publish"
    Write-Host "  or:  gh release create $Tag dist\*.zip --repo $Repo --generate-notes"
}
