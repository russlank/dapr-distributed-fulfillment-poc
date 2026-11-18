# Architecture

Distributed order fulfillment on Dapr 1.19 and .NET 10. SQL Server holds all
business truth; Dapr Workflow drives the process; Dapr Actors serialize
per-SKU inventory work; Dapr Pub/Sub distributes business events. External
parties (customers, the carrier, the payment provider) are simulated by
separate processes without Dapr. Synthetic data only.

This document describes the foundation that exists now and the boundaries
the later functionality builds on.

## Roles

One executable project and one container image per deployable role. Every
Dapr-enabled replica runs with its own `daprd` sidecar, which shares the
app's network namespace.

| Role (project) | Dapr app id | Owns | Hosts |
|---|---|---|---|
| `Fulfillment.OrderApi` | `order-api` | `OrderDb` | public HTTP API; start and signal records; activities that write `OrderDb` |
| `Fulfillment.WorkflowHost` | `workflow-host` | nothing (Dapr state only) | workflow orchestrations; the internal workflow-management API |
| `Fulfillment.ComputeWorker` | `compute-worker` | nothing | stateless activities |
| `Fulfillment.InventoryService` | `inventory-service` | `InventoryDb` | inventory activities and stock actors |
| `Fulfillment.PaymentService` | `payment-service` | `PaymentDb` | payment activities; client of the payment provider |
| `Fulfillment.ShippingService` | `shipping-service` | `ShippingDb` | shipment state and carrier status history |
| `Fulfillment.CarrierSimulator` | none | `CarrierSimDb` | external carrier (no sidecar) |
| `Fulfillment.PaymentProviderSimulator` | none | `PaymentProviderSimDb` | external payment provider (no sidecar) |
| `Fulfillment.ClientSimulator` | none | none | external customer/operator CLI |

Shared libraries: `Fulfillment.Contracts` (app ids, activity names and
payloads, management API contracts), `Fulfillment.Primitives` (operation
keys), `Fulfillment.Messaging` (caller-side invocation with retries; outbox
and inbox later), `Fulfillment.ServiceDefaults` (identity, health,
telemetry, sidecar-token check, test-only failpoints).

Each owning role has its own SQL login with rights on its own database only.
There is no cross-database access and no distributed transaction: every
business change commits in one local transaction of its owning database.

## Authority and Orchestration

- **SQL is the authority** for business state. Dapr workflow history (in
  `DaprStateDb`) and Scheduler timers are runtime state used to make
  progress; they never decide a business fact.
- **Workflows are deterministic orchestrators.** They call activities in the
  role that owns the data (`TargetAppId`), wait on durable timers and events,
  and do no I/O themselves.
- **Activities run at least once.** An activity interrupted after its effect
  committed runs again. Every effect therefore carries a stable operation key
  (`{subject}:{step}:{qualifier}`), and the owning database records it in the
  same transaction as the effect, so a repeat recognizes the earlier effect.
- **One execution per business subject.** Dapr replaces a completed instance
  when the same instance id is scheduled again, and a purged id can be reused.
  So the application enforces "one execution" itself: a unique start record in
  SQL, a first workflow step that claims execution in SQL and ends the run
  without effects if the subject is already finished, and keyed effects.

## Workflow Management Lives With the Workflow Host

A Dapr workflow instance belongs to the app whose sidecar scheduled it, and
the .NET workflow client cannot target another app. Scheduling a workflow
registered only on `workflow-host` through `order-api`'s sidecar creates the
instance in `order-api` and fails with `WorkflowNotFound`.

The roles that own the SQL records (for example the start and signal
dispatchers in `order-api`) therefore start, query, and signal workflows
through the workflow host's internal API, `/internal/workflows`, which is
reached by Dapr service invocation and protected by the app token:

| Call | Behavior |
|---|---|
| `POST /internal/workflows/{name}/{instanceId}/start` | checks the state first: an existing instance in any state is reported, never replaced (diagnostic probe instances may be replaced on purpose to prove the execution claim) |
| `GET /internal/workflows/{instanceId}` | state, output, failure details |
| `POST /internal/workflows/{instanceId}/events/{event}` | raises only to a running instance; otherwise returns the state (a completed instance silently ignores events, a missing one returns an error) |

The workflow host keeps no business logic and no database. Callers commit
their SQL record first and call this API with bounded retries.

## Service Invocation

The Dapr 1.19 sidecar did not retry application 5xx responses under a
resiliency policy, so callers retry themselves: `SidecarInvoker` calls the
sidecar's HTTP API with the Dapr API token, retries 5xx and transport errors
with exponential backoff up to a bounded number of attempts, never retries
4xx, and sends the same `Idempotency-Key` on every attempt. Receivers treat
repeats as duplicates.

## Runtime Probe

`POST /system/probes/runtime/{probeId}` (admin key) runs a diagnostic
workflow through the same mechanisms the fulfillment workflow uses: SQL start
record, execution claim in `OrderDb`, a keyed effect in `InventoryDb`, a
durable timer, a second keyed effect, a compute step, and completion. It is
how the stack is checked end to end; it is not business functionality. See
[testing.md](testing.md) for the checks built on it.

## Runtime Infrastructure

| Component | Purpose | Notes |
|---|---|---|
| SQL Server 2022 | business databases and `DaprStateDb` | one login per owning service; created by the `db-init` job |
| `state.sqlserver` **v2** | workflow history and actor state | v1 stores values as text and corrupts binary history; v2 stores binary values in `varbinary` |
| Toxiproxy | path from the sidecars to `DaprStateDb` | lets failure drills cut only the state-store path |
| Dapr Placement | actor placement | |
| Dapr Scheduler | durable timers and reminders | persistent volume; reset together with SQL data (lost timers are lost, see [journal.md](journal.md)) |
| RabbitMQ | pub/sub for business events | used from the event-distribution milestone |
| OpenTelemetry Collector, Jaeger, Prometheus, Grafana | traces, metrics | see [monitoring.md](monitoring.md) |

Workflow concurrency limits are sidecar settings
(`dapr/config/fulfillment.yaml`, `spec.workflow`); the .NET SDK's own
options are obsolete and have no effect in 1.19.
