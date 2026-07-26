#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [string]$InstallDir = "C:\Program Files\Cherry Sentinel Agent",
    [string]$ServiceName = "CherrySentinelAgent",
    [switch]$RemoveData,
    [string]$DataDir = "C:\ProgramData\CherrySentinel"
)

$ErrorActionPreference = "Stop"
Write-Host "== Cherry Sentinel Agent Uninstall ==" -ForegroundColor Cyan

$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($svc) {
    if ($svc.Status -ne 'Stopped') {
        Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 2
    }
    & sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 2
    Write-Host "Service removed: $ServiceName"
}

if (Test-Path $InstallDir) {
    Remove-Item -Path $InstallDir -Recurse -Force
    Write-Host "Removed $InstallDir"
}

if ($RemoveData -and (Test-Path $DataDir)) {
    Remove-Item -Path $DataDir -Recurse -Force
    Write-Host "Removed data $DataDir"
}

Write-Host "Uninstall complete. Security Event Log was never cleared by this agent." -ForegroundColor Green
