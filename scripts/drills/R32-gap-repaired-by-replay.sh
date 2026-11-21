#!/usr/bin/env bash
# R32 (S48, S18) a lost event is detected as a gap, later events are held as pending (state stays at the last
# contiguous sequence), and the reconciler's outbox replay fills the gap and drains them.
# Drill stack: bash scripts/stack-up.sh --drill --replicas
# A two-line order that needs a review (so the failpoint is armed before its inventory events exist) produces
# inventory events 1..4: two holds created, two holds consumed, each a partial update of a different field.
# ConsumerAckWithoutApply (Skip) on the order API subscriber acknowledges sequence 2 without storing it.
#   R32-lost       the failpoint fired for inventory sequence 2; 3 and 4 are in history as Pending; the gap view
#                  shows (order, inventory, 2) while the last applied sequence is 1; the timeline shows 2 pending
#   R32-held       current state holds only sequence 1's field (the fields 3 and 4 change are not applied)
#   R32-replayed   within the gap threshold plus a reconciler interval, the reconciler requested a replay from 2;
#                  history has 1..4 once each, all Applied; last applied 4; no gap left; 3 and 4 arrived again as
#                  duplicates
#   R32-converged  published = history for every source of the order; a rebuild gives the live checksum;
#                  no ProjectionGap case (the source had the event)
DRILL_ID=R32
. "$(dirname "$0")/lib.sh"
. "$REPO_ROOT/scripts/scenarios/m3-lib.sh"
. "$REPO_ROOT/scripts/scenarios/m4-lib.sh"
drill_preflight
trap 'drill_finish; exit $?' EXIT
RUN="r32-$(date -u +%H%M%S)-$((RANDOM % 1000))"
SKU="$(fresh_sku R32 10)"
REVIEW_SKU="$(review_sku)"

o="$(submit "$ORDER_API" "$RUN" "$(order_body "$RUN-c" "$REVIEW_SKU" 100 "$SKU" 1)")"
echo "$o" > "$DRILL_DIR/order.txt"
pending_review() { [[ -n "$(sql OrderDb "SELECT ReviewId FROM orders.ReviewTask WHERE OrderId = '$o' AND Status = 'Pending'")" ]]; }
wait_until 60 "review of $o" pending_review
arm_failpoint OrderDb ConsumerAckWithoutApply "inventory:$o:2" Skip
INJECTED_AT="$(now_iso)"
rid="$(sql OrderDb "SELECT ReviewId FROM orders.ReviewTask WHERE OrderId = '$o' AND Status = 'Pending'")"
decide "$ORDER_API" "$rid" approve > /dev/null

fired() { [[ -n "$(failpoint_fired_at OrderDb ConsumerAckWithoutApply)" ]]; }
held() { [[ "$(sql OrderDb "SELECT COUNT(*) FROM orders.OrderEventHistory WHERE OrderId = '$o' AND Source = 'inventory' AND Sequence IN (3, 4) AND Disposition = 'Pending'")" == 2 ]]; }
wait_until 120 "sequence 2 lost" fired
wait_until 120 "sequences 3 and 4 held" held
gap="$(sqln OrderDb "SELECT CONCAT(Source, ':', MissingSequence, ':', LastAppliedSequence) FROM orders.OrderEventGaps WHERE OrderId = '$o'")"
t="$(timeline_of "$o")"; echo "$t" > "$DRILL_DIR/timeline-gap.json"
pending="$(python -c 'import json,sys; t=json.loads(sys.argv[1]); print([s["pendingEvents"] for s in t["sources"] if s["source"]=="inventory"][0])' "$t")"
FAULT_OBSERVED="fired $(failpoint_fired_at OrderDb ConsumerAckWithoutApply); gap $gap; history $(history_of_events "$o" inventory)"
aexpect R32-lost "failpoint fired for inventory 2; 3,4 Pending; gap inventory:2 with last applied 1; timeline pendingEvents 2" \
  "$FAULT_OBSERVED; pendingEvents $pending" "$([[ "$gap" == inventory:2:1 && "$pending" == 2 ]] && fired && held; echo $?)"

fields="$(sql OrderDb "SELECT Fields FROM orders.OrderTimelineState WHERE OrderId = '$o' AND Source = 'inventory'")"
aexpect R32-held "inventory state at sequence 1: one hold Held, nothing Consumed" "fields $fields" \
  "$([[ "$fields" == *Held* && "$fields" != *Consumed* && $(grep -o 'hold:' <<< "$fields" | wc -l) == 1 ]]; echo $?)"

drained() { [[ "$(sql OrderDb "SELECT LastAppliedSequence FROM orders.OrderTimelineState WHERE OrderId = '$o' AND Source = 'inventory'")" == 4 ]]; }
wait_until 90 "gap filled by the reconciler's replay" drained
RECOVERED_AT="$(now_iso)"
replays="$(docker compose --profile replicas logs --since 10m order-api order-api-2 2>/dev/null | grep -c "replay of inventory for order $o from 2")"
hist="$(history_of_events "$o" inventory)"
dups="$(sql OrderDb "SELECT ISNULL(SUM(Duplicates), 0) FROM orders.OrderEventHistory WHERE OrderId = '$o' AND Source = 'inventory' AND Sequence IN (3, 4)")"
left="$(sql OrderDb "SELECT COUNT(*) FROM orders.OrderEventGaps WHERE OrderId = '$o'")"
aexpect R32-replayed "reconciler replay from 2; history inventory 1..4 Applied once each; last applied 4; no gap; 3,4 duplicates >= 2" \
  "replay log lines $replays; history $hist; duplicates of 3,4 $dups; gaps $left" \
  "$([[ "$replays" -ge 1 && "$hist" == "inventory:1=Applied,inventory:2=Applied,inventory:3=Applied,inventory:4=Applied" && "$dups" -ge 2 && "$left" == 0 ]]; echo $?)"

wait_status 180 "$o" 'Completed|Compensated|Rejected|ManualInterventionRequired'
wait_until 60 "order events converged" is_converged "$o" || true
conv="$(converged "$o")"; r="$(rebuild_timeline)"; cases="$(cases_of "$o")"
aexpect R32-converged "published = history for every source; rebuild equal; no ProjectionGap case" "converged $conv; rebuild $r; cases '$cases'; end $(end_of "$o")" \
  "$(is_converged "$o" && [[ "$r" == true\|* && "$cases" != *ProjectionGap* ]]; echo $?)"
