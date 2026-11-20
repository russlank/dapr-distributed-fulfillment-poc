#!/usr/bin/env bash
# R01 a waiting workflow survives its host's loss; an approval through another replica reaches it
# (S06, S09). Drill stack with replicas: bash scripts/stack-up.sh --drill --replicas
# (review deadline 20 s in the drill environment, so the kill and the approval happen within it).
# An order that needs a review is accepted through order-api and waits. The workflow-host replica
# that executed the wait is identified from its own log (the replay-safe line "review round 1
# resolved after Initial: Pending" is written only by the host running that step), then killed with
# its sidecar (SIGKILL). The approval goes through order-api-2.
#   R01-waiting    before the kill: AwaitingApproval, one Pending round, wake rows Initial:Pending
#                  only, workflow Running with custom status awaiting-review:1, history rows stored,
#                  exactly one host logged the wait
#   R01-killed     that host's app container ended with 137 (SIGKILL), its sidecar too
#   R01-decision   the approval through order-api-2: 200 (X-Served-By order-api-2); one decision row
#   R01-resumed    the same instance (unchanged creation time) continued on the other host: its log
#                  shows the next resolution; wake rows Initial:Pending then Signal:Approved (a Timer
#                  wake after the deadline is also correct, reported as such)
#   R01-completed  Completed once (history Submitted, Validating, AwaitingApproval, Reserving,
#                  Completed), one decision, one effect per line, one completion event, one execution claim,
#                  start request Closed
DRILL_ID=R01
. "$(dirname "$0")/lib.sh"
OUT_DIR="$DRILL_DIR"
. "$REPO_ROOT/scripts/scenarios/replica-lib.sh"

drill_preflight
require_replicas
[[ "$(docker compose --profile replicas ps --format '{{.State}}' workflow-host-2 2>/dev/null)" == running ]] && ready workflow-host-2 \
  || { log "REFUSED: workflow-host-2 must run (replicas profile)"; exit 2; }
client_sim identity > "$DRILL_DIR/client-identity.json" || { log "REFUSED: the client simulator does not build"; exit 2; }
CLIENT_SIM_BUILT=1
trap 'drill_finish; exit $?' EXIT
restart_if_down() { [[ "$(docker compose --profile replicas ps --format '{{.State}}' "$1" 2>/dev/null)" == running ]] || start_pair "$1"; }
on_cleanup "restart_if_down workflow-host"
on_cleanup "restart_if_down workflow-host-2"

RUN="r01-$(date -u +%H%M%S)-$((RANDOM % 1000))"
SINCE="$(date -u -d '-2 seconds' +%Y-%m-%dT%H:%M:%SZ)"
c="$(sim submit single --run-id "$RUN" --seed 11 --review --expect new --url "$ORDER_API" --replica 1 --follow --follow-until review --follow-timeout 60)"
order_id="$(jq_py submit "r['items'][0].get('orderId')")"
instance="fulfill-$(echo "$order_id" | tr -d '-')"
# Functions, not bash -c: wait_until runs its command in this shell, where sql is defined.
has_wake() { [[ "$(sql OrderDb "SELECT COUNT(*) FROM orders.ReviewWake w JOIN orders.ReviewTask r ON r.ReviewId = w.ReviewId WHERE r.OrderId = '$order_id'")" -ge 1 ]]; }
is_completed() { [[ "$(sql OrderDb "SELECT Status FROM orders.[Order] WHERE OrderId = '$order_id'")" == Completed ]]; }
wait_until 15 "the run waits (wake Initial:Pending)" has_wake
sleep 1

# R01-waiting -------------------------------------------------------------------------------------
before="$(admin_api GET "/system/orders/$order_id/execution")"; echo "$before" > "$DRILL_DIR/execution-before.json"
wakes_before="$(sql OrderDb "SELECT STRING_AGG(CONCAT(w.Wake, ':', w.SeenStatus), ',') WITHIN GROUP (ORDER BY w.Id) FROM orders.ReviewWake w JOIN orders.ReviewTask r ON r.ReviewId = w.ReviewId WHERE r.OrderId = '$order_id'")"
row="$(sql OrderDb "SELECT CONCAT(o.Status, '|', (SELECT COUNT(*) FROM orders.ReviewTask r WHERE r.OrderId = o.OrderId AND r.Status = 'Pending')) FROM orders.[Order] o WHERE o.OrderId = '$order_id'")"
history_rows_before="$(sql DaprStateDb "SELECT COUNT(*) FROM dbo.state WHERE [Key] LIKE 'workflow-host||%||$instance||%'")"
host=""; hosts=0
for h in workflow-host workflow-host-2; do
  n="$(docker compose --profile replicas logs --no-log-prefix --since "$SINCE" "$h" 2>/dev/null | grep -c "fulfillment $instance: review round [0-9]* resolved after")"
  echo "$h: $n" >> "$DRILL_DIR/wait-host.txt"
  [[ "$n" -ge 1 ]] && { host="$h"; hosts=$((hosts + 1)); }
done
other_host=workflow-host; [[ "$host" == workflow-host ]] && other_host=workflow-host-2
observed="order $order_id: $row (status|pending rounds); wakes $wakes_before; workflow $(json_get "$before" workflow.status), custom status $(json_get "$before" workflow.customStatus), created $(json_get "$before" workflow.createdAt); state rows $history_rows_before; wait logged by $host ($hosts host)"
if [[ "$c" == 0 && "$row" == "AwaitingApproval|1" && "$wakes_before" == "Initial:Pending" && "$(json_get "$before" workflow.status)" == Running \
      && "$(json_get "$before" workflow.customStatus)" == "awaiting-review:1" && "$history_rows_before" -gt 0 && "$hosts" == 1 ]]; then
  assert R01-waiting PASS "waiting durably for the approval on one identified host" "$observed"
else
  assert R01-waiting FAIL "AwaitingApproval, Initial:Pending, Running awaiting-review:1, one host" "$observed"
fi

# R01-killed --------------------------------------------------------------------------------------
INJECTED_AT="$(date -u +%Y-%m-%dT%H:%M:%S.%3NZ)"
kill_pair "${host:-workflow-host}"
app_exit="$(docker inspect -f '{{.State.ExitCode}}' "$(docker compose --profile replicas ps -a -q "${host:-workflow-host}")" 2>/dev/null)"
sidecar_state="$(docker compose --profile replicas ps -a --format '{{.State}}' "${host:-workflow-host}-dapr" 2>/dev/null)"
FAULT_OBSERVED="$host exit $app_exit, sidecar $sidecar_state"
if [[ "$app_exit" == 137 && "$sidecar_state" == exited ]]; then
  assert R01-killed PASS "the hosting replica and its sidecar were killed (SIGKILL)" "$host exit code $app_exit; $host-dapr $sidecar_state"
else
  assert R01-killed FAIL "exit 137" "$host exit code '$app_exit'; sidecar '$sidecar_state'"
fi

# R01-decision ------------------------------------------------------------------------------------
c="$(sim approve approve --order "$order_id" --operator operator-2 --url "$ORDER_API_2")"
decisions="$(sql OrderDb "SELECT CONCAT(COUNT(*), ':', MAX(d.OperatorId)) FROM orders.ReviewDecision d JOIN orders.ReviewTask r ON r.ReviewId = d.ReviewId WHERE r.OrderId = '$order_id'")"
served="$(jq_py approve "r['items'][0]['attempts'][-1].get('servedBy')")"
observed="exit $c, result $(jq_py approve "r['items'][0]['result']"), served by $served; decisions $decisions; decided $(( $(date +%s) )) "
if [[ "$c" == 0 && "$(jq_py approve "r['items'][0]['result']")" == accepted && "$served" == order-api-2 && "$decisions" == "1:operator-2" ]]; then
  assert R01-decision PASS "approved through order-api-2 while the host was down; one decision" "$observed"
else
  assert R01-decision FAIL "accepted on order-api-2, one decision" "$observed"
fi

# R01-resumed / R01-completed ---------------------------------------------------------------------
wait_until 90 "order $order_id Completed" is_completed
RECOVERED_AT="$(date -u +%Y-%m-%dT%H:%M:%S.%3NZ)"
# The execution view goes through order-api's sidecar to the workflow host: record how soon it answers
# after the kill (a route to the killed replica used to hang 15 s; see the journal).
view_start=$SECONDS; after=""
while (( SECONDS - view_start < 60 )); do
  after="$(curl -s -m 30 "$ORDER_API/system/orders/$order_id/execution" -H "X-Api-Key: $ADMIN_API_KEY")"
  [[ "$(json_get "$after" workflow.status)" == Completed ]] && break
  sleep 1
done
view_seconds=$((SECONDS - view_start))
echo "$after" > "$DRILL_DIR/execution-after.json"
wakes_after="$(sql OrderDb "SELECT STRING_AGG(CONCAT(w.Wake, ':', w.SeenStatus), ',') WITHIN GROUP (ORDER BY w.Id) FROM orders.ReviewWake w JOIN orders.ReviewTask r ON r.ReviewId = w.ReviewId WHERE r.OrderId = '$order_id'")"
resumed_line="$(docker compose --profile replicas logs --no-log-prefix --since "$INJECTED_AT" "$other_host" 2>/dev/null | grep "fulfillment $instance: review round [0-9]* resolved after" | head -1 | sed 's/^ *//')"
history_rows_after="$(sql DaprStateDb "SELECT COUNT(*) FROM dbo.state WHERE [Key] LIKE 'workflow-host||%||$instance||%'")"
same_instance=no; [[ "$(json_get "$after" workflow.createdAt)" == "$(json_get "$before" workflow.createdAt)" && -n "$(json_get "$after" workflow.createdAt)" ]] && same_instance=yes
observed="resumed on $other_host: '${resumed_line:-none}'; execution view answered after ${view_seconds}s; wakes $wakes_after; same instance (created $(json_get "$after" workflow.createdAt)): $same_instance; workflow $(json_get "$after" workflow.status); state rows $history_rows_before -> $history_rows_after; signal $(json_get "$after" signals.0.state)"
if [[ -n "$resumed_line" && "$same_instance" == yes && "$wakes_after" =~ ^Initial:Pending,(Signal|Timer):Approved$ && "$(json_get "$after" workflow.status)" == Completed ]]; then
  assert R01-resumed PASS "the same instance continued on $other_host and read the approval (wake ${wakes_after#*,})" "$observed"
else
  assert R01-resumed FAIL "continued on the other host, Initial:Pending then an Approved wake" "$observed"
fi
sql_orders_report "$RUN-c" > "$DRILL_DIR/sql-orders.jsonl"
consistent="$(sql_consistent "$DRILL_DIR/sql-orders.jsonl" 1)"
history="$(python -c "import json,sys; print(json.loads(open(sys.argv[1]).readline())['history'])" "$DRILL_DIR/sql-orders.jsonl")"
claims="$(python -c "import json,sys; print(json.loads(open(sys.argv[1]).readline())['claims'])" "$DRILL_DIR/sql-orders.jsonl")"
decisions="$(sql OrderDb "SELECT COUNT(*) FROM orders.ReviewDecision d JOIN orders.ReviewTask r ON r.ReviewId = d.ReviewId WHERE r.OrderId = '$order_id'")"
observed="$consistent; history $history; decisions $decisions; execution claims $claims"
if [[ "$consistent" == ok && "$history" == "Submitted,Validating,AwaitingApproval,Reserving,PaymentAuthorized,Packing,Dispatched,AwaitingDelivery,Completed" && "$decisions" == 1 && "$claims" == 1 ]]; then
  assert R01-completed PASS "Completed once; one decision; one effect set; one execution claim; start request Closed" "$observed"
else
  assert R01-completed FAIL "one of each" "$observed"
fi
