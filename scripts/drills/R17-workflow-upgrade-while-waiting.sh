#!/usr/bin/env bash
# R17 (S19) workflow instances survive an upgrade of the workflow hosts while they wait: version N to N+1 with a
# patched workflow change (patch execution-version-2: the run records ExecutionVersion 2), rolled one replica at a time.
# Drill stack: bash scripts/stack-up.sh --drill --replicas
# Version N is the same build with the patch held back (WORKFLOW_PATCHES_DISABLED); N+1 enables it.
#   R17-version-n     both workflow hosts run N (patch held back); order 2 waits for its delivery (carrier in Manual
#                     mode) across both restarts, order 3 completes on N without a recorded version; order 1 waits for
#                     its review (20 s rounds) from just before the roll and is approved after the first host's upgrade
#   R17-rolled        workflow-host and then workflow-host-2 restarted with the patch enabled (an order is accepted
#                     between the two restarts); both report the patch enabled
#   R17-old-path      the orders that waited across the upgrade finish on their recorded path: Completed/Delivered,
#                     no ExecutionVersion
#   R17-new-path      the order accepted after the roll runs the patched path (ExecutionVersion 2); the order accepted
#                     during the roll finished on whichever path its run started with (2 or none), Completed/Delivered
#   R17-no-failure    no workflow Failed, no non-determinism error in the workflow hosts' logs; each order one capture
#   R17-rollback      an order started on N+1 (patch recorded) waits for its review while both hosts roll back to N;
#                     after its approval it does not continue on N (Dapr stalls it instead of replaying it differently),
#                     and after the hosts roll forward again it completes with version 2
DRILL_ID=R17
. "$(dirname "$0")/lib.sh"
. "$REPO_ROOT/scripts/scenarios/m3-lib.sh"
drill_preflight
PATCH=execution-version-2
HOSTS=(workflow-host workflow-host-2)
back_to_default() { local h; for h in "${HOSTS[@]}"; do WORKFLOW_PATCHES_DISABLED= restart_pair "$h" >/dev/null 2>&1; done; }
on_cleanup "back_to_default"
trap 'run_cleanup; drill_finish; exit $?' EXIT
RUN="r17-$(date -u +%H%M%S)-$((RANDOM % 1000))"
SKU="$(fresh_sku R17 20)"
patches_of() { http_body "$1" /system/workflow-patches | tr -d '\r\n '; }
version_of() { sqln OrderDb "SELECT ISNULL(CONVERT(varchar(4), ExecutionVersion), 'none') FROM orders.WorkflowStartRequest WHERE OrderId = '$1'"; }
review_of() { sql OrderDb "SELECT ReviewId FROM orders.ReviewTask WHERE OrderId = '$1' AND Round = 1 AND Status = 'Pending'"; }
has_review() { [[ -n "$(review_of "$1")" ]]; }
SCRIPT='[{"status":"PickedUp","version":1,"delayMs":500},{"status":"InTransit","version":2,"delayMs":1000},{"status":"Delivered","version":3,"delayMs":1500}]'

# --- version N --------------------------------------------------------------------------------------------------
for h in "${HOSTS[@]}"; do WORKFLOW_PATCHES_DISABLED=$PATCH restart_pair "$h"; done
n_patches="$(for h in "${HOSTS[@]}"; do printf '%s=%s ' "$h" "$(patches_of "$h")"; done)"
O2="$(submit "$ORDER_API" "$RUN-2" "$(order_body "$RUN-c2" "$SKU" 1)")"; carrier_mode "$O2" Manual > /dev/null
O3="$(submit "$ORDER_API" "$RUN-3" "$(order_body "$RUN-c3" "$SKU" 1)")"
wait_status 120 "$O2" AwaitingDelivery || true
wait_status 180 "$O3" 'Completed|Compensated|Rejected|ManualInterventionRequired' || true
# The review order is accepted last, so that its 20 s rounds outlast the first host's upgrade.
O1="$(submit "$ORDER_API" "$RUN-1" "$(review_order_body "$RUN-c1")")"
wait_until 60 "order 1 waits for its review" has_review "$O1" || true
aexpect R17-version-n "both hosts hold the patch back; O1 AwaitingApproval, O2 AwaitingDelivery, O3 Completed/Delivered without a version" \
  "hosts $n_patches; O1 $(order_status "$O1"); O2 $(order_status "$O2"); O3 $(end_of "$O3") version $(version_of "$O3")" \
  "$([[ "$n_patches" == *"\"disabled\":[\"$PATCH\"]"*"\"disabled\":[\"$PATCH\"]"* && "$(order_status "$O1")" == AwaitingApproval && "$(order_status "$O2")" == AwaitingDelivery \
       && "$(end_of "$O3")" == Completed/Delivered && "$(version_of "$O3")" == none ]]; echo $?)"

# --- roll to N+1, one replica at a time ---------------------------------------------------------------------------
INJECTED_AT="$(now_iso)"
WORKFLOW_PATCHES_DISABLED= restart_pair workflow-host
decide "$ORDER_API" "$(review_of "$O1")" approve > "$DRILL_DIR/approve-1.txt"
O4="$(submit "$ORDER_API" "$RUN-4" "$(order_body "$RUN-c4" "$SKU" 1)")"
sleep 5
WORKFLOW_PATCHES_DISABLED= restart_pair workflow-host-2
rolled="$(for h in "${HOSTS[@]}"; do printf '%s=%s ' "$h" "$(patches_of "$h")"; done)"
printf '1 %s\n2 %s\n3 %s\n4 %s\n' "$O1" "$O2" "$O3" "$O4" > "$DRILL_DIR/orders.txt"
FAULT_OBSERVED="N: $n_patches; N+1: $rolled"
aexpect R17-rolled "both hosts report the patch enabled after the roll" "hosts $rolled" \
  "$([[ "$rolled" == *"\"enabled\":[\"$PATCH\"]"*"\"enabled\":[\"$PATCH\"]"* ]]; echo $?)"

# --- the waiting orders continue; a new order after the roll ------------------------------------------------------
carrier_script "$O2" "$DRILL_RUN_ID" "$SCRIPT" > "$DRILL_DIR/script-2.json"
O5="$(submit "$ORDER_API" "$RUN-5" "$(order_body "$RUN-c5" "$SKU" 1)")"
echo "5 $O5" >> "$DRILL_DIR/orders.txt"
for o in "$O1" "$O2" "$O4" "$O5"; do wait_status 240 "$o" 'Completed|Compensated|Rejected|ManualInterventionRequired' || true; done
RECOVERED_AT="$(now_iso)"
old="O1 $(end_of "$O1") version $(version_of "$O1"); O2 $(end_of "$O2") version $(version_of "$O2")"
aexpect R17-old-path "orders waiting across the upgrade Completed/Delivered on their recorded path (no version)" "$old" \
  "$([[ "$(end_of "$O1")" == Completed/Delivered && "$(end_of "$O2")" == Completed/Delivered && "$(version_of "$O1")" == none && "$(version_of "$O2")" == none ]]; echo $?)"
new="O4 (during the roll) $(end_of "$O4") version $(version_of "$O4"); O5 (after) $(end_of "$O5") version $(version_of "$O5")"
aexpect R17-new-path "O5 Completed/Delivered with version 2; O4 Completed/Delivered with version 2 or none" "$new" \
  "$([[ "$(end_of "$O5")" == Completed/Delivered && "$(version_of "$O5")" == 2 && "$(end_of "$O4")" == Completed/Delivered && "$(version_of "$O4")" =~ ^(2|none)$ ]]; echo $?)"

wfs=""; bad=0
for o in "$O1" "$O2" "$O3" "$O4" "$O5"; do
  w="$(workflow_of "$o")"; m="$(provider_money "$o")"; wfs+="$w $m; "
  [[ "$w" == Completed/* && "$m" == "1|1|0|0" ]] || bad=1
done
nondet="$(docker compose --profile replicas logs --since 15m workflow-host workflow-host-2 workflow-host-dapr workflow-host-2-dapr 2>/dev/null | grep -ciE 'non-?determinis')"
stalled="$(docker compose --profile replicas logs --since 15m workflow-host-dapr workflow-host-2-dapr 2>/dev/null | grep -ci 'stall')"
aexpect R17-no-failure "every workflow Completed, one authorization and one capture each; no non-determinism error" \
  "$wfs non-determinism lines $nondet; stall lines $stalled" "$([[ $bad == 0 && "$nondet" == 0 ]]; echo $?)"

# --- rollback: an instance that recorded the patch meets hosts that hold it back ----------------------------------
O6="$(submit "$ORDER_API" "$RUN-6" "$(review_order_body "$RUN-c6")")"
wait_until 60 "order 6 waits for its review" has_review "$O6" || true
v6_before="$(version_of "$O6")"
for h in "${HOSTS[@]}"; do WORKFLOW_PATCHES_DISABLED=$PATCH restart_pair "$h"; done
decide "$ORDER_API" "$(review_of "$O6")" approve > "$DRILL_DIR/approve-6.txt"
sleep 15
on_n="$(end_of "$O6") workflow $(workflow_of "$O6")"
stalls_n="$(docker compose --profile replicas logs --since 2m workflow-host-dapr workflow-host-2-dapr workflow-host workflow-host-2 2>/dev/null | grep -ciE 'stall')"
for h in "${HOSTS[@]}"; do WORKFLOW_PATCHES_DISABLED= restart_pair "$h"; done
wait_status 180 "$O6" 'Completed|Compensated|Rejected|ManualInterventionRequired' || true
after="$(end_of "$O6") version $(version_of "$O6") workflow $(workflow_of "$O6")"
echo "6 $O6" >> "$DRILL_DIR/orders.txt"
aexpect R17-rollback "version 2 recorded on N+1; on N after the approval not completed and not Failed; after rolling forward Completed/Delivered with version 2"   "before the rollback version $v6_before; on N after the approval: $on_n (stall log lines $stalls_n); after rolling forward: $after"   "$([[ "$v6_before" == 2 && "$on_n" != Completed/* && "$on_n" != *Failed* && "$(end_of "$O6")" == Completed/Delivered && "$(version_of "$O6")" == 2 ]]; echo $?)"
