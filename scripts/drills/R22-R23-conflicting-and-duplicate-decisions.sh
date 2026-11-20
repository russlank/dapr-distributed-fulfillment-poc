#!/usr/bin/env bash
# R22 (S33) two operators can never both win; R23 decision part (S34) a duplicate decision is a no-op.
# Drill stack with both order API replicas: bash scripts/stack-up.sh --drill --replicas
# N orders (default 20) wait for review. For each, an approval through order-api and a rejection through
# order-api-2 (different decision ids and operators) are released at the same instant (a shared start
# time); all pairs run at once. Then every decision is sent again with its own id and payload.
#   R22-one-winner   per review exactly one 200 and one 409 review-already-decided; one decision row whose
#                    outcome is the winner's; each replica's request log shows its attempt
#   R22-workflow     every workflow followed SQL: Approved -> Completed (Delivered), Rejected ->
#                    Rejected/ReviewRejected; one decision per order
#   R23-replay       the winner's repeat: 200 replayed (same decision id); the loser's repeat: 409 again;
#                    still one decision per review and one pending signal per review
DRILL_ID=R22
. "$(dirname "$0")/lib.sh"
. "$REPO_ROOT/scripts/scenarios/m3-lib.sh"
. "$REPO_ROOT/scripts/scenarios/replica-lib.sh"
OUT_DIR="$DRILL_DIR"
drill_preflight
require_replicas
client_sim identity > "$DRILL_DIR/client-identity.json" || { log "REFUSED: the client simulator does not build"; exit 2; }
CLIENT_SIM_BUILT=1
trap 'drill_finish; exit $?' EXIT
N="${R22_REPETITIONS:-20}"
RUN="r22-$(date -u +%H%M%S)-$((RANDOM % 1000))"
SINCE="$(date -u -d '-2 seconds' +%Y-%m-%dT%H:%M:%SZ)"
c="$(sim load load --run-id "$RUN" --seed 22 --orders "$N" --review-rate 1 --follow --follow-until review --follow-timeout 90 --url "$ORDER_API" --url "$ORDER_API_2" --output "$DRILL_DIR/load.result.json")"
python -c "import json,sys; print('\n'.join(i['orderId'] for i in json.load(open(sys.argv[1]))['items']))" "$DRILL_DIR/load.result.json" | tr -d '\r' > "$DRILL_DIR/orders.txt"
pending_all() { [[ "$(sql OrderDb "SELECT COUNT(*) FROM orders.ReviewTask t JOIN orders.[Order] o ON o.OrderId = t.OrderId WHERE o.CustomerId LIKE '$RUN-c%' AND t.Status = 'Pending'")" == "$N" ]]; }
wait_until 60 "all $N rounds pending" pending_all
mkdir -p "$DRILL_DIR/pairs"

START="$(python -c 'import time; print(f"{time.time() + 3:.3f}")')"
INJECTED_AT="$(now_iso)"
pair() { # pair <i> <order id>: approve on order-api and reject on order-api-2 at $START
  local i=$1 o=$2 review a r
  review="$(sql OrderDb "SELECT ReviewId FROM orders.ReviewTask WHERE OrderId = '$o' AND Round = 1")"
  a="$(new_uuid)"; r="$(new_uuid)"
  (python -c "import time; time.sleep(max(0, $START - time.time()))"; decide "$ORDER_API" "$review" approve "$a" operator-1 > "$DRILL_DIR/pairs/$i.approve") &
  (python -c "import time; time.sleep(max(0, $START - time.time()))"; decide "$ORDER_API_2" "$review" reject "$r" operator-2 > "$DRILL_DIR/pairs/$i.reject") &
  wait
  echo "$i $o $review $a $r" > "$DRILL_DIR/pairs/$i.ids"
}
i=0; while read -r -u 3 o; do i=$((i + 1)); pair "$i" "$o" & done 3< "$DRILL_DIR/orders.txt"
wait
FAULT_OBSERVED="$N simultaneous approve/reject pairs released at $START"

one=0; problems=(); declare -A WINNER
logs="$(docker compose --profile replicas logs --no-log-prefix --since "$SINCE" order-api order-api-2 2>/dev/null | grep -c 'decision - \(200\|409\)')"
for f in "$DRILL_DIR"/pairs/*.ids; do
  read -r i o review a r < "$f"
  read -r as abody < "$DRILL_DIR/pairs/$i.approve"; read -r rs rbody < "$DRILL_DIR/pairs/$i.reject"
  row="$(sql OrderDb "SELECT CONCAT(COUNT(*), '|', MAX(Outcome), '|', LOWER(CONVERT(varchar(36), MAX(DecisionId)))) FROM orders.ReviewDecision WHERE ReviewId = '$review'")"
  if [[ "$as" == 200 && "$rs" == 409 && "$(json_get "$rbody" code)" == review-already-decided && "$row" == 1\|Approved\|"$a" ]]; then
    one=$((one + 1)); WINNER[$i]="approve $a reject $r Approved"
  elif [[ "$rs" == 200 && "$as" == 409 && "$(json_get "$abody" code)" == review-already-decided && "$row" == 1\|Rejected\|"$r" ]]; then
    one=$((one + 1)); WINNER[$i]="reject $r approve $a Rejected"
  else
    problems+=("$i: approve $as $(json_get "$abody" code), reject $rs $(json_get "$rbody" code), decisions $row")
  fi
done
approvals=0; for i in "${!WINNER[@]}"; do [[ "${WINNER[$i]}" == approve* ]] && approvals=$((approvals + 1)); done
aexpect R22-one-winner "per review one 200 and one 409 review-already-decided; one decision with the winner's id; request log lines >= 2N" \
  "one winner $one/$N (approvals won $approvals, rejections won $((one - approvals))); decision request log lines $logs; problems: ${problems[*]:-none}" \
  "$([[ "$one" == "$N" && "$logs" -ge $((2 * N)) ]]; echo $?)"

ended() { [[ "$(sql OrderDb "SELECT COUNT(*) FROM orders.[Order] WHERE CustomerId LIKE '$RUN-c%' AND Status IN ('Completed', 'Rejected', 'Compensated', 'Cancelled')")" == "$N" ]]; }
wait_until 240 "all orders ended" ended
wf_ok=0; wf_bad=()
for f in "$DRILL_DIR"/pairs/*.ids; do
  read -r i o review a r < "$f"
  want="${WINNER[$i]##* }"; end="$(end_of "$o")"; wf="$(workflow_of "$o")"
  if [[ ( "$want" == Approved && "$end" == Completed/Delivered && "$wf" == Completed/Completed/* ) || ( "$want" == Rejected && "$end" == Rejected/ReviewRejected && "$wf" == Completed/Rejected/* ) ]]; then
    wf_ok=$((wf_ok + 1))
  else
    wf_bad+=("$i: SQL decision $want, end $end, workflow $wf")
  fi
done
aexpect R22-workflow "every workflow followed the SQL decision" "$wf_ok/$N; problems: ${wf_bad[*]:-none}" "$([[ "$wf_ok" == "$N" ]]; echo $?)"

replay_ok=0; replay_bad=()
for f in "$DRILL_DIR"/pairs/*.ids; do
  read -r i o review a r < "$f"
  read -r wkind wid lkind lid want <<< "${WINNER[$i]}"
  wbase="$ORDER_API"; lbase="$ORDER_API_2"; wop=operator-1; lop=operator-2
  [[ "$wkind" == reject ]] && { wbase="$ORDER_API_2"; lbase="$ORDER_API"; wop=operator-2; lop=operator-1; }
  read -r ws wbody <<< "$(decide "$wbase" "$review" "$wkind" "$wid" "$wop")"
  read -r ls lbody <<< "$(decide "$lbase" "$review" "$lkind" "$lid" "$lop")"
  counts="$(sql OrderDb "SELECT CONCAT((SELECT COUNT(*) FROM orders.ReviewDecision WHERE ReviewId = '$review'), '|', (SELECT COUNT(*) FROM orders.PendingSignal WHERE CausedBy IN (SELECT DecisionId FROM orders.ReviewDecision WHERE ReviewId = '$review')))")"
  if [[ "$ws" == 200 && "$(json_get "$wbody" replayed)" == True && "$ls" == 409 && "$counts" == 1\|1 ]]; then replay_ok=$((replay_ok + 1)); else replay_bad+=("$i: winner $ws replayed=$(json_get "$wbody" replayed), loser $ls, decisions|signals $counts"); fi
done
RECOVERED_AT="$(now_iso)"
aexpect R23-replay "winner repeat 200 replayed; loser repeat 409; one decision and one pending signal per review" "$replay_ok/$N; problems: ${replay_bad[*]:-none}" \
  "$([[ "$replay_ok" == "$N" ]]; echo $?)"
