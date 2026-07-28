#Requires -Version 3.0
<#
.SYNOPSIS
  Rebuild ALL Cherry Sentinel installers after code changes.

.EXAMPLE
  .\installer\rebuild-all-setups.ps1
  .\installer\rebuild-all-setups.ps1 -AgentVersion 1.0.12 -CentralVersion 1.0.4 -FullVersion 1.0.7
#>
[CmdletBinding()]
param(
    [string]$AgentVersion = "1.0.11",
    [string]$CentralVersion = "1.0.3",
    [string]$FullVersion = "1.0.6",
    [switch]$SkipFull
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
if (-not (Test-Path (Join-Path $Root "CherrySentinel.sln"))) {
    $Root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
}
Set-Location $Root
$env:Path = "C:\Program Files\dotnet;" + $env:Path

Write-Host "Stopping services/processes that lock publish outputs..." -ForegroundColor DarkYellow
Stop-Service CherrySentinelAgent,CherrySentinelCentral -Force -ErrorAction SilentlyContinue
foreach ($n in @("CherrySentinel.Agent","CherrySentinel.Server","CherrySentinel.Agent.Tray","CherrySentinel.Dashboard")) {
    Get-Process -Name $n -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
}
Start-Sleep -Seconds 2

# signatures for central package
New-Item -ItemType Directory -Force -Path (Join-Path $Root "artifacts\server-win-x64\signatures") | Out-Null
Copy-Item (Join-Path $Root "config\signatures\opensource-signatures.json") `
    (Join-Path $Root "artifacts\server-win-x64\signatures\") -Force -ErrorAction SilentlyContinue

Write-Host "`n[1/3] Agent Setup $AgentVersion" -ForegroundColor Cyan
& (Join-Path $Root "installer\build-setup-agent.ps1") -Version $AgentVersion
if ($LASTEXITCODE -ne 0) { throw "Agent setup failed" }

Write-Host "`n[2/3] Central Setup $CentralVersion" -ForegroundColor Cyan
& (Join-Path $Root "installer\build-setup-central.ps1") -Version $CentralVersion
if ($LASTEXITCODE -ne 0) { throw "Central setup failed" }

if (-not $SkipFull) {
    Write-Host "`n[3/3] Full Setup $FullVersion" -ForegroundColor Cyan
    & (Join-Path $Root "installer\build-setup.ps1") -Version $FullVersion
    if ($LASTEXITCODE -ne 0) { throw "Full setup failed" }
}

Write-Host "`n=== Latest installers ===" -ForegroundColor Green
Get-ChildItem (Join-Path $Root "artifacts\setup\*.exe") |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 6 Name, @{n='MB';e={[math]::Round($_.Length/1MB,1)}}, LastWriteTime |
    Format-Table -AutoSize

Start-Service CherrySentinelCentral -ErrorAction SilentlyContinue
Start-Service CherrySentinelAgent -ErrorAction SilentlyContinue
Write-Host "Done." -ForegroundColor Green
