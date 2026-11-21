#!/usr/bin/env bash
# R24 (S36) who wins at the deadline: the decision or the expiry, never both. Drill stack with replicas:
#   bash scripts/stack-up.sh --drill --replicas
# N orders (default 10) wait for review (deadline 20 s, timer grace 2 s). Each gets an approval at a seeded
# offset between -1.5 s and +1.5 s around its round's DeadlineAt, timed against the SQL clock. Every signal
# raise is held 3 s (failpoint BeforeSignalRaise, Delay:3000), so the workflow's deadline timer and the
# delayed signal race as well.
#   R24-split        both outcomes occurred: some approvals before the deadline (200), some after (409
#                    review-expired, or review-superseded once round 2 was already open)
#   R24-never-both   per order, by SQL: a 200 means round 1 Approved with DecidedAt before DeadlineAt and no
#                    expiry; a 409 means round 1 Expired with ExpiredAt at or after DeadlineAt and no decision;
#                    never a round both decided and expired
#   R24-workflow     every workflow followed SQL: approved orders Completed (Delivered), expired ones went to
#                    round 2 and ended Rejected/ReviewExpired when it expired too
DRILL_ID=R24
. "$(dirname "$0")/lib.sh"
. "$REPO_ROOT/scripts/scenarios/m3-lib.sh"
. "$REPO_ROOT/scripts/scenarios/replica-lib.sh"
OUT_DIR="$DRILL_DIR"
drill_preflight
require_replicas
client_sim identity > "$DRILL_DIR/client-identity.json" || { log "REFUSED: the client simulator does not build"; exit 2; }
CLIENT_SIM_BUILT=1
trap 'drill_finish; exit $?' EXIT
N="${R24_ORDERS:-10}"
RUN="r24-$(date -u +%H%M%S)-$((RANDOM % 1000))"
arm_failpoint_times OrderDb BeforeSignalRaise '*' Delay:3000 "$N"
c="$(sim load load --run-id "$RUN" --seed 24 --orders "$N" --review-rate 1 --follow --follow-until review --follow-timeout 90 --url "$ORDER_API" --url "$ORDER_API_2" --output "$DRILL_DIR/load.result.json")"
python -c "import json,sys; print('\n'.join(i['orderId'] for i in json.load(open(sys.argv[1]))['items']))" "$DRILL_DIR/load.result.json" | tr -d '\r' > "$DRILL_DIR/orders.txt"
INJECTED_AT="$(now_iso)"
mkdir -p "$DRILL_DIR/decisions"

# SQL clock minus local clock (seconds), so the approvals are timed against DeadlineAt as SQL measures it.
skew="$(python -c "
import datetime, sys
sqlnow = datetime.datetime.fromisoformat(sys.argv[1].strip()).replace(tzinfo=datetime.timezone.utc)
print((sqlnow - datetime.datetime.now(datetime.timezone.utc)).total_seconds())" "$(sql OrderDb "SELECT CONVERT(varchar(27), SYSUTCDATETIME(), 126)")")"
echo "skew $skew" > "$DRILL_DIR/clock.txt"

approve_at() { # approve_at <i> <order id>
  local i=$1 o=$2 review offset
  review="$(sql OrderDb "SELECT ReviewId FROM orders.ReviewTask WHERE OrderId = '$o' AND Round = 1")"
  offset="$(python -c "import random; random.seed($i * 104729); print(f'{random.uniform(-1.5, 1.5):.2f}')")"
  # The SQL clock steps (up to about 2 s under load), so the wait is measured by SQL itself shortly before the
  # decision: first coarsely, then once more within the last two seconds.
  local remaining
  remaining="$(sql OrderDb "SELECT DATEDIFF(millisecond, SYSUTCDATETIME(), DeadlineAt) FROM orders.ReviewTask WHERE ReviewId = '$review'")"
  python -c "import time, sys; time.sleep(max(0, (int(sys.argv[1]) + float(sys.argv[2]) * 1000) / 1000 - 2))" "$remaining" "$offset"
  remaining="$(sql OrderDb "SELECT DATEDIFF(millisecond, SYSUTCDATETIME(), DeadlineAt) FROM orders.ReviewTask WHERE ReviewId = '$review'")"
  python -c "import time, sys; time.sleep(max(0, (int(sys.argv[1]) + float(sys.argv[2]) * 1000) / 1000))" "$remaining" "$offset"
  local base="$ORDER_API"; (( i % 2 == 0 )) && base="$ORDER_API_2"
  echo "$i $o $review $offset $(decide "$base" "$review" approve)" > "$DRILL_DIR/decisions/$i.txt"
}
i=0; while read -r -u 3 o; do i=$((i + 1)); approve_at "$i" "$o" & done 3< "$DRILL_DIR/orders.txt"
wait
cat "$DRILL_DIR"/decisions/*.txt | sort -n > "$DRILL_DIR/decisions.txt"
FAULT_OBSERVED="signal raises delayed: $(sql OrderDb "SELECT COUNT(*) FROM drill.Failpoint WHERE DrillRunId LIKE '${DRILL_RUN_ID}-%' AND FiredAt IS NOT NULL") fired"

ended() { [[ "$(sql OrderDb "SELECT COUNT(*) FROM orders.[Order] WHERE CustomerId LIKE '$RUN-c%' AND Status IN ('Completed', 'Rejected', 'Compensated', 'Cancelled')")" == "$N" ]]; }
wait_until 180 "all orders ended" ended
before=0; after=0; bad=(); wf_bad=()
while read -r -u 3 i o review offset status body; do
  row="$(sql OrderDb "SELECT CONCAT(Status, '|', CASE WHEN DecidedAt IS NULL THEN '-' WHEN DecidedAt < DeadlineAt THEN 'decided-before' ELSE 'decided-after' END, '|',
    CASE WHEN ExpiredAt IS NULL THEN '-' WHEN ExpiredAt >= DeadlineAt THEN 'expired-after' ELSE 'expired-before' END, '|', (SELECT COUNT(*) FROM orders.ReviewDecision WHERE ReviewId = '$review'))
    FROM orders.ReviewTask WHERE ReviewId = '$review'")"
  end="$(end_of "$o")"; wf="$(workflow_of "$o")"
  echo "$i offset $offset answer $status $(json_get "$body" code) round1 $row end $end workflow $wf" >> "$DRILL_DIR/outcomes.txt"
  if [[ "$status" == 200 && "$row" == "Approved|decided-before|-|1" ]]; then
    before=$((before + 1))
    [[ "$end" == Completed/Delivered && "$wf" == Completed/Completed/* ]] || wf_bad+=("$i: approved, end $end, workflow $wf")
  elif [[ "$status" == 409 && "$(json_get "$body" code)" =~ ^review-(expired|superseded)$ && "$row" == "Expired|-|expired-after|0" ]]; then
    after=$((after + 1))
    [[ "$end" == Rejected/ReviewExpired && "$wf" == Completed/Rejected/* ]] || wf_bad+=("$i: expired, end $end, workflow $wf")
  else
    bad+=("$i (offset $offset): $status $(json_get "$body" code) round1 $row")
  fi
done 3< "$DRILL_DIR/decisions.txt"
RECOVERED_AT="$(now_iso)"
aexpect R24-split "approvals both before (200) and after (409 review-expired or review-superseded) the deadline occurred" "before $before, after $after (of $N)" \
  "$([[ "$before" -ge 1 && "$after" -ge 1 ]]; echo $?)"
aexpect R24-never-both "every order exactly one of: approved before the deadline, or expired at/after it with no decision" \
  "consistent $((before + after))/$N; problems: ${bad[*]:-none}" "$([[ $((before + after)) == "$N" ]]; echo $?)"
aexpect R24-workflow "every workflow followed SQL" "mismatches: ${wf_bad[*]:-none}" "$([[ ${#wf_bad[@]} == 0 ]]; echo $?)"
