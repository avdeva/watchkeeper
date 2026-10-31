@echo off
rem Watchkeeper - install or update this server in one step.
rem Put Watchkeeper.exe and this file (and optionally a settings file) in one folder,
rem then right-click deploy.cmd and choose "Run as administrator".
rem
rem   deploy.cmd                   install or update; uses settings.json next to this file if this server has no settings yet
rem   deploy.cmd my-settings.json  use that settings file, and REPLACE this server's settings with it
rem                                (the previous settings are kept in C:\ProgramData\Watchkeeper\backups)
rem
rem Running it again with a newer Watchkeeper.exe updates the program and keeps this server's settings.
setlocal
cd /d "%~dp0"
"%SystemRoot%\System32\net.exe" session >nul 2>&1
if errorlevel 1 (
  echo Run this as administrator: right-click deploy.cmd and choose "Run as administrator".
  pause
  exit /b 5
)
if not exist "%~dp0Watchkeeper.exe" (
  echo Watchkeeper.exe must be in the same folder as deploy.cmd.
  pause
  exit /b 2
)
set "OUT=%TEMP%\watchkeeper-deploy.txt"
if exist "%OUT%" del /q "%OUT%"
set "CFG="
set "REPLACE="
if not "%~1"=="" (
  set "CFG=%~f1"
  set "REPLACE=--replace-config"
) else if exist "%~dp0settings.json" (
  set "CFG=%~dp0settings.json"
)
if defined CFG (
  start "" /wait "%~dp0Watchkeeper.exe" --install --config "%CFG%" %REPLACE% --log "%OUT%"
) else (
  start "" /wait "%~dp0Watchkeeper.exe" --install --log "%OUT%"
)
set RC=%errorlevel%
echo.
if exist "%OUT%" (
  type "%OUT%"
) else (
  echo Watchkeeper.exe did not run. Windows may have blocked the downloaded file:
  echo right-click Watchkeeper.exe, Properties, tick Unblock, OK. Check the antivirus log too.
  if "%RC%"=="0" set RC=9
)
echo.
if "%RC%"=="0" (echo Finished. Open C:\Program Files\Watchkeeper\Watchkeeper.exe as administrator to check the settings.) else (echo Failed, exit code %RC%. See the lines above.)
pause
exit /b %RC%
