#!/usr/bin/env bash
# Shared helpers for the Dapr compatibility experiments. Source from scripts/run.sh.
set -uo pipefail

SPIKE_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$SPIKE_DIR"
set -a; . ./.env; set +a

ORCH1=http://127.0.0.1:5101
ORCH2=http://127.0.0.1:5102
RUN_ID="${RUN_ID:-$(date -u +%Y%m%dT%H%M%SZ)}"
RUN_DIR="$SPIKE_DIR/results/$RUN_ID"
mkdir -p "$RUN_DIR"

log() { printf '%s %s\n' "$(date -u +%H:%M:%S)" "$*" | tee -a "$RUN_DIR/run.log" >&2; }

# sql "<statement>" -> rows separated by |, no headers (SpikeDb / DaprStateDb by full name).
sql() {
  # Git Bash: no path conversion for the container path, but only here (it breaks /dev/null for curl.exe).
  MSYS_NO_PATHCONV=1 docker compose exec -T sqlserver /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P "$SA_PASSWORD" \
    -h -1 -W -s '|' -Q "SET NOCOUNT ON; $1" | tr -d '\r'
}

# json_get '<json>' key -> value (empty when missing).
json_get() { python -c "import sys,json; d=json.loads(sys.argv[1] or '{}'); v=d.get(sys.argv[2]); print('' if v is None else v)" "$1" "$2"; }

schedule() { curl -s -X POST "${4:-$ORCH1}/ctl/wf/$1/$2" -H 'content-type: application/json' -d "$3"; }
wf_state() { curl -s "${2:-$ORCH1}/ctl/wf/$1"; }
raise() { curl -s -X POST "${4:-$ORCH1}/ctl/wf/$1/event/$2?value=${3:-v}"; }

# wait_status <id> <status> [timeout seconds] -> prints final state JSON; exit 1 on timeout.
wait_status() {
  local id=$1 want=$2 timeout=${3:-60} base=${4:-$ORCH1} start=$SECONDS state status
  while (( SECONDS - start < timeout )); do
    state=$(wf_state "$id" "$base")
    status=$(json_get "$state" status)
    [[ "$status" == "$want" ]] && { echo "$state"; return 0; }
    sleep 1
  done
  echo "$state"
  return 1
}

# wait_custom <id> <customStatus> [timeout]
wait_custom() {
  local id=$1 want=$2 timeout=${3:-60} start=$SECONDS state
  while (( SECONDS - start < timeout )); do
    state=$(wf_state "$id")
    [[ "$(json_get "$state" customStatus)" == "$want" ]] && return 0
    sleep 1
  done
  return 1
}

# wait_sql <statement returning a number> <minimum> [timeout]
wait_sql() {
  local q=$1 min=$2 timeout=${3:-60} start=$SECONDS n
  while (( SECONDS - start < timeout )); do
    n=$(sql "$q" | head -1)
    (( ${n:-0} >= min )) && { echo "$n"; return 0; }
    sleep 1
  done
  echo "${n:-0}"
  return 1
}

evidence() { sql "SELECT Kind, [Key], Host, Detail, CONVERT(varchar(23), At, 126) FROM SpikeDb.dbo.Evidence WHERE [Key] LIKE '$1' ORDER BY Id"; }

# kill_pair <app service>: SIGKILL the app and its sidecar (a replica is the pair).
kill_pair() { docker compose kill -s KILL "$1" "$1-dapr" >/dev/null 2>&1; log "killed $1 + $1-dapr"; }
start_pair() { docker compose up -d "$1" "$1-dapr" >/dev/null 2>&1; log "started $1 + $1-dapr"; }

# wait_ready: every orchestrator answers and every sidecar is healthy.
wait_ready() {
  local start=$SECONDS
  until curl -sf "$ORCH1/healthz" >/dev/null && curl -sf "$ORCH2/healthz" >/dev/null \
    && curl -sf http://127.0.0.1:3601/v1.0/healthz -H "dapr-api-token: $DAPR_API_TOKEN" -o /dev/null \
    && curl -sf http://127.0.0.1:3701/v1.0/healthz -H "dapr-api-token: $DAPR_API_TOKEN" -o /dev/null \
    && curl -sf http://127.0.0.1:3702/v1.0/healthz -H "dapr-api-token: $DAPR_API_TOKEN" -o /dev/null; do
    (( SECONDS - start > 120 )) && { log "stack not ready"; return 1; }
    sleep 2
  done
  sleep 3
}

# replica_running_instance <id> <log phrase>: orchestrator replica whose app logged the phrase for the instance.
replica_logged() {
  local id=$1 phrase=$2
  for r in orch-1 orch-2; do
    docker compose logs "$r" 2>/dev/null | grep -q "workflow $id $phrase" && { echo "$r"; return 0; }
  done
  return 1
}

# record <assumption> <PASS|FAIL|UNVERIFIED|OBSERVED> <title> <expected> <observed> <evidence-file-or-text>
record() {
  python - "$RUN_DIR/results.jsonl" "$@" <<'PY'
import json, sys, datetime
path, aid, result, title, expected, observed, evidence = sys.argv[1:8]
with open(path, "a", encoding="utf-8") as f:
    f.write(json.dumps({"id": aid, "result": result, "title": title, "expected": expected,
                        "observed": observed, "evidence": evidence,
                        "at": datetime.datetime.now(datetime.timezone.utc).isoformat(timespec="seconds")}) + "\n")
PY
  log "RESULT $1 $2 - $3"
}
