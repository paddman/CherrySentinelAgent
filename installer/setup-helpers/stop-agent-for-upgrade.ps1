#Requires -Version 3.0
# Force-stop Agent service + kill tray/agent processes so in-place upgrade can overwrite files.
# Called by Setup BEFORE files are copied (PrepareToInstall).
param(
    [string]$ServiceName = "CherrySentinelAgent",
    [string]$InstallDir = ""
)

$ErrorActionPreference = "SilentlyContinue"
Write-Host "=== Cherry Sentinel: stop for upgrade ==="

# 1) Stop Windows Service
$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($svc) {
    Write-Host "Stopping service $ServiceName (status=$($svc.Status))..."
    try {
        Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
    } catch { }
    # sc stop is more reliable on some SKUs
    & sc.exe stop $ServiceName | Out-Null
    $deadline = (Get-Date).AddSeconds(20)
    do {
        Start-Sleep -Milliseconds 500
        $svc.Refresh()
        if ($svc.Status -eq 'Stopped') { break }
    } while ((Get-Date) -lt $deadline)
    Write-Host "Service status: $($svc.Status)"
}

# 2) Kill processes that lock install files (agent service host + tray + dashboard)
$names = @(
    "CherrySentinel.Agent",
    "CherrySentinel.Agent.Tray",
    "CherrySentinel.Dashboard"
)

foreach ($n in $names) {
    $procs = Get-Process -Name $n -ErrorAction SilentlyContinue
    foreach ($p in $procs) {
        Write-Host "Killing $($p.ProcessName) PID=$($p.Id)"
        try {
            Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
        } catch { }
    }
    # taskkill tree as backup
    & taskkill.exe /F /IM "$n.exe" /T 2>$null | Out-Null
}

# 3) If install dir given, kill anything still locking exes under it
if ($InstallDir -and (Test-Path $InstallDir)) {
    Get-Process -ErrorAction SilentlyContinue | Where-Object {
        try {
            $_.Path -and ($_.Path.StartsWith($InstallDir, [StringComparison]::OrdinalIgnoreCase))
        } catch { $false }
    } | ForEach-Object {
        Write-Host "Killing path-locked $($_.ProcessName) PID=$($_.Id)"
        Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
    }
}

Start-Sleep -Milliseconds 800
Write-Host "=== stop-for-upgrade done ==="
