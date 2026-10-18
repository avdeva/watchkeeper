#!/bin/bash
# Core tests (Mono): builds build/tests.exe and runs it. Optional argument filters test names.
set -e
cd "$(dirname "$0")"
API=${API:-/usr/lib/mono/4.5-api}
mkdir -p build/fakes
cp tests/fakes/ffmpeg tests/fakes/ffprobe build/fakes/
chmod +x build/fakes/*
mcs -langversion:5 -warn:4 -debug -nostdlib -lib:$API -r:mscorlib.dll -r:System.dll -r:System.Core.dll -r:System.Management.dll -r:System.Security.dll -r:System.ServiceProcess.dll \
    -out:build/tests.exe src/*.cs tests/*.cs
cd build && mono --debug tests.exe "$@"
