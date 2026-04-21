$boot = (Get-CimInstance Win32_OperatingSystem).LastBootUpTime
$now = Get-Date
$whea = Get-WinEvent -LogName System -MaxEvents 500 -ErrorAction SilentlyContinue |
    Where-Object { $_.TimeCreated -ge $boot -and ($_.ProviderName -like '*WHEA*' -or $_.Id -eq 41 -or $_.ProviderName -like '*BugCheck*') } |
    Measure-Object | Select-Object -ExpandProperty Count
Write-Host "baseline_utc=$($now.ToUniversalTime().ToString('o'))"
Write-Host "boot=$($boot.ToString('o'))"
Write-Host "whea_or_bugcheck_since_boot=$whea"
