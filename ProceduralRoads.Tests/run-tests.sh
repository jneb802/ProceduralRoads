#!/bin/sh
# Runs the suite on both runtimes:
#   net10.0 — fast local loop via dotnet test
#   net48   — mod's actual target, run under Mono (closest to Valheim's runtime)
# Note: bare `dotnet test` aborts on the net48 target on macOS; use this script
# or `dotnet test -f net10.0`.
set -e
cd "$(dirname "$0")"

echo "== net10.0 (dotnet test) =="
dotnet test -f net10.0 "$@"

echo "== net48 (mono + xunit console) =="
dotnet build -f net48 >/dev/null
XUNIT_CONSOLE="$HOME/.nuget/packages/xunit.runner.console/2.8.1/tools/net48/xunit.console.exe"
mono "$XUNIT_CONSOLE" bin/Debug/net48/ProceduralRoads.Tests.dll
