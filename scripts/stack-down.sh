#!/usr/bin/env bash
# Stops the stack and keeps its data (SQL Server, Scheduler, RabbitMQ volumes).
set -euo pipefail
cd "$(dirname "$0")/.."
docker compose --profile replicas --profile scale down
