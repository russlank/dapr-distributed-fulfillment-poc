#!/usr/bin/env bash
# S48 contiguous projection from a shuffled stream (with S43 for events): published through the drill-only
# publisher on the inventory topic, for a stream that belongs to no real order: sequences 1, 3, a duplicate
# of 3, 5, a conflicting payload under 3's event id, 4, and 2 after a 5 s delay.
# Drill stack: bash scripts/stack-up.sh --drill --replicas
# The deterministic part of S48 (every permutation of a 6-event set, 720 orders) runs in the unit tests
# (TimelineProjectionTests); the SQL part in the integration tests (EventConsumerTests).
#   S48-held        before 2 arrives: 1 Applied, 3, 4, 5 Pending, last applied 1, the gap view shows 2
#   S48-drained     after 2: history 1..5 once each, all Applied, last applied 5, no gap
#   S48-duplicate   the duplicate 3 was counted (Duplicates >= 1), not stored again
#   S43-events      the conflicting payload under 3's id is dead-lettered (EventIdPayloadConflict) and changed
#                   nothing; history keeps the original payload
#   S48-rebuild     a rebuild from history gives the live checksum
. "$(dirname "$0")/../lib.sh"
. "$(dirname "$0")/m3-lib.sh"
. "$(dirname "$0")/m4-lib.sh"
scenario_init S48
TOPIC=fulfillment.inventory.v1
stream="$(new_uuid)"
declare -A ids
for q in 1 2 3 4 5; do ids[$q]="$(new_uuid)"; done
event() { envelope "${ids[$1]}" inventory "$stream" "$1" "$2" 1 "$3"; }
e1="$(event 1 HoldCreated '{"holdId":"h1"}')"
e2="$(event 2 HoldCreated '{"holdId":"h2"}')"
e3="$(event 3 HoldConsumed '{"holdId":"h1"}')"
e4="$(event 4 HoldConsumed '{"holdId":"h2"}')"
e5="$(event 5 HoldExtended '{"holdId":"h3","expiresAt":"2026-11-21T00:00:00Z"}')"
c3="$(event 3 HoldReleased '{"holdId":"h1"}')"
echo "$stream" > "$OUT_DIR/stream.txt"
for e in "$e1" "$e3" "$e3" "$e5"; do drill_publish "$TOPIC" "$e" >> "$OUT_DIR/publish.txt"; done
# Two consumer replicas may take messages in parallel: the conflicting copy goes out once the original 3 is stored,
# so the conflict is the later arrival (as in a republication with a changed payload).
stored3() { [[ "$(sql OrderDb "SELECT COUNT(*) FROM orders.OrderEventHistory WHERE EventId = '${ids[3]}'")" == 1 ]]; }
wait_until 60 "event 3 stored" stored3 || true
for e in "$c3" "$e4"; do drill_publish "$TOPIC" "$e" >> "$OUT_DIR/publish.txt"; done

pend() { [[ "$(sql OrderDb "SELECT COUNT(*) FROM orders.OrderEventHistory WHERE OrderId = '$stream' AND Disposition = 'Pending'")" == 3 ]]; }
wait_until 60 "3, 4, 5 pending" pend || true
hist="$(history_of_events "$stream")"; gap="$(sqln OrderDb "SELECT STRING_AGG(CAST(MissingSequence AS varchar), ',') FROM orders.OrderEventGaps WHERE OrderId = '$stream'")"
last="$(sql OrderDb "SELECT LastAppliedSequence FROM orders.OrderTimelineState WHERE OrderId = '$stream' AND Source = 'inventory'")"
expect S48-held "1 Applied; 3,4,5 Pending; last applied 1; gap 2" "history $hist; last $last; gap $gap" \
  "$([[ "$hist" == "inventory:1=Applied,inventory:3=Pending,inventory:4=Pending,inventory:5=Pending" && "$last" == 1 && "$gap" == 2 ]]; echo $?)"

sleep 5
drill_publish "$TOPIC" "$e2" >> "$OUT_DIR/publish.txt"
five() { [[ "$(sql OrderDb "SELECT LastAppliedSequence FROM orders.OrderTimelineState WHERE OrderId = '$stream' AND Source = 'inventory'")" == 5 ]]; }
wait_until 60 "drained to 5" five || true
hist="$(history_of_events "$stream")"; left="$(sql OrderDb "SELECT COUNT(*) FROM orders.OrderEventGaps WHERE OrderId = '$stream'")"
expect S48-drained "history 1..5 Applied once each; last applied 5; no gap" "history $hist; gaps $left" \
  "$([[ "$hist" == "inventory:1=Applied,inventory:2=Applied,inventory:3=Applied,inventory:4=Applied,inventory:5=Applied" && "$left" == 0 ]] && five; echo $?)"

dup="$(sql OrderDb "SELECT Duplicates FROM orders.OrderEventHistory WHERE EventId = '${ids[3]}'")"
expect S48-duplicate "the duplicate of 3 counted, history row stored once" "duplicates $dup; rows $(sql OrderDb "SELECT COUNT(*) FROM orders.OrderEventHistory WHERE EventId = '${ids[3]}'")" \
  "$([[ "$dup" -ge 1 ]]; echo $?)"

dead="$(dead_letters_of "$stream")"; kept="$(sql OrderDb "SELECT Type FROM orders.OrderEventHistory WHERE EventId = '${ids[3]}'")"
fields="$(sql OrderDb "SELECT Fields FROM orders.OrderTimelineState WHERE OrderId = '$stream' AND Source = 'inventory'")"
expect S43-events "conflicting payload for 3 dead-lettered (EventIdPayloadConflict); history keeps HoldConsumed; h1 Consumed" \
  "dead letters $dead; stored type $kept; fields $fields" \
  "$([[ "$dead" == *EventIdPayloadConflict* && "$kept" == HoldConsumed && "$fields" == *'"hold:h1":"Consumed"'* ]]; echo $?)"

r="$(rebuild_timeline)"
expect S48-rebuild "a rebuild equals live state, stream by stream" "equal|differing|streams: $r" "$([[ "$r" == true\|* ]]; echo $?)"
scenario_finish
