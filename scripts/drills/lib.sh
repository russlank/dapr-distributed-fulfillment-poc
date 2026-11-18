#!/usr/bin/env bash
# Drill harness: preflight, cleanup trap, evidence, result.json. Source after setting DRILL_ID.
# Drills run only against a drill stack: scripts/stack-up.sh --drill (Compose project
# fulfillment-drill, drill images with failpoints compiled in, DAPR_POC_ENV=drill).
export COMPOSE_PROJECT_NAME="${COMPOSE_PROJECT_NAME:-fulfillment-drill}"
export BUILD_FLAVOR=drill DAPR_POC_ENV=drill
. "$(dirname "${BASH_SOURCE[0]}")/../lib.sh"

: "${DRILL_ID:?set DRILL_ID before sourcing drills/lib.sh}"
DRILL_RUN_ID="$DRILL_ID-$(date -u +%Y%m%dT%H%M%SZ)-$(printf '%04x' $RANDOM)"
DRILL_DIR="$REPO_ROOT/artifacts/drills/$DRILL_RUN_ID"
mkdir -p "$DRILL_DIR"
RESULTS_FILE="$DRILL_DIR/assertions.jsonl"
DRILL_STARTED=$SECONDS
DRILL_FAILURES=0
INJECTED_AT=""
FAULT_OBSERVED=""
RECOVERED_AT=""
CLEANUP_OK=true
CLEANUP_STEPS=()

# Refuse to run unless every role is a drill build in the drill environment, on the drill project.
drill_preflight() {
  [[ "$COMPOSE_PROJECT_NAME" == *-drill ]] || { log "REFUSED: Compose project '$COMPOSE_PROJECT_NAME' is not a drill project"; exit 2; }
  local svc id
  for svc in "${DAPR_APPS[@]}"; do
    id="$(http_body "$svc" /system/component-identity)"
    if [[ "$(json_get "$id" buildFlavor)" != drill || "$(json_get "$id" environment)" != drill || "$(json_get "$id" failpoints)" != "compiled, active" ]]; then
      log "REFUSED: $svc is not a drill build in the drill environment: ${id:-unreachable}"
      exit 2
    fi
    # A drill starts from a known good state: every role ready (its sidecar and database included).
    ready "$svc" || { log "REFUSED: $svc is not ready"; exit 2; }
    echo "$svc $id" >> "$DRILL_DIR/identity.txt"
  done
  log "preflight ok: drill stack $COMPOSE_PROJECT_NAME, run $DRILL_RUN_ID"
}

# on_cleanup "<command>": registered steps run in reverse order on exit, also after a failure.
on_cleanup() { CLEANUP_STEPS+=("$1"); }
run_cleanup() {
  local i
  for (( i=${#CLEANUP_STEPS[@]}-1; i>=0; i-- )); do
    eval "${CLEANUP_STEPS[$i]}" || CLEANUP_OK=false
  done
}

assert() { # assert <name> <PASS|FAIL> <expected> <observed>
  [[ "$2" == PASS ]] || DRILL_FAILURES=$((DRILL_FAILURES + 1))
  record "$@"
}

# arm_failpoint <database> <name> <operation key> <FailFast|Throw>
arm_failpoint() {
  sql "$1" "INSERT drill.Failpoint (Name, DrillRunId, OperationKey, Action) VALUES ('$2', '$DRILL_RUN_ID', '$3', '$4')"
  on_cleanup "sql '$1' \"DELETE drill.Failpoint WHERE DrillRunId = '$DRILL_RUN_ID' AND FiredAt IS NULL\" >/dev/null"
  log "armed $2 for $3 ($4) in $1"
}

failpoint_fired_at() { sql "$1" "SELECT CONVERT(varchar(23), FiredAt, 126) FROM drill.Failpoint WHERE DrillRunId = '$DRILL_RUN_ID' AND Name = '$2' AND FiredAt IS NOT NULL"; }

drill_finish() {
  run_cleanup
  local outcome=PASS
  (( DRILL_FAILURES == 0 )) && $CLEANUP_OK || outcome=FAIL
  python - "$DRILL_DIR" "$DRILL_ID" "$DRILL_RUN_ID" "$outcome" "$INJECTED_AT" "$FAULT_OBSERVED" "$RECOVERED_AT" \
    "$CLEANUP_OK" "$(( (SECONDS - DRILL_STARTED) * 1000 ))" "$(git rev-parse --short=10 HEAD)" <<'PY'
import json, os, sys
d, drill, run, outcome, injected, observed, recovered, cleanup, duration, commit = sys.argv[1:11]
assertions = [json.loads(l) for l in open(os.path.join(d, "assertions.jsonl"), encoding="utf-8")] if os.path.exists(os.path.join(d, "assertions.jsonl")) else []
identity = open(os.path.join(d, "identity.txt"), encoding="utf-8").read().splitlines() if os.path.exists(os.path.join(d, "identity.txt")) else []
result = {"drillId": drill, "runId": run, "gitCommit": commit, "environment": "drill", "versions": identity,
          "injectedAtUtc": injected or None, "faultObserved": observed or None, "recoveredAtUtc": recovered or None,
          "assertions": assertions, "cleanupSucceeded": cleanup == "true", "outcome": outcome, "durationMs": int(duration)}
json.dump(result, open(os.path.join(d, "result.json"), "w", encoding="utf-8"), indent=2)
PY
  log "$DRILL_ID $outcome (evidence: $DRILL_DIR/result.json)"
  [[ "$outcome" == PASS ]]
}

# drill_sql_script <database> <file> [name=value ...]: runs a SQL file with sqlcmd variables (as sa;
# -I: QUOTED_IDENTIFIER ON, which updates of tables with filtered indexes require).
drill_sql_script() {
  local db=$1 file=$2 vars=()
  shift 2
  for v in "$@"; do vars+=(-v "$v"); done
  MSYS_NO_PATHCONV=1 docker compose exec -T sqlserver /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa \
    -P "$SA_PASSWORD" -d "$db" -I -h -1 -W -s '|' -b "${vars[@]}" -Q "SET NOCOUNT ON; $(cat "$file")" | tr -d '\r'
}

# Drill-only start-request controls (scripts/drills/sql): reset a terminal order's start request to
# Pending (due after <seconds>), and restore the saved row.
drill_reset_start_request() { drill_sql_script OrderDb "$REPO_ROOT/scripts/drills/sql/reset-start-request.sql" "OrderId=$1" "DueInSeconds=${2:-10}" "DrillRunId=$DRILL_RUN_ID"; }
drill_restore_start_request() { drill_sql_script OrderDb "$REPO_ROOT/scripts/drills/sql/restore-start-request.sql" "OrderId=$1"; }

# drill_schedule_fulfillment <order id>: the workflow host's drill-only control schedules
# fulfill-{orderId:N} directly through the Dapr workflow API (no existence check). Prints Dapr's
# answer as JSON ({instanceId, scheduled, previousStatus, error}).
drill_schedule_fulfillment() {
  docker compose exec -T workflow-host bash -c \
    "exec 3<>/dev/tcp/127.0.0.1/8080 && printf 'POST /internal/drill/fulfillment/$1/schedule HTTP/1.0\r\nHost: localhost\r\ndapr-api-token: %s\r\nContent-Length: 0\r\n\r\n' \"\$APP_API_TOKEN\" >&3 && sed '1,/^\r\$/d' <&3" 2>/dev/null
}

# prometheus_sum <metric>: current sum over all series (empty when absent).
prometheus_sum() {
  curl -s "http://127.0.0.1:9095/api/v1/query" --data-urlencode "query=sum($1)" \
    | python -c "import json,sys; r=json.load(sys.stdin)['data']['result']; print(r[0]['value'][1] if r else '')" 2>/dev/null
}
