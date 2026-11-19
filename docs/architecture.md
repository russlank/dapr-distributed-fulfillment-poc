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

Every change after the claim is conditional on the lease token. When such an
update changes nothing, the dispatcher reads the request in the same batch to
say why, and the attempt records it:

| Found | Attempt result | Meaning |
|---|---|---|
| a later claim than its own (the claim counter moved on) | `LeaseLost` | its lease expired and another dispatcher took the request over (a hung call, a pause) |
| no later claim, request `Closed` | `AlreadyClosed` | the order became terminal while the start was in flight: a fast workflow can complete the order, and its terminal transaction close the request, before the start answer arrives |
| anything else (no later claim, not `Closed`, token gone) | `Unexpected` | no dispatcher or terminal transaction makes this change; logged as an error |

In every case the request is left as it is: a closed request is never
reopened or marked `Started` afterwards, so the terminal state stands. A
slow start never blocks the dispatcher: starts run concurrently, up to a
bound, while it keeps polling.

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

`GET /system/orders/{orderId}/execution` (admin key) shows the order's
status and history, its simulated effects and check results, the start
request, its attempts, the claims, and the workflow's runtime state.

## Fulfillment Workflow

`FulfillOrderWorkflow` runs on the workflow host; every step that touches
business state is an activity on the service that owns the data. In this
version the fulfillment itself is **simulated**: reservation, payment, and
shipment do not exist yet, and nothing outside `OrderDb` is changed.

| Step | Where | Does |
|---|---|---|
| `ClaimFulfillmentExecution` | order API | the execution claim (above); a terminal order ends the run |
| `ValidateFulfillmentOrder` | order API | `Submitted` → `Validating`, reads the lines, applies the business rules (today: one line per SKU, a deliberate limitation of this PoC's order model, not a requirement of Dapr or the inventory design); an order that breaks one is `Rejected` |
| `PriceOrder`, `AssessOrderRisk` | compute worker | independent checks, scheduled together and awaited together, so they run at the same time (on one or two compute replicas). Pricing uses a fixed stub price list (`stub-v1`); the risk rule (`stub-v2`) scores the order's size and requires a review from an order value of 1,000.00 |
| `RecordFulfillmentChecks` | order API | stores both results once (`OrderCheck`) and returns the **stored** risk result: the first committed result decides whether a review is required, whatever a repeated computation returned |
| review (if required) | order API, workflow | `CreateReviewTask`, `ResolveReview`, `ExpireOrEscalateReview` and the durable wait (see [Reviews and Signals](#reviews-and-signals)) |
| `BeginStubFulfillment` | order API | → `Reserving`: from `Validating` only if the stored result requires no review, from `AwaitingApproval` only if the review is `Approved`; no path skips a required review |
| `ApplyStubFulfillmentStep` | order API | one simulated step per line, in line order (`stub-fulfil`, qualifier `line-N`) |
| `CompleteStubFulfillment` | order API | the terminal transaction (below) |

Every order API step starts by locking the order row (`UPDLOCK`), so all
steps of one order, from any replica, retry, or repeated run, are
serialized. Each step is safe to repeat:

- **Status changes are conditional.** An update applies only if the order is
  still in the expected status, and the history row is appended in the same
  transaction with the next sequence number (the lock keeps the numbering
  contiguous; the unique `(OrderId, Sequence)` index is the backstop). A
  repeated validation finds `Validating` and changes nothing.
- **Effects are keyed.** Simulated effects (`StubEffect`) and check results
  (`OrderCheck`) are stored under their operation key
  (`{orderId:N}:{step}:{qualifier}`, for example `…:stub-fulfil:line-2`,
  `…:check:price`). The key is the primary key, so an effect exists at most
  once; an execution that finds it returns the stored result and only counts
  a replay (`Replays`). Simulated steps are recognisable everywhere: their
  names start with `stub-`, a check constraint enforces it, and they have
  their own table.
- **A terminal order ends the run.** Every step that finds the order
  terminal changes nothing and returns `AlreadyTerminal`; the workflow then
  ends without further steps.

The **terminal transaction** inserts the completion effect
(`…:stub-complete:1`), moves the order `Reserving` → `Completed` with its
history row (`StubFulfillmentCompleted`), and closes the start request
(`Closed`, lease cleared), all or nothing. A rejection by validation closes
the request the same way. The workflow is deterministic: no clock, random
numbers, or I/O in its code; later steps work from earlier results in its
history (the validated lines), and keys derive from the order id and step.
Activities are retried by the workflow on failure (5 attempts, exponential
backoff); every step tolerates it.

| Status | Set by |
|---|---|
| `Submitted` | acceptance |
| `Validating` | validation |
| `AwaitingApproval` | round 1 of a required review |
| `Reserving` | `BeginStubFulfillment` (no review required, or review approved); the simulated steps run here. The statuses between `Reserving` and `Completed` belong to the real steps and are not used yet |
| `Completed` | the terminal transaction |
| `Rejected` | validation, a rejected review, or the final expiry of a review |

## Reviews and Signals

SQL decides; workflows wait. A review round is a `ReviewTask` row, unique per
order, purpose, and round, with a deadline in SQL time (time profile: 10
minutes and 2 rounds by default, 20 seconds in the drill environment; timer
grace 5 s or 2 s).

**Decisions** (`POST /reviews/{reviewId}/decision`, operator role key
`OPERATOR_API_KEY`, configured simulated operator ids) are one transaction:
lock the review; a known decision id is a retry (same payload hash: `200`
with `replayed: true`; anything else: `422 decision-id-reused`); otherwise
the review must be `Pending`, and the conditional update
`Pending → Approved|Rejected` also requires `SYSUTCDATETIME() < DeadlineAt`,
so a deadline has exactly one answer, the database clock's. The same
transaction writes the immutable `ReviewDecision` (operator, payload hash;
the database refuses updates; at most one per review) and a `PendingSignal`.
Concurrent operators serialize on the review row: one commits, the rest get
`409 review-already-decided`. Late decisions get `409 review-expired`, decisions
for a round that a newer round replaced `409 review-superseded`. The order's
status is not changed by the decision.

**Waiting.** The workflow reads the review (`ResolveReview`, which records
each wake-up and its reason in `ReviewWake`) before its first wait and after
every wake-up, and waits on `WhenAny(review signal, timer at deadline +
grace)`. Whatever Dapr delivers first, SQL decides:

| SQL says | The workflow |
|---|---|
| `Approved` | `BeginStubFulfillment` (`AwaitingApproval` → `Reserving`) and the simulated steps |
| `Rejected` | `ResolveReview` already moved the order to `Rejected` (`ReviewRejected`) and closed the start request; the run ends |
| `Pending`, SQL time before the deadline | waits again (an early timer or a stray signal changes nothing); the next timer is never in the past |
| `Pending`, deadline reached | `ExpireOrEscalateReview`: the only step that takes a review out of `Pending` by time (`Expired`, conditional on SQL time). It opens the next round (fresh deadline) or, after the last round, rejects the order (`ReviewExpired`) and closes the start request. An approval committed before the deadline is never expired |

Manual escalation, cancellation, and operational intervention cases are not
built yet; the review wait never assumes an approval and never abandons a
review without the expiry protocol.

**Signals** are wake-up hints only; the fact is in SQL before the signal row
exists. A signal dispatcher in every order API replica claims due signals
with a lease and token (like the start dispatcher), records each attempt
(with the invocation's HTTP status, retries, and error), and raises the event
through the workflow host's management API:

| Situation | Signal |
|---|---|
| the workflow was not started yet | stays `Pending`, retried later |
| instance running: raised | `Forwarded` (only now; an attempt alone never marks it delivered) |
| instance terminal | `Discarded` (`WorkflowTerminal`); the decision stays in SQL and was applied by the workflow's own read, or the order is terminal for another reason |
| instance missing although started | `Stuck` (`WorkflowMissing`), for the reconciler |
| no usable answer, not running | retried with exponential backoff (1 s to 30 s) |

A crash after the raise and before the mark leaves the lease to expire; the
signal is raised again, which is harmless (a duplicate wake-up reads SQL
again; raising to a completed instance changes nothing). Events raised
before the workflow waits are buffered by the runtime.

Workflow code awaits only workflow tasks. In particular, the wait cancels
its losing branch with the synchronous `Cancel()`: `CancelAsync` resumes the
code off the orchestration thread and broke the run (see
[journal.md](journal.md)).

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
lists explicitly (today `order-api`, with the runtime probe and the
fulfillment workflow). The
caller is the app id Dapr attaches to the invocation (`dapr-caller-app-id`);
other callers get `403`, unknown workflow types `400`. Replacing an existing
instance is allowed only for probe instances of the probe workflow.

Drill builds add one control under `/internal/drill` (app token, drill
environment only, compiled out of release images): it schedules
`fulfill-{orderId:N}` directly through the Dapr workflow API, without the
existence check, to test the execution fence (scenario S49).

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
| Toxiproxy | path from the sidecars to `DaprStateDb`; a client path to each order API replica (`127.0.0.1:18080` → `order-api`, `:18081` → `order-api-2`) | drills cut only the state-store path; the replica scenarios delay or drop client traffic on the client path (the direct ports are unaffected) |
| Dapr Placement | actor placement | |
| Dapr Scheduler | durable timers and reminders | persistent volume; reset together with SQL data (lost timers are lost, see [journal.md](journal.md)) |
| RabbitMQ | pub/sub for business events | used from the event-distribution milestone |
| OpenTelemetry Collector, Jaeger, Prometheus, Grafana | traces, metrics | see [monitoring.md](monitoring.md) |

Workflow concurrency limits are sidecar settings
(`dapr/config/fulfillment.yaml`, `spec.workflow`); the .NET SDK's own
options are obsolete and have no effect in 1.19.
