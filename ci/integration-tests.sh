#!/usr/bin/env bash
# Integration tests against a real SQL Server. Locally and on runners with Docker, the tests start a
# container from the stack's pinned SQL Server image (Testcontainers). Where Docker is not available,
# FULFILLMENT_TEST_SQL_SERVER names a server instead (a master-database connection string, for
# example a CI service container); the test code is the same.
set -euo pipefail
cd "$(dirname "$0")/../src"

project=Fulfillment.IntegrationTests/Fulfillment.IntegrationTests.csproj
dotnet build "$project" -c Release
dotnet test --project "$project" -c Release --no-build
echo "integration tests passed"
