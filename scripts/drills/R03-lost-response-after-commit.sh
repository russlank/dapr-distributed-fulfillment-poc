#!/usr/bin/env bash
# R03 lost response after commit (S24). Drill stack with both order API replicas:
#   bash scripts/stack-up.sh --drill --replicas
# The failpoint AfterOrderAcceptanceCommit (action AbortConnection) on the order API fires after the
# acceptance transaction committed and resets the client's connection instead of answering. The
# client simulator sees a transport error (ambiguous), resends the same body and key to the other
# replica, and must get the committed order back. Assertions:
#   R03-fired      the failpoint row has FiredAt; order-api's log shows it fired for this key
#   R03-committed  the order, its key, and its start request were committed by order-api before the
#                  connection was reset (order created no later than FiredAt; order-api logged acceptance)
#   R03-client     first attempt ambiguous (transport, no answer, no X-Served-By); the retry on
#                  order-api-2 answered 200 replayed with the committed order id
#   R03-servers    order-api-2's log shows the replay of that order
#   R03-once       OrderDb: one order, one key, one start request (Closed), one started workflow,
#                  Completed once, one effect per line, one completion event
DRILL_ID=R03
. "$(dirname "$0")/lib.sh"
OUT_DIR="$DRILL_DIR"
. "$REPO_ROOT/scripts/scenarios/replica-lib.sh"

drill_preflight
require_replicas
client_sim identity > "$DRILL_DIR/client-identity.json" || { log "REFUSED: the client simulator does not build"; exit 2; }
CLIENT_SIM_BUILT=1
trap 'drill_finish; exit $?' EXIT

RUN="r03-$(date -u +%H%M%S)-$((RANDOM % 1000))"
KEY="$RUN-o00001"
SINCE="$(date -u -d '-2 seconds' +%Y-%m-%dT%H:%M:%SZ)"
arm_failpoint OrderDb AfterOrderAcceptanceCommit "$KEY" AbortConnection
before="$(sql OrderDb "SELECT COUNT(*) FROM orders.IdempotencyKey WHERE [Key] = '$KEY'")"
INJECTED_AT="$(date -u +%Y-%m-%dT%H:%M:%S.%3NZ)"
c="$(sim client single --run-id "$RUN" --seed 3 --expect new --url "$ORDER_API" --url "$ORDER_API_2" --request-timeout 10 --follow --follow-timeout 120)"
fired="$(failpoint_fired_at OrderDb AfterOrderAcceptanceCommit)"
FAULT_OBSERVED="$fired"
order_id="$(jq_py client "r['items'][0].get('orderId')")"

# R03-fired --------------------------------------------------------------------------------------
logged_fire="$(docker compose logs --no-log-prefix --since "$SINCE" order-api 2>/dev/null | grep -c "failpoint AfterOrderAcceptanceCommit fired for $KEY")"
if [[ -n "$fired" && "$logged_fire" -ge 1 ]]; then
  assert R03-fired PASS "FiredAt set; order-api logged the failpoint for the key" "FiredAt=$fired; order-api log lines=$logged_fire"
else
  assert R03-fired FAIL "fired once on order-api" "FiredAt='$fired'; order-api log lines=$logged_fire"
fi

# R03-committed ----------------------------------------------------------------------------------
echo "$order_id" > "$DRILL_DIR/order-ids.txt"
server_outcomes "$SINCE" "$DRILL_DIR/order-ids.txt" > "$DRILL_DIR/server-outcomes.json"
row="$(sql OrderDb "SELECT CONCAT(CONVERT(varchar(23), o.CreatedAt, 126), '|', CASE WHEN o.CreatedAt <= f.FiredAt THEN 1 ELSE 0 END, '|',
  (SELECT COUNT(*) FROM orders.IdempotencyKey k WHERE k.OrderId = o.OrderId), '|', (SELECT COUNT(*) FROM orders.WorkflowStartRequest r WHERE r.OrderId = o.OrderId))
  FROM orders.[Order] o CROSS JOIN drill.Failpoint f WHERE o.OrderId = '$order_id' AND f.DrillRunId = '$DRILL_RUN_ID' AND f.Name = 'AfterOrderAcceptanceCommit'")"
IFS='|' read -r created before_fire keys requests <<< "$row"
accepted_by_1="$(python -c "import json,sys; print(json.load(open(sys.argv[1]))['order-api'][sys.argv[2]]['accepted'])" "$DRILL_DIR/server-outcomes.json" "$order_id" 2>/dev/null)"
# The commit is proven by rows that exist although order-api never answered, and by its acceptance log
# line (written after SaveChanges returned, before the failpoint). The timestamps are reported only:
# the SQL clock steps back in this environment (see the journal).
observed="key rows before the request: $before; order $order_id created $created (no later than FiredAt by SQL time: $before_fire); key rows $keys, start requests $requests; order-api logged its acceptance: $accepted_by_1"
if [[ "$before" == 0 && "$keys" == 1 && "$requests" == 1 && "$accepted_by_1" == 1 && -n "$fired" ]]; then
  assert R03-committed PASS "order, key, start request committed by order-api before the connection was reset" "$observed"
else
  assert R03-committed FAIL "committed before the failpoint fired" "$observed"
fi

# R03-client -------------------------------------------------------------------------------------
attempts="$(jq_py client "[(a['endpoint'], a['class'], a.get('code'), a.get('httpStatus'), a.get('servedBy')) for a in r['items'][0]['attempts']]")"
client_ok="$(jq_py client "(lambda i: len(i['attempts']) == 2 and i['attempts'][0]['class'] == 'ambiguous' and i['attempts'][0].get('code') == 'transport' and i['attempts'][0].get('servedBy') is None and i['attempts'][0]['endpoint'] == 'replica-1' and i['attempts'][1].get('httpStatus') == 200 and i['attempts'][1].get('servedBy') == 'order-api-2' and i['acceptance'] == 'replayed' and i['orderId'] == '$order_id')(r['items'][0])")"
observed="exit $c; attempts (endpoint, class, code, http, served by): $attempts; acceptance $(jq_py client "r['items'][0]['acceptance']"); error $(jq_py client "r['items'][0]['attempts'][0].get('error')")"
if [[ "$c" == 0 && "$client_ok" == True ]]; then
  assert R03-client PASS "transport error (ambiguous) on order-api, then 200 replayed from order-api-2 with the committed order" "$observed"
else
  assert R03-client FAIL "ambiguous, then replay of the same order on the other replica" "$observed"
fi

# R03-servers ------------------------------------------------------------------------------------
replayed_by_2="$(python -c "import json,sys; print(json.load(open(sys.argv[1]))['order-api-2'][sys.argv[2]]['replayed'])" "$DRILL_DIR/server-outcomes.json" "$order_id" 2>/dev/null)"
if [[ "$replayed_by_2" -ge 1 ]]; then
  assert R03-servers PASS "order-api-2 answered the retry as a replay (its log)" "order-api-2 replay lines for $order_id: $replayed_by_2"
else
  assert R03-servers FAIL "a replay on order-api-2" "order-api-2 replay lines: '$replayed_by_2'"
fi

# R03-once ---------------------------------------------------------------------------------------
sql_orders_report "$RUN-c" > "$DRILL_DIR/sql-orders.jsonl"
consistent="$(sql_consistent "$DRILL_DIR/sql-orders.jsonl" 1)"
started="$(sql OrderDb "SELECT COUNT(*) FROM orders.WorkflowStartAttempt WHERE OrderId = '$order_id' AND Result = 'Started'")"
RECOVERED_AT="$(date -u +%Y-%m-%dT%H:%M:%S.%3NZ)"
observed="$consistent; started workflows $started; final status $(jq_py client "r['items'][0]['follow']['finalStatus']"); $(head -1 "$DRILL_DIR/sql-orders.jsonl")"
if [[ "$consistent" == ok && "$started" == 1 ]]; then
  assert R03-once PASS "one order, key, Closed start request, one started workflow, Completed once, one effect per line, one completion event" "$observed"
else
  assert R03-once FAIL "one of each" "$observed"
fi
