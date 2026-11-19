#!/usr/bin/env bash
# S43 idempotency conflicts (orders) across replicas. Needs both order API replicas
# (bash scripts/stack-up.sh --replicas, release or drill).
# An order is accepted through one replica and completed; then other bodies under its key (other
# customer, quantity, extra line, line order) are sent through both replicas; and on fresh keys,
# distinct bodies race each other across the replicas.
# PASS when:
#   S43-existing   every other body for the committed key: 422 idempotency-key-reused from both replicas
#                  (sent at once and again one by one); the original body: 200 replayed, same order;
#                  the stored order, key hash, and history are unchanged by all of it
#   S43-race       per fresh key exactly one body accepted, every other 422 in the race and again on
#                  the other replica; the accepted body repeated: 200 replayed; answers from both replicas
#   S43-servers    the replicas' own logs: refusals of other bodies recorded by both replicas
#   S43-sql        OrderDb: each order holds the accepted body (customer, lines), one key row, one
#                  Closed start request, Completed once, one effect per line plus completion
# Evidence in artifacts/scenarios/S43-<time>/.
. "$(dirname "$0")/../lib.sh"
. "$(dirname "$0")/replica-lib.sh"

RUN_ID="S43-$(date -u +%Y%m%dT%H%M%SZ)"
OUT_DIR="$REPO_ROOT/artifacts/scenarios/$RUN_ID"
mkdir -p "$OUT_DIR"
RESULTS_FILE="$OUT_DIR/results.jsonl"
KEYS="${S43_KEYS:-4}"
VARIANTS="${S43_VARIANTS:-5}"
STAMP="$(date -u +%Y%m%d-%H%M%S)-$((RANDOM % 1000))"
EXISTING_RUN="s43e-$STAMP"
RACE_RUN="s43r-$STAMP"
URLS=(--url "$ORDER_API" --url "$ORDER_API_2")

require_replicas
client_sim identity > "$OUT_DIR/identity.json" || { log "REFUSED: the client simulator does not build"; exit 2; }
CLIENT_SIM_BUILT=1
SINCE="$(date -u -d '-2 seconds' +%Y-%m-%dT%H:%M:%SZ 2>/dev/null || date -u +%Y-%m-%dT%H:%M:%SZ)"
log "S43 $RUN_ID: existing-key run $EXISTING_RUN, race run $RACE_RUN ($KEYS keys x $VARIANTS bodies); evidence in $OUT_DIR"

# S43-existing ------------------------------------------------------------------------------------
# The original is the generated order 1 of the run: the conflicts command's variant 0 for the same run id and seed.
c0="$(sim original single --run-id "$EXISTING_RUN" --seed 43 --expect new --follow --follow-timeout 120 --replica 1 "${URLS[@]}")"
original_id="$(jq_py original "r['items'][0]['orderId']")"
before="$(sql OrderDb "SELECT CONCAT(o.CustomerId, '|', o.Status, '|', CONVERT(varchar(64), k.RequestHash, 2), '|',
  (SELECT COUNT(*) FROM orders.OrderStatusHistory h WHERE h.OrderId = o.OrderId), '|',
  (SELECT STRING_AGG(CONCAT(l.Sku, ':', l.Quantity), ',') WITHIN GROUP (ORDER BY l.LineNumber) FROM orders.OrderLine l WHERE l.OrderId = o.OrderId), '|',
  (SELECT COUNT(*) FROM orders.StubEffect e WHERE e.OrderId = o.OrderId))
  FROM orders.[Order] o JOIN orders.IdempotencyKey k ON k.OrderId = o.OrderId WHERE o.OrderId = '$original_id'")"
c1="$(sim existing conflicts --run-id "$EXISTING_RUN" --seed 43 --orders 1 --variants "$VARIANTS" "${URLS[@]}")"
after="$(sql OrderDb "SELECT CONCAT(o.CustomerId, '|', o.Status, '|', CONVERT(varchar(64), k.RequestHash, 2), '|',
  (SELECT COUNT(*) FROM orders.OrderStatusHistory h WHERE h.OrderId = o.OrderId), '|',
  (SELECT STRING_AGG(CONCAT(l.Sku, ':', l.Quantity), ',') WITHIN GROUP (ORDER BY l.LineNumber) FROM orders.OrderLine l WHERE l.OrderId = o.OrderId), '|',
  (SELECT COUNT(*) FROM orders.StubEffect e WHERE e.OrderId = o.OrderId))
  FROM orders.[Order] o JOIN orders.IdempotencyKey k ON k.OrderId = o.OrderId WHERE o.OrderId = '$original_id'")"
existing_eval="$(jq_py existing "(lambda k: [k['winnerVariant'], k['orderId'], [(v['variant'], v['change'], v['race']['acceptance'], v['race'].get('httpStatus'), v['race'].get('code'), v['repeat']['acceptance']) for v in k['variants']], k['servedBy']])(r['items'][0])")"
refused="$(jq_py existing "sum(1 for v in r['items'][0]['variants'] if v['variant'] != 0 for x in (v['race'], v['repeat']) if x['acceptance'] == 'conflict' and x.get('httpStatus') == 422 and x.get('code') == 'idempotency-key-reused')")"
refused_by="$(jq_py existing "sorted(set(x['attempts'][-1].get('servedBy') for v in r['items'][0]['variants'] if v['variant'] != 0 for x in (v['race'], v['repeat'])))")"
original_ok="$(jq_py existing "all(x['acceptance'] == 'replayed' and x['orderId'] == '$original_id' for x in (r['items'][0]['variants'][0]['race'], r['items'][0]['variants'][0]['repeat']))")"
expected_refusals=$(( (VARIANTS - 1) * 2 ))
observed="original $original_id (exit $c0, $(jq_py original "r['items'][0]['follow']['finalStatus']")); conflicts exit $c1; refusals 422 idempotency-key-reused $refused/$expected_refusals answered by $refused_by; original body replayed both times: $original_ok; stored order before/after equal: $([[ "$before" == "$after" && -n "$before" ]] && echo yes || echo NO) ($after); variants $existing_eval"
if [[ "$c0" == 0 && "$c1" == 0 && "$refused" == "$expected_refusals" && "$refused_by" == '["order-api", "order-api-2"]' && "$original_ok" == True && "$before" == "$after" && -n "$before" ]]; then
  check S43-existing PASS "other bodies refused 422 by both replicas; original replayed; stored order, key hash, history unchanged" "$observed"
else
  check S43-existing FAIL "$expected_refusals refusals from both replicas, original replayed, order unchanged" "$observed"
fi

# S43-race ----------------------------------------------------------------------------------------
c2="$(sim race conflicts --run-id "$RACE_RUN" --seed 43 --orders "$KEYS" --variants "$VARIANTS" --follow --follow-timeout 120 "${URLS[@]}")"
summary="$(jq_py race "r['summary']")"
race_ok="$(jq_py race "r['summary']['keysWithOneWinner'] == $KEYS and r['summary']['refusedInRace'] == $KEYS * ($VARIANTS - 1) and r['summary']['refusedOnRepeat'] == $KEYS * ($VARIANTS - 1) and r['summary']['winnerReplayedOnRepeat'] == $KEYS and r['summary']['keysAnsweredByTwoOrMoreReplicas'] == $KEYS and r['summary']['completed'] == $KEYS")"
winners="$(jq_py race "[(k['winnerVariant'], k['variants'][k['winnerVariant']]['change'] if k['winnerVariant'] is not None else None) for k in r['items']]")"
observed="exit $c2; winners (variant, change) $winners; summary $summary"
if [[ "$c2" == 0 && "$race_ok" == True ]]; then
  check S43-race PASS "per key one body accepted, the other $((VARIANTS - 1)) refused 422 in the race and on repeat; accepted body replayed; both replicas answered" "$observed"
else
  check S43-race FAIL "one winner and $((VARIANTS - 1)) refusals per key, twice" "$observed"
fi

# S43-servers -------------------------------------------------------------------------------------
{ echo "$original_id"; order_ids_of race "[k['orderId'] for k in r['items'] if k['orderId']]"; } > "$OUT_DIR/order-ids.txt"
server_outcomes "$SINCE" "$OUT_DIR/order-ids.txt" > "$OUT_DIR/server-outcomes.json"
servers="$(python -c "
import json, sys
o = json.load(open(sys.argv[1]))
a, b = o['order-api'], o['order-api-2']
print(f\"{sum(1 for i in a if a[i]['reused'] > 0 and b[i]['reused'] > 0)}|{sum(v['reused'] for v in a.values())}|{sum(v['reused'] for v in b.values())}|{sum(a[i]['accepted'] + b[i]['accepted'] for i in a)}\")" "$OUT_DIR/server-outcomes.json")"
IFS='|' read -r both reused_a reused_b accepted_total <<< "$servers"
total=$((KEYS + 1))
observed="orders with refusals logged by both replicas: $both/$total; refusals logged: order-api $reused_a, order-api-2 $reused_b; acceptances logged for these orders: $accepted_total (one per order)"
if [[ "$both" == "$total" && "$accepted_total" == "$total" ]]; then
  check S43-servers PASS "both replicas refused other bodies for every order (own logs); each order accepted once" "$observed"
else
  check S43-servers FAIL "$total/$total" "$observed"
fi

# S43-sql -----------------------------------------------------------------------------------------
sql_orders_report "$RACE_RUN-c" > "$OUT_DIR/sql-orders.jsonl"
sql_orders_report "$EXISTING_RUN-c" >> "$OUT_DIR/sql-orders.jsonl"
consistent="$(sql_consistent "$OUT_DIR/sql-orders.jsonl" "$total")"
bodies="$(python - "$OUT_DIR/race.json" "$OUT_DIR/sql-orders.jsonl" <<'PY'
import json, sys
race = json.load(open(sys.argv[1]))
rows = {json.loads(l)["orderId"]: json.loads(l) for l in open(sys.argv[2]) if l.strip()}
ok = 0; bad = []
for k in race["items"]:
    w = k["variants"][k["winnerVariant"]]
    row = rows.get(k["orderId"])
    lines = ",".join(f"{l['sku']}:{l['quantity']}" for l in w["lines"])
    if row and row["customer"] == w["customerId"] and row["lines"] == lines:
        ok += 1
    else:
        bad.append(f"{k['orderId']}: stored {row and (row['customer'], row['lines'])} vs accepted {(w['customerId'], lines)}")
print(f"{ok}|{'; '.join(bad[:2])}")
PY
)"
IFS='|' read -r bodies_ok bodies_bad <<< "$bodies"
observed="$consistent; orders holding the accepted body: $bodies_ok/$KEYS ${bodies_bad}"
if [[ "$consistent" == ok && "$bodies_ok" == "$KEYS" ]]; then
  check S43-sql PASS "each order holds the accepted body; one key, one Closed start request, Completed once, one effect per line plus completion" "$observed"
else
  check S43-sql FAIL "$total consistent orders, accepted bodies stored" "$observed"
fi

finish S43
