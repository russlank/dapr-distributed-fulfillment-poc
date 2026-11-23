#!/usr/bin/env bash
# R33 (S50) a payment provider outage or crash leads to compensation or reconciliation, never to a
# duplicate charge. Drill stack: bash scripts/stack-up.sh --drill --replicas
#   A  the provider is stopped before the authorization: nothing can be sent (connection refused / name
#      not resolved). The authorization ends Unavailable after its budget; the workflow compensates with
#      PaymentProviderUnavailable. The provider is restored while the compensation runs, so the void by the
#      authorization's key reaches it: it commits NothingToVoid and prohibits the key, and a late
#      authorization with that key is refused (Prohibited).
#   B  the provider commits the authorization but answers after the client's timeout (DelayResponse); it is
#      killed as soon as the operation is committed, so every status query fails. The authorization is
#      undeterminable: an AmbiguousPayment case. After the provider is restored, the operator resolves with
#      the provider's record as evidence; the workflow reconciles, adopts Committed, and completes.
#   C  the provider commits the capture and crashes before answering (CrashAfterCommit, drill build). It is
#      restarted; the committed capture is adopted (directly, or through a case resolved with the provider's
#      record if the restart outlasts the status query budget); never a second capture.
#   R33-A-refused / R33-A-compensated / R33-A-prohibited, R33-B-ambiguous / R33-B-reconciled,
#   R33-C-crashed / R33-C-adopted (see the expected text of each assertion); R33-alert: PaymentProviderUnavailable fired
#   during the outage and resolved after the restore
DRILL_ID=R33
. "$(dirname "$0")/lib.sh"
. "$REPO_ROOT/scripts/scenarios/m3-lib.sh"
drill_preflight
PROV=payment-provider-simulator
provider_ready() { [[ "$(curl -s -m 5 -o /dev/null -w '%{http_code}' "$PROVIDER/health/ready")" == 200 ]]; }
provider_up() {
  [[ "$(docker compose ps --format '{{.State}}' "$PROV" 2>/dev/null)" == running ]] || docker compose up -d --no-build --no-deps "$PROV" >/dev/null 2>&1
  wait_until 90 "provider ready" provider_ready
}
trap 'provider_up; provider_clear "$DRILL_RUN_ID"; drill_finish; exit $?' EXIT
RUN="r33-$(date -u +%H%M%S)-$((RANDOM % 1000))"
SKU="$(fresh_sku R33 30)"

# --- A: outage before the authorization -------------------------------------------------------------
ALERT_SINCE="$(date +%s)"; PU_BEFORE="$(alert_firing PaymentProviderUnavailable)"
docker compose stop -t 5 "$PROV" >/dev/null 2>&1
INJECTED_AT="$(now_iso)"
stopped="$(docker compose ps -a --format '{{.State}}' "$PROV")"
A="$(submit "$ORDER_API" "$RUN-a" "$(order_body "$RUN-ca" "$SKU" 1)")"
echo "A $A" >> "$DRILL_DIR/orders.txt"
auth_unavailable() { [[ "$(payment_ops "$A")" == *Authorize=Finished/Unavailable* ]]; }
wait_until 120 "A: authorization Unavailable" auth_unavailable
attempts_a="$(payment_attempts "$A" Authorize)"
provider_up
wait_status 180 "$A" 'Compensated|Completed|Rejected|ManualInterventionRequired'
aexpect R33-A-refused "provider stopped before the order; every authorize send Refused (nothing sent); Unavailable; no AmbiguousPayment case" \
  "provider state $stopped; attempts $attempts_a; payment $(payment_ops "$A"); cases '$(cases_of "$A")'" \
  "$([[ "$stopped" == exited && "$attempts_a" =~ ^(Send:Refused@-,?)+$ && "$(cases_of "$A")" != *AmbiguousPayment* ]]; echo $?)"
ops_a="$(provider_ops "$A")"
aexpect R33-A-compensated "Compensated/PaymentProviderUnavailable; hold Released; provider: no authorization, the void by key committed NothingToVoid" \
  "end $(end_of "$A"); holds $(holds_of "$A"); provider $ops_a; payment $(payment_ops "$A")" \
  "$([[ "$(end_of "$A")" == Compensated/PaymentProviderUnavailable && "$(holds_of "$A")" == Released && "$ops_a" == "void:1=NothingToVoid" ]]; echo $?)"
late="$(curl -s -m 10 -X POST "$PROVIDER/provider/operations" -H "X-Api-Key: $PROVIDER_API_KEY" -H 'content-type: application/json' \
  -d '{"operationKey":"'"$(nkey "$A")"':authorize:1","kind":"Authorize","amountMinor":100,"currency":"EUR","reference":null}')"
prohibition="$(sql PaymentProviderSimDb "SELECT COUNT(*) FROM provider.Prohibition WHERE OperationKey = '$(nkey "$A"):authorize:1'")"
aexpect R33-A-prohibited "a late authorization with the order's key is refused (status Prohibited); one prohibition row; still no authorization" \
  "late answer $(json_get "$late" status)/$(json_get "$late" result); prohibitions $prohibition; provider $(provider_ops "$A")" \
  "$([[ "$(json_get "$late" status)" == Prohibited && "$prohibition" == 1 && "$(provider_money "$A")" == 0\|0\|0\|0 ]]; echo $?)"

# --- B: committed, answer delayed past the timeout, provider killed ---------------------------------
provider_fault "$DRILL_RUN_ID-b" kind:Authorize DelayResponse 15000 1 > /dev/null
B="$(submit "$ORDER_API" "$RUN-b" "$(order_body "$RUN-cb" "$SKU" 1)")"
echo "B $B" >> "$DRILL_DIR/orders.txt"
committed_b() { [[ "$(sql PaymentProviderSimDb "SELECT COUNT(*) FROM provider.Operation WHERE OperationKey = '$(nkey "$B"):authorize:1'")" == 1 ]]; }
wait_until 60 "B: authorization committed at the provider" committed_b
docker compose kill "$PROV" >/dev/null 2>&1
killed_at="$(now_iso)"
wait_until 180 "B: AmbiguousPayment case" case_open "$B" AmbiguousPayment
attempts_b="$(payment_attempts "$B" Authorize)"
aexpect R33-B-ambiguous "one send without an answer (Timeout, or Error when the kill broke the connection), status queries failing while the provider is down, AmbiguousPayment case; ManualInterventionRequired" \
  "killed $killed_at; attempts $attempts_b; status $(order_status "$B"); cases $(cases_of "$B")" \
  "$([[ "$attempts_b" =~ ^Send:(Timeout|Error)@-, && $(grep -o 'Send:' <<< "$attempts_b" | wc -l) == 1 && "$attempts_b" != *Committed* && "$(order_status "$B")" == ManualInterventionRequired ]]; echo $?)"
provider_up
evidence_b="$(curl -s -m 10 "$PROVIDER/provider/operations/$(nkey "$B"):authorize:1" -H "X-Api-Key: $PROVIDER_API_KEY")"
read -r sb pb <<< "$(resolve_case "$ORDER_API" "$(open_case_of "$B" AmbiguousPayment)" ConfirmExternalSuccess "$(new_uuid)")"
wait_status 180 "$B" 'Completed|Compensated|Rejected'
aexpect R33-B-reconciled "after restore the provider record is Committed; resolution Signal; reconciled, Completed; one authorization, one capture" \
  "evidence $(json_get "$evidence_b" answer)$(json_get "$evidence_b" status); resolution $sb $pb; end $(end_of "$B"); provider $(provider_ops "$B"); payment $(payment_ops "$B")" \
  "$([[ "$evidence_b" == *Committed* && "$sb" == 200 && "$pb" == Signal && "$(end_of "$B")" == Completed/Delivered && "$(provider_money "$B")" == 1\|1\|0\|0 ]]; echo $?)"

# --- C: crash after committing the capture ----------------------------------------------------------
provider_fault "$DRILL_RUN_ID-c" kind:Capture CrashAfterCommit null 1 > /dev/null
C="$(submit "$ORDER_API" "$RUN-c" "$(order_body "$RUN-cc" "$SKU" 1)")"
echo "C $C" >> "$DRILL_DIR/orders.txt"
provider_down() { [[ "$(docker compose ps -a --format '{{.State}}' "$PROV")" != running ]]; }
wait_until 120 "C: provider crashed" provider_down
exit_code="$(docker inspect -f '{{.State.ExitCode}}' "$(docker compose ps -a -q "$PROV")")"
crashed_capture="$(sql PaymentProviderSimDb "SELECT CONCAT(Result, '|', ISNULL(FaultApplied, '-')) FROM provider.Operation WHERE OperationKey = '$(nkey "$C"):capture:1'")"
FAULT_OBSERVED="A refused while stopped; B killed $killed_at; C provider exit $exit_code"
aexpect R33-C-crashed "the capture committed (CrashAfterCommit marker), then the provider process ended" \
  "capture $crashed_capture; provider exit code $exit_code" "$([[ "$crashed_capture" == Captured\|*CrashAfterCommit* && "$exit_code" != 0 ]]; echo $?)"
provider_up
settled_c() { status_is "$C" 'Completed|Compensated|Rejected' || case_open "$C" AmbiguousPayment; }
wait_until 180 "C: completed or waiting in a case" settled_c
resolved_c="-"
if case_open "$C" AmbiguousPayment; then
  read -r sc pc <<< "$(resolve_case "$ORDER_API_2" "$(open_case_of "$C" AmbiguousPayment)" ConfirmExternalSuccess "$(new_uuid)")"
  resolved_c="$sc $pc"
fi
wait_status 180 "$C" 'Completed|Compensated|Rejected'
RECOVERED_AT="$(now_iso)"
aexpect R33-C-adopted "the committed capture adopted (directly or via a resolved case); Completed; exactly one capture, no refund" \
  "case resolution $resolved_c; capture attempts $(payment_attempts "$C" Capture); end $(end_of "$C"); provider $(provider_ops "$C")" \
  "$([[ "$(end_of "$C")" == Completed/Delivered && "$(provider_money "$C")" == 1\|1\|0\|0 && $(payment_attempts "$C" Capture | grep -o 'Send:' | wc -l) == 1 ]]; echo $?)"
read -r verdict detail <<< "$(alert_outcome PaymentProviderUnavailable '' "$ALERT_SINCE" "$PU_BEFORE" 150)"
assert R33-alert "$verdict" "PaymentProviderUnavailable fires for the failing provider requests and resolves afterwards" "$detail"
