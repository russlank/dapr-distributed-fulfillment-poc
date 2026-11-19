#!/usr/bin/env bash
# S20 failed and retrying orders do not block a hot SKU. Needs failpoints, so it runs on the drill
# stack with replicas (bash scripts/stack-up.sh --drill --replicas) under the drill harness; evidence
# in artifacts/drills/S20-<time>/.
# Hot SKU H stocked 16 in W1. P1 asks for H x5 plus a SKU nobody stocks (Rejected). P2 asks for H x1
# plus SKU T whose stock actor fails every attempt (BeforeStockReserve Throw x5 on W1|T), so P2 holds
# its unit of H while Dapr retries T. While P2 retries, 10 ordinary one-unit orders for H are sent.
#   S20-normal    the 10 ordinary orders all Completed, and they completed while P2 still held its unit
#                 of H (P2's hold on H Held when the last ordinary order completed)
#   S20-failures  P1 Rejected (InsufficientStock), P2 Compensated (ReservationFailed); their H holds
#                 Released once; the T failpoint fired 5 times
#   S20-stock     H ends OnHand 6 and Reserved 0: 10 consumed, 6 released; invariants clean
DRILL_ID=S20
. "$(dirname "$0")/../drills/lib.sh"
OUT_DIR="$DRILL_DIR"
. "$REPO_ROOT/scripts/scenarios/replica-lib.sh"

drill_preflight
require_replicas
client_sim identity > "$DRILL_DIR/client-identity.json" || { log "REFUSED: the client simulator does not build"; exit 2; }
CLIENT_SIM_BUILT=1
trap 'drill_finish; exit $?' EXIT
URLS=(--url "$ORDER_API" --url "$ORDER_API_2")
TAG="$(printf '%04X' $((RANDOM % 65536)))"
# Lines are reserved in ordinal SKU order, so H (S20H) comes before T (S20T) and the missing SKU (S20Z).
H="SKU-S20H-$TAG"; T="SKU-S20T-$TAG"; Z="SKU-S20Z-$TAG"
set_stock W1 "$H" 16; set_stock W1 "$T" 5
RUN="s20-$(date -u +%H%M%S)-$((RANDOM % 1000))"
order_of() { jq_py "$1" "r['items'][0].get('orderId')"; }
end_of() { sql OrderDb "SELECT CONCAT(o.Status, '/', (SELECT TOP (1) h.ReasonCode FROM orders.OrderStatusHistory h WHERE h.OrderId = o.OrderId ORDER BY h.Sequence DESC)) FROM orders.[Order] o WHERE o.OrderId = '$1'"; }

arm_failpoint_times InventoryDb BeforeStockReserve "W1|$T" Throw 5
INJECTED_AT="$(date -u +%Y-%m-%dT%H:%M:%S.%3NZ)"
c1="$(sim p1 single --run-id "$RUN-p1" --line "$H:5" --line "$Z:1" --expect new "${URLS[@]}" --follow --follow-timeout 180)"
c2="$(sim p2 single --run-id "$RUN-p2" --line "$H:1" --line "$T:1" --expect new "${URLS[@]}")"
P1="$(order_of p1)"; P2="$(order_of p2)"
p2_held() { [[ "$(sql InventoryDb "SELECT COUNT(*) FROM inventory.Hold WHERE OrderId = '$P2' AND Sku = '$H' AND State = 'Held'")" == 1 ]]; }
wait_until 30 "P2 holds its unit of H" p2_held
cn="$(sim normal contention --run-id "$RUN-n" --seed 20 --sku "$H" --orders 10 --concurrency 10 "${URLS[@]}" --follow --follow-timeout 180)"
p2_at_end="$(sql InventoryDb "SELECT State FROM inventory.Hold WHERE OrderId = '$P2' AND Sku = '$H'")"
fired_then="$(sql InventoryDb "SELECT COUNT(*) FROM drill.Failpoint WHERE DrillRunId LIKE '$DRILL_RUN_ID-%' AND FiredAt IS NOT NULL")"
normal="$(sql OrderDb "SELECT CONCAT(SUM(CASE WHEN Status = 'Completed' THEN 1 ELSE 0 END), '/', COUNT(*)) FROM orders.[Order] WHERE CustomerId LIKE '$RUN-n-c%'")"
observed="contention exit $cn; ordinary orders Completed $normal; P2's hold on H when they ended: $p2_at_end (T failpoint fired $fired_then/5 by then)"
if [[ "$cn" == 0 && "$normal" == 10/10 && "$p2_at_end" == Held ]]; then
  assert S20-normal PASS "all ordinary orders completed while P2 still held its unit and retried" "$observed"
else
  assert S20-normal FAIL "10/10 Completed while P2 Held" "$observed"
fi

# S20-failures -----------------------------------------------------------------------------------
ended() { [[ "$(sql OrderDb "SELECT Status FROM orders.[Order] WHERE OrderId = '$P2'")" =~ ^(Completed|Compensated|Rejected|Failed)$ ]]; }
wait_until 180 "P2 ended" ended
RECOVERED_AT="$(date -u +%Y-%m-%dT%H:%M:%S.%3NZ)"
fired="$(sql InventoryDb "SELECT COUNT(*) FROM drill.Failpoint WHERE DrillRunId LIKE '$DRILL_RUN_ID-%' AND FiredAt IS NOT NULL")"
holds="$(sql InventoryDb "SELECT STRING_AGG(CONCAT(CASE OrderId WHEN '$P1' THEN 'P1' ELSE 'P2' END, ':', Sku, ':', State), ',') WITHIN GROUP (ORDER BY OrderId, Sku) FROM inventory.Hold WHERE OrderId IN ('$P1', '$P2')")"
FAULT_OBSERVED="BeforeStockReserve on W1|$T fired $fired/5"
observed="P1 (exit $c1) $(end_of "$P1"), P2 (exit $c2) $(end_of "$P2"); holds $holds; T failpoint fired $fired/5"
expected_holds="$(python -c "import sys; print(','.join(sorted(['P1:$H:Released', 'P2:$H:Released'])))")"
sorted_holds="$(python -c "import sys; print(','.join(sorted(sys.argv[1].split(','))))" "$holds")"
if [[ "$c1" == 0 && "$c2" == 0 && "$(end_of "$P1")" == Rejected/InsufficientStock && "$(end_of "$P2")" == Compensated/ReservationFailed && "$sorted_holds" == "$expected_holds" && "$fired" == 5 ]]; then
  assert S20-failures PASS "P1 Rejected and P2 Compensated, each H hold released once" "$observed"
else
  assert S20-failures FAIL "Rejected/InsufficientStock, Compensated/ReservationFailed, both H holds Released, 5 fired" "$observed"
fi

# S20-stock --------------------------------------------------------------------------------------
stock="$(sql InventoryDb "SELECT CONCAT(OnHand, '/', Reserved) FROM inventory.StockItem WHERE WarehouseId = 'W1' AND Sku = '$H'")"
states="$(sql InventoryDb "SELECT STRING_AGG(CONCAT(State, ':', n), ',') WITHIN GROUP (ORDER BY State) FROM (SELECT State, COUNT(*) AS n FROM inventory.Hold WHERE Sku = '$H' GROUP BY State) x")"
inv="$(inventory_invariants "SKU-S20_-$TAG")"
observed="H $stock; H holds $states; invariants (H, T, Z) $inv"
if [[ "$stock" == 6/0 && "$states" == "Consumed:10,Released:2" && "$inv" == "0|0|0|12|2" ]]; then
  assert S20-stock PASS "10 units consumed, the failed orders' 6 units back; invariants clean" "$observed"
else
  assert S20-stock FAIL "6/0, Consumed:10,Released:2, 0|0|0|12|2" "$observed"
fi
