#!/usr/bin/env bash
# Build gates for every CI provider (and for local use): restore, build, test, format, in the
# release flavor; then the tests again in the drill flavor, which proves that failure injection is
# compiled into drill builds only (the flavor tests assert both directions).
set -euo pipefail
cd "$(dirname "$0")/../src"

dotnet restore Fulfillment.slnx
dotnet build Fulfillment.slnx -c Release --no-restore
dotnet test Fulfillment.slnx -c Release --no-build
dotnet format Fulfillment.slnx --verify-no-changes --no-restore
dotnet test Fulfillment.slnx -c Release -p:EnableFailpoints=true -p:BuildFlavor=drill
echo "dotnet gates passed"
