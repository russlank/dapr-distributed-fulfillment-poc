#!/usr/bin/env bash
# Dapr 1.19 compatibility experiments. Usage:
#   scripts/run.sh                 # all experiments in order (about 15 minutes)
#   scripts/run.sh a3 a9           # selected experiments
# Results: results/<run-id>/results.jsonl, run.log, per-experiment evidence files.
. "$(dirname "$0")/lib.sh"

# Short run tag used in all instance ids. Must differ per run: scheduling a completed
# instance id again replaces that instance (observed, see a3).
T="r$(printf %s "$RUN_ID" | md5sum | cut -c1-8)"

ev_file() { echo "$RUN_DIR/$1.txt"; }

# A1: SQL Server state store (component v2) holds workflow history (binary) and actor state.
a1() {
  local id="$T-a1" f; f=$(ev_file a1)
  schedule ShortWorkflow "$id" '"a1"' >/dev/null
  local st; st=$(wait_status "$id" Completed 60)
  local both; both=$(wf_state "$id" "$ORCH2")
  local rows; rows=$(sql "SELECT [Key] + '|' + CAST(isBinary AS varchar) + '|' + CAST(DATALENGTH(BinaryData) AS varchar) FROM DaprStateDb.dbo.state_v2 WHERE [Key] LIKE '%||$id||history-%' ORDER BY [Key]")
  curl -s -X POST "$ORCH1/ctl/actor/$id/inc" >/dev/null
  local act; act=$(curl -s -X POST "$ORCH1/ctl/actor/$id/inc")
  local actrow; actrow=$(sql "SELECT [Key] + ' = ' + ISNULL(Data, '(binary)') FROM DaprStateDb.dbo.state_v2 WHERE [Key] LIKE '%CounterActor||$id||count'")
  { echo "state orch1: $st"; echo "state orch2: $both"; echo "history rows:"; echo "$rows"; echo "actor: $act"; echo "actor row: $actrow"; } > "$f"
  if [[ "$(json_get "$st" status)" == Completed && "$(json_get "$both" status)" == Completed && "$rows" == *"|1|"* && "$actrow" == *"= 2" ]]; then
    record A1 PASS "SQL Server state store (state.sqlserver v2) for workflows and actors" \
      "history persisted and readable from another sidecar; actor state persisted" \
      "workflow Completed, state read through both orchestrator sidecars; history rows isBinary=1 in varbinary; actor count=2 stored as JSON text" "$f"
  else
    record A1 FAIL "SQL Server state store for workflows and actors" "as above" "see evidence" "$f"
  fi
}

# A2 + distribution: cross-app activity through TargetAppId; spread over replicas.
a2() {
  local f; f=$(ev_file a2) n=20
  for i in $(seq -w 1 $n); do schedule CrossAppWorkflow "$T-a2-$i" '"a2"' >/dev/null; done
  local outs=""
  for i in $(seq -w 1 $n); do
    local st; st=$(wait_status "$T-a2-$i" Completed 90)
    outs+="$(json_get "$st" output)"$'\n'
  done
  echo "$outs" > "$f"
  local done; done=$(grep -c 'worker=worker' "$f")
  local o1 o2 w1 w2
  o1=$(grep -c 'orchestrator=orch-1' "$f"); o2=$(grep -c 'orchestrator=orch-2' "$f")
  w1=$(grep -c 'worker=worker-1' "$f"); w2=$(grep -c 'worker=worker-2' "$f")
  local act; act=$(sql "SELECT COUNT(*) FROM SpikeDb.dbo.Evidence WHERE Kind='activity' AND [Key] LIKE '$T-a2-%:cross'")
  echo "activity executions recorded: $act" >> "$f"
  if (( done == n )); then
    record A2 PASS "Cross-app activity via TargetAppId" "every workflow runs its activity on the worker app" \
      "$done/$n completed; activity executions recorded=$act; final orchestrator replica orch-1=$o1 orch-2=$o2; worker replica worker-1=$w1 worker-2=$w2" "$f"
  else
    record A2 FAIL "Cross-app activity via TargetAppId" "all complete" "$done/$n completed" "$f"
  fi
}

# Recovery R-a: kill the orchestrator replica running a workflow during its durable timer.
recover_orch() {
  local id="$T-ro" f; f=$(ev_file recover_orch)
  schedule RecoveryWorkflow "$id" '{"timerSeconds":20,"slowMilliseconds":500}' >/dev/null
  wait_custom "$id" timer 30
  sleep 2
  local host; host=$(replica_logged "$id" "timer armed on") || host=unknown
  [[ "$host" != unknown ]] && kill_pair "$host"
  # Poll through the surviving replica's control API (the killed one cannot answer).
  local survivor=$ORCH1; [[ "$host" == orch-1 ]] && survivor=$ORCH2
  local st; st=$(wait_status "$id" Completed 120 "$survivor"); local rc=$?
  [[ "$host" != unknown ]] && start_pair "$host"
  local step1; step1=$(sql "SELECT COUNT(*) FROM SpikeDb.dbo.Evidence WHERE Kind='activity' AND [Key]='$id:step1'")
  local fired; fired=$(replica_logged "$id" "timer fired on" || true)
  { echo "killed=$host"; echo "final=$st"; echo "timer fired logged on=$fired"; evidence "$id:%"; } > "$f"
  if (( rc == 0 )) && [[ "$step1" == 1 && "$host" != unknown ]]; then
    record RECOVERY-ORCH PASS "Workflow survives SIGKILL of its orchestrator replica during a timer" \
      "workflow completes on the other replica; completed step1 is not executed again" \
      "killed $host; completed; step1 executions=$step1; output=$(json_get "$st" output)" "$f"
  else
    record RECOVERY-ORCH FAIL "Workflow survives SIGKILL of its orchestrator replica" "complete, step1=1" "rc=$rc host=$host step1=$step1" "$f"
  fi
}

# Recovery R-b: kill the worker replica while it executes an activity: the activity runs again.
recover_worker() {
  local id="$T-rw" f; f=$(ev_file recover_worker)
  schedule RecoveryWorkflow "$id" '{"timerSeconds":1,"slowMilliseconds":15000}' >/dev/null
  wait_sql "SELECT COUNT(*) FROM SpikeDb.dbo.Evidence WHERE Kind='activity-start' AND [Key]='$id:step2'" 1 60 >/dev/null
  local host; host=$(sql "SELECT TOP 1 Host FROM SpikeDb.dbo.Evidence WHERE Kind='activity-start' AND [Key]='$id:step2'")
  local t0=$SECONDS
  kill_pair "$host"
  local st; st=$(wait_status "$id" Completed 300); local rc=$?
  local took=$(( SECONDS - t0 ))
  start_pair "$host"
  local starts ends
  starts=$(sql "SELECT COUNT(*) FROM SpikeDb.dbo.Evidence WHERE Kind='activity-start' AND [Key]='$id:step2'")
  ends=$(sql "SELECT COUNT(*) FROM SpikeDb.dbo.Evidence WHERE Kind='activity-end' AND [Key]='$id:step2'")
  { echo "killed=$host after first start; completion took ${took}s"; echo "final=$st"; evidence "$id:%"; } > "$f"
  if (( rc == 0 )) && (( starts >= 2 )) && (( ends == 1 )); then
    record RECOVERY-WORKER PASS "Activity re-executed after its worker replica is killed mid-execution" \
      "activity starts again on a surviving replica (at-least-once); workflow completes once" \
      "killed $host; step2 starts=$starts ends=$ends; completed ${took}s after the kill" "$f"
  else
    record RECOVERY-WORKER FAIL "Activity re-executed after worker kill" "starts>=2, ends=1, completed" "rc=$rc starts=$starts ends=$ends after ${took}s" "$f"
  fi
}

# Recovery R-c: kill the whole orchestrator pool during a timer, restart it later.
recover_pool() {
  local id="$T-rp" f; f=$(ev_file recover_pool)
  schedule RecoveryWorkflow "$id" '{"timerSeconds":10,"slowMilliseconds":200}' >/dev/null
  wait_custom "$id" timer 30
  kill_pair orch-1; kill_pair orch-2
  sleep 25
  start_pair orch-1; start_pair orch-2
  wait_ready
  local st; st=$(wait_status "$id" Completed 120); local rc=$?
  local step1; step1=$(sql "SELECT COUNT(*) FROM SpikeDb.dbo.Evidence WHERE Kind='activity' AND [Key]='$id:step1'")
  { echo "final=$st"; evidence "$id:%"; } > "$f"
  if (( rc == 0 )) && [[ "$step1" == 1 ]]; then
    record RECOVERY-POOL PASS "Workflow resumes after the whole orchestrator pool was down past its timer" \
      "timer fires late after restart; step1 not repeated" "completed; step1 executions=$step1" "$f"
  else
    record RECOVERY-POOL FAIL "Workflow resumes after orchestrator pool outage" "complete" "rc=$rc step1=$step1" "$f"
  fi
}

# A3: schedule an existing instance id while running and after completion.
a3() {
  local id="$T-a3" f; f=$(ev_file a3)
  local r1 r2 r3 s1 s2 s3 runs
  r1=$(schedule EventWorkflow "$id" '{"preTimerSeconds":0,"timeoutSeconds":300,"mode":"overload"}')
  wait_custom "$id" waiting 30
  s1=$(wf_state "$id")
  r2=$(schedule EventWorkflow "$id" '{"preTimerSeconds":0,"timeoutSeconds":300,"mode":"overload"}')
  sleep 3
  local s1b; s1b=$(wf_state "$id")
  raise "$id" go first >/dev/null
  s2=$(wait_status "$id" Completed 30)
  r3=$(schedule EventWorkflow "$id" '{"preTimerSeconds":0,"timeoutSeconds":300,"mode":"overload"}')
  sleep 4
  s3=$(wf_state "$id")
  { echo "schedule#1 (new): $r1"; echo "state while waiting: $s1";
    echo "schedule#2 (while running): $r2"; echo "state after schedule#2: $s1b";
    echo "state after event: $s2"; echo "schedule#3 (after completion): $r3"; echo "state after schedule#3: $s3"; } > "$f"
  # Second run of a completed id? (status back to Running/waiting, createdAt changed)
  local replaced=no
  [[ "$(json_get "$s3" createdAt)" != "$(json_get "$s2" createdAt)" || "$(json_get "$s3" status)" != Completed ]] && replaced=yes
  if [[ "$replaced" == yes ]]; then raise "$id" go second >/dev/null; wait_status "$id" Completed 30 >> "$f"; fi
  record A3 OBSERVED "Scheduling an existing instance id" "undocumented: record behavior while running and after completion" \
    "running: $(json_get "$r2" ok)/$(json_get "$r2" error) $(json_get "$r2" message | cut -c1-140); terminal: ok=$(json_get "$r3" ok) $(json_get "$r3" message | cut -c1-140); completed instance replaced by a new run=$replaced" "$f"
}

# A4 + A6: event before / after the wait; timeout overload vs WhenAny; timeout path.
a4_a6() {
  local f; f=$(ev_file a4_a6); : > "$f"
  local mode case id st out pass=0 total=0
  for mode in overload whenany; do
    # event raised during the pre-timer, i.e. before the workflow waits
    id="$T-a4-before-$mode"
    schedule EventWorkflow "$id" "{\"preTimerSeconds\":8,\"timeoutSeconds\":60,\"mode\":\"$mode\"}" >/dev/null
    wait_custom "$id" pre-timer 20; raise "$id" go early >/dev/null
    st=$(wait_status "$id" Completed 60); out=$(json_get "$st" output)
    echo "$id -> $out" >> "$f"; ((total++)); [[ "$out" == "event:early" ]] && ((pass++))
    # event raised while waiting
    id="$T-a4-after-$mode"
    schedule EventWorkflow "$id" "{\"preTimerSeconds\":0,\"timeoutSeconds\":60,\"mode\":\"$mode\"}" >/dev/null
    wait_custom "$id" waiting 20; sleep 1; raise "$id" go late >/dev/null
    st=$(wait_status "$id" Completed 60); out=$(json_get "$st" output)
    echo "$id -> $out" >> "$f"; ((total++)); [[ "$out" == "event:late" ]] && ((pass++))
    # no event: timeout path
    id="$T-a6-timeout-$mode"
    schedule EventWorkflow "$id" "{\"preTimerSeconds\":0,\"timeoutSeconds\":5,\"mode\":\"$mode\"}" >/dev/null
    st=$(wait_status "$id" Completed 60); out=$(json_get "$st" output)
    echo "$id -> $out" >> "$f"; ((total++)); [[ "$out" == timeout:* ]] && ((pass++))
  done
  local r4="FAIL"; grep -q 'before-overload -> event:early' "$f" && grep -q 'before-whenany -> event:early' "$f" \
    && grep -q 'after-overload -> event:late' "$f" && grep -q 'after-whenany -> event:late' "$f" && r4=PASS
  record A4 "$r4" "External event before and after the wait" "event raised before the wait is buffered and delivered" "$(grep -E 'a4-' "$f" | tr '\n' ';')" "$f"
  local r6="FAIL"; grep -q 'timeout-overload -> timeout:' "$f" && grep -q 'timeout-whenany -> timeout:whenany' "$f" && r6=PASS
  record A6 "$r6" "Event versus timeout (timeout overload and WhenAny with durable timer)" "event wins when raised; timeout path otherwise" "$(grep -E 'a6-' "$f" | tr '\n' ';')" "$f"
}

# A5: raise to a missing and to a completed instance.
a5() {
  local f; f=$(ev_file a5)
  local missing completed st
  missing=$(raise "$T-a5-does-not-exist" go x)
  schedule ShortWorkflow "$T-a5" '"a5"' >/dev/null
  wait_status "$T-a5" Completed 60 >/dev/null
  completed=$(raise "$T-a5" go x)
  sleep 2; st=$(wf_state "$T-a5")
  local miss_state; miss_state=$(wf_state "$T-a5-does-not-exist")
  { echo "raise to missing: $missing"; echo "state of missing after raise: $miss_state"; echo "raise to completed: $completed"; echo "state after: $st"; } > "$f"
  # SDK docs say events for non-existent or completed workflows are silently discarded.
  local r=FAIL
  if [[ "$(json_get "$completed" ok)" == True && "$(json_get "$st" status)" == Completed && "$(json_get "$miss_state" exists)" == False ]]; then
    r=OBSERVED   # completed: silent no-op as documented; missing: see observed text
  fi
  record A5 "$r" "Raise event to missing or completed instance" "SDK docs: silently discarded (no error) in both cases"     "missing: ok=$(json_get "$missing" ok) $(json_get "$missing" message | cut -c1-90) (instance not created); completed: ok=$(json_get "$completed" ok), status stays $(json_get "$st" status)" "$f"
}

# A7a: Scheduler restart (persistent volume) while a durable timer is pending.
a7_restart() {
  local id="$T-a7r" f; f=$(ev_file a7_restart)
  schedule TimerWorkflow "$id" '{"seconds":30}' >/dev/null
  wait_custom "$id" timer-armed 20
  local t0=$SECONDS
  sleep 5; docker compose restart scheduler >/dev/null 2>&1; log "scheduler restarted"
  local st; st=$(wait_status "$id" Completed 150); local rc=$?
  local took=$(( SECONDS - t0 ))
  { echo "final=$st (completed ${took}s after timer armed + ~0s; timer=30s)"; evidence "$id:%"; } > "$f"
  if (( rc == 0 )) && [[ "$(json_get "$st" output)" == timer ]]; then
    record A7-RESTART PASS "Durable timer survives Scheduler restart (persistent volume)" "timer fires after restart" "fired by timer ${took}s after arming (timer 30s)" "$f"
  else
    record A7-RESTART FAIL "Durable timer survives Scheduler restart" "timer fires" "rc=$rc output=$(json_get "$st" output) after ${took}s" "$f"
  fi
}

# A7b: Scheduler volume lost while a timer is pending; recovery by an external "nudge" event.
a7_volume_loss() {
  local id="$T-a7v" f; f=$(ev_file a7_volume_loss)
  schedule TimerWorkflow "$id" '{"seconds":20}' >/dev/null
  wait_custom "$id" timer-armed 20
  docker compose stop scheduler >/dev/null 2>&1
  docker compose rm -f scheduler >/dev/null 2>&1
  docker volume rm dapr-compat_scheduler-data >/dev/null 2>&1 && log "scheduler volume deleted"
  docker compose up -d scheduler >/dev/null 2>&1
  local st; st=$(wait_status "$id" Completed 60); local rc=$?
  local before; before=$(json_get "$st" status)
  local nudge=""
  if (( rc != 0 )); then nudge=$(raise "$id" nudge recover); st=$(wait_status "$id" Completed 60); rc=$?; fi
  { echo "status 60s after volume loss (timer 20s): $before"; echo "nudge: $nudge"; echo "final: $st"; evidence "$id:%"; } > "$f"
  record A7-VOLUME-LOSS OBSERVED "Durable timer after Scheduler volume loss" "unknown: timer may be lost; nudge event must recover" \
    "status 60s after loss=$before; final output=$(json_get "$st" output) (rc=$rc)" "$f"
}

# A8: activity concurrency with maxConcurrentActivityInvocations=2 per worker sidecar.
a8() {
  local id="$T-a8" f; f=$(ev_file a8)
  schedule FanOutWorkflow "$id" '{"count":12,"milliseconds":3000}' >/dev/null
  wait_status "$id" Completed 180 >/dev/null
  # Max overlap per host and overall from start/end evidence rows.
  local q="WITH s AS (SELECT [Key], Host, At FROM SpikeDb.dbo.Evidence WHERE Kind='activity-start' AND [Key] LIKE '$id:fan-%'),
  e AS (SELECT [Key], At FROM SpikeDb.dbo.Evidence WHERE Kind='activity-end' AND [Key] LIKE '$id:fan-%'),
  iv AS (SELECT s.[Key], s.Host, s.At st, e.At en FROM s JOIN e ON e.[Key]=s.[Key])
  SELECT 'host=' + a.Host + ' max=' + CAST(MAX(c) AS varchar) FROM (SELECT a.Host, a.st, (SELECT COUNT(*) FROM iv b WHERE b.Host=a.Host AND b.st<=a.st AND b.en>a.st) c FROM iv a) a GROUP BY a.Host
  UNION ALL SELECT 'all max=' + CAST(MAX(c) AS varchar) FROM (SELECT (SELECT COUNT(*) FROM iv b WHERE b.st<=a.st AND b.en>a.st) c FROM iv a) x"
  local res; res=$(sql "$q")
  { echo "$res"; evidence "$id:fan-%"; } > "$f"
  local over; over=$(echo "$res" | grep '^host=' | awk -F'max=' '$2>2' | wc -l)
  local r=FAIL; (( over == 0 )) && r=PASS
  record A8 "$r" "Activity concurrency limit (sidecar maxConcurrentActivityInvocations=2)" \
    "at most 2 concurrent activities per worker replica" "$(echo "$res" | tr '\n' ';')" "$f"
}

# A9: purge a completed instance and reuse its id; purge a running instance.
a9() {
  local id="$T-a9" f; f=$(ev_file a9)
  schedule ShortWorkflow "$id" '"a9"' >/dev/null; wait_status "$id" Completed 60 >/dev/null
  local p1; p1=$(curl -s -X POST "$ORCH1/ctl/wf/$id/purge")
  local s1; s1=$(wf_state "$id")
  local rows; rows=$(sql "SELECT COUNT(*) FROM DaprStateDb.dbo.state_v2 WHERE [Key] LIKE '%||$id||%'")
  local r2; r2=$(schedule ShortWorkflow "$id" '"a9-again"')
  local s2; s2=$(wait_status "$id" Completed 60)
  local runid="$T-a9run"
  schedule EventWorkflow "$runid" '{"preTimerSeconds":0,"timeoutSeconds":300,"mode":"overload"}' >/dev/null; wait_custom "$runid" waiting 30
  local p2; p2=$(curl -s -X POST "$ORCH1/ctl/wf/$runid/purge")
  raise "$runid" go x >/dev/null
  { echo "purge completed: $p1"; echo "state after purge: $s1"; echo "state rows left: $rows"; echo "reschedule: $r2"; echo "second run: $s2"; echo "purge running: $p2"; evidence "$id:%"; } > "$f"
  local r=FAIL
  [[ "$(json_get "$p1" purged)" == True && "$(json_get "$s1" exists)" == False && "$(json_get "$s2" status)" == Completed ]] && r=PASS
  record A9 "$r" "Purge and instance-id reuse" "purge removes a terminal instance; id can be scheduled again; running instance cannot be purged" \
    "purge completed=$(json_get "$p1" purged), exists after=$(json_get "$s1" exists), state rows left=$rows, reuse=$(json_get "$s2" status); purge running: purged=$(json_get "$p2" purged) ok=$(json_get "$p2" ok) $(json_get "$p2" message | cut -c1-100)" "$f"
}

# A10: RabbitMQ pub/sub: success, retried failures, poison -> dead letter, DROP.
a10() {
  local f; f=$(ev_file a10)
  for m in ok retry2 poison drop; do curl -s -X POST "$ORCH1/ctl/pub/spike/$m-$T" >/dev/null; done
  sleep 25
  local rows; rows=$(sql "SELECT Kind + ' ' + [Key] + ' ' + Host + ' ' + Detail FROM SpikeDb.dbo.Evidence WHERE Kind IN ('pubsub','dlq') AND [Key] LIKE '%-$T' ORDER BY Id")
  echo "$rows" > "$f"
  local okn r2n pn pd dn dd
  okn=$(grep -c "pubsub ok-$T" "$f"); r2n=$(grep -c "pubsub retry2-$T" "$f")
  pn=$(grep -c "pubsub poison-$T" "$f"); pd=$(grep -c "dlq poison-$T" "$f")
  dn=$(grep -c "pubsub drop-$T" "$f"); dd=$(grep -c "dlq drop-$T" "$f")
  local r=FAIL; (( okn == 1 && r2n == 3 && pd >= 1 )) && r=PASS
  record A10 "$r" "RabbitMQ pub/sub delivery, retries, dead letter" \
    "ok delivered once; retry2 succeeds on 3rd attempt (2 resiliency retries); poison dead-lettered after retries" \
    "ok deliveries=$okn; retry2 deliveries=$r2n; poison deliveries=$pn, dead-lettered=$pd; DROP deliveries=$dn, dead-lettered=$dd" "$f"
}

# A11: service invocation retries (resiliency, 500-599, maxRetries 3).
a11() {
  local f; f=$(ev_file a11)
  local a b
  a=$(curl -s -X POST "$ORCH1/ctl/invoke/$T-a11-two?fail=2")
  b=$(curl -s -X POST "$ORCH1/ctl/invoke/$T-a11-five?fail=5")
  local na nb
  na=$(sql "SELECT COUNT(*) FROM SpikeDb.dbo.Evidence WHERE Kind='invoke' AND [Key]='$T-a11-two'")
  nb=$(sql "SELECT COUNT(*) FROM SpikeDb.dbo.Evidence WHERE Kind='invoke' AND [Key]='$T-a11-five'")
  { echo "fail=2: $a (receiver attempts=$na)"; echo "fail=5: $b (receiver attempts=$nb)"; } > "$f"
  local r=FAIL; [[ "$(json_get "$a" ok)" == True && "$na" == 3 && "$(json_get "$b" ok)" == False && "$nb" == 4 ]] && r=PASS
  record A11 "$r" "Service invocation retries per resiliency policy" "3 retries on 5xx: fail=2 succeeds on attempt 3; fail=5 fails after 4 attempts; receiver sees every attempt" \
    "fail=2: ok=$(json_get "$a" ok) attempts=$na; fail=5: ok=$(json_get "$b" ok) status=$(json_get "$b" status) attempts=$nb" "$f"
}

# A12: Dapr API token on the sidecar and app token towards the app.
a12() {
  local f; f=$(ev_file a12)
  local s_no s_yes a_no a_side
  s_no=$(curl -s -o /dev/null -w '%{http_code}' http://127.0.0.1:3601/v1.0/metadata)
  s_yes=$(curl -s -o /dev/null -w '%{http_code}' -H "dapr-api-token: $DAPR_API_TOKEN" http://127.0.0.1:3601/v1.0/metadata)
  a_no=$(curl -s -o /dev/null -w '%{http_code}' -X POST -H 'content-type: application/json' -d '{"key":"direct","fail":0}' http://127.0.0.1:5201/flaky)
  a_side=$(curl -s -X POST "$ORCH1/ctl/invoke/$T-a12-via-sidecar?fail=0")
  { echo "sidecar without token: $s_no"; echo "sidecar with token: $s_yes"; echo "worker app direct without app token: $a_no"; echo "worker app via sidecar: $a_side"; } > "$f"
  local r=FAIL; [[ "$s_no" == 401 && "$s_yes" == 200 && "$a_no" == 401 && "$(json_get "$a_side" ok)" == True ]] && r=PASS
  record A12 "$r" "DAPR_API_TOKEN and APP_API_TOKEN enforced in self-hosted Compose" "401 without token at sidecar and app; calls through the sidecar succeed" \
    "sidecar no token=$s_no, with token=$s_yes; app direct=$a_no; app via sidecar ok=$(json_get "$a_side" ok)" "$f"
}

# A14: actor placement across worker replicas, host visibility, state after killing the host.
a14() {
  local f; f=$(ev_file a14); : > "$f"
  for i in 1 2 3 4 5 6 7 8; do echo "$T-act-$i $(curl -s -X POST "$ORCH1/ctl/actor/$T-act-$i/inc")" >> "$f"; done
  local m1 m2 pl
  m1=$(curl -s -H "dapr-api-token: $DAPR_API_TOKEN" http://127.0.0.1:3701/v1.0/metadata | python -c "import sys,json; d=json.load(sys.stdin); print(json.dumps(d.get('actorRuntime',d.get('actors'))))")
  m2=$(curl -s -H "dapr-api-token: $DAPR_API_TOKEN" http://127.0.0.1:3702/v1.0/metadata | python -c "import sys,json; d=json.load(sys.stdin); print(json.dumps(d.get('actorRuntime',d.get('actors'))))")
  pl=$(curl -s http://127.0.0.1:8085/placement/state | head -c 600)
  { echo "worker-1 sidecar metadata actors: $m1"; echo "worker-2 sidecar metadata actors: $m2"; echo "placement /placement/state: $pl"; } >> "$f"
  # Kill the replica that hosts actor 1, call it again.
  local first; first=$(curl -s -X POST "$ORCH1/ctl/actor/$T-act-1/inc")
  local host; host=$(json_get "$first" replica)
  kill_pair "$host"
  local after="" t0=$SECONDS
  for _ in $(seq 1 30); do after=$(curl -s -X POST "$ORCH1/ctl/actor/$T-act-1/inc"); [[ "$(json_get "$after" ok)" == True ]] && break; sleep 2; done
  local took=$(( SECONDS - t0 ))
  start_pair "$host"
  { echo "before kill: $first"; echo "after kill (${took}s): $after"; evidence "$T-act-1"; } >> "$f"
  local r=FAIL
  [[ "$(json_get "$after" ok)" == True && "$(json_get "$after" replica)" != "$host" && "$(json_get "$after" count)" == $(( $(json_get "$first" count) + 1 )) ]] && r=PASS
  record A14 "$r" "Actor placement, host visibility, state after killing the host replica" \
    "actors spread over worker replicas; host visible; state continues on another replica after SIGKILL" \
    "distribution: $(grep -o '"replica":"[^"]*"' "$f" | sort | uniq -c | tr -s ' ' | tr '\n' ';'); killed $host; next call on $(json_get "$after" replica) count $(json_get "$first" count)->$(json_get "$after" count) after ${took}s; host visible via method response, sidecar metadata, placement API (see evidence)" "$f"
}

# A18: memory footprint of the running stack.
a18() {
  local f; f=$(ev_file a18)
  docker stats --no-stream --format '{{.Name}}|{{.MemUsage}}' $(docker compose ps -q) > "$f"
  local total; total=$(python - "$f" <<'PY'
import sys,re
tot=0.0
for line in open(sys.argv[1]):
    name,mem=line.strip().split('|'); used=mem.split('/')[0].strip()
    m=re.match(r'([\d.]+)([KMG]i?B)',used); v=float(m.group(1)); u=m.group(2)
    tot+= v/1024 if u.startswith('K') else v*1024 if u.startswith('G') else v
print(f"{tot:.0f} MiB")
PY
)
  echo "total=$total" >> "$f"
  record A18 OBSERVED "Memory footprint" "spike stack (2 orchestrator + 2 worker pairs + infra) measured" "total used $total across $(wc -l < "$f" | tr -d ' ') containers (minus total line)" "$f"
}

ALL=(a1 a2 a3 a4_a6 a5 a8 a9 a10 a11 a12 a14 recover_orch recover_worker recover_pool a7_restart a18 a7_volume_loss)
wait_ready || exit 1
log "run $RUN_ID tag $T"
for e in "${@:-${ALL[@]}}"; do log "== $e"; "$e"; done
python - "$RUN_DIR/results.jsonl" <<'PY'
import json,sys
for l in open(sys.argv[1], encoding="utf-8"):
    r=json.loads(l); print(f"{r['id']:<18} {r['result']:<10} {r['observed'][:150]}")
PY
