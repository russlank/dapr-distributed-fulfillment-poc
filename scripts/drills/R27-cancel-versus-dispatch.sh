#!/usr/bin/env bash
# R27 (S39) cancellation and the start of dispatch never both win (I15). Drill stack:
#   bash scripts/stack-up.sh --drill --replicas
# N orders (default 20) run at once. The failpoint BeforeBeginDispatchCommit (Delay:2000) holds every
# BeginDispatch transaction for 2 s after it locked the order. For each order a customer cancels at a
# seeded offset (0.2-4 s) after the submission, through the other order API replica: some cancellations
# come before BeginDispatch (even before the reservation or the authorization), some while it holds the
# lock, some after it committed.
#   R27-hit          the failpoint fired for the orders whose dispatch started; both outcomes occurred
#                    (at least one cancellation won and at least one dispatch won)
#   R27-i15          for every order: never both CancelRequestedAt and DispatchStartedAt; the answer
#                    matches the end: 202 -> Cancelled (CancellationRequested), no shipment, no capture,
#                    an authorization (if any) voided, holds (if any) released; 409 dispatch-started -> Completed (Delivered),
#                    one shipment, one capture, no void
#   R27-workflow     every workflow ended in agreement with SQL (Cancelled or Completed)
DRILL_ID=R27
. "$(dirname "$0")/lib.sh"
. "$REPO_ROOT/scripts/scenarios/m3-lib.sh"
. "$REPO_ROOT/scripts/scenarios/replica-lib.sh"
drill_preflight
require_replicas
trap 'drill_finish; exit $?' EXIT
N="${R27_REPETITIONS:-20}"
RUN="r27-$(date -u +%H%M%S)-$((RANDOM % 1000))"
SKU="$(fresh_sku R27 $((N + 5)))"
arm_failpoint_times OrderDb BeforeBeginDispatchCommit '*' Delay:2000 "$N"
INJECTED_AT="$(now_iso)"
mkdir -p "$DRILL_DIR/reps"

repetition() { # repetition <i>: submit, wait for Packing, cancel at a seeded offset; writes reps/<i>.txt
  local i=$1 base other o offset answer
  base="$ORDER_API"; other="$ORDER_API_2"; (( i % 2 == 0 )) && { base="$ORDER_API_2"; other="$ORDER_API"; }
  o="$(submit "$base" "$RUN-$i" "$(order_body "$RUN-c$i" "$SKU" 1)")"
  offset="$(python -c "import random; random.seed($i * 7919); print(f'{random.uniform(0.2, 4):.2f}')")"
  sleep "$offset"
  answer="$(cancel_order "$other" "$o")"
  echo "$i $o $offset $answer" > "$DRILL_DIR/reps/$i.txt"
}
for i in $(seq 1 "$N"); do repetition "$i" & done
wait
cat "$DRILL_DIR"/reps/*.txt | sort -n > "$DRILL_DIR/repetitions.txt"

ended() { [[ "$(sql OrderDb "SELECT COUNT(*) FROM orders.[Order] WHERE CustomerId LIKE '$RUN-c%' AND Status IN ('Cancelled', 'Completed', 'Compensated', 'Rejected')")" == "$N" ]]; }
wait_until 240 "all $N orders ended" ended
fired="$(sql OrderDb "SELECT COUNT(*) FROM drill.Failpoint WHERE DrillRunId LIKE '${DRILL_RUN_ID}-%' AND FiredAt IS NOT NULL")"
FAULT_OBSERVED="failpoint fired $fired times"

won_cancel=0; won_dispatch=0; bad=(); wf_bad=()
while read -r -u 3 i o offset status code; do
  row="$(sql OrderDb "SELECT CONCAT(CASE WHEN CancelRequestedAt IS NULL THEN 0 ELSE 1 END, CASE WHEN DispatchStartedAt IS NULL THEN 0 ELSE 1 END, '|', Status) FROM orders.[Order] WHERE OrderId = '$o'")"
  end="$(end_of "$o")"; ship="$(shipment_of "$o")"; money="$(provider_money "$o")"; holds="$(holds_of "$o")"
  echo "$i $o offset $offset answer $status $code | flags|status $row | end $end | shipment ${ship:--} | a|c|v|r $money | holds $holds" >> "$DRILL_DIR/outcomes.txt"
  if [[ "$status" == 202 && "$row" == 10\|Cancelled && "$end" == Cancelled/CancellationRequested && -z "$ship" && "$money" =~ ^(1\|0\|1\|0|0\|0\|0\|0)$ && "$holds" =~ ^(Released)?$ ]]; then
    won_cancel=$((won_cancel + 1))
  elif [[ "$status" == 409 && "$code" == dispatch-started && "$row" == 01\|Completed && "$end" == Completed/Delivered && "$ship" == *\|Delivered && "$money" == 1\|1\|0\|0 ]]; then
    won_dispatch=$((won_dispatch + 1))
  else
    bad+=("$i: $status $code $row $end ship=${ship:--} money=$money holds=$holds")
  fi
  wf="$(workflow_of "$o")"
  [[ "$wf" == Completed/Cancelled/* && "$end" == Cancelled/* ]] || [[ "$wf" == Completed/Completed/* && "$end" == Completed/* ]] || wf_bad+=("$i: $wf vs $end")
done 3< "$DRILL_DIR/repetitions.txt"
RECOVERED_AT="$(now_iso)"

aexpect R27-hit "the failpoint held BeginDispatch; both outcomes occurred" "fired $fired; cancellation won $won_cancel, dispatch won $won_dispatch (of $N)" \
  "$([[ "$fired" -ge 1 && "$won_cancel" -ge 1 && "$won_dispatch" -ge 1 ]]; echo $?)"
aexpect R27-i15 "every order: one winner, and the answer matches the end (see outcomes.txt)" "consistent $((won_cancel + won_dispatch))/$N; problems: ${bad[*]:-none}" \
  "$([[ $((won_cancel + won_dispatch)) == "$N" ]]; echo $?)"
aexpect R27-workflow "every workflow Completed with the outcome SQL holds" "mismatches: ${wf_bad[*]:-none}" "$([[ ${#wf_bad[@]} == 0 ]]; echo $?)"
