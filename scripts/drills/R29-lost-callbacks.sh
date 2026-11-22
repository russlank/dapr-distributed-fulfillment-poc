#!/usr/bin/env bash
# R29 (S42) lost carrier callbacks do not leave the workflow waiting forever.
# Drill stack: bash scripts/stack-up.sh --drill --replicas
#   1  carrier test mode Lost: every callback is dropped until it is dead-lettered; at the delivery deadline the
#      workflow asks the carrier once (poll) and completes the order from that answer
#   2  mode LostNoPoll: the poll fails too, so the workflow opens a DeliveryOverdue case; the drill resolves it
#      (ConfirmExternalSuccess, as after a check with the carrier by other means) and the order completes
#   3  mode LostNoPoll again, but the failure is confirmed (ConfirmExternalFailure): the automated lifecycle ends
#      Escalated while the case stays open (Escalated) until the drill records the business resolution (Acknowledge)
#   R29-1-lost       every callback of order 1 was attempted without an answer and ends DeadLettered
#   R29-1-polled     order 1 Completed/Delivered; Shipping's terminal event for it came from source poll
#   R29-2-overdue    order 2 waits in ManualInterventionRequired with a DeliveryOverdue case, no terminal status in
#                    Shipping
#   R29-2-resolved   resolution 200 Signal; the order Completed/Delivered with one OrderCompleted event
#   R29-3-escalated  resolution 200 Escalate; the order Escalated/DeliveryFailed, its workflow completed at the fence,
#                    start request Closed, the case Escalated; payment and stock untouched (one authorization, one
#                    capture, no void or refund; holds Consumed); no OrderCompleted event
#   R29-3-settled    Acknowledge with the business resolution: 200 RecordOnly; the case Resolved; the order stays
#                    Escalated; payment and stock still untouched
DRILL_ID=R29
. "$(dirname "$0")/lib.sh"
. "$REPO_ROOT/scripts/scenarios/m3-lib.sh"
drill_preflight
trap 'drill_finish; exit $?' EXIT
RUN="r29-$(date -u +%H%M%S)-$((RANDOM % 1000))"
SKU="$(fresh_sku R29 10)"

o1="$(submit "$ORDER_API" "$RUN-1" "$(order_body "$RUN-c1" "$SKU" 1)")"; carrier_mode "$o1" Lost > /dev/null
o2="$(submit "$ORDER_API" "$RUN-2" "$(order_body "$RUN-c2" "$SKU" 1)")"; carrier_mode "$o2" LostNoPoll > /dev/null
o3="$(submit "$ORDER_API" "$RUN-3" "$(order_body "$RUN-c3" "$SKU" 1)")"; carrier_mode "$o3" LostNoPoll > /dev/null
printf '1 %s\n2 %s\n3 %s\n' "$o1" "$o2" "$o3" > "$DRILL_DIR/orders.txt"
INJECTED_AT="$(now_iso)"
callbacks() { sqln CarrierSimDb "SELECT STRING_AGG(CONCAT(Status, ':', State), ',') WITHIN GROUP (ORDER BY Version) FROM carrier.Callback WHERE ShipmentId = '$1'"; }
answered() { sql CarrierSimDb "SELECT COUNT(*) FROM carrier.Send s JOIN carrier.Callback c ON c.Id = s.CallbackId WHERE c.ShipmentId = '$1' AND s.ResponseCode IS NOT NULL"; }

wait_status 180 "$o1" 'Completed|Compensated|ManualInterventionRequired' || true
dead() { [[ "$(callbacks "$o1")" =~ ^PickedUp:DeadLettered,InTransit:DeadLettered,Delivered:DeadLettered$ ]]; }
wait_until 120 "order 1 callbacks dead-lettered" dead || true
FAULT_OBSERVED="1: callbacks $(callbacks "$o1"), answered sends $(answered "$o1")"
aexpect R29-1-lost "3 callbacks dead-lettered, no send answered" "callbacks $(callbacks "$o1"); answered sends $(answered "$o1")" \
  "$(dead && [[ "$(answered "$o1")" == 0 ]]; echo $?)"
terminal_source="$(sql ShippingDb "SELECT TOP (1) CONCAT(Status, '/', Source, '/', Outcome) FROM shipping.CarrierStatusEvent WHERE OrderId = '$o1' AND Outcome = 'TerminalApplied'")"
aexpect R29-1-polled "order 1 Completed/Delivered; terminal event Delivered from source poll" "end $(end_of "$o1"); terminal $terminal_source; shipment $(shipment_of "$o1")" \
  "$([[ "$(end_of "$o1")" == Completed/Delivered && "$terminal_source" == Delivered/poll/TerminalApplied ]]; echo $?)"

overdue() { case_open "$o2" DeliveryOverdue; }
wait_until 120 "order 2 DeliveryOverdue case" overdue || true
aexpect R29-2-overdue "order 2 ManualInterventionRequired with an open DeliveryOverdue case; Shipping has no terminal status" \
  "status $(order_status "$o2"); cases $(cases_of "$o2"); shipment $(shipment_of "$o2")" \
  "$(overdue && [[ "$(order_status "$o2")" == ManualInterventionRequired && "$(shipment_of "$o2")" == *"|-" ]]; echo $?)"
res="$(resolve_case "$ORDER_API" "$(open_case_of "$o2" DeliveryOverdue)" ConfirmExternalSuccess "$(new_uuid)")"
wait_status 120 "$o2" 'Completed|Compensated|Rejected' || true
RECOVERED_AT="$(now_iso)"
aexpect R29-2-resolved "resolution 200 Signal; Completed with one OrderCompleted event" \
  "resolution $res; end $(end_of "$o2"); OrderCompleted $(completed_events "$o2"); cases $(cases_of "$o2")" \
  "$([[ "$res" == "200 Signal" && "$(order_status "$o2")" == Completed && "$(completed_events "$o2")" == 1 ]]; echo $?)"

# 3: a confirmed failure ends the automated lifecycle; the case stays open for the business resolution.
overdue3() { case_open "$o3" DeliveryOverdue; }
wait_until 120 "order 3 DeliveryOverdue case" overdue3 || true
case3="$(open_case_of "$o3" DeliveryOverdue)"
untouched() { [[ "$(provider_money "$o3")" == "1|1|0|0" && "$(holds_of "$o3")" == Consumed ]]; }
res3="$(resolve_case "$ORDER_API" "$case3" ConfirmExternalFailure "$(new_uuid)")"
fenced() { [[ "$(workflow_of "$o3")" == Completed/* ]]; }
wait_until 60 "order 3 workflow ended" fenced || true
start3="$(sql OrderDb "SELECT State FROM orders.WorkflowStartRequest WHERE OrderId = '$o3'")"
aexpect R29-3-escalated "200 Escalate; Escalated/DeliveryFailed; workflow completed; start request Closed; case Escalated; money 1|1|0|0, holds Consumed; no OrderCompleted" \
  "resolution $res3; end $(end_of "$o3"); workflow $(workflow_of "$o3"); start $start3; cases $(cases_of "$o3"); money $(provider_money "$o3"); holds $(holds_of "$o3"); OrderCompleted $(completed_events "$o3")" \
  "$([[ "$res3" == "200 Escalate" && "$(end_of "$o3")" == Escalated/DeliveryFailed && "$start3" == Closed && "$(cases_of "$o3")" == DeliveryOverdue:Escalated:- \
       && "$(completed_events "$o3")" == 0 ]] && fenced && untouched; echo $?)"
settle3="$(resolve_case "$ORDER_API" "$case3" Acknowledge "$(new_uuid)" operator-2)"
RECOVERED_AT="$(now_iso)"
aexpect R29-3-settled "200 RecordOnly; case Resolved; order stays Escalated; money and stock untouched" \
  "resolution $settle3; end $(end_of "$o3"); cases $(cases_of "$o3"); money $(provider_money "$o3"); holds $(holds_of "$o3")" \
  "$([[ "$settle3" == "200 RecordOnly" && "$(order_status "$o3")" == Escalated && "$(cases_of "$o3")" == DeliveryOverdue:Resolved:RecordOnly ]] && untouched; echo $?)"
