#!/usr/bin/env bash
# S03 contention: 500 one-unit orders for 100 units of one SKU. Any stack with both order API
# replicas (bash scripts/stack-up.sh --replicas, release or drill). The SKU is new per run, stocked
# 60 in W1 and 40 in W2 (test-only setup); every order asks for 1 unit, sent by the client simulator
# with 32 requests in flight over both replicas and followed to its end.
# PASS when:
#   S03-orders     exactly 100 orders Completed and 400 Rejected with reason InsufficientStock
#                  (history Submitted, Validating, Reserving, Rejected), all accepted once
#   S03-stock      both stock rows end at OnHand 0 and Reserved 0: 60 units consumed from W1 and 40
#                  from W2 (whole line, warehouse priority), 100 holds Consumed, none left Held
#   S03-invariants per stock row Reserved = sum of Held holds; one creation and one closing event per
#                  hold; every order's outbox sequence gapless; one hold per successful order, none for
#                  a rejected one
#   S03-actors     each warehouse's stock actor ran on one inventory replica at a time (its holds and
#                  stored results written by that replica, its activation logged there), and the
#                  inventory work spread over more than one replica
# Evidence in artifacts/scenarios/S03-<time>/.
. "$(dirname "$0")/../lib.sh"
. "$(dirname "$0")/replica-lib.sh"

RUN_ID="S03-$(date -u +%Y%m%dT%H%M%SZ)"
OUT_DIR="$REPO_ROOT/artifacts/scenarios/$RUN_ID"
mkdir -p "$OUT_DIR"
RESULTS_FILE="$OUT_DIR/results.jsonl"
N="${S03_ORDERS:-500}"
W1_STOCK=60
W2_STOCK=40
STOCK=$((W1_STOCK + W2_STOCK))
SKU="SKU-S03-$(printf '%04X' $((RANDOM % 65536)))"
CLIENT_RUN="s03-$(date -u +%Y%m%d-%H%M%S)-$((RANDOM % 1000))"

require_replicas
client_sim identity > "$OUT_DIR/identity.json" || { log "REFUSED: the client simulator does not build"; exit 2; }
CLIENT_SIM_BUILT=1
SINCE="$(date -u -d '-2 seconds' +%Y-%m-%dT%H:%M:%SZ)"
set_stock W1 "$SKU" "$W1_STOCK"
set_stock W2 "$SKU" "$W2_STOCK"
log "S03 $RUN_ID: $N orders x 1 unit of $SKU (W1 $W1_STOCK, W2 $W2_STOCK); evidence in $OUT_DIR"

start=$SECONDS
c="$(sim contention contention --run-id "$CLIENT_RUN" --seed 3 --sku "$SKU" --orders "$N" --concurrency 32 --follow --follow-timeout 900 \
  --url "$ORDER_API" --url "$ORDER_API_2")"
elapsed=$((SECONDS - start))

# S03-orders ---------------------------------------------------------------------------------------
run_orders="SELECT OrderId FROM orders.[Order] WHERE CustomerId LIKE '$CLIENT_RUN-c%'"
row="$(sql OrderDb "SELECT CONCAT(
  (SELECT COUNT(*) FROM orders.[Order] WHERE OrderId IN ($run_orders) AND Status = 'Completed'), '|',
  (SELECT COUNT(*) FROM orders.[Order] o WHERE o.OrderId IN ($run_orders) AND o.Status = 'Rejected'
     AND (SELECT TOP (1) h.ReasonCode FROM orders.OrderStatusHistory h WHERE h.OrderId = o.OrderId ORDER BY h.Sequence DESC) = 'InsufficientStock'
     AND (SELECT STRING_AGG(h.ToStatus, ',') WITHIN GROUP (ORDER BY h.Sequence) FROM orders.OrderStatusHistory h WHERE h.OrderId = o.OrderId) = 'Submitted,Validating,Reserving,Rejected'), '|',
  (SELECT COUNT(*) FROM orders.[Order] WHERE OrderId IN ($run_orders)), '|',
  (SELECT COUNT(*) FROM orders.IdempotencyKey WHERE [Key] LIKE '$CLIENT_RUN-o%'))")"
IFS='|' read -r completed rejected orders keys <<< "$row"
observed="client exit $c in ${elapsed}s, final statuses $(jq_py contention "r['summary']['finalStatuses']"), accepted $(jq_py contention "r['summary']['accepted']"); SQL: Completed $completed, Rejected (InsufficientStock) $rejected, orders $orders, keys $keys"
if [[ "$c" == 0 && "$completed" == "$STOCK" && "$rejected" == "$((N - STOCK))" && "$orders" == "$N" && "$keys" == "$N" ]]; then
  check S03-orders PASS "$STOCK Completed, $((N - STOCK)) Rejected (InsufficientStock), every order accepted once" "$observed"
else
  check S03-orders FAIL "$STOCK Completed and $((N - STOCK)) Rejected" "$observed"
fi

# S03-stock ----------------------------------------------------------------------------------------
stock="$(sql InventoryDb "SELECT STRING_AGG(CONCAT(WarehouseId, ':', OnHand, '/', Reserved), ',') WITHIN GROUP (ORDER BY WarehouseId) FROM inventory.StockItem WHERE Sku = '$SKU'")"
holds="$(sql InventoryDb "SELECT STRING_AGG(CONCAT(WarehouseId, ':', State, ':', n), ',') WITHIN GROUP (ORDER BY WarehouseId, State) FROM (SELECT WarehouseId, State, COUNT(*) AS n FROM inventory.Hold WHERE Sku = '$SKU' GROUP BY WarehouseId, State) x")"
observed="stock (warehouse:onHand/reserved) $stock; holds $holds"
if [[ "$stock" == "W1:0/0,W2:0/0" && "$holds" == "W1:Consumed:$W1_STOCK,W2:Consumed:$W2_STOCK" ]]; then
  check S03-stock PASS "W1 and W2 consumed to 0, nothing reserved; $STOCK holds Consumed, whole line by warehouse priority" "$observed"
else
  check S03-stock FAIL "W1:0/0,W2:0/0; W1:Consumed:$W1_STOCK,W2:Consumed:$W2_STOCK" "$observed"
fi

# S03-invariants -----------------------------------------------------------------------------------
inv="$(inventory_invariants "$SKU")"
per_order="$(sql OrderDb "SELECT LOWER(CONVERT(varchar(36), OrderId)) + '|' + Status FROM orders.[Order] WHERE OrderId IN ($run_orders)")"
echo "$per_order" > "$OUT_DIR/order-status.txt"
holds_per_order="$(sql InventoryDb "SELECT LOWER(CONVERT(varchar(36), OrderId)) + '|' + CAST(COUNT(*) AS varchar(8)) FROM inventory.Hold WHERE Sku = '$SKU' GROUP BY OrderId")"
echo "$holds_per_order" > "$OUT_DIR/holds-per-order.txt"
mismatch="$(python - "$OUT_DIR/order-status.txt" "$OUT_DIR/holds-per-order.txt" <<'PY'
import sys
status = dict(l.strip().split('|') for l in open(sys.argv[1]) if '|' in l)
holds = {k: int(v) for k, v in (l.strip().split('|') for l in open(sys.argv[2]) if '|' in l)}
bad = [o for o, s in status.items() if holds.get(o, 0) != (1 if s == 'Completed' else 0)]
print(len(bad))
PY
)"
observed="invariants (reserved-mismatch|hold-event-violations|sequence-gaps|holds|rows) $inv; orders whose hold count differs from 1 (Completed) or 0 (Rejected): $mismatch"
if [[ "$inv" == "0|0|0|$STOCK|2" && "$mismatch" == 0 ]]; then
  check S03-invariants PASS "Reserved = held units per row; one creation and one closing event per hold; gapless outbox; one hold per completed order, none per rejected" "$observed"
else
  check S03-invariants FAIL "0|0|0|$STOCK|2 and 0" "$observed"
fi

MODE="$(docker compose --profile replicas exec -T inventory-service printenv Inventory__Coordination 2>/dev/null | tr -d '')"
log "inventory coordination: ${MODE:-Actor}"
# S03-actors ---------------------------------------------------------------------------------------
sku_writers="$(sql InventoryDb "SELECT STRING_AGG(CONCAT(WarehouseId, '=', ProcessedBy, ':', n), ',') WITHIN GROUP (ORDER BY WarehouseId, ProcessedBy) FROM (SELECT p.WarehouseId, p.ProcessedBy, COUNT(*) AS n FROM inventory.ProcessedOperation p JOIN inventory.Hold h ON h.OrderId = p.OrderId AND h.Sku = '$SKU' WHERE p.Operation = 'Reserve' GROUP BY p.WarehouseId, p.ProcessedBy) x")"
activations="$(docker compose --profile replicas logs --no-log-prefix --since "$SINCE" inventory-service inventory-service-2 inventory-service-3 2>/dev/null | grep -o "stock actor W[12]|$SKU activated on [a-z0-9-]*" | sort | uniq -c | tr -s ' ' | paste -sd';' -)"
spread="$(sql InventoryDb "SELECT COUNT(DISTINCT ProcessedBy) FROM inventory.ProcessedOperation p WHERE p.OrderId IN (SELECT OrderId FROM inventory.Hold WHERE Sku = '$SKU')")"
one_per_warehouse="$(python -c "
import sys
pairs = [p for p in sys.argv[1].split(',') if p]
by = {}
for p in pairs:
    w, rest = p.split('=', 1); by.setdefault(w, set()).add(rest.split(':')[0])
print(all(len(v) == 1 for v in by.values()) and len(by) == 2)" "$sku_writers")"
observed="reserve results of the successful orders written by (warehouse=replica:count) $sku_writers; activations: ${activations:-none}; replicas writing inventory results for these orders: $spread"
if [[ "$MODE" == SqlOnly ]]; then
  many_per_warehouse="$(python -c "
import sys
by = {}
for p in [p for p in sys.argv[1].split(',') if p]:
    w, rest = p.split('=', 1); by.setdefault(w, set()).add(rest.split(':')[0])
print(all(len(v) > 1 for v in by.values()) and len(by) == 2)" "$sku_writers")"
  observed="coordination SqlOnly; $observed"
  if [[ "$many_per_warehouse" == True && -z "$activations" ]]; then
    check S03-sqlonly PASS "no stock actor; each warehouse's reservations ran concurrently on several replicas, guarded by SQL alone" "$observed"
  else
    check S03-sqlonly FAIL "no activation, several writing replicas per warehouse" "$observed"
  fi
elif [[ "$one_per_warehouse" == True && -n "$activations" ]]; then
  check S03-actors PASS "each warehouse's reservations ran in one stock actor on one replica; activations logged" "$observed"
else
  check S03-actors FAIL "one writing replica per warehouse actor" "$observed"
fi

finish S03
