#!/usr/bin/env bash
# Rebuilds the order timeline (current state per order and source) from the event history: every stream returns
# to sequence 0 and its history is applied again from sequence 1 with the consumer's apply-and-drain rule.
# Prints the checksum of current state before and after; they are equal unless live state had diverged from its
# history. Exit 0 when equal, 1 otherwise.
# Usage: scripts/ops/rebuild-timeline.sh   (against the running stack; COMPOSE_PROJECT_NAME selects it)
. "$(dirname "$0")/../lib.sh"
out="$(curl -s -m 300 -X POST "$ORDER_API/system/timeline/rebuild" -H "X-Api-Key: $ADMIN_API_KEY")"
[[ -n "$out" ]] || die "no answer from $ORDER_API"
equal="$(json_get "$out" equal | tr 'A-Z' 'a-z')"
log "streams $(json_get "$out" streams); checksum before $(json_get "$out" checksumBefore); after $(json_get "$out" checksumAfter); equal $equal"
[[ "$equal" == true ]]
