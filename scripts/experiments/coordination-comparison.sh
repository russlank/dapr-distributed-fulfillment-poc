#!/usr/bin/env bash
# Measurement for docs/dapr-vs-alternatives.md: inventory coordination with stock actors versus SQL only.
# Drill or release stack with replicas (bash scripts/stack-up.sh --drill --replicas). For each mode the inventory
# replicas are restarted with INVENTORY_COORDINATION, then the contention scenario S03 runs (500 one-unit orders for
# 100 units of a new SKU, 32 requests in flight over both order API replicas). Afterwards the inventory returns to
# actors. Figures come from the databases:
#   wall time (first acceptance to last terminal status), orders per minute,
#   reservation latency per order (entering Reserving to the next status), end-to-end latency of completed orders,
#   hold duration (creation to consumption) of the run's holds,
# and, across every consumed hold on the stack, the hold-duration distribution that sizes the hold TTL. The SQL
# Server container's memory is sampled every 5 s per mode (peak), with the lock waits and user sessions SQL Server
# reports (lock wait time accumulated during the run, peak user sessions).
# Output: artifacts/experiments/coordination-<time>/coordination.json and a summary on stdout. Exit 1 if a run fails.
set -uo pipefail
. "$(dirname "$0")/../lib.sh"
export COMPOSE_PROFILES=replicas
OUT="$REPO_ROOT/artifacts/experiments/coordination-$(date -u +%Y%m%dT%H%M%SZ)"
mkdir -p "$OUT"
INV=(inventory-service inventory-service-2 inventory-service-3)
switch() { local s; for s in "${INV[@]}"; do INVENTORY_COORDINATION=$1 restart_pair "$s" || return 1; done; }
trap 'switch Actor >/dev/null 2>&1' EXIT
pct() { # pct <sql select of one float column named v> -> "p50|p95|max|count" in ms
  sql OrderDb "SELECT CONCAT(CAST(MAX(p50) AS int), '|', CAST(MAX(p95) AS int), '|', MAX(mx), '|', MAX(n)) FROM (
    SELECT PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY v) OVER () p50, PERCENTILE_CONT(0.95) WITHIN GROUP (ORDER BY v) OVER () p95,
           MAX(v) OVER () mx, COUNT(*) OVER () n FROM ($1) x) y"
}
pct_inv() {
  sql InventoryDb "SELECT CONCAT(CAST(MAX(p50) AS int), '|', CAST(MAX(p95) AS int), '|', MAX(mx), '|', MAX(n)) FROM (
    SELECT PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY v) OVER () p50, PERCENTILE_CONT(0.95) WITHIN GROUP (ORDER BY v) OVER () p95,
           MAX(v) OVER () mx, COUNT(*) OVER () n FROM ($1) x) y"
}
rc=0; json="{"
for mode in Actor SqlOnly; do
  switch "$mode" || { log "could not switch the inventory to $mode"; exit 1; }
  log "inventory coordination $mode"
  lockwait() { sql master "SELECT CAST(SUM(wait_time_ms) AS bigint) FROM sys.dm_os_wait_stats WHERE wait_type LIKE 'LCK[_]%'"; }
  w0="$(lockwait)"
  ( peak=0; sess=0; while :; do
      m="$(docker stats --no-stream --format '{{.MemUsage}}' "$(docker compose ps -q sqlserver)" 2>/dev/null | awk '{print $1}')"
      case "$m" in *GiB) v=$(python -c "print(int(float('${m%GiB}') * 1024))");; *MiB) v=${m%MiB}; v=${v%.*};; *) v=0;; esac
      n="$(sql master "SELECT COUNT(*) FROM sys.dm_exec_sessions WHERE is_user_process = 1" 2>/dev/null)"
      (( v > peak )) && peak=$v; [[ "$n" =~ ^[0-9]+$ ]] && (( n > sess )) && sess=$n
      echo "$peak|$sess" > "$OUT/sql-$mode.peak"; sleep 5
    done ) & sampler=$!
  bash "$REPO_ROOT/scripts/scenarios/S03-contention.sh" > "$OUT/S03-$mode.log" 2>&1; s03=$?
  kill "$sampler" 2>/dev/null; wait "$sampler" 2>/dev/null
  w1="$(lockwait)"; IFS='|' read -r sql_peak sql_sessions < "$OUT/sql-$mode.peak"
  (( s03 == 0 )) || rc=1
  sku="$(grep -o 'x 1 unit of SKU-S03-[0-9A-F]*' "$OUT/S03-$mode.log" | head -1 | awk '{print $NF}')"
  orders="SELECT o.OrderId FROM orders.[Order] o WHERE EXISTS (SELECT 1 FROM orders.OrderLine l WHERE l.OrderId = o.OrderId AND l.Sku = '$sku')"
  counts="$(sql OrderDb "SELECT CONCAT(COUNT(*), '|', COUNT(CASE WHEN Status = 'Completed' THEN 1 END), '|', COUNT(CASE WHEN Status = 'Rejected' THEN 1 END)) FROM orders.[Order] WHERE OrderId IN ($orders)")"
  wall="$(sql OrderDb "SELECT DATEDIFF(millisecond, MIN(h.OccurredAt), MAX(h.OccurredAt)) FROM orders.OrderStatusHistory h WHERE h.OrderId IN ($orders)")"
  reserve="$(pct "SELECT CAST(DATEDIFF(millisecond, r.OccurredAt, n.OccurredAt) AS float) v FROM orders.OrderStatusHistory r
    JOIN orders.OrderStatusHistory n ON n.OrderId = r.OrderId AND n.Sequence = r.Sequence + 1 WHERE r.ToStatus = 'Reserving' AND r.OrderId IN ($orders)")"
  e2e="$(pct "SELECT CAST(DATEDIFF(millisecond, MIN(h.OccurredAt), MAX(h.OccurredAt)) AS float) v FROM orders.OrderStatusHistory h
    WHERE h.OrderId IN ($orders) AND h.OrderId IN (SELECT OrderId FROM orders.[Order] WHERE Status = 'Completed') GROUP BY h.OrderId")"
  holds="$(pct_inv "SELECT CAST(DATEDIFF(millisecond, CreatedAt, ClosedAt) AS float) v FROM inventory.Hold WHERE Sku = '$sku' AND State = 'Consumed'")"
  locks=$(( ${w1:-0} - ${w0:-0} ))
  log "$mode: S03 exit $s03; orders|completed|rejected $counts; wall ${wall} ms; reserve p50|p95|max|n $reserve; end-to-end $e2e; holds $holds; SQL peak ${sql_peak} MiB, peak user sessions $sql_sessions, lock wait ${locks} ms"
  json+="\"$mode\":{\"s03Exit\":$s03,\"sqlPeakMiB\":${sql_peak:-0},\"sqlPeakUserSessions\":${sql_sessions:-0},\"lockWaitMs\":$locks,\"sku\":\"$sku\",\"ordersCompletedRejected\":\"$counts\",\"wallMs\":${wall:-0},\"reserveMs\":\"$reserve\",\"endToEndMs\":\"$e2e\",\"holdMs\":\"$holds\"},"
done
all_holds="$(pct_inv "SELECT CAST(DATEDIFF(millisecond, CreatedAt, ClosedAt) AS float) v FROM inventory.Hold WHERE State = 'Consumed' AND CreatedBy <> 'drill-orphan'")"
log "every consumed hold on the stack (p50|p95|max|n, ms): $all_holds"
json+="\"allConsumedHoldsMs\":\"$all_holds\",\"columns\":\"p50|p95|max|count\"}"
python -c "import json,sys; print(json.dumps(json.loads(sys.argv[1]), indent=1))" "$json" > "$OUT/coordination.json"
log "written $OUT/coordination.json"
exit $rc
