#!/usr/bin/env bash
# R26 (S37) an undeterminable payment becomes a safe case, resolved correctly while the workflow waits and
# after it ended. Drill stack: bash scripts/stack-up.sh --drill --replicas
# For each of two orders the provider commits the authorization and drops the answer
# (DropResponseAfterCommit), and every status query for that key is answered Unknown (StatusUnknown,
# unlimited). The payment service cannot decide, so the workflow opens an AmbiguousPayment case and waits.
# Order 1 is resolved while its workflow waits; order 2's workflow is terminated first (drill control).
# The operator's evidence is the provider record read after the StatusUnknown fault is cleared.
#   R26-case       per order: one committed authorization (fault marker), status queries answered
#                  Unknown, one send only, an Open AmbiguousPayment case, order ManualInterventionRequired
#   R26-signal     order 1: ConfirmExternalSuccess takes the Signal path; the workflow reconciles with the
#                  provider (Authorized), continues, and the order Completes; one capture
#   R26-repair     order 2: terminated, then ConfirmExternalSuccess takes the Repair path; the repair
#                  workflow releases the hold, voids the authorization with its repair key (Voided), the
#                  order ends Compensated, the case records the repair outcome
#   R26-once       each resolution executed once (order 1: one pending signal, no repair request; order 2:
#                  one repair start request, no signal); the provider has one authorization per order and
#                  no capture for order 2
DRILL_ID=R26
. "$(dirname "$0")/lib.sh"
. "$REPO_ROOT/scripts/scenarios/m3-lib.sh"
drill_preflight
trap 'provider_clear "$DRILL_RUN_ID"; run_cleanup; drill_finish; exit $?' EXIT
hold_workflow_patches_back || exit 2
RUN="r26-$(date -u +%H%M%S)-$((RANDOM % 1000))"
SKU="$(fresh_sku R26 20)"

# undeterminable <n>: submits order n with both faults; waits for its case. Sets O<n> and C<n>.
undeterminable() {
  local n=$1 o
  provider_fault "$DRILL_RUN_ID-drop-$n" kind:Authorize DropResponseAfterCommit null 1 > /dev/null
  o="$(submit "$ORDER_API" "$RUN-$n" "$(order_body "$RUN-c$n" "$SKU" 1)")"
  provider_fault "$DRILL_RUN_ID-unknown-$n" "$(nkey "$o"):authorize:1" StatusUnknown null null > /dev/null
  echo "order $n $o" >> "$DRILL_DIR/orders.txt"
  wait_until 180 "AmbiguousPayment case for order $n" case_open "$o" AmbiguousPayment
  printf -v "O$n" '%s' "$o"
  printf -v "C$n" '%s' "$(open_case_of "$o" AmbiguousPayment)"
}
case_state() { # case_state <n> -> one line of evidence; passes when everything matches
  local o=$1 key; key="$(nkey "$o"):authorize:1"
  local prov unknown sends status cases
  prov="$(sql PaymentProviderSimDb "SELECT CONCAT(Result, '|', ISNULL(FaultApplied, '-')) FROM provider.Operation WHERE OperationKey = '$key'")"
  unknown="$(sql PaymentProviderSimDb "SELECT COUNT(*) FROM provider.StatusQuery WHERE OperationKey = '$key' AND Answer = 'Unknown'")"
  sends="$(payment_attempts "$o" Authorize | grep -o 'Send:' | wc -l)"
  status="$(order_status "$o")"; cases="$(cases_of "$o")"
  echo "provider $prov; Unknown answers $unknown; sends $sends; status $status; cases $cases"
  [[ "$prov" == Authorized\|*DropResponseAfterCommit* && "$unknown" -ge 1 && "$sends" == 1 && "$status" == ManualInterventionRequired && "$cases" == AmbiguousPayment:Open:- ]]
}

INJECTED_AT="$(now_iso)"
undeterminable 1
undeterminable 2
FAULT_OBSERVED="cases $C1 (order $O1), $C2 (order $O2)"
e1="$(case_state "$O1")"; r1=$?; e2="$(case_state "$O2")"; r2=$?
aexpect R26-case "both: Authorized with the drop marker, Unknown status answers, one send, ManualInterventionRequired, AmbiguousPayment Open" \
  "order 1: $e1 | order 2: $e2" "$(( r1 + r2 ))"

# Order 1: resolve while the workflow waits.
provider_clear "$DRILL_RUN_ID-unknown-1"
evidence1="$(curl -s -m 10 "$PROVIDER/provider/operations/$(nkey "$O1"):authorize:1" -H "X-Api-Key: $PROVIDER_API_KEY")"
echo "order 1 evidence: $evidence1" >> "$DRILL_DIR/evidence.txt"
read -r s1 p1 <<< "$(resolve_case "$ORDER_API" "$C1" ConfirmExternalSuccess "$(new_uuid)")"
wait_status 180 "$O1" 'Completed|Compensated|Rejected'
aexpect R26-signal "provider answered Committed; 200 Signal; order Completed/Delivered; authorized 1, captured 1" \
  "evidence $(json_get "$evidence1" answer)/$(json_get "$evidence1" status); resolution $s1 $p1; end $(end_of "$O1"); provider a|c|v|r $(provider_money "$O1"); case $(cases_of "$O1")" \
  "$([[ "$(json_get "$evidence1" answer)$(json_get "$evidence1" status)" == *Committed* && "$s1" == 200 && "$p1" == Signal && "$(end_of "$O1")" == Completed/Delivered && "$(provider_money "$O1")" == 1\|1\|0\|0 ]]; echo $?)"

# Order 2: terminate the workflow, then resolve.
term="$(drill_terminate_fulfillment "$O2")"; echo "terminate: $term" >> "$DRILL_DIR/evidence.txt"
provider_clear "$DRILL_RUN_ID-unknown-2"
read -r s2 p2 <<< "$(resolve_case "$ORDER_API_2" "$C2" ConfirmExternalSuccess "$(new_uuid)" operator-2)"
wait_status 180 "$O2" 'Completed|Compensated|Rejected'
repair_done() { [[ -n "$(sqln OrderDb "SELECT RepairOutcome FROM orders.InterventionCase WHERE CaseId = '$C2'")" ]]; }
wait_until 60 "repair outcome recorded" repair_done
ops2="$(provider_ops "$O2")"; outcome2="$(sqln OrderDb "SELECT RepairOutcome FROM orders.InterventionCase WHERE CaseId = '$C2'")"
aexpect R26-repair "terminated; 200 Repair; hold Released (or Expired while waiting), never Consumed; authorize:1=Authorized then a void with the repair key Voided; Compensated; outcome recorded" \
  "terminate $(json_get "$term" previousStatus)->$(json_get "$term" status); resolution $s2 $p2; holds $(holds_of "$O2"); provider $ops2; end $(end_of "$O2"); repair outcome $outcome2" \
  "$([[ "$(json_get "$term" status)" == Terminated && "$s2" == 200 && "$p2" == Repair && "$(holds_of "$O2")" =~ ^(Released|Expired)$ && "$ops2" =~ ^authorize:1=Authorized,void:case-[0-9a-f]+=Voided$ && "$(order_status "$O2")" == Compensated && "$outcome2" == Void:Voided ]]; echo $?)"

once="$(sql OrderDb "SELECT CONCAT(
  (SELECT COUNT(*) FROM orders.PendingSignal WHERE OrderId = '$O1' AND EventName LIKE 'InterventionResolved%'), '|', (SELECT COUNT(*) FROM orders.RepairStartRequest WHERE OrderId = '$O1'), '|',
  (SELECT COUNT(*) FROM orders.PendingSignal WHERE OrderId = '$O2' AND EventName LIKE 'InterventionResolved%'), '|', (SELECT COUNT(*) FROM orders.RepairStartRequest WHERE OrderId = '$O2'), '|',
  (SELECT MAX(State) FROM orders.RepairStartRequest WHERE OrderId = '$O2'))")"
RECOVERED_AT="$(now_iso)"
aexpect R26-once "signal/repair per order: 1|0|0|1|Closed; provider one authorization each, no capture for order 2" \
  "signals|repairs: $once; order 1 a|c|v|r $(provider_money "$O1"); order 2 a|c|v|r $(provider_money "$O2")" \
  "$([[ "$once" == 1\|0\|0\|1\|Closed && "$(provider_money "$O2")" == 1\|0\|1\|0 ]]; echo $?)"
