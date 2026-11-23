#!/usr/bin/env bash
# R06 (M1 compute variant) work stays pending while the compute pool is down (S08). Drill stack
# (core or replicas): bash scripts/stack-up.sh --drill --replicas
# Every compute-worker replica (app and sidecar) is stopped; orders accepted meanwhile must stay
# pending (validated, no check results, no completion) and their workflows must not fail; after the
# pool is back, every order completes once. The inventory variant (holds expiring during an
# inventory outage, compensation) is R06-inventory-outage.sh.
#   R06-outage     no compute-worker replica runs; orders accepted during the outage stay without
#                  check results and without completion for the whole outage; workflows Running
#   R06-recovered  after the pool is restored every order completes, with two check results each
#   R06-alert      OrderProgressStalled fired while the orders could not progress and resolved after the restore
#   R06-once       OrderDb: one key, one start request (Closed), one execution claim, history
#                  history through AwaitingDelivery to Completed, one effect per line, one completion event;
#                  re-executed check activities, if any, counted as replays of one result
DRILL_ID=R06
. "$(dirname "$0")/lib.sh"
OUT_DIR="$DRILL_DIR"
. "$REPO_ROOT/scripts/scenarios/replica-lib.sh"

drill_preflight
client_sim identity > "$DRILL_DIR/client-identity.json" || { log "REFUSED: the client simulator does not build"; exit 2; }
CLIENT_SIM_BUILT=1
trap 'drill_finish; exit $?' EXIT

N="${R06_ORDERS:-4}"
OUTAGE="${R06_OUTAGE_SECONDS:-45}"
mapfile -t WORKERS < <(docker compose --profile replicas --profile scale ps --format '{{.Service}} {{.State}}' 2>/dev/null | awk '$1 ~ /^compute-worker(-[0-9]+)?$/ && $2 == "running" {print $1}' | sort)
[[ ${#WORKERS[@]} -ge 1 ]] || { log "REFUSED: no compute-worker replica runs"; exit 2; }
restore_pool() { local w; for w in "${WORKERS[@]}"; do [[ "$(docker compose --profile replicas --profile scale ps --format '{{.State}}' "$w" 2>/dev/null)" == running ]] || start_pair "$w"; done; }
on_cleanup "restore_pool"
URLS=(--url "$ORDER_API")
[[ "$(docker compose --profile replicas ps --format '{{.State}}' order-api-2 2>/dev/null)" == running ]] && URLS+=(--url "$ORDER_API_2")

# Outage -----------------------------------------------------------------------------------------
INJECTED_AT="$(date -u +%Y-%m-%dT%H:%M:%S.%3NZ)"
ALERT_SINCE="$(date +%s)"; PS_BEFORE="$(alert_firing OrderProgressStalled)"
for w in "${WORKERS[@]}"; do docker compose --profile replicas --profile scale stop -t 5 "$w" "$w-dapr" >/dev/null 2>&1; done
running="$(docker compose --profile replicas --profile scale ps --format '{{.Service}} {{.State}}' 2>/dev/null | awk '$1 ~ /^compute-worker/ && $2 == "running"' | wc -l | tr -d ' ')"
log "stopped ${WORKERS[*]} (and sidecars); compute containers running: $running"
RUN="r06-$(date -u +%H%M%S)-$((RANDOM % 1000))"
c="$(sim submit load --run-id "$RUN" --seed 6 --orders "$N" --concurrency 2 "${URLS[@]}" --output "$DRILL_DIR/submit.result.json")"
run_orders="SELECT OrderId FROM orders.[Order] WHERE CustomerId LIKE '$RUN-c%'"
: > "$DRILL_DIR/outage-samples.txt"
worst_checks=0; worst_completed=0; worst_beyond=0; failed_workflows=0
start=$SECONDS
while (( SECONDS - start < OUTAGE )); do
  sample="$(sql OrderDb "SELECT CONCAT((SELECT COUNT(*) FROM orders.OrderCheck WHERE OrderId IN ($run_orders)), '|',
    (SELECT COUNT(*) FROM orders.[Order] WHERE OrderId IN ($run_orders) AND Status = 'Completed'), '|',
    (SELECT COUNT(*) FROM orders.[Order] WHERE OrderId IN ($run_orders) AND Status NOT IN ('Submitted', 'Validating')), '|',
    (SELECT STRING_AGG(Status, ',') FROM orders.[Order] WHERE OrderId IN ($run_orders)))")"
  echo "$(date -u +%H:%M:%S) checks|completed|beyond-validating|statuses $sample" >> "$DRILL_DIR/outage-samples.txt"
  IFS='|' read -r ch co be _ <<< "$sample"
  (( ch > worst_checks )) && worst_checks=$ch; (( co > worst_completed )) && worst_completed=$co; (( be > worst_beyond )) && worst_beyond=$be
  sleep 5
done
# The pool stays down until the reconciler has reported the stalled orders (stage threshold plus a pass).
alert_wait_firing OrderProgressStalled '' 60 || true
# tr: Windows Python ends lines with CRLF, and mapfile would keep the CR in every id.
mapfile -t IDS < <(jq_py submit "[i['orderId'] for i in r['items'] if i.get('orderId')]" | python -c "import json,sys; print('\n'.join(json.load(sys.stdin)))" | tr -d '\r')
statuses=""
for id in "${IDS[@]}"; do
  e="$(admin_api GET "/system/orders/$id/execution")"
  statuses+="$(json_get "$e" workflow.status)/$(json_get "$e" workflow.customStatus) "
  [[ "$(json_get "$e" workflow.status)" == Failed ]] && { failed_workflows=$((failed_workflows + 1)); echo "$e" > "$DRILL_DIR/failed-$id.json"; }
done
FAULT_OBSERVED="compute replicas running: $running; during ${OUTAGE}s: max check rows $worst_checks, completed $worst_completed, beyond Validating $worst_beyond"
observed="stopped ${WORKERS[*]}; running $running; submit exit $c ($(jq_py submit "r['summary']['created']") created); during ${OUTAGE}s: max check rows $worst_checks, completed $worst_completed, beyond Validating $worst_beyond; workflows (status/custom) at the end of the outage: $statuses"
if [[ "$running" == 0 && "$c" == 0 && "$worst_checks" == 0 && "$worst_completed" == 0 && "$worst_beyond" == 0 && "$failed_workflows" == 0 ]]; then
  assert R06-outage PASS "pool down; accepted orders pending without check results or completion; workflows not failed" "$observed"
else
  assert R06-outage FAIL "no check results, no completion, no failed workflow during the outage" "$observed"
fi

# Recovery ---------------------------------------------------------------------------------------
restore_start=$SECONDS
restore_pool
all_completed() { [[ "$(sql OrderDb "SELECT COUNT(*) FROM orders.[Order] WHERE OrderId IN ($run_orders) AND Status = 'Completed'")" == "$N" ]]; }
wait_until 240 "all $N orders Completed" all_completed
RECOVERED_AT="$(date -u +%Y-%m-%dT%H:%M:%S.%3NZ)"
row="$(sql OrderDb "SELECT CONCAT((SELECT COUNT(*) FROM orders.[Order] WHERE OrderId IN ($run_orders) AND Status = 'Completed'), '|',
  (SELECT COUNT(*) FROM (SELECT OrderId FROM orders.OrderCheck WHERE OrderId IN ($run_orders) GROUP BY OrderId HAVING COUNT(*) = 2) x), '|',
  (SELECT ISNULL(SUM(Replays), 0) FROM orders.OrderCheck WHERE OrderId IN ($run_orders)), '|',
  (SELECT STRING_AGG(CONCAT(CheckName, '@', Replica), ',') FROM orders.OrderCheck WHERE OrderId IN ($run_orders)))")"
IFS='|' read -r completed checks_ok check_replays check_replicas <<< "$row"
observed="restored in $((SECONDS - restore_start)) s (pool start included); completed $completed/$N; orders with 2 check results $checks_ok/$N; check replays $check_replays; checks by replica: $check_replicas"
if [[ "$completed" == "$N" && "$checks_ok" == "$N" ]]; then
  assert R06-recovered PASS "after the pool returned, every order completed with its two check results" "$observed"
else
  assert R06-recovered FAIL "$N Completed with 2 checks each" "$observed"
fi
sql_orders_report "$RUN-c" > "$DRILL_DIR/sql-orders.jsonl"
consistent="$(sql_consistent "$DRILL_DIR/sql-orders.jsonl" "$N")"
claims="$(python -c "import json,sys; print([json.loads(l)['claims'] for l in open(sys.argv[1])])" "$DRILL_DIR/sql-orders.jsonl")"
observed="$consistent; execution claims $claims"
if [[ "$consistent" == ok && "$claims" == "[$(printf '1, %.0s' $(seq 1 "$N") | sed 's/, $//')]" ]]; then
  assert R06-once PASS "one key, Closed start request, one claim, Completed once, one effect per line and one completion event per order" "$observed"
else
  assert R06-once FAIL "consistent orders, one claim each" "$observed"
fi
read -r verdict detail <<< "$(alert_outcome OrderProgressStalled '' "$ALERT_SINCE" "$PS_BEFORE" 150)"
assert R06-alert "$verdict" "OrderProgressStalled fires while the orders cannot progress and resolves after the restore" "$detail"
