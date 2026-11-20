#!/usr/bin/env bash
# S29 interactive approval and S31 human rejection, through the client simulator's operator commands.
# Drill stack: bash scripts/stack-up.sh --drill --replicas
# Two orders that require a review wait in AwaitingApproval; the operator lists them (reviews), approves
# one, rejects the other (approve --outcome reject).
#   S29-listed     reviews --status Pending lists both pending rounds
#   S29-approved   the approval returned accepted; the order went through reservation, payment
#                  (authorize and capture once at the provider), dispatch and delivery to Completed
#                  (9 history rows), one decision, one OrderCompleted event
#   S31-rejected   the rejection returned accepted; the order ended Rejected/ReviewRejected right after
#                  the review: no hold, no provider operation, no shipment
#   S29-replay     repeating both commands returns replays (same decision ids), no second decision
. "$(dirname "$0")/../lib.sh"
. "$(dirname "$0")/m3-lib.sh"
. "$(dirname "$0")/replica-lib.sh"
scenario_init S29
A="$(submit "$ORDER_API" "$PREFIX-a" "$(review_order_body "$PREFIX-ca")")"
B="$(submit "$ORDER_API_2" "$PREFIX-b" "$(review_order_body "$PREFIX-cb")")"
echo "approve $A; reject $B" > "$OUT_DIR/orders.txt"
wait_status 90 "$A" AwaitingApproval; wait_status 90 "$B" AwaitingApproval
two_pending() { [[ "$(sql OrderDb "SELECT COUNT(*) FROM orders.ReviewTask WHERE OrderId IN ('$A', '$B') AND Status = 'Pending'")" == 2 ]]; }
wait_until 30 "both review rounds exist" two_pending

c="$(sim reviews reviews --status Pending --url "$ORDER_API")"
listed="$(jq_py reviews "sorted({x['orderId'] for i in r['items'] for x in (i.get('data') or [])} & {'$A', '$B'})")"
expect S29-listed "both pending rounds listed" "exit $c; listed $listed" "$([[ "$c" == 0 && "$listed" == *"$A"* && "$listed" == *"$B"* ]]; echo $?)"

ca="$(sim approve approve --order "$A" --url "$ORDER_API_2" --decision-seed "$PREFIX")"
cb="$(sim reject approve --order "$B" --outcome reject --url "$ORDER_API" --decision-seed "$PREFIX")"
wait_status 180 "$A" 'Completed|Compensated|Rejected|ManualInterventionRequired'
wait_status 60 "$B" 'Rejected|Completed|Compensated'

ha="$(history_of "$A")"; money="$(provider_money "$A")"; da="$(sql OrderDb "SELECT COUNT(*) FROM orders.ReviewDecision WHERE OrderId = '$A'")"
expect S29-approved "accepted; 9-row history to Completed; provider authorized 1, captured 1; 1 decision; 1 completion event; shipment Delivered" \
  "exit $ca result $(jq_py approve "r['items'][0]['result']"); history $ha; provider a|c|v|r $money; decisions $da; completion $(completed_events "$A"); shipment $(shipment_of "$A")" \
  "$([[ "$ca" == 0 && "$ha" == Submitted,Validating,AwaitingApproval,Reserving,PaymentAuthorized,Packing,Dispatched,AwaitingDelivery,Completed && "$money" == 1\|1\|0\|0 && "$da" == 1 && "$(completed_events "$A")" == 1 && "$(shipment_of "$A")" == *Delivered* ]]; echo $?)"

eb="$(end_of "$B")"; hb="$(history_of "$B")"; holds="$(holds_of "$B")"; ops="$(provider_ops "$B")"; ship="$(shipment_of "$B")"
expect S31-rejected "accepted; Rejected/ReviewRejected after Submitted,Validating,AwaitingApproval; no hold, provider operation, or shipment" \
  "exit $cb result $(jq_py reject "r['items'][0]['result']"); end $eb; history $hb; holds '$holds'; provider '$ops'; shipment '$ship'" \
  "$([[ "$cb" == 0 && "$eb" == Rejected/ReviewRejected && "$hb" == Submitted,Validating,AwaitingApproval,Rejected && -z "$holds" && -z "$ops" && -z "$ship" ]]; echo $?)"

ra="$(sim approve-again approve --order "$A" --url "$ORDER_API" --decision-seed "$PREFIX")"
rb="$(sim reject-again approve --order "$B" --outcome reject --url "$ORDER_API_2" --decision-seed "$PREFIX")"
decisions="$(sql OrderDb "SELECT COUNT(*) FROM orders.ReviewDecision WHERE OrderId IN ('$A', '$B')")"
expect S29-replay "both repeats replayed; 2 decisions in total" \
  "approve again exit $ra $(jq_py approve-again "r['items'][0]['result']"); reject again exit $rb $(jq_py reject-again "r['items'][0]['result']"); decisions $decisions" \
  "$([[ "$ra" == 0 && "$rb" == 0 && "$(jq_py approve-again "r['items'][0]['result']")" == replayed && "$(jq_py reject-again "r['items'][0]['result']")" == replayed && "$decisions" == 2 ]]; echo $?)"

scenario_finish
