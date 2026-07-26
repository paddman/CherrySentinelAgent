#Requires -Version 3.0
# Called by CherrySentinel-Setup.exe (Inno) after files are copied.
param(
    [Parameter(Mandatory = $true)][string]$InstallDir,
    [Parameter(Mandatory = $true)][string]$DataDir,
    [string]$ServiceName = "CherrySentinelAgent",
    [string]$DisplayName = "Cherry Sentinel Agent",
    [string]$StartService = "1",
    [string]$CentralUrl = "https://localhost:7443"
)

$ErrorActionPreference = "Stop"
$LogDir = Join-Path $DataDir "logs"
$exe = Join-Path $InstallDir "CherrySentinel.Agent.exe"

if (-not (Test-Path $exe)) {
    throw "Agent executable not found: $exe"
}

New-Item -ItemType Directory -Force -Path $DataDir, $LogDir, (Join-Path $DataDir "evidence") | Out-Null

# Patch appsettings if present
$appsettings = Join-Path $InstallDir "appsettings.json"
if (Test-Path $appsettings) {
    try {
        $json = Get-Content $appsettings -Raw | ConvertFrom-Json
        if ($json.Server) { $json.Server.Url = $CentralUrl }
        if ($json.Agent) {
            $json.Agent.DataDirectory = $DataDir
            $json.Agent.ComputerName = $env:COMPUTERNAME
            $json.Agent.DetectOnly = $true
        }
        if ($json.LoggingPaths) { $json.LoggingPaths.Directory = $LogDir }
        if ($json.Response) {
            $json.Response.DetectOnly = $true
            $json.Response.LogOnlyMode = $true
            $json.Response.EvidenceDirectory = (Join-Path $DataDir "evidence")
        }
        $json | ConvertTo-Json -Depth 12 | Set-Content $appsettings -Encoding UTF8
    } catch {
        Write-Warning "Could not patch appsettings.json: $_"
    }
}

# Event source (Application log only — never clear Security log)
$source = "CherrySentinelAgent"
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
    -Description "Cherry Sentinel Endpoint Security Monitoring Agent" `
    -StartupType Automatic | Out-Null

& sc.exe config $ServiceName start= delayed-auto | Out-Null
& sc.exe failure $ServiceName reset= 86400 actions= restart/60000/restart/60000/restart/60000 | Out-Null
& sc.exe failureflag $ServiceName 1 | Out-Null

if ($StartService -eq "1") {
    Start-Service -Name $ServiceName
}

Write-Host "Agent service registered: $ServiceName"
