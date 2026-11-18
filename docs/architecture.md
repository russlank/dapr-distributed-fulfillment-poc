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
payloads, management API contracts, public order API contracts), `Fulfillment.Primitives` (operation
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

## Order Acceptance

`POST /orders` (customer key, `Idempotency-Key` header) is the only way an
order enters the system. The request is checked for syntax and bounds only
(at most 20 lines, quantity 1-100, body at most 64 KB; unknown, duplicate, or
missing members are refused); business rules are the workflow's job. An
accepted order is committed in **one `OrderDb` transaction**:

| Table (`orders` schema) | Row | Kind of state |
|---|---|---|
| `Order` | the order, status `Submitted` | business |
| `OrderLine` | one per line, numbered from 1 in request order | business |
| `OrderStatusHistory` | sequence 1, `Submitted`, reason `Accepted` | business |
| `IdempotencyKey` | the client's key and the SHA-256 of the normalized request | business (client contract) |
| `WorkflowStartRequest` | `Pending`, instance id `fulfill-{orderId:N}`, attempt and lease columns, execution-claim counters | workflow execution |

Business state and execution state are separate: the order's status is
business truth; the start request only records how far starting its workflow
has got, and `GET /orders/{id}` returns the business view only.

Acceptance starts no workflow. The order is durable at commit; the start
dispatcher (next section) claims due `Pending` start requests and starts the
instance through the workflow host's management API. A crash between commit
and start therefore loses nothing.

Idempotency:

- same key, same normalized request → the original order (`200`, `replayed`);
  the first acceptance returns `201`;
- same key, different request → `422 idempotency-key-reused`, nothing
  written;
- concurrent submissions with one key, on any replica: the key's primary key
  decides. The first commit wins; a concurrent insert waits for it and then
  fails with a unique violation, after which the stored key is compared like
  a sequential retry. `OrderDb` uses read committed snapshot, so the lookup
  never blocks, and the losing transaction leaves no rows.

The normalized request is a fixed, versioned rendering of the validated
fields in request order: member order and whitespace of the body do not
change it; line order does, because it defines the line numbers.

Constraints in the database carry the invariants the code relies on: one
start request per order (its key is the order id), one order per idempotency
key, unique instance ids, contiguous status-history sequences, bounded line
numbers and quantities, allowed status values, and a consistent start-request
lifecycle (`Started` has a start time, `Closed` a close time, a lease has an
owner and an expiry).

The schema comes from EF Core migrations in `Fulfillment.OrderApi`
(`Persistence/Migrations`), applied by a one-shot `migrate` run of the order
API image (`order-db-migrate` in Compose) before any replica starts. The app
never migrates at start: replicas starting together must not race on DDL. It
checks that no migration is pending and refuses to start otherwise. The
runtime probe's `diagnostics` schema is separate and not part of the
migrations.

## Starting Workflows

Every order API replica runs a start dispatcher. Each poll (jittered around
one second) claims due `Pending` start requests in one transaction with
`UPDLOCK` and `READPAST`: concurrent dispatchers never claim the same row at
the same time and never wait for each other. A claim sets the lease owner, an
expiry, and a new **lease token**, and counts the attempt. In the same
transaction, requests whose order is already terminal are closed
(`OrderTerminal`) instead of started.

For each claimed request the dispatcher appends a row to
`WorkflowStartAttempt` (dispatcher, attempt, call time) and calls the
workflow host's management API for `FulfillOrderWorkflow` with the instance
id `fulfill-{orderId:N}`; it never uses its own sidecar's workflow API.

| Answer | Start request |
|---|---|
| scheduled, or the instance already exists (any status) | `Started` |
| no usable answer (transport errors or 5xx after the bounded retries) | stays `Pending`, released with exponential backoff (`StartAmbiguous`); the next attempt's state-first start reports an instance that the lost answer did create |
| refused (4xx, e.g. the management policy) | stays `Pending`, released with a long backoff (`StartRejected`) |
| the dispatcher dies | nothing is written; the lease expires and any replica claims the request again |

Every change after the claim is conditional on the lease token. A dispatcher
whose lease expired and was claimed by another (a hung call, a pause) changes
nothing when it comes back: its attempt ends `LeaseLost`. A slow start never
blocks the dispatcher: starts run concurrently, up to a bound, while it keeps
polling.

Two dispatchers can still both call the workflow host for one order (a lease
that expired during a slow call), and the host's state-first check is not
atomic. So the dispatcher is not what prevents duplicate execution; the
**execution claim** is: the first activity of every run, `ClaimExecution`
(on `order-api`), reads the order with an update lock, rejects the run when
the order is terminal (counted in `FenceRejections` and
`fence_rejections_total`), and otherwise counts the claim. Every claim is
audited in `ExecutionClaim`. More than one claim for a non-terminal order is
normal after a replay or a second start; effects are keyed, so they are not
repeated.

The fulfillment workflow is a minimal shell for now: it claims execution and
ends (`ShellClaimed`), or ends at once when the claim is rejected
(`AlreadyTerminal`). The fulfillment steps follow the claim.

`GET /system/orders/{orderId}/execution` (admin key) shows the start
request, its attempts, the claims, and the workflow's runtime state.

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

Access is limited to the calling apps and workflow types the workflow host
lists explicitly (today `order-api` and the runtime probe workflow). The
caller is the app id Dapr attaches to the invocation (`dapr-caller-app-id`);
other callers get `403`, unknown workflow types `400`. Replacing an existing
instance is allowed only for probe instances of the probe workflow.

The workflow host keeps no business logic and no database. Callers commit
their SQL record first and call this API with bounded retries. The
state-first check of a start is not atomic: two concurrent starts of the same
id can both see "no instance". Correctness therefore never rests on this API
but on the SQL start record, the execution claim, and keyed effects.

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
