#Requires -RunAsAdministrator
# Full RAMWatch install: publish both projects, copy to Program Files,
# install service, set ACLs, create shortcuts, enable autostart.
# Idempotent — safe to re-run on an existing install.
param(
    [string]$Configuration = "Release",
    [switch]$SkipPublish,
    [switch]$NoAutostart,
    [switch]$NoShortcuts,
    [switch]$DesktopShortcut
)

$ErrorActionPreference = 'Stop'
$repoRoot    = Split-Path $PSScriptRoot -Parent
$dotnet      = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
$distRoot    = Join-Path $repoRoot 'dist\RAMWatch'
$installDir  = Join-Path $env:ProgramFiles 'RAMWatch'
$dataDir     = Join-Path $env:ProgramData 'RAMWatch'
$serviceName = 'RAMWatch'

# Ensure vswhere.exe is discoverable (VS Installer doesn't add it to PATH)
$vsInstallerDir = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer"
if ((Test-Path $vsInstallerDir) -and ($env:PATH -notlike "*$vsInstallerDir*")) {
    $env:PATH += ";$vsInstallerDir"
}

# ── Publish ──────────────────────────────────────────────────────────────────

if (-not $SkipPublish) {
    if (-not (Test-Path $dotnet)) {
        Write-Host "ERROR: .NET SDK not found at $dotnet" -ForegroundColor Red
        Write-Host "Install from https://dot.net/install" -ForegroundColor Yellow
        exit 1
    }

    $sdkVersion = & $dotnet --version 2>&1
    Write-Host "=== RAMWatch Install ===" -ForegroundColor Cyan
    Write-Host ".NET SDK: $sdkVersion"
    Write-Host "Configuration: $Configuration`n"

    if (Test-Path $distRoot) {
        Remove-Item $distRoot -Recurse -Force
    }

    # Service (Native AOT, falls back to single-file)
    Write-Host "Publishing service (Native AOT)..." -ForegroundColor Cyan
    $serviceOut = Join-Path $distRoot 'service'
    $ErrorActionPreference = 'Continue'
    & $dotnet publish (Join-Path $repoRoot 'src\RAMWatch.Service') `
        -c $Configuration -r win-x64 -o $serviceOut 2>&1 | Out-Null
    $aotResult = $LASTEXITCODE

    if ($aotResult -ne 0) {
        Write-Host "  AOT failed, falling back to single-file..." -ForegroundColor Yellow
        & $dotnet publish (Join-Path $repoRoot 'src\RAMWatch.Service') `
            -c $Configuration -r win-x64 -o $serviceOut `
            -p:PublishAot=false -p:SelfContained=true -p:PublishSingleFile=true 2>&1
        if ($LASTEXITCODE -ne 0) {
            Write-Host "Service publish failed." -ForegroundColor Red
            exit 1
        }
        Write-Host "  Service published (non-AOT fallback)" -ForegroundColor Yellow
    } else {
        Write-Host "  Service published (Native AOT)" -ForegroundColor Green
    }
    $ErrorActionPreference = 'Stop'

    # GUI (self-contained single-file)
    Write-Host "Publishing GUI (single-file)..." -ForegroundColor Cyan
    $guiOut = Join-Path $distRoot 'gui'
    & $dotnet publish (Join-Path $repoRoot 'src\RAMWatch') `
        -c $Configuration -r win-x64 -o $guiOut 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Host "GUI publish failed." -ForegroundColor Red
        exit 1
    }
    Write-Host "  GUI published" -ForegroundColor Green
} else {
    Write-Host "=== RAMWatch Install (pre-built) ===" -ForegroundColor Cyan
}

# Verify dist output
$serviceExeSrc = Join-Path $distRoot 'service\RAMWatch.Service.exe'
$guiExeSrc     = Join-Path $distRoot 'gui\RAMWatch.exe'

if (-not (Test-Path $serviceExeSrc)) {
    Write-Host "ERROR: Published service not found at $serviceExeSrc" -ForegroundColor Red
    exit 1
}
if (-not (Test-Path $guiExeSrc)) {
    Write-Host "ERROR: Published GUI not found at $guiExeSrc" -ForegroundColor Red
    exit 1
}

# ── Stop existing install ────────────────────────────────────────────────────

$svc = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($svc) {
    Write-Host "`nStopping existing service..." -ForegroundColor Yellow
    sc.exe stop $serviceName 2>$null | Out-Null
    Start-Sleep -Seconds 2
    sc.exe delete $serviceName 2>$null | Out-Null
    Start-Sleep -Seconds 1
}

$guiProcess = Get-Process -Name "RAMWatch" -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path -like "*gui*" }
if ($guiProcess) {
    Write-Host "Closing running GUI..." -ForegroundColor Yellow
    $guiProcess | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 500
}

# ── Copy binaries ────────────────────────────────────────────────────────────

Write-Host "`nInstalling to $installDir ..." -ForegroundColor Cyan

if (Test-Path $installDir) {
    Remove-Item $installDir -Recurse -Force
}

$serviceInstall = Join-Path $installDir 'service'
New-Item -ItemType Directory -Path $serviceInstall -Force | Out-Null
Copy-Item (Join-Path $distRoot 'service\*') $serviceInstall -Recurse

$guiInstall = Join-Path $installDir 'gui'
New-Item -ItemType Directory -Path $guiInstall -Force | Out-Null
Copy-Item (Join-Path $distRoot 'gui\*') $guiInstall -Recurse

$serviceExe = Join-Path $serviceInstall 'RAMWatch.Service.exe'
$guiExe     = Join-Path $guiInstall 'RAMWatch.exe'

# ── ACLs ─────────────────────────────────────────────────────────────────────

Write-Host "Setting ACLs..." -ForegroundColor Cyan

# Install dir: Admins+SYSTEM full, Users read+execute (B5)
icacls $installDir /inheritance:r /q | Out-Null
icacls $installDir /grant:r "Administrators:(OI)(CI)F" /q | Out-Null
icacls $installDir /grant:r "SYSTEM:(OI)(CI)F" /q | Out-Null
icacls $installDir /grant:r "Users:(OI)(CI)RX" /q | Out-Null

# Data dir: service owns all writes
New-Item -ItemType Directory -Path (Join-Path $dataDir 'logs') -Force | Out-Null
icacls $dataDir /inheritance:r /q | Out-Null
icacls $dataDir /grant:r "Administrators:(OI)(CI)F" /q | Out-Null
icacls $dataDir /grant:r "SYSTEM:(OI)(CI)F" /q | Out-Null
icacls $dataDir /grant:r "Users:(OI)(CI)RX" /q | Out-Null

# ── Service ──────────────────────────────────────────────────────────────────

Write-Host "Installing service..." -ForegroundColor Cyan
sc.exe create $serviceName binPath= "`"$serviceExe`"" start= auto DisplayName= "RAMWatch Monitor" | Out-Null
sc.exe description $serviceName "DRAM timing monitor and tuning journal" | Out-Null
sc.exe failure $serviceName reset= 86400 actions= restart/5000/restart/10000/restart/30000 | Out-Null

Write-Host "Starting service..." -ForegroundColor Cyan
sc.exe start $serviceName | Out-Null
Start-Sleep -Seconds 2

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
    Write-Host "  PawnIO: NOT FOUND — hardware reads disabled" -ForegroundColor Yellow
}

# ── Shortcuts ────────────────────────────────────────────────────────────────

if (-not $NoShortcuts) {
    Write-Host "`nCreating shortcuts..." -ForegroundColor Cyan
    $shell = New-Object -ComObject WScript.Shell

    $startMenu = Join-Path ([Environment]::GetFolderPath('CommonStartMenu')) 'Programs\RAMWatch.lnk'
    $link = $shell.CreateShortcut($startMenu)
    $link.TargetPath = $guiExe
    $link.Description = "RAMWatch Dashboard"
    $link.WorkingDirectory = $guiInstall
    $link.Save()
    Write-Host "  Start menu: OK" -ForegroundColor Green

    if ($DesktopShortcut) {
        $desktop = Join-Path ([Environment]::GetFolderPath('CommonDesktopDirectory')) 'RAMWatch.lnk'
        $link = $shell.CreateShortcut($desktop)
        $link.TargetPath = $guiExe
        $link.Description = "RAMWatch Dashboard"
        $link.WorkingDirectory = $guiInstall
        $link.Save()
        Write-Host "  Desktop: OK" -ForegroundColor Green
    }
}

# ── Autostart ────────────────────────────────────────────────────────────────

if (-not $NoAutostart) {
    $regPath = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
    Set-ItemProperty -Path $regPath -Name "RAMWatch" -Value "`"$guiExe`" --minimized"
    Write-Host "  Autostart: enabled" -ForegroundColor Green
}

# ── Summary ──────────────────────────────────────────────────────────────────

$serviceSize = [math]::Round((Get-Item $serviceExe).Length / 1MB, 1)
$guiSize     = [math]::Round((Get-Item $guiExe).Length / 1MB, 1)

Write-Host "`n=== Install complete ===" -ForegroundColor Green
Write-Host "  Service: $serviceInstall ($serviceSize MB)"
Write-Host "  GUI:     $guiInstall ($guiSize MB)"
Write-Host "  Data:    $dataDir"
Write-Host "  Launch:  $guiExe"
