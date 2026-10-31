#!/bin/bash
# Tests (Mono): builds build/tests.exe from the engine code (no UI) and runs it. An argument filters test names.
set -e
cd "$(dirname "$0")"
API=${API:-/usr/lib/mono/4.5-api}
mkdir -p build/fakes
cp tests/fakes/* build/fakes/
rm -rf build/fixtures && cp -r tests/fixtures build/fixtures
chmod +x build/fakes/*
mcs -langversion:5 -warn:4 -debug -nostdlib -lib:$API -r:mscorlib.dll -r:System.dll -r:System.Core.dll -r:System.Management.dll -r:System.Security.dll -r:System.ServiceProcess.dll \
    -out:build/tests.exe src/Core/*.cs src/Modules/*.cs tests/*.cs
cd build && mono --debug tests.exe "$@"
