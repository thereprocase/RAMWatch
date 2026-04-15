#Requires -RunAsAdministrator
# Remove RAMWatch: stop service, delete binaries, remove shortcuts.
# Preserves data in %ProgramData%\RAMWatch by default.
param(
    [switch]$RemoveData
)

$ErrorActionPreference = 'Stop'
$installDir  = Join-Path $env:ProgramFiles 'RAMWatch'
$dataDir     = Join-Path $env:ProgramData 'RAMWatch'
$serviceName = 'RAMWatch'

Write-Host "=== RAMWatch Uninstall ===" -ForegroundColor Cyan

# Close GUI
$guiProcess = Get-Process -Name "RAMWatch" -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path -like "*gui*" }
if ($guiProcess) {
    Write-Host "Closing GUI..." -ForegroundColor Cyan
    $guiProcess | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 500
}

# Stop and delete service
$svc = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($svc) {
    Write-Host "Stopping service..." -ForegroundColor Cyan
    sc.exe stop $serviceName 2>$null | Out-Null
    Start-Sleep -Seconds 2
    Write-Host "Removing service..." -ForegroundColor Cyan
    sc.exe delete $serviceName 2>$null | Out-Null
    Write-Host "  Service removed" -ForegroundColor Green
} else {
    Write-Host "  Service not found (already removed)" -ForegroundColor Yellow
}

# Remove autostart
$regPath = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
$regValue = Get-ItemProperty -Path $regPath -Name "RAMWatch" -ErrorAction SilentlyContinue
if ($regValue) {
    Remove-ItemProperty -Path $regPath -Name "RAMWatch"
    Write-Host "  Autostart removed" -ForegroundColor Green
}

# Remove shortcuts
$startMenu = Join-Path ([Environment]::GetFolderPath('CommonStartMenu')) 'Programs\RAMWatch.lnk'
if (Test-Path $startMenu) {
    Remove-Item $startMenu -Force
    Write-Host "  Start menu shortcut removed" -ForegroundColor Green
}

$desktop = Join-Path ([Environment]::GetFolderPath('CommonDesktopDirectory')) 'RAMWatch.lnk'
if (Test-Path $desktop) {
    Remove-Item $desktop -Force
    Write-Host "  Desktop shortcut removed" -ForegroundColor Green
}

# Remove install directory
if (Test-Path $installDir) {
    Write-Host "Removing $installDir ..." -ForegroundColor Cyan
    Remove-Item $installDir -Recurse -Force
    Write-Host "  Install directory removed" -ForegroundColor Green
}

# Data directory
if ($RemoveData) {
    if (Test-Path $dataDir) {
        Write-Host "Removing $dataDir ..." -ForegroundColor Red
        Remove-Item $dataDir -Recurse -Force
        Write-Host "  Data directory removed" -ForegroundColor Green
    }
} else {
    if (Test-Path $dataDir) {
        Write-Host "`nData preserved at: $dataDir" -ForegroundColor Yellow
        Write-Host "  Use -RemoveData to delete it too."
    }
}

Write-Host "`n=== Uninstall complete ===" -ForegroundColor Green
