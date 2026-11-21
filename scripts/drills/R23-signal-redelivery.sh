#!/usr/bin/env bash
# R23 (signal part; S34, S10) a redelivered signal is a no-op: the workflow re-reads SQL and progresses once.
# Drill stack: bash scripts/stack-up.sh --drill --replicas
# An order needing a review is approved; once its signal was delivered and the order moved on, a drill control
# resets the signal row to Pending, so the dispatcher delivers the same signal again.
#   R23-delivered    the approval's signal Forwarded and the order past AwaitingApproval before the reset
#   R23-redelivered  after the reset the dispatcher raised the signal again (another attempt; the row ends
#                    Forwarded or Discarded)
#   R23-once         one decision, one AwaitingApproval -> Reserving transition, the order Completed/Delivered with
#                    one OrderCompleted event; the review was resolved once
DRILL_ID=R23S
. "$(dirname "$0")/lib.sh"
. "$REPO_ROOT/scripts/scenarios/m3-lib.sh"
drill_preflight
trap 'drill_finish; exit $?' EXIT
RUN="r23s-$(date -u +%H%M%S)-$((RANDOM % 1000))"
REVIEW_SKU="$(review_sku)"

o="$(submit "$ORDER_API" "$RUN" "$(order_body "$RUN-c" "$REVIEW_SKU" 100)")"
echo "$o" > "$DRILL_DIR/order.txt"
has_review() { [[ -n "$(sql OrderDb "SELECT ReviewId FROM orders.ReviewTask WHERE OrderId = '$o' AND Status = 'Pending'")" ]]; }
wait_until 60 "review" has_review
rid="$(sql OrderDb "SELECT ReviewId FROM orders.ReviewTask WHERE OrderId = '$o' AND Status = 'Pending'")"
decide "$ORDER_API" "$rid" approve > /dev/null
signal() { sql OrderDb "SELECT CONCAT(State, '|', Attempts) FROM orders.PendingSignal WHERE OrderId = '$o' AND EventName LIKE 'ReviewSignal%'"; }
moved_on() { [[ "$(signal)" == Forwarded\|* ]] && ! status_is "$o" 'AwaitingApproval|Validating|Submitted'; }
wait_until 90 "signal delivered, order moved on" moved_on
before="$(signal)"; status_before="$(order_status "$o")"
aexpect R23-delivered "signal Forwarded; order past AwaitingApproval" "signal $before; order $status_before" "$(moved_on; echo $?)"

INJECTED_AT="$(now_iso)"
# Drill control (the table has filtered indexes, so the session needs QUOTED_IDENTIFIER ON).
reset="$(sql OrderDb "SET QUOTED_IDENTIFIER ON; UPDATE orders.PendingSignal SET State = 'Pending', ForwardedAt = NULL, ClosedAt = NULL, LastErrorCode = NULL,
  NextAttemptAt = SYSUTCDATETIME() WHERE OrderId = '$o' AND EventName LIKE 'ReviewSignal%'; SELECT @@ROWCOUNT")"
[[ "$reset" == 1 ]] || log "signal reset did not apply: $reset"
again() { local s; s="$(signal)"; [[ "$s" =~ ^(Forwarded|Discarded)\|([0-9]+)$ && ${BASH_REMATCH[2]} -gt ${before#*|} ]]; }
wait_until 90 "signal raised again" again || true
after="$(signal)"
FAULT_OBSERVED="signal $before -> reset -> $after"
aexpect R23-redelivered "signal raised again (attempts rose), ends Forwarded or Discarded" "signal $before -> $after" "$(again; echo $?)"

wait_status 180 "$o" 'Completed|Compensated|Rejected|ManualInterventionRequired'
RECOVERED_AT="$(now_iso)"
decisions="$(sql OrderDb "SELECT COUNT(*) FROM orders.ReviewDecision d JOIN orders.ReviewTask r ON r.ReviewId = d.ReviewId WHERE r.OrderId = '$o'")"
moves="$(sql OrderDb "SELECT COUNT(*) FROM orders.OrderStatusHistory WHERE OrderId = '$o' AND FromStatus = 'AwaitingApproval' AND ToStatus = 'Reserving'")"
aexpect R23-once "1 decision, 1 AwaitingApproval->Reserving, Completed/Delivered, 1 OrderCompleted" \
  "decisions $decisions; transitions $moves; end $(end_of "$o"); OrderCompleted $(completed_events "$o"); history $(history_of "$o")" \
  "$([[ "$decisions" == 1 && "$moves" == 1 && "$(end_of "$o")" == Completed/Delivered && "$(completed_events "$o")" == 1 ]]; echo $?)"
