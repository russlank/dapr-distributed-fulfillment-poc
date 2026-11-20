#!/usr/bin/env bash
# S13 permanent payment rejection. Drill stack: bash scripts/stack-up.sh --drill --replicas
# The provider declines the authorization (fault Decline on this order's authorize key). The order
# must end Rejected (PaymentDeclined) without a capture, its holds released once, nothing to void.
#   S13-declined   the provider committed one Declined authorization for the order and nothing else
#                  (no capture, no void); the payment service recorded Authorize=Finished/Declined once
#   S13-rejected   OrderDb: Rejected with reason PaymentDeclined, history Submitted, Validating,
#                  Reserving, Rejected; no OrderCompleted event; no intervention case
#   S13-released   InventoryDb: the hold Released (HoldCreated, HoldReleased); stock back where it was
. "$(dirname "$0")/../lib.sh"
. "$(dirname "$0")/m3-lib.sh"
scenario_init S13

SKU="$(fresh_sku S13 10)"
LABEL="$PREFIX"
provider_fault "$LABEL" kind:Authorize Decline null 1 > "$OUT_DIR/fault.json"
trap 'provider_clear "$LABEL"' EXIT
order="$(submit "$ORDER_API" "$PREFIX-1" "$(order_body "$PREFIX-c1" "$SKU" 2)")"
echo "$order" > "$OUT_DIR/order.txt"
wait_status 120 "$order" 'Rejected|Compensated|Completed|ManualInterventionRequired'

ops="$(provider_ops "$order")"; pay="$(payment_ops "$order")"
expect S13-declined "provider authorize:1=Declined only; payment service Authorize=Finished/Declined" "provider $ops; payment $pay" \
  "$([[ "$ops" == "authorize:1=Declined" && "$pay" == "Authorize=Finished/Declined" ]]; echo $?)"

end="$(end_of "$order")"; history="$(history_of "$order")"; events="$(completed_events "$order")"; cases="$(cases_of "$order")"
expect S13-rejected "Rejected/PaymentDeclined; Submitted,Validating,Reserving,Rejected; no completion event, no case" \
  "end $end; history $history; completion events $events; cases '${cases}'" \
  "$([[ "$end" == Rejected/PaymentDeclined && "$history" == Submitted,Validating,Reserving,Rejected && "$events" == 0 && -z "$cases" ]]; echo $?)"

holds="$(holds_of "$order")"; hev="$(hold_events_of "$order")"; stock="$(sql InventoryDb "SELECT CONCAT(OnHand, '/', Reserved) FROM inventory.StockItem WHERE WarehouseId = 'W1' AND Sku = '$SKU'")"
expect S13-released "hold Released once (HoldCreated,HoldReleased); stock 10/0" "holds $holds; events $hev; stock $stock" \
  "$([[ "$holds" == Released && "$hev" == HoldCreated,HoldReleased && "$stock" == 10/0 ]]; echo $?)"

scenario_finish
