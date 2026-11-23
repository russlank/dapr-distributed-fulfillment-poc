#!/usr/bin/env bash
# R07 (S21) a workflow state-store outage loses no history and produces no false success.
# Drill stack: bash scripts/stack-up.sh --drill --replicas
# Only the sidecars' path to DaprStateDb is cut (Toxiproxy proxy dapr-state-sql disabled); SQL Server, OrderDb, and
# every other database stay up. Before the cut one order is in flight and one waits for its review; during the cut a
# new order is accepted and the waiting order's review is approved. The outage lasts until StartRequestBacklogAging
# fired (at least 40 s), with order statuses sampled every 5 s.
#   R07-cut         the proxy is disabled; sidecars report state-store errors
#   R07-held        during the outage no order completes (no false success): the new order stays Submitted (its start
#                   request Pending), the approved order keeps its committed decision (still AwaitingApproval), and no
#                   order's status moves past what its committed steps allow
#   R07-recovered   after the restore every order completes (Completed/Delivered), its workflow Completed (history
#                   intact, no non-determinism or failure), start request Closed, one OrderCompleted event each
#   R07-once        no effect repeated: per order one provider authorization and one capture, holds Consumed once,
#                   one decision for the approved order
#   R07-alert       StartRequestBacklogAging fired for the order accepted during the outage and resolved after it
DRILL_ID=R07
. "$(dirname "$0")/lib.sh"
. "$REPO_ROOT/scripts/scenarios/m3-lib.sh"
drill_preflight
PROXY=dapr-state-sql
proxy_enabled() { curl -s -m 10 "$TOXIPROXY/proxies/$PROXY" | python -c 'import json,sys; print(str(json.load(sys.stdin)["enabled"]).lower())'; }
set_proxy() { curl -s -m 10 -o /dev/null -w '%{http_code}' -X POST "$TOXIPROXY/proxies/$PROXY" -H 'content-type: application/json' -d '{"enabled":'"$1"'}'; }
on_cleanup "set_proxy true >/dev/null"
trap 'run_cleanup; drill_finish; exit $?' EXIT
RUN="r07-$(date -u +%H%M%S)-$((RANDOM % 1000))"
SKU="$(fresh_sku R07 20)"
review_of() { sql OrderDb "SELECT ReviewId FROM orders.ReviewTask WHERE OrderId = '$1' AND Round = 1 AND Status = 'Pending'"; }
has_review() { [[ -n "$(review_of "$1")" ]]; }

W="$(submit "$ORDER_API" "$RUN-w" "$(review_order_body "$RUN-cw")")"
wait_until 60 "order W waits for its review" has_review "$W"
M="$(submit "$ORDER_API" "$RUN-m" "$(order_body "$RUN-cm" "$SKU" 1)")"
in_flight() { status_is "$M" 'Reserving|PaymentAuthorized|Packing|Dispatched|AwaitingDelivery'; }
wait_until 60 "order M in flight" in_flight || true
m_at_cut="$(order_status "$M")"

ALERT_SINCE="$(date +%s)"; SR_BEFORE="$(alert_firing StartRequestBacklogAging)"
cut_code="$(set_proxy false)"
INJECTED_AT="$(now_iso)"
N="$(submit "$ORDER_API" "$RUN-n" "$(order_body "$RUN-cn" "$SKU" 1)")"
read -r approve _ <<< "$(decide "$ORDER_API" "$(review_of "$W")" approve)"
printf 'W %s\nM %s\nN %s\n' "$W" "$M" "$N" > "$DRILL_DIR/orders.txt"
: > "$DRILL_DIR/outage-samples.txt"
held_ok=0; completed_during=0; start=$SECONDS
sample() {
  local line; line="$(sql OrderDb "SELECT CONCAT((SELECT Status FROM orders.[Order] WHERE OrderId = '$W'), '|', (SELECT Status FROM orders.[Order] WHERE OrderId = '$M'), '|',
    (SELECT Status FROM orders.[Order] WHERE OrderId = '$N'), '|', (SELECT State FROM orders.WorkflowStartRequest WHERE OrderId = '$N'))")"
  echo "$(date -u +%H:%M:%S) W|M|N|N-start $line" >> "$DRILL_DIR/outage-samples.txt"
  [[ "$line" == *Completed* ]] && completed_during=1
  return 0
}
while (( SECONDS - start < 40 )); do sample; sleep 5; done
alert_wait_firing StartRequestBacklogAging '' 60 || true
sample
during="$(tail -1 "$DRILL_DIR/outage-samples.txt")"
errors="$(docker compose --profile replicas logs --since 5m workflow-host-dapr workflow-host-2-dapr order-api-dapr 2>/dev/null | grep -ciE 'state store|sqlserver|connection refused|dial tcp|i/o timeout|failed to save|actor')"
decision="$(sql OrderDb "SELECT CONCAT(COUNT(*), '|', MAX(Outcome)) FROM orders.ReviewDecision d JOIN orders.ReviewTask r ON r.ReviewId = d.ReviewId WHERE r.OrderId = '$W'")"
FAULT_OBSERVED="proxy $PROXY disabled ($cut_code, enabled now $(proxy_enabled)); M at the cut $m_at_cut; at the end of the outage W|M|N|N-start ${during#* * }; sidecar state errors $errors"
aexpect R07-cut "proxy disabled (204), sidecars report state-store errors" "set $cut_code, enabled $(proxy_enabled); sidecar error lines $errors" \
  "$([[ "$cut_code" == 20* && "$(proxy_enabled)" == false && "$errors" -gt 0 ]]; echo $?)"
n_line="${during##* }"
IFS='|' read -r w_st m_st n_st n_start <<< "$n_line"
aexpect R07-held "no order completed during the outage; N Submitted with its start request Pending; W AwaitingApproval with its decision (1|Approved) committed" \
  "samples in outage-samples.txt; last W|M|N|N-start $n_line; approval answered $approve; decision $decision" \
  "$([[ "$completed_during" == 0 && "$n_st" == Submitted && "$n_start" == Pending && "$w_st" == AwaitingApproval && "$approve" == 200 && "$decision" == "1|Approved" ]]; echo $?)"

restore_code="$(set_proxy true)"
ends=""; bad=0
for o in "$W" "$M" "$N"; do
  wait_status 240 "$o" 'Completed|Compensated|Rejected|ManualInterventionRequired' || true
  e="$(end_of "$o")"; wf="$(workflow_of "$o")"; st="$(sql OrderDb "SELECT State FROM orders.WorkflowStartRequest WHERE OrderId = '$o'")"
  ends+="$e wf $wf start $st OrderCompleted $(completed_events "$o"); "
  [[ "$e" == Completed/Delivered && "$wf" == Completed/* && "$st" == Closed && "$(completed_events "$o")" == 1 ]] || bad=1
done
RECOVERED_AT="$(now_iso)"
aexpect R07-recovered "restore $restore_code; every order Completed/Delivered, workflow Completed, start request Closed, one OrderCompleted" "$ends" "$bad"

once=""; bad=0
for o in "$W" "$M" "$N"; do
  money="$(provider_money "$o")"; holds="$(holds_of "$o")"; once+="$money holds $holds; "
  [[ "$money" == "1|1|0|0" && "$holds" =~ ^Consumed(,Consumed)*$ ]] || bad=1
done
aexpect R07-once "per order one authorization and one capture (1|1|0|0), holds Consumed; one decision for W" "$once decisions $decision" \
  "$([[ $bad == 0 && "$(sql OrderDb "SELECT COUNT(*) FROM orders.ReviewDecision d JOIN orders.ReviewTask r ON r.ReviewId = d.ReviewId WHERE r.OrderId = '$W'")" == 1 ]]; echo $?)"

read -r verdict detail <<< "$(alert_outcome StartRequestBacklogAging '' "$ALERT_SINCE" "$SR_BEFORE" 120)"
assert R07-alert "$verdict" "StartRequestBacklogAging fires for the order accepted during the outage and resolves after the restore" "$detail"
