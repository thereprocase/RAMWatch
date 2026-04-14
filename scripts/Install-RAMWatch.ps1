#Requires -RunAsAdministrator
# First-time RAMWatch installation.
# Copies published binaries to Program Files, installs service, creates shortcuts.
# Run Publish-Release.ps1 first.
param(
    [switch]$NoAutostart,
    [switch]$NoShortcuts,
    [switch]$DesktopShortcut
)

$ErrorActionPreference = 'Stop'
$repoRoot   = Split-Path $PSScriptRoot -Parent
$distRoot   = Join-Path $repoRoot 'dist\RAMWatch'
$installDir = Join-Path $env:ProgramFiles 'RAMWatch'
$dataDir    = Join-Path $env:ProgramData 'RAMWatch'
$serviceName = 'RAMWatch'

# Verify publish output exists
$serviceExeSrc = Join-Path $distRoot 'service\RAMWatch.Service.exe'
$guiExeSrc     = Join-Path $distRoot 'gui\RAMWatch.exe'

if (-not (Test-Path $serviceExeSrc)) {
    Write-Host "ERROR: Published service not found at $serviceExeSrc" -ForegroundColor Red
    Write-Host "Run .\Publish-Release.ps1 first." -ForegroundColor Yellow
    exit 1
}
if (-not (Test-Path $guiExeSrc)) {
    Write-Host "ERROR: Published GUI not found at $guiExeSrc" -ForegroundColor Red
    Write-Host "Run .\Publish-Release.ps1 first." -ForegroundColor Yellow
    exit 1
}

Write-Host "=== RAMWatch Install ===" -ForegroundColor Cyan

# Stop existing service if present
$svc = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($svc) {
    Write-Host "Stopping existing service..." -ForegroundColor Yellow
    sc.exe stop $serviceName 2>$null | Out-Null
    Start-Sleep -Seconds 2
    sc.exe delete $serviceName 2>$null | Out-Null
    Start-Sleep -Seconds 1
}

# Copy binaries to Program Files
Write-Host "Installing to $installDir ..." -ForegroundColor Cyan

if (Test-Path $installDir) {
    Remove-Item $installDir -Recurse -Force
}

# Service
$serviceInstall = Join-Path $installDir 'service'
New-Item -ItemType Directory -Path $serviceInstall -Force | Out-Null
Copy-Item (Join-Path $distRoot 'service\*') $serviceInstall -Recurse

# GUI
$guiInstall = Join-Path $installDir 'gui'
New-Item -ItemType Directory -Path $guiInstall -Force | Out-Null
Copy-Item (Join-Path $distRoot 'gui\*') $guiInstall -Recurse

$serviceExe = Join-Path $serviceInstall 'RAMWatch.Service.exe'
$guiExe     = Join-Path $guiInstall 'RAMWatch.exe'

# ACLs on install directory — Administrators+SYSTEM full, Users read+execute.
# Prevents unprivileged user from replacing the service binary (B5).
Write-Host "Setting install directory ACLs..." -ForegroundColor Cyan
icacls $installDir /inheritance:r /q | Out-Null
icacls $installDir /grant:r "Administrators:(OI)(CI)F" /q | Out-Null
icacls $installDir /grant:r "SYSTEM:(OI)(CI)F" /q | Out-Null
icacls $installDir /grant:r "Users:(OI)(CI)RX" /q | Out-Null

# Data directory — service owns all writes
Write-Host "Creating data directory..." -ForegroundColor Cyan
New-Item -ItemType Directory -Path (Join-Path $dataDir 'logs') -Force | Out-Null
icacls $dataDir /inheritance:r /q | Out-Null
icacls $dataDir /grant:r "Administrators:(OI)(CI)F" /q | Out-Null
icacls $dataDir /grant:r "SYSTEM:(OI)(CI)F" /q | Out-Null
icacls $dataDir /grant:r "Users:(OI)(CI)RX" /q | Out-Null

# Install service
Write-Host "Installing service..." -ForegroundColor Cyan
sc.exe create $serviceName binPath= "`"$serviceExe`"" start= auto DisplayName= "RAMWatch Monitor" | Out-Null
sc.exe description $serviceName "DRAM timing monitor and tuning journal" | Out-Null
sc.exe failure $serviceName reset= 86400 actions= restart/5000/restart/10000/restart/30000 | Out-Null

# Start service
Write-Host "Starting service..." -ForegroundColor Cyan
sc.exe start $serviceName | Out-Null
Start-Sleep -Seconds 2

# Verify service is running
$svc = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($svc -and $svc.Status -eq 'Running') {
    Write-Host "  Service: RUNNING" -ForegroundColor Green
} else {
    Write-Host "  Service: NOT RUNNING (check Event Viewer)" -ForegroundColor Red
}

# PawnIO check
$pawnioPath = "C:\Program Files\PawnIO\PawnIOLib.dll"
if (Test-Path $pawnioPath) {
    Write-Host "  PawnIO: INSTALLED" -ForegroundColor Green
} else {
    Write-Host "  PawnIO: NOT FOUND" -ForegroundColor Yellow
    Write-Host "    Hardware timing reads require PawnIO." -ForegroundColor Yellow
    Write-Host "    Install from: https://pawnio.com" -ForegroundColor Yellow
    Write-Host "    The service will work without it (event monitoring only)." -ForegroundColor Yellow
}

# Shortcuts
if (-not $NoShortcuts) {
    Write-Host "Creating shortcuts..." -ForegroundColor Cyan
    $shell = New-Object -ComObject WScript.Shell

    # Start menu
    $startMenu = Join-Path ([Environment]::GetFolderPath('CommonStartMenu')) 'Programs\RAMWatch.lnk'
    $link = $shell.CreateShortcut($startMenu)
    $link.TargetPath = $guiExe
    $link.Description = "RAMWatch Dashboard"
    $link.WorkingDirectory = $guiInstall
    $link.Save()
    Write-Host "  Start menu: $startMenu" -ForegroundColor Green

    # Desktop shortcut (optional)
    if ($DesktopShortcut) {
        $desktop = Join-Path ([Environment]::GetFolderPath('CommonDesktopDirectory')) 'RAMWatch.lnk'
        $link = $shell.CreateShortcut($desktop)
        $link.TargetPath = $guiExe
        $link.Description = "RAMWatch Dashboard"
        $link.WorkingDirectory = $guiInstall
        $link.Save()
        Write-Host "  Desktop: $desktop" -ForegroundColor Green
    }
}

# Autostart
if (-not $NoAutostart) {
    Write-Host "Enabling GUI autostart at login..." -ForegroundColor Cyan
    $regPath = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
    Set-ItemProperty -Path $regPath -Name "RAMWatch" -Value "`"$guiExe`" --minimized"
    Write-Host "  Autostart: enabled (HKCU Run)" -ForegroundColor Green
}

Write-Host "`n=== Installation complete ===" -ForegroundColor Green
Write-Host "Service installed at: $installDir\service\"
Write-Host "Dashboard installed at: $installDir\gui\"
Write-Host "Data directory: $dataDir\"
Write-Host "`nLaunch the dashboard: `"$guiExe`""
