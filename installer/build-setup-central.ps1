#Requires -Version 3.0
<#
.SYNOPSIS
  Publish Central Server and build CherrySentinel-Central-Setup-x.y.z.exe (Inno Setup).

.EXAMPLE
  .\installer\build-setup-central.ps1
  .\installer\build-setup-central.ps1 -SkipPublish
  .\installer\build-setup-central.ps1 -Version 1.0.0
#>
[CmdletBinding()]
param(
    [switch]$SkipPublish,
    [string]$Configuration = "Release",
    [string]$Version = ""
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
if (-not (Test-Path (Join-Path $Root "CherrySentinel.sln"))) {
    $Root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
}
Set-Location $Root
$env:Path = "C:\Program Files\dotnet;" + $env:Path

if (-not $Version) {
    $props = Get-Content (Join-Path $Root "Directory.Build.props") -Raw
    if ($props -match '<Version>([^<]+)</Version>') { $Version = $Matches[1].Trim() }
    else { $Version = "1.1.0" }
}
Write-Host "Setup/Product Version: $Version" -ForegroundColor Green

function Find-ISCC {
    $candidates = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles}\Inno Setup 6\ISCC.exe",
        "${env:LocalAppData}\Programs\Inno Setup 6\ISCC.exe"
    )
    foreach ($c in $candidates) {
        if (Test-Path $c) { return $c }
    }
    return $null
}

Write-Host "========================================" -ForegroundColor Cyan
Write-Host " Cherry Sentinel - Central Setup.exe" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

$ico = Join-Path $Root "assets\icons\CherrySentinel.ico"
if (-not (Test-Path $ico)) { throw "Missing icon: $ico" }

$serverOut = Join-Path $Root "artifacts\server-win-x64"

if (-not $SkipPublish) {
    Write-Host ""
    Write-Host "[1/2] Publishing Central Server (self-contained win-x64)..." -ForegroundColor Yellow
    if (Test-Path $serverOut) {
        Get-ChildItem $serverOut -Force | Where-Object { $_.Name -ne 'logs' } | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
    }
    New-Item -ItemType Directory -Force -Path $serverOut | Out-Null
    # Stop service if locking files in publish output (rare)
    Get-Process -Name "CherrySentinel.Server" -ErrorAction SilentlyContinue | Out-Null

    # Publish to temp then copy if target locked
    $tmp = Join-Path $Root "artifacts\server-publish-tmp"
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force -Path $tmp | Out-Null

    # Self-contained: target PCs need no installed .NET runtime
    dotnet publish (Join-Path $Root "src\CherrySentinel.Server\CherrySentinel.Server.csproj") `
        -c $Configuration -r win-x64 --self-contained true `
        -p:PublishSingleFile=false `
        -o $tmp
    if ($LASTEXITCODE -ne 0) { throw "Central publish failed" }

    try {
        Copy-Item (Join-Path $tmp "*") $serverOut -Recurse -Force
    } catch {
        Write-Host "Direct copy failed, using robocopy..." -ForegroundColor DarkYellow
        robocopy $tmp $serverOut /E /NFL /NDL /NJH /NJS /nc /ns /np | Out-Null
    }
    Copy-Item $ico $serverOut -Force -ErrorAction SilentlyContinue
} else {
    Write-Host "[skip] Publish (using existing artifacts\server-win-x64)" -ForegroundColor DarkYellow
}

$serverExe = Join-Path $serverOut "CherrySentinel.Server.exe"
if (-not (Test-Path $serverExe)) { throw "Missing server exe. Run without -SkipPublish." }

Write-Host ""
Write-Host "[2/2] Compiling Central Setup.exe with Inno Setup..." -ForegroundColor Yellow
$iscc = Find-ISCC
if (-not $iscc) {
    throw "ISCC.exe not found. Install: winget install JRSoftware.InnoSetup"
}
Write-Host "  Using: $iscc"

$iss = Join-Path $Root "installer\CherrySentinel-Central.iss"
$outDir = Join-Path $Root "artifacts\setup"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

$rootFwd = $Root -replace '\\', '/'
& $iscc "/DMyAppVersion=$Version" "/DSourceRoot=$rootFwd" $iss
if ($LASTEXITCODE -ne 0) { throw "Inno Setup compile failed (exit $LASTEXITCODE)" }

$setup = Get-ChildItem $outDir -Filter "CherrySentinel-Central-Setup-*.exe" |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1

if (-not $setup) { throw "Central Setup.exe was not produced in $outDir" }

Write-Host ""
Write-Host "========================================" -ForegroundColor Green
Write-Host " SUCCESS" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Green
Write-Host " Installer: $($setup.FullName)"
Write-Host " Size     : $([math]::Round($setup.Length / 1MB, 1)) MB"
Write-Host ""
Write-Host " Install (GUI - enter HTTPS port, default 7443):"
Write-Host "   $($setup.FullName)"
Write-Host ""
Write-Host " Silent:"
Write-Host "   $($setup.FullName) /VERYSILENT /Port=7443"
Write-Host ""
Write-Host " After install: Agent + Dashboard both use https://<host>:7443"
Write-Host ""
