<#
.SYNOPSIS
  Builds CheckupAddin 2026 and packages it as the Kit #5 experiment bundle.

.DESCRIPTION
  1. Builds CheckupAddin2026 in Release/x64.
  2. Stages all deployable files into dist\kit5\CheckupExperiment2026\.
  3. Zips the staged folder to dist\CheckupExperiment2026_kit5.zip.

  The interop DLL is copied from lib\2026\Autodesk.Inventor.Interop.dll
  (populate first with fetch_interop.ps1 if not present).

.EXAMPLE
  pwsh .\pack_kit5.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root    = $PSScriptRoot
$year    = 2026
$kitDir  = Join-Path $root "dist\kit5\CheckupExperiment2026"
$zipPath = Join-Path $root "dist\CheckupExperiment2026_kit5.zip"

# --- Locate MSBuild -----------------------------------------------------------
$msbuild = (Get-Command msbuild -ErrorAction SilentlyContinue).Source
if (-not $msbuild) {
    $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    if (Test-Path $vswhere) {
        $msbuild = & $vswhere -latest -requires Microsoft.Component.MSBuild `
            -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
    }
}
if (-not $msbuild) { throw 'MSBuild not found. Install Visual Studio or the Build Tools.' }

# --- Locate interop DLL -------------------------------------------------------
$interop = Join-Path $root "lib\$year\Autodesk.Inventor.Interop.dll"
if (-not (Test-Path $interop)) {
    throw "lib\$year\Autodesk.Inventor.Interop.dll not found. Run fetch_interop.ps1 first."
}

# --- Build --------------------------------------------------------------------
$proj = Join-Path $root "CheckupAddin$year\CheckupAddin$year\CheckupAddin$year.csproj"
Write-Host "Building CheckupAddin$year (Release/x64)..." -ForegroundColor Cyan
& $msbuild $proj /t:Restore,Build /p:Configuration=Release /p:Platform=x64 `
    /p:WarningLevel=0 /v:minimal /nologo
if ($LASTEXITCODE -ne 0) { throw "Build failed (exit $LASTEXITCODE)." }

$bin = Join-Path $root "CheckupAddin$year\CheckupAddin$year\bin"

# --- Stage into kit5 folder ---------------------------------------------------
Write-Host "Staging files into $kitDir ..." -ForegroundColor Cyan
New-Item -ItemType Directory $kitDir -Force | Out-Null

# DLLs + runtime
Copy-Item "$bin\CheckupAddIn.dll"                 $kitDir -Force
Copy-Item "$bin\CheckupAddIn.comhost.dll"         $kitDir -Force
Copy-Item "$bin\CheckupAddIn.runtimeconfig.json"  $kitDir -Force
Copy-Item "$bin\CheckupAddIn.deps.json"           $kitDir -Force

# Manifest (built .addin, not the template)
Copy-Item "$bin\Autodesk.CheckupAddIn$year.addin" $kitDir -Force

# Factory settings
Copy-Item "$bin\Checkup_Settings.json"            $kitDir -Force

# Interop (needed on the test machine)
Copy-Item $interop                                $kitDir -Force

# Languages
$langDest = Join-Path $kitDir 'Languages'
New-Item -ItemType Directory $langDest -Force | Out-Null
Copy-Item "$bin\Languages\*" $langDest -Recurse -Force

# Catalogs + Capabilities (tracked public seeds only)
foreach ($sub in @('Catalogs', 'Capabilities')) {
    $srcDir  = Join-Path $bin $sub
    $destDir = Join-Path $kitDir $sub
    if (Test-Path $srcDir) {
        New-Item -ItemType Directory $destDir -Force | Out-Null
        Copy-Item "$srcDir\*" $destDir -Recurse -Force
    }
}

# --- Locate 7-Zip (preferred) or fall back to Compress-Archive ---------------
$sevenZip = @(
    (Get-Command 7z.exe -ErrorAction SilentlyContinue).Source
    "$env:ProgramFiles\7-Zip\7z.exe"
    "${env:ProgramFiles(x86)}\7-Zip\7z.exe"
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1

# --- Zip ----------------------------------------------------------------------
Write-Host "Creating $zipPath ..." -ForegroundColor Cyan
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }

if ($sevenZip) {
    Push-Location $kitDir
    try {
        & $sevenZip a -tzip -mx=9 -bso0 -bsp0 -- $zipPath '*' | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "7-Zip failed (exit $LASTEXITCODE)." }
    } finally { Pop-Location }
} else {
    Compress-Archive -Path (Join-Path $kitDir '*') -DestinationPath $zipPath -Force
}

Write-Host ""
Write-Host "Done: $zipPath" -ForegroundColor Green
Write-Host "Transfer this ZIP to the test machine and follow the kit4 README for deployment." -ForegroundColor Yellow
