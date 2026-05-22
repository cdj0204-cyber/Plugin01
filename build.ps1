<#
.SYNOPSIS
  Plugin01: close Rhino -> build -> relaunch Rhino, in one step.

.DESCRIPTION
  A loaded .rhp is locked by Rhino, so rebuilding requires closing Rhino first.
  This script closes the running Rhino, builds the plug-in, then relaunches the
  same Rhino version. Once registered, the plug-in auto-loads on next startup.

.PARAMETER Rhino
  Rhino version to relaunch (7 or 8). If omitted, uses the version that was
  running before close; if none was running, defaults to 7.

.PARAMETER Force
  Force-kill Rhino if it does not close gracefully (e.g. a save prompt).
  WARNING: unsaved work may be lost.

.PARAMETER NoLaunch
  Build only; do not relaunch Rhino.

.PARAMETER Config
  Build configuration (default Release).

.EXAMPLE
  .\build.ps1
  .\build.ps1 -Rhino 8
  .\build.ps1 -Force
#>
param(
    [ValidateSet(7, 8)] [int] $Rhino,
    [switch] $Force,
    [switch] $NoLaunch,
    [string] $Config = "Release"
)

$ErrorActionPreference = "Stop"
$proj   = Join-Path $PSScriptRoot "Plugin01.csproj"
$dotnet = "C:\Program Files\dotnet\dotnet.exe"
$exe7   = "C:\Program Files\Rhino 7\System\Rhino.exe"
$exe8   = "C:\Program Files\Rhino 8\System\Rhino.exe"

function Write-Step($msg) { Write-Host "`n==> $msg" -ForegroundColor Cyan }

# 1) Detect running Rhino (to decide which version to relaunch)
$running = Get-Process Rhino -ErrorAction SilentlyContinue
$runningVersion = $null
if ($running) {
    $p = ($running | Select-Object -First 1).Path
    if ($p -like "*Rhino 8*") { $runningVersion = 8 }
    elseif ($p -like "*Rhino 7*") { $runningVersion = 7 }
}
if (-not $Rhino) {
    if ($runningVersion) { $Rhino = $runningVersion } else { $Rhino = 7 }
}

# 2) Close Rhino
if ($running) {
    Write-Step "Closing Rhino... (PID: $($running.Id -join ', '))"
    foreach ($pr in $running) { try { $pr.CloseMainWindow() | Out-Null } catch {} }

    $deadline = (Get-Date).AddSeconds(15)
    while ((Get-Process Rhino -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 500
    }

    $still = Get-Process Rhino -ErrorAction SilentlyContinue
    if ($still) {
        if ($Force) {
            Write-Host "Graceful close failed -> force killing (-Force)." -ForegroundColor Yellow
            $still | Stop-Process -Force
            Start-Sleep -Milliseconds 800
        }
        else {
            Write-Host "Rhino did not close (a Save dialog may be open)." -ForegroundColor Red
            Write-Host "Save & close it manually and rerun, or rerun with -Force." -ForegroundColor Red
            exit 1
        }
    }
    Write-Host "Rhino closed." -ForegroundColor Green
}
else {
    Write-Host "No running Rhino - building directly." -ForegroundColor DarkGray
}

# 3) Build
Write-Step "Building ($Config)..."
& $dotnet build $proj -c $Config --nologo -v minimal
if ($LASTEXITCODE -ne 0) {
    Write-Host "Build FAILED (exit $LASTEXITCODE). Rhino will not be relaunched." -ForegroundColor Red
    exit $LASTEXITCODE
}
Write-Host "Build succeeded." -ForegroundColor Green

# 3b) Ensure plug-in registry FileName points at the built .rhp
#     (force-kill / drag cycles can blank this, which makes Rhino fail to load it)
$pluginGuid = "2806fd57-74a5-43f1-ac4b-152c03862cf3"
$verKey = if ($Rhino -eq 8) { "8.0" } else { "7.0" }
$regKey = "HKCU:\Software\McNeel\Rhinoceros\$verKey\Plug-Ins\$pluginGuid"
$rhpPath = Join-Path $PSScriptRoot "bin\$Config\Plugin01.rhp"
if (Test-Path $regKey) {
    try {
        Set-ItemProperty -Path $regKey -Name "FileName" -Value $rhpPath -ErrorAction Stop
        Write-Host "Plug-in path ensured in registry." -ForegroundColor DarkGray
    } catch {
        Write-Host "Could not set registry FileName: $($_.Exception.Message)" -ForegroundColor Yellow
    }
}

# 4) Relaunch Rhino
if ($NoLaunch) {
    Write-Host "`n-NoLaunch set - not opening Rhino." -ForegroundColor DarkGray
    exit 0
}

$exe = if ($Rhino -eq 8) { $exe8 } else { $exe7 }
if (-not (Test-Path $exe)) {
    Write-Host "Rhino $Rhino executable not found: $exe" -ForegroundColor Red
    exit 1
}
Write-Step "Launching Rhino $Rhino..."
Start-Process $exe
Write-Host "Done. When Rhino opens, run 'Plugin01ImportSvg' to test." -ForegroundColor Green
