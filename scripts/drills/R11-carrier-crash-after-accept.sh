#!/usr/bin/env bash
# R11 (S25, S26) a carrier crash after acceptance neither loses nor duplicates the shipment.
# Drill stack: bash scripts/stack-up.sh --drill --replicas
# The carrier's failpoint AfterCarrierAcceptCommitBeforeResponse (FailFast) ends the carrier process after
# it committed the shipment and its callback schedule, before it answered. Shipping cannot know whether
# the request reached the carrier, so it sends the same shipment id again (S25: carrier request
# redelivery). The carrier is restarted at once. Either a resend within Shipping's budget gets the stored
# acceptance back, or the budget runs out first and an AmbiguousDispatch case opens; the operator then
# confirms with the carrier's own record, and the workflow dispatches again with the same id.
#   R11-fired      the failpoint row has FiredAt; the carrier process exited (S26)
#   R11-once       the carrier holds exactly one shipment for the order (a later request was a replay);
#                  Shipping's attempt log shows the unanswered first send
#   R11-delivered  the callbacks scheduled before the crash were sent after the restart and applied by
#                  Shipping; the order Completed (Delivered), one OrderCompleted event
DRILL_ID=R11
. "$(dirname "$0")/lib.sh"
. "$REPO_ROOT/scripts/scenarios/m3-lib.sh"
drill_preflight
CAR=carrier-simulator
carrier_ready() { [[ "$(curl -s -m 5 -o /dev/null -w '%{http_code}' "$CARRIER/health/ready")" == 200 ]]; }
carrier_up() {
  [[ "$(docker compose ps --format '{{.State}}' "$CAR" 2>/dev/null)" == running ]] || docker compose up -d --no-build --no-deps "$CAR" >/dev/null 2>&1
  wait_until 90 "carrier ready" carrier_ready
}
trap 'carrier_up; drill_finish; exit $?' EXIT
RUN="r11-$(date -u +%H%M%S)-$((RANDOM % 1000))"
SKU="$(fresh_sku R11 10)"

arm_failpoint CarrierSimDb AfterCarrierAcceptCommitBeforeResponse '*' FailFast
INJECTED_AT="$(now_iso)"
O="$(submit "$ORDER_API" "$RUN-1" "$(order_body "$RUN-c1" "$SKU" 1)")"
echo "$O" > "$DRILL_DIR/order.txt"
carrier_down() { [[ "$(docker compose ps -a --format '{{.State}}' "$CAR")" != running ]]; }
wait_until 120 "carrier crashed" carrier_down
exit_code="$(docker inspect -f '{{.State.ExitCode}}' "$(docker compose ps -a -q "$CAR")")"
fired="$(failpoint_fired_at CarrierSimDb AfterCarrierAcceptCommitBeforeResponse)"
FAULT_OBSERVED="FiredAt $fired; carrier exit $exit_code"
carrier_up
aexpect R11-fired "FiredAt set; carrier exited" "FiredAt '$fired'; exit code $exit_code" "$([[ -n "$fired" && "$exit_code" != 0 ]]; echo $?)"

settled() { status_is "$O" 'Completed|Compensated|Rejected' || case_open "$O" AmbiguousDispatch; }
wait_until 180 "order completed or waiting in a dispatch case" settled
resolution="-"
if case_open "$O" AmbiguousDispatch; then
  record_at_carrier="$(curl -s -m 10 "$CARRIER/carrier/shipments/$O" -H "X-Api-Key: $CARRIER_API_KEY")"
  echo "carrier record: $record_at_carrier" >> "$DRILL_DIR/evidence.txt"
  read -r rs rp <<< "$(resolve_case "$ORDER_API" "$(open_case_of "$O" AmbiguousDispatch)" ConfirmExternalSuccess "$(new_uuid)")"
  resolution="$rs $rp (carrier record: $(json_get "$record_at_carrier" shipmentId))"
fi
wait_status 180 "$O" 'Completed|Compensated|Rejected'
RECOVERED_AT="$(now_iso)"

carrier_row="$(sql CarrierSimDb "SELECT CONCAT(COUNT(*), '|', ISNULL(MAX(Replays), 0)) FROM carrier.Shipment WHERE OrderId = '$O'")"
attempts="$(sqln ShippingDb "SELECT STRING_AGG(CONCAT(Answer, '@', ISNULL(CONVERT(varchar(5), HttpStatus), '-')), ',') WITHIN GROUP (ORDER BY Id) FROM shipping.DispatchAttempt WHERE ShipmentId = '$O'")"
aexpect R11-once "one carrier shipment (later requests replays); first send unanswered" \
  "carrier shipments|replays $carrier_row; Shipping attempts $attempts; case resolution $resolution" \
  "$([[ "$carrier_row" == 1\|* && "${carrier_row#1|}" -ge 1 && "$attempts" != [Aa]ccepted* ]]; echo $?)"

sends="$(carrier_sends "$O" | python -c "import json,sys; print(','.join(f\"{s['status']}:{s['state']}:{s.get('responseCode')}\" for s in json.load(sys.stdin)['sends']))" 2>/dev/null)"
aexpect R11-delivered "callbacks sent (202) and applied (Delivered terminal in Shipping); Completed/Delivered; one OrderCompleted event" \
  "carrier sends $sends; Shipping $(shipment_of "$O"); events $(carrier_events_of "$O"); end $(end_of "$O"); completion $(completed_events "$O")" \
  "$([[ "$(shipment_of "$O")" == *Delivered && "$(end_of "$O")" == Completed/Delivered && "$(completed_events "$O")" == 1 ]]; echo $?)"
