#!/usr/bin/env bash
# Smoke deploy of the Kubernetes template (deploy/kubernetes) on a local kind cluster.
#
# Creates the kind cluster "fulfillment-smoke" (unless it exists), installs the Dapr control plane
# with Helm, loads the locally built release images (fulfillment/<role>:local-release, built by
# scripts/stack-up.sh or scripts/verify.sh) and the SQL Server and RabbitMQ images into the cluster,
# generates throwaway secrets, applies the in-cluster infrastructure and then the kind-smoke
# overlay, and checks:
#   K8S-1  the database setup and every migration job completed
#   K8S-2  every Dapr role is ready with its injected daprd sidecar (2/2 containers)
#   K8S-3  one order submitted through the order API completes (Completed), with every service on
#          the cluster taking part (compute, inventory, payment, shipping, both simulators)
# Evidence goes to artifacts/k8s/kind-smoke-<timestamp>/. The cluster stays up for inspection;
# remove it with: kind delete cluster --name fulfillment-smoke
#
# Requires: docker, kind, helm, kubectl, curl, python. Usage: scripts/kind-smoke.sh [--down]
# Each run deletes and recreates the namespace "fulfillment" in that cluster.
set -uo pipefail
REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
CLUSTER=fulfillment-smoke
NS=fulfillment
DAPR_VERSION=1.19.1
OVERLAY="$REPO_ROOT/deploy/kubernetes/overlays/kind-smoke"
OUT="$REPO_ROOT/artifacts/k8s/kind-smoke-$(date -u +%Y%m%dT%H%M%SZ)"
ROLES=(order-api workflow-host compute-worker inventory-service payment-service shipping-service)
SIMS=(carrier-simulator payment-provider-simulator)
INFRA_IMAGES=(mcr.microsoft.com/mssql/server:2022-CU27-ubuntu-22.04 rabbitmq:4.3.6-management)

if [[ "${1:-}" == --down ]]; then kind delete cluster --name "$CLUSTER"; exit $?; fi
mkdir -p "$OUT"
failures=0
log() { printf '%s %s\n' "$(date -u +%H:%M:%SZ)" "$*" | tee -a "$OUT/run.log"; }
check() { # check <id> <PASS|FAIL> <detail>
  [[ "$2" == PASS ]] || failures=$((failures + 1))
  log "$2 $1: $3"
  printf '{"check":"%s","result":"%s","detail":"%s"}\n' "$1" "$2" "${3//\"/\'}" >> "$OUT/results.jsonl"
}
k() { kubectl --context "kind-$CLUSTER" "$@"; }
native() { if command -v cygpath >/dev/null; then cygpath -w "$1"; else printf '%s' "$1"; fi; }  # paths for kubectl.exe on Windows
secret() { python -c "import secrets; print('Smk-' + secrets.token_hex(12) + 'aA1!')"; }

for tool in docker kind helm kubectl curl python; do
  command -v "$tool" >/dev/null || { log "REFUSED: $tool not found"; exit 2; }
done
for r in "${ROLES[@]}" "${SIMS[@]}"; do
  docker image inspect "fulfillment/$r:local-release" >/dev/null 2>&1 || { log "REFUSED: image fulfillment/$r:local-release missing (build it with scripts/stack-up.sh)"; exit 2; }
done
docker image inspect fulfillment/order-api:local-release --format '{{index .Config.Labels "org.opencontainers.image.revision"}}' > "$OUT/image-revision.txt" 2>/dev/null
{ kind version; helm version --short; kubectl version --client; } > "$OUT/tools.txt" 2>&1

# --- cluster and Dapr ----------------------------------------------------------------------------
if ! kind get clusters 2>/dev/null | grep -qx "$CLUSTER"; then
  log "creating kind cluster $CLUSTER"
  kind create cluster --name "$CLUSTER" --wait 120s >> "$OUT/run.log" 2>&1 || { log "REFUSED: kind create cluster failed"; exit 2; }
fi
log "installing Dapr $DAPR_VERSION with Helm"
helm repo add dapr https://dapr.github.io/helm-charts/ >/dev/null 2>&1
helm repo update dapr >/dev/null 2>&1
helm upgrade --install dapr dapr/dapr --version "$DAPR_VERSION" --namespace dapr-system --create-namespace \
  --kube-context "kind-$CLUSTER" --wait --timeout 10m >> "$OUT/run.log" 2>&1 || { log "REFUSED: Dapr install failed"; exit 2; }
k -n dapr-system get pods -o wide > "$OUT/dapr-system.txt" 2>&1

log "loading images into the cluster"
for img in "${INFRA_IMAGES[@]}"; do
  docker image inspect "$img" >/dev/null 2>&1 || docker pull "$img" >> "$OUT/run.log" 2>&1
  kind load docker-image --name "$CLUSTER" "$img" >> "$OUT/run.log" 2>&1
done
for r in "${ROLES[@]}" "${SIMS[@]}"; do kind load docker-image --name "$CLUSTER" "fulfillment/$r:local-release" >> "$OUT/run.log" 2>&1; done

# --- secrets (throwaway, git-ignored) --------------------------------------------------------------
declare -A PW
for key in SA_PASSWORD RABBIT_PASSWORD ORDER_DB_PASSWORD INVENTORY_DB_PASSWORD PAYMENT_DB_PASSWORD SHIPPING_DB_PASSWORD \
           CARRIER_SIM_DB_PASSWORD PAYMENT_PROVIDER_SIM_DB_PASSWORD DAPR_STATE_DB_PASSWORD ADMIN_API_KEY CUSTOMER_API_KEY \
           OPERATOR_API_KEY CARRIER_HMAC_KEY CARRIER_API_KEY PROVIDER_API_KEY DAPR_API_TOKEN APP_API_TOKEN; do PW[$key]="$(secret)"; done
{
  for key in SA_PASSWORD RABBIT_PASSWORD ORDER_DB_PASSWORD INVENTORY_DB_PASSWORD PAYMENT_DB_PASSWORD SHIPPING_DB_PASSWORD \
             CARRIER_SIM_DB_PASSWORD PAYMENT_PROVIDER_SIM_DB_PASSWORD DAPR_STATE_DB_PASSWORD; do echo "$key=${PW[$key]}"; done
} > "$OVERLAY/infra/infra.env"
{
  echo "STATE_STORE_CONN=Server=sqlserver;Database=DaprStateDb;User Id=dapr_state;Password=${PW[DAPR_STATE_DB_PASSWORD]};TrustServerCertificate=true"
  echo "RABBIT_CONN=amqp://fulfillment:${PW[RABBIT_PASSWORD]}@rabbitmq:5672"
  for key in ORDER_DB_PASSWORD INVENTORY_DB_PASSWORD PAYMENT_DB_PASSWORD SHIPPING_DB_PASSWORD CARRIER_SIM_DB_PASSWORD \
             PAYMENT_PROVIDER_SIM_DB_PASSWORD ADMIN_API_KEY CUSTOMER_API_KEY OPERATOR_API_KEY CARRIER_HMAC_KEY CARRIER_API_KEY \
             PROVIDER_API_KEY; do echo "$key=${PW[$key]}"; done
} > "$OVERLAY/secrets.env"
echo "token=${PW[DAPR_API_TOKEN]}" > "$OVERLAY/dapr-api-token.env"
echo "token=${PW[APP_API_TOKEN]}" > "$OVERLAY/app-api-token.env"

# --- infrastructure, then the services -----------------------------------------------------------
log "applying SQL Server, RabbitMQ and the database setup"
# Every run starts from a fresh namespace: the secrets are regenerated, and SQL Server keeps its data
# only for the life of its pod.
k delete namespace "$NS" --ignore-not-found --wait=true --timeout=5m >> "$OUT/run.log" 2>&1
k create namespace "$NS" --dry-run=client -o yaml | k apply -f - >> "$OUT/run.log" 2>&1
k -n "$NS" create configmap db-init-sql --from-file=init.sql="$(native "$REPO_ROOT/db/init/init.sql")" --dry-run=client -o yaml | k apply -f - >> "$OUT/run.log" 2>&1
k apply -k "$(native "$OVERLAY/infra")" >> "$OUT/run.log" 2>&1 || { log "REFUSED: infra apply failed"; exit 2; }
k -n "$NS" wait --for=condition=complete job/db-init --timeout=10m >> "$OUT/run.log" 2>&1
db_init="$(k -n "$NS" get job db-init -o jsonpath='{.status.succeeded}' 2>/dev/null)"

log "applying the kind-smoke overlay"
k kustomize "$(native "$OVERLAY")" > "$OUT/rendered.yaml" 2>&1
k apply -k "$(native "$OVERLAY")" >> "$OUT/run.log" 2>&1 || { log "FAIL: overlay apply failed"; failures=$((failures + 1)); }
k -n "$NS" wait --for=condition=complete job --all --timeout=10m >> "$OUT/run.log" 2>&1
jobs="$(k -n "$NS" get jobs -o jsonpath='{range .items[*]}{.metadata.name}={.status.succeeded}{" "}{end}' 2>/dev/null)"
if [[ "$db_init" == 1 && "$jobs" != *"=0"* && "$jobs" != *"= "* && "$(grep -o '=1' <<< "$jobs" | wc -l)" -ge 5 ]]; then
  check K8S-1 PASS "database setup and migrations completed: $jobs"
else
  check K8S-1 FAIL "db-init=$db_init; jobs: $jobs"
fi

k -n "$NS" wait --for=condition=available deployment --all --timeout=10m >> "$OUT/run.log" 2>&1
k -n "$NS" get pods -o wide > "$OUT/pods.txt" 2>&1
bad=()
for r in "${ROLES[@]}"; do
  ready="$(k -n "$NS" get pods -l "app.kubernetes.io/name=$r" -o jsonpath='{range .items[*]}{.status.containerStatuses[*].ready}{"|"}{end}')"
  want="$(k -n "$NS" get deployment "$r" -o jsonpath='{.spec.replicas}')"
  pods="$(tr '|' '\n' <<< "$ready" | grep -c '^true true$')"
  [[ "$pods" == "$want" ]] || bad+=("$r ${pods}/${want}")
done
for s in "${SIMS[@]}"; do
  [[ "$(k -n "$NS" get deployment "$s" -o jsonpath='{.status.availableReplicas}')" == 1 ]] || bad+=("$s not available")
done
if (( ${#bad[@]} == 0 )); then
  check K8S-2 PASS "every Dapr role ready with app and daprd (2/2) at its replica count; both simulators available"
else
  check K8S-2 FAIL "not ready: ${bad[*]}"
fi
for r in "${ROLES[@]}"; do k -n "$NS" logs "deployment/$r" -c app --tail=200 > "$OUT/logs-$r.txt" 2>&1; k -n "$NS" logs "deployment/$r" -c daprd --tail=200 > "$OUT/logs-$r-daprd.txt" 2>&1; done

# --- one order through the cluster ---------------------------------------------------------------
k -n "$NS" port-forward svc/order-api 18480:8080 > "$OUT/port-forward.log" 2>&1 &
pf=$!
trap 'kill $pf 2>/dev/null' EXIT
for _ in $(seq 1 30); do curl -s -o /dev/null -m 2 http://127.0.0.1:18480/health/live && break; sleep 1; done
API=http://127.0.0.1:18480
identity="$(curl -s -m 10 "$API/system/component-identity")"
echo "$identity" > "$OUT/identity-order-api.json"
key="kind-smoke-$(date -u +%H%M%S)-$RANDOM"
answer="$(curl -s -m 30 -X POST "$API/orders" -H "X-Api-Key: ${PW[CUSTOMER_API_KEY]}" -H "Idempotency-Key: $key" \
  -H 'content-type: application/json' -d '{"customerId":"kind-smoke","lines":[{"sku":"SKU-1","quantity":1}]}')"
echo "submit: $answer" > "$OUT/order.txt"
order="$(python -c "import json,sys; print(json.loads(sys.argv[1]).get('orderId',''))" "$answer" 2>/dev/null)"
status=""
if [[ -n "$order" ]]; then
  for _ in $(seq 1 120); do
    body="$(curl -s -m 10 "$API/orders/$order" -H "X-Api-Key: ${PW[CUSTOMER_API_KEY]}")"
    status="$(python -c "import json,sys; print(json.loads(sys.argv[1]).get('status',''))" "$body" 2>/dev/null)"
    [[ "$status" =~ ^(Completed|Compensated|Rejected|Cancelled|ManualInterventionRequired|Escalated)$ ]] && break
    sleep 5
  done
  echo "final: $body" >> "$OUT/order.txt"
fi
version="$(python -c "import json,sys; d=json.loads(sys.argv[1]); print(d.get('version',''), d.get('commit',''), d.get('buildFlavor',''))" "$identity" 2>/dev/null)"
if [[ "$status" == Completed ]]; then
  check K8S-3 PASS "order $order Completed through the cluster (order API $version)"
else
  check K8S-3 FAIL "order ${order:-not accepted} ended '${status:-unknown}'; answer: ${answer:0:200}"
fi

k -n "$NS" get pods -o wide > "$OUT/pods-after.txt" 2>&1
if (( failures == 0 )); then log "kind smoke PASS (evidence: $OUT)"; exit 0; fi
log "kind smoke FAIL: $failures check(s) failed (evidence: $OUT)"
exit 1
