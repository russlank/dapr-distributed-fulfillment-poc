#!/usr/bin/env bash
# R00 harness self-test: the failure-injection mechanism works as the drills rely on it.
# Question: when the inventory process dies right after its effect transaction commits (before the
# activity result reaches Dapr), does Dapr run the activity again, and does the operation key keep
# the effect single?
# Expected: the failpoint fires once (persisted FiredAt); the inventory container exits; after it is
# restarted the activity runs again (2 committed attempts) but the effect exists once; the probe
# completes; the execution claim count is unchanged by the activity retry.
# Usage: scripts/stack-up.sh --drill && scripts/drills/R00-failpoint-selftest.sh
DRILL_ID=R00
. "$(dirname "$0")/lib.sh"

drill_preflight
PROBE="r00-$(date -u +%H%M%S)-$RANDOM"
KEY="$PROBE:probe-effect:step1"
FAILPOINT=AfterProbeEffectCommitBeforeActivityCompletion
on_cleanup "start_pair inventory-service"
# Predicates run in this shell: functions such as sql are not visible inside `bash -c`.
fired() { [[ -n "$(failpoint_fired_at InventoryDb "$FAILPOINT")" ]]; }
probe_completed() { [[ "$(json_get "$(admin_api GET "/system/probes/runtime/$PROBE")" workflow.status)" == Completed ]]; }
trap 'drill_finish; exit $?' EXIT

arm_failpoint InventoryDb "$FAILPOINT" "$KEY" FailFast
INJECTED_AT="$(date -u +%FT%TZ)"
admin_api POST "/system/probes/runtime/$PROBE" '{"timerSeconds":2}' > "$DRILL_DIR/start.json"

# Hit proof: the persisted FiredAt, not a log line.
wait_until 60 "failpoint fired" fired
fired="$(failpoint_fired_at InventoryDb "$FAILPOINT")"
wait_until 30 "inventory-service exited" bash -c "[[ \"\$(docker compose ps -a --format '{{.State}}' inventory-service)\" == exited ]]"
exit_code="$(docker inspect -f '{{.State.ExitCode}}' "$(docker compose ps -a -q inventory-service)")"
FAULT_OBSERVED="failpoint FiredAt=$fired; inventory-service exit code $exit_code"
assert R00-fired "$([[ -n "$fired" && "$exit_code" != 0 ]] && echo PASS || echo FAIL)" \
  "failpoint fires once and the process dies" "$FAULT_OBSERVED"

attempts_before="$(sql InventoryDb "SELECT COUNT(*) FROM diagnostics.ProbeEffectAttempt WHERE OperationKey = '$KEY'")"
start_pair inventory-service
wait_until 120 "inventory ready" ready inventory-service
RECOVERED_AT="$(date -u +%FT%TZ)"

wait_until 180 "probe completion" probe_completed
state="$(admin_api GET "/system/probes/runtime/$PROBE")"
echo "$state" > "$DRILL_DIR/final.json"
attempts="$(sql InventoryDb "SELECT COUNT(*) FROM diagnostics.ProbeEffectAttempt WHERE OperationKey = '$KEY'")"
applied="$(sql InventoryDb "SELECT COUNT(*) FROM diagnostics.ProbeEffectAttempt WHERE OperationKey = '$KEY' AND Applied = 1")"
effects="$(sql InventoryDb "SELECT COUNT(*) FROM diagnostics.ProbeEffect WHERE OperationKey = '$KEY'")"
sql InventoryDb "SELECT OperationKey, Replica, Applied, CONVERT(varchar(23), At, 126) FROM diagnostics.ProbeEffectAttempt WHERE OperationKey LIKE '$PROBE:%' ORDER BY Id" > "$DRILL_DIR/attempts.txt"

assert R00-reexecuted "$([[ "$attempts_before" == 1 && "$attempts" == 2 ]] && echo PASS || echo FAIL)" \
  "activity runs again after the crash (2 committed attempts)" "attempts before restart=$attempts_before, after=$attempts"
assert R00-single-effect "$([[ "$effects" == 1 && "$applied" == 1 ]] && echo PASS || echo FAIL)" \
  "the effect exists once; only the first attempt applied it" "effects=$effects, applying attempts=$applied"
assert R00-completed "$([[ "$(json_get "$state" workflow.status)" == Completed && "$(json_get "$state" probe.claims)" == 1 ]] && echo PASS || echo FAIL)" \
  "probe completes; execution claimed once" "status=$(json_get "$state" workflow.status), claims=$(json_get "$state" probe.claims)"
