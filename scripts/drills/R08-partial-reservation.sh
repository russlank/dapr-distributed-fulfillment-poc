#!/usr/bin/env bash
# R08 partial reservations are compensated, and holds expire without the Dapr Scheduler. Drill stack
# with replicas: bash scripts/stack-up.sh --drill --replicas
# Variant A (S45): an order of two lines whose first SKU is in stock and whose second has none: the
#   first line's hold is released once, no hold exists for the second, the order Rejected
#   (InsufficientStock).
# Variant B: an order of two lines whose second line's stock actor fails every attempt (failpoint
#   BeforeStockReserve Throw x5 on that actor's id): the order Compensated (ReservationFailed), the
#   first line's hold released once.
# Variant C (S15): an orphan hold (scripts/drills/sql/insert-orphan-hold.sql) with the Scheduler and
#   Placement stopped: the inventory's own sweeper expires it from SQL (ClosedBy sweeper:<replica>),
#   Reserved returns to 0, one HoldExpired event.
#   R08-A, R08-B, R08-C   per variant; each also checks the invariants of its SKUs
DRILL_ID=R08
. "$(dirname "$0")/lib.sh"
OUT_DIR="$DRILL_DIR"
. "$REPO_ROOT/scripts/scenarios/replica-lib.sh"

drill_preflight
require_replicas
client_sim identity > "$DRILL_DIR/client-identity.json" || { log "REFUSED: the client simulator does not build"; exit 2; }
CLIENT_SIM_BUILT=1
trap 'drill_finish; exit $?' EXIT
URLS=(--url "$ORDER_API" --url "$ORDER_API_2")
TAG="$(printf '%04X' $((RANDOM % 65536)))"
RUN="r08-$(date -u +%H%M%S)-$((RANDOM % 1000))"
order_of() { jq_py "$1" "r['items'][0].get('orderId')"; }
end_of() { sql OrderDb "SELECT CONCAT(o.Status, '/', (SELECT TOP (1) h.ReasonCode FROM orders.OrderStatusHistory h WHERE h.OrderId = o.OrderId ORDER BY h.Sequence DESC)) FROM orders.[Order] o WHERE o.OrderId = '$1'"; }
holds_of() { sql InventoryDb "SELECT ISNULL(STRING_AGG(CONCAT(Sku, ':', State), ',') WITHIN GROUP (ORDER BY Sku), 'none') FROM inventory.Hold WHERE OrderId = '$1'"; }
events_of() { sql InventoryDb "SELECT ISNULL(STRING_AGG(EventType, ',') WITHIN GROUP (ORDER BY Sequence), 'none') FROM inventory.Outbox WHERE OrderId = '$1'"; }
stock_of() { sql InventoryDb "SELECT CONCAT(OnHand, '/', Reserved) FROM inventory.StockItem WHERE WarehouseId = 'W1' AND Sku = '$1'"; }

# Variant A (S45) ------------------------------------------------------------------------------
A_IN="SKU-R08A-$TAG"; A_OUT="SKU-R08B-$TAG"
set_stock W1 "$A_IN" 5; set_stock W1 "$A_OUT" 0
INJECTED_AT="$(date -u +%Y-%m-%dT%H:%M:%S.%3NZ)"
ca="$(sim a single --run-id "$RUN-a" --line "$A_IN:2" --line "$A_OUT:1" --expect new "${URLS[@]}" --follow --follow-timeout 180)"
A="$(order_of a)"
observed="client exit $ca; order $A $(end_of "$A"); holds $(holds_of "$A"); events $(events_of "$A"); stock $A_IN $(stock_of "$A_IN"), $A_OUT $(stock_of "$A_OUT"); invariants $(inventory_invariants "SKU-R08[AB]-$TAG")"
if [[ "$ca" == 0 && "$(end_of "$A")" == Rejected/InsufficientStock && "$(holds_of "$A")" == "$A_IN:Released" && "$(events_of "$A")" == HoldCreated,HoldReleased \
      && "$(stock_of "$A_IN")" == 5/0 && "$(inventory_invariants "SKU-R08[AB]-$TAG")" == "0|0|0|1|2" ]]; then
  assert R08-A PASS "the reserved line released once, no hold for the missing line; Rejected (InsufficientStock)" "$observed"
else
  assert R08-A FAIL "Rejected/InsufficientStock; $A_IN:Released; HoldCreated,HoldReleased; 5/0" "$observed"
fi

# Variant B --------------------------------------------------------------------------------------
B_OK="SKU-R08C-$TAG"; B_BAD="SKU-R08D-$TAG"
set_stock W1 "$B_OK" 5; set_stock W1 "$B_BAD" 5
arm_failpoint_times InventoryDb BeforeStockReserve "W1|$B_BAD" Throw 5
cb="$(sim b single --run-id "$RUN-b" --line "$B_OK:1" --line "$B_BAD:1" --expect new "${URLS[@]}" --follow --follow-timeout 240)"
B="$(order_of b)"
fired="$(sql InventoryDb "SELECT COUNT(*) FROM drill.Failpoint WHERE DrillRunId LIKE '$DRILL_RUN_ID-%' AND Name = 'BeforeStockReserve' AND FiredAt IS NOT NULL")"
FAULT_OBSERVED="variant B: BeforeStockReserve fired $fired/5"
observed="client exit $cb; failpoint fired $fired/5; order $B $(end_of "$B"); holds $(holds_of "$B"); events $(events_of "$B"); stock $B_OK $(stock_of "$B_OK"), $B_BAD $(stock_of "$B_BAD"); invariants $(inventory_invariants "SKU-R08[CD]-$TAG")"
if [[ "$cb" == 0 && "$fired" == 5 && "$(end_of "$B")" == Compensated/ReservationFailed && "$(holds_of "$B")" == "$B_OK:Released" && "$(events_of "$B")" == HoldCreated,HoldReleased \
      && "$(stock_of "$B_OK")" == 5/0 && "$(stock_of "$B_BAD")" == 5/0 && "$(inventory_invariants "SKU-R08[CD]-$TAG")" == "0|0|0|1|2" ]]; then
  assert R08-B PASS "every attempt for the second line failed; the first line's hold released once; Compensated (ReservationFailed)" "$observed"
else
  assert R08-B FAIL "5 fired; Compensated/ReservationFailed; $B_OK:Released; 5/0 and 5/0" "$observed"
fi

# Variant C (S15) --------------------------------------------------------------------------------
C_SKU="SKU-R08E-$TAG"
set_stock W1 "$C_SKU" 5
CONTROL=(placement scheduler)
restore_control() { docker compose up -d --no-build --no-deps "${CONTROL[@]}" >/dev/null 2>&1; }
on_cleanup "restore_control"
docker compose stop -t 5 "${CONTROL[@]}" >/dev/null 2>&1
control_state="$(for s in "${CONTROL[@]}"; do printf '%s=%s ' "$s" "$(docker compose ps -a --format '{{.State}}' "$s")"; done)"
C="$(python -c 'import uuid; print(uuid.uuid4())')"
hold="$(drill_sql_script InventoryDb "$REPO_ROOT/scripts/drills/sql/insert-orphan-hold.sql" "OrderId=$C" "WarehouseId=W1" "Sku=$C_SKU" "Quantity=3" "ExpiresInSeconds=5" | tail -1)"
before="$(stock_of "$C_SKU")"
expired() { [[ "$(sql InventoryDb "SELECT State FROM inventory.Hold WHERE HoldId = '$hold'")" == Expired ]]; }
wait_until 60 "the orphan hold expired" expired
row="$(sql InventoryDb "SELECT CONCAT(State, '|', ClosedBy, '|', DATEDIFF(millisecond, ExpiresAt, ClosedAt)) FROM inventory.Hold WHERE HoldId = '$hold'")"
IFS='|' read -r st by late_ms <<< "$row"
control_after="$(for s in "${CONTROL[@]}"; do printf '%s=%s ' "$s" "$(docker compose ps -a --format '{{.State}}' "$s")"; done)"
observed="control plane at the start: $control_state, at the expiry: $control_after; orphan hold $hold: stock before $before, $st by $by ${late_ms} ms after its expiry; events $(events_of "$C"); stock $(stock_of "$C_SKU"); invariants $(inventory_invariants "$C_SKU")"
FAULT_OBSERVED+="; variant C: $control_after"
if [[ "$control_after" != *running* && "$before" == 5/3 && "$st" == Expired && "$by" == sweeper:* && "$(events_of "$C")" == HoldCreated,HoldExpired \
      && "$(stock_of "$C_SKU")" == 5/0 && "$(inventory_invariants "$C_SKU")" == "0|0|0|1|1" ]]; then
  assert R08-C PASS "the sweeper expired the orphan hold from SQL with Scheduler and Placement stopped; units free again" "$observed"
else
  assert R08-C FAIL "Expired by sweeper:*, HoldCreated,HoldExpired, 5/0, control plane stopped" "$observed"
fi
restore_control
RECOVERED_AT="$(date -u +%Y-%m-%dT%H:%M:%S.%3NZ)"
