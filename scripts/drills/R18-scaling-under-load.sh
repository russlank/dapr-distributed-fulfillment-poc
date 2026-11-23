#!/usr/bin/env bash
# R18 (S16) correctness while the compute pool scales and a replica fails under load; measurements recorded.
# Drill stack: bash scripts/stack-up.sh --drill --replicas (compute-worker-3..5 come from the scale profile)
# The same client load (R18_ORDERS orders, concurrency 4, through both order API replicas) runs with 1, 3, and 5
# compute-worker replicas; during the last run one replica is killed. Per run: duration, throughput, and the
# check activities per replica (from OrderDb) go to measurements.json.
#   S16-1, S16-3, S16-5   every order of the run Completed/Delivered with its two checks; with 3 and 5 replicas the
#                         checks spread over at least 2 and 3 replicas
#   R18-kill              compute-worker-2 killed during the 5-replica run: every order still completes, each with
#                         exactly one result per check (a re-executed check counts as a replay), one capture each
DRILL_ID=R18
. "$(dirname "$0")/lib.sh"
OUT_DIR="$DRILL_DIR"
. "$REPO_ROOT/scripts/scenarios/replica-lib.sh"
. "$REPO_ROOT/scripts/scenarios/m3-lib.sh"
drill_preflight
client_sim identity > "$DRILL_DIR/client-identity.json" || { log "REFUSED: the client simulator does not build"; exit 2; }
export COMPOSE_PROFILES=replicas,scale
W=(compute-worker compute-worker-2 compute-worker-3 compute-worker-4 compute-worker-5)
running() { [[ "$(docker compose ps --format '{{.State}}' "$1" 2>/dev/null)" == running ]]; }
scale_to() { # scale_to <n>: replicas 1..n run, the others are stopped
  local i
  for i in "${!W[@]}"; do
    if (( i < $1 )); then running "${W[$i]}" || start_pair "${W[$i]}"; else docker compose stop -t 5 "${W[$i]}" "${W[$i]}-dapr" >/dev/null 2>&1; fi
  done
}
on_cleanup "scale_to 2"
trap 'run_cleanup; drill_finish; exit $?' EXIT
N="${R18_ORDERS:-10}"
URLS=(--url "$ORDER_API" --url "$ORDER_API_2")
SKU_NOTE="client simulator default SKUs"
: > "$DRILL_DIR/measurements.jsonl"

run_load() { # run_load <run id> [kill replica after seconds]: submits the load, waits for every order, prints "<seconds>|<ok>|<distribution>"
  local run=$1 t0=$SECONDS ids q
  if [[ -n "${2:-}" ]]; then
    sim submit load --run-id "$run" --seed 18 --orders "$N" --concurrency 4 "${URLS[@]}" --output "$DRILL_DIR/$run.result.json" > /dev/null &
    local pid=$!; sleep "$2"; docker compose kill compute-worker-2 >/dev/null 2>&1; log "killed compute-worker-2"; wait "$pid"
  else
    sim submit load --run-id "$run" --seed 18 --orders "$N" --concurrency 4 "${URLS[@]}" --output "$DRILL_DIR/$run.result.json" > /dev/null
  fi
  q="SELECT OrderId FROM orders.[Order] WHERE CustomerId LIKE '$run-c%'"
  done_all() { [[ "$(sql OrderDb "SELECT COUNT(*) FROM orders.[Order] WHERE OrderId IN ($q) AND Status IN ('Completed', 'Rejected', 'Compensated', 'Cancelled', 'Escalated', 'ManualInterventionRequired')")" == "$N" ]]; }
  wait_until 300 "run $run settled" done_all || true
  local secs=$((SECONDS - t0))
  local ok; ok="$(sql OrderDb "SELECT CONCAT((SELECT COUNT(*) FROM orders.[Order] WHERE OrderId IN ($q) AND Status = 'Completed'), '/', (SELECT COUNT(*) FROM orders.[Order] WHERE OrderId IN ($q)), '/',
    (SELECT COUNT(*) FROM orders.OrderCheck WHERE OrderId IN ($q)), '/', (SELECT ISNULL(SUM(Replays), 0) FROM orders.OrderCheck WHERE OrderId IN ($q)))")"
  local dist; dist="$(sqln OrderDb "SELECT STRING_AGG(CONCAT(Replica, '=', n), ',') WITHIN GROUP (ORDER BY Replica) FROM (SELECT Replica, COUNT(*) n FROM orders.OrderCheck WHERE OrderId IN ($q) GROUP BY Replica) x")"
  python -c "import json,sys; print(json.dumps({'run': sys.argv[1], 'orders': int(sys.argv[2]), 'seconds': int(sys.argv[3]), 'ordersPerMinute': round(int(sys.argv[2]) * 60 / max(int(sys.argv[3]), 1), 1), 'completed|orders|checks|replays': sys.argv[4], 'checksPerReplica': sys.argv[5]}))" \
    "$run" "$N" "$secs" "$ok" "$dist" >> "$DRILL_DIR/measurements.jsonl"
  printf '%s|%s|%s\n' "$secs" "$ok" "$dist"
}

INJECTED_AT="$(now_iso)"
for n in 1 3 5; do
  scale_to "$n"
  RUN="r18-$n-$(date -u +%H%M%S)-$((RANDOM % 1000))"
  if (( n == 5 )); then out="$(run_load "$RUN" 3)"; else out="$(run_load "$RUN")"; fi
  IFS='|' read -r secs ok dist <<< "$out"
  IFS='/' read -r completed total checks replays <<< "$ok"
  spread=$(tr ',' '\n' <<< "$dist" | grep -c '=')
  want=$(( n == 1 ? 1 : (n == 3 ? 2 : 3) ))
  aexpect "S16-$n" "$N orders Completed with 2 checks each; checks on at least $want replica(s)" "$secs s for $N orders; completed/orders/checks/replays $ok; checks per replica $dist" \
    "$([[ "$completed" == "$N" && "$total" == "$N" && "$checks" == $((N * 2)) && "$spread" -ge "$want" ]]; echo $?)"
  [[ $n == 5 ]] && LAST="$RUN" && LAST_OK="$ok"
done
FAULT_OBSERVED="compute-worker-2 killed 3 s into the 5-replica run"
RECOVERED_AT="$(now_iso)"
q="SELECT OrderId FROM orders.[Order] WHERE CustomerId LIKE '$LAST-c%'"
money="$(sqln OrderDb "SELECT COUNT(*) FROM orders.[Order] WHERE OrderId IN ($q) AND Status = 'Completed'")"
per_order="$(sql OrderDb "SELECT CONCAT(MIN(n), '-', MAX(n)) FROM (SELECT OrderId, COUNT(*) n FROM orders.OrderCheck WHERE OrderId IN ($q) GROUP BY OrderId) x")"
caps=0; for o in $(sql OrderDb "$q"); do [[ "$(provider_money "$o")" == "1|1|0|0" ]] && caps=$((caps + 1)); done
aexpect R18-kill "with a replica killed under load: every order Completed, exactly 2 check results per order, one authorization and capture each" \
  "completed $money of $N; checks per order $per_order; completed/orders/checks/replays $LAST_OK; orders with 1|1|0|0 $caps" \
  "$([[ "$money" == "$N" && "$per_order" == 2-2 && "$caps" == "$N" ]]; echo $?)"
