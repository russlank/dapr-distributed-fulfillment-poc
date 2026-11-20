#!/usr/bin/env bash
# S09 cross-replica approval. Runs on a stack with both order API replicas (release or drill):
#   bash scripts/stack-up.sh --replicas
# For N orders that require a review (order value from 1,000.00), each accepted on one order API
# replica, the workflow must wait durably; the approval is submitted through the OTHER replica; the
# signal dispatcher (any replica) wakes the workflow, which reads the decision from SQL and finishes.
# PASS when, from OrderDb rows, the workflow host's view, and the attempt logs:
#   S09-waiting   every order is AwaitingApproval with one Pending review round, its workflow is
#                 Running, and the workflow has resolved the review once (Initial: Pending) and not
#                 yet again: it waits
#   S09-decision  the approval through the other replica returned 200; one immutable decision with
#                 the operator id, the review Approved, and one pending signal, all committed
#   S09-resumed   the workflow woke by the signal and re-read SQL (wake rows Initial:Pending then
#                 Signal:Approved); the signal is Forwarded only after a raise to a running instance
#   S09-completed every order Completed with history Submitted, Validating, AwaitingApproval,
#                 Reserving, PaymentAuthorized, Packing, Dispatched, AwaitingDelivery, Completed (one terminal transition), one effect per operation key, start
#                 request Closed, workflow Completed, its state under workflow-host only
# Evidence in artifacts/scenarios/S09-<time>/ (execution views with signal attempts and timings).
. "$(dirname "$0")/../lib.sh"

RUN_ID="S09-$(date -u +%Y%m%dT%H%M%SZ)"
OUT_DIR="$REPO_ROOT/artifacts/scenarios/$RUN_ID"
mkdir -p "$OUT_DIR"
RESULTS_FILE="$OUT_DIR/results.jsonl"
N="${S09_ORDERS:-3}"
PREFIX="s09-$(date -u +%H%M%S)-$RANDOM"
failures=0
check() { [[ "$2" == PASS ]] || failures=$((failures + 1)); record "$@"; }

[[ "$(docker compose --profile replicas ps --format '{{.State}}' order-api-2 2>/dev/null)" == running ]] && ready order-api-2 \
  || { log "REFUSED: order-api-2 is not running and ready (start the stack with --replicas)"; exit 2; }
log "S09 $RUN_ID: $N orders, evidence in $OUT_DIR"

declare -A ORDER_OF SUBMITTED_ON DECIDED_ON
for i in $(seq 1 "$N"); do
  base="$ORDER_API"; other="$ORDER_API_2"; (( i % 2 == 0 )) && { base="$ORDER_API_2"; other="$ORDER_API"; }
  out="$(curl -s -X POST "$base/orders" -H "X-Api-Key: $CUSTOMER_API_KEY" -H "Idempotency-Key: $PREFIX-$i" \
    -H 'content-type: application/json' -d "$(review_order_body "$PREFIX-$i")")"
  ORDER_OF[$i]="$(json_get "$out" orderId)"; SUBMITTED_ON[$i]="$base"; DECIDED_ON[$i]="$other"
  echo "order $i ${ORDER_OF[$i]} submitted on $base, to be approved on $other" >> "$OUT_DIR/orders.txt"
done
run_orders="SELECT OrderId FROM orders.[Order] WHERE CustomerId LIKE '$PREFIX-%'"

# S09-waiting -------------------------------------------------------------------------------------
all_waiting() { [[ "$(sql OrderDb "SELECT COUNT(*) FROM orders.[Order] o WHERE o.OrderId IN ($run_orders) AND o.Status = 'AwaitingApproval'
  AND EXISTS (SELECT 1 FROM orders.ReviewWake w JOIN orders.ReviewTask r ON r.ReviewId = w.ReviewId WHERE r.OrderId = o.OrderId)")" == "$N" ]]; }
wait_until 90 "all $N orders waiting for their review" all_waiting
sleep 3   # a waiting workflow does nothing more: the wake log must not grow
waiting_ok=0; details=()
for i in $(seq 1 "$N"); do
  o="${ORDER_OF[$i]}"
  e="$(admin_api GET "/system/orders/$o/execution")"; echo "$e" > "$OUT_DIR/waiting-$i.json"
  row="$(sql OrderDb "SELECT CONCAT((SELECT COUNT(*) FROM orders.ReviewTask WHERE OrderId = '$o' AND Status = 'Pending'), '|',
    (SELECT STRING_AGG(CONCAT(w.Wake, ':', w.SeenStatus), ',') WITHIN GROUP (ORDER BY w.Id) FROM orders.ReviewWake w JOIN orders.ReviewTask r ON r.ReviewId = w.ReviewId WHERE r.OrderId = '$o'), '|',
    (SELECT Status FROM orders.[Order] WHERE OrderId = '$o'))")"
  if [[ "$row" == "1|Initial:Pending|AwaitingApproval" && "$(json_get "$e" workflow.status)" == Running ]]; then
    waiting_ok=$((waiting_ok + 1))
  else
    details+=("$o: $row workflow=$(json_get "$e" workflow.status)")
  fi
done
if [[ "$waiting_ok" == "$N" ]]; then
  check S09-waiting PASS "AwaitingApproval, one Pending round, workflow Running, resolved once (Initial:Pending) and waiting" "$waiting_ok/$N waiting"
else
  check S09-waiting FAIL "$N/$N waiting" "$waiting_ok/$N; ${details[*]:0:3}"
fi

# S09-decision ------------------------------------------------------------------------------------
decision_ok=0; details=()
for i in $(seq 1 "$N"); do
  o="${ORDER_OF[$i]}"
  review="$(sql OrderDb "SELECT ReviewId FROM orders.ReviewTask WHERE OrderId = '$o' AND Round = 1")"
  read -r status body <<< "$(decide "${DECIDED_ON[$i]}" "$review" approve "" operator-2)"
  echo "decision $i via ${DECIDED_ON[$i]}: $status $body" >> "$OUT_DIR/decisions.txt"
  row="$(sql OrderDb "SELECT CONCAT((SELECT Status FROM orders.ReviewTask WHERE ReviewId = '$review'), '|',
    (SELECT CONCAT(COUNT(*), ':', MAX(OperatorId)) FROM orders.ReviewDecision WHERE ReviewId = '$review'), '|',
    (SELECT COUNT(*) FROM orders.PendingSignal WHERE OrderId = '$o' AND EventName = 'ReviewSignal_' + REPLACE(LOWER(CONVERT(varchar(36), CAST('$review' AS uniqueidentifier))), '-', '')))")"
  if [[ "$status" == 200 && "$(json_get "$body" outcome)" == Approved && "$row" == "Approved|1:operator-2|1" ]]; then
    decision_ok=$((decision_ok + 1))
  else
    details+=("$o: $status $body rows=$row")
  fi
done
if [[ "$decision_ok" == "$N" ]]; then
  check S09-decision PASS "approval via the other replica: 200; review Approved, one decision (operator-2), one pending signal" "$decision_ok/$N"
else
  check S09-decision FAIL "$N/$N" "$decision_ok/$N; ${details[*]:0:3}"
fi

# S09-resumed and S09-completed -------------------------------------------------------------------
all_completed() { [[ "$(sql OrderDb "SELECT COUNT(*) FROM orders.[Order] WHERE OrderId IN ($run_orders) AND Status = 'Completed'")" == "$N" ]]; }
wait_until 120 "all $N orders Completed" all_completed
resumed_ok=0; completed_ok=0; r_details=(); c_details=()
for i in $(seq 1 "$N"); do
  o="${ORDER_OF[$i]}"; normalized="$(echo "$o" | tr -d '-' | tr 'A-F' 'a-f')"
  e="$(admin_api GET "/system/orders/$o/execution")"; echo "$e" > "$OUT_DIR/completed-$i.json"
  wakes="$(sql OrderDb "SELECT STRING_AGG(CONCAT(w.Wake, ':', w.SeenStatus), ',') WITHIN GROUP (ORDER BY w.Id) FROM orders.ReviewWake w JOIN orders.ReviewTask r ON r.ReviewId = w.ReviewId WHERE r.OrderId = '$o'")"
  signal="$(sql OrderDb "SELECT CONCAT(s.State, '|', s.Attempts, '|',
    (SELECT STRING_AGG(CONCAT(a.Result, '@', LEFT(a.DispatcherId, CHARINDEX('/', a.DispatcherId) - 1), ' http=', a.HttpStatus, ' calls=', a.InvocationAttempts), ',') WITHIN GROUP (ORDER BY a.Id) FROM orders.SignalAttempt a WHERE a.SignalId = s.SignalId), '|',
    DATEDIFF(millisecond, d.DecidedAt, s.ForwardedAt), '|',
    DATEDIFF(millisecond, s.ForwardedAt, (SELECT OccurredAt FROM orders.OrderStatusHistory WHERE OrderId = s.OrderId AND ToStatus = 'Completed')))
    FROM orders.PendingSignal s JOIN orders.ReviewDecision d ON d.DecisionId = s.CausedBy WHERE s.OrderId = '$o'")"
  IFS='|' read -r s_state s_attempts s_log decided_to_forwarded forwarded_to_completed <<< "$signal"
  echo "order $i $o: wakes $wakes; signal $s_state attempts=$s_attempts [$s_log]; decision->forwarded ${decided_to_forwarded} ms; forwarded->completed ${forwarded_to_completed} ms" >> "$OUT_DIR/timings.txt"
  if [[ "$wakes" == "Initial:Pending,Signal:Approved" && "$s_state" == Forwarded && "$s_log" == *Forwarded* ]]; then
    resumed_ok=$((resumed_ok + 1))
  else
    r_details+=("$o: wakes=$wakes signal=$s_state [$s_log]")
  fi
  row="$(sql OrderDb "SELECT CONCAT(
    (SELECT STRING_AGG(CONCAT(Sequence, ':', ToStatus), ',') WITHIN GROUP (ORDER BY Sequence) FROM orders.OrderStatusHistory WHERE OrderId = '$o'), '|',
    (SELECT STRING_AGG(SUBSTRING(OperationKey, 34, 100), ',') WITHIN GROUP (ORDER BY OperationKey) FROM orders.StubEffect WHERE OrderId = '$o'), '|',
    (SELECT CONCAT(State, ':', ISNULL(LastErrorCode, '-')) FROM orders.WorkflowStartRequest WHERE OrderId = '$o'), '|',
    (SELECT COUNT(*) FROM orders.ReviewDecision WHERE OrderId = '$o'))")"
  host_rows="$(sql DaprStateDb "SELECT COUNT(*) FROM dbo.state WHERE [Key] LIKE 'workflow-host||%||fulfill-$normalized||%'")"
  own_rows="$(sql DaprStateDb "SELECT COUNT(*) FROM dbo.state WHERE [Key] LIKE 'order-api||%||fulfill-$normalized||%'")"
  if [[ "$row" == "1:Submitted,2:Validating,3:AwaitingApproval,4:Reserving,5:PaymentAuthorized,6:Packing,7:Dispatched,8:AwaitingDelivery,9:Completed|stub-fulfil:line-1|Closed:-|1" \
        && "$(json_get "$e" workflow.status)" == Completed && "$(json_get "$e" workflow.output.outcome)" == Completed && "${host_rows:-0}" -gt 0 && "$own_rows" == 0 ]]; then
    completed_ok=$((completed_ok + 1))
  else
    c_details+=("$o: $row workflow=$(json_get "$e" workflow.status)/$(json_get "$e" workflow.output.outcome) rows=$host_rows/$own_rows")
  fi
done
timing="$(tr '\n' ' ' < "$OUT_DIR/timings.txt")"
if [[ "$resumed_ok" == "$N" ]]; then
  check S09-resumed PASS "woken by the signal, re-read SQL (Initial:Pending, Signal:Approved); signal Forwarded after a raise" "$resumed_ok/$N; $timing"
else
  check S09-resumed FAIL "$N/$N" "$resumed_ok/$N; ${r_details[*]:0:3}"
fi
if [[ "$completed_ok" == "$N" ]]; then
  check S09-completed PASS "Completed once (9 history rows), one effect per key, request Closed, workflow Completed on workflow-host only" "$completed_ok/$N"
else
  check S09-completed FAIL "$N/$N" "$completed_ok/$N; ${c_details[*]:0:3}"
fi

log "S09 finished: $failures failed check(s); results in $RESULTS_FILE"
(( failures == 0 ))
