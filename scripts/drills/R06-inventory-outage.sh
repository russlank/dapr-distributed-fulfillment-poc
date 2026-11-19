#!/usr/bin/env bash
# R06 (M2 inventory variant, S08) holds expire while the whole inventory is down, and nothing is
# lost or doubled. Drill stack with replicas: bash scripts/stack-up.sh --drill --replicas
# The drill recreates the inventory replicas with a 20 s hold lifetime (INVENTORY_HOLD_TTL) unless
# they already run with it, and recreates them with the stack's own setting in cleanup.
# Orders A1 and A2 (1 unit each) reserve their hold, then wait 25 s in their first stub step
# (failpoint BeforeStubFulfillmentStep Delay:25000, OrderDb). Meanwhile every inventory replica (app
# and sidecar) is stopped; B1 and B2 are accepted during the outage. The A holds pass their expiry
# while no inventory process (and no sweeper) runs. After the restore the A orders cannot consume
# their expired holds and end Compensated (ReservationExpired); B1 and B2 reserve and complete.
#   R06-held       A1 and A2 hold their unit and are inside the delayed stub step (both failpoints
#                  fired) before the outage
#   R06-outage     no inventory replica runs; for the whole outage nothing changes in InventoryDb for
#                  the SKU (stock, holds, outbox), no order of the run ends (B1 and B2 wait for their reservation),
#                  and the A holds' expiry passes during the outage; workflows not Failed
#   R06-recovered  after the restore A1, A2 Compensated (ReservationExpired), their holds Expired by the
#                  sweeper or left unconsumed (one closing event each); B1, B2 Completed
#   R06-stock      OnHand 10 - 2 = 8, Reserved 0; invariants clean (4 holds)
DRILL_ID=R06
. "$(dirname "$0")/lib.sh"
OUT_DIR="$DRILL_DIR"
. "$REPO_ROOT/scripts/scenarios/replica-lib.sh"

drill_preflight
require_replicas
client_sim identity > "$DRILL_DIR/client-identity.json" || { log "REFUSED: the client simulator does not build"; exit 2; }
CLIENT_SIM_BUILT=1
trap 'drill_finish; exit $?' EXIT
INV=(inventory-service inventory-service-2 inventory-service-3)
OUTAGE="${R06_OUTAGE_SECONDS:-50}"
state_of() { docker compose --profile replicas ps -a --format '{{.State}}' "$1" 2>/dev/null; }
restart_dead() { local s; for s in "${INV[@]}"; do [[ "$(state_of "$s")" == running ]] || start_pair "$s"; done; }
on_cleanup "restart_dead"

ttl_of() { docker compose --profile replicas exec -T "$1" printenv INVENTORY_HOLD_TTL 2>/dev/null | tr -d '\r'; }
STACK_TTL="$(ttl_of inventory-service)"
with_ttl() { local s; for s in "${INV[@]}"; do INVENTORY_HOLD_TTL="$1" restart_pair "$s" || return 1; done; }
if [[ "$STACK_TTL" != 00:00:20 ]]; then
  on_cleanup "with_ttl '$STACK_TTL'"
  with_ttl 00:00:20 || { log "REFUSED: could not recreate the inventory replicas with a 20 s hold lifetime"; exit 2; }
fi
ttls="$(for s in "${INV[@]}"; do ttl_of "$s"; done | sort -u | paste -sd, -)"
[[ "$ttls" == 00:00:20 ]] || { log "REFUSED: inventory hold lifetime is '$ttls', not 00:00:20"; exit 2; }
SKU="SKU-R06-$(printf '%04X' $((RANDOM % 65536)))"
set_stock W1 "$SKU" 10
RUN="r06i-$(date -u +%H%M%S)-$((RANDOM % 1000))"
URLS=(--url "$ORDER_API" --url "$ORDER_API_2")
order_of() { jq_py "$1" "r['items'][0].get('orderId')"; }
status_of() { sql OrderDb "SELECT Status FROM orders.[Order] WHERE OrderId = '$1'"; }
reason_of() { sql OrderDb "SELECT TOP (1) ReasonCode FROM orders.OrderStatusHistory WHERE OrderId = '$1' ORDER BY Sequence DESC"; }
fired_count() { sql OrderDb "SELECT COUNT(*) FROM drill.Failpoint WHERE DrillRunId LIKE '$DRILL_RUN_ID%' AND FiredAt IS NOT NULL"; }

# R06-held: each A order takes one delay row (one row armed per order, so neither order takes both).
ids=()
for a in a1 a2; do
  sql OrderDb "INSERT drill.Failpoint (Name, DrillRunId, OperationKey, Action) VALUES ('BeforeStubFulfillmentStep', '$DRILL_RUN_ID-$a', '*', 'Delay:25000')" >/dev/null
  rc="$(sim "$a" single --run-id "$RUN-$a" --line "$SKU:1" --expect new "${URLS[@]}")"
  ids+=("$(order_of "$a")")
  want=${#ids[@]}
  fired_enough() { [[ "$(fired_count)" -ge "$want" ]]; }
  wait_until 30 "$a in its delayed stub step" fired_enough
done
on_cleanup "sql OrderDb \"DELETE drill.Failpoint WHERE DrillRunId LIKE '$DRILL_RUN_ID%' AND FiredAt IS NULL\" >/dev/null"
A1=${ids[0]}; A2=${ids[1]}
held="$(sql InventoryDb "SELECT CONCAT(COUNT(*), '|', MIN(DATEDIFF(millisecond, SYSUTCDATETIME(), ExpiresAt))) FROM inventory.Hold WHERE Sku = '$SKU' AND State = 'Held'")"
IFS='|' read -r held_n expires_in_ms <<< "$held"
observed="A1 $A1 ($(status_of "$A1")), A2 $A2 ($(status_of "$A2")); delay failpoints fired $(fired_count)/2; Held holds $held_n, first expiry in ${expires_in_ms} ms"
if [[ "$held_n" == 2 && "$(fired_count)" == 2 ]]; then
  assert R06-held PASS "both A orders hold their unit and wait in the delayed stub step" "$observed"
else
  assert R06-held FAIL "2 Held holds, 2 fired delays" "$observed"
fi

# R06-outage -------------------------------------------------------------------------------------
INJECTED_AT="$(date -u +%Y-%m-%dT%H:%M:%S.%3NZ)"
for s in "${INV[@]}"; do docker compose --profile replicas stop -t 5 "$s" "$s-dapr" >/dev/null 2>&1; done
running="$(for s in "${INV[@]}"; do state_of "$s"; state_of "$s-dapr"; done | grep -c '^running$')"
log "stopped ${INV[*]} and their sidecars; running: $running"
rb1="$(sim b1 single --run-id "$RUN-b1" --line "$SKU:1" --expect new "${URLS[@]}")"
rb2="$(sim b2 single --run-id "$RUN-b2" --line "$SKU:1" --expect new "${URLS[@]}")"
B1="$(order_of b1)"; B2="$(order_of b2)"
fingerprint() {
  sql InventoryDb "SELECT CONCAT((SELECT CONCAT(OnHand, '/', Reserved) FROM inventory.StockItem WHERE WarehouseId = 'W1' AND Sku = '$SKU'), '|',
    (SELECT STRING_AGG(CONCAT(State, ':', ISNULL(ClosedBy, '-')), ',') WITHIN GROUP (ORDER BY CreatedAt) FROM inventory.Hold WHERE Sku = '$SKU'), '|',
    (SELECT COUNT(*) FROM inventory.Outbox WHERE OrderId IN (SELECT OrderId FROM inventory.Hold WHERE Sku = '$SKU')))"
}
orders_state() { sql OrderDb "SELECT STRING_AGG(Status, ',') WITHIN GROUP (ORDER BY CreatedAt) FROM orders.[Order] WHERE OrderId IN ('$A1', '$A2', '$B1', '$B2')"; }
first="$(fingerprint)"; changes=0; passed_expiry=""
: > "$DRILL_DIR/outage-samples.txt"
start=$SECONDS
while (( SECONDS - start < OUTAGE )); do
  f="$(fingerprint)"; o="$(orders_state)"
  echo "$(date -u +%H:%M:%S) inventory $f; orders $o" >> "$DRILL_DIR/outage-samples.txt"
  [[ "$f" == "$first" ]] || changes=$((changes + 1))
  [[ ",$o," =~ ,(Completed|Compensated|Rejected|Failed), ]] && changes=$((changes + 1))
  sleep 5
done
passed_expiry="$(sql InventoryDb "SELECT COUNT(*) FROM inventory.Hold WHERE Sku = '$SKU' AND State = 'Held' AND ExpiresAt < SYSUTCDATETIME()")"
wf=""; failed=0
for id in "$A1" "$A2" "$B1" "$B2"; do
  e="$(admin_api GET "/system/orders/$id/execution")"; s="$(json_get "$e" workflow.status)"; wf+="$s "
  [[ "$s" == Failed ]] && failed=$((failed + 1))
done
FAULT_OBSERVED="inventory containers running: $running; ${OUTAGE}s: changed samples $changes; held holds past expiry at the end $passed_expiry"
observed="running $running; B submits $rb1/$rb2; samples with a change $changes (inventory at start $first; orders A1,A2,B1,B2 $(orders_state)); Held holds past their expiry $passed_expiry; workflows $wf"
if [[ "$running" == 0 && "$rb1" == 0 && "$rb2" == 0 && "$changes" == 0 && "$passed_expiry" == 2 && "$failed" == 0 ]]; then
  assert R06-outage PASS "inventory down: nothing changed, B orders pending, A holds expired during the outage, no workflow failed" "$observed"
else
  assert R06-outage FAIL "running 0, no change, 2 expired-but-Held holds, no Failed workflow" "$observed"
fi

# R06-recovered ----------------------------------------------------------------------------------
restart_dead
ended() { [[ "$(sql OrderDb "SELECT COUNT(*) FROM orders.[Order] WHERE OrderId IN ('$A1', '$A2', '$B1', '$B2') AND Status IN ('Completed', 'Compensated', 'Rejected', 'Failed')")" == 4 ]]; }
wait_until 180 "all four orders ended" ended
RECOVERED_AT="$(date -u +%Y-%m-%dT%H:%M:%S.%3NZ)"
a_end="$(status_of "$A1")/$(reason_of "$A1"),$(status_of "$A2")/$(reason_of "$A2")"
b_end="$(status_of "$B1"),$(status_of "$B2")"
a_holds="$(sql InventoryDb "SELECT STRING_AGG(CONCAT(State, ':', ClosedBy), ',') FROM inventory.Hold WHERE OrderId IN ('$A1', '$A2')")"
b_holds="$(sql InventoryDb "SELECT STRING_AGG(State, ',') FROM inventory.Hold WHERE OrderId IN ('$B1', '$B2')")"
observed="A $a_end, holds $a_holds; B $b_end, holds $b_holds"
if [[ "$a_end" == "Compensated/ReservationExpired,Compensated/ReservationExpired" && "$b_end" == "Completed,Completed" && "$b_holds" == "Consumed,Consumed" && "$a_holds" != *Consumed* && "$a_holds" != *Held* ]]; then
  assert R06-recovered PASS "A orders compensated for their expired holds; B orders completed" "$observed"
else
  assert R06-recovered FAIL "A Compensated/ReservationExpired x2, B Completed x2" "$observed"
fi

# R06-stock --------------------------------------------------------------------------------------
stock="$(sql InventoryDb "SELECT CONCAT(OnHand, '/', Reserved) FROM inventory.StockItem WHERE WarehouseId = 'W1' AND Sku = '$SKU'")"
inv="$(inventory_invariants "$SKU")"
observed="stock $stock; invariants $inv"
if [[ "$stock" == 8/0 && "$inv" == "0|0|0|4|1" ]]; then
  assert R06-stock PASS "two units consumed, the expired ones back; invariants clean" "$observed"
else
  assert R06-stock FAIL "8/0 and 0|0|0|4|1" "$observed"
fi
