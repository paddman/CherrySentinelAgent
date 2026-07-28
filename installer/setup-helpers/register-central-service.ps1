#Requires -Version 3.0
# Called by CherrySentinel-Central-Setup.exe after files are copied.
param(
    [Parameter(Mandatory = $true)][string]$InstallDir,
    [Parameter(Mandatory = $true)][string]$DataDir,
    [string]$ServiceName = "CherrySentinelCentral",
    [string]$DisplayName = "Cherry Sentinel Central",
    [string]$StartService = "1",
    [string]$Port = "7443"
)

$ErrorActionPreference = "Stop"
$LogDir = Join-Path $DataDir "logs"
$DbPath = Join-Path $DataDir "central.db"
$exe = Join-Path $InstallDir "CherrySentinel.Server.exe"

if (-not (Test-Path $exe)) {
    throw "Central executable not found: $exe"
}

$SigDir = Join-Path $DataDir "signatures"
New-Item -ItemType Directory -Force -Path $DataDir, $LogDir, (Join-Path $DataDir "certs"), $SigDir | Out-Null

# Open-source signature pack
$sigSrc = Join-Path $InstallDir "signatures\opensource-signatures.json"
if (-not (Test-Path $sigSrc)) {
    $sigSrc = Join-Path $InstallDir "opensource-signatures.json"
}
$sigDst = Join-Path $SigDir "opensource-signatures.json"
if (Test-Path $sigSrc) {
    Copy-Item $sigSrc $sigDst -Force
    Write-Host "OK: open-source signatures -> $sigDst"
}

# Force-stop old instance (in-place upgrade)
$ErrorActionPreference = "SilentlyContinue"
& sc.exe stop $ServiceName | Out-Null
Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 600
Get-Process -Name "CherrySentinel.Server" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
& taskkill.exe /F /IM "CherrySentinel.Server.exe" /T 2>$null | Out-Null
Start-Sleep -Milliseconds 400
$ErrorActionPreference = "Stop"

$portNum = 7443
[void][int]::TryParse($Port, [ref]$portNum)
if ($portNum -lt 1 -or $portNum -gt 65535) { $portNum = 7443 }

# Write/patch appsettings for this install
$appsettings = Join-Path $InstallDir "appsettings.json"
$config = @{
    Kestrel      = @{ Port = $portNum }
    Security     = @{
        EnableMtls            = $false
        CertificatePath       = (Join-Path $DataDir "certs\central.pfx")
        CertificatePassword   = "CherrySentinel!"
    }
    Database     = @{ Provider = "Sqlite" }
    Sqlite       = @{ DatabasePath = $DbPath }
    Postgres     = @{
        ConnectionString = "Host=127.0.0.1;Port=5432;Database=cherrysentinel;Username=cherrysentinel;Password=cherrysentinel"
    }
    Syslog       = @{
        Enabled                   = $true
        UdpPort                   = 5514
        TcpPort                   = 0
        MatchOpenSourceSignatures = $true
        SignaturesPath            = (Join-Path $DataDir "signatures\opensource-signatures.json")
    }
    Correlation  = @{ TimestampToleranceSeconds = 120 }
    LoggingPaths = @{ Directory = $LogDir }
    Serilog      = @{
        MinimumLevel = @{
            Default  = "Information"
            Override = @{ "Microsoft.AspNetCore" = "Warning" }
        }
    }
    AllowedHosts = "*"
}
$config | ConvertTo-Json -Depth 10 | Set-Content -Path $appsettings -Encoding UTF8
Write-Host "OK: appsettings.json Port=$portNum Sqlite=$DbPath"

# Event source
$source = "CherrySentinelCentral"
if (-not [System.Diagnostics.EventLog]::SourceExists($source)) {
    try { New-EventLog -LogName Application -Source $source } catch { }
}

# Recreate service
$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
    & sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 2
}

$binPath = "`"$exe`""
New-Service -Name $ServiceName `
    -BinaryPathName $binPath `
    -DisplayName $DisplayName `
    -Description "Cherry Sentinel Central API - Agents and Dashboard connect here (port $portNum)" `
    -StartupType Automatic | Out-Null

& sc.exe config $ServiceName start= delayed-auto | Out-Null
& sc.exe failure $ServiceName reset= 86400 actions= restart/10000/restart/30000/restart/60000 | Out-Null
& sc.exe failureflag $ServiceName 1 | Out-Null

# Content root env for ASP.NET (exe directory usually enough)
$reg = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName"
$envVals = @(
    "ASPNETCORE_CONTENTROOT=$InstallDir",
    "DOTNET_CONTENTROOT=$InstallDir"
)
try {
    New-ItemProperty -Path $reg -Name "Environment" -PropertyType MultiString -Value $envVals -Force | Out-Null
} catch { }

if ($StartService -eq "1") {
    Start-Service -Name $ServiceName
    Start-Sleep -Seconds 3
    $svc = Get-Service -Name $ServiceName
    Write-Host "Service $ServiceName : $($svc.Status)"
}

# Always write CONNECTION.txt (desktop / Start Menu rely on this)
$hostName = $env:COMPUTERNAME
try { $hostName = (Get-CimInstance Win32_ComputerSystem -ErrorAction Stop).Name } catch { }
$centralVer = "?"
try {
    $exe = Join-Path $InstallDir "CherrySentinel.Server.exe"
    if (Test-Path $exe) {
        $centralVer = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($exe).ProductVersion
        if (-not $centralVer) { $centralVer = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($exe).FileVersion }
    }
} catch { }
$hint = @"
Cherry Sentinel Central
=======================
Product      : Cherry Sentinel Central
Version      : $centralVer
URL (local)  : https://localhost:$portNum
URL (remote) : https://${hostName}:$portNum
Syslog UDP   : 5514 (open-source signatures)
Signatures   : $DataDir\signatures\opensource-signatures.json
Service      : $ServiceName
Install dir  : $InstallDir
Data         : $DataDir
Database     : $DbPath (SQLite)

Health (includes version JSON):
  GET https://localhost:$portNum/api/v1/health
Agents       : GET https://localhost:$portNum/api/v1/agents
Signatures   : GET https://localhost:$portNum/api/v1/signatures

Agent  Server.Url     = https://${hostName}:$portNum
Agent  SyslogEnabled  = true
Agent  SyslogHost     = $hostName
Agent  SyslogPort     = 5514
Dashboard Settings    = https://${hostName}:$portNum

Start / stop (Admin PowerShell):
  Start-Service $ServiceName
  Stop-Service $ServiceName
  Get-Service $ServiceName

Logs:
  $LogDir
"@
try {
    Set-Content -Path (Join-Path $InstallDir "CONNECTION.txt") -Value $hint -Encoding UTF8 -Force
    Set-Content -Path (Join-Path $DataDir "CONNECTION.txt") -Value $hint -Encoding UTF8 -Force
    Write-Host "OK: CONNECTION.txt written"
} catch {
    Write-Warning "Could not write CONNECTION.txt: $_"
}

Write-Host "Central service registered: $ServiceName on port $portNum"
