#!/usr/bin/env bash
# R20 (S35) a decision accepted while every workflow host is down is applied later; R21 (S35) a decision
# committed just before the order API crashes is delivered. Drill stack with replicas:
#   bash scripts/stack-up.sh --drill --replicas
#   R20-down        order 1 waits for review; both workflow hosts are stopped (graceful); the approval
#                   through the order API answers 200 and commits the decision and its pending signal
#                   while no host runs (the signal cannot be forwarded yet)
#   R20-alerts      while the hosts are down: ReviewSignalBacklog and PendingSignalAging (review) for the decision's
#                   signal, StartRequestBacklogAging for an order accepted meanwhile; all resolved after the restart,
#                   and that order completes
#   R20-resumed     after the hosts start again the signal is Forwarded, the workflow reads the decision
#                   from SQL, and the order completes; one decision, one signal
#   R21-crashed     order 2: the failpoint AfterReviewDecisionCommitBeforeSignalDispatch (FailFast) ends the
#                   order API replica that took the approval after the decision, its audit, and its signal
#                   committed; the client gets no answer
#   R21-delivered   after the replica restarts (any replica's dispatcher may forward it) the signal is
#                   Forwarded once and the order completes; one decision, one transition out of review
DRILL_ID=R20
. "$(dirname "$0")/lib.sh"
. "$REPO_ROOT/scripts/scenarios/m3-lib.sh"
. "$REPO_ROOT/scripts/scenarios/replica-lib.sh"
drill_preflight
require_replicas
HOSTS=(workflow-host workflow-host-2)
APIS=(order-api order-api-2)
state_of() { docker compose --profile replicas ps -a --format '{{.State}}' "$1" 2>/dev/null; }
restart_down() { local s; for s in "$@"; do [[ "$(state_of "$s")" == running ]] || start_pair "$s"; done; }
trap 'restart_down "${HOSTS[@]}" "${APIS[@]}"; drill_finish; exit $?' EXIT
RUN="r20-$(date -u +%H%M%S)-$((RANDOM % 1000))"
review_of() { sql OrderDb "SELECT ReviewId FROM orders.ReviewTask WHERE OrderId = '$1' AND Round = 1 AND Status = 'Pending'"; }
has_review() { [[ -n "$(sqln OrderDb "SELECT ReviewId FROM orders.ReviewTask WHERE OrderId = '$1' AND Status = 'Pending'")" ]]; }
signal_of() { sqln OrderDb "SELECT CONCAT(s.State, '|', s.Attempts) FROM orders.PendingSignal s JOIN orders.ReviewDecision d ON d.DecisionId = s.CausedBy WHERE d.OrderId = '$1'"; }

# --- R20 -----------------------------------------------------------------------------------------------
O1="$(submit "$ORDER_API" "$RUN-1" "$(review_order_body "$RUN-c1")")"
wait_until 60 "order 1 review" has_review "$O1"
ALERT_SINCE="$(date +%s)"; RS_BEFORE="$(alert_firing ReviewSignalBacklog)"; PSA_BEFORE="$(alert_firing PendingSignalAging 'kind="review"')"; SR_BEFORE="$(alert_firing StartRequestBacklogAging)"
docker compose --profile replicas stop -t 10 "${HOSTS[@]}" >/dev/null 2>&1
INJECTED_AT="$(now_iso)"
hosts="$(for s in "${HOSTS[@]}"; do printf '%s=%s ' "$s" "$(state_of "$s")"; done)"
read -r s1 b1 <<< "$(decide "$ORDER_API_2" "$(review_of "$O1")" approve)"
sleep 3
committed="$(sql OrderDb "SELECT CONCAT((SELECT COUNT(*) FROM orders.ReviewDecision WHERE OrderId = '$O1'), '|', (SELECT Status FROM orders.ReviewTask WHERE OrderId = '$O1' AND Round = 1), '|', (SELECT Status FROM orders.[Order] WHERE OrderId = '$O1'))")"
sig_down="$(signal_of "$O1")"
aexpect R20-down "hosts exited; approval 200; decision committed (1|Approved|AwaitingApproval); signal not Forwarded while no host runs" \
  "hosts $hosts; approval $s1; decision|round|order $committed; signal $sig_down" \
  "$([[ "$hosts" == *workflow-host=exited*workflow-host-2=exited* && "$s1" == 200 && "$committed" == 1\|Approved\|AwaitingApproval && "$sig_down" != Forwarded* ]]; echo $?)"
# An order accepted while no host runs: its start request waits. The hosts stay down until the alerts fired.
O0="$(submit "$ORDER_API" "$RUN-0" "$(order_body "$RUN-c0" "$(fresh_sku R20 5)" 1)")"
alert_wait_firing ReviewSignalBacklog '' 90 || true
alert_wait_firing StartRequestBacklogAging '' 60 || true
restart_down "${HOSTS[@]}"
wait_status 240 "$O1" 'Completed|Rejected|Compensated'
aexpect R20-resumed "signal Forwarded; Completed/Delivered; one decision" \
  "signal $(signal_of "$O1"); end $(end_of "$O1"); decisions $(sql OrderDb "SELECT COUNT(*) FROM orders.ReviewDecision WHERE OrderId = '$O1'"); wakes $(sqln OrderDb "SELECT STRING_AGG(CONCAT(w.Wake, ':', w.SeenStatus), ',') WITHIN GROUP (ORDER BY w.Id) FROM orders.ReviewWake w JOIN orders.ReviewTask r ON r.ReviewId = w.ReviewId WHERE r.OrderId = '$O1'")" \
  "$([[ "$(signal_of "$O1")" == Forwarded* && "$(end_of "$O1")" == Completed/Delivered && "$(sql OrderDb "SELECT COUNT(*) FROM orders.ReviewDecision WHERE OrderId = '$O1'")" == 1 ]]; echo $?)"

# --- R21 -----------------------------------------------------------------------------------------------
O2="$(submit "$ORDER_API" "$RUN-2" "$(review_order_body "$RUN-c2")")"
wait_until 60 "order 2 review" has_review "$O2"
DECISION="$(new_uuid)"
arm_failpoint OrderDb AfterReviewDecisionCommitBeforeSignalDispatch "$(printf '%s' "$DECISION" | tr -d '-')" FailFast
read -r s2 b2 <<< "$(decide "$ORDER_API" "$(review_of "$O2")" approve "$DECISION")"
dead=""; for s in "${APIS[@]}"; do [[ "$(state_of "$s")" != running ]] && dead="$s"; done
fired="$(failpoint_fired_at OrderDb AfterReviewDecisionCommitBeforeSignalDispatch)"
rows="$(sql OrderDb "SELECT CONCAT((SELECT COUNT(*) FROM orders.ReviewDecision WHERE DecisionId = '$DECISION'), '|', (SELECT COUNT(*) FROM orders.PendingSignal WHERE CausedBy = '$DECISION'))")"
FAULT_OBSERVED="R20: hosts stopped; R21: FiredAt $fired, exited ${dead:-none}"
aexpect R21-crashed "FiredAt set; order-api exited; no answer to the client; decision and signal committed (1|1)" \
  "FiredAt '$fired'; exited '${dead:-none}'; client status '${s2:-none}'; decision|signal rows $rows" \
  "$([[ -n "$fired" && "$dead" == order-api && "$s2" != 200 && "$rows" == 1\|1 ]]; echo $?)"
restart_down "${APIS[@]}"
wait_status 240 "$O2" 'Completed|Rejected|Compensated'
RECOVERED_AT="$(now_iso)"
transitions="$(sql OrderDb "SELECT COUNT(*) FROM orders.OrderStatusHistory WHERE OrderId = '$O2' AND ToStatus = 'Reserving'")"
aexpect R21-delivered "signal Forwarded once; Completed/Delivered; one decision; one transition to Reserving" \
  "signal $(signal_of "$O2"); end $(end_of "$O2"); decisions $(sql OrderDb "SELECT COUNT(*) FROM orders.ReviewDecision WHERE OrderId = '$O2'"); to Reserving $transitions" \
  "$([[ "$(signal_of "$O2")" == Forwarded* && "$(end_of "$O2")" == Completed/Delivered && "$(sql OrderDb "SELECT COUNT(*) FROM orders.ReviewDecision WHERE OrderId = '$O2'")" == 1 && "$transitions" == 1 ]]; echo $?)"
wait_status 240 "$O0" 'Completed|Rejected|Compensated' || true
read -r v1 d1 <<< "$(alert_outcome ReviewSignalBacklog '' "$ALERT_SINCE" "$RS_BEFORE" 120)"
read -r v2 d2 <<< "$(alert_outcome PendingSignalAging 'kind="review"' "$ALERT_SINCE" "$PSA_BEFORE" 60)"
read -r v3 d3 <<< "$(alert_outcome StartRequestBacklogAging '' "$ALERT_SINCE" "$SR_BEFORE" 60)"
all="$v1 $v2 $v3"
assert R20-alerts "$([[ "$all" == "PASS PASS PASS" && "$(end_of "$O0")" == Completed/Delivered ]] && echo PASS || { [[ "$all" == *UNVERIFIED* ]] && echo UNVERIFIED || echo FAIL; })" \
  "ReviewSignalBacklog, PendingSignalAging (review), StartRequestBacklogAging fire while the hosts are down and resolve; the order accepted meanwhile completes" \
  "$v1 $d1; $v2 $d2; $v3 $d3; order accepted during the outage $(end_of "$O0")"
