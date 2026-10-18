#!/bin/bash
# Builds the UI smoke test (Mono only): everything except Program.cs plus tests/Fakes.cs tests/ui/Harness.cs.
set -e
cd "$(dirname "$0")"
API=${API:-/usr/lib/mono/4.5-api}
mkdir -p build/fakes
cp tests/fakes/ffmpeg tests/fakes/ffprobe build/fakes/
chmod +x build/fakes/*
UI=$(ls src/UI/*.cs | grep -v Program.cs)
mcs -langversion:5 -warn:4 -debug -target:exe -nostdlib -lib:$API -win32icon:assets/app.ico -resource:assets/app.ico,app.ico -resource:assets/logo64.png,logo.png \
    -r:mscorlib.dll -r:System.dll -r:System.Core.dll -r:System.Windows.Forms.dll -r:System.Drawing.dll -r:System.Management.dll -r:System.Security.dll -r:System.ServiceProcess.dll \
    -out:build/uitest.exe src/*.cs $UI tests/Fakes.cs tests/ui/Harness.cs

# Run (needs Xvfb on :99 and ImageMagick "import"):
#   DISPLAY=:99 mono build/uitest.exe /tmp/uitest-work /tmp/uitest-shots
