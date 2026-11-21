#!/usr/bin/env bash
# S52 workflow signaling through the workflow host's management API (drill program 6.13). Runs on a
# drill stack with replicas (drill time profile: review deadline 20 s, timer grace 2 s):
#   bash scripts/stack-up.sh --drill --replicas
# Every variant uses a fresh order that requires a review. The decision in SQL is the authority;
# the raised event only wakes the workflow, which re-reads SQL. PASS per variant, from OrderDb rows
# (review, decision, signal and its attempt log, the workflow's review wake log, status history)
# and the workflow host's logs:
#   S52-A  decision before the wait: the run is held right after its review round committed
#          (failpoint AfterReviewTaskCreateCommit, Delay); the approval and its signal are committed
#          and delivered while the run has not resolved the review yet (proof: no wake row); after
#          release the run reads Approved at its first resolution and completes without waiting
#   S52-B  duplicate delivery after a dispatcher crash: the replica that raised the signal ends before
#          marking it (failpoint AfterSignalRaiseBeforeMarkForwarded, FailFast); after the lease the
#          signal is raised again by another replica (two attempts, two raise calls in the workflow
#          host's log); the decision is applied once (one resolution after the signal, logged by the
#          workflow, that saw Approved; one completion). If the crash also lost the answer of that
#          resolution's activity (it can run on the dying replica), Dapr runs it again and its wake
#          row repeats as Signal:Approved
#   S52-B2 duplicate and early wake-ups: before any decision the review event is raised directly
#          from both order API replicas; the run wakes, reads Pending, waits again, changes nothing;
#          the approval afterwards completes it once
#   S52-C  delivery racing termination: the approval is committed shortly before the SQL deadline
#          and its delivery held past the deadline (failpoint BeforeSignalRaise, Delay); the run wakes
#          by its timer, reads Approved (decided in time), completes; the late raise finds the
#          instance terminal: the signal ends Discarded (WorkflowTerminal), never Pending
#   S52-C2 decision versus final expiry: no decision until the last round's deadline; a decision at
#          that moment either commits (then the order completes) or gets 409 review-expired (then the
#          order is Rejected, ReviewExpired), never both; no signal without a decision
DRILL_ID=S52
. "$(dirname "$0")/../drills/lib.sh"

drill_preflight
for svc in order-api-2 workflow-host-2; do
  [[ "$(docker compose --profile replicas ps --format '{{.State}}' "$svc")" == running ]] && ready "$svc" \
    || { log "REFUSED: $svc is not running and ready (start the stack with --replicas)"; exit 2; }
done
trap 'drill_finish; exit $?' EXIT
on_cleanup "for s in order-api order-api-2; do [[ \"\$(docker compose --profile replicas ps -a --format '{{.State}}' \$s)\" == running ]] || start_pair \$s; done"
on_cleanup "sql OrderDb \"DELETE drill.Failpoint WHERE DrillRunId LIKE '$DRILL_RUN_ID-%' AND FiredAt IS NULL\" >/dev/null"
PREFIX="s52-$(date -u +%H%M%S)-$RANDOM"
running() { [[ "$(docker compose --profile replicas ps -a --format '{{.State}}' "$1")" == running ]]; }
api_base() { if running order-api; then echo "$ORDER_API"; else echo "$ORDER_API_2"; fi; }

submit() { # submit <tag> -> order id
  json_get "$(curl -s -X POST "$(api_base)/orders" -H "X-Api-Key: $CUSTOMER_API_KEY" -H "Idempotency-Key: $PREFIX-$1" \
    -H 'content-type: application/json' -d "$(review_order_body "$PREFIX-$1")")" orderId
}
arm() { sql OrderDb "INSERT drill.Failpoint (Name, DrillRunId, OperationKey, Action) VALUES ('$1', '$DRILL_RUN_ID-$2', '${4:-*}', '$3')"; }
fired_at() { sql OrderDb "SELECT CONVERT(varchar(23), FiredAt, 126) FROM drill.Failpoint WHERE DrillRunId = '$DRILL_RUN_ID-$2' AND Name = '$1' AND FiredAt IS NOT NULL"; }
review_of() { sql OrderDb "SELECT ReviewId FROM orders.ReviewTask WHERE OrderId = '$1' AND Round = ${2:-1}"; }
wakes() { sql OrderDb "SELECT ISNULL(STRING_AGG(CONCAT(w.Wake, ':', w.SeenStatus), ',') WITHIN GROUP (ORDER BY w.Id), '') FROM orders.ReviewWake w JOIN orders.ReviewTask r ON r.ReviewId = w.ReviewId WHERE r.OrderId = '$1'"; }
history() { sql OrderDb "SELECT STRING_AGG(CONCAT(Sequence, ':', ToStatus, ':', ReasonCode), ',') WITHIN GROUP (ORDER BY Sequence) FROM orders.OrderStatusHistory WHERE OrderId = '$1'"; }
signal_row() { sql OrderDb "SELECT CONCAT(s.State, '|', ISNULL(s.LastErrorCode, '-'), '|', s.Attempts, '|',
  (SELECT STRING_AGG(CONCAT(ISNULL(a.Result, 'unfinished'), '@', LEFT(a.DispatcherId, CHARINDEX('/', a.DispatcherId) - 1)), ',') WITHIN GROUP (ORDER BY a.Id) FROM orders.SignalAttempt a WHERE a.SignalId = s.SignalId))
  FROM orders.PendingSignal s WHERE s.OrderId = '$1' AND s.EventName LIKE 'ReviewSignal%'"; }
status_of() { sql OrderDb "SELECT Status FROM orders.[Order] WHERE OrderId = '$1'"; }
event_of() { echo "ReviewSignal_$(echo "$1" | tr -d '-' | tr 'A-F' 'a-f')"; }
instance_of() { echo "fulfill-$(echo "$1" | tr -d '-' | tr 'A-F' 'a-f')"; }
evidence() { admin_api GET "/system/orders/$2/execution" "" "$(api_base)" > "$DRILL_DIR/$1-execution.json"; }
waiting() { [[ "$(wakes "$1")" == "Initial:Pending" && "$(status_of "$1")" == AwaitingApproval ]]; }
completed_once() { [[ "$(status_of "$1")" == Completed && "$(sql OrderDb "SELECT COUNT(*) FROM orders.OrderStatusHistory WHERE OrderId = '$1' AND ToStatus = 'Completed'")" == 1 ]]; }
COMPLETE_HISTORY="1:Submitted:Accepted,2:Validating:ValidationStarted,3:AwaitingApproval:ReviewRequired,4:Reserving:ReviewApproved,5:PaymentAuthorized:PaymentAuthorized,6:Packing:PackingStarted,7:Dispatched:ShipmentDispatched,8:AwaitingDelivery:DeliveryAwaited,9:Completed:Delivered"
INJECTED_AT="$(date -u +%FT%TZ)"

# S52-A decision committed before the run reaches its wait ---------------------------------------
arm AfterReviewTaskCreateCommit A Delay:12000
A="$(submit A)"
fired_a() { [[ -n "$(fired_at AfterReviewTaskCreateCommit A)" ]]; }
wait_until 60 "A: run held after its review round" fired_a
review="$(review_of "$A")"
read -r status body <<< "$(decide "$(api_base)" "$review" approve)"
forwarded_a() { [[ "$(signal_row "$A")" == Forwarded* ]]; }
wait_until 20 "A: signal delivered while the run is held" forwarded_a
proof="decision $status; signal $(signal_row "$A"); wake rows before release: '$(wakes "$A")'"
c_a() { completed_once "$A"; }
wait_until 60 "A: order completed" c_a
evidence A "$A"
observed="$proof; after: wakes $(wakes "$A"); history $(history "$A")"
if [[ "$status" == 200 && "$proof" == *"wake rows before release: ''"* && "$(wakes "$A")" == "Initial:Approved" && "$(history "$A")" == "$COMPLETE_HISTORY" ]]; then
  assert S52-A PASS "decision and delivery before the wait; first resolution reads Approved; completed once without waiting" "$observed"
else
  assert S52-A FAIL "no wake before release; wakes Initial:Approved; completed once" "$observed"
fi

# S52-B duplicate delivery after a dispatcher crash -----------------------------------------------
B="$(submit B)"
w_b() { waiting "$B"; }
wait_until 60 "B: run waiting" w_b
arm AfterSignalRaiseBeforeMarkForwarded B FailFast
read -r status body <<< "$(decide "$(api_base)" "$(review_of "$B")" approve)"
fired_b() { [[ -n "$(fired_at AfterSignalRaiseBeforeMarkForwarded B)" ]]; }
wait_until 30 "B: dispatcher crashed after its raise" fired_b
dead_b() { ! running order-api || ! running order-api-2; }
wait_until 30 "B: an order API replica exited" dead_b
dead="$(running order-api && echo order-api-2 || echo order-api)"
FAULT_OBSERVED="B: $dead ended after raising (FailFast)"
closed_b() { [[ "$(signal_row "$B")" == Forwarded* || "$(signal_row "$B")" == Discarded* ]]; }
wait_until 90 "B: signal re-delivered after the lease" closed_b
c_b() { completed_once "$B"; }
wait_until 30 "B: order completed" c_b
start_pair "$dead"
evidence B "$B"
raises="$(docker compose --profile replicas logs --since 10m workflow-host workflow-host-2 2>/dev/null | grep -c "Request starting HTTP/1.1 POST .*/events/$(event_of "$(review_of "$B")") ")"
row="$(signal_row "$B")"; IFS='|' read -r b_state b_code b_attempts b_log <<< "$row"
# One logical wake-up: the workflow's replay-safe log names one resolution after the signal. The
# crash can also hit the replica that runs that resolution (both order API replicas host the
# activities): its wake row is committed, the answer lost, and Dapr runs the activity again, which
# adds an identical row. Extra rows are accepted only as such re-executions: Signal:Approved again,
# with one logged resolution.
resolutions="$(docker compose --profile replicas logs --since 10m workflow-host workflow-host-2 2>/dev/null | grep -c "fulfill-${B//-/}: review round 1 resolved after Signal: Approved")"
b_wakes="$(wakes "$B")"
one_wake=false
[[ "$b_wakes" =~ ^Initial:Pending(,Signal:Approved)+$ && "$resolutions" == 1 ]] && one_wake=true
observed="signal $row; raise calls received by the workflow host: $raises; wakes $b_wakes (resolutions logged by the workflow: $resolutions); history $(history "$B")"
if [[ "$b_attempts" -ge 2 && "$b_log" == unfinished@* && "${raises:-0}" -ge 2 && "$one_wake" == true && "$(history "$B")" == "$COMPLETE_HISTORY" ]]; then
  assert S52-B PASS "raised again after the crash (>= 2 attempts, the first unfinished); decision applied once" "$observed"
else
  assert S52-B FAIL ">= 2 attempts, first unfinished; one resolution after the signal (wake rows Initial:Pending,Signal:Approved, repeated only by a re-executed activity); completed once" "$observed"
fi

# S52-B2 duplicate and early wake-ups from both replicas -------------------------------------------
B2="$(submit B2)"
w_b2() { waiting "$B2"; }
wait_until 60 "B2: run waiting" w_b2
event="$(event_of "$(review_of "$B2")")"; instance="$(instance_of "$B2")"
r1="$(raise_via_sidecar order-api "$instance" "$event")"; r2="$(raise_via_sidecar order-api-2 "$instance" "$event")"
two_wakes() { [[ "$(sql OrderDb "SELECT COUNT(*) FROM orders.ReviewWake w JOIN orders.ReviewTask r ON r.ReviewId = w.ReviewId WHERE r.OrderId = '$B2'")" -ge 3 ]]; }
wait_until 30 "B2: two early wake-ups resolved" two_wakes
early="$(wakes "$B2") status $(status_of "$B2")"
read -r status body <<< "$(decide "$ORDER_API_2" "$(review_of "$B2")" approve)"
c_b2() { completed_once "$B2"; }
wait_until 60 "B2: order completed" c_b2
evidence B2 "$B2"
observed="raises: $(json_get "$r1" raised)/$(json_get "$r2" raised); after early wake-ups: $early; final wakes $(wakes "$B2"); history $(history "$B2")"
if [[ "$early" == "Initial:Pending,Signal:Pending,Signal:Pending status AwaitingApproval" && "$(wakes "$B2")" == "Initial:Pending,Signal:Pending,Signal:Pending,Signal:Approved" && "$(history "$B2")" == "$COMPLETE_HISTORY" ]]; then
  assert S52-B2 PASS "early wake-ups from both replicas read Pending and change nothing; the approval completes the order once" "$observed"
else
  assert S52-B2 FAIL "Initial:Pending,Signal:Pending,Signal:Pending then Signal:Approved; completed once" "$observed"
fi

# S52-C delivery racing termination ---------------------------------------------------------------
C="$(submit C)"
w_c() { waiting "$C"; }
wait_until 60 "C: run waiting" w_c
review="$(review_of "$C")"
near_deadline() { [[ "$(sql OrderDb "SELECT CASE WHEN DATEDIFF(millisecond, SYSUTCDATETIME(), DeadlineAt) < 3000 THEN 1 ELSE 0 END FROM orders.ReviewTask WHERE ReviewId = '$review'")" == 1 ]]; }
wait_until 30 "C: 3 s before the deadline" near_deadline
# Held 20 s (within the dispatcher's 30 s lease): the run must finish its whole fulfillment (payment, packing,
# carrier callbacks) after the timer fired before the raise lands; 12 s left too little margin.
arm BeforeSignalRaise C Delay:20000
read -r status body <<< "$(decide "$(api_base)" "$review" approve)"
c_c() { completed_once "$C"; }
wait_until 60 "C: order completed by the timer path" c_c
closed_c() { [[ "$(signal_row "$C")" == Discarded* || "$(signal_row "$C")" == Forwarded* ]]; }
wait_until 60 "C: held signal finished" closed_c
RECOVERED_AT="$(date -u +%FT%TZ)"
evidence C "$C"
in_time="$(sql OrderDb "SELECT CONCAT(CASE WHEN DecidedAt < DeadlineAt THEN 'in-time' ELSE 'late' END, ' by ', DATEDIFF(millisecond, DecidedAt, DeadlineAt), ' ms') FROM orders.ReviewTask WHERE ReviewId = '$review'")"
observed="decision $status ($in_time); hold fired $(fired_at BeforeSignalRaise C); signal $(signal_row "$C"); wakes $(wakes "$C"); history $(history "$C")"
if [[ "$status" == 200 && "$in_time" == in-time* && "$(signal_row "$C")" == Discarded\|WorkflowTerminal* && "$(wakes "$C")" == "Initial:Pending,Timer:Approved" && "$(history "$C")" == "$COMPLETE_HISTORY" ]]; then
  assert S52-C PASS "approved in time, delivered too late: the timer path applied it once; the signal Discarded (WorkflowTerminal)" "$observed"
elif [[ "$status" == 200 && "$(signal_row "$C")" == Forwarded* && "$(history "$C")" == "$COMPLETE_HISTORY" ]]; then
  assert S52-C UNVERIFIED "the late raise found the instance terminal" "the raise landed while the run was still running: $observed"
else
  assert S52-C FAIL "completed once; signal Discarded (WorkflowTerminal); wakes Initial:Pending,Timer:Approved" "$observed"
fi

# S52-C2 decision versus final expiry -------------------------------------------------------------
C2="$(submit C2)"
w_c2() { waiting "$C2"; }
wait_until 60 "C2: run waiting" w_c2
round2() { [[ -n "$(review_of "$C2" 2)" ]]; }
wait_until 60 "C2: round 1 expired, round 2 open" round2
review2="$(review_of "$C2" 2)"
at_deadline() { [[ "$(sql OrderDb "SELECT CASE WHEN DATEDIFF(millisecond, SYSUTCDATETIME(), DeadlineAt) < 150 THEN 1 ELSE 0 END FROM orders.ReviewTask WHERE ReviewId = '$review2'")" == 1 ]]; }
wait_until 40 "C2: the last round's deadline" at_deadline
read -r status body <<< "$(decide "$(api_base)" "$review2" approve)"
terminal_c2() { [[ "$(status_of "$C2")" == Completed || "$(status_of "$C2")" == Rejected ]]; }
wait_until 60 "C2: order terminal" terminal_c2
evidence C2 "$C2"
signals="$(sql OrderDb "SELECT COUNT(*) FROM orders.PendingSignal WHERE OrderId = '$C2' AND EventName LIKE 'ReviewSignal%'")"
decisions="$(sql OrderDb "SELECT COUNT(*) FROM orders.ReviewDecision WHERE OrderId = '$C2'")"
observed="decision at the deadline: $status $(json_get "$body" code); order $(status_of "$C2"); decisions $decisions, signals $signals; wakes $(wakes "$C2"); history $(history "$C2")"
if [[ "$status" == 200 && "$(status_of "$C2")" == Completed && "$decisions" == 1 && "$signals" == 1 ]] \
   || [[ "$status" == 409 && "$(json_get "$body" code)" == review-expired && "$(history "$C2")" == *",4:Rejected:ReviewExpired" && "$decisions" == 0 && "$signals" == 0 ]]; then
  assert S52-C2 PASS "exactly one of: decision committed and order completed, or 409 review-expired and order Rejected" "$observed"
else
  assert S52-C2 FAIL "decision and outcome agree; never both" "$observed"
fi

# No signal may be left Pending for a terminal instance.
left="$(sql OrderDb "SELECT COUNT(*) FROM orders.PendingSignal s JOIN orders.[Order] o ON o.OrderId = s.OrderId WHERE o.CustomerId LIKE '$PREFIX-%' AND s.State = 'Pending'")"
assert S52-signals "$([[ "$left" == 0 ]] && echo PASS || echo FAIL)" "no signal left Pending" "pending signals of this run: $left"
