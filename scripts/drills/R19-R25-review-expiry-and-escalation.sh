#!/usr/bin/env bash
# R19 (S32) an ignored review expires and escalates per policy; R25 (S32, S36) an old round's decision
# cannot approve a new round. Drill stack: bash scripts/stack-up.sh --drill --replicas
# (drill review policy: 2 rounds, 20 s deadline each, timer grace 2 s; SQL time decides.)
#   R19-expired      order 1 is ignored: round 1 Expired, round 2 opened after it and Expired, each
#                    ExpiredAt at or after its DeadlineAt (SQL timestamps); the order Rejected
#                    (ReviewExpired); no hold, no provider operation, no decision
#   R25-escalated    order 2: an operator escalates round 1 before its deadline (client simulator):
#                    round 1 Escalated (ExpiredAt before DeadlineAt), round 2 Pending
#   R25-superseded   a decision for round 1 afterwards: 409 review-superseded; round 2 untouched (Pending,
#                    no decision); escalating round 2: 409 no-further-round
#   R25-completed    approving round 2 completes the order through payment and delivery; one decision
DRILL_ID=R19
. "$(dirname "$0")/lib.sh"
. "$REPO_ROOT/scripts/scenarios/m3-lib.sh"
. "$REPO_ROOT/scripts/scenarios/replica-lib.sh"
OUT_DIR="$DRILL_DIR"
drill_preflight
client_sim identity > "$DRILL_DIR/client-identity.json" || { log "REFUSED: the client simulator does not build"; exit 2; }
CLIENT_SIM_BUILT=1
trap 'drill_finish; exit $?' EXIT
RUN="r19-$(date -u +%H%M%S)-$((RANDOM % 1000))"
rounds_of() { sqln OrderDb "SELECT STRING_AGG(CONCAT(Round, ':', Status), ',') WITHIN GROUP (ORDER BY Round) FROM orders.ReviewTask WHERE OrderId = '$1'"; }

INJECTED_AT="$(now_iso)"
O1="$(submit "$ORDER_API" "$RUN-1" "$(review_order_body "$RUN-c1")")"
O2="$(submit "$ORDER_API_2" "$RUN-2" "$(review_order_body "$RUN-c2")")"
echo "ignored $O1; escalated $O2" > "$DRILL_DIR/orders.txt"
wait_status 90 "$O2" AwaitingApproval
round_pending() { [[ "$(rounds_of "$O2")" == "1:Pending" ]]; }
wait_until 30 "order 2 round 1 pending" round_pending

# R25: escalate round 1 of order 2 before its deadline.
c="$(sim escalate escalate --order "$O2" --operator operator-2 --reason "senior decision needed" --url "$ORDER_API")"
r1="$(sql OrderDb "SELECT ReviewId FROM orders.ReviewTask WHERE OrderId = '$O2' AND Round = 1")"
timing="$(sql OrderDb "SELECT CASE WHEN ExpiredAt < DeadlineAt THEN 'before-deadline' ELSE 'after-deadline' END FROM orders.ReviewTask WHERE ReviewId = '$r1'")"
aexpect R25-escalated "escalate exit 0 (escalated); round 1 Escalated before its deadline; round 2 Pending" \
  "exit $c result $(jq_py escalate "r['items'][0]['result']"); rounds $(rounds_of "$O2"); escalated $timing" \
  "$([[ "$c" == 0 && "$(jq_py escalate "r['items'][0]['result']")" == escalated && "$(rounds_of "$O2")" == "1:Escalated,2:Pending" && "$timing" == before-deadline ]]; echo $?)"

read -r ds dbody <<< "$(decide "$ORDER_API_2" "$r1" approve)"
r2="$(sql OrderDb "SELECT ReviewId FROM orders.ReviewTask WHERE OrderId = '$O2' AND Round = 2")"
c2="$(sim escalate-2 escalate --review "$r2" --url "$ORDER_API_2")"
untouched="$(sql OrderDb "SELECT CONCAT(Status, '|', (SELECT COUNT(*) FROM orders.ReviewDecision WHERE ReviewId = '$r2')) FROM orders.ReviewTask WHERE ReviewId = '$r2'")"
aexpect R25-superseded "round 1 decision 409 review-superseded; round 2 Pending with no decision; escalating round 2 refused no-further-round" \
  "decision $ds $(json_get "$dbody" code); round 2 $untouched; escalate round 2 exit $c2 code $(jq_py escalate-2 "r['items'][0].get('code')")" \
  "$([[ "$ds" == 409 && "$(json_get "$dbody" code)" == review-superseded && "$untouched" == Pending\|0 && "$(jq_py escalate-2 "r['items'][0].get('code')")" == no-further-round ]]; echo $?)"

ca="$(sim approve approve --order "$O2" --url "$ORDER_API" --decision-seed "$RUN")"
wait_status 180 "$O2" 'Completed|Rejected|Compensated'
aexpect R25-completed "round 2 approved; Completed/Delivered; one decision (round 2); authorized and captured once" \
  "approve exit $ca; rounds $(rounds_of "$O2"); end $(end_of "$O2"); decisions $(sql OrderDb "SELECT COUNT(*) FROM orders.ReviewDecision WHERE OrderId = '$O2'"); provider a|c|v|r $(provider_money "$O2")" \
  "$([[ "$ca" == 0 && "$(rounds_of "$O2")" == "1:Escalated,2:Approved" && "$(end_of "$O2")" == Completed/Delivered && "$(sql OrderDb "SELECT COUNT(*) FROM orders.ReviewDecision WHERE OrderId = '$O2'")" == 1 && "$(provider_money "$O2")" == 1\|1\|0\|0 ]]; echo $?)"

# R19: order 1 was never decided.
wait_status 120 "$O1" 'Rejected|Completed|Cancelled'
FAULT_OBSERVED="order 1 ignored for two rounds"
times="$(sqln OrderDb "SELECT STRING_AGG(x.Line, ',') WITHIN GROUP (ORDER BY x.Round) FROM (
  SELECT t.Round, CONCAT(t.Round, ':', t.Status, ':', CASE WHEN t.ExpiredAt >= t.DeadlineAt THEN 'at-or-after' ELSE 'before' END, ':',
    CASE WHEN t.Round = 1 OR t.CreatedAt >= p.ExpiredAt THEN 'after-previous' ELSE 'early' END) AS Line
  FROM orders.ReviewTask t LEFT JOIN orders.ReviewTask p ON p.OrderId = t.OrderId AND p.Round = t.Round - 1 WHERE t.OrderId = '$O1') x")"
RECOVERED_AT="$(now_iso)"
aexpect R19-expired "1:Expired and 2:Expired, each at or after its deadline, round 2 opened after round 1 expired; Rejected/ReviewExpired; no hold, provider operation, or decision" \
  "rounds $times; end $(end_of "$O1"); holds '$(holds_of "$O1")'; provider '$(provider_ops "$O1")'; decisions $(sql OrderDb "SELECT COUNT(*) FROM orders.ReviewDecision WHERE OrderId = '$O1'")" \
  "$([[ "$times" == "1:Expired:at-or-after:after-previous,2:Expired:at-or-after:after-previous" && "$(end_of "$O1")" == Rejected/ReviewExpired && -z "$(holds_of "$O1")" && -z "$(provider_ops "$O1")" && "$(sql OrderDb "SELECT COUNT(*) FROM orders.ReviewDecision WHERE OrderId = '$O1'")" == 0 ]]; echo $?)"
