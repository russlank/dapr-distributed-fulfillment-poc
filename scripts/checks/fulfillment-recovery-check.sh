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
  if [[ "$status" == Completed && "$history" == "1:Submitted,2:Validating,3:Completed" && "$keys" == "stub-complete:1,stub-fulfil:line-1,stub-fulfil:line-2" \
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
