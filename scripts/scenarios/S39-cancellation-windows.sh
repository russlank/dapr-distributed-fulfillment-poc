#!/usr/bin/env bash
# S39 cancellation in each window. Drill stack: bash scripts/stack-up.sh --drill --replicas
# (The race at the point of no return itself is drill R27.)
#   S39-review       cancelled while waiting for a review: 202; the order Cancelled, the round Cancelled,
#                    nothing reserved or paid; a decision afterwards gets 409 review-cancelled
#   S39-authorized   cancelled after the authorization committed, before dispatch (the provider holds its
#                    answer 1.5 s): 202; the order Cancelled (CancellationRequested) at the point of no
#                    return; the authorization voided by its key; holds released; no shipment
#   S39-repeat       a repeated cancellation of a cancelled order: 200
#   S39-dispatched   cancelled once the dispatch started (manual carrier: the order waits in
#                    AwaitingDelivery): 409 dispatch-started; the order then completes normally
#   S39-terminal     cancelled after completion: 409 dispatch-started (the point of no return is checked
#                    first); a terminal order that never dispatched (payment declined): 409 order-terminal;
#                    unknown order: 404
. "$(dirname "$0")/../lib.sh"
. "$(dirname "$0")/m3-lib.sh"
scenario_init S39
trap 'provider_clear "$PREFIX"' EXIT
SKU="$(fresh_sku S39 20)"

# --- during the review ---------------------------------------------------------------------------------
R="$(submit "$ORDER_API" "$PREFIX-r" "$(review_order_body "$PREFIX-cr")")"
wait_status 90 "$R" AwaitingApproval
review_open() { [[ -n "$(sqln OrderDb "SELECT ReviewId FROM orders.ReviewTask WHERE OrderId = '$R' AND Status = 'Pending'")" ]]; }
wait_until 30 "review open" review_open
review="$(sql OrderDb "SELECT ReviewId FROM orders.ReviewTask WHERE OrderId = '$R' AND Status = 'Pending'")"
read -r cs cc <<< "$(cancel_order "$ORDER_API_2" "$R")"
wait_status 60 "$R" 'Cancelled|Rejected|Completed'
read -r ds dbody <<< "$(decide "$ORDER_API" "$review" approve)"
expect S39-review "202; Cancelled; round Cancelled; no hold, no provider operation; late decision 409 review-cancelled" \
  "cancel $cs $cc; end $(end_of "$R"); round $(sql OrderDb "SELECT Status FROM orders.ReviewTask WHERE ReviewId = '$review'"); holds '$(holds_of "$R")'; provider '$(provider_ops "$R")'; decision $ds $(json_get "$dbody" code)" \
  "$([[ "$cs" == 202 && "$(order_status "$R")" == Cancelled && "$(sql OrderDb "SELECT Status FROM orders.ReviewTask WHERE ReviewId = '$review'")" == Cancelled && -z "$(holds_of "$R")" && -z "$(provider_ops "$R")" && "$ds" == 409 && "$(json_get "$dbody" code)" == review-cancelled ]]; echo $?)"

# --- after the authorization, before dispatch ------------------------------------------------------------
provider_fault "$PREFIX-a" kind:Authorize DelayResponse 1500 1 > /dev/null
A="$(submit "$ORDER_API" "$PREFIX-a" "$(order_body "$PREFIX-ca" "$SKU" 1)")"
authorized() { [[ "$(sql PaymentProviderSimDb "SELECT COUNT(*) FROM provider.Operation WHERE OperationKey = '$(nkey "$A"):authorize:1'")" == 1 ]]; }
wait_until 60 "authorization committed" authorized
read -r as ac <<< "$(cancel_order "$ORDER_API" "$A")"
wait_status 90 "$A" 'Cancelled|Completed|Compensated'
expect S39-authorized "202; Cancelled/CancellationRequested; authorize:1=Authorized,void:1=Voided; hold Released; no shipment, never DispatchStartedAt" \
  "cancel $as $ac; end $(end_of "$A"); provider $(provider_ops "$A"); holds $(holds_of "$A"); shipment '$(shipment_of "$A")'; dispatch started '$(sqln OrderDb "SELECT DispatchStartedAt FROM orders.[Order] WHERE OrderId = '$A'")'" \
  "$([[ "$as" == 202 && "$(end_of "$A")" == Cancelled/CancellationRequested && "$(provider_ops "$A")" == "authorize:1=Authorized,void:1=Voided" && "$(holds_of "$A")" == Released && -z "$(shipment_of "$A")" && -z "$(sqln OrderDb "SELECT DispatchStartedAt FROM orders.[Order] WHERE OrderId = '$A'")" ]]; echo $?)"

read -r rs rc <<< "$(cancel_order "$ORDER_API_2" "$A")"
expect S39-repeat "200 for a cancelled order" "repeat $rs $rc" "$([[ "$rs" == 200 ]]; echo $?)"

# --- after the dispatch started ----------------------------------------------------------------------------
D="$(submit "$ORDER_API" "$PREFIX-d" "$(order_body "$PREFIX-cd" "$SKU" 1)")"
carrier_mode "$D" Manual > /dev/null
wait_status 120 "$D" AwaitingDelivery
read -r xs xc <<< "$(cancel_order "$ORDER_API" "$D")"
carrier_script "$D" "$PREFIX" '[{"status":"PickedUp","version":1,"delayMs":0},{"status":"Delivered","version":2,"delayMs":500}]' > /dev/null
wait_status 90 "$D" 'Completed|Cancelled'
expect S39-dispatched "409 dispatch-started; then Completed/Delivered with no cancellation recorded" \
  "cancel $xs $xc; end $(end_of "$D"); cancel requested '$(sqln OrderDb "SELECT CancelRequestedAt FROM orders.[Order] WHERE OrderId = '$D'")'" \
  "$([[ "$xs" == 409 && "$xc" == dispatch-started && "$(end_of "$D")" == Completed/Delivered && -z "$(sqln OrderDb "SELECT CancelRequestedAt FROM orders.[Order] WHERE OrderId = '$D'")" ]]; echo $?)"

# --- after completion, unknown order -----------------------------------------------------------------------
provider_fault "$PREFIX-x" kind:Authorize Decline null 1 > /dev/null
X="$(submit "$ORDER_API" "$PREFIX-x" "$(order_body "$PREFIX-cx" "$SKU" 1)")"
wait_status 90 "$X" 'Rejected|Completed|Compensated'
read -r ts tc <<< "$(cancel_order "$ORDER_API_2" "$D")"
read -r ys yc <<< "$(cancel_order "$ORDER_API" "$X")"
read -r us uc <<< "$(cancel_order "$ORDER_API" "$(new_uuid)")"
expect S39-terminal "completed: 409 dispatch-started; declined (Rejected, never dispatched): 409 order-terminal; unknown: 404" \
  "completed $ts $tc; rejected ($(end_of "$X")) $ys $yc; unknown $us $uc" \
  "$([[ "$ts" == 409 && "$tc" == dispatch-started && "$(order_status "$X")" == Rejected && "$ys" == 409 && "$yc" == order-terminal && "$us" == 404 ]]; echo $?)"

scenario_finish
