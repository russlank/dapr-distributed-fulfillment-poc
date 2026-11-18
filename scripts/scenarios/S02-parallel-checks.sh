#!/usr/bin/env bash
# S02 parallel independent checks. Runs on any ready stack (release or drill; with --replicas the
# checks may run on different compute-worker replicas). For N orders the fulfillment run schedules
# the pricing and the risk check together; each check records where and when it ran (compute
# replica clock; the replicas share the Docker host's clock) and takes a simulated computation time.
# PASS when, from OrderDb rows:
#   S02-1 completed   every order is Completed with both check results stored once
#   S02-2 overlap     for every order the two checks' execution intervals overlap
#                     (max(start) < min(end)): they ran concurrently, not one after the other;
#                     and the check phase (min start to max end) is shorter than the sum of both
# Usage: scripts/scenarios/S02-parallel-checks.sh   (exit 0 only if every check passed)
. "$(dirname "$0")/../lib.sh"

RUN_ID="S02-$(date -u +%Y%m%dT%H%M%SZ)"
OUT_DIR="$REPO_ROOT/artifacts/scenarios/$RUN_ID"
mkdir -p "$OUT_DIR"
RESULTS_FILE="$OUT_DIR/results.jsonl"
N="${S02_ORDERS:-10}"
CUSTOMER_PREFIX="s02-$(date -u +%H%M%S)-$RANDOM"
failures=0
check() { [[ "$2" == PASS ]] || failures=$((failures + 1)); record "$@"; }

log "S02 $RUN_ID: $N orders, evidence in $OUT_DIR"
for i in $(seq 1 "$N"); do
  curl -s -o /dev/null -w '%{http_code}\n' -X POST "$ORDER_API/orders" -H "X-Api-Key: $CUSTOMER_API_KEY" \
    -H "Idempotency-Key: $CUSTOMER_PREFIX-$i" -H 'content-type: application/json' \
    -d '{"customerId":"'$CUSTOMER_PREFIX-$i'","lines":[{"sku":"SKU-1","quantity":2},{"sku":"SKU-2","quantity":1}]}' >> "$OUT_DIR/accepted.txt"
done
run_orders="SELECT OrderId FROM orders.[Order] WHERE CustomerId LIKE '$CUSTOMER_PREFIX-%'"
all_completed() { [[ "$(sql OrderDb "SELECT COUNT(*) FROM orders.[Order] WHERE Status = 'Completed' AND OrderId IN ($run_orders)")" == "$N" ]]; }
wait_until 120 "all $N orders Completed" all_completed

sql OrderDb "SELECT CONCAT(c.OrderId, ' ', c.CheckName, ' ', c.Replica, ' ', CONVERT(varchar(23), c.StartedAt, 126), ' ', CONVERT(varchar(23), c.CompletedAt, 126), ' replays=', c.Replays)
  FROM orders.OrderCheck c WHERE c.OrderId IN ($run_orders) ORDER BY c.OrderId, c.CheckName" > "$OUT_DIR/checks.txt"

# S02-1 completed ----------------------------------------------------------------------------------
done_row="$(sql OrderDb "SELECT CONCAT(
  (SELECT COUNT(*) FROM orders.[Order] WHERE Status = 'Completed' AND OrderId IN ($run_orders)), '|',
  (SELECT COUNT(*) FROM (SELECT OrderId FROM orders.OrderCheck WHERE OrderId IN ($run_orders) GROUP BY OrderId HAVING COUNT(*) = 2) x))")"
IFS='|' read -r completed with_checks <<< "$done_row"
if [[ "$(grep -c '^201$' "$OUT_DIR/accepted.txt")" == "$N" && "$completed" == "$N" && "$with_checks" == "$N" ]]; then
  check S02-1 PASS "$N orders Completed, each with a price and a risk result" "completed=$completed with both checks=$with_checks"
else
  check S02-1 FAIL "$N/$N" "accepted=$(grep -c '^201$' "$OUT_DIR/accepted.txt") completed=$completed with both checks=$with_checks"
fi

# S02-2 overlap ------------------------------------------------------------------------------------
overlap="$(sql OrderDb "SELECT CONCAT(
  SUM(CASE WHEN LastStart < FirstEnd THEN 1 ELSE 0 END), '|',
  SUM(CASE WHEN Phase < Total THEN 1 ELSE 0 END), '|',
  MIN(DATEDIFF(millisecond, LastStart, FirstEnd)), '|',
  AVG(Phase), '|', AVG(Total), '|',
  SUM(CASE WHEN Replicas = 2 THEN 1 ELSE 0 END))
  FROM (SELECT OrderId, MAX(StartedAt) AS LastStart, MIN(CompletedAt) AS FirstEnd,
               DATEDIFF(millisecond, MIN(StartedAt), MAX(CompletedAt)) AS Phase,
               SUM(DATEDIFF(millisecond, StartedAt, CompletedAt)) AS Total,
               COUNT(DISTINCT Replica) AS Replicas
        FROM orders.OrderCheck WHERE OrderId IN ($run_orders) GROUP BY OrderId) o")"
IFS='|' read -r overlapping shorter min_overlap avg_phase avg_total cross <<< "$overlap"
observed="orders with overlapping checks=$overlapping/$N; phase shorter than the sum=$shorter/$N; smallest overlap ${min_overlap} ms; mean phase ${avg_phase} ms vs mean sum ${avg_total} ms; checks on two replicas: $cross orders"
if [[ "$overlapping" == "$N" && "$shorter" == "$N" && "${min_overlap:-0}" -gt 0 ]]; then
  check S02-2 PASS "for every order the two checks ran at the same time (intervals overlap; phase < sum)" "$observed"
else
  check S02-2 FAIL "$N/$N overlapping, $N/$N shorter than the sum" "$observed"
fi

log "S02 finished: $failures failed check(s); results in $RESULTS_FILE"
(( failures == 0 ))
