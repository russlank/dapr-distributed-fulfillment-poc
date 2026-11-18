#!/usr/bin/env bash
# Build gates for every CI provider (and for local use): restore, build, unit tests, format, in the
# release flavor; then the whole solution again in the drill flavor and the unit tests on it, which
# proves that failure injection is compiled into drill builds only (the flavor tests assert both
# directions). Integration tests need SQL Server and run separately: ci/integration-tests.sh.
set -euo pipefail
cd "$(dirname "$0")/../src"

unit=Fulfillment.UnitTests/Fulfillment.UnitTests.csproj
dotnet restore Fulfillment.slnx
dotnet build Fulfillment.slnx -c Release --no-restore
dotnet test --project "$unit" -c Release --no-build
dotnet format Fulfillment.slnx --verify-no-changes --no-restore
dotnet build Fulfillment.slnx -c Release --no-restore -p:EnableFailpoints=true -p:BuildFlavor=drill
dotnet test --project "$unit" -c Release --no-build
echo "dotnet gates passed"
