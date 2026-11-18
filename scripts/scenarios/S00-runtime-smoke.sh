#!/usr/bin/env bash
# S00 runtime smoke: the deployed runtime behaves as the design assumes. Run on a ready stack
# (scripts/stack-up.sh). Checks, each PASS/FAIL from authoritative state, not API success:
#   S00-1 identity      every service reports the expected commit and flavor; release builds have no failpoints
#   S00-2 recovery      the runtime probe completes although its workflow host is SIGKILLed during the
#                       durable timer; the completed first effect is not executed again
#   S00-3 persistence   after cold restarts of the sidecars, the probe's workflow history is still readable,
#                       stored binary (state.sqlserver v2), with no corrupted rows
#   S00-4 fence         deliberately rescheduling the completed probe's instance id (which Dapr replaces)
#                       adds no effect: the execution claim rejects it
#   S00-5 access        the workflow host's management API refuses other callers, unsupported workflow
#                       types, and replacement of non-probe instances (service invocation, real sidecars)
# Usage: scripts/scenarios/S00-runtime-smoke.sh   (exit 0 only if every check passed)
. "$(dirname "$0")/../lib.sh"

RUN_ID="S00-$(date -u +%Y%m%dT%H%M%SZ)"
OUT_DIR="$REPO_ROOT/artifacts/scenarios/$RUN_ID"
mkdir -p "$OUT_DIR"
RESULTS_FILE="$OUT_DIR/results.jsonl"
PROBE="s00-$(date -u +%H%M%S)-$RANDOM"
failures=0
check() { [[ "$2" == PASS ]] || failures=$((failures + 1)); record "$@"; }

effects() { sql InventoryDb "SELECT COUNT(*) FROM diagnostics.ProbeEffect WHERE OperationKey LIKE '$PROBE:%'"; }
attempts() { sql InventoryDb "SELECT COUNT(*) FROM diagnostics.ProbeEffectAttempt WHERE OperationKey = '$PROBE:probe-effect:$1'"; }
probe_field() { json_get "$(admin_api GET "/system/probes/runtime/$PROBE")" "$1"; }
probe_completed() { [[ "$(probe_field workflow.status)" == Completed ]]; }
# Predicates run in this shell: functions such as sql are not visible inside `bash -c`.
step1_exists() { [[ "$(sql InventoryDb "SELECT COUNT(*) FROM diagnostics.ProbeEffect WHERE OperationKey = '$PROBE:probe-effect:step1'")" == 1 ]]; }
fenced_once() { [[ "$(sql OrderDb "SELECT FenceRejections FROM diagnostics.RuntimeProbe WHERE ProbeId = '$PROBE'")" -ge 1 ]]; }

log "S00 run $RUN_ID, probe $PROBE, evidence in $OUT_DIR"

# S00-1 identity ---------------------------------------------------------------------------------
expected_commit="$(git rev-parse --short=10 HEAD)"
bad=()
for svc in "${DAPR_APPS[@]}" "${SIMULATORS[@]}"; do
  id="$(http_body "$svc" /system/component-identity)"
  echo "$svc $id" >> "$OUT_DIR/identity.txt"
  commit="$(json_get "$id" commit)"; flavor="$(json_get "$id" buildFlavor)"; fp="$(json_get "$id" failpoints)"
  [[ "$commit" == "$expected_commit"* ]] || bad+=("$svc commit=$commit")
  [[ "$flavor" == release && "$fp" == absent ]] || [[ "$flavor" == drill ]] || bad+=("$svc flavor=$flavor failpoints=$fp")
done
if (( ${#bad[@]} == 0 )); then
  check S00-1 PASS "every service reports commit $expected_commit; release flavor without failpoints" \
    "${#DAPR_APPS[@]} Dapr roles + ${#SIMULATORS[@]} simulators match"
else
  check S00-1 FAIL "every service reports commit $expected_commit" "${bad[*]}"
fi

# S00-2 recovery after SIGKILL of the workflow host during the durable timer ----------------------
TIMER=45
start="$(admin_api POST "/system/probes/runtime/$PROBE" "{\"timerSeconds\":$TIMER}")"
echo "start: $start" >> "$OUT_DIR/probe.txt"
wait_until 60 "step1 effect" step1_exists || log "step1 effect not observed"
kill_pair workflow-host
killed_at="$(date -u +%FT%TZ)"
# Window proof: the kill must land inside the durable timer, i.e. before step2 exists.
step2_at_kill="$(sql InventoryDb "SELECT COUNT(*) FROM diagnostics.ProbeEffect WHERE OperationKey = '$PROBE:probe-effect:step2'")"
since_step1="$(sql InventoryDb "SELECT DATEDIFF(second, AppliedAt, SYSUTCDATETIME()) FROM diagnostics.ProbeEffect WHERE OperationKey = '$PROBE:probe-effect:step1'")"
echo "killed at $killed_at, ${since_step1}s after step1, step2 rows at kill: $step2_at_kill" >> "$OUT_DIR/probe.txt"
sleep 10
start_pair workflow-host
wait_until 180 "probe completion" probe_completed
state="$(admin_api GET "/system/probes/runtime/$PROBE")"
echo "after recovery: $state" >> "$OUT_DIR/probe.txt"
n_effects="$(effects)"; a1="$(attempts step1)"; a2="$(attempts step2)"; claims="$(json_get "$state" probe.claims)"
if [[ "$step2_at_kill" != 0 ]]; then
  check S00-2 FAIL "kill inside the ${TIMER}s timer" "window missed: step2 already existed at kill time (${since_step1}s after step1)"
elif [[ "$(json_get "$state" workflow.status)" == Completed && "$n_effects" == 2 && "$a1" == 1 && "$a2" == 1 ]]; then
  check S00-2 PASS "probe completes after SIGKILL of workflow-host during its timer; step1 not executed again" \
    "killed ${since_step1}s into the ${TIMER}s timer (step2 absent); Completed; effects=2; attempts step1=1 step2=1; claims=$claims"
else
  check S00-2 FAIL "Completed, effects=2, attempts 1/1" \
    "status=$(json_get "$state" workflow.status) effects=$n_effects attempts=$a1/$a2 claims=$claims"
fi

# S00-3 persistence: cold sidecar caches, then read history back through the state store --------
restart_pair workflow-host
restart_pair order-api
wait_until 120 "order-api ready" ready order-api
wait_until 120 "workflow-host ready" ready workflow-host
reread="$(admin_api GET "/system/probes/runtime/$PROBE")"
echo "after cold restart: $reread" >> "$OUT_DIR/probe.txt"
store="$(sql DaprStateDb "SELECT CAST(COUNT(*) AS varchar) + '|' + CAST(SUM(CASE WHEN isBinary = 1 AND BinaryData IS NOT NULL THEN 1 ELSE 0 END) AS varchar) + '|' + CAST(SUM(CASE WHEN Data LIKE N'%' + NCHAR(65533) + N'%' THEN 1 ELSE 0 END) AS varchar) FROM dbo.state WHERE [Key] LIKE '%||probe-$PROBE||history-%'" 2>&1)"
echo "history rows|binary|corrupted: $store" >> "$OUT_DIR/probe.txt"
IFS='|' read -r rows binary corrupted <<< "$store"
if [[ "$(json_get "$reread" workflow.status)" == Completed && "$(json_get "$reread" workflow.output.outcome)" == Completed \
      && "${rows:-0}" -gt 0 && "$rows" == "$binary" && "${corrupted:-1}" == 0 ]]; then
  check S00-3 PASS "history readable after cold sidecar restarts; stored binary; no corrupted rows" \
    "status Completed after restart; history rows=$rows, binary=$binary, corrupted=$corrupted"
else
  check S00-3 FAIL "readable, all history rows binary, none corrupted (v1 component would fail here)" \
    "status=$(json_get "$reread" workflow.status) store=$store"
fi

# S00-4 fence: schedule the completed instance id again ------------------------------------------
before_effects="$(effects)"
resched="$(admin_api POST "/system/probes/runtime/$PROBE/reschedule")"
echo "reschedule: $resched" >> "$OUT_DIR/probe.txt"
wait_until 60 "rescheduled run fenced" fenced_once
sleep 3
fenced="$(admin_api GET "/system/probes/runtime/$PROBE")"
echo "after reschedule: $fenced" >> "$OUT_DIR/probe.txt"
after_effects="$(effects)"
outcome="$(json_get "$fenced" workflow.output.outcome)"
if [[ "$outcome" == AlreadyTerminal && "$after_effects" == "$before_effects" && "$(json_get "$fenced" probe.fenceRejections)" -ge 1 ]]; then
  check S00-4 PASS "rescheduling a completed instance id adds no effect" \
    "Dapr accepted the reschedule (new run); run ended AlreadyTerminal; effects $before_effects -> $after_effects; fence rejections=$(json_get "$fenced" probe.fenceRejections)"
else
  check S00-4 FAIL "AlreadyTerminal, effects unchanged" "outcome=$outcome effects $before_effects -> $after_effects response=$resched"
fi

# S00-5 management access: the workflow host's management API refuses unauthorized callers, unsupported
# workflow types, and replacement of non-probe instances, through real service invocation ----------
# invoke_status <from-service> <POST|GET> <path on workflow-host>: HTTP status seen by the caller app.
invoke_status() {
  docker compose exec -T "$1" bash -c \
    "exec 3<>/dev/tcp/127.0.0.1/3500 && printf '$2 /v1.0/invoke/workflow-host/method$3 HTTP/1.0\r\nHost: localhost\r\ndapr-api-token: %s\r\nContent-Type: application/json\r\nContent-Length: 2\r\n\r\n{}' \"\$DAPR_API_TOKEN\" >&3 && head -1 <&3" 2>/dev/null \
    | tr -d '\r' | awk '{print $2}'
}
foreign_query="$(invoke_status compute-worker GET "/internal/workflows/probe-$PROBE")"
foreign_start="$(invoke_status compute-worker POST "/internal/workflows/RuntimeProbeWorkflow/probe-$PROBE-x/start")"
unknown_type="$(invoke_status order-api POST "/internal/workflows/UnknownWorkflow/probe-$PROBE-x/start")"
replace_other="$(invoke_status order-api POST "/internal/workflows/RuntimeProbeWorkflow/other-$PROBE/start?replaceTerminal=true")"
own_query="$(invoke_status order-api GET "/internal/workflows/probe-$PROBE")"
observed="compute-worker query=$foreign_query start=$foreign_start; order-api unknown type=$unknown_type, replace non-probe=$replace_other, own query=$own_query"
if [[ "$foreign_query $foreign_start $unknown_type $replace_other $own_query" == "403 403 400 403 200" ]]; then
  check S00-5 PASS "management API admits only order-api, supported types, probe-only replacement" "$observed"
else
  check S00-5 FAIL "403 403 400 403 200" "$observed"
fi

log "S00 finished: $failures failed check(s); results in $RESULTS_FILE"
(( failures == 0 ))
