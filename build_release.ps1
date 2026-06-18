<#
.SYNOPSIS
  Builds, packages, and (optionally) publishes CheckupAddin release bundles
  (all four Inventor year variants + the DesignHarness tool).

.DESCRIPTION
  Replaces the former GitHub Actions auto-build: windows-latest has no Inventor,
  so releases are built locally. For each requested year this script:
    1. msbuild Restore + Build (Release, x64)
    2. Stages the deployable files into dist\release_<year>\
    3. Zips it to dist\CheckupAddin<year>_<Tag>.zip
  Then builds the DesignHarness (unless -SkipHarness) into dist\CheckupDesignHarness_<Tag>.zip.
  With -Publish it then creates the GitHub Release and uploads all zips via gh.

  Packaging by bundle type:
    net48  (2024, 2025): CheckupAddIn.dll + Newtonsoft.Json.dll
    net8   (2026, 2027): CheckupAddIn.dll + .comhost.dll + .runtimeconfig.json
    DesignHarness:       CheckupAddIn.DesignHarness.exe/.dll/.runtimeconfig.json
                         + CheckupAddIn.dll + .comhost.dll (used by the harness)
                         Requires: .NET 8 Desktop Runtime (x64) + Inventor 2026 interop DLL
                         (interop is NOT shipped — user copies from their Inventor 2026 install)

.PARAMETER Tag
  Release tag used in the zip names and (with -Publish) the GitHub release.
  Defaults to "v" + the <Version> read from the 2026 csproj.

.PARAMETER Years
  Which add-in variants to build. Default: 2024, 2025, 2026, 2027.

.PARAMETER SkipHarness
  Skip building and packaging the DesignHarness. Use when the 2026 interop is
  unavailable or when building a partial release.

.PARAMETER Publish
  After building, create the GitHub release for $Tag and upload the zips (gh CLI).

.EXAMPLE
  pwsh ./build_release.ps1                          # build + zip all four variants + harness
.EXAMPLE
  pwsh ./build_release.ps1 -Tag v0.14.0 -Publish   # build, zip, and publish to GitHub
.EXAMPLE
  pwsh ./build_release.ps1 -SkipHarness            # variants only, no harness
#>
[CmdletBinding()]
param(
    [string]   $Tag,
    [int[]]    $Years = @(2024, 2025, 2026, 2027),
    [switch]   $SkipHarness,
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

# --- Public/private boundary (git is the single source of truth) --------------
# A release must contain only git-tracked content (+ build outputs); never a
# gitignored DATA file. On dev machines the build output (bin\) is a deliberate
# mix of public seeds (Demo.*) and private, gitignored files copied in for local
# testing (Test_Spezifik.*, the interop DLL, the legacy Checkup_*.json). These
# helpers make the packager honour the same line git already draws, so the same
# rules protect this repo for every maintainer who clones it.

# Basenames of every gitignored file in the working tree that is private DATA —
# i.e. ignored AND not also tracked under the same name anywhere. Build/tooling
# output dirs (bin, obj, .vs, packages, dist, Dotfuscator, tools) are excluded so
# their artifacts (CheckupAddIn.dll, …) don't masquerade as private; the tracked
# subtraction keeps public seeds (Demo.*, Checkup_Settings.json, language files)
# out of the set even when ignored copies of them exist (e.g. under dist\).
# What remains is the real private set: interop DLL, Test_Spezifik.*, legacy
# Checkup_Catalogs/Capabilities.json[.migrated], generated *.addin, *.user, …
$trackedNames = @( git -C $root ls-files ) |
    ForEach-Object { Split-Path $_ -Leaf } | Sort-Object -Unique
$privateNames = @(
    git -C $root ls-files --others --ignored --exclude-standard -- `
        '.' ':(exclude)**/bin/**' ':(exclude)**/obj/**' ':(exclude)**/.vs/**' `
            ':(exclude)**/packages/**' ':(exclude)dist/**' ':(exclude)**/Dotfuscator/**' `
            ':(exclude)**/tools/**'
) | Where-Object { $_ } | ForEach-Object { Split-Path $_ -Leaf } |
    Sort-Object -Unique | Where-Object { $trackedNames -notcontains $_ }

# Hard stop: abort packaging if any staged file's name matches a private file.
# This is the self-maintaining safety net — drop a new gitignored file into the
# tree and it is refused automatically, no per-file denylist to maintain.
function Assert-NoPrivateFiles {
    param([string] $StageDir, [string] $Label)
    $leaks = Get-ChildItem $StageDir -Recurse -File |
        Where-Object { $privateNames -contains $_.Name }
    if ($leaks) {
        $list = ($leaks | ForEach-Object { $_.FullName.Substring($StageDir.Length + 1) }) -join "`n  "
        throw "ABORT: private (gitignored) file(s) would ship in ${Label}:`n  $list"
    }
}

# Copy ONLY the git-tracked (public) catalog/capability seeds for a variant into
# the staging dir's Catalogs\ / Capabilities\ subfolders. bin\ may also hold the
# private Test_Spezifik.* seeds — selecting by git-tracked source excludes them
# structurally rather than by name.
function Copy-PublicSeeds {
    param([string] $VariantRelDir, [string] $Bin, [string] $Pkg)
    $tracked = git -C $root ls-files -- `
        "$VariantRelDir/Resources/*.catalog.json" `
        "$VariantRelDir/Resources/*.capability.json"
    foreach ($rel in $tracked) {
        if (-not $rel) { continue }
        $name = Split-Path $rel -Leaf
        $sub  = if ($name -like '*.catalog.json') { 'Catalogs' } else { 'Capabilities' }
        # Prefer the built copy (placed at <sub>\<name> by the csproj TargetPath);
        # fall back to the tracked source (identical content) if absent.
        $src = Join-Path $Bin "$sub\$name"
        if (-not (Test-Path $src)) { $src = Join-Path $root $rel }
        $destDir = Join-Path $Pkg $sub
        New-Item -ItemType Directory $destDir -Force | Out-Null
        Copy-Item $src $destDir
    }
}

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
        Copy-Item "$bin\CheckupAddIn.dll"                 $pkg
        Copy-Item "$bin\Newtonsoft.Json.dll"             $pkg
    }

    # Manifest template + factory settings seed
    Copy-Item (Join-Path $root "CheckupAddin$y\CheckupAddin$y\Autodesk.CheckupAddIn$y.addin.template") $pkg
    Copy-Item "$bin\Checkup_Settings.json" $pkg

    # Language files
    New-Item -ItemType Directory "$pkg\Languages" | Out-Null
    Copy-Item "$bin\Languages\*" "$pkg\Languages\" -Recurse

    # Seed data — ONLY the git-tracked public seeds (Demo.*). The private
    # Test_Spezifik.* seeds also live in bin\ on dev machines; they must not ship.
    Copy-PublicSeeds -VariantRelDir "CheckupAddin$y/CheckupAddin$y" -Bin $bin -Pkg $pkg

    Assert-NoPrivateFiles -StageDir $pkg -Label "CheckupAddin$y"

    $zip = Join-Path $dist "CheckupAddin${y}_$Tag.zip"
    New-Bundle -SourceDir $pkg -ZipPath $zip
    $zips += $zip
    Write-Host "  packaged -> $zip" -ForegroundColor Green
}

if (-not $zips) { throw 'No add-in bundles produced.' }

# --- Design Harness -------------------------------------------------------
Write-Host "==================== DesignHarness ====================" -ForegroundColor Cyan
$harnessProj    = Join-Path $root 'DesignHarness\DesignHarness.csproj'
$harnessInterop = Join-Path $root 'lib\2026\Autodesk.Inventor.Interop.dll'

if ($SkipHarness) {
    Write-Host '  (skipped via -SkipHarness)' -ForegroundColor DarkGray
} elseif (-not (Test-Path $harnessProj)) {
    Write-Warning 'DesignHarness\DesignHarness.csproj not found — skipped.'
} elseif (-not (Test-Path $harnessInterop)) {
    Write-Warning 'lib\2026\Autodesk.Inventor.Interop.dll missing — DesignHarness skipped (run fetch_interop.ps1 first).'
} else {
    & $msbuild $harnessProj /t:Restore,Build /p:Configuration=Release /p:Platform=x64 `
        /p:WarningLevel=0 /v:minimal /nologo
    if ($LASTEXITCODE -ne 0) { throw 'DesignHarness build failed.' }

    $hbin = Join-Path $root 'DesignHarness\bin'
    $hpkg = Join-Path $dist 'release_DesignHarness'
    New-Item -ItemType Directory $hpkg | Out-Null

    # Copy the harness output EXCEPT:
    #   - the proprietary Inventor interop (users copy it from their Inventor 2026 install)
    #   - debug symbols (.pdb)
    #   - the COM .addin manifest (meaningless for a standalone .exe — the harness is not
    #     loaded by Inventor)
    #   - .migrated runtime artifacts (regenerated by the store on first run)
    #   - the legacy monolithic Checkup_Catalogs.json / Checkup_Capabilities.json
    #     (gitignored; on dev machines they hold private Test_Spezifik data)
    # Checkup_Settings.json IS kept — it is the tracked factory seed.
    Get-ChildItem $hbin -File | Where-Object {
        $_.Name -ne 'Autodesk.Inventor.Interop.dll' -and
        $_.Name -ne 'Checkup_Catalogs.json'          -and
        $_.Name -ne 'Checkup_Capabilities.json'      -and
        $_.Extension -ne '.pdb'      -and
        $_.Extension -ne '.addin'    -and
        $_.Extension -ne '.migrated'
    } | Copy-Item -Destination $hpkg

    # Logics-Constructor preview seeds: ship the public Demo catalog/capability in
    # the per-file format the store reads from BaseDirectory\Catalogs|Capabilities.
    # This replaces the old legacy-json migration path and ships no private data.
    Copy-PublicSeeds -VariantRelDir 'CheckupAddin2026/CheckupAddin2026' -Bin $hbin -Pkg $hpkg

    Assert-NoPrivateFiles -StageDir $hpkg -Label 'CheckupDesignHarness'

    $hzip = Join-Path $dist "CheckupDesignHarness_$Tag.zip"
    New-Bundle -SourceDir $hpkg -ZipPath $hzip
    $zips += $hzip
    Write-Host "  packaged -> $hzip" -ForegroundColor Green
    Write-Host "  REMINDER: DesignHarness requires .NET 8 Desktop Runtime (x64) and" -ForegroundColor Yellow
    Write-Host "            Autodesk.Inventor.Interop.dll from the Inventor 2026 install." -ForegroundColor Yellow
}

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
