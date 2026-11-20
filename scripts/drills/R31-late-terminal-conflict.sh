#!/usr/bin/env bash
# R31 (S47) a conflicting terminal carrier status after completion reaches Shipping and becomes one case,
# also when the forwarder crashes. Drill stack with both order API replicas:
#   bash scripts/stack-up.sh --drill --replicas
# Two orders complete normally (Delivered v3). Then the carrier sends Exception v4 with a new event id.
#   1  plain: forwarded once
#   2  crash: the forwarding order API replica ends (failpoint AfterForwardResponseBeforeMark, FailFast)
#      after Shipping committed and answered, before the OrderDb transaction; the forward row stays
#      Pending, its lease expires, and a forwarder retries: Shipping answers its stored outcome.
#   R31-<v>-before   the order Completed, its workflow Completed, start request Closed, before the callback
#   R31-<v>-recorded the carrier got 202; one inbox row for the event; Shipping holds one event row with
#                    TerminalConflict, Shipment.TerminalStatus still Delivered
#   R31-<v>-case     forward row Processed (TerminalConflict); one CarrierTerminalConflict case; no new
#                    signal; no workflow started; the order stays Completed with a PostTerminalCase row;
#                    Acknowledge resolves it RecordOnly
#   R31-2-crash      the failpoint fired, a replica exited, and the event was forwarded again (Shipping replay)
DRILL_ID=R31
. "$(dirname "$0")/lib.sh"
. "$REPO_ROOT/scripts/scenarios/m3-lib.sh"
. "$REPO_ROOT/scripts/scenarios/replica-lib.sh"
drill_preflight
require_replicas
APIS=(order-api order-api-2)
state_of() { docker compose --profile replicas ps -a --format '{{.State}}' "$1" 2>/dev/null; }
restart_dead() { local s; for s in "${APIS[@]}"; do [[ "$(state_of "$s")" == running ]] || start_pair "$s"; done; }
trap 'restart_dead; drill_finish; exit $?' EXIT
RUN="r31-$(date -u +%H%M%S)-$((RANDOM % 1000))"
SKU="$(fresh_sku R31 10)"

variant() { # variant <n> <crash: yes|no>
  local v=$1 crash=$2 o
  o="$(submit "$ORDER_API" "$RUN-$v" "$(order_body "$RUN-c$v" "$SKU" 1)")"
  echo "$v $o" >> "$DRILL_DIR/orders.txt"
  wait_status 120 "$o" 'Completed|Compensated|Rejected|ManualInterventionRequired'
  sleep 2
  local wf before
  wf="$(workflow_of "$o")"
  before="$(sql OrderDb "SELECT CONCAT(o.Status, '|', (SELECT State FROM orders.WorkflowStartRequest WHERE OrderId = o.OrderId), '|',
    (SELECT COUNT(*) FROM orders.PendingSignal WHERE OrderId = o.OrderId)) FROM orders.[Order] o WHERE o.OrderId = '$o'")"
  aexpect "R31-$v-before" "Completed, workflow Completed, start request Closed" "order|start|signals $before; workflow $wf; shipment $(shipment_of "$o")" \
    "$([[ "$before" == Completed\|Closed\|* && "$wf" == Completed/* && "$(shipment_of "$o")" == *\|Delivered ]]; echo $?)"
  local signals_before="${before##*|}"

  [[ "$crash" == yes ]] && arm_failpoint OrderDb AfterForwardResponseBeforeMark '*' FailFast
  [[ -z "$INJECTED_AT" ]] && INJECTED_AT="$(now_iso)"
  local ids event
  ids="$(carrier_script "$o" "$DRILL_RUN_ID-$v" '[{"status":"Exception","version":4,"delayMs":0}]')"
  event="$(json_get "$ids" eventIds.0)"
  if [[ "$crash" == yes ]]; then
    local dead=""
    any_dead() { local s; for s in "${APIS[@]}"; do [[ "$(state_of "$s")" != running ]] && return 0; done; return 1; }
    wait_until 60 "a forwarding replica to exit" any_dead
    for s in "${APIS[@]}"; do [[ "$(state_of "$s")" != running ]] && dead="$s"; done
    local fired; fired="$(failpoint_fired_at OrderDb AfterForwardResponseBeforeMark)"
    local pending; pending="$(sql OrderDb "SELECT State FROM orders.CarrierStatusForward WHERE EventId = '$event'")"
    FAULT_OBSERVED="FiredAt $fired; exited $dead; forward row $pending after the crash"
    restart_dead
  fi
  processed() { [[ "$(sql OrderDb "SELECT State FROM orders.CarrierStatusForward WHERE EventId = '$event'")" == Processed ]]; }
  wait_until 120 "forward of $event processed" processed
  case_ready() { [[ -n "$(open_case_of "$o" CarrierTerminalConflict)" ]]; }
  wait_until 30 "case open" case_ready

  local code inbox ship
  code="$(sql CarrierSimDb "SELECT STRING_AGG(CONVERT(varchar(5), s.ResponseCode), ',') FROM carrier.Send s WHERE s.EventId = '$event'")"
  inbox="$(sql OrderDb "SELECT COUNT(*) FROM orders.CarrierCallbackInbox WHERE EventId = '$event'")"
  ship="$(sql ShippingDb "SELECT CONCAT(COUNT(*), '|', MAX(Outcome), '|', MAX(Replays)) FROM shipping.CarrierStatusEvent WHERE EventId = '$event'")"
  aexpect "R31-$v-recorded" "carrier 202; one inbox row; Shipping one row TerminalConflict; terminal still Delivered" \
    "carrier responses $code; inbox $inbox; Shipping rows|outcome|replays $ship; shipment $(shipment_of "$o")" \
    "$([[ "$code" == 202 && "$inbox" == 1 && "$ship" == 1\|TerminalConflict\|* && "$(shipment_of "$o")" == *\|Delivered ]]; echo $?)"

  local fwd signals starts history rs rp
  fwd="$(sql OrderDb "SELECT CONCAT(State, '|', Outcome, '|', Attempts) FROM orders.CarrierStatusForward WHERE EventId = '$event'")"
  signals="$(sql OrderDb "SELECT COUNT(*) FROM orders.PendingSignal WHERE OrderId = '$o'")"
  starts="$(sql OrderDb "SELECT CONCAT((SELECT COUNT(*) FROM orders.WorkflowStartRequest WHERE OrderId = '$o'), '|', (SELECT COUNT(*) FROM orders.RepairStartRequest WHERE OrderId = '$o'))")"
  history="$(sql OrderDb "SELECT TOP (1) CONCAT(ToStatus, '/', ReasonCode) FROM orders.OrderStatusHistory WHERE OrderId = '$o' ORDER BY Sequence DESC")"
  read -r rs rp <<< "$(resolve_case "$ORDER_API" "$(open_case_of "$o" CarrierTerminalConflict)" Acknowledge "$(new_uuid)")"
  aexpect "R31-$v-case" "forward Processed/TerminalConflict; one case; no new signal; start requests|repairs 1|0; Completed with PostTerminalCase; Acknowledge -> RecordOnly" \
    "forward $fwd; cases $(cases_of "$o"); signals $signals_before -> $signals; starts $starts; last history $history; resolution $rs $rp; status $(order_status "$o")" \
    "$([[ "$fwd" == Processed\|TerminalConflict\|* && "$(cases_of "$o")" == CarrierTerminalConflict:Resolved:RecordOnly && "$signals" == "$signals_before" && "$starts" == 1\|0 && "$history" == Completed/PostTerminalCase && "$rs $rp" == "200 RecordOnly" && "$(order_status "$o")" == Completed ]]; echo $?)"

  if [[ "$crash" == yes ]]; then
    aexpect R31-2-crash "failpoint fired; a replica exited; the forward row stayed Pending; forwarded again (attempts >= 2, Shipping replay >= 1)" \
      "$FAULT_OBSERVED; forward $fwd; Shipping $ship" \
      "$([[ -n "$fired" && -n "$dead" && "$pending" == Pending && "${fwd##*|}" -ge 2 && "${ship##*|}" -ge 1 ]]; echo $?)"
  fi
}

variant 1 no
variant 2 yes
RECOVERED_AT="$(now_iso)"
