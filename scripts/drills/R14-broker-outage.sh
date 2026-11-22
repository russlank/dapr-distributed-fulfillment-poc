#!/usr/bin/env bash
# R14 (S11) a broker outage loses no event: business commits continue, the outboxes hold the backlog, and it drains
# after the broker returns; published and history event sets converge.
# Drill stack: bash scripts/stack-up.sh --drill --replicas
#   R14-down        RabbitMQ stopped; orders submitted during the outage complete (commits do not depend on it)
#   R14-backlog     while it is down, outbox rows of those orders stay Pending with failed attempts (backlog > 0)
#   R14-drained     after the restart every outbox row of the run is Published (no row lost, none deleted)
#   R14-converged   per order and source the published event ids equal the history's (I20), all Applied, state at
#                   the last sequence; a rebuild gives the live checksum
#   R14-alert       OutboxAging (source orders) fired during the outage and resolved after the drain (OutboxStalled:
#                   R15; during an outage each row gets an attempt only about every batch x publish timeout)
DRILL_ID=R14
. "$(dirname "$0")/lib.sh"
. "$REPO_ROOT/scripts/scenarios/m3-lib.sh"
. "$REPO_ROOT/scripts/scenarios/m4-lib.sh"
drill_preflight
broker_up() { [[ "$(docker compose ps --format '{{.State}}' rabbitmq 2>/dev/null)" == running ]] || docker compose up -d --no-deps rabbitmq >/dev/null 2>&1; }
on_cleanup "broker_up"
trap 'run_cleanup; drill_finish; exit $?' EXIT
RUN="r14-$(date -u +%H%M%S)-$((RANDOM % 1000))"
SKU="$(fresh_sku R14 20)"

ALERT_SINCE="$(date +%s)"; AGING_BEFORE="$(alert_firing OutboxAging 'source="orders"')"
docker compose stop -t 5 rabbitmq >/dev/null 2>&1
INJECTED_AT="$(now_iso)"
state="$(docker compose ps -a --format '{{.State}}' rabbitmq)"
orders=()
for i in 1 2 3 4; do orders+=("$(submit "$ORDER_API" "$RUN-$i" "$(order_body "$RUN-c$i" "$SKU" 1)")"); done
printf '%s\n' "${orders[@]}" > "$DRILL_DIR/orders.txt"
ends=""; bad=0
for o in "${orders[@]}"; do
  wait_status 180 "$o" 'Completed|Compensated|Rejected|ManualInterventionRequired'; e="$(end_of "$o")"; ends+="$e "
  [[ "$e" == Completed/Delivered ]] || bad=1
done
aexpect R14-down "RabbitMQ exited; 4 orders accepted during the outage Completed/Delivered" "rabbitmq $state; ends $ends" "$([[ "$state" == exited && $bad == 0 ]]; echo $?)"

ids="$(printf "'%s'," "${orders[@]}")"; ids="${ids%,}"
backlog=""; total=0
for s in "${M4_SOURCES[@]}"; do
  IFS='|' read -r name db table <<< "$s"
  n="$(sql "$db" "SELECT CONCAT(COUNT(*), '/', ISNULL(MAX(Attempts), 0)) FROM $table WHERE OrderId IN ($ids) AND State = 'Pending'")"
  backlog+="$name $n "; total=$((total + ${n%/*}))
done
FAULT_OBSERVED="backlog (pending/max attempts) $backlog"
aexpect R14-backlog "outbox rows of the run Pending with failed attempts while the broker is down" "$backlog" \
  "$([[ $total -gt 0 && "$backlog" == *"/"[1-9]* ]]; echo $?)"

# Keep the broker down until the rows have aged past the threshold and the alert fired.
alert_wait_firing OutboxAging 'source="orders"' 90 || true
docker compose start rabbitmq >/dev/null 2>&1
broker_ready() { docker compose exec -T rabbitmq rabbitmq-diagnostics -q check_port_connectivity >/dev/null 2>&1; }
wait_until 120 "RabbitMQ ready" broker_ready
drained() {
  local s name db table
  for s in "${M4_SOURCES[@]}"; do IFS='|' read -r name db table <<< "$s"
    [[ "$(sql "$db" "SELECT COUNT(*) FROM $table WHERE OrderId IN ($ids) AND State = 'Pending'")" == 0 ]] || return 1; done
}
wait_until 240 "outbox backlog drained" drained
RECOVERED_AT="$(now_iso)"
rows=""; for s in "${M4_SOURCES[@]}"; do IFS='|' read -r name db table <<< "$s"; rows+="$name $(sql "$db" "SELECT CONCAT(State, ':', COUNT(*)) FROM $table WHERE OrderId IN ($ids) GROUP BY State") "; done
aexpect R14-drained "every outbox row of the run Published" "$rows" "$(drained; echo $?)"

conv=""; bad=0
for o in "${orders[@]}"; do wait_until 120 "events of $o converged" is_converged "$o" || true; conv+="$(converged "$o"); "; is_converged "$o" || bad=1; done
r="$(rebuild_timeline)"
aexpect R14-converged "published = history (I20), all Applied; rebuild equal" "$conv rebuild $r" "$([[ $bad == 0 && "$r" == true\|* ]]; echo $?)"
read -r verdict detail <<< "$(alert_outcome OutboxAging 'source="orders"' "$ALERT_SINCE" "$AGING_BEFORE" 120)"
assert R14-alert "$verdict" "OutboxAging fires during the outage and resolves after the drain" "$detail"
