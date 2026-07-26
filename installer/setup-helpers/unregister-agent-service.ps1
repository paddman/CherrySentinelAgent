param(
    [string]$ServiceName = "CherrySentinelAgent"
)

$ErrorActionPreference = "SilentlyContinue"
$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($svc) {
    if ($svc.Status -ne "Stopped") {
        Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 2
    }
    & sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 1
}
Write-Host "Service removed: $ServiceName"
