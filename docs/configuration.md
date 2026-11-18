# Configuration

## Secrets (`.env`)

`.env` is git-ignored. `scripts/init-env.sh` writes it with random values
from the names in `.env.example`, and appends names added to `.env.example`
later to an existing `.env` (existing values never change; `stack-up.sh` runs
it). Compose refuses to start when a required value is missing.

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
| `CUSTOMER_API_KEY` | customer endpoints of the order API: `/orders` (`X-Api-Key`) |
| `GRAFANA_ADMIN_PASSWORD` | Grafana `admin` user |

## Application Settings

| Setting | Meaning |
|---|---|
| `ConnectionStrings__<Db>` | connection string of the role's own database (set by Compose with the role's login) |
| `REPLICA_NAME` | replica identity reported in logs, telemetry, and `/system/component-identity` |
| `DAPR_POC_ENV` | `local` (default) or `drill`; failpoints act only in `drill` |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | when set, logs, traces, and metrics are exported over OTLP |
| `DAPR_HTTP_PORT` | sidecar HTTP port (default 3500) |

## Start Dispatcher

Section `StartDispatcher` (environment `StartDispatcher__<Name>`):

| Setting | Default | Meaning |
|---|---|---|
| `Enabled` | `true` | the dispatcher runs in every order API replica |
| `PollInterval` | `00:00:01` | time between polls (jittered 0.5-1.5×) |
| `BatchSize` | 10 | requests claimed per poll at most |
| `MaxInFlight` | 32 | concurrent starts per replica at most |
| `Lease` | `00:00:30` | exclusive claim time; Compose sets it from `START_DISPATCHER_LEASE` |
| `BaseBackoff` / `MaxBackoff` | `00:00:01` / `00:01:00` | retry delay after an ambiguous answer, doubling |
| `RejectedBackoff` | `00:05:00` | retry delay after a refusal |

## Database Schema

The order API's tables (`OrderDb`, schema `orders`) come from EF Core
migrations. `dotnet Fulfillment.OrderApi.dll migrate` applies them and exits;
in Compose this is the `order-db-migrate` job, which both order API replicas
wait for. A new migration is created with the pinned tool:

```bash
cd src && dotnet tool restore
dotnet ef migrations add <Name> --project Fulfillment.OrderApi --output-dir Persistence/Migrations
```

Generated migration files are excluded from analyzers (`src/.editorconfig`).

## Compute Checks

| Setting | Default | Meaning |
|---|---|---|
| `ComputeChecks__SimulatedWorkMilliseconds` | `300` | simulated computation time of each check (the checks run concurrently; S02 measures it) |
| `ComputeChecks__ReviewAmountThresholdMinor` | `100000` | order value (minor units, stub price list) from which a review is required: 1,000.00. Empty means no review is ever required |

## Reviews and Signals (order API)

| Setting | Default | Meaning |
|---|---|---|
| `OPERATOR_API_KEY` | (`.env`) | operator role key for `/reviews` (distinct from `CUSTOMER_API_KEY`) |
| `Review__Deadline`, `Review__TimerGrace` | 10 min, 5 s (`DAPR_POC_ENV=drill`: 20 s, 2 s) | time a round stays open (SQL time); the workflow's timer fires this much later |
| `Review__Rounds` | `2` | rounds including the first; after the last one expires the order is `Rejected` |
| `Review__Operators` | `operator-1,operator-2` | simulated operator ids allowed to decide |
| `SignalDispatcher__Lease`, `__BaseBackoff`, `__MaxBackoff` | 30 s, 1 s, 30 s | signal claim lease and retry backoff |

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
