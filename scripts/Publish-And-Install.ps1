#Requires -RunAsAdministrator
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path $PSScriptRoot -Parent
$dotnet = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
$serviceProject = Join-Path $repoRoot 'src\RAMWatch.Service'
$publishDir = Join-Path $repoRoot 'src\RAMWatch.Service\bin\Release\net10.0-windows\win-x64\publish'
$serviceExe = Join-Path $publishDir 'RAMWatch.Service.exe'
$serviceName = 'RAMWatch'

Write-Host "Publishing service..." -ForegroundColor Cyan
& $dotnet publish $serviceProject -c Release -r win-x64
if ($LASTEXITCODE -ne 0) { throw "Publish failed" }

Write-Host "`nStopping existing service (if any)..." -ForegroundColor Cyan
sc.exe stop $serviceName 2>$null
sc.exe delete $serviceName 2>$null
Start-Sleep -Seconds 2

Write-Host "Creating data directory..." -ForegroundColor Cyan
$dataDir = Join-Path $env:ProgramData 'RAMWatch\logs'
New-Item -ItemType Directory -Path $dataDir -Force | Out-Null

Write-Host "Installing service..." -ForegroundColor Cyan
sc.exe create $serviceName binPath= "`"$serviceExe`"" start= auto DisplayName= "RAMWatch Monitor"
sc.exe description $serviceName "RAM stability and system integrity monitor"
sc.exe failure $serviceName reset= 86400 actions= restart/5000/restart/10000/restart/30000

Write-Host "Starting service..." -ForegroundColor Cyan
sc.exe start $serviceName

Write-Host "`nDone. Service status:" -ForegroundColor Green
sc.exe query $serviceName | Select-String "STATE"
