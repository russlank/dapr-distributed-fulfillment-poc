#!/usr/bin/env bash
# Shared parts of the event-distribution, projection, and reconciler scenarios and drills (source after
# m3-lib.sh; do not run). They run on the drill stack: bash scripts/stack-up.sh --drill --replicas

# The four sources: name|database|outbox table
M4_SOURCES=("orders|OrderDb|orders.OrderOutbox" "inventory|InventoryDb|inventory.Outbox" "payments|PaymentDb|payment.Outbox"
  "shipping|ShippingDb|shipping.Outbox")

# history_of_events <order id> [source] -> "source:seq=Disposition" in stream order
history_of_events() {
  local filter=""; [[ -n "${2:-}" ]] && filter="AND Source = '$2'"
  sqln OrderDb "SELECT STRING_AGG(CONCAT(Source, ':', Sequence, '=', Disposition), ',') WITHIN GROUP (ORDER BY Source, Sequence)
    FROM orders.OrderEventHistory WHERE OrderId = '$1' $filter"
}
# outbox_of <order id> <source> -> "seq=State" in sequence order (from the source's own database)
outbox_of() {
  local s db table; for s in "${M4_SOURCES[@]}"; do IFS='|' read -r name db table <<< "$s"; [[ "$name" == "$2" ]] && break; done
  sqln "$db" "SELECT STRING_AGG(CONCAT(Sequence, '=', State), ',') WITHIN GROUP (ORDER BY Sequence) FROM $table WHERE OrderId = '$1'"
}
# pending_outbox <source db> <table> -> number of rows not yet published
pending_outbox() { sql "$1" "SELECT COUNT(*) FROM $2 WHERE State = 'Pending'"; }

# converged <order id>: for every source, the published event ids equal the history's (all Applied), and current
# state stands at the source's last sequence. Prints a summary; returns 0 when converged.
converged() {
  local o=$1 ok=0 out="" s name db table published history last max
  for s in "${M4_SOURCES[@]}"; do
    IFS='|' read -r name db table <<< "$s"
    published="$(sqln "$db" "SELECT STRING_AGG(LOWER(CONVERT(varchar(36), EventId)), ',') WITHIN GROUP (ORDER BY Sequence) FROM $table WHERE OrderId = '$o' AND State = 'Published'")"
    max="$(sql "$db" "SELECT ISNULL(MAX(Sequence), 0) FROM $table WHERE OrderId = '$o'")"
    history="$(sqln OrderDb "SELECT STRING_AGG(LOWER(CONVERT(varchar(36), EventId)), ',') WITHIN GROUP (ORDER BY Sequence) FROM orders.OrderEventHistory WHERE OrderId = '$o' AND Source = '$name' AND Disposition = 'Applied'")"
    last="$(sql OrderDb "SELECT ISNULL(MAX(LastAppliedSequence), 0) FROM orders.OrderTimelineState WHERE OrderId = '$o' AND Source = '$name'")"
    out+="$name $max/$last "
    [[ "$published" == "$history" && "$last" == "$max" && "$(sql "$db" "SELECT COUNT(*) FROM $table WHERE OrderId = '$o' AND State = 'Pending'")" == 0 ]] || ok=1
  done
  printf '%s\n' "$out"
  return $ok
}
is_converged() { converged "$1" > /dev/null; }

# timeline_of <order id> -> the admin timeline JSON
timeline_of() { admin_api GET "/system/orders/$1/timeline"; }
# checksum_of <order ids, comma-separated> -> current-state checksum
checksum_of() { json_get "$(admin_api GET "/system/timeline/checksum?orders=$1")" checksum; }
# rebuild_timeline -> "<equal>|<differing streams>|<streams>": equal when every stream's rebuilt state equals its live state
rebuild_timeline() {
  local r; r="$(curl -s -m 120 -X POST "$ORDER_API/system/timeline/rebuild" -H "X-Api-Key: $ADMIN_API_KEY")"
  printf '%s|%s|%s\n' "$(json_get "$r" equal | tr A-Z a-z)" "$(json_get "$r" mismatchCount)" "$(json_get "$r" streams)"
}
# replay_outbox <source> <order id> <from sequence> -> replay result JSON
replay_outbox() {
  curl -s -m 30 -X POST "$ORDER_API/system/outbox/$1/replay" -H "X-Api-Key: $ADMIN_API_KEY" -H 'content-type: application/json' \
    -d '{"orderId":"'"$2"'","fromSequence":'"$3"'}'
}
# drill_publish <topic> <body> [cloudevent id] [content type] -> HTTP status of the sidecar's publish (drill builds only)
drill_publish() {
  local q=""; [[ -n "${3:-}" ]] && q="?id=$3"
  json_get "$(curl -s -m 30 -X POST "$ORDER_API/system/drill/publish/$1$q" -H "X-Api-Key: $ADMIN_API_KEY" -H "content-type: ${4:-application/json}" -d "$2")" status
}
# envelope <event id> <source> <order id> <sequence> <type> <schema version> <data json>
envelope() {
  printf '{"eventId":"%s","source":"%s","orderId":"%s","sequence":%s,"type":"%s","schemaVersion":%s,"occurredAtUtc":"%s","data":%s}' \
    "$1" "$2" "$3" "$4" "$5" "$6" "$(now_iso)" "$7"
}
dead_letters_of() { sqln OrderDb "SELECT STRING_AGG(Reason, ',') WITHIN GROUP (ORDER BY Id) FROM orders.EventDeadLetter WHERE OrderId = '$1'"; }
