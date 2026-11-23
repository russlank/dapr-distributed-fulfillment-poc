#!/usr/bin/env bash
# R28 variant A (S46) Scheduler and Placement outages: timers fire late (or are nudged) and reviews still expire by
# SQL time; actor calls resume after Placement returns; nothing is applied twice. Variant B (an emptied Scheduler
# volume) is deferred.
# Drill stack: bash scripts/stack-up.sh --drill --replicas (review deadline 20 s, 2 rounds)
#   S  two orders wait for a review nobody decides; the Scheduler is stopped while their round-1 deadlines pass and
#      stays down past both deadlines (at least 45 s, until OrderProgressStalled fired), then starts again
#   P  the Placement service is stopped during a burst of three orders (stock actors and workflow actors need it) for
#      30 s, then starts again
#   R28-S-outage     the Scheduler exited; both round-1 deadlines passed during the outage (DeadlineAt < outage end)
#                    while the rounds were still Pending (no timer fired)
#   R28-S-expired    after the restart each round expired at or after its deadline (never before), the second round
#                    too; both orders Rejected/ReviewExpired, no decision, nothing reserved or charged; timer lag of
#                    round 1 (expiry minus deadline) is reported
#   R28-P-outage     Placement exited during the burst; actor calls failed meanwhile (inventory or workflow logs)
#   R28-P-resumed    after the restart all three orders Completed/Delivered, one capture and holds Consumed each; stock
#                    invariants of the SKU hold
#   R28-alert        OrderProgressStalled fired while the reviews were overdue and resolved after the restart
DRILL_ID=R28
. "$(dirname "$0")/lib.sh"
. "$REPO_ROOT/scripts/scenarios/m3-lib.sh"
drill_preflight
control_up() { docker compose up -d --no-build --no-deps scheduler placement >/dev/null 2>&1; }
on_cleanup "control_up"
trap 'run_cleanup; drill_finish; exit $?' EXIT
RUN="r28-$(date -u +%H%M%S)-$((RANDOM % 1000))"
state_of_c() { docker compose ps -a --format '{{.State}}' "$1" 2>/dev/null; }
rounds_of() { sqln OrderDb "SELECT STRING_AGG(CONCAT(Round, ':', Status), ',') WITHIN GROUP (ORDER BY Round) FROM orders.ReviewTask WHERE OrderId = '$1'"; }
waiting() { [[ -n "$(sql OrderDb "SELECT ReviewId FROM orders.ReviewTask WHERE OrderId = '$1' AND Round = 1 AND Status = 'Pending'")" ]]; }

# --- S: Scheduler down while review deadlines pass ----------------------------------------------------------------
T1="$(submit "$ORDER_API" "$RUN-t1" "$(review_order_body "$RUN-ct1")")"
T2="$(submit "$ORDER_API" "$RUN-t2" "$(review_order_body "$RUN-ct2")")"
for t in "$T1" "$T2"; do wait_until 60 "order $t waits for its review" waiting "$t"; done
ALERT_SINCE="$(date +%s)"; PS_BEFORE="$(alert_firing OrderProgressStalled)"
docker compose stop -t 5 scheduler >/dev/null 2>&1
INJECTED_AT="$(now_iso)"
s_state="$(state_of_c scheduler)"
outage_start=$SECONDS
sleep 45
alert_wait_firing OrderProgressStalled '' 60 || true
outage_end="$(sql OrderDb "SELECT CONVERT(varchar(23), SYSUTCDATETIME(), 126)")"
during="$(for t in "$T1" "$T2"; do printf '%s ' "$(rounds_of "$t")"; done)"
passed="$(sql OrderDb "SELECT COUNT(*) FROM orders.ReviewTask WHERE OrderId IN ('$T1', '$T2') AND Round = 1 AND DeadlineAt < '$outage_end'")"
aexpect R28-S-outage "Scheduler exited; both round-1 deadlines passed during the outage while the rounds stayed Pending" \
  "scheduler $s_state for $((SECONDS - outage_start)) s; deadlines passed $passed; rounds during the outage $during" \
  "$([[ "$s_state" == exited && "$passed" == 2 && "$during" == "1:Pending 1:Pending " ]]; echo $?)"
docker compose start scheduler >/dev/null 2>&1

# --- P: Placement down during a burst (runs while the reviews finish their rounds) --------------------------------
SKU="$(fresh_sku R28 20)"
docker compose stop -t 5 placement >/dev/null 2>&1
p_state="$(state_of_c placement)"
P=(); for i in 1 2 3; do P+=("$(submit "$ORDER_API" "$RUN-p$i" "$(order_body "$RUN-cp$i" "$SKU" 1)")"); done
printf 'T %s\nT %s\n' "$T1" "$T2" > "$DRILL_DIR/orders.txt"; printf 'P %s\n' "${P[@]}" >> "$DRILL_DIR/orders.txt"
sleep 30
p_during="$(for o in "${P[@]}"; do printf '%s ' "$(order_status "$o")"; done)"
actor_errors="$(docker compose --profile replicas logs --since 2m inventory-service-dapr inventory-service-2-dapr workflow-host-dapr workflow-host-2-dapr 2>/dev/null | grep -ciE 'placement|actor')"
FAULT_OBSERVED="scheduler $s_state ${outage_end:+until $outage_end}; placement $p_state; P during $p_during"
aexpect R28-P-outage "Placement exited during the burst; actor errors in the sidecar logs" \
  "placement $p_state; orders after 30 s $p_during; sidecar placement/actor lines $actor_errors" \
  "$([[ "$p_state" == exited && "$actor_errors" -gt 0 ]]; echo $?)"
docker compose start placement >/dev/null 2>&1

ends=""; bad=0
for o in "${P[@]}"; do
  wait_status 240 "$o" 'Completed|Compensated|Rejected|ManualInterventionRequired' || true
  e="$(end_of "$o")"; m="$(provider_money "$o")"; h="$(holds_of "$o")"; ends+="$e $m $h; "
  [[ "$e" == Completed/Delivered && "$m" == "1|1|0|0" && "$h" =~ ^Consumed(,Consumed)*$ ]] || bad=1
done
inv="$(sql InventoryDb "SELECT CONCAT(OnHand, '/', Reserved) FROM inventory.StockItem WHERE Sku = '$SKU' AND WarehouseId = 'W1'")"
aexpect R28-P-resumed "after the restart all three Completed/Delivered, 1|1|0|0 and holds Consumed each; stock 17/0" "$ends stock $inv" \
  "$([[ $bad == 0 && "$inv" == 17/0 ]]; echo $?)"

exp=""; bad=0
for t in "$T1" "$T2"; do
  wait_status 240 "$t" 'Rejected|Completed|Compensated|ManualInterventionRequired' || true
  r="$(sqln OrderDb "SELECT STRING_AGG(CONCAT(Round, ':', Status, ':', CASE WHEN ExpiredAt >= DeadlineAt THEN 'after' ELSE 'early' END, ':', DATEDIFF(millisecond, DeadlineAt, ExpiredAt)), ',') WITHIN GROUP (ORDER BY Round) FROM orders.ReviewTask WHERE OrderId = '$t'")"
  e="$(end_of "$t")"; d="$(sql OrderDb "SELECT COUNT(*) FROM orders.ReviewDecision d JOIN orders.ReviewTask r ON r.ReviewId = d.ReviewId WHERE r.OrderId = '$t'")"
  m="$(provider_money "$t")"; h="$(holds_of "$t")"
  exp+="$e rounds $r decisions $d money $m holds '${h}'; "
  [[ "$e" == Rejected/ReviewExpired && "$r" =~ ^1:Expired:after:[0-9]+,2:Expired:after:[0-9]+$ && "$d" == 0 && "$m" == "0|0|0|0" && -z "$h" ]] || bad=1
done
RECOVERED_AT="$(now_iso)"
aexpect R28-S-expired "each round Expired at or after its deadline (lag in ms), both Rejected/ReviewExpired; no decision, nothing reserved or charged" "$exp" "$bad"

read -r verdict detail <<< "$(alert_outcome OrderProgressStalled '' "$ALERT_SINCE" "$PS_BEFORE" 150)"
assert R28-alert "$verdict" "OrderProgressStalled fires while the reviews are overdue and resolves after the restart" "$detail"
