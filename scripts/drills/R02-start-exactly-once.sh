#!/usr/bin/env bash
# R02 start exactly-once in effect (S04, S49), variants A-D. Drill stack with both order API replicas:
#   bash scripts/stack-up.sh --drill --replicas
# The guarantee is about business effects: one order, one start request that ends Closed when the
# order completes, one set of effect rows; not about one physical workflow execution.
#   A  AfterOrderAcceptanceCommit (FailFast) on order-api: the process ends right after the
#      acceptance committed. The client retries on order-api-2 and gets the committed order; the
#      other replica's dispatcher starts it; after order-api restarts nothing is done again.
#   B  BeforeWorkflowStartCall (FailFast) on whichever dispatcher claims the next start intent: it
#      ends holding the lease, after its attempt row, before calling the workflow host. After the
#      lease, the other replica's dispatcher starts the workflow once; the restarted replica changes
#      nothing.
#   C  the drill-only reset puts a completed order's start request back to Pending; the terminal
#      guard closes it without a start (S49 C, the existing drill-only control).
#   D  the drill-only control schedules the completed order's instance directly; the execution fence
#      rejects it, no effect is added (S49 D, the existing drill-only control).
# For A and B, the SQL fingerprint (status, history, effects, checks, start request, attempts) is
# taken after completion and again after the crashed replica has restarted: it must not change.
DRILL_ID=R02
. "$(dirname "$0")/lib.sh"
OUT_DIR="$DRILL_DIR"
. "$REPO_ROOT/scripts/scenarios/replica-lib.sh"

drill_preflight
require_replicas
client_sim identity > "$DRILL_DIR/client-identity.json" || { log "REFUSED: the client simulator does not build"; exit 2; }
CLIENT_SIM_BUILT=1
trap 'drill_finish; exit $?' EXIT

service_state() { docker compose --profile replicas ps -a --format '{{.State}}' "$1" 2>/dev/null; }
exit_code() { docker inspect -f '{{.State.ExitCode}}' "$(docker compose --profile replicas ps -a -q "$1")" 2>/dev/null; }
# restart_if_down <service>: brings a crashed app back with a fresh sidecar (cleanup and recovery).
restart_if_down() { [[ "$(service_state "$1")" == running ]] || start_pair "$1"; }
on_cleanup "restart_if_down order-api"
on_cleanup "restart_if_down order-api-2"
fingerprint() { # <order id>: everything the order's business state consists of, plus its start bookkeeping
  sql OrderDb "SELECT CONCAT(o.Status, '#',
    (SELECT STRING_AGG(CONCAT(h.Sequence, ':', h.ToStatus, ':', CONVERT(varchar(23), h.OccurredAt, 126)), ',') WITHIN GROUP (ORDER BY h.Sequence) FROM orders.OrderStatusHistory h WHERE h.OrderId = o.OrderId), '#',
    (SELECT STRING_AGG(CONCAT(e.OperationKey, ':', e.Replays), ',') WITHIN GROUP (ORDER BY e.OperationKey) FROM orders.StubEffect e WHERE e.OrderId = o.OrderId), '#',
    (SELECT COUNT(*) FROM orders.OrderCheck c WHERE c.OrderId = o.OrderId), '#',
    (SELECT CONCAT(r.State, ':', r.ExecutionClaims, ':', r.FenceRejections) FROM orders.WorkflowStartRequest r WHERE r.OrderId = o.OrderId), '#',
    (SELECT COUNT(*) FROM orders.WorkflowStartAttempt a WHERE a.OrderId = o.OrderId))
    FROM orders.[Order] o WHERE o.OrderId = '$1'"
}
attempts_of() { sql OrderDb "SELECT STRING_AGG(CONCAT(a.DispatcherId, '=', ISNULL(a.Result, 'unfinished')), ', ') WITHIN GROUP (ORDER BY a.Id) FROM orders.WorkflowStartAttempt a WHERE a.OrderId = '$1'"; }
STAMP="$(date -u +%H%M%S)-$((RANDOM % 1000))"

# A ----------------------------------------------------------------------------------------------
RUN_A="r02a-$STAMP"
SINCE="$(date -u -d '-2 seconds' +%Y-%m-%dT%H:%M:%SZ)"
arm_failpoint OrderDb AfterOrderAcceptanceCommit "$RUN_A-o00001" FailFast
INJECTED_AT="$(date -u +%Y-%m-%dT%H:%M:%S.%3NZ)"
c="$(sim a-client single --run-id "$RUN_A" --seed 21 --expect new --url "$ORDER_API" --url "$ORDER_API_2" --request-timeout 10 --follow --follow-timeout 180)"
fired_a="$(failpoint_fired_at OrderDb AfterOrderAcceptanceCommit)"
state_a="$(service_state order-api) (exit $(exit_code order-api))"
FAULT_OBSERVED="A: order-api $state_a after FiredAt $fired_a"
order_a="$(jq_py a-client "r['items'][0].get('orderId')")"
attempts="$(jq_py a-client "[(a['endpoint'], a['class'], a.get('code'), a.get('httpStatus'), a.get('servedBy')) for a in r['items'][0]['attempts']]")"
started_by="$(attempts_of "$order_a")"
if [[ -n "$fired_a" && "$(service_state order-api)" != running && "$c" == 0 && "$(jq_py a-client "r['items'][0]['acceptance']")" == replayed \
      && "$(jq_py a-client "r['items'][0]['attempts'][-1].get('servedBy')")" == order-api-2 ]]; then
  assert R02-A-crash PASS "order-api ended right after the acceptance commit; the client's retry on order-api-2 returned the committed order" \
    "FiredAt $fired_a; order-api $state_a; client exit $c, attempts $attempts; order $order_a; start attempts while order-api was down: $started_by"
else
  assert R02-A-crash FAIL "FailFast after commit, replay on order-api-2" "FiredAt '$fired_a'; order-api $state_a; client exit $c, attempts $attempts"
fi
done_a="$(fingerprint "$order_a")"
start_pair order-api || log "could not restart order-api"
sleep 10   # the restarted replica's dispatcher polls; nothing is due
after_a="$(fingerprint "$order_a")"
sql_orders_report "$RUN_A-c" > "$DRILL_DIR/a-sql-orders.jsonl"
consistent="$(sql_consistent "$DRILL_DIR/a-sql-orders.jsonl" 1)"
started="$(sql OrderDb "SELECT COUNT(*) FROM orders.WorkflowStartAttempt WHERE OrderId = '$order_a' AND Result = 'Started'")"
observed="$consistent; started $started; fingerprint after completion = after the restart: $([[ "$done_a" == "$after_a" ]] && echo yes || echo NO) ($after_a)"
if [[ "$consistent" == ok && "$started" == 1 && "$done_a" == "$after_a" ]]; then
  assert R02-A-once PASS "one order, key, start request (Closed), one start, one effect set; unchanged after order-api restarted" "$observed"
else
  assert R02-A-once FAIL "one of each, unchanged after restart" "$observed"
fi

# B ----------------------------------------------------------------------------------------------
RUN_B="r02b-$STAMP"
pending="$(sql OrderDb "SELECT COUNT(*) FROM orders.WorkflowStartRequest WHERE State = 'Pending'")"
[[ "$pending" == 0 ]] || log "warning: $pending start requests Pending before arming the wildcard failpoint"
arm_failpoint OrderDb BeforeWorkflowStartCall '*' FailFast
c="$(sim b-client single --run-id "$RUN_B" --seed 22 --expect new --url "$ORDER_API_2" --url "$ORDER_API" --request-timeout 10 --follow --follow-timeout 240)"
fired_b="$(failpoint_fired_at OrderDb BeforeWorkflowStartCall)"
order_b="$(jq_py b-client "r['items'][0].get('orderId')")"
dead=""
for s in order-api order-api-2; do [[ "$(service_state "$s")" != running ]] && dead="$s"; done
attempts_b="$(attempts_of "$order_b")"
FAULT_OBSERVED="$FAULT_OBSERVED; B: $dead $(service_state "${dead:-order-api}") (exit $(exit_code "${dead:-order-api}")) after FiredAt $fired_b"
# The crashed dispatcher's attempt stays unfinished; the other replica's later attempt started the workflow.
b_ok="$(python -c "
import sys
parts = [p.strip() for p in sys.argv[1].split(',') if p.strip()]
dead = sys.argv[2]
first = parts[0] if parts else ''
print(len(parts) >= 2 and first.endswith('=unfinished') and first.startswith(dead + '/') and parts[-1].endswith('=Started') and not parts[-1].startswith(dead + '/'))" "$attempts_b" "$dead")"
if [[ -n "$fired_b" && -n "$dead" && "$c" == 0 && "$b_ok" == True ]]; then
  assert R02-B-crash PASS "the claiming dispatcher ended after its attempt row, before the start call; the other replica started the workflow after the lease" \
    "FiredAt $fired_b; crashed $dead (exit $(exit_code "$dead")); attempts: $attempts_b; client exit $c, final $(jq_py b-client "r['items'][0]['follow']['finalStatus']")"
else
  assert R02-B-crash FAIL "crash after claim, takeover by the other replica" "FiredAt '$fired_b'; crashed '$dead'; attempts: $attempts_b; client exit $c"
fi
done_b="$(fingerprint "$order_b")"
[[ -n "$dead" ]] && { start_pair "$dead" || log "could not restart $dead"; }
sleep 10
after_b="$(fingerprint "$order_b")"
sql_orders_report "$RUN_B-c" > "$DRILL_DIR/b-sql-orders.jsonl"
consistent="$(sql_consistent "$DRILL_DIR/b-sql-orders.jsonl" 1)"
started="$(sql OrderDb "SELECT COUNT(*) FROM orders.WorkflowStartAttempt WHERE OrderId = '$order_b' AND Result = 'Started'")"
observed="$consistent; started $started; fingerprint after completion = after the restart: $([[ "$done_b" == "$after_b" ]] && echo yes || echo NO) ($after_b)"
if [[ "$consistent" == ok && "$started" == 1 && "$done_b" == "$after_b" ]]; then
  assert R02-B-once PASS "one order, start request (Closed), one start, one effect set; unchanged after the crashed replica restarted" "$observed"
else
  assert R02-B-once FAIL "one of each, unchanged after restart" "$observed"
fi

# C and D: the existing drill-only controls, run through S49 on a fresh completed order -------------------------
bash "$REPO_ROOT/scripts/scenarios/S49-execution-fence.sh" > "$DRILL_DIR/s49.log" 2>&1
s49_exit=$?
s49_result="$(grep -o 'evidence: [^)]*result.json' "$DRILL_DIR/s49.log" | tail -1 | sed 's/evidence: //')"
for variant in C D; do
  verdict="$(python - "$s49_result" "$variant" <<'PY'
import json, sys
try:
    r = json.load(open(sys.argv[1]))
except Exception as e:
    print(f"FAIL|no S49 result ({e})"); sys.exit()
mine = [a for a in r["assertions"] if a["check"].startswith(f"S49-{sys.argv[2]}-") or a["check"] in ("S49-setup", "S49-restore")]
bad = [a for a in mine if a["result"] != "PASS"]
print(("PASS" if mine and not bad else "FAIL") + "|" + "; ".join(f"{a['check']} {a['result']}: {a['observed']}" for a in mine)[:1500])
PY
)"
  assert "R02-$variant" "${verdict%%|*}" "$( [[ $variant == C ]] && echo "terminal guard closes the reset request; no start, no change" || echo "fence rejects the directly scheduled instance; no change; metric +1")" "S49 exit $s49_exit ($s49_result): ${verdict#*|}"
done
RECOVERED_AT="$(date -u +%Y-%m-%dT%H:%M:%S.%3NZ)"
