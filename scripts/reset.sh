#!/usr/bin/env bash
# Removes the stack AND all its data. SQL data (business truth and Dapr state) and the Scheduler
# volume (durable timers) are always removed together: resetting one without the other leaves
# workflows whose timers or history no longer exist.
# Usage: scripts/reset.sh --yes
set -euo pipefail
cd "$(dirname "$0")/.."
[[ "${1:-}" == "--yes" ]] || { echo "this deletes all stack data; rerun with --yes" >&2; exit 2; }
docker compose --profile replicas --profile scale down -v --remove-orphans
