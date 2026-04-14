# Publish RAMWatch service and GUI to dist/ for installation.
# Does NOT require admin. Run this before Install or Update.
param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$dotnet   = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
$distRoot = Join-Path $repoRoot 'dist\RAMWatch'

if (-not (Test-Path $dotnet)) {
    Write-Host "ERROR: .NET SDK not found at $dotnet" -ForegroundColor Red
    Write-Host "Install from https://dot.net/install" -ForegroundColor Yellow
    exit 1
}

# Ensure vswhere.exe is discoverable (VS Installer doesn't add it to PATH)
$vsInstallerDir = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer"
if ((Test-Path $vsInstallerDir) -and ($env:PATH -notlike "*$vsInstallerDir*")) {
    $env:PATH += ";$vsInstallerDir"
}

# Check SDK version
$sdkVersion = & $dotnet --version 2>&1
Write-Host "=== RAMWatch Publish ===" -ForegroundColor Cyan
Write-Host ".NET SDK: $sdkVersion"
Write-Host "Configuration: $Configuration"
Write-Host "Output: $distRoot`n"

# Clean previous output
if (Test-Path $distRoot) {
    Remove-Item $distRoot -Recurse -Force
}

# Publish service (Native AOT — requires MSVC build tools)
Write-Host "Publishing service (Native AOT)..." -ForegroundColor Cyan
$serviceOut = Join-Path $distRoot 'service'
& $dotnet publish (Join-Path $repoRoot 'src\RAMWatch.Service') `
    -c $Configuration -r win-x64 -o $serviceOut 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Host "`nService publish failed." -ForegroundColor Red
    Write-Host "Native AOT requires Visual Studio Build Tools with C++ workload." -ForegroundColor Yellow
    Write-Host "Falling back to non-AOT publish..." -ForegroundColor Yellow

    # Fallback: publish without AOT (self-contained single-file)
    & $dotnet publish (Join-Path $repoRoot 'src\RAMWatch.Service') `
        -c $Configuration -r win-x64 -o $serviceOut `
        -p:PublishAot=false -p:SelfContained=true -p:PublishSingleFile=true 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Service publish failed even without AOT." -ForegroundColor Red
        exit 1
    }
    Write-Host "Published (non-AOT fallback)" -ForegroundColor Yellow
} else {
    Write-Host "Published (Native AOT)" -ForegroundColor Green
}

$serviceExe = Join-Path $serviceOut 'RAMWatch.Service.exe'
if (Test-Path $serviceExe) {
    $size = [math]::Round((Get-Item $serviceExe).Length / 1MB, 1)
    Write-Host "  Service: $serviceExe ($size MB)"
}

# Publish GUI (self-contained single-file)
Write-Host "`nPublishing GUI (single-file)..." -ForegroundColor Cyan
$guiOut = Join-Path $distRoot 'gui'
& $dotnet publish (Join-Path $repoRoot 'src\RAMWatch') `
    -c $Configuration -r win-x64 -o $guiOut 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Host "GUI publish failed." -ForegroundColor Red
    exit 1
}

$guiExe = Join-Path $guiOut 'RAMWatch.exe'
if (Test-Path $guiExe) {
    $size = [math]::Round((Get-Item $guiExe).Length / 1MB, 1)
    Write-Host "  GUI: $guiExe ($size MB)" -ForegroundColor Green
}

Write-Host "`n=== Publish complete ===" -ForegroundColor Green
Write-Host "Next: run Install-RAMWatch.ps1 (first time) or Update-RAMWatch.ps1 (dev iteration)"
