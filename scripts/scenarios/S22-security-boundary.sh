#!/usr/bin/env bash
# S22 security boundary: every trust boundary of docs/security.md refuses a caller without its credential, and no
# secret value appears in any container's log.
# Any stack with replicas (bash scripts/stack-up.sh --replicas, release or drill).
#   S22-role-keys     public endpoints: no key or a wrong key is 401; another role's key on admin, operator, and
#                     customer endpoints is refused (401/403)
#   S22-app-token     sidecar-only routes of the order API (/events/..., /dapr/subscribe) without the app token: 401
#   S22-sidecar-api   the order API's own sidecar API (/v1.0/metadata) without the Dapr API token: 401; with it: 200
#   S22-workflow-api  workflow management through Dapr from a service that may not start workflows (payment-service):
#                     403 from the workflow host; nothing started
#   S22-carrier       a carrier callback without a valid signature: 401; no inbox row for its event id
#   S22-database      a service login cannot read another service's database (payment_service on OrderDb)
#   S22-secrets       no value from .env appears in the logs of any container of the stack
. "$(dirname "$0")/../lib.sh"
. "$(dirname "$0")/m3-lib.sh"
scenario_init S22
export COMPOSE_PROFILES=replicas
code() { curl -s -o /dev/null -w '%{http_code}' -m 10 "$@"; }
# raw <service> <port> <method> <path> <body> [header...]: HTTP/1.0 from inside the container (no curl in app images)
raw() {
  local svc=$1 port=$2 method=$3 path=$4 body=$5; shift 5
  local req="$method $path HTTP/1.0\r\nHost: localhost\r\nContent-Type: application/json\r\nContent-Length: ${#body}\r\n"
  local h; for h in "$@"; do req+="$h\r\n"; done
  req+="\r\n$body"
  docker compose exec -T -e REQ="$req" "$svc" bash -c "exec 3<>/dev/tcp/127.0.0.1/$port && printf '%b' \"\$REQ\" >&3 && head -1 <&3" 2>/dev/null | awk '{print $2}'
}
body='{"customerId":"s22-c1","lines":[{"sku":"SKU-1","quantity":1}]}'

# role keys --------------------------------------------------------------------------------------------------------
k1="$(code -X POST "$ORDER_API/orders" -H 'content-type: application/json' -H "Idempotency-Key: s22-$RANDOM" -d "$body")"
k2="$(code -X POST "$ORDER_API/orders" -H 'content-type: application/json' -H "X-Api-Key: wrong" -H "Idempotency-Key: s22-$RANDOM" -d "$body")"
k3="$(code "$ORDER_API/system/timeline/checksum" -H "X-Api-Key: $CUSTOMER_API_KEY")"
k4="$(code -X POST "$ORDER_API/reviews/$(new_uuid)/decision" -H 'content-type: application/json' -H "X-Api-Key: $CUSTOMER_API_KEY" -d '{"decisionId":"'"$(new_uuid)"'","operatorId":"operator-1","outcome":"approve"}')"
k5="$(code -X POST "$ORDER_API/interventions/$(new_uuid)/resolution" -H 'content-type: application/json' -H "X-Api-Key: $ADMIN_API_KEY" -d '{"resolutionId":"'"$(new_uuid)"'","operatorId":"operator-1","action":"Acknowledge","evidence":"x"}')"
k6="$(code "$ORDER_API/orders/$(new_uuid)" -H "X-Api-Key: $OPERATOR_API_KEY")"
expect S22-role-keys "orders: no key 401, wrong key 401; admin with customer key, decision with customer key, resolution with admin key, order read with operator key: refused" \
  "orders $k1/$k2; admin route $k3; decision $k4; resolution $k5; order read $k6" \
  "$([[ "$k1" == 401 && "$k2" == 401 && "$k3" =~ ^40[13]$ && "$k4" =~ ^40[13]$ && "$k5" =~ ^40[13]$ && "$k6" =~ ^40[13]$ ]]; echo $?)"

# app token on sidecar-only routes --------------------------------------------------------------------------------
t1="$(code -X POST "$ORDER_API/events/orders" -H 'content-type: application/json' -d '{}')"
t2="$(code "$ORDER_API/dapr/subscribe")"
expect S22-app-token "sidecar-only routes without the app token: 401" "/events/orders $t1; /dapr/subscribe $t2" "$([[ "$t1" == 401 && "$t2" == 401 ]]; echo $?)"

# the sidecar's own API -------------------------------------------------------------------------------------------
s1="$(raw order-api 3500 GET /v1.0/metadata '')"
s2="$(raw order-api 3500 GET /v1.0/metadata '' "dapr-api-token: $DAPR_API_TOKEN")"
expect S22-sidecar-api "sidecar API without the Dapr API token 401, with it 200" "without $s1; with $s2" "$([[ "$s1" == 401 && "$s2" == 200 ]]; echo $?)"

# workflow management from a service that is not allowed to start workflows ----------------------------------------
fake="s22-$(date -u +%H%M%S)$RANDOM"
w1="$(raw payment-service 3500 POST "/v1.0/invoke/workflow-host/method/internal/workflows/FulfillOrderWorkflow/fulfill-$fake/start" '{}' "dapr-api-token: $DAPR_API_TOKEN")"
w2="$(raw order-api 3500 GET "/v1.0/invoke/workflow-host/method/internal/workflows/fulfill-$fake" '' "dapr-api-token: $DAPR_API_TOKEN")"
expect S22-workflow-api "payment-service may not start a fulfillment workflow (403); the instance does not exist afterwards (read by the order API)" \
  "start from payment-service $w1; instance read from order-api $w2" "$([[ "$w1" == 403 && "$w2" =~ ^(200|404)$ ]]; echo $?)"
[[ "$w2" == 200 ]] && log "note: the workflow host answers 200 with exists=false for an unknown instance"

# carrier callback without a valid signature ----------------------------------------------------------------------
eid="$(new_uuid)"
c1="$(code -X POST "$ORDER_API/carrier/callbacks" -H 'content-type: application/json' -H 'X-Carrier-Timestamp: 0' -H 'X-Carrier-Signature: bad' \
  -d '{"eventId":"'"$eid"'","shipmentId":"'"$(new_uuid)"'","orderId":"'"$(new_uuid)"'","status":"Delivered","version":1,"occurredAtUtc":"'"$(now_iso)"'"}')"
c2="$(sql OrderDb "SELECT COUNT(*) FROM orders.CarrierCallbackInbox WHERE EventId = '$eid'")"
expect S22-carrier "unsigned callback 401; no inbox row" "status $c1; inbox rows $c2" "$([[ "$c1" == 401 && "$c2" == 0 ]]; echo $?)"

# database logins ---------------------------------------------------------------------------------------------------
d1="$(MSYS_NO_PATHCONV=1 docker compose exec -T sqlserver /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U payment_service -P "$PAYMENT_DB_PASSWORD" -d OrderDb -h -1 -b \
  -Q "SET NOCOUNT ON; SELECT COUNT(*) FROM orders.[Order]" 2>&1 | tr -d '\r' | head -2 | tr '\n' ' ')"
d2="$(MSYS_NO_PATHCONV=1 docker compose exec -T sqlserver /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U payment_service -P "$PAYMENT_DB_PASSWORD" -d PaymentDb -h -1 -b \
  -Q "SET NOCOUNT ON; SELECT 1" 2>&1 | tr -d '\r ' | head -1)"
expect S22-database "payment_service is refused on OrderDb, accepted on PaymentDb" "OrderDb: ${d1:0:160}; PaymentDb: $d2" \
  "$([[ "$d1" == *"Cannot open database"* || "$d1" == *"Login failed"* || "$d1" == *"denied"* ]] && [[ "$d2" == 1 ]]; echo $?)"

# secrets in logs --------------------------------------------------------------------------------------------------
grep -E '^[A-Z_]+=' "$REPO_ROOT/.env" | cut -d= -f2- | grep -E '.{8,}' > "$OUT_DIR/.secret-values" || true
n_secrets="$(wc -l < "$OUT_DIR/.secret-values")"
hits="$(docker compose --profile replicas --profile scale logs --no-color 2>/dev/null | grep -c -F -f "$OUT_DIR/.secret-values")"
rm -f "$OUT_DIR/.secret-values"
expect S22-secrets "no .env value in any container log" "$n_secrets secret values checked; log lines containing one: $hits" "$([[ "$n_secrets" -ge 10 && "$hits" == 0 ]]; echo $?)"
scenario_finish
