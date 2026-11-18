#!/usr/bin/env bash
# S49 execution fence on a completed order (drill program 6.1, variants C and D, without the other
# drill machinery). Runs on a drill stack (core or --replicas): scripts/stack-up.sh --drill
# A fresh order is fulfilled to Completed; its business rows are fingerprinted (status, history,
# simulated effects with their replay counts, check results). Then:
#   C  the drill-only reset puts its start request back to Pending (operator error or bug); the
#      start dispatcher must close it again (OrderTerminal) without any start call;
#   D  the workflow host's drill-only control schedules fulfill-{orderId:N} directly through the
#      Dapr workflow API (manual restart, id reuse); Dapr's answer is recorded (assumption A3); a
#      new run must end at the execution claim (AlreadyTerminal), counted and audited.
# PASS when, from OrderDb rows and the workflow host's view:
#   S49-setup       the order is Completed, its request Closed, its workflow Completed
#   S49-C-reset     proof: the request is Pending while the order is Completed
#   S49-C-guard     the dispatcher closes it again (OrderTerminal); no start attempt, no new instance
#   S49-C-unchanged business fingerprint, claims, and attempts unchanged
#   S49-D-schedule  Dapr accepted the direct schedule (else D is UNVERIFIED)
#   S49-D-fenced    the new run ended AlreadyTerminal after the claim: one more fence rejection and
#                   one AlreadyTerminal audit row, no new Proceed claim
#   S49-D-unchanged business fingerprint unchanged
#   S49-D-metric    fence_rejections_total rose (Prometheus)
#   S49-restore     the trap restores the start request to its saved row
DRILL_ID=S49
. "$(dirname "$0")/../drills/lib.sh"

drill_preflight
trap 'drill_finish; exit $?' EXIT
CUSTOMER="s49-$(date -u +%H%M%S)-$RANDOM"

out="$(curl -s -w '\n%{http_code}' -X POST "$ORDER_API/orders" -H "X-Api-Key: $CUSTOMER_API_KEY" -H "Idempotency-Key: $CUSTOMER" \
  -H 'content-type: application/json' -d '{"customerId":"'$CUSTOMER'","lines":[{"sku":"SKU-1","quantity":2},{"sku":"SKU-2","quantity":1}]}')"
status="${out##*$'\n'}"; body="${out%$'\n'*}"
ORDER="$(json_get "$body" orderId)"
[[ "$status" == 201 && -n "$ORDER" ]] || { log "could not submit the order: $status $body"; exit 1; }
log "order $ORDER"
execution() { admin_api GET "/system/orders/$ORDER/execution"; }
completed() { [[ "$(sql OrderDb "SELECT CONCAT(o.Status, '|', r.State) FROM orders.[Order] o JOIN orders.WorkflowStartRequest r ON r.OrderId = o.OrderId WHERE o.OrderId = '$ORDER'")" == "Completed|Closed" \
               && "$(json_get "$(execution)" workflow.status)" == Completed ]]; }
wait_until 90 "order Completed" completed

fingerprint() {
  sql OrderDb "SELECT CONCAT(
    (SELECT Status FROM orders.[Order] WHERE OrderId = '$ORDER'), ' # ',
    (SELECT STRING_AGG(CONCAT(Sequence, ':', FromStatus, '>', ToStatus, ':', ReasonCode, '@', CONVERT(varchar(23), OccurredAt, 126)), ', ') WITHIN GROUP (ORDER BY Sequence)
       FROM orders.OrderStatusHistory WHERE OrderId = '$ORDER'), ' # ',
    (SELECT STRING_AGG(CONCAT(SUBSTRING(OperationKey, 34, 100), '@', CONVERT(varchar(23), AppliedAt, 126), '/', AppliedBy, '/replays=', Replays), ', ') WITHIN GROUP (ORDER BY OperationKey)
       FROM orders.StubEffect WHERE OrderId = '$ORDER'), ' # ',
    (SELECT STRING_AGG(CONCAT(CheckName, '=', TotalMinor, RiskScore, '/replays=', Replays), ', ') WITHIN GROUP (ORDER BY CheckName)
       FROM orders.OrderCheck WHERE OrderId = '$ORDER'))"
}
counters() { sql OrderDb "SELECT CONCAT(r.ExecutionClaims, '|', r.FenceRejections, '|',
  (SELECT COUNT(*) FROM orders.ExecutionClaim WHERE OrderId = r.OrderId AND Outcome = 'Proceed'), '|',
  (SELECT COUNT(*) FROM orders.ExecutionClaim WHERE OrderId = r.OrderId AND Outcome = 'AlreadyTerminal'), '|',
  (SELECT COUNT(*) FROM orders.WorkflowStartAttempt WHERE OrderId = r.OrderId)) FROM orders.WorkflowStartRequest r WHERE r.OrderId = '$ORDER'"; }
request_row() { sql OrderDb "SELECT CONCAT(State, '|', ISNULL(LastErrorCode, '-'), '|', CONVERT(varchar(23), StartedAt, 126), '|', CONVERT(varchar(23), ClosedAt, 126)) FROM orders.WorkflowStartRequest WHERE OrderId = '$ORDER'"; }

BASELINE="$(fingerprint)"; echo "$BASELINE" > "$DRILL_DIR/fingerprint-baseline.txt"
REQUEST_BASELINE="$(request_row)"
execution > "$DRILL_DIR/execution-baseline.json"
created_before="$(json_get "$(cat "$DRILL_DIR/execution-baseline.json")" workflow.createdAt)"
IFS='|' read -r claims0 rejections0 proceed0 fenced0 attempts0 <<< "$(counters)"
outcome0="$(json_get "$(cat "$DRILL_DIR/execution-baseline.json")" workflow.output.outcome)"
if [[ "$outcome0" == Completed && "$BASELINE" == Completed* ]]; then
  assert S49-setup PASS "order Completed, start request Closed, workflow Completed" "request $REQUEST_BASELINE; workflow outcome $outcome0; claims=$claims0"
else
  assert S49-setup FAIL "Completed order" "outcome=$outcome0 fingerprint=$BASELINE"
  exit 1
fi

# C: reset the start request of the completed order ------------------------------------------------
INJECTED_AT="$(date -u +%FT%TZ)"
reset="$(drill_reset_start_request "$ORDER" 10)" || { assert S49-C-reset FAIL "reset applied" "control refused: $reset"; exit 1; }
on_cleanup "check_restore"
check_restore() {
  local restored
  restored="$(drill_restore_start_request "$ORDER")"
  if [[ "$(request_row)" == "$REQUEST_BASELINE" ]]; then
    assert S49-restore PASS "start request restored to its saved row" "$restored; row $(request_row)"
  else
    assert S49-restore FAIL "$REQUEST_BASELINE" "$restored; row $(request_row)"
  fi
}
proof="$(sql OrderDb "SELECT CONCAT(o.Status, '|', r.State) FROM orders.[Order] o JOIN orders.WorkflowStartRequest r ON r.OrderId = o.OrderId WHERE o.OrderId = '$ORDER'")"
FAULT_OBSERVED="reset: $proof"
assert S49-C-reset "$([[ "$proof" == "Completed|Pending" ]] && echo PASS || echo FAIL)" "request Pending while the order is Completed" "order|request = $proof ($reset)"

guard_closed() { [[ "$(sql OrderDb "SELECT CONCAT(State, '|', LastErrorCode) FROM orders.WorkflowStartRequest WHERE OrderId = '$ORDER'")" == "Closed|OrderTerminal" ]]; }
wait_until 60 "the dispatcher closes the reset request" guard_closed
after_c="$(execution)"; echo "$after_c" > "$DRILL_DIR/execution-after-c.json"
IFS='|' read -r claims1 rejections1 proceed1 fenced1 attempts1 <<< "$(counters)"
created_c="$(json_get "$after_c" workflow.createdAt)"
observed="request $(request_row); start attempts $attempts0 -> $attempts1; instance created $created_before -> $created_c"
if guard_closed && [[ "$attempts1" == "$attempts0" && "$created_c" == "$created_before" ]]; then
  assert S49-C-guard PASS "closed again by the terminal guard (OrderTerminal); no start attempt; no new instance" "$observed"
else
  assert S49-C-guard FAIL "Closed/OrderTerminal, no attempt, same instance" "$observed"
fi
fp_c="$(fingerprint)"; echo "$fp_c" > "$DRILL_DIR/fingerprint-after-c.txt"
if [[ "$fp_c" == "$BASELINE" && "$claims1|$rejections1|$proceed1|$fenced1" == "$claims0|$rejections0|$proceed0|$fenced0" ]]; then
  assert S49-C-unchanged PASS "status, history, effects, checks, claims unchanged" "fingerprint equal; claims|rejections|proceed|fenced = $claims1|$rejections1|$proceed1|$fenced1"
else
  assert S49-C-unchanged FAIL "unchanged" "claims $claims0|$rejections0|$proceed0|$fenced0 -> $claims1|$rejections1|$proceed1|$fenced1; see fingerprint files"
fi

# D: schedule the completed order's instance directly ----------------------------------------------
metric_before="$(prometheus_sum fence_rejections_total)"
answer="$(drill_schedule_fulfillment "$ORDER")"
echo "$answer" > "$DRILL_DIR/schedule-answer.json"
scheduled="$(json_get "$answer" scheduled)"
FAULT_OBSERVED="$FAULT_OBSERVED; direct schedule: $answer"
if [[ "$scheduled" == True ]]; then
  assert S49-D-schedule PASS "Dapr accepted the direct schedule of the existing id (recorded for A3)" "$answer"
else
  assert S49-D-schedule UNVERIFIED "Dapr accepted the direct schedule" "$answer"
fi
rerun_done() {
  local e; e="$(execution)"
  [[ "$(json_get "$e" workflow.status)" == Completed && "$(json_get "$e" workflow.createdAt)" != "$created_before" ]]
}
if [[ "$scheduled" == True ]]; then
  wait_until 90 "the new run to complete" rerun_done
  after_d="$(execution)"; echo "$after_d" > "$DRILL_DIR/execution-after-d.json"
  RECOVERED_AT="$(date -u +%FT%TZ)"
  IFS='|' read -r claims2 rejections2 proceed2 fenced2 attempts2 <<< "$(counters)"
  outcome="$(json_get "$after_d" workflow.output.outcome)"
  observed="outcome $outcome (created $(json_get "$after_d" workflow.createdAt)); claims $claims1 -> $claims2; rejections $rejections1 -> $rejections2; audit Proceed $proceed1 -> $proceed2, AlreadyTerminal $fenced1 -> $fenced2"
  if [[ "$outcome" == AlreadyTerminal && "$claims2" == "$claims1" && "$rejections2" == $((rejections1 + 1)) && "$proceed2" == "$proceed1" && "$fenced2" == $((fenced1 + 1)) ]]; then
    assert S49-D-fenced PASS "the new run ended at the claim (AlreadyTerminal); one more rejection, audited" "$observed"
  else
    assert S49-D-fenced FAIL "AlreadyTerminal, rejections +1, audit AlreadyTerminal +1, claims unchanged" "$observed"
  fi
  fp_d="$(fingerprint)"; echo "$fp_d" > "$DRILL_DIR/fingerprint-after-d.txt"
  assert S49-D-unchanged "$([[ "$fp_d" == "$BASELINE" ]] && echo PASS || echo FAIL)" "status, history, effects, checks unchanged" \
    "$([[ "$fp_d" == "$BASELINE" ]] && echo "fingerprint equal" || echo "fingerprint differs: see $DRILL_DIR")"
  # The application exports metrics every 60 s and Prometheus scrapes every 15 s.
  metric_rose() { local now; now="$(prometheus_sum fence_rejections_total)"; [[ -n "$now" ]] && python -c "import sys; sys.exit(0 if float(sys.argv[1]) >= float(sys.argv[2] or 0) + 1 else 1)" "$now" "$metric_before"; }
  if wait_until 150 "fence_rejections_total to rise" metric_rose; then
    assert S49-D-metric PASS "fence_rejections_total rose" "sum ${metric_before:-absent} -> $(prometheus_sum fence_rejections_total)"
  else
    assert S49-D-metric FAIL "fence_rejections_total +1" "sum ${metric_before:-absent} -> $(prometheus_sum fence_rejections_total)"
  fi
fi
