@echo off
rem Watchkeeper - set up this server in one step.
rem Put Watchkeeper.exe and this file (and optionally a configuration file) in one folder,
rem then right-click deploy.cmd and choose "Run as administrator".
rem
rem   deploy.cmd                     config.json next to this file if there is one, otherwise the watchdog starter
rem   deploy.cmd my-settings.json    that configuration file (exported with "Export for another server")
rem   deploy.cmd watchdog            watchdog-and-alerts starter (only if this server has no configuration yet)
rem   deploy.cmd studio              studio starter (only if this server has no configuration yet)
rem
rem It writes the configuration to C:\ProgramData\Watchkeeper (passwords typed into the file are
rem encrypted for this computer), installs the service, and starts it. Everything starts switched off
rem unless the configuration file switches it on.
setlocal
cd /d "%~dp0"
"%SystemRoot%\System32\net.exe" session >nul 2>&1
if errorlevel 1 goto notadmin
if not exist "%~dp0Watchkeeper.exe" goto noexe

set "ARG=%~1"
if "%ARG%"=="" if exist "%~dp0config.json" set "ARG=%~dp0config.json"
if "%ARG%"=="" set "ARG=watchdog"
if /i "%ARG%"=="watchdog" goto profile
if /i "%ARG%"=="studio" goto profile
if not exist "%ARG%" goto noconfig
start "" /wait "%~dp0Watchkeeper.exe" --deploy --config "%ARG%"
set RC=%errorlevel%
echo.
echo If "%ARG%" holds passwords or tokens in plain text, delete it now: they are encrypted in this server's settings.
goto shown

:profile
start "" /wait "%~dp0Watchkeeper.exe" --deploy --profile %ARG%
goto done

:done
set RC=%errorlevel%
:shown
echo.
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -Command "Get-Content -Path \"$env:ProgramData\Watchkeeper\deploy.log\" -Tail 12"
echo.
if "%RC%"=="0" (echo Finished. Open Watchkeeper.exe to check the settings.) else (echo Deploy failed, exit code %RC%. See the lines above.)
pause
exit /b %RC%

:notadmin
echo Run this as administrator: right-click deploy.cmd and choose "Run as administrator".
pause
exit /b 5

:noexe
echo Watchkeeper.exe must be in the same folder as deploy.cmd.
pause
exit /b 2

:noconfig
echo Configuration file not found: %ARG%
pause
exit /b 2
