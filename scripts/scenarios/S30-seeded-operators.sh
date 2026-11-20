#!/usr/bin/env bash
# S30 seeded scripted operators are reproducible. Drill stack: bash scripts/stack-up.sh --drill --replicas
# Orders that require a review are created by a seeded load run; the scripted operator (operator run)
# chooses per order approve, reject, escalate (then approve round 2), or ignore, from a seed.
#   S30-plan       the same seed and orders give the same plan and digest twice (plan-only, nothing
#                  sent); another seed gives another digest
#   S30-run        the run follows the plan (exit 0); every order ends as its choice implies: approve and
#                  escalate Completed (escalate: round 1 Escalated, round 2 Approved), reject
#                  Rejected/ReviewRejected, ignore Rejected/ReviewExpired after both rounds expired
#   S30-replay     the same command again: exit 0, every decision and escalation comes back as a replay,
#                  no new decision, no new round
. "$(dirname "$0")/../lib.sh"
. "$(dirname "$0")/m3-lib.sh"
. "$(dirname "$0")/replica-lib.sh"
scenario_init S30
N="${S30_ORDERS:-12}"; SEED="${S30_SEED:-30}"
URLS=(--url "$ORDER_API" --url "$ORDER_API_2")
WEIGHTS=(--approve 2 --reject 1 --escalate 1 --ignore 1 --operators operator-1,operator-2)
c="$(sim load load --run-id "$PREFIX" --seed "$SEED" --orders "$N" --review-rate 1 --follow --follow-until review --follow-timeout 90 "${URLS[@]}" --output "$OUT_DIR/load.result.json")"
[[ "$c" == 0 ]] || log "load exit $c"

p1="$(sim plan-1 operator run --from "$OUT_DIR/load.result.json" --seed "$SEED" "${WEIGHTS[@]}" --plan-only)"
p2="$(sim plan-2 operator run --from "$OUT_DIR/load.result.json" --seed "$SEED" "${WEIGHTS[@]}" --plan-only)"
p3="$(sim plan-other operator run --from "$OUT_DIR/load.result.json" --seed "$((SEED + 1))" "${WEIGHTS[@]}" --plan-only)"
d1="$(jq_py plan-1 "r['planDigest']")"; d2="$(jq_py plan-2 "r['planDigest']")"; d3="$(jq_py plan-other "r['planDigest']")"
same="$(python -c "import json,sys; print(json.load(open(sys.argv[1]))['plan'] == json.load(open(sys.argv[2]))['plan'])" "$OUT_DIR/plan-1.json" "$OUT_DIR/plan-2.json")"
actions="$(jq_py plan-1 "sorted({c['action'] for c in r['plan']})")"
expect S30-plan "same seed: same plan and digest; other seed: other digest" "digests $d1 / $d2 / other $d3; plans equal $same; actions $actions; exits $p1 $p2 $p3" \
  "$([[ "$p1$p2$p3" == 000 && "$d1" == "$d2" && "$same" == True && "$d1" != "$d3" ]]; echo $?)"

r1="$(sim run-1 operator run --from "$OUT_DIR/load.result.json" --seed "$SEED" "${WEIGHTS[@]}" --wait 30 "${URLS[@]}")"
plan_of() { jq_py plan-1 "{c['orderId']: c['action'] for c in r['plan']}"; }
orders_terminal() { [[ "$(sql OrderDb "SELECT COUNT(*) FROM orders.[Order] WHERE CustomerId LIKE '$PREFIX-c%' AND Status IN ('Completed', 'Rejected', 'Compensated', 'Cancelled')")" == "$N" ]]; }
wait_until 180 "all $N orders terminal" orders_terminal
python - "$OUT_DIR/plan-1.json" > "$OUT_DIR/expected.txt" <<'PY'
import json, sys
for c in json.load(open(sys.argv[1]))["plan"]:
    print(c["orderId"], c["action"])
PY
sed -i 's/\r$//' "$OUT_DIR/expected.txt"
mismatch=(); summary=()
while read -r -u 3 o action; do
  end="$(end_of "$o")"
  rounds="$(sqln OrderDb "SELECT STRING_AGG(CONCAT(Round, ':', Status), ',') WITHIN GROUP (ORDER BY Round) FROM orders.ReviewTask WHERE OrderId = '$o'")"
  case "$action" in
    approve) want="Completed/Delivered|1:Approved" ;;
    reject) want="Rejected/ReviewRejected|1:Rejected" ;;
    escalate) want="Completed/Delivered|1:Escalated,2:Approved" ;;
    ignore) want="Rejected/ReviewExpired|1:Expired,2:Expired" ;;
  esac
  summary+=("$action:$end")
  [[ "$end|$rounds" == "$want" ]] || mismatch+=("$o $action: $end|$rounds (want $want)")
done 3< "$OUT_DIR/expected.txt"
expect S30-run "run exit 0; every order ends as its choice implies" "exit $r1; results $(jq_py run-1 "r['summary']['results']"); ${summary[*]}; mismatches ${mismatch[*]:-none}" \
  "$([[ "$r1" == 0 && ${#mismatch[@]} == 0 ]]; echo $?)"

decisions_before="$(sql OrderDb "SELECT CONCAT((SELECT COUNT(*) FROM orders.ReviewDecision d JOIN orders.[Order] o ON o.OrderId = d.OrderId WHERE o.CustomerId LIKE '$PREFIX-c%'), '/', (SELECT COUNT(*) FROM orders.ReviewTask t JOIN orders.[Order] o ON o.OrderId = t.OrderId WHERE o.CustomerId LIKE '$PREFIX-c%'))")"
r2="$(sim run-2 operator run --from "$OUT_DIR/load.result.json" --seed "$SEED" "${WEIGHTS[@]}" --wait 0 "${URLS[@]}")"
decisions_after="$(sql OrderDb "SELECT CONCAT((SELECT COUNT(*) FROM orders.ReviewDecision d JOIN orders.[Order] o ON o.OrderId = d.OrderId WHERE o.CustomerId LIKE '$PREFIX-c%'), '/', (SELECT COUNT(*) FROM orders.ReviewTask t JOIN orders.[Order] o ON o.OrderId = t.OrderId WHERE o.CustomerId LIKE '$PREFIX-c%'))")"
replays="$(jq_py run-2 "sorted({x['result'] for x in r['items']})")"
expect S30-replay "exit 0; only replayed (and ignored); decisions/rounds unchanged" "exit $r2; results $replays; decisions/rounds before $decisions_before after $decisions_after; plan digest $(jq_py run-2 "r['workloadDigest']")" \
  "$([[ "$r2" == 0 && "$decisions_before" == "$decisions_after" && "$(jq_py run-2 "all(x['result'] in ('replayed', 'ignored') for x in r['items'])")" == True && "$(jq_py run-2 "r['workloadDigest']")" == "$d1" ]]; echo $?)"

scenario_finish
