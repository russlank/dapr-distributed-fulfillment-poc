#!/usr/bin/env bash
# Shared helpers for stack, scenario, and drill scripts. Source it; do not run it.
# Runs in Git Bash on Windows and in bash on Linux.
set -uo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"

if [[ -f .env ]]; then
  set -a; . ./.env; set +a
fi

# The same values stack-up.sh uses, so Compose sees an unchanged configuration (otherwise it
# would recreate containers with different settings when a script starts or restarts one).
export BUILD_FLAVOR="${BUILD_FLAVOR:-release}"
export DAPR_POC_ENV="${DAPR_POC_ENV:-local}"
export START_DISPATCHER_LEASE="${START_DISPATCHER_LEASE:-00:00:30}"
export CARRIER_SIM_STEP="${CARRIER_SIM_STEP:-}"
# stack-up.sh --replicas names both order API replicas as callback receivers; a script that starts the
# carrier again must pass the same list, or Compose recreates it with the single default receiver.
if [[ -z "${CARRIER_CALLBACK_URLS:-}" ]]; then
  if docker compose ps -a --services 2>/dev/null | grep -qx order-api-2; then
    export CARRIER_CALLBACK_URLS=http://order-api:8080,http://order-api-2:8080
  else
    export CARRIER_CALLBACK_URLS=http://order-api:8080
  fi
fi
export GIT_COMMIT="${GIT_COMMIT:-$(git rev-parse --short=10 HEAD 2>/dev/null || echo unknown)}"
export GIT_BRANCH="${GIT_BRANCH:-$(git rev-parse --abbrev-ref HEAD 2>/dev/null || echo unknown)}"
if [[ -z "${GIT_TREE_STATE:-}" ]]; then
  GIT_TREE_STATE=clean
  [[ -n "$(git status --porcelain 2>/dev/null)" ]] && GIT_TREE_STATE=dirty
  export GIT_TREE_STATE
fi

ORDER_API="${ORDER_API:-http://127.0.0.1:8080}"
ORDER_API_2="${ORDER_API_2:-http://127.0.0.1:8081}"

# Every Dapr-enabled role of the core profile, in start order.
DAPR_APPS=(order-api workflow-host compute-worker inventory-service payment-service shipping-service)
SIMULATORS=(carrier-simulator payment-provider-simulator)

log() { printf '%s %s\n' "$(date -u +%H:%M:%SZ)" "$*" >&2; }
die() { log "ERROR: $*"; exit 1; }

# sql <database> "<statement>": rows separated by '|', no headers, as sa (read-only checks only).
sql() {
  # Git Bash: no path conversion for the container path; set only here (it breaks /dev/null for curl.exe).
  MSYS_NO_PATHCONV=1 docker compose exec -T sqlserver /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa \
    -P "$SA_PASSWORD" -d "$1" -h -1 -W -s '|' -b -Q "SET NOCOUNT ON; $2" | tr -d '\r'
}

# http_status <service> <path>: HTTP status of GET http://127.0.0.1:8080<path> inside the container.
# Uses bash /dev/tcp because the runtime image has no curl.
http_status() {
  docker compose exec -T "$1" bash -c \
    "exec 3<>/dev/tcp/127.0.0.1/8080 && printf 'GET $2 HTTP/1.0\r\nHost: localhost\r\n\r\n' >&3 && head -1 <&3" 2>/dev/null \
    | tr -d '\r' | awk '{print $2}'
}

# http_body <service> <path>: response body of GET inside the container.
http_body() {
  docker compose exec -T "$1" bash -c \
    "exec 3<>/dev/tcp/127.0.0.1/8080 && printf 'GET $2 HTTP/1.0\r\nHost: localhost\r\n\r\n' >&3 && sed '1,/^\r\$/d' <&3" 2>/dev/null
}

# wait_until <seconds> <description> <command...>: polls once a second until the command succeeds.
wait_until() {
  local timeout=$1 what=$2 start=$SECONDS
  shift 2
  until "$@" >/dev/null 2>&1; do
    if (( SECONDS - start >= timeout )); then
      log "timed out after ${timeout}s waiting for $what"
      return 1
    fi
    sleep 1
  done
}

# json_get '<json>' 'path.to.value' -> value or empty (python, available on dev machines and CI).
json_get() {
  python -c "
import json, sys
try:
    v = json.loads(sys.argv[1])
    for k in sys.argv[2].split('.'):
        v = v[int(k)] if isinstance(v, list) else v.get(k)
        if v is None: break
    print('' if v is None else (json.dumps(v) if isinstance(v, (dict, list)) else v))
except Exception:
    print('')
" "$1" "$2"
}

# admin_api <method> <path> [json body] [base url]: call the order API with the admin key.
admin_api() {
  local method=$1 path=$2 body=${3:-} base=${4:-$ORDER_API}
  if [[ -n "$body" ]]; then
    curl -s -X "$method" "$base$path" -H "X-Api-Key: $ADMIN_API_KEY" -H 'content-type: application/json' -d "$body"
  else
    curl -s -X "$method" "$base$path" -H "X-Api-Key: $ADMIN_API_KEY"
  fi
}

ready() { [[ "$(http_status "$1" /health/ready)" == 200 ]]; }

# A replica is an app container plus its sidecar, which joins the app's network namespace.
# Whenever the app container starts again, its sidecar must be recreated: an old sidecar keeps
# running inside the dead namespace (DNS fails, the app gets "connection refused" on 3500).
kill_pair() { docker compose kill -s KILL "$1" "$1-dapr" >/dev/null 2>&1; log "killed $1 + $1-dapr (SIGKILL)"; }
_recreate_sidecar() {
  wait_until 60 "$1 running" bash -c "[[ \"\$(docker compose ps --format '{{.State}}' $1)\" == running ]]" || return 1
  docker compose up -d --no-build --no-deps --force-recreate "$1-dapr" >/dev/null || return 1
  wait_until 120 "$1 ready" ready "$1"
}
start_pair() {
  docker compose up -d --no-build --no-deps "$1" >/dev/null || return 1
  _recreate_sidecar "$1" && log "started $1 + $1-dapr (ready)" || { log "could not start $1 + $1-dapr"; return 1; }
}
# Cold restart: new app and sidecar processes (empty in-memory workflow caches).
restart_pair() {
  docker compose up -d --no-build --no-deps --force-recreate "$1" >/dev/null || return 1
  _recreate_sidecar "$1" && log "restarted $1 + $1-dapr (ready)" || { log "could not restart $1 + $1-dapr"; return 1; }
}

# Results: one JSON line per check in $RESULTS_FILE, plus a PASS/FAIL line on stderr.
RESULTS_FILE="${RESULTS_FILE:-}"
# Each line also names the commit, tree state, and build flavor the scripts ran from, so a result
# file stays attributable to its commit after it is copied or archived.
record() { # record <id> <PASS|FAIL|UNVERIFIED> <expected> <observed>
  log "$2 $1: $4"
  if [[ -n "$RESULTS_FILE" ]]; then
    python -c "
import json, sys, datetime
print(json.dumps({'check': sys.argv[1], 'result': sys.argv[2], 'expected': sys.argv[3], 'observed': sys.argv[4],
                  'at': datetime.datetime.now(datetime.timezone.utc).isoformat(timespec='seconds'),
                  'gitCommit': sys.argv[5], 'treeState': sys.argv[6], 'buildFlavor': sys.argv[7]}))
" "$1" "$2" "$3" "$4" "$GIT_COMMIT" "$GIT_TREE_STATE" "$BUILD_FLAVOR" >> "$RESULTS_FILE"
  fi
}

# review_sku: a SKU whose stub unit price makes 100 units reach the review threshold (1,000.00):
# the same FNV-1a stub price list as the compute worker (100 + hash % 9900 minor units).
review_sku() {
  python -c "
def price(s):
    h = 2166136261
    for c in s:
        h = ((h ^ ord(c)) * 16777619) & 0xFFFFFFFF
    return 100 + h % 9900
print(next(f'SKU-R{i}' for i in range(1, 1000) if price(f'SKU-R{i}') * 100 >= 100000))"
}

# review_order_body <customer id>: an order that requires a review (one line of 100 units of review_sku).
review_order_body() { printf '{"customerId":"%s","lines":[{"sku":"%s","quantity":100}]}' "$1" "${REVIEW_SKU:=$(review_sku)}"; }

# client_sim <command> [options]: the client simulator (external HTTP client; keys from the
# environment, which .env provides). Built once per script into artifacts/tools (git-ignored).
CLIENT_SIM_DIR="$REPO_ROOT/artifacts/tools/client-simulator"
client_sim() {
  if [[ -z "${CLIENT_SIM_BUILT:-}" ]]; then
    dotnet build "$REPO_ROOT/src/Fulfillment.ClientSimulator/Fulfillment.ClientSimulator.csproj" -c Release -o "$CLIENT_SIM_DIR" \
      --nologo -v q -p:SourceRevisionId="$GIT_COMMIT" >&2 || { log "client simulator build failed"; return 2; }
    CLIENT_SIM_BUILT=1
  fi
  dotnet "$CLIENT_SIM_DIR/Fulfillment.ClientSimulator.dll" "$@"
}

# Client path through Toxiproxy (deploy/compose/toxiproxy.json): order-api via 18080, order-api-2 via 18081.
TOXIPROXY="${TOXIPROXY:-http://127.0.0.1:8474}"
ORDER_API_PROXY="${ORDER_API_PROXY:-http://127.0.0.1:18080}"
ORDER_API_2_PROXY="${ORDER_API_2_PROXY:-http://127.0.0.1:18081}"
# toxic_add <proxy> <name> <type> <upstream|downstream> '<attributes json>'
toxic_add() {
  curl -s -m 10 -o /dev/null -w '%{http_code}' -X POST "$TOXIPROXY/proxies/$1/toxics" -H 'content-type: application/json' \
    -d '{"name":"'"$2"'","type":"'"$3"'","stream":"'"$4"'","toxicity":1.0,"attributes":'"$5"'}'
}
# toxic_clear <proxy>...: removes every toxic of the proxies (cleanup; safe to repeat).
toxic_clear() {
  local proxy name
  for proxy in "$@"; do
    for name in $(curl -s -m 10 "$TOXIPROXY/proxies/$proxy/toxics" | python -c "import json,sys; print(' '.join(t['name'] for t in json.load(sys.stdin)))" 2>/dev/null); do
      curl -s -m 10 -o /dev/null -X DELETE "$TOXIPROXY/proxies/$proxy/toxics/$name"
    done
  done
}

# server_outcomes <since RFC3339> <order ids file> [services...]: what each order API replica's own log
# says about the orders since then, as JSON {service: {orderId: {accepted, replayed, concurrent, reused}}}.
# The acceptance log lines name the order id; the container names the replica.
server_outcomes() {
  local since=$1 ids=$2; shift 2
  local services=("$@")
  [[ ${#services[@]} -eq 0 ]] && services=(order-api order-api-2)
  local tmp; tmp="$(mktemp -d)"
  for s in "${services[@]}"; do
    docker compose --profile replicas logs --no-log-prefix --since "$since" "$s" > "$tmp/$s.log" 2>/dev/null
  done
  python - "$tmp" "$ids" "${services[@]}" <<'PY'
import json, re, sys, os
tmp, ids_file, services = sys.argv[1], sys.argv[2], sys.argv[3:]
ids = [l.strip().lower() for l in open(ids_file) if l.strip()]
kinds = {"accepted": "accepted with", "replayed": "repeated submission (replay)", "concurrent": "concurrent submission", "reused": "reused with a different payload"}
out = {}
for s in services:
    text = open(os.path.join(tmp, s + ".log"), encoding="utf-8", errors="replace").read().lower().splitlines()
    out[s] = {i: {k: sum(1 for line in text if i in line and marker in line) for k, marker in kinds.items()} for i in ids}
print(json.dumps(out))
PY
  rm -rf "$tmp"
}

# set_stock <warehouse> <sku> <on hand>: test-only stock setup in InventoryDb (the inventory has no
# restock path); sets Reserved to 0, so use it only for SKUs without open holds (a fresh SKU per run).
set_stock() {
  sql InventoryDb "MERGE inventory.StockItem AS t USING (SELECT '$1' AS WarehouseId, '$2' AS Sku) AS s
    ON t.WarehouseId = s.WarehouseId AND t.Sku = s.Sku
    WHEN MATCHED THEN UPDATE SET OnHand = $3, Reserved = 0
    WHEN NOT MATCHED THEN INSERT (WarehouseId, Sku, OnHand, Reserved) VALUES ('$1', '$2', $3, 0);" >/dev/null
}

# inventory_invariants <sku pattern>: the SQL invariants of the matching stock rows, holds, and their
# orders' outbox, as "reserved-mismatch|hold-event-violations|sequence-gaps|holds|rows" (0|0|0|... is clean):
#   Reserved equals the sum of the Held holds of each stock row (no leaked or double-counted units);
#   each hold has exactly one HoldCreated and, once it left Held, exactly one closing event (I16;
#   HoldExtended rows are not closing events);
#   each order's outbox sequence is 1..n without gaps or repeats.
inventory_invariants() {
  sql InventoryDb "SELECT CONCAT(
    (SELECT COUNT(*) FROM inventory.StockItem s WHERE s.Sku LIKE '$1'
       AND s.Reserved <> ISNULL((SELECT SUM(h.Quantity) FROM inventory.Hold h WHERE h.WarehouseId = s.WarehouseId AND h.Sku = s.Sku AND h.State = 'Held'), 0)), '|',
    (SELECT COUNT(*) FROM inventory.Hold h WHERE h.Sku LIKE '$1' AND (
       (SELECT COUNT(*) FROM inventory.Outbox o WHERE o.HoldId = h.HoldId AND o.EventType = 'HoldCreated') <> 1
       OR (SELECT COUNT(*) FROM inventory.Outbox o WHERE o.HoldId = h.HoldId AND o.EventType IN ('HoldReleased', 'HoldExpired', 'HoldConsumed')) <> CASE WHEN h.State = 'Held' THEN 0 ELSE 1 END
       OR (h.State = 'Released' AND NOT EXISTS (SELECT 1 FROM inventory.Outbox o WHERE o.HoldId = h.HoldId AND o.EventType = 'HoldReleased'))
       OR (h.State = 'Expired' AND NOT EXISTS (SELECT 1 FROM inventory.Outbox o WHERE o.HoldId = h.HoldId AND o.EventType = 'HoldExpired'))
       OR (h.State = 'Consumed' AND NOT EXISTS (SELECT 1 FROM inventory.Outbox o WHERE o.HoldId = h.HoldId AND o.EventType = 'HoldConsumed')))), '|',
    (SELECT COUNT(*) FROM (SELECT o.OrderId FROM inventory.Outbox o WHERE o.OrderId IN (SELECT h.OrderId FROM inventory.Hold h WHERE h.Sku LIKE '$1')
       GROUP BY o.OrderId HAVING MIN(o.Sequence) <> 1 OR MAX(o.Sequence) <> COUNT(*) OR COUNT(DISTINCT o.Sequence) <> COUNT(*)) g), '|',
    (SELECT COUNT(*) FROM inventory.Hold WHERE Sku LIKE '$1'), '|',
    (SELECT COUNT(*) FROM inventory.StockItem WHERE Sku LIKE '$1'))"
}

# arm_failpoint_times <database> <name> <key> <action> <times>: arms one failpoint for the next <times>
# hits (one row per hit; rows differ by a run-id suffix). Drill cleanup removes unfired rows.
arm_failpoint_times() {
  local i
  for i in $(seq 1 "$5"); do
    sql "$1" "INSERT drill.Failpoint (Name, DrillRunId, OperationKey, Action) VALUES ('$2', '${DRILL_RUN_ID}-$i', '$3', '$4')" >/dev/null
  done
  on_cleanup "sql '$1' \"DELETE drill.Failpoint WHERE DrillRunId LIKE '${DRILL_RUN_ID}-%' AND FiredAt IS NULL\" >/dev/null"
  log "armed $2 for $3 ($4) x$5 in $1"
}

# decide <base url> <review id> <approve|reject> [decision id] [operator] -> "<status> <body>"
decide() {
  local id=${4:-$(python -c 'import uuid; print(uuid.uuid4())')} op=${5:-operator-1} out
  out="$(curl -s -w '\n%{http_code}' -X POST "$1/reviews/$2/decision" -H "X-Api-Key: $OPERATOR_API_KEY" -H 'content-type: application/json' \
    -d '{"decisionId":"'"$id"'","operatorId":"'"$op"'","outcome":"'"$3"'"}')"
  printf '%s %s\n' "${out##*$'\n'}" "$(printf '%s' "${out%$'\n'*}" | tr -d '\r\n')"
}
