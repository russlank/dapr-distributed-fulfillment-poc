#!/usr/bin/env bash
# S17 the sidecar's activity concurrency cap holds under a burst: with one compute-worker replica whose sidecar runs
# with maxConcurrentActivityInvocations 2 (dapr/config/fulfillment-capped.yaml), a burst of orders never has more
# than two check activities running at once on it, and every order still completes. The same burst with the default
# configuration (cap 100) is measured for comparison.
# Drill stack: bash scripts/stack-up.sh --drill --replicas
# Overlap is computed from OrderCheck StartedAt/CompletedAt, recorded by the compute replica (one clock per replica).
#   S17-uncapped   default configuration, one replica: the burst completes; the highest overlap is reported
#   S17-capped     capped configuration, one replica: the burst completes, at most 2 checks overlap (and 2 are
#                  reached, so the cap and not the load sets the limit); the sidecar's --config is checked
. "$(dirname "$0")/../lib.sh"
. "$(dirname "$0")/m3-lib.sh"
scenario_init S17
OUT_DIR="${OUT_DIR:-$SCENARIO_DIR}"
. "$(dirname "$0")/replica-lib.sh"
export COMPOSE_PROFILES=replicas,scale
W=(compute-worker compute-worker-2 compute-worker-3 compute-worker-4 compute-worker-5)
restore() { COMPUTE_DAPR_CONFIG= restart_pair compute-worker >/dev/null 2>&1; start_pair compute-worker-2 >/dev/null 2>&1; local w; for w in "${W[@]:2}"; do docker compose stop -t 5 "$w" "$w-dapr" >/dev/null 2>&1; done; }
trap 'restore' EXIT
for w in "${W[@]:1}"; do docker compose stop -t 5 "$w" "$w-dapr" >/dev/null 2>&1; done
N="${S17_ORDERS:-10}"
URLS=(--url "$ORDER_API" --url "$ORDER_API_2")

burst() { # burst <run> -> "<seconds>|<completed>/<orders>|<max overlap>"
  local run=$1 t0=$SECONDS q
  sim submit load --run-id "$run" --seed 17 --orders "$N" --concurrency "$N" "${URLS[@]}" --output "$OUT_DIR/$run.result.json" > /dev/null
  q="SELECT OrderId FROM orders.[Order] WHERE CustomerId LIKE '$run-c%'"
  settled() { [[ "$(sql OrderDb "SELECT COUNT(*) FROM orders.[Order] WHERE OrderId IN ($q) AND Status IN ('Completed', 'Rejected', 'Compensated', 'ManualInterventionRequired')")" == "$N" ]]; }
  wait_until 300 "burst $run settled" settled || true
  local overlap; overlap="$(sql OrderDb "SELECT ISNULL(MAX(c), 0) FROM (SELECT (SELECT COUNT(*) FROM orders.OrderCheck b WHERE b.Replica = a.Replica AND b.OrderId IN ($q) AND b.StartedAt <= a.StartedAt AND b.CompletedAt > a.StartedAt) c FROM orders.OrderCheck a WHERE a.OrderId IN ($q)) x")"
  printf '%s|%s|%s\n' "$((SECONDS - t0))" "$(sql OrderDb "SELECT CONCAT((SELECT COUNT(*) FROM orders.[Order] WHERE OrderId IN ($q) AND Status = 'Completed'), '/', COUNT(*)) FROM orders.[Order] WHERE OrderId IN ($q)")" "$overlap"
}

sidecar_config() { docker inspect "$(docker compose ps -q compute-worker-dapr)" --format '{{json .Args}}' | python -c 'import json,sys; a=json.load(sys.stdin); print(a[a.index("--config") + 1])'; }
COMPUTE_DAPR_CONFIG= restart_pair compute-worker || { log "could not restart compute-worker"; exit 1; }
config_u="$(sidecar_config)"
IFS='|' read -r s1 c1 o1 <<< "$(burst "$PREFIX-u")"
expect S17-uncapped "default cap: $N orders Completed; overlap measured" "sidecar config $config_u; $s1 s, completed $c1, highest overlap $o1" \
  "$([[ "$c1" == "$N/$N" && "$config_u" == */fulfillment.yaml ]]; echo $?)"

COMPUTE_DAPR_CONFIG=fulfillment-capped.yaml restart_pair compute-worker || { log "could not restart compute-worker with the capped configuration"; exit 1; }
config_c="$(sidecar_config)"
IFS='|' read -r s2 c2 o2 <<< "$(burst "$PREFIX-c")"
python -c "import json,sys; print(json.dumps({'orders': int(sys.argv[1]), 'uncapped': {'seconds': int(sys.argv[2]), 'maxOverlap': int(sys.argv[3])}, 'capped2': {'seconds': int(sys.argv[4]), 'maxOverlap': int(sys.argv[5])}}))" \
  "$N" "$s1" "$o1" "$s2" "$o2" > "$OUT_DIR/measurements.json"
expect S17-capped "cap 2: $N orders Completed; highest overlap exactly 2 (durations reported, not judged: the cap adds little to the whole flow)" \
  "sidecar config $config_c; $s2 s (uncapped $s1 s), completed $c2, highest overlap $o2 (uncapped $o1)" \
  "$([[ "$config_c" == */fulfillment-capped.yaml && "$c2" == "$N/$N" && "$o2" == 2 ]]; echo $?)"
scenario_finish
