#Requires -Version 3.0
<#
.SYNOPSIS
  Publish Agent + Dashboard and build CherrySentinel-Setup-x.y.z.exe (Inno Setup).

.EXAMPLE
  .\installer\build-setup.ps1
  .\installer\build-setup.ps1 -SkipPublish
#>
[CmdletBinding()]
param(
    [switch]$SkipPublish,
    [string]$Configuration = "Release",
    [string]$Version = "1.0.0"
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
if (-not (Test-Path (Join-Path $Root "CherrySentinel.sln"))) {
    $Root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
}
Set-Location $Root
$env:Path = "C:\Program Files\dotnet;" + $env:Path

function Find-ISCC {
    $candidates = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles}\Inno Setup 6\ISCC.exe",
        "${env:LocalAppData}\Programs\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 7\ISCC.exe",
        "${env:ProgramFiles}\Inno Setup 7\ISCC.exe"
    )
    foreach ($c in $candidates) {
        if (Test-Path $c) { return $c }
    }
    $found = Get-ChildItem -Path "$env:LocalAppData\Programs","${env:ProgramFiles(x86)}","$env:ProgramFiles" `
        -Filter "ISCC.exe" -Recurse -ErrorAction SilentlyContinue |
        Select-Object -First 1 -ExpandProperty FullName
    return $found
}

Write-Host "========================================" -ForegroundColor Cyan
Write-Host " Cherry Sentinel - Build Setup.exe" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

$ico = Join-Path $Root "assets\icons\CherrySentinel.ico"
if (-not (Test-Path $ico)) {
    throw "Missing icon: $ico"
}

if (-not $SkipPublish) {
    Write-Host ""
    Write-Host "[1/3] Publishing Agent (self-contained win-x64)..." -ForegroundColor Yellow
    New-Item -ItemType Directory -Force -Path (Join-Path $Root "artifacts\agent-win-x64") | Out-Null
    dotnet publish (Join-Path $Root "src\CherrySentinel.Agent\CherrySentinel.Agent.csproj") `
        -c $Configuration -r win-x64 --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -o (Join-Path $Root "artifacts\agent-win-x64")
    if ($LASTEXITCODE -ne 0) { throw "Agent publish failed" }

    Copy-Item (Join-Path $Root "config\rules.json") (Join-Path $Root "artifacts\agent-win-x64\") -Force -ErrorAction SilentlyContinue
    Copy-Item (Join-Path $Root "config\allowlist.json") (Join-Path $Root "artifacts\agent-win-x64\") -Force -ErrorAction SilentlyContinue
    Copy-Item $ico (Join-Path $Root "artifacts\agent-win-x64\") -Force

    Write-Host ""
    Write-Host "[2/3] Publishing Dashboard..." -ForegroundColor Yellow
    New-Item -ItemType Directory -Force -Path (Join-Path $Root "artifacts\dashboard-win-x64") | Out-Null
    dotnet publish (Join-Path $Root "src\CherrySentinel.Dashboard\CherrySentinel.Dashboard.csproj") `
        -c $Configuration -r win-x64 --self-contained false `
        -o (Join-Path $Root "artifacts\dashboard-win-x64")
    if ($LASTEXITCODE -ne 0) { throw "Dashboard publish failed" }

    Copy-Item $ico (Join-Path $Root "artifacts\dashboard-win-x64\") -Force
    New-Item -ItemType Directory -Force -Path (Join-Path $Root "artifacts\dashboard-win-x64\Assets") | Out-Null
    Copy-Item $ico (Join-Path $Root "artifacts\dashboard-win-x64\Assets\") -Force
} else {
    Write-Host "[skip] Publish (using existing artifacts)" -ForegroundColor DarkYellow
}

$agentExe = Join-Path $Root "artifacts\agent-win-x64\CherrySentinel.Agent.exe"
$dashExe = Join-Path $Root "artifacts\dashboard-win-x64\CherrySentinel.Dashboard.exe"
if (-not (Test-Path $agentExe)) { throw "Missing agent exe. Run without -SkipPublish." }
if (-not (Test-Path $dashExe)) { throw "Missing dashboard exe. Run without -SkipPublish." }

Write-Host ""
Write-Host "[3/3] Compiling Setup.exe with Inno Setup..." -ForegroundColor Yellow
$iscc = Find-ISCC
if (-not $iscc) {
    throw "ISCC.exe not found. Install: winget install JRSoftware.InnoSetup"
}
Write-Host "  Using: $iscc"

$iss = Join-Path $Root "installer\CherrySentinel.iss"
$outDir = Join-Path $Root "artifacts\setup"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

$rootFwd = $Root -replace '\\', '/'
& $iscc "/DMyAppVersion=$Version" "/DSourceRoot=$rootFwd" $iss
if ($LASTEXITCODE -ne 0) { throw "Inno Setup compile failed (exit $LASTEXITCODE)" }

$setup = Get-ChildItem $outDir -Filter "CherrySentinel-Setup-*.exe" |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1

if (-not $setup) { throw "Setup.exe was not produced in $outDir" }

Write-Host ""
Write-Host "========================================" -ForegroundColor Green
Write-Host " SUCCESS" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Green
Write-Host " Installer: $($setup.FullName)"
Write-Host " Size     : $([math]::Round($setup.Length / 1MB, 1)) MB"
Write-Host ""
Write-Host " Run as Administrator:"
Write-Host "   $($setup.FullName)"
Write-Host ""
