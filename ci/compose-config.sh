#!/usr/bin/env bash
# Validates docker-compose.yml for every profile with the placeholder values of .env.example
# (compose resolves every variable, also for services it does not start).
set -euo pipefail
cd "$(dirname "$0")/.."
docker compose --env-file .env.example config -q
docker compose --env-file .env.example --profile replicas --profile scale config -q
echo "compose configuration valid"
