$start = Get-Date '2026-04-18 08:00:00'
$events = Get-WinEvent -FilterHashtable @{
    LogName = 'Security'
    Id = 4688
    StartTime = $start
} -MaxEvents 500 -ErrorAction SilentlyContinue |
    Where-Object { $_.Message -like '*RamTestSuite*' }
if ($events) {
    $events | Format-Table TimeCreated, Id -AutoSize
    Write-Host "Found $($events.Count) RamTestSuite exec events since $start"
} else {
    Write-Host "No 4688 events for RamTestSuite. Security audit may not be enabled."
}
Write-Host '---'
Get-ChildItem -Path @(
    "$env:ProgramData",
    "$env:LOCALAPPDATA",
    "$env:TEMP",
    "F:/Claude/ramburn"
) -Filter 'status.json' -Recurse -File -ErrorAction SilentlyContinue 2>$null |
    Where-Object { $_.LastWriteTime -gt (Get-Date).AddHours(-2) } |
    Format-Table Name, FullName, LastWriteTime -AutoSize
