# Configuration

## Secrets (`.env`)

`.env` is git-ignored. `scripts/init-env.sh` writes it with random values
from the names in `.env.example`; Compose refuses to start when a required
value is missing.

| Variable | Used by |
|---|---|
| `SA_PASSWORD` | SQL Server administrator (init job and inspection only) |
| `ORDER_DB_PASSWORD`, `INVENTORY_DB_PASSWORD`, `PAYMENT_DB_PASSWORD`, `SHIPPING_DB_PASSWORD` | per-service SQL logins |
| `CARRIER_SIM_DB_PASSWORD`, `PAYMENT_PROVIDER_SIM_DB_PASSWORD` | simulator SQL logins |
| `DAPR_STATE_DB_PASSWORD` | SQL login of the Dapr state store (`DaprStateDb` only) |
| `RABBIT_PASSWORD` | RabbitMQ user `fulfillment` |
| `DAPR_API_TOKEN` | required by every sidecar API; sent by the apps |
| `APP_API_TOKEN` | sent by every sidecar to its app; required on sidecar-only routes |
| `ADMIN_API_KEY` | admin endpoints of the order API (`X-Api-Key`) |
| `GRAFANA_ADMIN_PASSWORD` | Grafana `admin` user |

## Application Settings

| Setting | Meaning |
|---|---|
| `ConnectionStrings__<Db>` | connection string of the role's own database (set by Compose with the role's login) |
| `REPLICA_NAME` | replica identity reported in logs, telemetry, and `/system/component-identity` |
| `DAPR_POC_ENV` | `local` (default) or `drill`; failpoints act only in `drill` |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | when set, logs, traces, and metrics are exported over OTLP |
| `DAPR_HTTP_PORT` | sidecar HTTP port (default 3500) |

## Dapr

| File | Content |
|---|---|
| `dapr/components/statestore.yaml` | `state.sqlserver` **version v2**, `DaprStateDb` through Toxiproxy, actor state store |
| `dapr/components/pubsub.yaml` | RabbitMQ |
| `dapr/components/secretstore.yaml` | `secretstores.local.env`: component secrets come from the sidecar's environment. This store refuses variables whose names start with `DAPR_`, so component secrets use other names (`STATE_STORE_CONN`, `RABBIT_CONN`) |
| `dapr/components/resiliency.yaml` | bounded inbound pub/sub redelivery; deliberately no service-invocation retry policy |
| `dapr/config/fulfillment.yaml` | tracing to the OpenTelemetry Collector, metrics, workflow concurrency limits (`spec.workflow`) |

## Compose Profiles

| Profile | Replicas |
|---|---|
| (none) | one per role |
| `replicas` | order API 2, workflow host 2, compute 2, inventory 3, payment 2, shipping 2 |
| `scale` (with `replicas`) | workflow host 3, compute 5 |

Each replica is declared explicitly (app plus sidecar): Compose cannot scale
an app and its sidecar as a pair.

## Build Flavors

`BUILD_FLAVOR=release` (default) or `drill` (failure injection compiled in;
`scripts/stack-up.sh --drill`). Images are tagged `local-<flavor>`. Build
identity (commit, branch, tree state) is passed as build arguments and
reported by `/system/component-identity`.
