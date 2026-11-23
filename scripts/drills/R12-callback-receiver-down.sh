#!/usr/bin/env bash
# R12 (S28) carrier callbacks survive an unavailable receiver (A) and an unavailable Shipping (B).
# Drill stack: bash scripts/stack-up.sh --drill --replicas
# Each variant's order waits in AwaitingDelivery with the carrier in Manual mode; the carrier's three callbacks
# are scripted to fall due while the outage lasts (about 20 s, inside the carrier's retry budget of about 60 s).
#   A  every order API replica stopped (the callbacks' receivers)
#   B  every shipping-service replica stopped while the order API's ingress keeps answering
#   R12-A-backlog    while the order APIs are down the carrier records failing attempts and keeps the callbacks scheduled
#   R12-A-drained    after the restart every callback was Sent, none dead-lettered; the inbox holds each carrier event
#                    once; Shipping recorded the same events; the order Completed/Delivered with one OrderCompleted
#   R12-B-pending    while Shipping is down the ingress answered 202 and forward rows stay Pending with attempts > 0
#   R12-alert        CarrierForwardBacklogAging fired while Shipping was down and resolved after the restore
#   R12-B-drained    after the restore the forward rows were Processed once each; Shipping recorded the events; the
#                    order Completed/Delivered with one OrderCompleted (by the forwarded signal or, when the outage
#                    outlasts the delivery deadline, by the deadline poll; a late forward then only adds an audit row)
DRILL_ID=R12
. "$(dirname "$0")/lib.sh"
. "$REPO_ROOT/scripts/scenarios/m3-lib.sh"
drill_preflight
APIS=(order-api order-api-2)
SHIPS=(shipping-service shipping-service-2)
bring_back() { local s; for s in "$@"; do [[ "$(docker compose --profile replicas ps --format '{{.State}}' "$s" 2>/dev/null)" == running ]] || start_pair "$s" >/dev/null 2>&1; done; }
on_cleanup "bring_back ${APIS[*]} ${SHIPS[*]}"
trap 'run_cleanup; drill_finish; exit $?' EXIT
RUN="r12-$(date -u +%H%M%S)-$((RANDOM % 1000))"
SKU="$(fresh_sku R12 10)"
SCRIPT='[{"status":"PickedUp","version":1,"delayMs":2000},{"status":"InTransit","version":2,"delayMs":4000},{"status":"Delivered","version":3,"delayMs":6000}]'
inbox_of() { sql OrderDb "SELECT COUNT(DISTINCT EventId) FROM orders.CarrierCallbackInbox WHERE ShipmentId = '$1'"; }
shipping_rows() { sql ShippingDb "SELECT COUNT(*) FROM shipping.CarrierStatusEvent WHERE ShipmentId = '$1'"; }
callbacks() { sqln CarrierSimDb "SELECT STRING_AGG(CONCAT(State, ':', Attempts), ',') WITHIN GROUP (ORDER BY Version) FROM carrier.Callback WHERE ShipmentId = '$1' AND Label = '$DRILL_RUN_ID-$2'"; }
# ended_by <order id> -> the last real transition (status/reason). A terminal callback forwarded after the order already
# completed (the delivery deadline poll can win the race while Shipping is down) adds an audit row with the same from
# and to status (CarrierStatusAfterTerminal); that row records the late forward, not how the order ended.
ended_by() { sql OrderDb "SELECT TOP (1) CONCAT(ToStatus, '/', ReasonCode) FROM orders.OrderStatusHistory WHERE OrderId = '$1' AND FromStatus <> ToStatus ORDER BY Sequence DESC"; }
late_rows() { sql OrderDb "SELECT COUNT(*) FROM orders.OrderStatusHistory WHERE OrderId = '$1' AND ReasonCode = 'CarrierStatusAfterTerminal'"; }
failed_sends() { sql CarrierSimDb "SELECT COUNT(*) FROM carrier.Send s JOIN carrier.Callback c ON c.Id = s.CallbackId WHERE c.ShipmentId = '$1' AND c.Label = '$DRILL_RUN_ID-$2' AND s.ResponseCode IS NULL"; }

prepare() { # prepare <variant> -> order id waiting in AwaitingDelivery, carrier in Manual mode
  local o; o="$(submit "$ORDER_API" "$RUN-$1" "$(order_body "$RUN-c$1" "$SKU" 1)")"
  carrier_mode "$o" Manual > /dev/null
  wait_status 120 "$o" AwaitingDelivery
  echo "$1 $o" >> "$DRILL_DIR/orders.txt"
  printf '%s' "$o"
}

# --- A: receivers down ---------------------------------------------------------------------------
A="$(prepare A)"
INJECTED_AT="$(now_iso)"
docker compose --profile replicas stop -t 5 "${APIS[@]}" >/dev/null 2>&1
carrier_script "$A" "$DRILL_RUN_ID-A" "$SCRIPT" > "$DRILL_DIR/script-A.json"
sleep 20
cb="$(callbacks "$A" A)"; failed="$(failed_sends "$A" A)"
FAULT_OBSERVED="A: callbacks $cb, failed sends $failed"
aexpect R12-A-backlog "order APIs stopped; failing carrier attempts; callbacks still Scheduled" "callbacks $cb; failed sends $failed" \
  "$([[ "$failed" -ge 3 && "$cb" == *Scheduled* && "$cb" != *DeadLettered* ]]; echo $?)"
bring_back "${APIS[@]}"
sent3() { [[ "$(callbacks "$A" A)" =~ ^Sent:[0-9]+,Sent:[0-9]+,Sent:[0-9]+$ ]]; }
wait_until 120 "A callbacks delivered" sent3 || true
wait_status 120 "$A" 'Completed|Compensated|ManualInterventionRequired' || true
aexpect R12-A-drained "3 callbacks Sent, none dead-lettered; inbox 3 events; Shipping 3 events; Completed/Delivered; 1 OrderCompleted" \
  "callbacks $(callbacks "$A" A); inbox $(inbox_of "$A"); shipping $(shipping_rows "$A"); end $(end_of "$A"); OrderCompleted $(completed_events "$A")" \
  "$(sent3 && [[ "$(inbox_of "$A")" == 3 && "$(shipping_rows "$A")" == 3 && "$(end_of "$A")" == Completed/Delivered && "$(completed_events "$A")" == 1 ]]; echo $?)"

# --- B: Shipping down ----------------------------------------------------------------------------
B="$(prepare B)"
ALERT_SINCE="$(date +%s)"; FWD_BEFORE="$(alert_firing CarrierForwardBacklogAging)"
docker compose --profile replicas stop -t 5 "${SHIPS[@]}" >/dev/null 2>&1
carrier_script "$B" "$DRILL_RUN_ID-B" "$SCRIPT" > "$DRILL_DIR/script-B.json"
answered() { [[ "$(callbacks "$B" B)" =~ ^Sent:[0-9]+,Sent:[0-9]+,Sent:[0-9]+$ ]]; }
wait_until 60 "B callbacks answered by the ingress" answered || true
sleep 8
fwd="$(sqln OrderDb "SELECT STRING_AGG(CONCAT(f.State, ':', f.Attempts), ',') FROM orders.CarrierStatusForward f JOIN orders.CarrierCallbackInbox i ON i.EventId = f.EventId WHERE i.ShipmentId = '$B'")"
FAULT_OBSERVED+="; B: callbacks $(callbacks "$B" B), forward $fwd"
aexpect R12-B-pending "ingress answered (callbacks Sent); forward rows Pending with attempts > 0" "callbacks $(callbacks "$B" B); forward $fwd" \
  "$(answered && [[ "$fwd" == *Pending:[1-9]* && "$fwd" != *Processed* ]]; echo $?)"
# Shipping stays down until the forward rows have aged past the backlog threshold and the alert fired.
alert_wait_firing CarrierForwardBacklogAging '' 90 || true
bring_back "${SHIPS[@]}"
processed() { [[ "$(sql OrderDb "SELECT COUNT(*) FROM orders.CarrierStatusForward f JOIN orders.CarrierCallbackInbox i ON i.EventId = f.EventId WHERE i.ShipmentId = '$B' AND f.State = 'Processed'")" == 3 ]]; }
wait_until 120 "B forward rows processed" processed || true
wait_status 120 "$B" 'Completed|Compensated|ManualInterventionRequired' || true
RECOVERED_AT="$(now_iso)"
aexpect R12-B-drained "3 forward rows Processed once; Shipping 3 events; Completed/Delivered; 1 OrderCompleted" \
  "forward $(sqln OrderDb "SELECT STRING_AGG(CONCAT(f.State, ':', f.Outcome), ',') FROM orders.CarrierStatusForward f JOIN orders.CarrierCallbackInbox i ON i.EventId = f.EventId WHERE i.ShipmentId = '$B'"); shipping $(shipping_rows "$B"); ended by $(ended_by "$B"); late terminal forwards $(late_rows "$B"); OrderCompleted $(completed_events "$B")" \
  "$(processed && [[ "$(shipping_rows "$B")" == 3 && "$(ended_by "$B")" == Completed/Delivered && "$(completed_events "$B")" == 1 ]]; echo $?)"
read -r verdict detail <<< "$(alert_outcome CarrierForwardBacklogAging '' "$ALERT_SINCE" "$FWD_BEFORE" 120)"
assert R12-alert "$verdict" "CarrierForwardBacklogAging fires while Shipping is down and resolves after the restore" "$detail"
