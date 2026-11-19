#!/usr/bin/env bash
# Shared parts of the replica scenarios S04, S24, S43 (source after lib.sh; do not run).
# Each scenario writes the simulator's results, the replicas' own log outcomes, and SQL rows to
# its evidence directory, and one JSON line per check (PASS, FAIL, or UNVERIFIED) to results.jsonl.

failures=0
unverified=0
check() {
  case "$2" in
    PASS) ;;
    UNVERIFIED) unverified=$((unverified + 1)) ;;
    *) failures=$((failures + 1)) ;;
  esac
  record "$@"
}

# require_replicas: both order API replicas run and are ready, or the scenario is refused (exit 2).
require_replicas() {
  [[ "$(docker compose --profile replicas ps --format '{{.State}}' order-api-2 2>/dev/null)" == running ]] && ready order-api && ready order-api-2 \
    || { log "REFUSED: both order API replicas must run and be ready (bash scripts/stack-up.sh --replicas)"; exit 2; }
}

# sim <name> <args...>: the client simulator; stdout to <name>.json, stderr to <name>.log; prints the exit code.
sim() {
  local name=$1; shift
  client_sim "$@" > "$OUT_DIR/$name.json" 2> "$OUT_DIR/$name.log"
  echo $?
}

# jq_py <name> '<python expression over r>': evaluates an expression over a result file (r = parsed JSON).
jq_py() { python -c "
import json, sys
r = json.load(open(sys.argv[1]))
v = eval(sys.argv[2])
print(json.dumps(v, sort_keys=True) if isinstance(v, (dict, list)) else v)" "$OUT_DIR/$1.json" "$2"; }

# order_ids <name> '<python expression yielding order ids>' > file
order_ids_of() { jq_py "$1" "$2" | python -c "import json,sys; print('\n'.join(json.load(sys.stdin)))" | tr -d '\r'; }

# sql_orders_report <customer prefix>: per order of the run, the authoritative rows as JSON lines:
# status, key rows, start requests (state, claims, fence rejections), history, lines, effects, replays.
sql_orders_report() {
  # Separate short columns (sqlcmd truncates long ones), turned into one JSON object per order.
  sql OrderDb "SELECT LOWER(CONVERT(varchar(36), o.OrderId)), o.CustomerId, o.Status,
    (SELECT COUNT(*) FROM orders.IdempotencyKey k WHERE k.OrderId = o.OrderId),
    (SELECT COUNT(*) FROM orders.WorkflowStartRequest r WHERE r.OrderId = o.OrderId),
    (SELECT MAX(r.State) FROM orders.WorkflowStartRequest r WHERE r.OrderId = o.OrderId),
    (SELECT ISNULL(MAX(r.ExecutionClaims), 0) FROM orders.WorkflowStartRequest r WHERE r.OrderId = o.OrderId),
    (SELECT ISNULL(MAX(r.FenceRejections), 0) FROM orders.WorkflowStartRequest r WHERE r.OrderId = o.OrderId),
    (SELECT STRING_AGG(a.Result, ',') WITHIN GROUP (ORDER BY a.Id) FROM orders.WorkflowStartAttempt a WHERE a.OrderId = o.OrderId),
    (SELECT STRING_AGG(h.ToStatus, ',') WITHIN GROUP (ORDER BY h.Sequence) FROM orders.OrderStatusHistory h WHERE h.OrderId = o.OrderId),
    (SELECT STRING_AGG(CONCAT(l.Sku, ':', l.Quantity), ',') WITHIN GROUP (ORDER BY l.LineNumber) FROM orders.OrderLine l WHERE l.OrderId = o.OrderId),
    (SELECT COUNT(*) FROM orders.OrderLine l WHERE l.OrderId = o.OrderId),
    (SELECT COUNT(*) FROM orders.StubEffect e WHERE e.OrderId = o.OrderId),
    (SELECT COUNT(*) FROM orders.StubEffect e WHERE e.OrderId = o.OrderId AND e.OperationKey LIKE '%stub-complete:1'),
    (SELECT ISNULL(SUM(e.Replays), 0) FROM orders.StubEffect e WHERE e.OrderId = o.OrderId)
    FROM orders.[Order] o WHERE o.CustomerId LIKE '$1%' ORDER BY o.CreatedAt" | python -c "
import json, sys
names = ['orderId', 'customer', 'status', 'keys', 'startRequests', 'startState', 'claims', 'fenceRejections', 'startAttempts', 'history', 'lines', 'lineCount', 'effects', 'completionEffects', 'effectReplays']
for line in sys.stdin:
    cells = line.strip().split('|')
    if len(cells) != len(names):
        continue
    row = dict(zip(names, cells))
    for k in ('keys', 'startRequests', 'claims', 'fenceRejections', 'lineCount', 'effects', 'completionEffects', 'effectReplays'):
        row[k] = int(row[k])
    print(json.dumps(row))"
}

# sql_consistent <report file> <expected orders> -> "ok" or the first problems: one key and one Closed
# start request per order, history ending Completed once, one effect per line plus one completion.
sql_consistent() {
  python - "$1" "$2" <<'PY'
import json, sys
rows = [json.loads(l) for l in open(sys.argv[1]) if l.strip()]
want = int(sys.argv[2])
problems = []
if len(rows) != want:
    problems.append(f"{len(rows)} orders, expected {want}")
for r in rows:
    bad = []
    if r["keys"] != 1: bad.append(f"keys={r['keys']}")
    if r["startRequests"] != 1 or r["startState"] != "Closed": bad.append(f"start={r['startRequests']}:{r['startState']}")
    if r["status"] != "Completed" or not r["history"].endswith("Reserving,Completed") or r["history"].count("Completed") != 1: bad.append(f"history={r['history']}")
    if r["effects"] != r["lineCount"] + 1 or r["completionEffects"] != 1: bad.append(f"effects={r['effects']} for {r['lineCount']} lines")
    if r["fenceRejections"] != 0: bad.append(f"fenceRejections={r['fenceRejections']}")
    if bad: problems.append(r["orderId"] + ": " + " ".join(bad))
print("ok" if not problems else "; ".join(problems[:4]))
PY
}

finish() {
  local verdict=PASS
  (( unverified > 0 )) && verdict="PASS with $unverified UNVERIFIED"
  (( failures > 0 )) && verdict="FAIL ($failures)"
  log "$1 $RUN_ID: $verdict (results: $RESULTS_FILE)"
  exit $(( failures == 0 ? 0 : 1 ))
}
