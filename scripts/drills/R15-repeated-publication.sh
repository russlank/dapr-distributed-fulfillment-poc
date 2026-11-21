#!/usr/bin/env bash
# R15 (S12, S18) an event published any number of times is applied once; replays change neither history nor state.
# Drill stack: bash scripts/stack-up.sh --drill --replicas
# An order completes; its first inventory event is then published again three times through a crash after the
# broker's acknowledgement: AfterBrokerAckBeforeOutboxMarkSent (FailFast) is armed three times for that event id
# and a replay returns the row to Pending; each relay that publishes it dies before marking it, the next one
# (after the lease expires) publishes it again. A last replay of the order's every source republishes all
# its events once more.
#   R15-fired       three FiredAt rows for the event; inventory replicas exited and were restarted
#   R15-once        the outbox row ends Published; the event is in history once (Applied); its duplicate counter
#                   rose by at least 3 (the number of publications is reported, not bounded)
#   R15-unchanged   the order's current-state checksum is unchanged by the duplicates
#   S18-replay      a replay of all four sources from sequence 1: history unchanged (same rows, all Applied),
#                   checksum unchanged, duplicates counted for every event
DRILL_ID=R15
. "$(dirname "$0")/lib.sh"
. "$REPO_ROOT/scripts/scenarios/m3-lib.sh"
. "$REPO_ROOT/scripts/scenarios/m4-lib.sh"
drill_preflight
INV=(inventory-service inventory-service-2 inventory-service-3)
restart_dead() { local s; for s in "${INV[@]}"; do [[ "$(docker compose --profile replicas ps -a --format '{{.State}}' "$s" 2>/dev/null)" == running ]] || start_pair "$s" >/dev/null 2>&1; done; }
on_cleanup "restart_dead"
trap 'run_cleanup; drill_finish; exit $?' EXIT
RUN="r15-$(date -u +%H%M%S)-$((RANDOM % 1000))"
SKU="$(fresh_sku R15 10)"

o="$(submit "$ORDER_API" "$RUN" "$(order_body "$RUN-c" "$SKU" 1)")"
echo "$o" > "$DRILL_DIR/order.txt"
wait_status 180 "$o" 'Completed|Compensated|Rejected|ManualInterventionRequired'
wait_until 60 "order events converged" is_converged "$o"
eid="$(sql InventoryDb "SELECT LOWER(CONVERT(varchar(36), EventId)) FROM inventory.Outbox WHERE OrderId = '$o' AND Sequence = 1")"
before="$(checksum_of "$o")"
dup0="$(sql OrderDb "SELECT Duplicates FROM orders.OrderEventHistory WHERE EventId = '$eid'")"

arm_failpoint_times InventoryDb AfterBrokerAckBeforeOutboxMarkSent "$eid" FailFast 3
INJECTED_AT="$(now_iso)"
replay_outbox inventory "$o" 1 > "$DRILL_DIR/replay-1.json"
fired_count() { sql InventoryDb "SELECT COUNT(*) FROM drill.Failpoint WHERE DrillRunId LIKE '$DRILL_RUN_ID-%' AND FiredAt IS NOT NULL"; }
for n in 1 2 3; do
  three() { [[ "$(fired_count)" -ge $n ]]; }
  wait_until 90 "publication $n crashed" three
  sleep 2; restart_dead
done
published() { [[ "$(sql InventoryDb "SELECT State FROM inventory.Outbox WHERE EventId = '$eid'")" == Published ]]; }
wait_until 90 "row marked Published" published
restart_dead
fires="$(sql InventoryDb "SELECT STRING_AGG(CONVERT(varchar(23), FiredAt, 126), ',') FROM drill.Failpoint WHERE DrillRunId LIKE '$DRILL_RUN_ID-%'")"
FAULT_OBSERVED="fired $fires"
aexpect R15-fired "three FiredAt rows for event $eid" "fired $fires" "$([[ "$(fired_count)" == 3 ]]; echo $?)"

dupes_reached() { [[ "$(sql OrderDb "SELECT Duplicates FROM orders.OrderEventHistory WHERE EventId = '$eid'")" -ge $((dup0 + 3)) ]]; }
wait_until 60 "duplicates counted" dupes_reached || true
count="$(sql OrderDb "SELECT COUNT(*) FROM orders.OrderEventHistory WHERE EventId = '$eid'")"
row="$(sql OrderDb "SELECT CONCAT(Disposition, '|', Duplicates) FROM orders.OrderEventHistory WHERE EventId = '$eid'")"
aexpect R15-once "outbox Published; history once Applied; duplicates rose by >= 3 (from $dup0)" \
  "outbox $(sql InventoryDb "SELECT State FROM inventory.Outbox WHERE EventId = '$eid'"); history rows $count ($row)" \
  "$([[ "$count" == 1 && "$row" == Applied\|* && "${row#*|}" -ge $((dup0 + 3)) ]] && published; echo $?)"
after="$(checksum_of "$o")"
aexpect R15-unchanged "checksum unchanged by the duplicates" "before $before after $after" "$([[ -n "$before" && "$before" == "$after" ]]; echo $?)"

hist_before="$(history_of_events "$o")"; dups_before="$(sql OrderDb "SELECT SUM(Duplicates) FROM orders.OrderEventHistory WHERE OrderId = '$o'")"
events="$(sql OrderDb "SELECT COUNT(*) FROM orders.OrderEventHistory WHERE OrderId = '$o'")"
for s in orders inventory payments shipping; do replay_outbox "$s" "$o" 1 >> "$DRILL_DIR/replay-all.json"; echo >> "$DRILL_DIR/replay-all.json"; done
all_again() { [[ "$(sql OrderDb "SELECT SUM(Duplicates) FROM orders.OrderEventHistory WHERE OrderId = '$o'")" -ge $((dups_before + events)) ]]; }
wait_until 60 "every event delivered again" all_again || true
RECOVERED_AT="$(now_iso)"
hist_after="$(history_of_events "$o")"; dups_after="$(sql OrderDb "SELECT SUM(Duplicates) FROM orders.OrderEventHistory WHERE OrderId = '$o'")"
aexpect S18-replay "replay of all sources: history unchanged, checksum unchanged, +$events duplicates" \
  "history same: $([[ "$hist_before" == "$hist_after" ]] && echo yes || echo no) ($hist_after); duplicates $dups_before -> $dups_after; checksum $(checksum_of "$o")" \
  "$([[ "$hist_before" == "$hist_after" && "$dups_after" -ge $((dups_before + events)) && "$(checksum_of "$o")" == "$before" ]] && is_converged "$o"; echo $?)"
