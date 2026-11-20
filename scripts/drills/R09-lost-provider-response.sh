#!/usr/bin/env bash
# R09 (S38) a lost provider response is reconciled without a second financial effect.
# Drill stack: bash scripts/stack-up.sh --drill --replicas
# The provider fault DropResponseAfterCommit commits the operation and closes the connection without an
# answer: variant 1 on the authorization of order 1, variant 2 on the capture of order 2. The payment
# service must take the missing answer as "may have committed", query the status by the same key, adopt
# the committed result, and the order continue to Completed.
#   R09-<v>-fired      the provider committed the operation once, marked DropResponseAfterCommit
#   R09-<v>-detected   the payment service's first send of that key has no answer (Timeout or Error),
#                      followed by a status query answered Committed, and no second send
#   R09-<v>-adopted    the payment operation Finished with the provider's result (Authorized / Captured);
#                      no void or refund was decided
#   R09-<v>-once       provider: exactly one operation per key of the order (authorize, capture), no void
#                      or refund; the order Completed (9-row history without review: 8 rows)
DRILL_ID=R09
. "$(dirname "$0")/lib.sh"
. "$REPO_ROOT/scripts/scenarios/m3-lib.sh"
drill_preflight
trap 'provider_clear "$DRILL_RUN_ID"; drill_finish; exit $?' EXIT
RUN="r09-$(date -u +%H%M%S)-$((RANDOM % 1000))"
SKU="$(fresh_sku R09 20)"

variant() { # variant <name> <kind: Authorize|Capture> <key step: authorize|capture> <expected result>
  local v=$1 kind=$2 step=$3 result=$4
  provider_fault "$DRILL_RUN_ID-$v" "kind:$kind" DropResponseAfterCommit null 1 > "$DRILL_DIR/fault-$v.json"
  [[ -z "$INJECTED_AT" ]] && INJECTED_AT="$(now_iso)"
  local o; o="$(submit "$ORDER_API" "$RUN-$v" "$(order_body "$RUN-c-$v" "$SKU" 1)")"
  echo "$v $o" >> "$DRILL_DIR/orders.txt"
  wait_status 180 "$o" 'Completed|Compensated|Rejected|ManualInterventionRequired'
  local key; key="$(nkey "$o"):$step:1"

  local prov; prov="$(sql PaymentProviderSimDb "SELECT CONCAT(Result, '|', ISNULL(FaultApplied, '-'), '|', Replays) FROM provider.Operation WHERE OperationKey = '$key'")"
  FAULT_OBSERVED+="$v: $prov; "
  aexpect "R09-$v-fired" "$result committed once with DropResponseAfterCommit" "$key: $prov" "$([[ "$prov" == "$result|"*DropResponseAfterCommit* ]]; echo $?)"

  local attempts; attempts="$(payment_attempts "$o" "$kind")"
  aexpect "R09-$v-detected" "first send unanswered (Timeout/Error), then a status query Committed, one send" "attempts $attempts" \
    "$([[ "$attempts" =~ ^Send:(Timeout|Error)@-, && "$attempts" == *Committed* && $(grep -o 'Send:' <<< "$attempts" | wc -l) == 1 ]]; echo $?)"

  local pay; pay="$(payment_ops "$o")"
  aexpect "R09-$v-adopted" "$kind Finished/$result adopted; no Void or Refund operation" "payment operations $pay" \
    "$([[ "$pay" == *"$kind=Finished/$result"* && "$pay" != *Void* && "$pay" != *Refund* ]]; echo $?)"

  local ops money end history; ops="$(provider_ops "$o")"; money="$(provider_money "$o")"; end="$(end_of "$o")"; history="$(history_of "$o")"
  aexpect "R09-$v-once" "provider authorize:1=Authorized,capture:1=Captured only; Completed/Delivered after 8 history rows" \
    "provider $ops (a|c|v|r $money); end $end; history $history" \
    "$([[ "$ops" == "authorize:1=Authorized,capture:1=Captured" && "$money" == 1\|1\|0\|0 && "$end" == Completed/Delivered && $(tr ',' '\n' <<< "$history" | wc -l) == 8 ]]; echo $?)"
}

variant authorize Authorize authorize Authorized
variant capture Capture capture Captured
RECOVERED_AT="$(now_iso)"
