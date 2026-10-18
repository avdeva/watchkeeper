@echo off
rem Rebuilds Watchkeeper.exe on Windows with the C# compiler that ships with Windows.
rem No Visual Studio, SDK or download needed. Run this file by double-clicking it.
setlocal
cd /d "%~dp0"

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo Cannot find csc.exe. .NET Framework 4.x is required.
  if not defined CI pause
  exit /b 1
)

if not exist dist mkdir dist
"%CSC%" /nologo /target:winexe /optimize+ /langversion:5 /platform:anycpu ^
  /win32icon:assets\app.ico /win32manifest:assets\app.manifest ^
  /resource:assets\app.ico,app.ico /resource:assets\logo64.png,logo.png ^
  /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Management.dll /reference:System.Security.dll /reference:System.ServiceProcess.dll /reference:System.Core.dll ^
  /out:dist\Watchkeeper.exe src\*.cs src\UI\*.cs
if errorlevel 1 (
  echo.
  echo Build failed.
  if not defined CI pause
  exit /b 1
)
echo.
echo Built dist\Watchkeeper.exe
if not defined CI pause
