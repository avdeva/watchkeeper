#!/bin/bash
# UI check (Mono): the engine, the manager and tests/ui/Harness.cs in one program. Run under Xvfb:
#   DISPLAY=:99 mono build/uitest.exe /tmp/uitest-work /tmp/uitest-shots
set -e
cd "$(dirname "$0")"
API=${API:-/usr/lib/mono/4.5-api}
mkdir -p build/fakes
cp tests/fakes/* build/fakes/
chmod +x build/fakes/*
mcs -langversion:5 -warn:4 -debug -target:exe -nostdlib -lib:$API -resource:assets/logo64.png,logo.png \
    -r:mscorlib.dll -r:System.dll -r:System.Core.dll -r:System.Windows.Forms.dll -r:System.Drawing.dll -r:System.Management.dll -r:System.Security.dll -r:System.ServiceProcess.dll \
    -main:Watchkeeper.UiTest.Harness -out:build/uitest.exe src/Core/*.cs src/Modules/*.cs src/UI/*.cs tests/Fakes.cs tests/FakeServers.cs tests/TestFramework.cs tests/ui/Harness.cs
