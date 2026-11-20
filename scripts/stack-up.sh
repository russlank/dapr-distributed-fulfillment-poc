#!/usr/bin/env bash
# Builds and starts the local stack, waits for the init jobs, then for every service to be ready.
# Usage: scripts/stack-up.sh [--replicas] [--scale] [--drill] [--no-build]
#   --replicas  default replica counts (second order-api on 127.0.0.1:8081, ...)
#   --scale     maximum replica counts (implies --replicas)
#   --drill     drill images (failure injection compiled in) and DAPR_POC_ENV=drill
. "$(dirname "$0")/lib.sh"
set -e

profiles=()
build=(--build)
for arg in "$@"; do
  case "$arg" in
    --replicas) profiles+=(--profile replicas); export CARRIER_CALLBACK_URLS="${CARRIER_CALLBACK_URLS:-http://order-api:8080,http://order-api-2:8080}" ;;
    --scale) profiles+=(--profile replicas --profile scale); export CARRIER_CALLBACK_URLS="${CARRIER_CALLBACK_URLS:-http://order-api:8080,http://order-api-2:8080}" ;;
    # Drill stacks are a separate Compose project with their own volumes (drill preflight checks it).
    --drill) export BUILD_FLAVOR=drill DAPR_POC_ENV=drill COMPOSE_PROJECT_NAME=fulfillment-drill ;;
    --no-build) build=() ;;
    *) die "unknown option $arg" ;;
  esac
done
# Creates .env, or appends names added to .env.example since it was written.
bash scripts/init-env.sh
set -a; . ./.env; set +a
grep -q '=change-me$' .env && die ".env still contains change-me values"

# Build identity (GIT_COMMIT, GIT_BRANCH, GIT_TREE_STATE) comes from lib.sh.
log "starting stack: flavor=$BUILD_FLAVOR env=$DAPR_POC_ENV commit=$GIT_COMMIT ($GIT_TREE_STATE) ${profiles[*]:-core}"
all_services=($(docker compose "${profiles[@]}" config --services))
sidecars=(); others=()
for s in "${all_services[@]}"; do [[ "$s" == *-dapr ]] && sidecars+=("$s") || others+=("$s"); done

# Sidecars join their app's network namespace. When Compose recreates an app, a sidecar created in
# the same run can end up attached to the old app container's namespace (observed: sidecar running,
# DNS dead). So start everything else first, then recreate every sidecar against the running apps.
docker compose "${profiles[@]}" up -d "${build[@]}" "${others[@]}"

# `up --wait` fails as soon as a one-shot job exits, even with code 0, so wait for each job here.
for job in db-init scheduler-volume-init order-db-migrate inventory-db-migrate payment-db-migrate shipping-db-migrate; do
  code=$(docker wait "$(docker compose ps -a -q "$job")")
  if [[ "$code" != 0 ]]; then
    docker compose logs "$job" | tail -20
    die "$job exited with $code"
  fi
  log "$job completed"
done

for sidecar in "${sidecars[@]}"; do
  app="${sidecar%-dapr}"
  wait_until 120 "$app running" bash -c "[[ \"\$(docker compose ps --format '{{.State}}' $app)\" == running ]]" \
    || { docker compose logs --tail 30 "$app"; die "$app is not running"; }
done
# The first sidecar alone: state.sqlserver v2 creates its tables and stored procedures at start, and
# sidecars starting at the same time on a fresh DaprStateDb race on that (observed: "There is already
# an object named 'sp_Upsert_v5_state'", then the sidecar exits). Once one is ready, the schema exists.
first="${sidecars[0]}"
docker compose "${profiles[@]}" up -d --no-deps --force-recreate "$first"
wait_until 120 "${first%-dapr} ready" ready "${first%-dapr}" || { docker compose logs --tail 30 "$first"; die "$first not ready"; }
docker compose "${profiles[@]}" up -d --no-deps --force-recreate "${sidecars[@]:1}"

services=($(printf '%s\n' "${others[@]}" | grep -vE '^(db-init|scheduler-volume-init|order-db-migrate|inventory-db-migrate|payment-db-migrate|shipping-db-migrate|sqlserver|rabbitmq|toxiproxy|placement|scheduler|otel-collector|jaeger|prometheus|grafana)$'))
for svc in "${services[@]}"; do
  wait_until 120 "$svc ready" ready "$svc" || { docker compose logs --tail 30 "$svc" "$svc-dapr" 2>/dev/null; die "$svc not ready"; }
  log "$svc ready: $(http_body "$svc" /system/component-identity)"
done
log "stack ready (${#services[@]} application services)"
