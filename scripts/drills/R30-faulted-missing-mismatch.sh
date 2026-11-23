#!/usr/bin/env bash
# R30 (S44) faulted, missing, and mismatched workflows are detected by the reconciler and repaired.
# Drill stack: bash scripts/stack-up.sh --drill --replicas
#   A  an order that needs a review: BeforeStubFulfillmentStep (Throw) for its packing step more often than the
#      activity is retried, so the workflow fails after reservation and authorization (Dapr: Failed)
#   B  an order waiting for its review: the workflow host's drill control purges the instance (Dapr: not found)
#   C  FinalizeReportsTerminal (Skip): the finalizing step reports the order terminal without finalizing it, so the
#      run completes while SQL stays AwaitingDelivery (Dapr: Completed)
#   R30-<v>-proof     the Dapr state read through the workflow API (Failed / not found / Completed) and the order
#                     still non-terminal in SQL
#   R30-<v>-case      one case of the matching kind (WorkflowFaulted / WorkflowMissing / ExecutionMismatch) opened
#                     by the reconciler within the stage threshold plus two intervals, and only one
#   R30-alerts        WorkflowFaulted (A) and ExecutionMismatch (B, C) fired while their cases were open and resolved
#                     after the resolutions
#   R30-<v>-repaired  the scripted resolution takes the repair path: A and B (ConfirmExternalFailure) release the
#                     holds, void with the repair key, end Compensated/WorkflowFailed; C (ConfirmExternalSuccess)
#                     finalizes after Shipping shows Delivered: Completed/Delivered with one OrderCompleted event
DRILL_ID=R30
. "$(dirname "$0")/lib.sh"
. "$REPO_ROOT/scripts/scenarios/m3-lib.sh"
. "$REPO_ROOT/scripts/scenarios/m4-lib.sh"
drill_preflight
trap 'run_cleanup; drill_finish; exit $?' EXIT
hold_workflow_patches_back || exit 2
RUN="r30-$(date -u +%H%M%S)-$((RANDOM % 1000))"
SKU="$(fresh_sku R30 20)"
REVIEW_SKU="$(review_sku)"
review_of() { sql OrderDb "SELECT ReviewId FROM orders.ReviewTask WHERE OrderId = '$1' AND Status = 'Pending'"; }

a="$(submit "$ORDER_API" "$RUN-a" "$(order_body "$RUN-ca" "$REVIEW_SKU" 100 "$SKU" 1)")"
b="$(submit "$ORDER_API" "$RUN-b" "$(order_body "$RUN-cb" "$REVIEW_SKU" 100)")"
c="$(submit "$ORDER_API" "$RUN-c" "$(order_body "$RUN-cc" "$SKU" 1)")"
printf 'A %s\nB %s\nC %s\n' "$a" "$b" "$c" > "$DRILL_DIR/orders.txt"
arm_failpoint OrderDb FinalizeReportsTerminal "$(nkey "$c"):finalize:1" Skip
for o in "$a" "$b"; do has_review() { [[ -n "$(review_of "$o")" ]]; }; wait_until 60 "review of $o" has_review; done
arm_failpoint_times OrderDb BeforeStubFulfillmentStep "$(nkey "$a"):stub-fulfil:line-1" Throw 8
INJECTED_AT="$(now_iso)"
ALERT_SINCE="$(date +%s)"; WF_BEFORE="$(alert_firing WorkflowFaulted)"; EM_BEFORE="$(alert_firing ExecutionMismatch)"
decide "$ORDER_API" "$(review_of "$a")" approve > /dev/null
purge="$(drill_purge_fulfillment "$b")"; echo "$purge" > "$DRILL_DIR/purge-b.json"

wf_is() { [[ "$(json_get "$(admin_api GET "/system/orders/$1/execution")" workflow.status)" == "$2" ]]; }
wf_missing() { [[ "$(json_get "$(admin_api GET "/system/orders/$1/execution")" workflow.exists | tr A-Z a-z)" == false ]]; }
wait_until 120 "A workflow Failed" wf_is "$a" Failed || true
wait_until 30 "B workflow gone" wf_missing "$b" || true
wait_until 180 "C workflow Completed" wf_is "$c" Completed || true
proof_a="$(workflow_of "$a") $(order_status "$a")"; proof_b="purge $(json_get "$purge" purged | tr A-Z a-z)/exists $(json_get "$purge" exists | tr A-Z a-z) $(order_status "$b")"
proof_c="$(workflow_of "$c") $(order_status "$c")"
FAULT_OBSERVED="A $proof_a; B $proof_b; C $proof_c"
aexpect R30-A-proof "A workflow Failed, order not terminal (Packing)" "$proof_a" "$([[ "$proof_a" == Failed/* && "$proof_a" == *" Packing" ]]; echo $?)"
aexpect R30-B-proof "B instance purged (not found), order AwaitingApproval" "$proof_b" "$([[ "$proof_b" == "purge true/exists false AwaitingApproval" ]] && wf_missing "$b"; echo $?)"
aexpect R30-C-proof "C workflow Completed, order AwaitingDelivery" "$proof_c" "$([[ "$proof_c" == Completed/* && "$proof_c" == *" AwaitingDelivery" ]]; echo $?)"

variant() { # variant <name> <order> <kind> <action> <expected end>
  local v=$1 o=$2 kind=$3 action=$4 want=$5 t0=$SECONDS case_id res
  opened() { case_open "$o" "$kind"; }
  wait_until 75 "$v $kind case" opened || true
  local took=$((SECONDS - t0)) count; count="$(sql OrderDb "SELECT COUNT(*) FROM orders.InterventionCase WHERE OrderId = '$o' AND Kind = '$kind'")"
  aexpect "R30-$v-case" "one $kind case opened by the reconciler" "cases $(cases_of "$o") (waited ${took}s)" \
    "$([[ "$count" == 1 && "$(cases_of "$o")" == "$kind:Open:-" ]]; echo $?)"
  case_id="$(open_case_of "$o" "$kind")"
  # The case stays open until its alert has fired (the alert pipeline takes up to about 20 s).
  alert_wait_firing "$([[ "$kind" == WorkflowFaulted ]] && echo WorkflowFaulted || echo ExecutionMismatch)" '' 60 || true
  res="$(resolve_case "$ORDER_API" "$case_id" "$action" "$(new_uuid)")"
  wait_status 120 "$o" 'Completed|Compensated|Rejected|Cancelled' || true
  repaired() { [[ -n "$(sql OrderDb "SELECT RepairOutcome FROM orders.InterventionCase WHERE CaseId = '$case_id'")" ]]; }
  wait_until 60 "$v repair outcome recorded" repaired || true
  local outcome end ops holds events
  outcome="$(sql OrderDb "SELECT RepairOutcome FROM orders.InterventionCase WHERE CaseId = '$case_id'")"
  end="$(end_of "$o")"; ops="$(provider_ops "$o")"; holds="$(holds_of "$o")"; events="$(completed_events "$o")"
  local ok=1
  if [[ "$want" == Compensated/WorkflowFailed ]]; then
    [[ "$res" == "200 Repair" && "$end" == "$want" && "$outcome" == Void:* && "$holds" != *Held* && "$ops" == *"void:case-"* ]] && ok=0
  else
    [[ "$res" == "200 Repair" && "$end" == "$want" && "$outcome" == Finalized:Applied && "$events" == 1 ]] && ok=0
  fi
  aexpect "R30-$v-repaired" "resolution 200 Repair; end $want; repair keys only" \
    "resolution $res; repair outcome $outcome; end $end; provider $ops; holds '$holds'; OrderCompleted $events; cases $(cases_of "$o")" "$ok"
}
variant A "$a" WorkflowFaulted ConfirmExternalFailure Compensated/WorkflowFailed
variant B "$b" WorkflowMissing ConfirmExternalFailure Compensated/WorkflowFailed
variant C "$c" ExecutionMismatch ConfirmExternalSuccess Completed/Delivered
RECOVERED_AT="$(now_iso)"
read -r v1 d1 <<< "$(alert_outcome WorkflowFaulted '' "$ALERT_SINCE" "$WF_BEFORE" 90)"
read -r v2 d2 <<< "$(alert_outcome ExecutionMismatch '' "$ALERT_SINCE" "$EM_BEFORE" 90)"
assert R30-alerts "$([[ "$v1" == PASS && "$v2" == PASS ]] && echo PASS || { [[ "$v1" == UNVERIFIED || "$v2" == UNVERIFIED ]] && echo UNVERIFIED || echo FAIL; })" \
  "WorkflowFaulted and ExecutionMismatch fire while their cases are open and resolve after the resolutions" "$v1 $d1; $v2 $d2"
