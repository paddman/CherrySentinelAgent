@echo off
:: Configure Agent Central Server (IP + Port)
setlocal
set "SCRIPT=%~dp0..\Installer\set-agent-central-url.ps1"
if not exist "%SCRIPT%" set "SCRIPT=%~dp0set-agent-central-url.ps1"
if not exist "%SCRIPT%" (
  for %%D in (
    "%ProgramFiles%\Cherry Sentinel Agent\Installer\set-agent-central-url.ps1"
    "%ProgramFiles%\Cherry Sentinel\Agent\..\Installer\set-agent-central-url.ps1"
    "%ProgramFiles%\Cherry Sentinel\Installer\set-agent-central-url.ps1"
  ) do if exist %%~D set "SCRIPT=%%~D"
)
if not exist "%SCRIPT%" (
  echo Cannot find set-agent-central-url.ps1
  pause
  exit /b 1
)
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT%" %*
if errorlevel 1 pause
endlocal
