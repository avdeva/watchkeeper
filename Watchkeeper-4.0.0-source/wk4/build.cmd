@echo off
rem Builds Watchkeeper.exe on Windows with the C# compiler that comes with Windows (.NET Framework 4.x).
rem No Visual Studio or download needed. With /test it also builds and runs the tests.
setlocal
cd /d "%~dp0"
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo Cannot find csc.exe: .NET Framework 4.x is needed.
  if not defined CI pause
  exit /b 1
)
set "REFS=/reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Management.dll /reference:System.Security.dll /reference:System.ServiceProcess.dll /reference:System.Core.dll"
if not exist dist mkdir dist
"%CSC%" /nologo /target:winexe /optimize+ /langversion:5 /platform:anycpu /win32icon:assets\app.ico /win32manifest:assets\app.manifest ^
  /resource:assets\logo64.png,logo.png %REFS% /out:dist\Watchkeeper.exe src\Core\*.cs src\Modules\*.cs src\UI\*.cs src\Program.cs
if errorlevel 1 goto failed
echo Built dist\Watchkeeper.exe
if /i not "%~1"=="/test" goto done
if not exist build mkdir build
"%CSC%" /nologo /target:exe /langversion:5 %REFS% /out:build\tests.exe src\Core\*.cs src\Modules\*.cs tests\*.cs
if errorlevel 1 goto failed
xcopy /y /i /q tests\fixtures build\fixtures >nul
build\tests.exe
if errorlevel 1 goto failed
:done
if not defined CI pause
exit /b 0
:failed
echo.
echo Failed.
if not defined CI pause
exit /b 1
