#!/usr/bin/env bash
# R13 (S10 carrier part, S27, S43 carrier part) duplicate, early, late, conflicting, and wrong carrier
# callbacks are recorded by Shipping and harmless. Drill stack: bash scripts/stack-up.sh --drill --replicas
# The order's shipment is put in manual mode before dispatch (no automatic callbacks); once the order
# waits for delivery, the carrier sends a script, in this order:
#   1-3  PickedUp v1 three times with the same event id          (duplicates)
#   4    Delivered v3                                            (early: before InTransit)
#   5    InTransit v2                                            (late: after Delivered)
#   6    Exception v4                                            (conflicting terminal after Delivered)
#   7    the event id of 4 with another payload (InTransit v3)   (payload conflict)
#   8    PickedUp v1 naming another order                        (correlation mismatch)
#   9    PickedUp v1 with a bad signature
#   R13-responses  the carrier's send log: 202 for 1-6 and 8, 409 for 7, 401 for 9
#   R13-inbox      OrderDb: one inbox row per valid unique event id (1, 4, 5, 6, 8), the duplicate counted
#                  twice on the first, one conflict audit row (ingress) for 7
#   R13-shipping   Shipping recorded every forwarded event with its outcome: PickedUp Applied, Delivered
#                  TerminalApplied, InTransit Stale, Exception TerminalConflict, the wrong order
#                  CorrelationMismatch; TerminalStatus stays Delivered
#   R13-once       the order Completed once on Delivered (one OrderCompleted event); one
#                  CarrierTerminalConflict case, acknowledged record-only; no workflow started for it
DRILL_ID=R13
. "$(dirname "$0")/lib.sh"
. "$REPO_ROOT/scripts/scenarios/m3-lib.sh"
drill_preflight
trap 'drill_finish; exit $?' EXIT
RUN="r13-$(date -u +%H%M%S)-$((RANDOM % 1000))"
SKU="$(fresh_sku R13 10)"

O="$(submit "$ORDER_API" "$RUN-1" "$(order_body "$RUN-c1" "$SKU" 1)")"
carrier_mode "$O" Manual > "$DRILL_DIR/mode.json"
echo "$O" > "$DRILL_DIR/order.txt"
wait_status 120 "$O" AwaitingDelivery
OTHER="$(new_uuid)"
INJECTED_AT="$(now_iso)"
script='[
 {"status":"PickedUp","version":1,"delayMs":0},
 {"status":"PickedUp","version":1,"delayMs":400,"sameEventIdAs":0},
 {"status":"PickedUp","version":1,"delayMs":800,"sameEventIdAs":0},
 {"status":"Delivered","version":3,"delayMs":1600},
 {"status":"InTransit","version":2,"delayMs":4000},
 {"status":"Exception","version":4,"delayMs":5000},
 {"status":"InTransit","version":3,"delayMs":6000,"sameEventIdAs":3},
 {"status":"PickedUp","version":1,"delayMs":7000,"orderIdOverride":"'"$OTHER"'"},
 {"status":"PickedUp","version":1,"delayMs":8000,"badSignature":true}]'
ids="$(carrier_script "$O" "$DRILL_RUN_ID" "$script")"
echo "$ids" > "$DRILL_DIR/script-event-ids.json"
all_sent() { [[ "$(sql CarrierSimDb "SELECT COUNT(*) FROM carrier.Callback WHERE ShipmentId = '$O' AND Label = '$DRILL_RUN_ID' AND State IN ('Sent', 'DeadLettered')")" == 9 ]]; }
wait_until 90 "all 9 scripted callbacks sent" all_sent
forwarded() { [[ "$(sql OrderDb "SELECT COUNT(*) FROM orders.CarrierStatusForward f JOIN orders.CarrierCallbackInbox i ON i.EventId = f.EventId WHERE i.ShipmentId = '$O' AND f.State = 'Pending'")" == 0 ]]; }
wait_until 60 "forward queue drained" forwarded
wait_status 60 "$O" 'Completed'
case_ready() { [[ -n "$(open_case_of "$O" CarrierTerminalConflict)" ]]; }
wait_until 60 "CarrierTerminalConflict case" case_ready
FAULT_OBSERVED="9 scripted callbacks"

codes="$(sql CarrierSimDb "SELECT STRING_AGG(CONCAT(c.Status, ':', ISNULL(CONVERT(varchar(5), s.ResponseCode), '-')), ',') WITHIN GROUP (ORDER BY c.DueAt, c.Id)
  FROM carrier.Callback c JOIN carrier.Send s ON s.CallbackId = c.Id WHERE c.ShipmentId = '$O' AND c.Label = '$DRILL_RUN_ID'")"
aexpect R13-responses "202 x3 PickedUp, 202 Delivered, 202 InTransit, 202 Exception, 409 payload conflict, 202 wrong order, 401 bad signature" "$codes" \
  "$([[ "$codes" == "PickedUp:202,PickedUp:202,PickedUp:202,Delivered:202,InTransit:202,Exception:202,InTransit:409,PickedUp:202,PickedUp:401" ]]; echo $?)"

inbox="$(sql OrderDb "SELECT CONCAT((SELECT COUNT(*) FROM orders.CarrierCallbackInbox WHERE ShipmentId = '$O'), '|',
  (SELECT MAX(Duplicates) FROM orders.CarrierCallbackInbox WHERE ShipmentId = '$O' AND Status = 'PickedUp' AND OrderId = '$O'), '|',
  (SELECT COUNT(*) FROM orders.CarrierCallbackConflict x JOIN orders.CarrierCallbackInbox i ON i.EventId = x.EventId WHERE i.ShipmentId = '$O' AND x.DetectedBy = 'ingress'))")"
aexpect R13-inbox "inbox rows 5; the duplicate counted 2; one ingress conflict row" "rows|duplicates|conflicts $inbox" \
  "$([[ "$inbox" == 5\|2\|1 ]]; echo $?)"

events="$(sqln ShippingDb "SELECT STRING_AGG(CONCAT(Status, ':', Version, '=', Outcome), ',') WITHIN GROUP (ORDER BY Status, Version, Outcome) FROM shipping.CarrierStatusEvent WHERE ShipmentId = '$O'")"
aexpect R13-shipping "Delivered:3=TerminalApplied, Exception:4=TerminalConflict, InTransit:2=Stale, PickedUp:1=Applied, PickedUp:1=CorrelationMismatch; terminal Delivered" \
  "events $events; shipment $(shipment_of "$O")" \
  "$([[ "$events" == "Delivered:3=TerminalApplied,Exception:4=TerminalConflict,InTransit:2=Stale,PickedUp:1=Applied,PickedUp:1=CorrelationMismatch" && "$(shipment_of "$O")" == *\|Delivered ]]; echo $?)"

case_id="$(open_case_of "$O" CarrierTerminalConflict)"
read -r rs rp <<< "$(resolve_case "$ORDER_API" "$case_id" Acknowledge "$(new_uuid)")"
RECOVERED_AT="$(now_iso)"
starts="$(sql OrderDb "SELECT CONCAT((SELECT COUNT(*) FROM orders.WorkflowStartRequest WHERE OrderId = '$O'), '|', (SELECT COUNT(*) FROM orders.RepairStartRequest WHERE OrderId = '$O'), '|',
  (SELECT COUNT(*) FROM orders.PendingSignal WHERE OrderId = '$O' AND EventName LIKE 'InterventionResolved%'))")"
aexpect R13-once "Completed/... once with one OrderCompleted event; one case, Acknowledge -> RecordOnly; start requests|repairs|case signals 1|0|0" \
  "end $(end_of "$O"); completion $(completed_events "$O"); history $(history_of "$O"); cases $(cases_of "$O"); resolution $rs $rp; starts $starts" \
  "$([[ "$(order_status "$O")" == Completed && "$(completed_events "$O")" == 1 && "$(grep -o Completed <<< "$(history_of "$O")" | wc -l)" -ge 1 && "$(cases_of "$O")" == CarrierTerminalConflict:Resolved:RecordOnly && "$rs $rp" == "200 RecordOnly" && "$starts" == 1\|0\|0 ]]; echo $?)"
