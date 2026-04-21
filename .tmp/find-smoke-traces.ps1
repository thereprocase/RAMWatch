Get-ChildItem -Path 'F:/Claude/ramburn/engine' -Filter 'status*.json' -Recurse -File -ErrorAction SilentlyContinue |
    Format-Table Name, FullName, LastWriteTime -AutoSize
Write-Host '---'
Get-ChildItem -Path 'F:/Claude/ramburn' -Include '*.log', '*.jsonl' -Recurse -File -ErrorAction SilentlyContinue |
    Where-Object { $_.LastWriteTime -gt (Get-Date).AddMinutes(-30) } |
    Format-Table Name, FullName, LastWriteTime -AutoSize
