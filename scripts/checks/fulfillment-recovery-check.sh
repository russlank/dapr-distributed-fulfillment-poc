#!/usr/bin/env bash
# Recovery of fulfillment runs on a drill stack with replicas:
#   scripts/stack-up.sh --drill --replicas
# Each check fulfils one fresh order (two lines) and injects one fault into its run; the order must
# still end Completed exactly once, with exactly one committed effect per operation key:
#   REC-1 crash after commit   the order-api replica running the first simulated step dies right
#                              after the step's transaction committed, before the activity result
#                              reached Dapr (failpoint, FailFast); the step runs again elsewhere or
#                              later, finds its effect, and only counts a replay
#   REC-2 workflow host down   every workflow-host replica is SIGKILLed while the run waits for a
#                              held step (failpoint Delay on order-api), then started again; the run
#                              resumes from its stored history
#   REC-3 order-api replica    the order-api replica holding the step (Delay, before the step's
#                              transaction) is SIGKILLed; the step runs on the other replica
#   REC-4 hosts down in review every workflow-host replica is SIGKILLed while an order awaits
#                              approval; the approval is decided during the outage; after the restart
#                              the signal (or the timer) wakes the run, which reads the approval
#                              (diagnostics of the recovery delay: attempt log, sidecar errors, timing)
#   REC-5 diagnostic           after every workflow host restarts, how long invocation from both order
#                              API sidecars keeps failing after readiness, and with what
# The killed containers are started again by the cleanup trap. Evidence in artifacts/drills/REC-*.
# Usage: scripts/checks/fulfillment-recovery-check.sh   (exit 0 only if every check passed)
DRILL_ID=REC
. "$(dirname "$0")/../drills/lib.sh"

drill_preflight
for svc in order-api-2 workflow-host-2; do
  [[ "$(docker compose --profile replicas ps --format '{{.State}}' "$svc")" == running ]] && ready "$svc" \
    || { log "REFUSED: $svc is not running and ready (start the stack with --replicas)"; exit 2; }
done
trap 'drill_finish; exit $?' EXIT
on_cleanup "for s in order-api order-api-2 workflow-host workflow-host-2; do [[ \"\$(docker compose --profile replicas ps -a --format '{{.State}}' \$s)\" == running ]] || start_pair \$s; done"

STEP_COMMIT=AfterStubEffectCommitBeforeActivityCompletion
STEP_BEFORE=BeforeStubFulfillmentStep
running() { [[ "$(docker compose --profile replicas ps -a --format '{{.State}}' "$1")" == running ]]; }
api_base() { if running order-api; then echo "$ORDER_API"; else echo "$ORDER_API_2"; fi; }

submit_order() { # submit_order <tag> -> order id
  local customer="rec-$1-$(date -u +%H%M%S)-$RANDOM" out
  out="$(curl -s -X POST "$(api_base)/orders" -H "X-Api-Key: $CUSTOMER_API_KEY" -H "Idempotency-Key: $customer" -H 'content-type: application/json' \
    -d '{"customerId":"'$customer'","lines":[{"sku":"SKU-1","quantity":1},{"sku":"SKU-2","quantity":3}]}')"
  json_get "$out" orderId
}
arm_any() { # arm_any <name> <action>: one-shot row for any key, scoped to this run and check
  sql OrderDb "INSERT drill.Failpoint (Name, DrillRunId, OperationKey, Action) VALUES ('$1', '$DRILL_RUN_ID-$2', '*', '$3')"
}
fired_at() { sql OrderDb "SELECT CONVERT(varchar(23), FiredAt, 126) FROM drill.Failpoint WHERE DrillRunId = '$DRILL_RUN_ID-$2' AND Name = '$1' AND FiredAt IS NOT NULL"; }
on_cleanup "sql OrderDb \"DELETE drill.Failpoint WHERE DrillRunId LIKE '$DRILL_RUN_ID-%' AND FiredAt IS NULL\" >/dev/null"
completed() { [[ "$(sql OrderDb "SELECT CONCAT(o.Status, '|', r.State) FROM orders.[Order] o JOIN orders.WorkflowStartRequest r ON r.OrderId = o.OrderId WHERE o.OrderId = '$1'")" == "Completed|Closed" ]]; }
# Holder of a fired failpoint: the replica whose log shows it (the failpoint row does not name it).
holder_of() { local s; for s in order-api order-api-2; do docker compose --profile replicas logs --since 5m "$s" 2>/dev/null | grep -q "failpoint $1 fired.*$2" && { echo "$s"; return; }; done; }

# verify <check> <order id> <description>: Completed once, one effect per key, contiguous history.
verify() {
  local check=$1 order=$2 what=$3 row
  row="$(sql OrderDb "SELECT CONCAT(
    (SELECT Status FROM orders.[Order] WHERE OrderId = '$order'), '|',
    (SELECT STRING_AGG(CONCAT(Sequence, ':', ToStatus), ',') WITHIN GROUP (ORDER BY Sequence) FROM orders.OrderStatusHistory WHERE OrderId = '$order'), '|',
    (SELECT STRING_AGG(SUBSTRING(OperationKey, 34, 100), ',') WITHIN GROUP (ORDER BY OperationKey) FROM orders.StubEffect WHERE OrderId = '$order'), '|',
    (SELECT STRING_AGG(CONCAT(SUBSTRING(OperationKey, 34, 100), '=', AppliedBy, '/replays=', Replays), ', ') WITHIN GROUP (ORDER BY OperationKey) FROM orders.StubEffect WHERE OrderId = '$order'), '|',
    (SELECT CONCAT(ExecutionClaims, '/', FenceRejections) FROM orders.WorkflowStartRequest WHERE OrderId = '$order'), '|',
    (SELECT COUNT(*) FROM orders.OrderCheck WHERE OrderId = '$order'))")"
  IFS='|' read -r status history keys detail claims checks <<< "$row"
  echo "$check $order: $row" >> "$DRILL_DIR/orders.txt"
  # The view shows no workflow while the workflow host cannot be reached (observed for some seconds
  # after every workflow-host replica restarted, although the runs themselves resumed): wait for it.
  view() { admin_api GET "/system/orders/$order/execution" "" "$(api_base)" > "$DRILL_DIR/$check-execution.json"; [[ -n "$(json_get "$(cat "$DRILL_DIR/$check-execution.json")" workflow.status)" ]]; }
  wait_until 60 "$check workflow state readable" view
  local outcome; outcome="$(json_get "$(cat "$DRILL_DIR/$check-execution.json")" workflow.output.outcome)"
  if [[ "$status" == Completed && "$history" == "${EXPECT_HISTORY:-1:Submitted,2:Validating,3:Reserving,4:PaymentAuthorized,5:Packing,6:Dispatched,7:AwaitingDelivery,8:Completed}" \
        && "$keys" == "${EXPECT_KEYS:-stub-fulfil:line-1,stub-fulfil:line-2}" \
        && "$checks" == 2 && "$outcome" == Completed && -n "${4:-}" ]] && eval "$4"; then
    assert "$check" PASS "$what" "order $order Completed, history $history; effects $detail; claims/rejections $claims; workflow $outcome; ${5:-}"
  else
    assert "$check" FAIL "$what" "order $order: status=$status history=$history effects=$detail checks=$checks claims=$claims workflow=$outcome; ${5:-}"
  fi
}

# REC-1 crash after the step's commit, before the activity result reached Dapr --------------------
arm_any "$STEP_COMMIT" 1 FailFast
INJECTED_AT="$(date -u +%FT%TZ)"
order="$(submit_order 1)"
fired1() { [[ -n "$(fired_at "$STEP_COMMIT" 1)" ]]; }
wait_until 60 "REC-1 failpoint fired" fired1
dead_one() { ! running order-api || ! running order-api-2; }
wait_until 30 "an order-api replica exited" dead_one
dead="$(running order-api && echo order-api-2 || echo order-api)"
FAULT_OBSERVED="REC-1: $STEP_COMMIT fired at $(fired_at "$STEP_COMMIT" 1), $dead exited ($(docker inspect -f '{{.State.ExitCode}}' "$(docker compose --profile replicas ps -a -q "$dead")"))"
c1() { completed "$order"; }
wait_until 120 "REC-1 order completed" c1
line1="$(sql OrderDb "SELECT CONCAT(AppliedBy, '|', Replays) FROM orders.StubEffect WHERE OperationKey = '$(echo "$order" | tr -d '-' | tr 'A-F' 'a-f'):stub-fulfil:line-1'")"
start_pair "$dead"
verify REC-1 "$order" "after the crash the step ran again, found its committed effect (replay counted), and the order completed once" \
  "[[ \"${line1%%|*}\" == \"$dead\" && \"${line1##*|}\" -ge 1 ]]" "line-1 committed by $dead, replays ${line1##*|}"

# REC-2 every workflow-host replica down while a step is held ---------------------------------------
arm_any "$STEP_BEFORE" 2 Delay:10000
order="$(submit_order 2)"
fired2() { [[ -n "$(fired_at "$STEP_BEFORE" 2)" ]]; }
wait_until 60 "REC-2 hold fired" fired2
kill_pair workflow-host; kill_pair workflow-host-2
sleep 5
start_pair workflow-host; start_pair workflow-host-2
RECOVERED_AT="$(date -u +%FT%TZ)"
c2() { completed "$order"; }
wait_until 180 "REC-2 order completed" c2
verify REC-2 "$order" "with every workflow host killed mid-run and restarted, the run resumed and completed once" "true" \
  "hold fired at $(fired_at "$STEP_BEFORE" 2); workflow hosts down about 5 s"

# REC-3 the order-api replica holding the step is killed --------------------------------------------
arm_any "$STEP_BEFORE" 3 Delay:15000
order="$(submit_order 3)"
fired3() { [[ -n "$(fired_at "$STEP_BEFORE" 3)" ]]; }
wait_until 60 "REC-3 hold fired" fired3
key="$(echo "$order" | tr -d '-' | tr 'A-F' 'a-f'):stub-fulfil:line-1"
sleep 1
holder="$(holder_of "$STEP_BEFORE" "$key")"
[[ -n "$holder" ]] || holder=order-api
kill_pair "$holder"
c3() { completed "$order"; }
wait_until 180 "REC-3 order completed" c3
line1="$(sql OrderDb "SELECT CONCAT(AppliedBy, '|', Replays) FROM orders.StubEffect WHERE OperationKey = '$key'")"
start_pair "$holder"
verify REC-3 "$order" "with the replica holding the step killed, the step ran on the other replica (or after restart) and the order completed once" \
  "true" "step held by $holder; line-1 committed by ${line1%%|*}, replays ${line1##*|}"

# REC-4 every workflow host down while an order awaits approval; approved during the outage --------
# Also the diagnostics for the delay between workflow-host recovery and signal delivery: attempt log
# (result, HTTP status, invoker calls, transport error), the calling sidecars' invocation errors, and
# the time from "workflow hosts ready" to the signal's delivery. Expectation: delivered (or made moot
# by the workflow's own timer) within 60 s of the hosts being ready, the order completed once.
customer="rec-4-$(date -u +%H%M%S)-$RANDOM"
order="$(json_get "$(curl -s -X POST "$(api_base)/orders" -H "X-Api-Key: $CUSTOMER_API_KEY" -H "Idempotency-Key: $customer" \
  -H 'content-type: application/json' -d "$(review_order_body "$customer")")" orderId)"
waiting4() { [[ "$(sql OrderDb "SELECT COUNT(*) FROM orders.ReviewWake w JOIN orders.ReviewTask r ON r.ReviewId = w.ReviewId WHERE r.OrderId = '$order'")" -ge 1 ]]; }
wait_until 60 "REC-4 order waiting for review" waiting4
killed_at="$(sql OrderDb "SELECT CONVERT(varchar(23), SYSUTCDATETIME(), 126)")"
kill_pair workflow-host; kill_pair workflow-host-2
review="$(sql OrderDb "SELECT ReviewId FROM orders.ReviewTask WHERE OrderId = '$order' AND Round = 1")"
read -r d_status d_body <<< "$(decide "$(api_base)" "$review" approve)"
sleep 10
start_pair workflow-host; start_pair workflow-host-2
ready_at="$(sql OrderDb "SELECT CONVERT(varchar(23), SYSUTCDATETIME(), 126)")"
c4() { completed "$order"; }
wait_until 180 "REC-4 order completed" c4
delivered4() { [[ "$(sql OrderDb "SELECT State FROM orders.PendingSignal WHERE OrderId = '$order' AND EventName LIKE 'ReviewSignal%'")" != Pending ]]; }
wait_until 120 "REC-4 signal finished" delivered4
sql OrderDb "SELECT CONCAT(a.Attempt, ' ', CONVERT(varchar(23), a.CalledAt, 126), ' ', ISNULL(a.Result, 'unfinished'), ' ', LEFT(a.DispatcherId, CHARINDEX('/', a.DispatcherId) - 1), ' http=', a.HttpStatus, ' calls=', a.InvocationAttempts, ' reported=', a.ReportedStatus, ' transport=', a.TransportError)
  FROM orders.SignalAttempt a JOIN orders.PendingSignal s ON s.SignalId = a.SignalId WHERE s.OrderId = '$order' AND s.EventName LIKE 'ReviewSignal%' ORDER BY a.Id" > "$DRILL_DIR/REC-4-attempts.txt"
for s in order-api-dapr order-api-2-dapr; do
  docker compose --profile replicas logs --since 5m -t "$s" 2>/dev/null | grep -i -E "level=(error|warning)" | grep -i -E "invoke|resolv|workflow-host|connect" >> "$DRILL_DIR/REC-4-sidecar-errors.txt"
done
row="$(sql OrderDb "SELECT CONCAT(s.State, '|', s.Attempts, '|', DATEDIFF(millisecond, '$ready_at', ISNULL(s.ForwardedAt, s.ClosedAt)), '|',
  (SELECT TOP 1 CONVERT(varchar(23), a.CalledAt, 126) FROM orders.SignalAttempt a WHERE a.SignalId = s.SignalId AND a.Result IN ('Forwarded', 'Discarded') ORDER BY a.Id))
  FROM orders.PendingSignal s WHERE s.OrderId = '$order' AND s.EventName LIKE 'ReviewSignal%'")"
IFS='|' read -r s4_state s4_attempts s4_delay s4_success_call <<< "$row"
wakes4="$(sql OrderDb "SELECT STRING_AGG(CONCAT(w.Wake, ':', w.SeenStatus), ',') WITHIN GROUP (ORDER BY w.Id) FROM orders.ReviewWake w JOIN orders.ReviewTask r ON r.ReviewId = w.ReviewId WHERE r.OrderId = '$order'")"
attempts4="$(awk '{print $3"/"$5"/"$6}' "$DRILL_DIR/REC-4-attempts.txt" | sort | uniq -c | tr -s ' ' | tr '\n' ';')"
sidecar4="$(wc -l < "$DRILL_DIR/REC-4-sidecar-errors.txt" | tr -d ' ') sidecar invocation warnings/errors"
detail="decision during outage: $d_status; hosts killed $killed_at, ready $ready_at; signal $s4_state after $s4_attempts attempts, first successful call $s4_success_call, delivered ${s4_delay} ms after the hosts were ready; attempts (result/http/calls): $attempts4 $sidecar4; wakes $wakes4"
FAULT_OBSERVED="$FAULT_OBSERVED; REC-4: $detail"
if [[ "$d_status" == 200 && ( "$s4_state" == Forwarded || "$s4_state" == Discarded ) && "${s4_delay:-999999}" -le 60000 && "$wakes4" == *":Approved" ]]; then
  EXPECT_HISTORY="1:Submitted,2:Validating,3:AwaitingApproval,4:Reserving,5:PaymentAuthorized,6:Packing,7:Dispatched,8:AwaitingDelivery,9:Completed" EXPECT_KEYS="stub-fulfil:line-1" verify REC-4 "$order" "decided while every workflow host was down: after their restart the run resumed, read the approval, completed once" "true" "$detail"
else
  assert REC-4 FAIL "delivered within 60 s of recovery, completed once" "$detail"
fi

# REC-5 diagnostic: invocation availability after every workflow host restarts -----------------------
# Measures, from both order API sidecars, how long service invocation of the workflow host keeps
# failing after the hosts report ready (and with what), polling every 0.5 s. Expectation (recorded,
# not a timeout fix): invocation succeeds within 60 s of readiness.
kill_pair workflow-host; kill_pair workflow-host-2
sleep 3
down_answer="$(invoke_probe order-api)"
start_pair workflow-host; start_pair workflow-host-2
ready_s=$SECONDS; ready5="$(date -u +%H:%M:%S.%3N)"
: > "$DRILL_DIR/REC-5-probes.txt"
declare -A first_ok=()
while (( SECONDS - ready_s < 60 )) && (( ${#first_ok[@]} < 2 )); do
  for s in order-api order-api-2; do
    [[ -n "${first_ok[$s]:-}" ]] && continue
    answer="$(invoke_probe "$s")"
    echo "$(date -u +%H:%M:%S.%3N) $s $answer" >> "$DRILL_DIR/REC-5-probes.txt"
    [[ "$answer" == 200* ]] && first_ok[$s]="$(( SECONDS - ready_s ))s"
  done
  sleep 0.5
done
failures5="$(grep -v ' 200 ' "$DRILL_DIR/REC-5-probes.txt" | awk '{$1=""; $2=""; print}' | sort | uniq -c | sort -rn | head -3 | tr -s ' ' | tr '\n' ';')"
detail="while down: '${down_answer:0:120}'; hosts ready at $ready5; first success order-api ${first_ok[order-api]:-none}, order-api-2 ${first_ok[order-api-2]:-none}; $(grep -c ' 200 ' "$DRILL_DIR/REC-5-probes.txt") ok / $(grep -vc ' 200 ' "$DRILL_DIR/REC-5-probes.txt") failed probes; failures: $failures5"
if [[ -n "${first_ok[order-api]:-}" && -n "${first_ok[order-api-2]:-}" ]]; then
  assert REC-5 PASS "invocation of the workflow host works again within 60 s of readiness (diagnostic)" "$detail"
else
  assert REC-5 FAIL "invocation works again within 60 s" "$detail"
fi
