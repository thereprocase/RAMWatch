# Build and run RAMWatch GUI from source. No admin required.
# Service must already be running (via Install.ps1 or Update.ps1).
param(
    [string]$Configuration = "Debug",
    [switch]$Release,
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$dotnet   = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'

if (-not (Test-Path $dotnet)) {
    Write-Host "ERROR: .NET SDK not found at $dotnet" -ForegroundColor Red
    exit 1
}

if ($Release) { $Configuration = "Release" }

# Check service status
$svc = Get-Service -Name 'RAMWatch' -ErrorAction SilentlyContinue
if (-not $svc -or $svc.Status -ne 'Running') {
    Write-Host "WARNING: RAMWatch service not running — GUI will use fallback mode" -ForegroundColor Yellow
}

# Kill any existing GUI instance
$guiProcess = Get-Process -Name "RAMWatch" -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path -like "*RAMWatch*" -and $_.Path -notlike "*Service*" }
if ($guiProcess) {
    Write-Host "Closing existing GUI..." -ForegroundColor Yellow
    $guiProcess | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 500
}

$guiProject = Join-Path $repoRoot 'src\RAMWatch'

if ($NoBuild) {
    Write-Host "Launching RAMWatch ($Configuration, no build)..." -ForegroundColor Cyan
    & $dotnet run --project $guiProject --no-build -c $Configuration
} else {
    Write-Host "Building and launching RAMWatch ($Configuration)..." -ForegroundColor Cyan
    & $dotnet run --project $guiProject -c $Configuration
}
