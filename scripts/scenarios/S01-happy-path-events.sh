#!/usr/bin/env bash
# S01 happy path: orders complete with one completion record each, and every event of every source reaches the
# event history once, in order, applied; current state matches; a rebuild reproduces it.
# Drill stack: bash scripts/stack-up.sh --drill --replicas
#   S01-completed    3 orders (one with an operator approval) Completed/Delivered, one OrderCompleted event each
#   S01-history      per order and source: the published event ids equal the history's, all Applied, current
#                    state at the source's last sequence, nothing pending in any outbox
#   S01-timeline     the timeline's combined stage is Completed, no pending events, no dead letters
#   S01-rebuild      a rebuild of current state from history gives the same checksum
. "$(dirname "$0")/../lib.sh"
. "$(dirname "$0")/m3-lib.sh"
. "$(dirname "$0")/m4-lib.sh"
scenario_init S01
SKU="$(fresh_sku S01 20)"
SKU2="$(fresh_sku S01B 20)"

orders=()
for i in 1 2; do orders+=("$(submit "$ORDER_API" "$PREFIX-$i" "$(order_body "$PREFIX-c$i" "$SKU" 1 "$SKU2" 1)")"); done
review="$(submit "$ORDER_API" "$PREFIX-r" "$(review_order_body "$PREFIX-cr")")"
orders+=("$review")
printf '%s\n' "${orders[@]}" > "$OUT_DIR/orders.txt"
pending_review() { [[ -n "$(sql OrderDb "SELECT ReviewId FROM orders.ReviewTask WHERE OrderId = '$review' AND Status = 'Pending'")" ]]; }
wait_until 60 "review task" pending_review
rid="$(sql OrderDb "SELECT ReviewId FROM orders.ReviewTask WHERE OrderId = '$review' AND Status = 'Pending'")"
read -r d_status _ <<< "$(decide "$ORDER_API" "$rid" approve)"

summary=""; bad=0
for o in "${orders[@]}"; do
  wait_status 180 "$o" 'Completed|Compensated|Rejected|ManualInterventionRequired|Cancelled'
  e="$(end_of "$o")"; c="$(completed_events "$o")"; summary+="$e/$c "
  [[ "$e" == Completed/Delivered && "$c" == 1 ]] || bad=1
done
expect S01-completed "3 orders Completed/Delivered (approval $d_status), one OrderCompleted event each" "$summary" "$bad"

hist=""; bad=0
for o in "${orders[@]}"; do
  wait_until 60 "events of $o converged" is_converged "$o" || true
  hist+="$(converged "$o"); "; is_converged "$o" || bad=1
done
expect S01-history "every source's published events in history once, Applied, state at the last sequence" "$hist" "$bad"

tl=""; bad=0
for o in "${orders[@]}"; do
  t="$(timeline_of "$o")"; stage="$(json_get "$t" stage)"; dl="$(json_get "$t" deadLetters)"
  pending="$(sql OrderDb "SELECT COUNT(*) FROM orders.OrderEventHistory WHERE OrderId = '$o' AND Disposition = 'Pending'")"
  tl+="$stage/$pending/$dl "; [[ "$stage" == Completed && "$pending" == 0 && "$dl" == 0 ]] || bad=1
done
echo "$t" > "$OUT_DIR/timeline-last.json"
expect S01-timeline "stage Completed, 0 pending, 0 dead letters per order" "$tl" "$bad"

r="$(rebuild_timeline)"
expect S01-rebuild "rebuild checksum equals live state" "equal|before|after|streams: $r" "$([[ "$r" == true\|* ]]; echo $?)"
scenario_finish
