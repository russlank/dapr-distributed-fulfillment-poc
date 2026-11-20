#!/usr/bin/env bash
# Shared parts of the payment, shipping, cancellation, review, and intervention scenarios and drills
# (source after lib.sh or drills/lib.sh; do not run). They run on the drill stack (provider and carrier
# test controls exist only with DAPR_POC_ENV=drill): bash scripts/stack-up.sh --drill --replicas
PROVIDER="${PROVIDER:-http://127.0.0.1:8091}"
CARRIER="${CARRIER:-http://127.0.0.1:8090}"

# scenario_init <id>: evidence directory, results file, PASS/FAIL counting; refuses (exit 2) unless the
# provider's drill-only test controls answer (the scenario needs a drill stack).
scenario_init() {
  SCENARIO_ID=$1
  RUN_ID="$1-$(date -u +%Y%m%dT%H%M%SZ)"
  OUT_DIR="$REPO_ROOT/artifacts/scenarios/$RUN_ID"
  mkdir -p "$OUT_DIR"
  RESULTS_FILE="$OUT_DIR/results.jsonl"
  PREFIX="$(printf '%s' "$1" | tr 'A-Z' 'a-z')-$(date -u +%H%M%S)-$RANDOM"
  failures=0
  local probe
  probe="$(curl -s -m 10 -o /dev/null -w '%{http_code}' "$PROVIDER/provider/test/operations?prefix=00000000" -H "X-Api-Key: $PROVIDER_API_KEY")"
  [[ "$probe" == 200 ]] || { log "REFUSED: $1 needs the drill stack (provider test controls answered '$probe'): bash scripts/stack-up.sh --drill --replicas"; exit 2; }
  log "$1 $RUN_ID: evidence in $OUT_DIR"
}
check() { [[ "$2" == PASS ]] || failures=$((failures + 1)); record "$@"; }
# expect <check id> <expected> <observed> <condition result: 0 PASS, else FAIL>
expect() { if [[ "$4" == 0 ]]; then check "$1" PASS "$2" "$3"; else check "$1" FAIL "$2" "$3"; fi; }
scenario_finish() {
  log "$SCENARIO_ID finished: $failures failed check(s); results in $RESULTS_FILE"
  (( failures == 0 ))
}

# nkey <order id>: the id as operation keys spell it (32 lower-case hex digits).
# sqln: sql, with an empty aggregate (NULL) printed as nothing.
sqln() { sql "$@" | sed 's/^NULL$//'; }
nkey() { printf '%s' "$1" | tr -d '-' | tr 'A-F' 'a-f'; }
new_uuid() { python -c 'import uuid; print(uuid.uuid4())'; }
now_iso() { date -u +%Y-%m-%dT%H:%M:%S.%3NZ; }

# --- payment provider test controls (drill only) ---------------------------------------------------
# provider_fault <label> <match: operation key or kind:Authorize|Capture|Void|Refund> <mode> [param] [uses]
provider_fault() {
  curl -s -m 10 -X POST "$PROVIDER/provider/test/faults" -H "X-Api-Key: $PROVIDER_API_KEY" -H 'content-type: application/json' \
    -d '{"label":"'"$1"'","match":"'"$2"'","mode":"'"$3"'","param":'"${4:-null}"',"uses":'"${5:-null}"'}'
}
provider_clear() { curl -s -m 10 -o /dev/null -X DELETE "$PROVIDER/provider/test/faults?labelPrefix=$1" -H "X-Api-Key: $PROVIDER_API_KEY"; }

# provider_ops <order id>: the provider's committed operations of the order, "kind:1=Result" in commit order.
provider_ops() {
  sqln PaymentProviderSimDb "SELECT STRING_AGG(CONCAT(SUBSTRING(OperationKey, 34, 100), '=', Result), ',') WITHIN GROUP (ORDER BY CommittedAt, OperationKey)
    FROM provider.Operation WHERE OperationKey LIKE '$(nkey "$1"):%'"
}
# provider_money <order id>: "authorized|captured|voided|refunded" counts of financial effects at the provider.
provider_money() {
  sql PaymentProviderSimDb "SELECT CONCAT(SUM(CASE WHEN Result = 'Authorized' THEN 1 ELSE 0 END), '|', SUM(CASE WHEN Result = 'Captured' THEN 1 ELSE 0 END), '|',
    SUM(CASE WHEN Result = 'Voided' THEN 1 ELSE 0 END), '|', SUM(CASE WHEN Result = 'Refunded' THEN 1 ELSE 0 END))
    FROM provider.Operation WHERE OperationKey LIKE '$(nkey "$1"):%'"
}
# payment_ops <order id>: the payment service's operations, "Kind=State/Outcome" in creation order.
payment_ops() {
  sqln PaymentDb "SELECT STRING_AGG(CONCAT(Kind, '=', State, '/', ISNULL(Outcome, '-')), ',') WITHIN GROUP (ORDER BY CreatedAt, OperationKey)
    FROM payment.PaymentOperation WHERE OrderId = '$1'"
}
# payment_attempts <order id> <kind>: the attempt log of one operation kind, "answer@http" in order.
payment_attempts() {
  sqln PaymentDb "SELECT STRING_AGG(CONCAT(a.Kind, ':', a.Answer, '@', ISNULL(CONVERT(varchar(5), a.HttpStatus), '-')), ',') WITHIN GROUP (ORDER BY a.Id)
    FROM payment.PaymentAttempt a JOIN payment.PaymentOperation o ON o.OperationKey = a.OperationKey WHERE o.OrderId = '$1' AND o.Kind = '$2'"
}

# --- carrier test controls (drill only) ------------------------------------------------------------
carrier_mode() { curl -s -m 10 -X PUT "$CARRIER/carrier/test/shipments/$1/mode/$2" -H "X-Api-Key: $CARRIER_API_KEY"; }
# carrier_script <shipment id> <label> '<json array of {status, version, delayMs, truth?, badSignature?, sameEventIdAs?, orderIdOverride?}>'
carrier_script() {
  curl -s -m 10 -X POST "$CARRIER/carrier/test/shipments/$1/script?label=$2" -H "X-Api-Key: $CARRIER_API_KEY" -H 'content-type: application/json' -d "$3"
}
carrier_sends() { curl -s -m 10 "$CARRIER/carrier/test/shipments/$1/sends" -H "X-Api-Key: $CARRIER_API_KEY"; }

# --- orders ------------------------------------------------------------------------------------------
# order_body <customer> <sku> <quantity> [<sku> <quantity>...]
order_body() {
  local customer=$1; shift
  local lines="" sep=""
  while (( $# >= 2 )); do lines+="$sep{\"sku\":\"$1\",\"quantity\":$2}"; sep=","; shift 2; done
  printf '{"customerId":"%s","lines":[%s]}' "$customer" "$lines"
}
# submit <base url> <idempotency key> <body> -> order id (empty when not accepted)
submit() {
  local out
  out="$(curl -s -m 30 -X POST "$1/orders" -H "X-Api-Key: $CUSTOMER_API_KEY" -H "Idempotency-Key: $2" -H 'content-type: application/json' -d "$3")"
  json_get "$out" orderId
}
# cancel_order <base url> <order id> -> "<status> <code or cancellation>"
cancel_order() {
  local out
  out="$(curl -s -m 30 -w '\n%{http_code}' -X POST "$1/orders/$2/cancel" -H "X-Api-Key: $CUSTOMER_API_KEY")"
  local body="${out%$'\n'*}"
  printf '%s %s\n' "${out##*$'\n'}" "$(json_get "$body" code)$(json_get "$body" cancellation)"
}
order_status() { sql OrderDb "SELECT Status FROM orders.[Order] WHERE OrderId = '$1'"; }
status_is() { [[ "$(order_status "$1")" =~ ^($2)$ ]]; }
# wait_status <seconds> <order id> <status regex>
wait_status() { wait_until "$1" "order $2 $3" status_is "$2" "$3"; }
history_of() { sqln OrderDb "SELECT STRING_AGG(ToStatus, ',') WITHIN GROUP (ORDER BY Sequence) FROM orders.OrderStatusHistory WHERE OrderId = '$1'"; }
# end_of <order id> -> "<status>/<reason of the last history row>"
end_of() {
  sql OrderDb "SELECT CONCAT(o.Status, '/', (SELECT TOP (1) h.ReasonCode FROM orders.OrderStatusHistory h WHERE h.OrderId = o.OrderId ORDER BY h.Sequence DESC))
    FROM orders.[Order] o WHERE o.OrderId = '$1'"
}
completed_events() { sql OrderDb "SELECT COUNT(*) FROM orders.OrderOutbox WHERE OrderId = '$1' AND EventType = 'OrderCompleted'"; }
# holds_of <order id> -> hold states in line order; hold_events_of <order id> -> inventory outbox events
holds_of() { sqln InventoryDb "SELECT STRING_AGG(State, ',') WITHIN GROUP (ORDER BY LineNumber) FROM inventory.Hold WHERE OrderId = '$1'"; }
hold_events_of() { sqln InventoryDb "SELECT STRING_AGG(EventType, ',') WITHIN GROUP (ORDER BY Sequence) FROM inventory.Outbox WHERE OrderId = '$1'"; }
# cases_of <order id> -> "Kind:State:ResolutionPath" per case in opening order
cases_of() {
  sqln OrderDb "SELECT STRING_AGG(CONCAT(Kind, ':', State, ':', ISNULL(ResolutionPath, '-')), ',') WITHIN GROUP (ORDER BY OpenedAt) FROM orders.InterventionCase WHERE OrderId = '$1'"
}
open_case_of() { sql OrderDb "SELECT TOP (1) CaseId FROM orders.InterventionCase WHERE OrderId = '$1' AND Kind = '$2' AND State = 'Open' ORDER BY OpenedAt"; }
case_open() { [[ -n "$(open_case_of "$1" "$2")" ]]; }
# resolve_case <base url> <case id> <action> <resolution id> [operator] -> "<status> <path or code>"
resolve_case() {
  local out
  out="$(curl -s -m 30 -w '\n%{http_code}' -X POST "$1/interventions/$2/resolution" -H "X-Api-Key: $OPERATOR_API_KEY" -H 'content-type: application/json' \
    -d '{"resolutionId":"'"$4"'","operatorId":"'"${5:-operator-1}"'","action":"'"$3"'","evidence":"provider and carrier records checked by the drill"}')"
  local body="${out%$'\n'*}"
  printf '%s %s\n' "${out##*$'\n'}" "$(json_get "$body" path)$(json_get "$body" code)"
}
# shipment_of <order id> -> "State|CurrentStatus|CurrentVersion|TerminalStatus" in ShippingDb
shipment_of() {
  sql ShippingDb "SELECT CONCAT(State, '|', ISNULL(CurrentStatus, '-'), '|', ISNULL(CONVERT(varchar(5), CurrentVersion), '-'), '|', ISNULL(TerminalStatus, '-'))
    FROM shipping.Shipment WHERE OrderId = '$1'"
}
# carrier_events_of <order id> -> Shipping's recorded carrier events "Status:Version=Outcome" in arrival order
carrier_events_of() {
  sqln ShippingDb "SELECT STRING_AGG(CONCAT(Status, ':', Version, '=', Outcome), ',') WITHIN GROUP (ORDER BY ReceivedAt, EventId) FROM shipping.CarrierStatusEvent WHERE OrderId = '$1'"
}
# workflow_of <order id> -> "<runtime status>/<output outcome>/<custom status>" from the execution diagnostics
workflow_of() {
  local e; e="$(admin_api GET "/system/orders/$1/execution")"
  printf '%s/%s/%s\n' "$(json_get "$e" workflow.status)" "$(json_get "$e" workflow.output.outcome)" "$(json_get "$e" workflow.customStatus)"
}
# fresh_sku <prefix> [on hand]: a new SKU with stock in W1 (and none elsewhere).
fresh_sku() {
  local sku="SKU-$1-$(printf '%04X' $((RANDOM % 65536)))"
  set_stock W1 "$sku" "${2:-10}"
  printf '%s' "$sku"
}
# aexpect <assertion id> <expected> <observed> <condition result: 0 PASS, else FAIL> (drills: counts toward the drill outcome)
aexpect() { if [[ "$4" == 0 ]]; then assert "$1" PASS "$2" "$3"; else assert "$1" FAIL "$2" "$3"; fi; }
# carrier_sent <shipment id> -> the carrier's send log "Status:State:ResponseCode" per send
carrier_sent() {
  carrier_sends "$1" | python -c "import json,sys; print(','.join(f\"{s['status']}:{s['state']}:{s.get('responseCode')}\" for s in json.load(sys.stdin)['sends']))" 2>/dev/null
}
