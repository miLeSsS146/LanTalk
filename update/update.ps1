# Replaces LanTalk.exe and lt.exe on this PC with the ones next to this script and restarts LanTalk.
# Keeps the old programs as *.old-<time>.exe; settings and chat history (Data) are not touched.
$ErrorActionPreference = 'Stop'
$run = @(Get-Process LanTalk -ErrorAction SilentlyContinue | Where-Object { $_.Path })
if ($run.Count -gt 0) { $dir = Split-Path $run[0].Path } else { $dir = 'D:\LanTalk' }
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
if (-not (Test-Path (Join-Path $dir 'LanTalk.exe'))) { Write-Host "LanTalk not found in $dir"; exit 1 }
$changed = $false
foreach ($name in 'LanTalk', 'lt') {
    $new = Join-Path $PSScriptRoot "$name.exe"; $exe = Join-Path $dir "$name.exe"; $old = Join-Path $dir "$name.old-$stamp.exe"
    if (Test-Path $exe) {
        if ((Get-FileHash $exe).Hash -eq (Get-FileHash $new).Hash) { continue }
        Move-Item $exe $old               # a running program can be renamed
        try { Copy-Item $new $exe } catch { Remove-Item $exe -ErrorAction SilentlyContinue; Move-Item $old $exe; throw }
        Write-Host "Old version kept: $old"
    } else { Copy-Item $new $exe }
    Write-Host "Updated: $exe"
    $changed = $true
}
if (-not $changed) { Write-Host 'Already up to date.'; exit 0 }
foreach ($p in $run) { Stop-Process -Id $p.Id -Force; [void]$p.WaitForExit(5000) }
Start-Process (Join-Path $dir 'LanTalk.exe')
