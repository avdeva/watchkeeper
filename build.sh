#!/bin/bash
# Linux/Mono build (development only). Windows users run build.bat.
# Compiles against the .NET Framework reference assemblies (not Mono's own runtime assemblies):
# types such as Queue<T> live in different assemblies in Mono and in .NET Framework.
set -e
cd "$(dirname "$0")"
API=${API:-/usr/lib/mono/4.5-api}
mkdir -p dist
mcs -langversion:5 -warn:4 -optimize+ -target:winexe -platform:anycpu -nostdlib -lib:$API \
    -win32icon:assets/app.ico -resource:assets/app.ico,app.ico -resource:assets/logo64.png,logo.png \
    -r:mscorlib.dll -r:System.dll -r:System.Core.dll -r:System.Windows.Forms.dll -r:System.Drawing.dll -r:System.Management.dll -r:System.Security.dll -r:System.ServiceProcess.dll \
    -out:dist/Watchkeeper.exe src/*.cs src/UI/*.cs
python3 tools/check_typerefs.py dist/Watchkeeper.exe || { echo "dist/Watchkeeper.exe references types that .NET Framework does not have there"; exit 1; }
