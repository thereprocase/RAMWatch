#Requires -RunAsAdministrator
# Quick dev iteration: publish, stop service, copy new binaries, restart.
# One command to go from code change to running service+GUI.
param(
    [string]$Configuration = "Release",
    [switch]$SkipPublish,
    [switch]$ServiceOnly,
    [switch]$GuiOnly
)

$ErrorActionPreference = 'Stop'
$repoRoot   = Split-Path $PSScriptRoot -Parent
$dotnet     = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
$distRoot   = Join-Path $repoRoot 'dist\RAMWatch'
$installDir = Join-Path $env:ProgramFiles 'RAMWatch'
$serviceName = 'RAMWatch'

# Ensure vswhere.exe is discoverable (VS Installer doesn't add it to PATH)
$vsInstallerDir = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer"
if ((Test-Path $vsInstallerDir) -and ($env:PATH -notlike "*$vsInstallerDir*")) {
    $env:PATH += ";$vsInstallerDir"
}

$serviceInstall = Join-Path $installDir 'service'
$guiInstall     = Join-Path $installDir 'gui'
$guiExe         = Join-Path $guiInstall 'RAMWatch.exe'

# Check install exists
if (-not (Test-Path $installDir)) {
    Write-Host "ERROR: RAMWatch not installed at $installDir" -ForegroundColor Red
    Write-Host "Run Install-RAMWatch.ps1 first." -ForegroundColor Yellow
    exit 1
}

Write-Host "=== RAMWatch Update ===" -ForegroundColor Cyan

# Track whether GUI was running so we can relaunch it
$guiWasRunning = $false
$guiProcess = Get-Process -Name "RAMWatch" -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path -like "*gui*" }
if ($guiProcess) {
    $guiWasRunning = $true
}

# 1. Publish (unless skipped)
if (-not $SkipPublish) {
    if (-not $GuiOnly) {
        Write-Host "Publishing service..." -ForegroundColor Cyan
        $serviceOut = Join-Path $distRoot 'service'
        & $dotnet publish (Join-Path $repoRoot 'src\RAMWatch.Service') `
            -c $Configuration -r win-x64 -o $serviceOut 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) {
            # Fallback to non-AOT
            & $dotnet publish (Join-Path $repoRoot 'src\RAMWatch.Service') `
                -c $Configuration -r win-x64 -o $serviceOut `
                -p:PublishAot=false -p:SelfContained=true -p:PublishSingleFile=true 2>&1 | Out-Null
            if ($LASTEXITCODE -ne 0) {
                Write-Host "Service publish failed." -ForegroundColor Red
                exit 1
            }
        }
        Write-Host "  Service published" -ForegroundColor Green
    }

    if (-not $ServiceOnly) {
        Write-Host "Publishing GUI..." -ForegroundColor Cyan
        $guiOut = Join-Path $distRoot 'gui'
        & $dotnet publish (Join-Path $repoRoot 'src\RAMWatch') `
            -c $Configuration -r win-x64 -o $guiOut 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) {
            Write-Host "GUI publish failed." -ForegroundColor Red
            exit 1
        }
        Write-Host "  GUI published" -ForegroundColor Green
    }
}

# 2. Stop service
if (-not $GuiOnly) {
    $svc = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    if ($svc -and $svc.Status -eq 'Running') {
        Write-Host "Stopping service..." -ForegroundColor Cyan
        sc.exe stop $serviceName 2>$null | Out-Null
        # Wait for service to actually stop (file locks)
        $waited = 0
        while ($waited -lt 10) {
            $svc = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
            if ($svc.Status -ne 'Running') { break }
            Start-Sleep -Milliseconds 500
            $waited++
        }
    }
}

# 3. Close GUI if running
if (-not $ServiceOnly -and $guiProcess) {
    Write-Host "Closing GUI..." -ForegroundColor Cyan
    $guiProcess | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 500
}

# 4. Copy new files
if (-not $GuiOnly) {
    Write-Host "Updating service binaries..." -ForegroundColor Cyan
    $serviceSrc = Join-Path $distRoot 'service'
    if (Test-Path $serviceSrc) {
        Copy-Item "$serviceSrc\*" $serviceInstall -Recurse -Force
        Write-Host "  Service updated" -ForegroundColor Green
    }
}

if (-not $ServiceOnly) {
    Write-Host "Updating GUI binaries..." -ForegroundColor Cyan
    $guiSrc = Join-Path $distRoot 'gui'
    if (Test-Path $guiSrc) {
        Copy-Item "$guiSrc\*" $guiInstall -Recurse -Force
        Write-Host "  GUI updated" -ForegroundColor Green
    }
}

# 5. Restart service
if (-not $GuiOnly) {
    Write-Host "Starting service..." -ForegroundColor Cyan
    sc.exe start $serviceName 2>$null | Out-Null
    Start-Sleep -Seconds 1

    $svc = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    if ($svc -and $svc.Status -eq 'Running') {
        Write-Host "  Service: RUNNING" -ForegroundColor Green
    } else {
        Write-Host "  Service: FAILED TO START" -ForegroundColor Red
    }
}

# 6. Relaunch GUI if it was running
if (-not $ServiceOnly -and $guiWasRunning -and (Test-Path $guiExe)) {
    Write-Host "Relaunching GUI..." -ForegroundColor Cyan
    Start-Process $guiExe
    Write-Host "  GUI: launched" -ForegroundColor Green
}

Write-Host "`n=== Update complete ===" -ForegroundColor Green
