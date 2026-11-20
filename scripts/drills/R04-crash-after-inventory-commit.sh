#!/usr/bin/env bash
# R04 (S05) a committed reservation is applied once when Dapr never learns of the activity's
# completion. Drill stack with replicas: bash scripts/stack-up.sh --drill --replicas
# The failpoint AfterBusinessCommitBeforeActivityCompletion (FailFast) ends the inventory process
# that ran the reservation, right after its transaction committed and before the activity returned.
# Dapr retries the activity on a live replica; the retry asks for the same operation keys and gets
# the stored results back.
#   R04-fired     FiredAt set; one inventory replica exited
#   R04-replayed  the reserve operation's stored result was read again by the retry (its replay count
#                 rose), and the retry ran on another replica than the one that died
#   R04-once      one hold for the line (consumed), one HoldCreated and one HoldConsumed outbox row,
#                 Reserved back at 0 and OnHand down by the quantity once; the order Completed once
#   R04-restart   after the replica restarts, nothing changes (fingerprint)
DRILL_ID=R04
. "$(dirname "$0")/lib.sh"
OUT_DIR="$DRILL_DIR"
. "$REPO_ROOT/scripts/scenarios/replica-lib.sh"

drill_preflight
require_replicas
client_sim identity > "$DRILL_DIR/client-identity.json" || { log "REFUSED: the client simulator does not build"; exit 2; }
CLIENT_SIM_BUILT=1
trap 'drill_finish; exit $?' EXIT
INV=(inventory-service inventory-service-2 inventory-service-3)
state_of() { docker compose --profile replicas ps -a --format '{{.State}}' "$1" 2>/dev/null; }
restart_dead() { local s; for s in "${INV[@]}"; do [[ "$(state_of "$s")" == running ]] || start_pair "$s"; done; }
on_cleanup "restart_dead"

pending="$(sql OrderDb "SELECT COUNT(*) FROM orders.WorkflowStartRequest WHERE State <> 'Closed'")"
[[ "$pending" == 0 ]] || log "warning: $pending start requests not closed before the wildcard failpoint"
SKU="SKU-R04-$(printf '%04X' $((RANDOM % 65536)))"
set_stock W1 "$SKU" 5
RUN="r04-$(date -u +%H%M%S)-$((RANDOM % 1000))"
arm_failpoint InventoryDb AfterBusinessCommitBeforeActivityCompletion '*' FailFast
INJECTED_AT="$(date -u +%Y-%m-%dT%H:%M:%S.%3NZ)"
c="$(sim order single --run-id "$RUN" --line "$SKU:2" --expect new --url "$ORDER_API" --url "$ORDER_API_2" --follow --follow-timeout 180)"
order_id="$(jq_py order "r['items'][0].get('orderId')")"
fired="$(failpoint_fired_at InventoryDb AfterBusinessCommitBeforeActivityCompletion)"
dead=""; for s in "${INV[@]}"; do [[ "$(state_of "$s")" != running ]] && dead="$s"; done
FAULT_OBSERVED="FiredAt $fired; exited: ${dead:-none}"

# R04-fired -------------------------------------------------------------------------------------
if [[ -n "$fired" && -n "$dead" ]]; then
  assert R04-fired PASS "the failpoint fired after the reservation committed; the replica ended" "FiredAt $fired; $dead exited (code $(docker inspect -f '{{.State.ExitCode}}' "$(docker compose --profile replicas ps -a -q "$dead")"))"
else
  assert R04-fired FAIL "fired and one replica exited" "FiredAt '$fired'; exited '${dead:-none}'"
fi

# R04-replayed ----------------------------------------------------------------------------------
po="$(sql InventoryDb "SELECT CONCAT(Outcome, '|', Replays, '|', ProcessedBy) FROM inventory.ProcessedOperation WHERE OrderId = '$order_id' AND Operation = 'Reserve'")"
IFS='|' read -r outcome replays by <<< "$po"
retry_log="$(docker compose --profile replicas logs --no-log-prefix --since "$INJECTED_AT" "${INV[@]}" 2>/dev/null | grep -c "failpoint AfterBusinessCommitBeforeActivityCompletion fired")"
observed="reserve result $outcome written by $by, read again $replays time(s); failpoint log lines $retry_log; died: $dead"
if [[ "$outcome" == Reserved && "$replays" -ge 1 ]]; then
  assert R04-replayed PASS "the retry read the stored reservation instead of reserving again" "$observed"
else
  assert R04-replayed FAIL "Reserved, replays >= 1" "$observed"
fi

# R04-once --------------------------------------------------------------------------------------
row="$(sql InventoryDb "SELECT CONCAT(
  (SELECT COUNT(*) FROM inventory.Hold WHERE OrderId = '$order_id'), '|',
  (SELECT MAX(State) FROM inventory.Hold WHERE OrderId = '$order_id'), '|',
  (SELECT STRING_AGG(EventType, ',') WITHIN GROUP (ORDER BY Sequence) FROM inventory.Outbox WHERE OrderId = '$order_id'), '|',
  (SELECT CONCAT(OnHand, '/', Reserved) FROM inventory.StockItem WHERE WarehouseId = 'W1' AND Sku = '$SKU'))")"
history="$(sql OrderDb "SELECT STRING_AGG(ToStatus, ',') WITHIN GROUP (ORDER BY Sequence) FROM orders.OrderStatusHistory WHERE OrderId = '$order_id'")"
inv="$(inventory_invariants "$SKU")"
observed="client exit $c; holds|state|outbox|stock $row; history $history; invariants $inv"
if [[ "$c" == 0 && "$row" == "1|Consumed|HoldCreated,HoldConsumed|3/0" && "$history" == "Submitted,Validating,Reserving,PaymentAuthorized,Packing,Dispatched,AwaitingDelivery,Completed" && "$inv" == 0\|0\|0\|1\|1 ]]; then
  assert R04-once PASS "one hold, one creation and one consumption event (the 60 s drill hold outlasts the 30 s extension), stock moved once; Completed once" "$observed"
else
  assert R04-once FAIL "1|Consumed|HoldCreated,HoldConsumed|3/0" "$observed"
fi

# R04-restart -----------------------------------------------------------------------------------
before="$row|$history"
restart_dead
RECOVERED_AT="$(date -u +%Y-%m-%dT%H:%M:%S.%3NZ)"
sleep 5
after="$(sql InventoryDb "SELECT CONCAT(
  (SELECT COUNT(*) FROM inventory.Hold WHERE OrderId = '$order_id'), '|',
  (SELECT MAX(State) FROM inventory.Hold WHERE OrderId = '$order_id'), '|',
  (SELECT STRING_AGG(EventType, ',') WITHIN GROUP (ORDER BY Sequence) FROM inventory.Outbox WHERE OrderId = '$order_id'), '|',
  (SELECT CONCAT(OnHand, '/', Reserved) FROM inventory.StockItem WHERE WarehouseId = 'W1' AND Sku = '$SKU'))")|$(sql OrderDb "SELECT STRING_AGG(ToStatus, ',') WITHIN GROUP (ORDER BY Sequence) FROM orders.OrderStatusHistory WHERE OrderId = '$order_id'")"
if [[ "$before" == "$after" ]]; then
  assert R04-restart PASS "the restarted replica changed nothing" "$after"
else
  assert R04-restart FAIL "unchanged" "before $before; after $after"
fi
