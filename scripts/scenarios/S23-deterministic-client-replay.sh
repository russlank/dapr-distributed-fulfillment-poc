#!/usr/bin/env bash
# S23 deterministic client replay. Runs on a release or drill stack, with both order API replicas
# when they run (bash scripts/stack-up.sh --replicas), otherwise with one.
# The client simulator (external, HTTP only) generates a seeded workload; the same run id and seed
# must reproduce the same logical requests and keys, a replay must return the original orders
# without new business effects, and the results must report acceptance versus completion.
# PASS when, from the simulator's results and OrderDb rows:
#   S23-plan      two dry runs of the same run id and seed give the same workload digest, keys, and
#                 payload hashes; another seed keeps the keys and changes the payloads
#   S23-new       a new run: every order created (201), spread over the replicas, every order
#                 followed to Completed; the result reports accepted and completed separately
#   S23-replay    a replay from the recorded result (other replica assignment): same digest, every
#                 request 200 replayed with its original order id, every order still Completed
#   S23-reused    a new run under the used run id is refused by the simulator (exit 3), nothing created
#   S23-effects   OrderDb: one order, one idempotency key, one start request (Closed) per logical
#                 request; history Submitted through AwaitingDelivery, Completed once; one stub effect
#                 per line plus one completion effect, no extra rows after the replay
#   S23-operator  orders that need a review: followed until they wait, approved through the operator
#                 API, the approval repeated (replayed, one decision each), replayed and Completed;
#                 inspect shows business view, reviews, and execution diagnostics; no key in any output
# Evidence in artifacts/scenarios/S23-<time>/ (every simulator result as JSON).
. "$(dirname "$0")/../lib.sh"

RUN_ID="S23-$(date -u +%Y%m%dT%H%M%SZ)"
OUT_DIR="$REPO_ROOT/artifacts/scenarios/$RUN_ID"
mkdir -p "$OUT_DIR"
RESULTS_FILE="$OUT_DIR/results.jsonl"
N="${S23_ORDERS:-12}"
SEED="${S23_SEED:-23}"
CLIENT_RUN="s23-$(date -u +%Y%m%d-%H%M%S)-$((RANDOM % 1000))"
REVIEW_RUN="${CLIENT_RUN}-r"
failures=0
check() { [[ "$2" == PASS ]] || failures=$((failures + 1)); record "$@"; }

URLS=(--url "$ORDER_API")
if [[ "$(docker compose --profile replicas ps --format '{{.State}}' order-api-2 2>/dev/null)" == running ]] && ready order-api-2; then
  URLS+=(--url "$ORDER_API_2")
fi
ready order-api || { log "REFUSED: order-api is not ready (start the stack first)"; exit 2; }
log "S23 $RUN_ID: client run $CLIENT_RUN, seed $SEED, $N orders, $(( ${#URLS[@]} / 2 )) replica(s); evidence in $OUT_DIR"
client_sim identity > "$OUT_DIR/identity.json" || { log "REFUSED: the client simulator does not build"; exit 2; }
CLIENT_SIM_BUILT=1   # built once here; the calls below run in subshells

# sim <name> <args...>: runs the simulator, keeps stdout as <name>.json and stderr as <name>.log; prints the exit code.
sim() {
  local name=$1; shift
  client_sim "$@" > "$OUT_DIR/$name.json" 2> "$OUT_DIR/$name.log"
  echo $?
}
field() { python -c "
import json, sys
v = json.load(open(sys.argv[1]))
for k in sys.argv[2].split('.'):
    v = v[int(k)] if isinstance(v, list) else v.get(k)
print('' if v is None else (json.dumps(v, sort_keys=True) if isinstance(v, (dict, list)) else v))" "$OUT_DIR/$1.json" "$2"; }
# pairs <name> <fields...>: one line per item, the named fields joined by '|'.
pairs() { local name=$1; shift; python -c "
import json, sys
items = json.load(open(sys.argv[1]))['items']
print('\n'.join('|'.join(str(i.get(f)) for f in sys.argv[2:]) for i in items))" "$OUT_DIR/$name.json" "$@"; }

# S23-plan ----------------------------------------------------------------------------------------
c1="$(sim plan-1 load --run-id "$CLIENT_RUN" --seed "$SEED" --orders "$N" --dry-run "${URLS[@]}")"
c2="$(sim plan-2 load --run-id "$CLIENT_RUN" --seed "$SEED" --orders "$N" --dry-run "${URLS[@]}")"
c3="$(sim plan-other-seed load --run-id "$CLIENT_RUN" --seed "$((SEED + 1))" --orders "$N" --dry-run "${URLS[@]}")"
d1="$(field plan-1 workloadDigest)"; d2="$(field plan-2 workloadDigest)"; d3="$(field plan-other-seed workloadDigest)"
same_requests=no; [[ "$(pairs plan-1 idempotencyKey payloadSha256)" == "$(pairs plan-2 idempotencyKey payloadSha256)" ]] && same_requests=yes
same_keys=no; [[ "$(pairs plan-1 idempotencyKey)" == "$(pairs plan-other-seed idempotencyKey)" ]] && same_keys=yes
observed="exits $c1/$c2/$c3; digest $d1 twice: $([[ "$d1" == "$d2" ]] && echo equal || echo DIFFERENT); keys+payloads equal: $same_requests; other seed: digest $d3, keys equal: $same_keys"
if [[ "$c1$c2$c3" == 000 && -n "$d1" && "$d1" == "$d2" && "$same_requests" == yes && "$d3" != "$d1" && "$same_keys" == yes ]]; then
  check S23-plan PASS "same run id and seed: equal digest, keys, payload hashes; other seed: same keys, other payloads" "$observed"
else
  check S23-plan FAIL "equal plans for equal inputs" "$observed"
fi

# S23-new -----------------------------------------------------------------------------------------
c="$(sim run-new load --run-id "$CLIENT_RUN" --seed "$SEED" --orders "$N" --concurrency 4 --follow --follow-timeout 120 "${URLS[@]}" --output "$OUT_DIR/run-new.result.json")"
observed="exit $c; digest $(field run-new workloadDigest); accepted $(field run-new summary.accepted), created $(field run-new summary.created), completed $(field run-new summary.completed)/$(field run-new summary.followed); by endpoint $(field run-new summary.byEndpoint); ambiguous attempts $(field run-new summary.ambiguousAttempts)"
replicas_used="$(python -c "import json,sys; print(len(json.loads(sys.argv[1])))" "$(field run-new summary.byEndpoint)")"
expected_replicas=$(( ${#URLS[@]} / 2 ))
if [[ "$c" == 0 && "$(field run-new workloadDigest)" == "$d1" && "$(field run-new summary.created)" == "$N" && "$(field run-new summary.completed)" == "$N" && "$replicas_used" == "$expected_replicas" ]]; then
  check S23-new PASS "$N created on $expected_replicas replica(s), all Completed; accepted and completed reported separately" "$observed"
else
  check S23-new FAIL "exit 0, $N created, $N Completed, $expected_replicas replica(s)" "$observed"
fi
effects_before="$(sql OrderDb "SELECT CONCAT((SELECT COUNT(*) FROM orders.StubEffect e JOIN orders.[Order] o ON o.OrderId = e.OrderId WHERE o.CustomerId LIKE '$CLIENT_RUN-c%'), ':',
  (SELECT ISNULL(SUM(e.Replays), 0) FROM orders.StubEffect e JOIN orders.[Order] o ON o.OrderId = e.OrderId WHERE o.CustomerId LIKE '$CLIENT_RUN-c%'))")"

# S23-replay --------------------------------------------------------------------------------------
c="$(sim run-replay load --replay-of "$OUT_DIR/run-new.result.json" --distribution seeded --concurrency 4 --follow --follow-timeout 60 "${URLS[@]}")"
same_orders=no; [[ "$(pairs run-new idempotencyKey payloadSha256 orderId)" == "$(pairs run-replay idempotencyKey payloadSha256 orderId)" ]] && same_orders=yes
observed="exit $c; mode $(field run-replay mode); digest $([[ "$(field run-replay workloadDigest)" == "$d1" ]] && echo equal || echo DIFFERENT); replayed $(field run-replay summary.replayed), created $(field run-replay summary.created); same key, payload, order id per request: $same_orders; completed $(field run-replay summary.completed); by endpoint $(field run-replay summary.byEndpoint)"
if [[ "$c" == 0 && "$(field run-replay mode)" == replay && "$(field run-replay workloadDigest)" == "$d1" && "$(field run-replay summary.replayed)" == "$N" && "$same_orders" == yes && "$(field run-replay summary.completed)" == "$N" ]]; then
  check S23-replay PASS "same digest; every request replayed with its original order; all Completed" "$observed"
else
  check S23-replay FAIL "exit 0, $N replayed with the original order ids" "$observed"
fi

# S23-reused --------------------------------------------------------------------------------------
c="$(sim run-reused load --run-id "$CLIENT_RUN" --seed "$SEED" --orders "$N" "${URLS[@]}")"
observed="exit $c ($(field run-reused outcome)); created $(field run-reused summary.created), replayed $(field run-reused summary.replayed); first problem: $(python -c "import json,sys; p=json.load(open(sys.argv[1]))['problems']; print(p[0] if p else '-')" "$OUT_DIR/run-reused.json")"
if [[ "$c" == 3 && "$(field run-reused summary.created)" == 0 && "$(field run-reused summary.replayed)" == "$N" ]]; then
  check S23-reused PASS "new run under a used run id: exit 3 (run-identity-mismatch), nothing created" "$observed"
else
  check S23-reused FAIL "exit 3, 0 created" "$observed"
fi

# S23-effects -------------------------------------------------------------------------------------
run_orders="SELECT OrderId FROM orders.[Order] WHERE CustomerId LIKE '$CLIENT_RUN-c%'"
row="$(sql OrderDb "SELECT CONCAT(
  (SELECT COUNT(*) FROM orders.[Order] WHERE OrderId IN ($run_orders)), '|',
  (SELECT COUNT(*) FROM orders.IdempotencyKey WHERE [Key] LIKE '$CLIENT_RUN-o%'), '|',
  (SELECT COUNT(*) FROM orders.WorkflowStartRequest WHERE OrderId IN ($run_orders) AND State = 'Closed'), '|',
  (SELECT COUNT(*) FROM (SELECT h.OrderId FROM orders.OrderStatusHistory h WHERE h.OrderId IN ($run_orders) GROUP BY h.OrderId
     HAVING STRING_AGG(h.ToStatus, ',') WITHIN GROUP (ORDER BY h.Sequence) = 'Submitted,Validating,Reserving,PaymentAuthorized,Packing,Dispatched,AwaitingDelivery,Completed') x), '|',
  (SELECT COUNT(*) FROM orders.[Order] o WHERE o.OrderId IN ($run_orders)
     AND (SELECT COUNT(*) FROM orders.StubEffect e WHERE e.OrderId = o.OrderId) = (SELECT COUNT(*) FROM orders.OrderLine l WHERE l.OrderId = o.OrderId) + 1
     AND EXISTS (SELECT 1 FROM orders.OrderOutbox x WHERE x.OrderId = o.OrderId AND x.EventType = 'OrderCompleted')), '|',
  (SELECT COUNT(*) FROM orders.StubEffect e WHERE e.OrderId IN ($run_orders)), ':',
  (SELECT ISNULL(SUM(e.Replays), 0) FROM orders.StubEffect e WHERE e.OrderId IN ($run_orders)))")"
IFS='|' read -r orders keys closed history effects_ok effects_after <<< "$row"
observed="orders=$orders keys=$keys start-requests-closed=$closed history-ok=$history effects-per-line-ok=$effects_ok effect rows:replays before replay $effects_before, after $effects_after"
if [[ "$orders" == "$N" && "$keys" == "$N" && "$closed" == "$N" && "$history" == "$N" && "$effects_ok" == "$N" && "$effects_after" == "$effects_before" ]]; then
  check S23-effects PASS "one order, key, closed start request per request; one Completed transition; one effect per line + completion; unchanged by the replay" "$observed"
else
  check S23-effects FAIL "$N each; effect rows unchanged" "$observed"
fi

# S23-operator ------------------------------------------------------------------------------------
cw="$(sim review-new load --run-id "$REVIEW_RUN" --seed "$SEED" --orders 2 --review-rate 1 --follow --follow-until review --follow-timeout 90 "${URLS[@]}" --output "$OUT_DIR/review-new.result.json")"
ca="$(sim approve-1 approve --from "$OUT_DIR/review-new.result.json" --wait 30 --operator operator-2 "${URLS[@]}")"
cb="$(sim approve-2 approve --from "$OUT_DIR/review-new.result.json" --operator operator-2 "${URLS[@]}")"
cr="$(sim review-replay load --replay-of "$OUT_DIR/review-new.result.json" --follow --follow-timeout 90 "${URLS[@]}")"
ci="$(sim inspect inspect --from "$OUT_DIR/review-new.result.json" "${URLS[@]}")"
decisions="$(sql OrderDb "SELECT CONCAT(COUNT(*), ':', COUNT(DISTINCT d.ReviewId), ':', MAX(d.OperatorId)) FROM orders.ReviewDecision d JOIN orders.ReviewTask r ON r.ReviewId = d.ReviewId JOIN orders.[Order] o ON o.OrderId = r.OrderId WHERE o.CustomerId LIKE '$REVIEW_RUN-c%'")"
second_results="$(python -c "import json,sys; print(','.join(i['result'] for i in json.load(open(sys.argv[1]))['items']))" "$OUT_DIR/approve-2.json")"
leaks=0
for f in "$OUT_DIR"/*.json "$OUT_DIR"/*.log; do
  for secret in "$CUSTOMER_API_KEY" "$OPERATOR_API_KEY" "$ADMIN_API_KEY"; do
    [[ -n "$secret" ]] && grep -qF -- "$secret" "$f" && leaks=$((leaks + 1))
  done
done
observed="exits load/approve/approve-again/replay/inspect $cw/$ca/$cb/$cr/$ci; waiting $(field review-new summary.finalStatuses); approve $(field approve-1 summary.accepted) accepted; again: $second_results; decisions (rows:reviews:operator) $decisions; after replay $(field review-replay summary.finalStatuses); inspect execution shown $(field inspect summary.executionShown)/2, reviews $(field inspect summary.reviewsShown)/2; key occurrences in outputs $leaks"
if [[ "$cw$ca$cb$cr$ci" == 00000 && "$(field review-new summary.finalStatuses)" == '{"AwaitingApproval": 2}' && "$second_results" == replayed,replayed \
      && "$decisions" == "2:2:operator-2" && "$(field review-replay summary.completed)" == 2 && "$(field inspect summary.executionShown)" == 2 && "$leaks" == 0 ]]; then
  check S23-operator PASS "review orders wait, are approved once through the operator API (repeat = replay), then Completed; inspect complete; no key in any output" "$observed"
else
  check S23-operator FAIL "all exit 0; 2 decisions; repeat replayed; 2 Completed; no key leaked" "$observed"
fi

log "S23 $RUN_ID: $([[ $failures == 0 ]] && echo PASS || echo "FAIL ($failures)") (results: $RESULTS_FILE)"
exit $(( failures == 0 ? 0 : 1 ))
