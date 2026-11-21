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
| `OPERATOR_API_KEY` | operator endpoints of the order API: `/reviews`, `/interventions` (`X-Api-Key`) |
| `PROVIDER_API_KEY` | the payment provider's API (sent by the payment service; required by the provider) |
| `CARRIER_API_KEY` | the carrier's API (sent by the shipping service; required by the carrier) |
| `CARRIER_HMAC_KEY` | signs carrier callbacks (carrier) and verifies them (order API ingress) |

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

## Inventory

| Setting | Default | Meaning |
|---|---|---|
| `INVENTORY_COORDINATION` (`Inventory__Coordination`) | `Actor` | `Actor`: reserve and release go through the warehouse/SKU stock actor; `SqlOnly`: the activities call the guarded SQL directly (same guarantees; used to prove they hold without actors) |
| `INVENTORY_HOLD_TTL` | empty | when set (a `TimeSpan`, for example `00:00:20`), the lifetime of new holds; otherwise `Inventory__HoldTtl` |
| `Inventory__HoldTtl` | 30 min (`DAPR_POC_ENV=drill`: 60 s) | how long a hold stays valid, by SQL time |
| `Inventory__SweepInterval`, `__SweepBatch` | 5 s, 50 (drill: 2 s) | hold sweeper cadence and holds expired per sweep (one transaction each) |
| `Inventory__SweeperLease` | 15 s (drill: 6 s) | the sweeper's SQL lease; another replica's sweeper takes over after it |
| `Inventory__SweeperEnabled` | `true` | turns the sweeper off (tests) |

Stock is seeded by the migration: the catalog SKUs in `W1` with 1,000,000
units each. Scenarios and drills create their own SKUs with test-only SQL
(`set_stock` in `scripts/lib.sh`); there is no restock API.

## Reviews and Signals (order API)

| Setting | Default | Meaning |
|---|---|---|
| `OPERATOR_API_KEY` | (`.env`) | operator role key for `/reviews` (distinct from `CUSTOMER_API_KEY`) |
| `Review__Deadline`, `Review__TimerGrace` | 10 min, 5 s (`DAPR_POC_ENV=drill`: 20 s, 2 s) | time a round stays open (SQL time); the workflow's timer fires this much later |
| `Review__Rounds` | `2` | rounds including the first; after the last one expires the order is `Rejected` |
| `Review__Operators` | `operator-1,operator-2` | simulated operator ids allowed to decide |
| `SignalDispatcher__Lease`, `__BaseBackoff`, `__MaxBackoff` | 30 s, 1 s, 30 s | signal claim lease and retry backoff |

## Payment, Shipping, Carrier, Interventions

`DAPR_POC_ENV=drill` shortens the time-related defaults (in parentheses).

| Setting | Default | Meaning |
|---|---|---|
| `Payment__ProviderUrl` | `http://payment-provider-simulator:8080` | the payment provider |
| `Payment__RequestTimeout` | 5 s (2 s) | per request; connecting gets half of it (a connect timeout counts as "nothing sent") |
| `Payment__MaxSends`, `__SendBackoff` | 4, 1 s (500 ms) | sends per operation and execution, backoff doubling |
| `Payment__StatusQueries`, `__StatusQueryInterval` | 6, 1 s (500 ms) | status queries after a request that may have reached the provider |
| `Payment__IdempotencyWindow` | 10 s (3 s) | a `NotFound` status is trusted only this long after the last send |
| `Provider__AuthorizationValidity` | 7 days (120 s) | simulator: how long an authorization can be captured |
| `Provider__DeclineFromMinor` | 10,000,000 | simulator: authorizations from this amount are declined |
| `Shipping__CarrierUrl` | `http://carrier-simulator:8080` | the carrier |
| `Shipping__RequestTimeout`, `__MaxSends`, `__SendBackoff` | 5 s (2 s), 4, 1 s (500 ms) | dispatch requests to the carrier |
| `Shipping__DeliveryDeadline` | 30 min (45 s) | a terminal status is expected this long after acceptance; then the carrier is polled once |
| `CarrierSim__Step` | 10 s (2 s) | simulator: interval between the automatic PickedUp, InTransit, Delivered callbacks |
| `CARRIER_SIM_STEP` | empty | simulator: when set to a positive `TimeSpan` (for example `00:00:02`), replaces the step above; `scripts/verify.sh` sets it on the release stack so that scenarios do not wait 30 s per delivery. It paces only the simulated carrier, not the system under test |
| `CarrierSim__CallbackUrls` | `http://order-api:8080` | simulator: callback receivers, comma-separated, tried in turn (`CARRIER_CALLBACK_URLS`; `stack-up.sh --replicas` names both order API replicas) |
| `CarrierSim__RetrySchedule` | `1,5,30,120,600` s (`1,2,4,8,16,32`) | simulator: retries of an unanswered callback, then dead letter (the drill schedule outlasts a restart of every order API replica) |
| `CarrierForwarder__Enabled`, `__PollInterval`, `__BatchSize`, `__Lease` | true, 500 ms, 10, 30 s | the order API's forwarder of accepted callbacks to Shipping |
| `CarrierForwarder__Lease`, `__MaxBackoff` (drill) | (10 s, 10 s) | the forwarder's drill profile: a Shipping outage drains within seconds of the restore |
| `CarrierForwarder__MaxBackoff`, `__UnknownShipmentRetries` | 1 min, 5 | retry backoff; attempts while Shipping does not know the shipment, then an `AmbiguousDispatch` case |
| `RepairDispatcher__Enabled` | true | the order API's starter of repair workflows for resolved cases |

## Events and the Reconciler

| Setting | Default | Meaning |
|---|---|---|
| `OutboxRelay__Enabled` | true | the outbox relay of an owning host (order API, inventory, payment, shipping) |
| `OutboxRelay__PollInterval`, `__BatchSize` | 500 ms, 20 | relay cadence and rows leased per cycle |
| `OutboxRelay__Lease` | 30 s (10 s) | how long a leased row is reserved for one relay; afterwards another relay takes it over |
| `OutboxRelay__FirstBackoff`, `__MaxBackoff` | 1 s, 5 min (10 s) | backoff of a failed publication, doubling, capped |
| `OutboxRelay__StallThreshold` | 10 (5) | attempts after which a row is flagged `Stalled` (it is still retried) |
| `OutboxRelay__PublishTimeout` | 5 s | per publication; a timed-out publication is retried (it may have reached the broker) |
| `Reconciler__Enabled` | true | the reconciler (one leased instance across the order API replicas) |
| `Reconciler__Interval`, `__Lease` | 30 s, 90 s (5 s, 15 s) | pass cadence and lease |
| `Reconciler__StageThreshold` | 5 min (20 s) | an order without a status change this long has its workflow checked |
| `Reconciler__ReviewNudgeAfter` | 2 min (60 s) | a running review wait this long past its deadline gets a nudge |
| `Reconciler__GapThreshold` | 1 min (10 s) | a projection gap, or a source head ahead of current state, this old is repaired by replay |
| `Reconciler__HeadsWindow`, `__MaxOrdersPerPass` | 24 h (30 min), 200 | how far back source heads are compared; orders checked per pass |

The workflow's time profile is fixed per environment: hold extension before
the capture 30 min (drill 30 s), minimum remaining hold validity for the
capture 2 min (15 s), compensation rounds 3 with 10 s (2 s) between them, and
a waiting case re-read every 10 min (30 s).

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
