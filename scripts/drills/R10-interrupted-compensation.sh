#!/usr/bin/env bash
# R10 (S14) an interrupted or deliberately failing compensation finishes, or surfaces as a case.
# Drill stack: bash scripts/stack-up.sh --drill --replicas
# Both orders have two lines (two holds). The provider refuses the capture before committing anything
# (RejectBeforeCommit on every capture send), so the workflow compensates: release both holds, void the
# authorization by its key, end Compensated (PaymentProviderUnavailable).
#   A  the inventory process ends (FailFast) right after releasing the first hold, before the second
#      (failpoint AfterReleaseOneHoldBeforeNext). Dapr retries the release on a live replica, which
#      releases only the hold still held.
#   B  the void is refused before committing as well, until the compensation rounds are used up: a
#      CompensationExhausted case. The operator clears the cause and resolves ConfirmExternalFailure (the
#      void did not happen: compensate again); the void commits and the order ends Compensated.
#   R10-A-fired      the failpoint fired after one hold was released; one inventory replica exited
#   R10-A-finished   both holds Released exactly once (two HoldReleased events), stock back, the
#                    authorization Voided once, the order Compensated/PaymentProviderUnavailable
#   R10-B-surfaced   holds released; the void Unavailable after its rounds; a CompensationExhausted case
#                    Open; the order ManualInterventionRequired; nothing voided yet
#   R10-B-finished   after the resolution the void commits (Voided once), the case is Resolved (Signal),
#                    the order Compensated/PaymentProviderUnavailable; never a capture
DRILL_ID=R10
. "$(dirname "$0")/lib.sh"
. "$REPO_ROOT/scripts/scenarios/m3-lib.sh"
. "$REPO_ROOT/scripts/scenarios/replica-lib.sh"
drill_preflight
require_replicas
INV=(inventory-service inventory-service-2 inventory-service-3)
state_of() { docker compose --profile replicas ps -a --format '{{.State}}' "$1" 2>/dev/null; }
restart_dead() { local s; for s in "${INV[@]}"; do [[ "$(state_of "$s")" == running ]] || start_pair "$s"; done; }
trap 'restart_dead; provider_clear "$DRILL_RUN_ID"; drill_finish; exit $?' EXIT
RUN="r10-$(date -u +%H%M%S)-$((RANDOM % 1000))"
S1="$(fresh_sku R10A 10)"; S2="$(fresh_sku R10B 10)"
stock_of() { sql InventoryDb "SELECT STRING_AGG(CONCAT(OnHand, '/', Reserved), ',') WITHIN GROUP (ORDER BY Sku) FROM inventory.StockItem WHERE WarehouseId = 'W1' AND Sku IN ('$S1', '$S2')"; }

# --- A: crash between the two releases -------------------------------------------------------------
provider_fault "$DRILL_RUN_ID-a" kind:Capture RejectBeforeCommit null 4 > /dev/null
arm_failpoint InventoryDb AfterReleaseOneHoldBeforeNext '*' FailFast
INJECTED_AT="$(now_iso)"
A="$(submit "$ORDER_API" "$RUN-a" "$(order_body "$RUN-ca" "$S1" 1 "$S2" 1)")"
echo "A $A" >> "$DRILL_DIR/orders.txt"
wait_status 240 "$A" 'Compensated|Completed|Rejected|ManualInterventionRequired'
fired="$(failpoint_fired_at InventoryDb AfterReleaseOneHoldBeforeNext)"
dead=""; for s in "${INV[@]}"; do [[ "$(state_of "$s")" != running ]] && dead="$s"; done
FAULT_OBSERVED="A: FiredAt $fired, exited ${dead:-none}"
aexpect R10-A-fired "fired after the first release; one inventory replica exited" "FiredAt '$fired'; exited '${dead:-none}'" \
  "$([[ -n "$fired" && -n "$dead" ]]; echo $?)"
released="$(sql InventoryDb "SELECT COUNT(*) FROM inventory.Outbox WHERE OrderId = '$A' AND EventType = 'HoldReleased'")"
aexpect R10-A-finished "holds Released,Released (2 HoldReleased); stock 10/0,10/0; void:1=Voided once; Compensated/PaymentProviderUnavailable" \
  "holds $(holds_of "$A"); HoldReleased $released; stock $(stock_of); provider $(provider_ops "$A"); end $(end_of "$A")" \
  "$([[ "$(holds_of "$A")" == Released,Released && "$released" == 2 && "$(stock_of)" == 10/0,10/0 && "$(provider_ops "$A")" == "authorize:1=Authorized,void:1=Voided" && "$(end_of "$A")" == Compensated/PaymentProviderUnavailable ]]; echo $?)"
restart_dead

# --- B: the void fails until the rounds are used up --------------------------------------------------
provider_fault "$DRILL_RUN_ID-b-capture" kind:Capture RejectBeforeCommit null 4 > /dev/null
provider_fault "$DRILL_RUN_ID-b-void" kind:Void RejectBeforeCommit null null > /dev/null
B="$(submit "$ORDER_API" "$RUN-b" "$(order_body "$RUN-cb" "$S1" 1 "$S2" 1)")"
echo "B $B" >> "$DRILL_DIR/orders.txt"
wait_until 240 "B: CompensationExhausted case" case_open "$B" CompensationExhausted
void_attempts="$(payment_attempts "$B" Void)"
aexpect R10-B-surfaced "holds Released; void sends refused (503) over the rounds; CompensationExhausted Open; ManualInterventionRequired; nothing voided" \
  "holds $(holds_of "$B"); void attempts $void_attempts; cases $(cases_of "$B"); status $(order_status "$B"); provider a|c|v|r $(provider_money "$B")" \
  "$([[ "$(holds_of "$B")" == Released,Released && "$void_attempts" == *Rejected503* && "$(cases_of "$B")" == CompensationExhausted:Open:- && "$(order_status "$B")" == ManualInterventionRequired && "$(provider_money "$B")" == 1\|0\|0\|0 ]]; echo $?)"
provider_clear "$DRILL_RUN_ID-b-void"
read -r sb pb <<< "$(resolve_case "$ORDER_API_2" "$(open_case_of "$B" CompensationExhausted)" ConfirmExternalFailure "$(new_uuid)")"
wait_status 180 "$B" 'Compensated|Completed|Rejected'
RECOVERED_AT="$(now_iso)"
aexpect R10-B-finished "resolution 200 Signal; void:1=Voided once; case Resolved; Compensated/PaymentProviderUnavailable; no capture" \
  "resolution $sb $pb; provider $(provider_ops "$B"); cases $(cases_of "$B"); end $(end_of "$B")" \
  "$([[ "$sb" == 200 && "$pb" == Signal && "$(provider_ops "$B")" == "authorize:1=Authorized,void:1=Voided" && "$(cases_of "$B")" == CompensationExhausted:Resolved:Signal && "$(end_of "$B")" == Compensated/PaymentProviderUnavailable ]]; echo $?)"
