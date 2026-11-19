#!/usr/bin/env bash
# R05 (S07, S03) contention and actor relocation keep stock correct. Drill stack with replicas:
#   bash scripts/stack-up.sh --drill --replicas
# 200 one-unit orders for one SKU stocked 150 in W1 only (one stock actor), sent by the client
# simulator over both order API replicas. When the actor's host (from its own activation log) has
# served part of the reservations, that inventory replica and its sidecar are killed (SIGKILL).
#   R05-host       the actor's host identified before the kill (activation log), reservations already
#                  committed by it
#   R05-killed     that replica's app exited 137, its sidecar too
#   R05-relocated  the actor activated again on another replica after the kill, and reservations
#                  committed there (stored results written by the new host)
#   R05-stock      successes = stock: 150 Completed, 50 Rejected (InsufficientStock); OnHand 0,
#                  Reserved 0; 150 holds Consumed; invariants (I1, I2, I16) clean
DRILL_ID=R05
. "$(dirname "$0")/lib.sh"
OUT_DIR="$DRILL_DIR"
. "$REPO_ROOT/scripts/scenarios/replica-lib.sh"

drill_preflight
require_replicas
client_sim identity > "$DRILL_DIR/client-identity.json" || { log "REFUSED: the client simulator does not build"; exit 2; }
CLIENT_SIM_BUILT=1
trap 'drill_finish; exit $?' EXIT
INV=(inventory-service inventory-service-2 inventory-service-3)
state_of() { docker compose --profile replicas ps -a --format '{{.State}}' "$1" 2>/dev/null; }
restart_dead() { local s; for s in "${INV[@]}"; do [[ "$(state_of "$s")" == running ]] || start_pair "$s"; done; }
on_cleanup "restart_dead"

N=200; STOCK=150
SKU="SKU-R05-$(printf '%04X' $((RANDOM % 65536)))"
set_stock W1 "$SKU" "$STOCK"
RUN="r05-$(date -u +%H%M%S)-$((RANDOM % 1000))"
SINCE="$(date -u -d '-2 seconds' +%Y-%m-%dT%H:%M:%SZ)"
client_sim contention --run-id "$RUN" --seed 5 --sku "$SKU" --orders "$N" --concurrency 16 --rate 15 --follow --follow-timeout 900 \
  --url "$ORDER_API" --url "$ORDER_API_2" > "$DRILL_DIR/contention.json" 2> "$DRILL_DIR/contention.log" &
client_pid=$!

# R05-host --------------------------------------------------------------------------------------
host=""
for _ in $(seq 1 120); do
  host="$(docker compose --profile replicas logs --no-log-prefix --since "$SINCE" "${INV[@]}" 2>/dev/null | grep -o "stock actor W1|$SKU activated on [a-z0-9-]*" | head -1 | awk '{print $NF}')"
  done_before="$(sql InventoryDb "SELECT COUNT(*) FROM inventory.Hold WHERE Sku = '$SKU'")"
  [[ -n "$host" && "$done_before" -ge 40 ]] && break
  sleep 0.5
done
reserves_of() { # reserves_of <extra condition>: reserve results of this SKU's successful orders, as replica:count,...
  sql InventoryDb "SELECT ISNULL(STRING_AGG(CONCAT(ProcessedBy, ':', n), ','), '') FROM (SELECT p.ProcessedBy, COUNT(*) AS n FROM inventory.ProcessedOperation p
    JOIN inventory.Hold h ON h.OrderId = p.OrderId AND h.Sku = '$SKU' WHERE p.Operation = 'Reserve' ${1:-} GROUP BY p.ProcessedBy) x"
}
by_host="$(reserves_of)"
if [[ -n "$host" && "$by_host" == *"$host:"* ]]; then
  assert R05-host PASS "the stock actor's host found before the kill; it had committed reservations" "actor W1|$SKU on $host; holds so far $done_before, reserve results so far (replica:count) $by_host"
else
  assert R05-host FAIL "host and committed reservations" "host '${host:-none}', holds $done_before"
fi

# R05-killed ------------------------------------------------------------------------------------
INJECTED_AT="$(date -u +%Y-%m-%dT%H:%M:%S.%3NZ)"
# The kill instant by SQL's clock (ProcessedAt is SQL time; the host clock differs).
KILL_SQL="$(sql InventoryDb "SELECT CONVERT(varchar(27), SYSUTCDATETIME(), 126)")"
kill_pair "${host:-inventory-service}"
code="$(docker inspect -f '{{.State.ExitCode}}' "$(docker compose --profile replicas ps -a -q "${host:-inventory-service}")" 2>/dev/null)"
sidecar="$(state_of "${host:-inventory-service}-dapr")"
finished_at="$(docker inspect -f '{{.State.FinishedAt}}' "$(docker compose --profile replicas ps -a -q "${host:-inventory-service}")" 2>/dev/null)"
holds_at_kill="$(sql InventoryDb "SELECT COUNT(*) FROM inventory.Hold WHERE Sku = '$SKU'")"
FAULT_OBSERVED="$host exit $code, sidecar $sidecar, holds at the kill $holds_at_kill"
if [[ "$code" == 137 && "$sidecar" == exited ]]; then
  assert R05-killed PASS "the actor's host and its sidecar were killed mid-run" "$host exit $code; sidecar $sidecar; $holds_at_kill of $N reservations had holds"
else
  assert R05-killed FAIL "exit 137" "$host exit '$code'; sidecar '$sidecar'"
fi

wait $client_pid; c=$?
RECOVERED_AT="$(date -u +%Y-%m-%dT%H:%M:%S.%3NZ)"

# R05-relocated ---------------------------------------------------------------------------------
new_host="$(docker compose --profile replicas logs --no-log-prefix --since "$INJECTED_AT" "${INV[@]}" 2>/dev/null | grep -o "stock actor W1|$SKU activated on [a-z0-9-]*" | head -1 | awk '{print $NF}')"
# Commits by the old host stamped after KILL_SQL are the ones in flight while docker delivered SIGKILL.
writers="$(reserves_of "AND p.ProcessedAt > '$KILL_SQL'")"
last_old="$(sql InventoryDb "SELECT CONVERT(varchar(27), MAX(p.ProcessedAt), 126) FROM inventory.ProcessedOperation p JOIN inventory.Hold h ON h.OrderId = p.OrderId AND h.Sku = '$SKU' WHERE p.ProcessedBy = '$host'")"
replays="$(sql InventoryDb "SELECT ISNULL(SUM(p.Replays), 0) FROM inventory.ProcessedOperation p JOIN inventory.Hold h ON h.OrderId = p.OrderId AND h.Sku = '$SKU' WHERE p.Operation = 'Reserve'")"
observed="after the kill the actor activated on ${new_host:-none}; reserve results after the kill (replica:count) $writers; replays of stored reservations $replays; kill requested at SQL time $KILL_SQL, the old host's last write at SQL time $last_old, its container finished at $finished_at (Docker clock)"
if [[ -n "$new_host" && "$new_host" != "$host" && "$writers" == *"$new_host:"* ]]; then
  assert R05-relocated PASS "the actor moved to $new_host and went on reserving there" "$observed"
else
  assert R05-relocated FAIL "activation and reservations on another replica" "$observed"
fi

# R05-stock -------------------------------------------------------------------------------------
run_orders="SELECT OrderId FROM orders.[Order] WHERE CustomerId LIKE '$RUN-c%'"
row="$(sql OrderDb "SELECT CONCAT((SELECT COUNT(*) FROM orders.[Order] WHERE OrderId IN ($run_orders) AND Status = 'Completed'), '|',
  (SELECT COUNT(*) FROM orders.[Order] o WHERE o.OrderId IN ($run_orders) AND o.Status = 'Rejected'
     AND (SELECT TOP (1) h.ReasonCode FROM orders.OrderStatusHistory h WHERE h.OrderId = o.OrderId ORDER BY h.Sequence DESC) = 'InsufficientStock'), '|',
  (SELECT COUNT(*) FROM orders.[Order] WHERE OrderId IN ($run_orders)))")"
IFS='|' read -r completed rejected orders <<< "$row"
stock="$(sql InventoryDb "SELECT CONCAT(OnHand, '/', Reserved) FROM inventory.StockItem WHERE WarehouseId = 'W1' AND Sku = '$SKU'")"
consumed="$(sql InventoryDb "SELECT COUNT(*) FROM inventory.Hold WHERE Sku = '$SKU' AND State = 'Consumed'")"
inv="$(inventory_invariants "$SKU")"
observed="client exit $c ($(jq_py contention "r['summary']['finalStatuses']")); Completed $completed, Rejected $rejected of $orders; stock $stock; consumed holds $consumed; invariants $inv"
if [[ "$c" == 0 && "$completed" == "$STOCK" && "$rejected" == "$((N - STOCK))" && "$stock" == "0/0" && "$consumed" == "$STOCK" && "$inv" == "0|0|0|$STOCK|1" ]]; then
  assert R05-stock PASS "successes equal the stock; no unit lost or doubled across the relocation" "$observed"
else
  assert R05-stock FAIL "$STOCK Completed, $((N - STOCK)) Rejected, 0/0" "$observed"
fi
