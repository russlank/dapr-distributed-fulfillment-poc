#!/usr/bin/env bash
# S51 concurrent workflow start across order API replicas. Runs on a drill stack with both order API
# replicas and a short start lease:
#   START_DISPATCHER_LEASE=00:00:02 bash scripts/stack-up.sh --drill --replicas
# Condition: for N orders accepted on order-api and order-api-2, the first start attempt of each is
# held inside its window (drill failpoint BeforeWorkflowStartCall, Delay) longer than the lease, so
# the request is claimed again, by either replica, while the first attempt is still in flight; both
# attempts call the workflow host. Then concurrent identical submissions (one key, both replicas).
# PASS when, from OrderDb and DaprStateDb rows:
#   S51-race       concurrent start attempts actually occurred: at least one order has overlapping
#                  attempts from both replicas, and the late attempt ended LeaseLost (else UNVERIFIED)
#   S51-start      every order: one start request, Started, no error code, no lease left; every
#                  LeaseLost attempt has another attempt (other token) that recorded the start
#   S51-instance   every order: its workflow ran on workflow-host (Completed, shell outcome), claims
#                  counted and audited (claim rows = ExecutionClaims >= 1, no fence rejection), no
#                  workflow state under order-api's own sidecar
#   S51-accept     concurrent identical submissions over both replicas: one order per key
DRILL_ID=S51
. "$(dirname "$0")/../drills/lib.sh"

N="${S51_ORDERS:-20}"
DELAY_MS="${S51_DELAY_MS:-6000}"
CUSTOMER_PREFIX="s51-$(date -u +%H%M%S)-$RANDOM"

drill_preflight
[[ "$(docker compose --profile replicas ps --format '{{.State}}' order-api-2)" == running ]] \
  || { log "REFUSED: order-api-2 is not running (start the stack with --replicas)"; exit 2; }
ready order-api-2 || { log "REFUSED: order-api-2 is not ready"; exit 2; }
lease="$(docker compose exec -T order-api printenv StartDispatcher__Lease | tr -d '\r')"
lease_ms="$(python -c "import sys; h, m, s = sys.argv[1].split(':'); print(int((int(h) * 3600 + int(m) * 60 + float(s)) * 1000))" "$lease")"
(( lease_ms * 2 < DELAY_MS )) || { log "REFUSED: lease $lease is not well below the ${DELAY_MS} ms hold (set START_DISPATCHER_LEASE=00:00:02)"; exit 2; }
echo "lease $lease, hold ${DELAY_MS} ms, orders $N" > "$DRILL_DIR/setup.txt"
trap 'drill_finish; exit $?' EXIT

submit() { # submit <base url> <key> <body> -> "<status> <body>"
  local out
  out="$(curl -s -w '\n%{http_code}' -X POST "$1/orders" -H "X-Api-Key: $CUSTOMER_API_KEY" \
    -H "Idempotency-Key: $2" -H 'content-type: application/json' -d "$3")"
  printf '%s %s\n' "${out##*$'\n'}" "$(printf '%s' "${out%$'\n'*}" | tr -d '\r\n')"
}
run_orders="SELECT o.OrderId FROM orders.[Order] o WHERE o.CustomerId LIKE '$CUSTOMER_PREFIX-%'"

# Arm one one-shot hold per order ('*': any order; each row fires once).
for i in $(seq 1 "$N"); do
  sql OrderDb "INSERT drill.Failpoint (Name, DrillRunId, OperationKey, Action) VALUES ('BeforeWorkflowStartCall', '$DRILL_RUN_ID-$i', '*', 'Delay:$DELAY_MS')"
done
on_cleanup "sql OrderDb \"DELETE drill.Failpoint WHERE DrillRunId LIKE '$DRILL_RUN_ID-%' AND FiredAt IS NULL\" >/dev/null"
INJECTED_AT="$(date -u +%FT%TZ)"
log "armed $N holds of ${DELAY_MS} ms before the start call (lease $lease)"

# Variant A: N orders over both replicas.
for i in $(seq 1 "$N"); do
  base="$ORDER_API"; (( i % 2 == 0 )) && base="$ORDER_API_2"
  submit "$base" "$CUSTOMER_PREFIX-key-$i" '{"customerId":"'$CUSTOMER_PREFIX-$i'","lines":[{"sku":"SKU-1","quantity":1}]}' >> "$DRILL_DIR/accepted.txt"
done
accepted="$(grep -c '^201 ' "$DRILL_DIR/accepted.txt")"
all_started() { [[ "$(sql OrderDb "SELECT COUNT(*) FROM orders.WorkflowStartRequest WHERE State = 'Started' AND OrderId IN ($run_orders)")" == "$N" ]]; }
wait_until 120 "all $N start requests Started" all_started
fired="$(sql OrderDb "SELECT COUNT(*) FROM drill.Failpoint WHERE DrillRunId LIKE '$DRILL_RUN_ID-%' AND FiredAt IS NOT NULL")"
FAULT_OBSERVED="holds fired: $fired"
# Let every held attempt finish (hold + call) before reading the attempt log.
held_done() { [[ "$(sql OrderDb "SELECT COUNT(*) FROM orders.WorkflowStartAttempt WHERE CompletedAt IS NULL AND OrderId IN ($run_orders)")" == 0 ]]; }
wait_until 60 "all start attempts completed" held_done
RECOVERED_AT="$(date -u +%FT%TZ)"
sql OrderDb "SELECT CONCAT(a.OrderId, ' ', a.Attempt, ' ', a.DispatcherId, ' ', CONVERT(varchar(23), a.CalledAt, 126), ' ', CONVERT(varchar(23), a.CompletedAt, 126), ' ', a.Result) FROM orders.WorkflowStartAttempt a WHERE a.OrderId IN ($run_orders) ORDER BY a.OrderId, a.Id" > "$DRILL_DIR/attempts.txt"

# S51-race: overlapping attempts from different replicas, the earlier one ended LeaseLost.
race="$(sql OrderDb "SELECT CONCAT(
  (SELECT COUNT(DISTINCT a1.OrderId) FROM orders.WorkflowStartAttempt a1 JOIN orders.WorkflowStartAttempt a2
     ON a2.OrderId = a1.OrderId AND a2.Id <> a1.Id AND a1.CalledAt <= a2.CalledAt AND a2.CalledAt < a1.CompletedAt
     AND LEFT(a1.DispatcherId, CHARINDEX('/', a1.DispatcherId)) <> LEFT(a2.DispatcherId, CHARINDEX('/', a2.DispatcherId))
     WHERE a1.OrderId IN ($run_orders) AND a1.Result = 'LeaseLost'), '|',
  (SELECT COUNT(DISTINCT a1.OrderId) FROM orders.WorkflowStartAttempt a1 JOIN orders.WorkflowStartAttempt a2
     ON a2.OrderId = a1.OrderId AND a2.Id <> a1.Id AND a1.CalledAt <= a2.CalledAt AND a2.CalledAt < a1.CompletedAt
     WHERE a1.OrderId IN ($run_orders)), '|',
  (SELECT COUNT(*) FROM orders.WorkflowStartAttempt WHERE OrderId IN ($run_orders) AND Result = 'LeaseLost'), '|',
  (SELECT COUNT(*) FROM orders.WorkflowStartAttempt WHERE OrderId IN ($run_orders)))")"
IFS='|' read -r cross overlapping lease_lost attempts <<< "$race"
observed="orders with cross-replica overlap ending LeaseLost=$cross; with any overlap=$overlapping; LeaseLost attempts=$lease_lost; attempts=$attempts; holds fired=$fired"
if (( ${cross:-0} >= 1 && ${lease_lost:-0} >= 1 )); then
  assert S51-race PASS "concurrent start attempts from both replicas on one order; the stale one LeaseLost" "$observed"
else
  assert S51-race UNVERIFIED "concurrent start attempts from both replicas on one order" "$observed"
fi

# S51-start: one Started request per order, untouched by stale dispatchers.
start="$(sql OrderDb "SELECT CONCAT(COUNT(*), '|', SUM(CASE WHEN r.State = 'Started' AND r.LastErrorCode IS NULL AND r.LeaseToken IS NULL AND r.StartedAt IS NOT NULL THEN 1 ELSE 0 END), '|',
  (SELECT COUNT(*) FROM orders.[Order] WHERE CustomerId LIKE '$CUSTOMER_PREFIX-%'), '|',
  (SELECT COUNT(*) FROM orders.WorkflowStartAttempt l WHERE l.OrderId IN ($run_orders) AND l.Result = 'LeaseLost'
     AND NOT EXISTS (SELECT 1 FROM orders.WorkflowStartAttempt w WHERE w.OrderId = l.OrderId AND w.LeaseToken <> l.LeaseToken
                     AND w.Result IN ('Started', 'AlreadyExists'))))
  FROM orders.WorkflowStartRequest r WHERE r.OrderId IN ($run_orders)")"
IFS='|' read -r requests clean orders unexplained <<< "$start"
if [[ "$accepted" == "$N" && "$orders" == "$N" && "$requests" == "$N" && "$clean" == "$N" && "${unexplained:-1}" == 0 ]]; then
  assert S51-start PASS "$N orders, $N start requests, all Started cleanly; every lost lease has another attempt that recorded the start" "accepted=$accepted orders=$orders requests=$requests clean=$clean lease-lost-without-winner=$unexplained"
else
  assert S51-start FAIL "$N/$N/$N/$N/0" "accepted=$accepted orders=$orders requests=$requests clean=$clean lease-lost-without-winner=$unexplained"
fi

# S51-instance: each instance ran on workflow-host; claims counted and audited; nothing under order-api.
instances_ok=0; details=()
for order in $(sql OrderDb "$run_orders"); do
  normalized="$(echo "$order" | tr -d '-' | tr 'A-F' 'a-f')"
  execution="$(admin_api GET "/system/orders/$order/execution")"
  echo "$execution" >> "$DRILL_DIR/executions.jsonl"
  claims="$(json_get "$execution" startRequest.executionClaims)"
  rejections="$(json_get "$execution" startRequest.fenceRejections)"
  claim_rows="$(sql OrderDb "SELECT COUNT(*) FROM orders.ExecutionClaim WHERE OrderId = '$order'")"
  own_rows="$(sql DaprStateDb "SELECT COUNT(*) FROM dbo.state WHERE [Key] LIKE 'order-api||%||fulfill-$normalized||%'")"
  host_rows="$(sql DaprStateDb "SELECT COUNT(*) FROM dbo.state WHERE [Key] LIKE 'workflow-host||%||fulfill-$normalized||%'")"
  if [[ "$(json_get "$execution" workflow.status)" == Completed && "$(json_get "$execution" workflow.output.outcome)" == ShellClaimed \
        && "${claims:-0}" -ge 1 && "$claim_rows" == "$claims" && "$rejections" == 0 && "$own_rows" == 0 && "${host_rows:-0}" -gt 0 ]]; then
    instances_ok=$((instances_ok + 1))
  else
    details+=("$order: status=$(json_get "$execution" workflow.status) claims=$claims rows=$claim_rows rejections=$rejections order-api=$own_rows workflow-host=$host_rows")
  fi
done
claim_spread="$(sql OrderDb "SELECT STRING_AGG(CONCAT(ExecutionClaims, 'x', n), ' ') FROM (SELECT ExecutionClaims, COUNT(*) AS n FROM orders.WorkflowStartRequest WHERE OrderId IN ($run_orders) GROUP BY ExecutionClaims) s")"
if [[ "$instances_ok" == "$N" ]]; then
  assert S51-instance PASS "every instance Completed on workflow-host; claims = audit rows >= 1; no rejection; no order-api state" "$instances_ok/$N ok; claims per order (claims x orders): $claim_spread"
else
  assert S51-instance FAIL "$N/$N" "$instances_ok/$N ok; ${details[*]:0:3}"
fi

# S51-accept: concurrent identical submissions over both replicas.
sql OrderDb "DELETE drill.Failpoint WHERE DrillRunId LIKE '$DRILL_RUN_ID-%' AND FiredAt IS NULL" >/dev/null
for k in 1 2 3 4 5; do
  for i in 1 2 3 4; do
    base="$ORDER_API"; (( i % 2 == 0 )) && base="$ORDER_API_2"
    submit "$base" "$CUSTOMER_PREFIX-dup-$k" '{"customerId":"'$CUSTOMER_PREFIX-dup-$k'","lines":[{"sku":"SKU-2","quantity":2}]}' > "$DRILL_DIR/dup-$k-$i.txt" &
  done
done
wait
dup="$(sql OrderDb "SELECT CONCAT(COUNT(*), '|', COUNT(DISTINCT CustomerId)) FROM orders.[Order] WHERE CustomerId LIKE '$CUSTOMER_PREFIX-dup-%'")"
created="$(cat "$DRILL_DIR"/dup-*.txt | grep -c '^201 ')"
replayed="$(cat "$DRILL_DIR"/dup-*.txt | grep -c '^200 ')"
if [[ "$dup" == "5|5" && "$created" == 5 && "$replayed" == 15 ]]; then
  assert S51-accept PASS "5 keys x 4 concurrent submissions over both replicas: one order per key" "orders|customers=$dup; 201 x$created, 200 x$replayed"
else
  assert S51-accept FAIL "5|5, 201 x5, 200 x15" "orders|customers=$dup; 201 x$created, 200 x$replayed"
fi
