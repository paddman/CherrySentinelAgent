#Requires -Version 3.0
param(
    [string]$InstallDir = ""
)

$ErrorActionPreference = "SilentlyContinue"
$valueName = "CherrySentinelAgentTray"

foreach ($runKey in @(
    "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run",
    "HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run"
)) {
    if (Test-Path $runKey) {
        Remove-ItemProperty -Path $runKey -Name $valueName -ErrorAction SilentlyContinue
    }
}

$startup = [Environment]::GetFolderPath("CommonStartup")
if ($startup) {
    $lnk = Join-Path $startup "Cherry Sentinel Agent Tray.lnk"
    if (Test-Path $lnk) { Remove-Item $lnk -Force }
}

Get-Process -Name "CherrySentinel.Agent.Tray" -ErrorAction SilentlyContinue | Stop-Process -Force
Write-Host "Tray unregistered"
