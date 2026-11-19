#!/usr/bin/env bash
# S24 client timeout, then retry, across replicas. Needs both order API replicas
# (bash scripts/stack-up.sh --replicas, release or drill). The client reaches the replicas through
# Toxiproxy's client path (order-api via 18080, order-api-2 via 18081); faults are network toxics,
# not failpoints (the failpoint drill R03 is separate). Every toxic is removed on exit.
# The simulator waits 2 s per attempt and retries an ambiguous attempt on the other replica with
# the same body and key. Orders alternate their first replica (odd: order-api, even: order-api-2).
# PASS when:
#   S24-lost       response lost after the commit (order-api's answers dropped): the odd orders'
#                  first attempt times out, the retry on order-api-2 is answered 200 replayed with
#                  the same order; order-api's log shows it accepted those orders, order-api-2's that
#                  it replayed them
#   S24-unsent     timeout before the request reached the server (requests to order-api dropped):
#                  the retry on order-api-2 creates the order (201, not a replay); order-api never
#                  logged those orders, also after the toxic is gone
#   S24-delayed    a hedged retry: the original request is delayed 4 s on its way to order-api while
#                  the same request goes to order-api-2 after 1 s and creates the order (201); the
#                  delayed original reaches order-api afterwards and is answered 200 replayed with the
#                  same order (order-api's log: replay); one order. (A request whose client gave up is
#                  dropped by the proxy with its connection, so the late arrival needs a waiting client.)
#   S24-unresolved both replicas' answers lost: the client reports unresolved (exit 4) although SQL
#                  holds each order once; after the network recovers, a replay of the run returns
#                  exactly those orders, which then complete
#   S24-sql        OrderDb for every order of the scenario: one key row, one Closed start request,
#                  Completed once, one effect per line plus completion, no fence rejection
# Evidence in artifacts/scenarios/S24-<time>/.
. "$(dirname "$0")/../lib.sh"
. "$(dirname "$0")/replica-lib.sh"

RUN_ID="S24-$(date -u +%Y%m%dT%H%M%SZ)"
OUT_DIR="$REPO_ROOT/artifacts/scenarios/$RUN_ID"
mkdir -p "$OUT_DIR"
RESULTS_FILE="$OUT_DIR/results.jsonl"
N="${S24_ORDERS:-4}"
STAMP="$(date -u +%Y%m%d-%H%M%S)-$((RANDOM % 1000))"
P1=order-api-client
P2=order-api-2-client
PROXIES=(--url "$ORDER_API_PROXY" --url "$ORDER_API_2_PROXY")
CLIENT=(--request-timeout 2 --max-attempts 3 --concurrency 2)

require_replicas
trap 'toxic_clear $P1 $P2' EXIT
toxic_clear $P1 $P2
for proxy in $P1 $P2; do
  [[ "$(curl -s -m 5 -o /dev/null -w '%{http_code}' "$TOXIPROXY/proxies/$proxy")" == 200 ]] \
    || { log "REFUSED: Toxiproxy proxy $proxy is missing (stack started with the current deploy/compose/toxiproxy.json?)"; exit 2; }
done
client_sim identity > "$OUT_DIR/identity.json" || { log "REFUSED: the client simulator does not build"; exit 2; }
CLIENT_SIM_BUILT=1
SINCE="$(date -u -d '-2 seconds' +%Y-%m-%dT%H:%M:%SZ 2>/dev/null || date -u +%Y-%m-%dT%H:%M:%SZ)"
log "S24 $RUN_ID: $N orders per case through $ORDER_API_PROXY and $ORDER_API_2_PROXY; evidence in $OUT_DIR"

# The proxies reach the replicas they name (the servers say who answered).
c="$(sim path single --run-id "s24p-$STAMP" --seed 24 --expect new "${PROXIES[@]}" --replica 1)"
c2="$(sim path-2 single --run-id "s24q-$STAMP" --seed 24 --expect new "${PROXIES[@]}" --replica 2)"
path_ok="$(jq_py path "r['items'][0]['attempts'][-1].get('servedBy')")/$(jq_py path-2 "r['items'][0]['attempts'][-1].get('servedBy')")"
[[ "$c$c2" == 00 && "$path_ok" == order-api/order-api-2 ]] || { log "REFUSED: the client path does not reach the replicas as expected ($c/$c2, $path_ok)"; exit 2; }

odd_even() { # <result name> -> JSON per order: index, acceptance, attempts (class:code@servedBy), order id
  jq_py "$1" "[ (i['index'], i['acceptance'], ['%s:%s@%s' % (a['class'], a.get('code') or a.get('httpStatus'), a.get('servedBy')) for a in i['attempts']], i.get('orderId')) for i in r['items'] ]"
}
ids_where() { jq_py "$1" "[i['orderId'] for i in r['items'] if i.get('orderId') and $2]" | python -c "import json,sys; print('\n'.join(json.load(sys.stdin)))"; }
outcomes_for() { # <ids file> -> JSON server outcomes
  server_outcomes "$SINCE" "$1"
}
sum_kind() { python -c "import json,sys; o=json.load(open(sys.argv[1])); print(sum(v[sys.argv[3]] for v in o[sys.argv[2]].values()))" "$@"; }

# S24-lost ----------------------------------------------------------------------------------------
LOST_RUN="s24l-$STAMP"
[[ "$(toxic_add $P1 lose-response timeout downstream '{"timeout":0}')" == 200 ]] || log "could not add the downstream toxic"
c="$(sim lost load --run-id "$LOST_RUN" --seed 24 --orders "$N" --follow --follow-timeout 120 "${CLIENT[@]}" "${PROXIES[@]}")"
toxic_clear $P1
ids_where lost "i['index'] % 2 == 1" > "$OUT_DIR/lost-odd-ids.txt"
outcomes_for "$OUT_DIR/lost-odd-ids.txt" > "$OUT_DIR/lost-server.json"
odd_ok="$(jq_py lost "sum(1 for i in r['items'] if i['index'] % 2 == 1 and i['acceptance'] == 'replayed' and len(i['attempts']) == 2 and i['attempts'][0]['class'] == 'ambiguous' and i['attempts'][0].get('code') == 'timeout' and i['attempts'][1].get('servedBy') == 'order-api-2' and i['follow']['finalStatus'] == 'Completed')")"
even_ok="$(jq_py lost "sum(1 for i in r['items'] if i['index'] % 2 == 0 and i['acceptance'] == 'created' and len(i['attempts']) == 1 and i['follow']['finalStatus'] == 'Completed')")"
odd=$(( (N + 1) / 2 )); even=$(( N / 2 ))
acc1="$(sum_kind "$OUT_DIR/lost-server.json" order-api accepted)"; rep2="$(sum_kind "$OUT_DIR/lost-server.json" order-api-2 replayed)"
observed="exit $c; odd orders replayed after a timeout on order-api: $odd_ok/$odd, even created directly: $even_ok/$even; order-api accepted (committed) the odd orders: $acc1/$odd, order-api-2 replayed them: $rep2; attempts $(odd_even lost)"
if [[ "$c" == 0 && "$odd_ok" == "$odd" && "$even_ok" == "$even" && "$acc1" == "$odd" && "$rep2" -ge "$odd" ]]; then
  check S24-lost PASS "answer lost after commit on order-api: timeout, retry on order-api-2 = replay of the committed order; both replicas' logs agree" "$observed"
else
  check S24-lost FAIL "timeouts resolved as replays of orders committed by order-api" "$observed"
fi

# S24-unsent --------------------------------------------------------------------------------------
UNSENT_RUN="s24u-$STAMP"
[[ "$(toxic_add $P1 drop-request timeout upstream '{"timeout":0}')" == 200 ]] || log "could not add the upstream toxic"
c="$(sim unsent load --run-id "$UNSENT_RUN" --seed 24 --orders "$N" --follow --follow-timeout 120 "${CLIENT[@]}" "${PROXIES[@]}")"
toxic_clear $P1
sleep 5   # a dropped request must stay dropped after the toxic is removed
ids_where unsent "i['index'] % 2 == 1" > "$OUT_DIR/unsent-odd-ids.txt"
outcomes_for "$OUT_DIR/unsent-odd-ids.txt" > "$OUT_DIR/unsent-server.json"
odd_ok="$(jq_py unsent "sum(1 for i in r['items'] if i['index'] % 2 == 1 and i['acceptance'] == 'created' and len(i['attempts']) == 2 and i['attempts'][0].get('code') == 'timeout' and i['attempts'][1].get('servedBy') == 'order-api-2' and i['follow']['finalStatus'] == 'Completed')")"
seen1="$(python -c "import json,sys; o=json.load(open(sys.argv[1])); print(sum(sum(v.values()) for v in o['order-api'].values()))" "$OUT_DIR/unsent-server.json")"
acc2="$(sum_kind "$OUT_DIR/unsent-server.json" order-api-2 accepted)"
observed="exit $c; odd orders created by the retry on order-api-2 after a timeout: $odd_ok/$odd; order-api log lines for them: $seen1 (never reached it); order-api-2 accepted them: $acc2/$odd; attempts $(odd_even unsent)"
if [[ "$c" == 0 && "$odd_ok" == "$odd" && "$seen1" == 0 && "$acc2" == "$odd" ]]; then
  check S24-unsent PASS "request dropped before order-api: timeout, the retry on order-api-2 creates the order (201, not a replay); order-api never saw it" "$observed"
else
  check S24-unsent FAIL "timeouts resolved as first creations on order-api-2; nothing on order-api" "$observed"
fi

# S24-delayed -------------------------------------------------------------------------------------
# A hedged retry: the original request stays in flight behind a 4 s delay on the way to order-api
# (the client keeps waiting), while the same request (same key and body) goes to order-api-2 after
# 1 s and creates the order. The delayed original then reaches order-api and must be a replay.
DELAYED_RUN="s24d-$STAMP"
[[ "$(toxic_add $P1 delay-request latency upstream '{"latency":4000,"jitter":0}')" == 200 ]] || log "could not add the latency toxic"
client_sim single --run-id "$DELAYED_RUN" --seed 24 --url "$ORDER_API_PROXY" --request-timeout 15 --max-attempts 1   > "$OUT_DIR/delayed-original.json" 2> "$OUT_DIR/delayed-original.log" &
original_pid=$!
sleep 1
c_retry="$(sim delayed-retry single --run-id "$DELAYED_RUN" --seed 24 --url "$ORDER_API_2_PROXY" --max-attempts 1 --follow --follow-timeout 120)"
wait $original_pid; c_original=$?
toxic_clear $P1
ids_where delayed-retry "True" > "$OUT_DIR/delayed-ids.txt"
outcomes_for "$OUT_DIR/delayed-ids.txt" > "$OUT_DIR/delayed-server.json"
retry="$(jq_py delayed-retry "[r['items'][0]['acceptance'], r['items'][0]['attempts'][-1].get('servedBy'), r['items'][0]['follow']['finalStatus']]")"
original="$(jq_py delayed-original "[r['items'][0]['acceptance'], r['items'][0]['attempts'][-1].get('servedBy'), r['items'][0]['attempts'][-1]['elapsedMs'] >= 3500]")"
same_order="$(python -c "import json,sys; a=json.load(open(sys.argv[1]))['items'][0].get('orderId'); b=json.load(open(sys.argv[2]))['items'][0].get('orderId'); print(a is not None and a == b)" "$OUT_DIR/delayed-original.json" "$OUT_DIR/delayed-retry.json")"
late1="$(python -c "import json,sys; o=json.load(open(sys.argv[1])); print(sum(v['replayed'] + v['concurrent'] for v in o['order-api'].values()))" "$OUT_DIR/delayed-server.json")"
acc2="$(sum_kind "$OUT_DIR/delayed-server.json" order-api-2 accepted)"
orders_delayed="$(sql OrderDb "SELECT COUNT(*) FROM orders.[Order] WHERE CustomerId LIKE '$DELAYED_RUN-c%'")"
observed="retry exit $c_retry (acceptance, server, final status): $retry; delayed original exit $c_original (acceptance, server, waited >= 3.5 s): $original; same order: $same_order; order-api-2 accepted it: $acc2, order-api answered the late original as a replay: $late1; orders in SQL: $orders_delayed/1"
if [[ "$c_retry" == 0 && "$retry" == '["created", "order-api-2", "Completed"]' && "$c_original" == 0 && "$original" == '["replayed", "order-api", true]'       && "$same_order" == True && "$acc2" == 1 && "$late1" == 1 && "$orders_delayed" == 1 ]]; then
  check S24-delayed PASS "hedged retry: the retry on order-api-2 created the order; the delayed original reached order-api later and was answered as a replay of it; one order" "$observed"
else
  check S24-delayed FAIL "retry creates, late original replays the same order, one order" "$observed"
fi

# S24-unresolved ----------------------------------------------------------------------------------
UNRES_RUN="s24x-$STAMP"
toxic_add $P1 lose-response timeout downstream '{"timeout":0}' > /dev/null
toxic_add $P2 lose-response timeout downstream '{"timeout":0}' > /dev/null
c="$(sim unresolved load --run-id "$UNRES_RUN" --seed 24 --orders 2 --request-timeout 2 --max-attempts 2 "${PROXIES[@]}")"
in_sql="$(sql OrderDb "SELECT COUNT(*) FROM orders.[Order] WHERE CustomerId LIKE '$UNRES_RUN-c%'")"
toxic_clear $P1 $P2
c_replay="$(sim unresolved-replay load --replay --run-id "$UNRES_RUN" --seed 24 --orders 2 --follow --follow-timeout 120 "${PROXIES[@]}")"
sql_ids="$(sql OrderDb "SELECT LOWER(CONVERT(varchar(36), OrderId)) FROM orders.[Order] WHERE CustomerId LIKE '$UNRES_RUN-c%' ORDER BY OrderId" | tr -d ' ' | sort | paste -sd, -)"
replay_ids="$(jq_py unresolved-replay "','.join(sorted(i['orderId'] for i in r['items']))")"
observed="blind run exit $c ($(jq_py unresolved "r['summary']['unresolved']") unresolved), orders already in SQL: $in_sql/2; after recovery replay exit $c_replay: replayed $(jq_py unresolved-replay "r['summary']['replayed']")/2, same orders as in SQL: $([[ "$sql_ids" == "$replay_ids" ]] && echo yes || echo NO), completed $(jq_py unresolved-replay "r['summary']['completed']")/2"
if [[ "$c" == 4 && "$in_sql" == 2 && "$c_replay" == 0 && "$sql_ids" == "$replay_ids" && "$(jq_py unresolved-replay "r['summary']['completed']")" == 2 ]]; then
  check S24-unresolved PASS "no answer: unresolved (exit 4) while SQL holds each order once; the replay after recovery returns those orders, completed" "$observed"
else
  check S24-unresolved FAIL "exit 4, 2 orders, replay returns them" "$observed"
fi

# S24-sql -----------------------------------------------------------------------------------------
: > "$OUT_DIR/sql-orders.jsonl"
for run in "$LOST_RUN" "$UNSENT_RUN" "$DELAYED_RUN" "$UNRES_RUN"; do sql_orders_report "$run-c" >> "$OUT_DIR/sql-orders.jsonl"; done
consistent="$(sql_consistent "$OUT_DIR/sql-orders.jsonl" $(( N * 2 + 3 )))"
keys_rows="$(sql OrderDb "SELECT COUNT(*) FROM orders.IdempotencyKey WHERE [Key] LIKE 's24[ludx]-$STAMP-o%'")"
observed="$consistent; key rows $keys_rows/$(( N * 2 + 3 )); effect replays $(python -c "import json,sys; print(sum(json.loads(l)['effectReplays'] for l in open(sys.argv[1])))" "$OUT_DIR/sql-orders.jsonl")"
if [[ "$consistent" == ok && "$keys_rows" == "$(( N * 2 + 3 ))" ]]; then
  check S24-sql PASS "every order once: one key, one Closed start request, Completed once, one effect per line plus completion" "$observed"
else
  check S24-sql FAIL "$(( N * 2 + 3 )) consistent orders" "$observed"
fi

finish S24
