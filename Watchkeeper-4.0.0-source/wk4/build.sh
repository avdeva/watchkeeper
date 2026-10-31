#!/bin/bash
# Linux/Mono build (development). Compiles against the .NET Framework 4.5 reference assemblies so the result runs on
# Windows' own .NET Framework. Windows: build.cmd.
set -e
cd "$(dirname "$0")"
API=${API:-/usr/lib/mono/4.5-api}
mkdir -p dist
mcs -langversion:5 -warn:4 -optimize+ -target:winexe -platform:anycpu -nostdlib -lib:$API \
    -win32icon:assets/app.ico -win32manifest:assets/app.manifest -resource:assets/logo64.png,logo.png \
    -r:mscorlib.dll -r:System.dll -r:System.Core.dll -r:System.Windows.Forms.dll -r:System.Drawing.dll -r:System.Management.dll -r:System.Security.dll -r:System.ServiceProcess.dll \
    -out:dist/Watchkeeper.exe src/Core/*.cs src/Modules/*.cs src/UI/*.cs src/Program.cs
python3 tools/check_typerefs.py dist/Watchkeeper.exe || { echo "dist/Watchkeeper.exe references types that .NET Framework does not have there"; exit 1; }
