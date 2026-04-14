$repoRoot = Split-Path $PSScriptRoot -Parent
$dotnet = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
$guiProject = Join-Path $repoRoot 'src\RAMWatch'

Write-Host "Launching RAMWatch dashboard..." -ForegroundColor Cyan
& $dotnet run --project $guiProject
