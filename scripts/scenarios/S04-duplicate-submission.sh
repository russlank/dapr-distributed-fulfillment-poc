#!/usr/bin/env bash
# S04 duplicate order submission across replicas. Needs both order API replicas
# (bash scripts/stack-up.sh --replicas, release or drill).
# The client simulator sends each idempotency key several times at once with an identical body,
# alternating between the two replicas, and follows the order to its end.
# PASS when:
#   S04-client     per key exactly one 201 and every other copy 200 replayed, all with one order id;
#                  the answers came from both replicas as the replicas report themselves (X-Served-By)
#   S04-servers    each replica's own log names every order: it was accepted (committed) by exactly
#                  one replica, and the other replica answered copies of it as replays
#   S04-sql        OrderDb per key: one order, one key row, one start request (Closed), history
#                  history through AwaitingDelivery to Completed with one terminal transition, one effect
#                  per line plus one completion, no fence rejection
#   S04-race       at least one copy lost the insert race in the database (unique-key resolution on the
#                  replica that lost): concurrency reached SQL, not only the fast path; UNVERIFIED if all
#                  copies happened to arrive after the first commit
# Evidence in artifacts/scenarios/S04-<time>/.
. "$(dirname "$0")/../lib.sh"
. "$(dirname "$0")/replica-lib.sh"

RUN_ID="S04-$(date -u +%Y%m%dT%H%M%SZ)"
OUT_DIR="$REPO_ROOT/artifacts/scenarios/$RUN_ID"
mkdir -p "$OUT_DIR"
RESULTS_FILE="$OUT_DIR/results.jsonl"
KEYS="${S04_KEYS:-6}"
COPIES="${S04_COPIES:-8}"
CLIENT_RUN="s04-$(date -u +%Y%m%d-%H%M%S)-$((RANDOM % 1000))"

require_replicas
client_sim identity > "$OUT_DIR/identity.json" || { log "REFUSED: the client simulator does not build"; exit 2; }
CLIENT_SIM_BUILT=1
SINCE="$(date -u -d '-2 seconds' +%Y-%m-%dT%H:%M:%SZ 2>/dev/null || date -u +%Y-%m-%dT%H:%M:%SZ)"
log "S04 $RUN_ID: client run $CLIENT_RUN, $KEYS keys x $COPIES copies over $ORDER_API and $ORDER_API_2; evidence in $OUT_DIR"

c="$(sim duplicates duplicates --run-id "$CLIENT_RUN" --seed 4 --orders "$KEYS" --copies "$COPIES" --no-conflict --follow --follow-timeout 120 --url "$ORDER_API" --url "$ORDER_API_2")"

# S04-client --------------------------------------------------------------------------------------
per_key="$(jq_py duplicates "[ (len(k['distinctOrderIds']), k['created'], sum(1 for x in k['copies'] if x['acceptance'] == 'replayed'), len(k['servedBy'])) for k in r['items'] ]")"
good_keys="$(jq_py duplicates "sum(1 for k in r['items'] if len(k['distinctOrderIds']) == 1 and k['created'] == 1 and sum(1 for x in k['copies'] if x['acceptance'] == 'replayed') == $COPIES - 1 and len(k['servedBy']) >= 2)")"
observed="exit $c; per key (order ids, created, replayed, replicas answering): $per_key; answers by server $(jq_py duplicates "r['summary']['byServer']"); ambiguous attempts $(jq_py duplicates "r['summary']['ambiguousAttempts']")"
if [[ "$c" == 0 && "$good_keys" == "$KEYS" ]]; then
  check S04-client PASS "per key one 201, $((COPIES - 1)) replays, one order id; answered by both replicas" "$observed"
else
  check S04-client FAIL "$KEYS/$KEYS keys correct" "good keys $good_keys/$KEYS; $observed"
fi

# S04-servers -------------------------------------------------------------------------------------
order_ids_of duplicates "[k['distinctOrderIds'][0] for k in r['items'] if k['distinctOrderIds']]" > "$OUT_DIR/order-ids.txt"
server_outcomes "$SINCE" "$OUT_DIR/order-ids.txt" > "$OUT_DIR/server-outcomes.json"
verdict="$(python - "$OUT_DIR/server-outcomes.json" <<'PY'
import json, sys
o = json.load(open(sys.argv[1]))
a, b = o["order-api"], o["order-api-2"]
ok = 0; notes = []; races = 0; by = {"order-api": 0, "order-api-2": 0}
for i in a:
    acc = a[i]["accepted"] + b[i]["accepted"]
    seen_a = sum(a[i].values()) > 0; seen_b = sum(b[i].values()) > 0
    races += a[i]["concurrent"] + b[i]["concurrent"]
    if a[i]["accepted"]: by["order-api"] += 1
    if b[i]["accepted"]: by["order-api-2"] += 1
    if acc == 1 and seen_a and seen_b:
        ok += 1
    else:
        notes.append(f"{i}: accepted {a[i]['accepted']}+{b[i]['accepted']}, order-api {a[i]}, order-api-2 {b[i]}")
print(f"{ok}|{races}|accepted on order-api {by['order-api']}, on order-api-2 {by['order-api-2']}|{'; '.join(notes[:2])}")
PY
)"
IFS='|' read -r servers_ok races accepted_by notes <<< "$verdict"
observed="$servers_ok/$KEYS orders accepted once and seen by both replicas' logs; $accepted_by; insert races resolved by the unique key: $races ${notes:+; $notes}"
if [[ "$servers_ok" == "$KEYS" ]]; then
  check S04-servers PASS "each order accepted by exactly one replica, the other answered replays of it (replica logs)" "$observed"
else
  check S04-servers FAIL "$KEYS/$KEYS" "$observed"
fi

# S04-sql -----------------------------------------------------------------------------------------
sql_orders_report "$CLIENT_RUN-c" > "$OUT_DIR/sql-orders.jsonl"
consistent="$(sql_consistent "$OUT_DIR/sql-orders.jsonl" "$KEYS")"
keys_rows="$(sql OrderDb "SELECT COUNT(*) FROM orders.IdempotencyKey WHERE [Key] LIKE '$CLIENT_RUN-o%'")"
observed="orders $(wc -l < "$OUT_DIR/sql-orders.jsonl" | tr -d ' '), key rows $keys_rows; $consistent; effect replays $(python -c "import json,sys; print(sum(json.loads(l)['effectReplays'] for l in open(sys.argv[1])))" "$OUT_DIR/sql-orders.jsonl"); claims $(python -c "import json,sys; print([json.loads(l)['claims'] for l in open(sys.argv[1])])" "$OUT_DIR/sql-orders.jsonl")"
if [[ "$consistent" == ok && "$keys_rows" == "$KEYS" ]]; then
  check S04-sql PASS "one order, key, Closed start request per key; one Completed transition; one effect per line, one completion event" "$observed"
else
  check S04-sql FAIL "$KEYS consistent orders" "$observed"
fi

# S04-race ----------------------------------------------------------------------------------------
if [[ "${races:-0}" -gt 0 ]]; then
  check S04-race PASS "concurrent copies reached the insert in SQL and lost to the unique key (resolved as replay)" "$races insert races resolved"
else
  check S04-race UNVERIFIED "at least one insert race" "0: every copy found the committed key on the fast path in this run"
fi

finish S04
