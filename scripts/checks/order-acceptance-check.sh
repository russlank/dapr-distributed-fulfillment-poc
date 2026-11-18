#!/usr/bin/env bash
# Durable order acceptance on the running stack, proven from OrderDb rows (not only HTTP codes):
#   ACC-1 accept      a new order commits order, lines, first status row, key, and a Pending start
#                     request with the derived instance id, all at once
#   ACC-2 replay      the same key and payload (on the second replica when it runs) returns the
#                     original order and writes nothing
#   ACC-3 conflict    the same key with another payload returns 422 and writes nothing
#   ACC-4 replicas    concurrent submissions with one key, alternating order-api and order-api-2,
#                     create exactly one order (needs the replicas profile; UNVERIFIED otherwise)
#   ACC-5 restart     after a cold restart of the order API the retry returns the same order
#   ACC-6 no start    acceptance started no workflow: no state for the order's instance id in
#                     DaprStateDb (starting is the start dispatcher's job)
# Usage: scripts/checks/order-acceptance-check.sh   (exit 0 only if no check failed)
. "$(dirname "$0")/../lib.sh"

RUN_ID="ACC-$(date -u +%Y%m%dT%H%M%SZ)"
OUT_DIR="$REPO_ROOT/artifacts/checks/$RUN_ID"
mkdir -p "$OUT_DIR"
RESULTS_FILE="$OUT_DIR/results.jsonl"
failures=0
check() { [[ "$2" == FAIL ]] && failures=$((failures + 1)); record "$@"; }

CUSTOMER="acc-$(date -u +%H%M%S)-$RANDOM"
KEY="acc-key-$CUSTOMER"
BODY='{"customerId":"'$CUSTOMER'","lines":[{"sku":"SKU-1","quantity":2},{"sku":"SKU-2","quantity":1}]}'
OTHER_BODY='{"customerId":"'$CUSTOMER'","lines":[{"sku":"SKU-1","quantity":3}]}'

# submit <base url> <key> <body> -> "<status> <body>"
submit() {
  local out
  out="$(curl -s -w '\n%{http_code}' -X POST "$1/orders" -H "X-Api-Key: $CUSTOMER_API_KEY" \
    -H "Idempotency-Key: $2" -H 'content-type: application/json' -d "$3")"
  printf '%s %s\n' "${out##*$'\n'}" "$(printf '%s' "${out%$'\n'*}" | tr -d '\r\n')"
}
counts() {
  sql OrderDb "SELECT CONCAT(
    (SELECT COUNT(*) FROM orders.[Order] WHERE CustomerId = '$CUSTOMER'), '/',
    (SELECT COUNT(*) FROM orders.OrderLine l JOIN orders.[Order] o ON o.OrderId = l.OrderId WHERE o.CustomerId = '$CUSTOMER'), '/',
    (SELECT COUNT(*) FROM orders.OrderStatusHistory h JOIN orders.[Order] o ON o.OrderId = h.OrderId WHERE o.CustomerId = '$CUSTOMER'), '/',
    (SELECT COUNT(*) FROM orders.IdempotencyKey k JOIN orders.[Order] o ON o.OrderId = k.OrderId WHERE o.CustomerId = '$CUSTOMER'), '/',
    (SELECT COUNT(*) FROM orders.WorkflowStartRequest r JOIN orders.[Order] o ON o.OrderId = r.OrderId WHERE o.CustomerId = '$CUSTOMER'))"
}
replica2_running() { [[ "$(docker compose --profile replicas ps --format '{{.State}}' order-api-2 2>/dev/null)" == running ]]; }

log "order acceptance check $RUN_ID, customer $CUSTOMER, evidence in $OUT_DIR"

# ACC-1 accept ------------------------------------------------------------------------------------
read -r status body <<< "$(submit "$ORDER_API" "$KEY" "$BODY")"
echo "accept: $status $body" >> "$OUT_DIR/responses.txt"
ORDER_ID="$(json_get "$body" orderId)"
NORMALIZED="$(echo "$ORDER_ID" | tr -d '-')"
row="$(sql OrderDb "SELECT CONCAT(o.Status, '|', r.State, '|', r.InstanceId, '|', r.Attempts, '|', r.ExecutionClaims, '|',
  (SELECT STRING_AGG(CONCAT(LineNumber, ':', Sku, ':', Quantity), ',') WITHIN GROUP (ORDER BY LineNumber) FROM orders.OrderLine WHERE OrderId = o.OrderId), '|',
  (SELECT CONCAT(Sequence, ':', ToStatus, ':', ReasonCode) FROM orders.OrderStatusHistory WHERE OrderId = o.OrderId))
  FROM orders.[Order] o JOIN orders.WorkflowStartRequest r ON r.OrderId = o.OrderId WHERE o.OrderId = '$ORDER_ID'")"
expected_row="Submitted|Pending|fulfill-$NORMALIZED|0|0|1:SKU-1:2,2:SKU-2:1|1:Submitted:Accepted"
if [[ "$status" == 201 && "$row" == "$expected_row" && "$(counts)" == "1/2/1/1/1" ]]; then
  check ACC-1 PASS "201; order, 2 lines, 1 status row, key, Pending start request" "order $ORDER_ID: $row; rows $(counts)"
else
  check ACC-1 FAIL "201 and $expected_row, rows 1/2/1/1/1" "status=$status row=$row rows=$(counts)"
fi

# ACC-2 replay ------------------------------------------------------------------------------------
replay_base="$ORDER_API"; replica2_running && replay_base="$ORDER_API_2"
read -r status body <<< "$(submit "$replay_base" "$KEY" "$BODY")"
echo "replay ($replay_base): $status $body" >> "$OUT_DIR/responses.txt"
if [[ "$status" == 200 && "$(json_get "$body" orderId)" == "$ORDER_ID" && "$(json_get "$body" replayed)" == True && "$(counts)" == "1/2/1/1/1" ]]; then
  check ACC-2 PASS "200, original order, nothing written" "via $replay_base: replayed order $ORDER_ID; rows $(counts)"
else
  check ACC-2 FAIL "200 replayed $ORDER_ID, rows 1/2/1/1/1" "via $replay_base: status=$status body=$body rows=$(counts)"
fi

# ACC-3 conflict ----------------------------------------------------------------------------------
read -r status body <<< "$(submit "$ORDER_API" "$KEY" "$OTHER_BODY")"
echo "conflict: $status $body" >> "$OUT_DIR/responses.txt"
if [[ "$status" == 422 && "$(json_get "$body" code)" == idempotency-key-reused && "$(counts)" == "1/2/1/1/1" ]]; then
  check ACC-3 PASS "422 idempotency-key-reused, nothing written" "status 422; rows $(counts)"
else
  check ACC-3 FAIL "422 idempotency-key-reused, rows 1/2/1/1/1" "status=$status body=$body rows=$(counts)"
fi

# ACC-4 concurrent submissions across replicas -----------------------------------------------------
if replica2_running; then
  RACE_CUSTOMER="$CUSTOMER-r"; RACE_KEY="acc-race-$RACE_CUSTOMER"
  RACE_BODY='{"customerId":"'$RACE_CUSTOMER'","lines":[{"sku":"SKU-3","quantity":1}]}'
  for i in $(seq 1 20); do
    base="$ORDER_API"; (( i % 2 == 0 )) && base="$ORDER_API_2"
    submit "$base" "$RACE_KEY" "$RACE_BODY" > "$OUT_DIR/race-$i.txt" &
  done
  wait
  summary="$(cat "$OUT_DIR"/race-*.txt | python -c "
import json, sys
rows = [line.split(' ', 1) for line in sys.stdin.read().splitlines()]
codes = [r[0] for r in rows]
ids = {json.loads(r[1]).get('orderId') for r in rows if r[0] in ('200', '201')}
print(f\"{codes.count('201')} {codes.count('200')} {len(codes) - codes.count('201') - codes.count('200')} {len(ids)}\")")"
  read -r created replayed others distinct <<< "$summary"
  race_rows="$(sql OrderDb "SELECT COUNT(*) FROM orders.[Order] WHERE CustomerId = '$RACE_CUSTOMER'")"
  if [[ "$created" == 1 && "$replayed" == 19 && "$others" == 0 && "$distinct" == 1 && "$race_rows" == 1 ]]; then
    check ACC-4 PASS "20 concurrent submissions, one key, two replicas: one order" "201 x$created, 200 x$replayed, one order id; orders in SQL=$race_rows"
  else
    check ACC-4 FAIL "201 x1, 200 x19, one order" "201 x$created, 200 x$replayed, other x$others, ids=$distinct, orders in SQL=$race_rows"
  fi
else
  check ACC-4 UNVERIFIED "concurrent submissions across order-api and order-api-2" "order-api-2 not running (start with scripts/stack-up.sh --replicas)"
fi

# ACC-5 retry after a cold restart ------------------------------------------------------------------
restart_pair order-api
read -r status body <<< "$(submit "$ORDER_API" "$KEY" "$BODY")"
echo "after restart: $status $body" >> "$OUT_DIR/responses.txt"
if [[ "$status" == 200 && "$(json_get "$body" orderId)" == "$ORDER_ID" && "$(counts)" == "1/2/1/1/1" ]]; then
  check ACC-5 PASS "after a cold restart the retry returns the same order" "replayed order $ORDER_ID; rows $(counts)"
else
  check ACC-5 FAIL "200 replayed $ORDER_ID" "status=$status body=$body rows=$(counts)"
fi

# ACC-6 no workflow was started by acceptance -------------------------------------------------------
state_rows="$(sql DaprStateDb "SELECT COUNT(*) FROM dbo.state WHERE [Key] LIKE '%||fulfill-$NORMALIZED||%'")"
start_state="$(sql OrderDb "SELECT State FROM orders.WorkflowStartRequest WHERE OrderId = '$ORDER_ID'")"
if [[ "$state_rows" == 0 && "$start_state" == Pending ]]; then
  check ACC-6 PASS "no workflow state for fulfill-<orderId>; start request still Pending" "DaprStateDb rows=0; start request $start_state"
else
  check ACC-6 FAIL "0 workflow state rows, start request Pending" "DaprStateDb rows=$state_rows; start request $start_state"
fi

log "order acceptance check finished: $failures failed check(s); results in $RESULTS_FILE"
(( failures == 0 ))
