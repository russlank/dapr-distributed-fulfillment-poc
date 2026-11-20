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
business state is an activity on the service that owns the data. Stock,
payment, and shipment are real (inventory service, payment service against
the external payment provider, shipping service against the external
carrier). Packing remains **simulated**: one `stub-` step per line that
changes nothing outside `OrderDb`.

| Step | Where | Does |
|---|---|---|
| `ClaimFulfillmentExecution` | order API | the execution claim (above); a terminal order ends the run |
| `ValidateFulfillmentOrder` | order API | `Submitted` → `Validating`, reads the lines, applies the business rules (today: one line per SKU, a deliberate limitation of this PoC's order model, not a requirement of Dapr or the inventory design); an order that breaks one is `Rejected` |
| `PriceOrder`, `AssessOrderRisk` | compute worker | independent checks, scheduled together and awaited together, so they run at the same time (on one or two compute replicas). Pricing uses a fixed stub price list (`stub-v1`); the risk rule (`stub-v2`) scores the order's size and requires a review from an order value of 1,000.00 |
| `RecordFulfillmentChecks` | order API | stores both results once (`OrderCheck`) and returns the **stored** risk result: the first committed result decides whether a review is required, whatever a repeated computation returned |
| review (if required) | order API, workflow | `CreateReviewTask`, `ResolveReview`, `ExpireOrEscalateReview` and the durable wait (see [Reviews and Signals](#reviews-and-signals)) |
| `BeginStubFulfillment` | order API | → `Reserving`: from `Validating` only if the stored result requires no review, from `AwaitingApproval` only if the review is `Approved`; no path skips a required review; a cancellation recorded before ends the order `Cancelled` instead |
| `ReserveOrderLine` | inventory service | one per line, in ordinal SKU order: a hold for the whole line in the first warehouse (by priority) that can serve it; `InsufficientStock` when none can |
| `ExecutePayment` (authorize) | payment service | authorizes the order's amount at the provider (see [Payment](#payment)) |
| `AdvanceFulfillment` | order API | `Reserving` → `PaymentAuthorized` → `Packing`; later `Dispatched` → `AwaitingDelivery` (only forward along this chain) |
| `ApplyStubFulfillmentStep` | order API | one simulated packing step per line, in line order (`stub-fulfil`, qualifier `line-N`) |
| `BeginDispatch` | order API | the cancellation point of no return (see [Cancellation](#cancellation)) |
| `ExtendOrderHolds` | inventory service | extends every hold once before the capture, never beyond the authorization's expiry; reports the remaining validity |
| `ExecutePayment` (capture) | payment service | captures the authorized amount, only with enough hold validity left |
| `ConsumeOrderHolds` | inventory service | turns all of the order's holds into consumed stock in one transaction, or none of them (`HoldExpired`, `Incomplete`) |
| `DispatchShipment` | shipping service | hands the shipment to the carrier (see [Shipping and the Carrier](#shipping-and-the-carrier)) |
| `GetShipmentStatus`, `PollCarrierStatus` | shipping service | Shipping's record of the carrier status (`PollCarrierStatus` asks the carrier once, at the delivery deadline) |
| `FinalizeDelivered` | order API | the terminal transaction on `Delivered` (below) |
| `OpenInterventionCase`, `ReadInterventionCase` | order API | a case and its durable wait (see [Intervention Cases](#intervention-cases)) |
| `ReleaseOrderHolds`, compensating `ExecutePayment`, `CloseUnfulfilledOrder` | inventory, payment, order API | only when the order cannot be fulfilled: release the holds, void or refund by key, then `Rejected`, `Compensated`, or `Cancelled` (below) |

Every order API step starts by locking the order row (`UPDLOCK`), so all
steps of one order, from any replica, retry, or repeated run, are
serialized. Each step is safe to repeat:

- **Status changes are conditional.** An update applies only if the order is
  still in the expected status, and the history row is appended in the same
  transaction with the next sequence number (the lock keeps the numbering
  contiguous; the unique `(OrderId, Sequence)` index is the backstop). A
  repeated validation finds `Validating` and changes nothing.
- **Effects are keyed.** Simulated effects (`StubEffect`), check results
  (`OrderCheck`), payment operations, inventory operations, and shipments are
  stored under their operation key (`{orderId:N}:{step}:{qualifier}`, for
  example `…:stub-fulfil:line-2`, `…:authorize:1`, `…:extend:1`). An
  execution that finds the key returns the stored result and only counts a
  replay. Simulated steps are recognisable everywhere: their names start with
  `stub-`, a check constraint enforces it, and they have their own table.
- **A terminal order ends the run.** Every step that finds the order
  terminal changes nothing and returns `AlreadyTerminal`; the workflow then
  ends without further steps.

The **terminal transaction** (`FinalizeDelivered`) moves the order
`AwaitingDelivery` → `Completed` with its history row (`Delivered`), writes
the `OrderCompleted` row of the order outbox, and closes the start request
(`Closed`, lease cleared), all or nothing. A rejection by validation closes
the request the same way. The workflow is deterministic: no clock, random
numbers, or I/O in its code; later steps work from earlier results in its
history (the validated lines, the authorization's expiry), and keys derive
from the order id and step. Activities are retried by the workflow on
failure (5 attempts, exponential backoff); every step tolerates it.

**An order that cannot be fulfilled** releases its holds first (closing the
order in the inventory), then compensates what it holds at the provider: a
void by the authorization's key, or a refund by the capture's key (a void
that finds the authorization captured is followed by a refund). A
compensation without a definite result is retried in rounds (same key); after
the last round a `CompensationExhausted` case waits for an operator. A
release that keeps failing opens the same kind of case. Once the dispatch
happened there is no compensation: an undeterminable dispatch, a delivery
exception, or an overdue delivery opens a case.

| Status | Set by |
|---|---|
| `Submitted` | acceptance |
| `Validating` | validation |
| `AwaitingApproval` | round 1 of a required review |
| `Reserving` | `BeginStubFulfillment` (no review required, or review approved) |
| `PaymentAuthorized`, `Packing` | `AdvanceFulfillment` after the authorization and before the packing steps |
| `Dispatched`, `AwaitingDelivery` | `AdvanceFulfillment` after the carrier accepted the shipment |
| `ManualInterventionRequired` | an open intervention case; the resolution puts the order back to the status it had |
| `Completed` | the terminal transaction on `Delivered` |
| `Rejected` | validation, a rejected review, the final expiry of a review, a line no warehouse can serve (`InsufficientStock`), or a declined authorization (`PaymentDeclined`) |
| `Compensated` | the order's holds were released and its payment voided or refunded: a reservation failed for good (`ReservationFailed`), a hold expired before it was consumed (`ReservationExpired`), the provider could not be reached (`PaymentProviderUnavailable`), the capture was refused (`PaymentCaptureFailed`), or a repair compensated it (`PaymentFailed`) |
| `Cancelled` | a customer cancellation recorded before the point of no return (`CancellationRequested`) |

## Payment

The payment provider is an external system (simulated, no sidecar, its own
database). The payment service calls it over HTTP; every operation carries
the caller's operation key, and the provider stores each key with a hash of
its payload: a repeat returns the stored result, the same key with another
payload is refused (`409`).

For each operation the payment service writes its intent row, and each
attempt row, **before** the request is sent. Then:

| What happened to the request | The payment service |
|---|---|
| answered (committed, declined, prohibited) | stores the result; done |
| no connection could be made (refused, name not resolved, connect timeout) | nothing was sent: resends with backoff; after its budget `Unavailable` |
| sent, but no answer (timeout, broken connection) | it may have committed: asks the provider's status for that key; `Committed` is adopted, `NotFound` is trusted only after the idempotency window (then the same key is sent again); without an answer the outcome is `Ambiguous` |

`Unavailable` compensates (nothing can have committed; the void by key still
records the key as prohibited). `Ambiguous` opens an `AmbiguousPayment` case:
the workflow waits, and after the resolution it asks the provider again
(`ReconcilePayment`); the provider's record wins over the operator's claim.

**Compensation by key.** A void references the authorization's key, a refund
the capture's key. In the provider's database, under locks on both keys, a
compensation against an operation that never committed is a committed no-op
(`NothingToVoid`, `NothingToRefund`) that leaves a **prohibition**: the
original key can no longer commit (a late request with it returns
`Prohibited`), and a capture is refused once its authorization was voided.
So a delayed original can never commit after its compensation.

**Before the capture** the holds are extended once (key `…:extend:1`): the
new end is the earlier of now plus the extension (30 minutes; drill 30 s)
and the authorization's expiry, and an extension never shortens a hold. The
capture starts only if the holds' remaining validity covers its own retry
and status-query window (2 minutes; drill 15 s); otherwise the authorization
is voided and the order compensated (`ReservationExpired`). The extension
does not guarantee the consumption, which checks expiry again in its own
transaction; a hold lost after the capture is refunded. Holds keep expiring
while an order waits in an intervention case; the continuation acts on what
the inventory then holds.

## Shipping and the Carrier

The carrier is external as well (simulated, own database, no sidecar).
`DispatchShipment` sends the shipment with the order's id as shipment id, so
every retry asks with the same key; the carrier stores the shipment and its
callback schedule before it answers. A request that may have reached the
carrier is resent with the same id; if the outcome stays unknown, the stock
is already consumed and the payment captured, so the workflow opens an
`AmbiguousDispatch` case rather than compensate.

**Callbacks** are signed (HMAC over timestamp and body) and arrive at the
order API (`POST /carrier/callbacks`): it checks the signature, then writes
the inbox row and a forward command in one transaction and answers `202`. A
repeated event (same id and payload) is counted; the same id with another
payload is refused (`409`) and audited; a bad signature gets `401`.

The **carrier forwarder** in every order API replica claims due forward
commands with a lease and calls Shipping by service invocation; Shipping
stores the outcome, and the forwarder records it with its consequence in one
`OrderDb` transaction:

| Shipping's outcome | Meaning | Consequence |
|---|---|---|
| `Applied` | a newer, non-terminal status | none |
| `TerminalApplied` | the first terminal status (`Delivered`, `Exception`) | a carrier signal for the waiting workflow (only while `AwaitingDelivery`) |
| `Stale`, `Duplicate`, `TerminalRepeat` | older version, version already recorded, the same terminal again | none |
| `TerminalConflict` | another terminal status after the first one | a `CarrierTerminalConflict` case (also after completion: the order stays `Completed`) |
| `CorrelationMismatch` | the callback names another order than the shipment's | recorded, nothing else |
| `UnknownShipment` | Shipping has no such shipment (yet) | retried; then an `AmbiguousDispatch` case |
| `PayloadConflict` | the event id is known with another payload | audited |

Shipping's **current-state rule**: versions only move forward, and the first
terminal status stays. Every forwarded event is kept with its outcome (one
row per event id; one per shipment and version, except mismatches). The
workflow never trusts the signal: woken by it, or by the delivery deadline
(then after one poll of the carrier), it reads Shipping's record.

## Cancellation

`POST /orders/{id}/cancel` (customer key) records a cancellation request; the
order ends `Cancelled` at the fulfillment's next check, which releases the
holds and voids an authorization by key. `BeginDispatch` is the **point of
no return**: it records the dispatch start only if no cancellation was
recorded, and the cancellation is recorded only if the dispatch has not
started. Both lock the order row, and a check constraint refuses a row with
both. Answers: `202` recorded, `200` already requested (or already
`Cancelled`), `409 dispatch-started` once the dispatch started (checked
first), `409 order-terminal` for another terminal order, `404`. A
cancellation during a review wakes the waiting workflow; the round becomes
`Cancelled`, and a later decision gets `409 review-cancelled`.

## Intervention Cases

A case (`InterventionCase`: one open case per order and kind) is opened by the
workflow when it cannot decide safely: `AmbiguousPayment`,
`CompensationExhausted`, `AmbiguousDispatch`, `DeliveryException`,
`DeliveryOverdue`, `CarrierTerminalConflict`. A non-terminal order waits in
`ManualInterventionRequired`; the workflow waits for the resolution's signal
and re-reads the case periodically (a lost signal only delays).

An operator resolves it with `POST /interventions/{id}/resolution` (operator
key; a resolution id makes a repeat a replay; the action must suit the kind:
`ConfirmExternalSuccess`, `ConfirmExternalFailure`, `Acknowledge`). One
transaction resolves the case and takes exactly one path:

| Path | When | What follows |
|---|---|---|
| `Signal` | the original workflow is running (asked of the workflow host) | the order goes back to its status, and a pending signal wakes the workflow, which re-reads the case and continues (after a payment case it first reconciles with the provider) |
| `Repair` | no workflow waits (terminated, failed, missing) | a repair start request; a repair dispatcher starts `RepairOrderWorkflow` (`repair-{order}-{case}`): for payment cases it releases the holds, voids with its own repair key (refunds if the authorization was captured), ends the order `Compensated`, and records the outcome on the case |
| `RecordOnly` | the order is terminal | the resolution is recorded; nothing else |

If the workflow host cannot be asked, nothing changes and the operator is told
to retry (`503 workflow-state-unknown`). Repair keys differ from the original
keys, so a repair never repeats an original effect; voiding by the
authorization's key also prohibits a late original.

## Inventory

The inventory service owns `InventoryDb` (schema `inventory`, its own
migrations, applied by the one-shot `inventory-db-migrate` job; the service
refuses to start on an older schema). Stock never leaves SQL: actors and
workflows coordinate, SQL decides.

| Table | Holds |
|---|---|
| `Warehouse` | `W1`, `W2`, `W3` with an allocation priority |
| `StockItem` | per warehouse and SKU: `OnHand`, `Reserved`; check constraints `OnHand >= 0` and `0 <= Reserved <= OnHand`; `rowversion` |
| `Hold` | one per order line: `Held` → `Released`, `Expired`, or `Consumed`, once (a check constraint ties the state to its closing columns); unique `(OrderId, LineNumber)`; `ExpiresAt` by SQL time |
| `ProcessedOperation` | the stored result of every operation key (reserve per line and warehouse, release per hold, consume per order) and how often it was replayed |
| `Outbox` | one event per hold transition (`HoldCreated`, `HoldReleased`, `HoldExpired`, `HoldConsumed`), sequence gapless per order; written in the same transaction, not yet published |
| `OrderClosure` | an order closed for reservations (released or consumed); a late reservation for it returns `OrderClosed` |
| `SweeperLease` | the single lease of the hold sweeper |

**Every change is guarded SQL.** Each operation runs in one transaction
that first takes an exclusive application lock for the order
(`sp_getapplock`), then reads its operation key's stored result (a repeat
returns it and counts a replay), and then changes stock with conditional
updates: a reservation succeeds only where `OnHand - Reserved >= quantity`,
a release or expiry only for a hold still `Held`, a consumption only when
every hold of the order is `Held` and unexpired and their number matches the
order's lines. Deadlocks are retried. The guarantees therefore do not depend
on the actors: with `INVENTORY_COORDINATION=SqlOnly` the same activities call
SQL directly from any replica, and S03 passes in both modes.

**Stock actors** (`StockActor`, id `{warehouse}|{sku}`, one per stock row,
non-reentrant, no actor state) serialize reservations and releases of one
stock row, so contention on a hot SKU queues in the actor instead of in SQL
lock waits. Dapr Placement assigns each actor to one inventory replica; when
that replica dies, the actor activates on another one.

**Allocation** is whole-line: a line is reserved in the first warehouse by
priority that can serve all of it; lines are not split across warehouses. A
line no warehouse can serve rejects the order (`InsufficientStock`) and the
holds of its earlier lines are released.

**Expiry is the inventory's own.** A hold lives `HoldTtl` (30 minutes;
`INVENTORY_HOLD_TTL`) by SQL time. The hold sweeper on every inventory
replica takes the SQL lease (one sweeper at a time), finds due holds, and
expires each under the order's lock; it uses neither the Dapr Scheduler nor
actor reminders, so holds expire while the Scheduler or Placement is down.
Consumption checks the expiry in the same transaction, so a hold that
expired is never consumed, whoever came first.

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
| `Approved` | `BeginStubFulfillment` (`AwaitingApproval` → `Reserving`) and the fulfillment |
| `Rejected` | `ResolveReview` already moved the order to `Rejected` (`ReviewRejected`) and closed the start request; the run ends |
| `Pending`, SQL time before the deadline | waits again (an early timer or a stray signal changes nothing); the next timer is never in the past |
| `Pending`, deadline reached | `ExpireOrEscalateReview`: the only step that takes a review out of `Pending` by time (`Expired`, conditional on SQL time). It opens the next round (fresh deadline) or, after the last round, rejects the order (`ReviewExpired`) and closes the start request. An approval committed before the deadline is never expired |

An operator can **escalate** a pending round before its deadline
(`POST /reviews/{id}/escalate`): in one transaction the round becomes
`Escalated`, the next round opens with a fresh deadline, and a pending signal
wakes the workflow, which follows an escalated round like an expired one. A
repeat returns the same next round; the last round cannot be escalated
(`409 no-further-round`); a decision for an escalated round is superseded. A
cancellation during the wait ends the round `Cancelled` (see
[Cancellation](#cancellation)). The review wait never assumes an approval
and never abandons a review without the expiry protocol.

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
