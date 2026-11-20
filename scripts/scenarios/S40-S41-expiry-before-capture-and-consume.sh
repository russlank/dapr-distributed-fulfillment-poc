#!/usr/bin/env bash
# S40 a hold expired before consumption (also during an intervention pause) and S41 an authorization that
# expired before the capture. Drill stack with replicas: bash scripts/stack-up.sh --drill --replicas
# The scenario recreates the inventory replicas with a 20 s hold lifetime (INVENTORY_HOLD_TTL) and restores
# the stack's value afterwards, so the 30 s extension before the capture really lengthens the holds.
#   S40-before-capture  the authorization is undeterminable (dropped answer, status Unknown): the order waits in
#                       an AmbiguousPayment case until its hold expired (sweeper). After the resolution the
#                       workflow reconciles (Authorized), the extension finds the hold expired, and the order
#                       ends Compensated (ReservationExpired): the authorization voided, never captured
#   S40-after-capture   the hold is extended (HoldExtended), then the capture is undeterminable the same way;
#                       the order waits until the extended hold expired. After the resolution the capture is
#                       adopted (Captured), the consumption finds the hold expired, and the capture is refunded
#                       by its key: Compensated (ReservationExpired), Refunded once, stock untouched
#   S41-authorization   the provider grants an authorization valid for 10 s, and the authorization's answer is
#                       undeterminable too: the order waits in a case past the authorization's expiry (the hold
#                       may still be valid). After the resolution nothing is captured: either the extension,
#                       capped at the expired authorization, leaves too little validity and no capture is sent
#                       (Compensated, ReservationExpired), or a capture is sent and the provider refuses it
#                       (Compensated, PaymentCaptureFailed); the authorization voided, the hold released
. "$(dirname "$0")/../lib.sh"
. "$(dirname "$0")/m3-lib.sh"
. "$(dirname "$0")/replica-lib.sh"
scenario_init S40
INV=(inventory-service inventory-service-2 inventory-service-3)
ttl_of() { docker compose --profile replicas exec -T "$1" printenv INVENTORY_HOLD_TTL 2>/dev/null | tr -d '\r'; }
with_ttl() { local s; for s in "${INV[@]}"; do INVENTORY_HOLD_TTL="$1" restart_pair "$s" || return 1; done; }
STACK_TTL="$(ttl_of inventory-service)"
restore() { provider_clear "$PREFIX"; [[ "$STACK_TTL" == 00:00:20 ]] || with_ttl "$STACK_TTL" >/dev/null 2>&1; }
trap restore EXIT
if [[ "$STACK_TTL" != 00:00:20 ]]; then
  with_ttl 00:00:20 >/dev/null 2>&1 || { log "REFUSED: could not recreate the inventory replicas with a 20 s hold lifetime"; exit 2; }
fi
SKU="$(fresh_sku S40 10)"
stock() { sql InventoryDb "SELECT CONCAT(OnHand, '/', Reserved) FROM inventory.StockItem WHERE WarehouseId = 'W1' AND Sku = '$SKU'"; }

# undeterminable <order key suffix> <step> [extra authorize fault mode] [param]: order with a dropped answer and
# Unknown status for <step>; prints the order id
undeterminable() {
  local suffix=$1 step=$2 kind o
  kind="$(tr 'a-z' 'A-Z' <<< "${step:0:1}")${step:1}"
  provider_fault "$PREFIX-$suffix-drop" "kind:$kind" DropResponseAfterCommit null 1 > /dev/null
  [[ -n "${3:-}" ]] && provider_fault "$PREFIX-$suffix-extra" kind:Authorize "$3" "${4:-null}" 1 > /dev/null
  o="$(submit "$ORDER_API" "$PREFIX-$suffix" "$(order_body "$PREFIX-c$suffix" "$SKU" 1)")"
  provider_fault "$PREFIX-$suffix-unknown" "$(nkey "$o"):$step:1" StatusUnknown null null > /dev/null
  printf '%s' "$o"
}
resolve_after() { # resolve_after <order> <suffix> <condition function>
  local o=$1
  wait_until 180 "case for $o" case_open "$o" AmbiguousPayment
  wait_until 150 "pause condition for $o" "$3" "$o"
  provider_clear "$PREFIX-$2-unknown"
  resolve_case "$ORDER_API" "$(open_case_of "$o" AmbiguousPayment)" ConfirmExternalSuccess "$(new_uuid)" > /dev/null
  wait_status 120 "$o" 'Compensated|Completed|Rejected'
}
hold_expired() { [[ "$(holds_of "$1")" == Expired ]]; }

# --- S40 before the capture -----------------------------------------------------------------------------------
A="$(undeterminable a authorize)"
resolve_after "$A" a hold_expired
expect S40-before-capture "Compensated/ReservationExpired; hold Expired (HoldCreated,HoldExpired); authorize:1=Authorized,void:1=Voided; no capture" \
  "end $(end_of "$A"); holds $(holds_of "$A") ($(hold_events_of "$A")); provider $(provider_ops "$A"); cases $(cases_of "$A")" \
  "$([[ "$(end_of "$A")" == Compensated/ReservationExpired && "$(holds_of "$A")" == Expired && "$(hold_events_of "$A")" == HoldCreated,HoldExpired && "$(provider_ops "$A")" == "authorize:1=Authorized,void:1=Voided" ]]; echo $?)"

# --- S40 after the capture ------------------------------------------------------------------------------------
B="$(undeterminable b capture)"
resolve_after "$B" b hold_expired
expect S40-after-capture "Compensated/ReservationExpired; hold extended then Expired (HoldCreated,HoldExtended,HoldExpired); authorized, captured, refunded once; stock 10/0" \
  "end $(end_of "$B"); holds $(holds_of "$B") ($(hold_events_of "$B")); provider $(provider_ops "$B"); stock $(stock)" \
  "$([[ "$(end_of "$B")" == Compensated/ReservationExpired && "$(hold_events_of "$B")" == HoldCreated,HoldExtended,HoldExpired && "$(provider_ops "$B")" == "authorize:1=Authorized,capture:1=Captured,refund:1=Refunded" && "$(stock)" == 10/0 ]]; echo $?)"

# --- S41 --------------------------------------------------------------------------------------------------------
C="$(undeterminable c authorize ExpireAuthorization 10)"
auth_expired() { [[ "$(sql PaymentProviderSimDb "SELECT CASE WHEN AuthorizationExpiresAt < SYSUTCDATETIME() THEN 1 ELSE 0 END FROM provider.Operation WHERE OperationKey = '$(nkey "$1"):authorize:1'")" == 1 ]]; }
resolve_after "$C" c auth_expired
capture="$(sqln PaymentProviderSimDb "SELECT CONCAT(Result, '/', ISNULL(ResultCode, '-')) FROM provider.Operation WHERE OperationKey = '$(nkey "$C"):capture:1'")"
expect S41-authorization "never Captured (no capture sent, or refused); authorization voided; hold Released; Compensated (ReservationExpired or PaymentCaptureFailed)" \
  "capture $capture; provider $(provider_ops "$C") (a|c|v|r $(provider_money "$C")); holds $(holds_of "$C") ($(hold_events_of "$C")); end $(end_of "$C")" \
  "$([[ "$capture" != Captured* && "$(provider_money "$C")" =~ ^1\|0\|[01]\|0$ && "$(holds_of "$C")" == Released && "$(end_of "$C")" =~ ^Compensated/(ReservationExpired|PaymentCaptureFailed)$ ]]; echo $?)"

scenario_finish
