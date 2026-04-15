#Requires -RunAsAdministrator
# Update installed RAMWatch: publish, hot-swap binaries in Program Files, restart.
# Requires a prior Install.ps1 run.
param(
    [string]$Configuration = "Release",
    [switch]$SkipPublish,
    [switch]$ServiceOnly,
    [switch]$GuiOnly
)

$ErrorActionPreference = 'Stop'
$repoRoot    = Split-Path $PSScriptRoot -Parent
$dotnet      = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
$distRoot    = Join-Path $repoRoot 'dist\RAMWatch'
$installDir  = Join-Path $env:ProgramFiles 'RAMWatch'
$serviceName = 'RAMWatch'

$serviceInstall = Join-Path $installDir 'service'
$guiInstall     = Join-Path $installDir 'gui'
$guiExe         = Join-Path $guiInstall 'RAMWatch.exe'

# Ensure vswhere.exe is discoverable (VS Installer doesn't add it to PATH)
$vsInstallerDir = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer"
if ((Test-Path $vsInstallerDir) -and ($env:PATH -notlike "*$vsInstallerDir*")) {
    $env:PATH += ";$vsInstallerDir"
}

if (-not (Test-Path $installDir)) {
    Write-Host "ERROR: RAMWatch not installed at $installDir" -ForegroundColor Red
    Write-Host "Run Install.ps1 first." -ForegroundColor Yellow
    exit 1
}

Write-Host "=== RAMWatch Update ===" -ForegroundColor Cyan

# Track whether GUI was running so we can relaunch
$guiWasRunning = $false
$guiProcess = Get-Process -Name "RAMWatch" -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path -like "*gui*" }
if ($guiProcess) { $guiWasRunning = $true }

# ── Publish ──────────────────────────────────────────────────────────────────

if (-not $SkipPublish) {
    if (-not $GuiOnly) {
        Write-Host "Publishing service..." -ForegroundColor Cyan
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
                $ErrorActionPreference = 'Stop'
                exit 1
            }
        }
        $ErrorActionPreference = 'Stop'
        Write-Host "  Service published" -ForegroundColor Green
    }

    if (-not $ServiceOnly) {
        Write-Host "Publishing GUI..." -ForegroundColor Cyan
        $guiOut = Join-Path $distRoot 'gui'
        & $dotnet publish (Join-Path $repoRoot 'src\RAMWatch') `
            -c $Configuration -r win-x64 -o $guiOut 2>&1
        if ($LASTEXITCODE -ne 0) {
            Write-Host "GUI publish failed." -ForegroundColor Red
            exit 1
        }
        Write-Host "  GUI published" -ForegroundColor Green
    }
}

# ── Stop ─────────────────────────────────────────────────────────────────────

if (-not $GuiOnly) {
    $svc = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    if ($svc -and $svc.Status -eq 'Running') {
        Write-Host "Stopping service..." -ForegroundColor Cyan
        sc.exe stop $serviceName 2>$null | Out-Null
        $waited = 0
        while ($waited -lt 10) {
            $svc = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
            if ($svc.Status -ne 'Running') { break }
            Start-Sleep -Milliseconds 500
            $waited++
        }
    }
}

if (-not $ServiceOnly -and $guiProcess) {
    Write-Host "Closing GUI..." -ForegroundColor Cyan
    $guiProcess | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 500
}

# ── Swap binaries ────────────────────────────────────────────────────────────

if (-not $GuiOnly) {
    $serviceSrc = Join-Path $distRoot 'service'
    if (Test-Path $serviceSrc) {
        Copy-Item "$serviceSrc\*" $serviceInstall -Recurse -Force
        Write-Host "  Service binaries updated" -ForegroundColor Green
    }
}

if (-not $ServiceOnly) {
    $guiSrc = Join-Path $distRoot 'gui'
    if (Test-Path $guiSrc) {
        Copy-Item "$guiSrc\*" $guiInstall -Recurse -Force
        Write-Host "  GUI binaries updated" -ForegroundColor Green
    }
}

# ── Restart ──────────────────────────────────────────────────────────────────

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

# Relaunch GUI (de-elevated via scheduled task)
if (-not $ServiceOnly -and (Test-Path $guiExe)) {
    Write-Host "Launching GUI..." -ForegroundColor Cyan
    $action = New-ScheduledTaskAction -Execute $guiExe
    $principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive -RunLevel Limited
    $task = New-ScheduledTask -Action $action -Principal $principal
    Register-ScheduledTask -TaskName "_RAMWatch_Launch" -InputObject $task -Force | Out-Null
    Start-ScheduledTask -TaskName "_RAMWatch_Launch"
    Start-Sleep -Milliseconds 500
    Unregister-ScheduledTask -TaskName "_RAMWatch_Launch" -Confirm:$false
    Write-Host "  GUI: launched" -ForegroundColor Green
}

Write-Host "`n=== Update complete ===" -ForegroundColor Green
