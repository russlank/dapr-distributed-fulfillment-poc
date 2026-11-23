# Operations

How to run, inspect, and repair the stack: the scripts, health and startup,
what to check for each part of the process, the alert runbooks, rolling out
a workflow change, restarting replicas, and troubleshooting. The settings
are in [configuration.md](configuration.md); the design behind them is in
[architecture.md](architecture.md).

## Contents

- [Scripts](#scripts)
- [Health](#health)
- [Startup Order](#startup-order)
- [Workflow Starts](#workflow-starts)
- [Reviews](#reviews)
- [Inventory](#inventory)
- [Payment, Shipment, Cancellation](#payment-shipment-cancellation)
- [Intervention Cases](#intervention-cases)
- [Events and the Timeline](#events-and-the-timeline)
- [Alerts](#alerts)
- [Rolling Out a Workflow Change](#rolling-out-a-workflow-change)
  - [Never Terminate a Patched Workflow](#never-terminate-a-patched-workflow)
- [Restarting a Replica](#restarting-a-replica)
- [Reset](#reset)
- [Troubleshooting](#troubleshooting)

## Scripts

| Script | Does |
|---|---|
| `scripts/init-env.sh` | writes `.env` with random secrets if missing |
| `scripts/stack-up.sh [--replicas] [--scale] [--drill] [--no-build]` | builds, starts, waits for the one-shot jobs, then for every service's readiness; prints identities |
| `scripts/stack-down.sh` | stops the stack, keeps data |
| `scripts/reset.sh --yes` | stops the stack and deletes all data volumes |
| `scripts/scenarios/S00-runtime-smoke.sh` | runtime smoke checks (see [testing.md](testing.md)) |
| `scripts/scenarios/S02-parallel-checks.sh` | the compute checks of an order run concurrently |
| `scripts/scenarios/S04-duplicate-submission.sh`, `S24-client-timeout-retry.sh`, `S43-idempotency-conflicts.sh` | replica scenarios (`--replicas`): concurrent duplicates, client timeouts with retries (Toxiproxy client path), idempotency conflicts |
| `scripts/scenarios/S23-deterministic-client-replay.sh` | the client simulator's seeded run, replay, and operator commands (see [client-simulator.md](client-simulator.md)) |
| `scripts/scenarios/S49-execution-fence.sh`, `S51-concurrent-start.sh` | execution fence and concurrent starts (drill stack) |
| `scripts/checks/*.sh` | order acceptance, observability, fulfillment recovery (drill stack) |
| `scripts/drills/*.sh` | resilience drills, drill stack only |

## Health

Every service has `GET /health/live` (process up) and `GET /health/ready`
(process up, local sidecar healthy where it has one, its own database
reachable), and `GET /system/component-identity` (component, replica,
version, commit, tree state, branch, build flavor, failpoints, environment).
Container health checks use the live endpoint; `stack-up.sh` waits for ready.

## Startup Order

`sqlserver` (healthy) → `db-init` (databases and logins; must exit 0) and
`scheduler-volume-init` (volume ownership; must exit 0) → `order-db-migrate`
(OrderDb migrations; must exit 0; the order API refuses to start while a
migration is pending) → control plane
(Placement, Scheduler), RabbitMQ, Toxiproxy, telemetry → apps → their
sidecars. `docker compose up --wait` cannot be used alone because it fails
as soon as a one-shot job exits, even with code 0; `stack-up.sh` waits for
each job explicitly.

## Workflow Starts

Accepted orders are started by the order API's start dispatcher. While the
workflow host is unreachable, start requests stay `Pending` and are retried
with backoff; nothing is lost. To look at one order:

```bash
. ./.env
curl -s http://127.0.0.1:8080/system/orders/<orderId>/execution -H "X-Api-Key: $ADMIN_API_KEY"
```

`LastErrorCode` on the start request names the last problem
(`StartAmbiguous`, `StartRejected`, `OrderTerminal`); the attempts list shows
every call with its dispatcher and result. A request is `Closed` by the
transaction that makes its order terminal; `Started` may never be seen for a
fast order (the attempt then ends `AlreadyClosed`). `OrderTerminal` on a
closed request means the dispatcher found a pending request of an order that
was already terminal and closed it without starting anything.

## Reviews

Orders from 1,000.00 wait in `AwaitingApproval` for an operator decision:

```bash
. ./.env
curl -s "http://127.0.0.1:8080/reviews?status=Pending" -H "X-Api-Key: $OPERATOR_API_KEY"
curl -s -X POST http://127.0.0.1:8080/reviews/<reviewId>/decision -H "X-Api-Key: $OPERATOR_API_KEY" \
  -H 'content-type: application/json' -d '{"decisionId":"<new GUID>","operatorId":"operator-1","outcome":"approve"}'
```

Or with the client simulator: `approve --order <orderId>` (or `--from <result.json>`), see
[client-simulator.md](client-simulator.md).

Retry a decision with the same `decisionId` and payload; a new `decisionId`
for a decided review gets `409`. The execution view shows the review rounds,
the decision, the workflow's wake-ups, and the signal with every delivery
attempt (HTTP status, invoker calls, error). A signal stays `Pending` while
the workflow host cannot be reached and is retried with backoff (up to 30 s
between attempts); the decision itself is already effective, and the
workflow also reads it when its timer fires. After every workflow-host
replica restarted, service invocation failed for a few seconds after
readiness (measured: up to about 4 s); delivery then waits for the next retry.

## Inventory

Stock, holds, and the inventory's stored results are in `InventoryDb`; the
order's execution view does not show them yet. Inspect them with SQL, for
example with the `sql` helper of `scripts/lib.sh`:

```bash
. scripts/lib.sh
sql InventoryDb "SELECT WarehouseId, Sku, OnHand, Reserved FROM inventory.StockItem WHERE Sku = 'SKU-1'"
sql InventoryDb "SELECT LineNumber, WarehouseId, Sku, Quantity, State, ExpiresAt, ClosedBy FROM inventory.Hold WHERE OrderId = '<orderId>'"
sql InventoryDb "SELECT OperationKey, Outcome, ProcessedBy, Replays FROM inventory.ProcessedOperation WHERE OrderId = '<orderId>'"
```

- `Reserved` always equals the units of the row's `Held` holds; a hold leaves
  `Held` once (`Released`, `Expired`, `Consumed`) with one outbox event.
- The hold sweeper runs on every inventory replica; one at a time holds the
  SQL lease (`inventory.SweeperLease`). It expires holds by SQL time without
  the Dapr Scheduler or Placement; `ClosedBy` names `sweeper:<replica>`.
- An order `Compensated` with `ReservationExpired` waited longer than the hold
  lifetime (`INVENTORY_HOLD_TTL`, default 30 minutes) between reservation
  and consumption, for example while the inventory was down.
- `INVENTORY_COORDINATION=SqlOnly` (recreate the inventory replicas) runs the
  same guarded SQL without stock actors, for comparison; the guarantees are
  the same.
- Each stock actor logs `stock actor <warehouse>|<sku> activated on <replica>`
  where it runs; after a replica loss it activates on another one.

A round can be escalated before its deadline (`POST /reviews/<reviewId>/escalate`
with `{"operatorId": ..., "reason": ...}`, or the simulator's `escalate`); the
next round opens at once.

## Payment, Shipment, Cancellation

- Payment operations and every attempt (send or status query, with its
  answer) are in `PaymentDb` (`payment.PaymentOperation`,
  `payment.PaymentAttempt`); the provider's own record is in
  `PaymentProviderSimDb` (`provider.Operation`, with `provider.Prohibition`
  for keys compensated before they committed).
- An order `Compensated` with `PaymentProviderUnavailable` never reached the
  provider (every send refused); its void by key still prohibits a late
  authorization.
- Shipments and every carrier event Shipping received, with its outcome, are
  in `ShippingDb` (`shipping.Shipment`, `shipping.CarrierStatusEvent`); the
  callbacks as received and their forwarding state in `OrderDb`
  (`orders.CarrierCallbackInbox`, `orders.CarrierStatusForward`,
  `orders.CarrierCallbackConflict`).
- A forward row that stays `Pending` with a `LastError` means Shipping could
  not be reached or did not know the shipment yet; it is retried with backoff
  and becomes an `AmbiguousDispatch` case after its retries.
- A customer cancels with `POST /orders/<orderId>/cancel` (customer key).
  `409 dispatch-started` means the shipment is already on its way.

## Intervention Cases

An order in `ManualInterventionRequired` waits for an operator:

```bash
. scripts/lib.sh
sql OrderDb "SELECT CaseId, Kind, State, OperationKey, Evidence, OpenedAt FROM orders.InterventionCase WHERE OrderId = '<orderId>'"
curl -s -X POST http://127.0.0.1:8080/interventions/<caseId>/resolution -H "X-Api-Key: $OPERATOR_API_KEY" -H 'content-type: application/json' \
  -d '{"resolutionId":"<new GUID>","operatorId":"operator-1","action":"ConfirmExternalSuccess","evidence":"provider record: Committed"}'
```

Or with the client simulator: `resolve --case <caseId> --action ... --evidence ...`.

| Kind | Check first | Then |
|---|---|---|
| `AmbiguousPayment` | the provider's record for the operation key in the case | `ConfirmExternalSuccess` (it committed) or `ConfirmExternalFailure` (it did not); the workflow reads the provider again either way and acts on its record |
| `CompensationExhausted` | why the void, refund, or release failed (the attempts) | fix the cause, then `ConfirmExternalFailure` to compensate again, or `ConfirmExternalSuccess`/`Acknowledge` if it was done outside |
| `AmbiguousDispatch` | the carrier's record for the shipment id (= order id) | `ConfirmExternalSuccess` dispatches again with the same id (a replay at the carrier) |
| `DeliveryException`, `DeliveryOverdue` | the carrier status | `ConfirmExternalSuccess` completes the order; `ConfirmExternalFailure` (the delivery failed) ends it `Escalated` and keeps the case open as `Escalated`; while the outcome is uncertain, leave the case open (it cannot be acknowledged) |
| an `Escalated` case | Shipping's and the provider's records; what was settled outside the system (refund, reshipment, loss) | `Acknowledge` with that settlement as evidence (record only); the order stays `Escalated` |
| `CarrierTerminalConflict` | Shipping's history for the shipment | `Acknowledge` (record only) |

The answer names the path: `Signal` (the workflow continues), `Repair` (a
repair workflow compensates; its outcome is written to the case's
`RepairOutcome`), `Escalate` (a confirmed delivery failure), or `RecordOnly`.
Escalated cases waiting for their business resolution:
`sql OrderDb "SELECT CaseId, OrderId, Kind, EscalatedBy, EscalatedAt, EscalationEvidence FROM orders.InterventionCase WHERE State = 'Escalated'"`. `503 workflow-state-unknown` changed
nothing: retry the same request.

## Events and the Timeline

Every business event is published from its source's outbox and projected by
the order API (see [architecture.md](architecture.md#events-outbox-distribution-projection)).

```bash
. ./.env
# The order's projection: per source the last applied sequence, pending events, missing sequences, fields
curl -s http://127.0.0.1:8080/system/orders/<orderId>/timeline -H "X-Api-Key: $ADMIN_API_KEY"
# Publish an order's events of one source again from a sequence (consumers treat repeats as duplicates)
curl -s -X POST http://127.0.0.1:8080/system/outbox/inventory/replay -H "X-Api-Key: $ADMIN_API_KEY" \
  -H 'content-type: application/json' -d '{"orderId":"<orderId>","fromSequence":1}'
bash scripts/ops/rebuild-timeline.sh     # rebuild current state from history; prints both checksums
```

What to look at in SQL:

| Question | Query |
|---|---|
| backlog of a source | `SELECT State, Stalled, COUNT(*), MAX(Attempts) FROM inventory.Outbox GROUP BY State, Stalled` (likewise `orders.OrderOutbox`, `payment.Outbox`, `shipping.Outbox`) |
| open gaps | `SELECT * FROM orders.OrderEventGaps` |
| dead letters | `SELECT Reason, COUNT(*) FROM orders.EventDeadLetter GROUP BY Reason` (ids and reasons only) |
| repeated deliveries | `SELECT EventId, Duplicates FROM orders.OrderEventHistory WHERE Duplicates > 0` |

A `Stalled` row is still retried; it means the broker has refused it many
times (broker down, component misconfigured). Rows are never deleted, also not
after publication.

The reconciler (one order API replica at a time) opens `WorkflowFaulted`,
`WorkflowMissing`, and `ExecutionMismatch` cases for orders whose workflow
failed, vanished, or completed without finalizing; resolve them like other
cases (`ConfirmExternalFailure` compensates before the point of no return,
`ConfirmExternalSuccess` of a mismatch finalizes a delivered order,
`Acknowledge` records only). It also nudges a review wait whose timer was lost,
and replays an order's events when its projection lags; a `ProjectionGap` case
means the source no longer has the missing event (the projection for that order
and source stays at its last contiguous state).

## Alerts

Alertmanager (http://127.0.0.1:9093) groups the firing alerts; the rules are in
`monitoring/prometheus/alerts.yml`, the dashboards in Grafana (folder
"Fulfillment"). Thresholds per time profile are in the metric descriptions
([monitoring.md](monitoring.md)).

| Alert | Meaning | Check first | Act |
|---|---|---|---|
| `StartRequestBacklogAging` | accepted orders wait for their workflow start | workflow host replicas and sidecars; `orders.WorkflowStartRequest` (`LastErrorCode`, `Attempts`) | restore the workflow host; the dispatcher retries by itself |
| `PendingSignalAging` | wake-up signals are not delivered | workflow hosts; `orders.SignalAttempt` results | restore the workflow host; facts are in SQL, signals only wake |
| `ReviewSignalBacklog` | review decisions do not reach their workflows | as above, for review signals | as above; the reconciler nudges overdue review waits |
| `CarrierForwardBacklogAging` | accepted carrier callbacks are not forwarded | Shipping replicas; `orders.CarrierStatusForward` (`LastError`) | restore Shipping; the forwarder retries |
| `OutboxAging` | events wait for publication | RabbitMQ and the owner's sidecar; dashboard (d) | restore the broker; rows stay `Pending` and drain |
| `OutboxStalled` | rows keep failing (still retried) | the row's `LastError` in the owner's outbox | fix the cause; nothing is lost, the relay keeps retrying |
| `DeadLetterNotEmpty` | events were dead-lettered | `orders.EventDeadLetter` (reason) | a conflict or invalid event is a producer defect; `DeliveryFailed` means the consumer failed until the broker gave up: replay from the outbox after the fix |
| `InterventionCaseOpen` | cases wait for an operator (`Escalated`: a business resolution) | [Intervention Cases](#intervention-cases) | resolve with evidence |
| `WorkflowFaulted` | a workflow failed while its order was not terminal | the case's evidence; the workflow host's status | resolve the `WorkflowFaulted` case (repair) |
| `ExecutionMismatch` | a workflow is missing or contradicts its order | the case; Shipping's record for a mismatch after delivery | resolve the case (repair or finalize) |
| `OrderProgressStalled` | orders have not moved past the stage threshold | the reconciler's log; the order's workflow | usually a host or store outage: restore it; a case follows if the workflow is gone |
| `CarrierCallbackDeadLettered` | the carrier gave up on callbacks | ingress reachability; the carrier's send log | none needed for the order: the delivery deadline poll completes it, or a `DeliveryOverdue` case follows |
| `CarrierContractViolation` | callbacks refused as invalid or unauthorized | the shared secret and payloads at the carrier | fix the configuration; refused callbacks change nothing |
| `ProjectionSequenceGap` | timeline streams wait for a missing event | `orders.OrderEventGaps` | the reconciler replays from the source after the gap threshold |
| `ProjectionGapUnresolvable` | a gap the source cannot fill (a `ProjectionGap` case) | the case and the source's outbox | acknowledge after checking; the timeline is a read model |
| `PaymentProviderUnavailable` | provider requests fail or go unanswered | provider reachability; `payment.PaymentAttempt` | restore the provider; orders compensate or open `AmbiguousPayment` cases |
| `ReservationHoldsLeaking` | holds stay `Held` past their expiry | `inventory.SweeperLease` (owner, expiry); inventory replicas | restore the sweeper (a stuck lease expires by SQL time); holds are then expired and stock returned |

## Rolling Out a Workflow Change

1. Deploy the new workflow host build with the change behind a new patch, held
   back on every replica: `WORKFLOW_PATCHES_DISABLED=<patch>`.
2. Enable it replica by replica (restart each workflow host and its sidecar
   without the variable); check `/system/workflow-patches` on each.
3. New runs take the new path; instances that started before keep theirs.

To roll back, hold the patch back again: runs that already recorded it are
reported `Stalled` and wait until it is enabled again (they are never replayed
on the old path).

### Never Terminate a Patched Workflow

A patched instance is a workflow instance whose history recorded a patch
marker. With `execution-version-2` enabled, that is every order started on
the current build (its start request shows `ExecutionVersion` 2).

On Dapr 1.19.1, terminating or purging a patched instance never completes, and
the instance stays `Stalled` for good, even after host and sidecar restarts
(see the journal). Instances without a patch marker terminate normally.

So never end a running order by terminating or purging its workflow.

**The alternative: let the workflow end itself.** SQL Server owns the
order's state; the workflow only carries it forward. At every step the
workflow re-reads the order, and when the order is already final it records
nothing more and completes as `Fenced` (runtime status `Completed`). So to
stop an order, make its outcome final through the order's own paths, and the
workflow finishes on its next step or wake-up:

| Goal | Path | What the workflow does |
|---|---|---|
| stop an order before dispatch | cancel it through the API | releases the holds, voids the authorization by key, completes `Cancelled` |
| end an order that waits for a decision | resolve its review or its case (`Signal`, `ConfirmExternalSuccess`, `ConfirmExternalFailure`) | continues or compensates along the resolved path |
| replace a failed or missing workflow | resolve the reconciler's case with `Repair` | a repair workflow with its own instance id and keys finishes the order; an old instance that wakes later is fenced |
| an order whose stored deadline passed without progress (a lost timer) | the reconciler sends a `Nudge` signal | re-evaluates SQL and continues or fences |

This is the same mechanism that makes a stray or restarted instance harmless
(the execution fence), so it needs no runtime operation that the defect
affects.

A workflow reported `Stalled` has one of two causes:

| Cause | What to do |
|---|---|
| a host runs without a patch that the instance recorded | enable the patch on every replica (roll forward); the instance continues |
| the instance was terminated or purged after it recorded a patch | the instance cannot be resumed. It cannot run, so it causes no effects, but the order is stuck: the reconciler treats `Stalled` as running (`OrderProgressStalled` fires, no case opens). See below. |

**If a patched instance was terminated anyway.** There is no supported
recovery path in this version. Two options exist, neither demonstrated here:

- Dapr's force purge (`dapr workflow purge <instance id> --force`, Dapr CLI
  1.19) deletes an instance's state whatever its status. If it works on such
  an instance, the order becomes "workflow missing", which the reconciler
  already detects and repairs through a `WorkflowMissing` case. Dapr warns
  that a force purge runs outside the workflow state machine; use it only
  when no worker processes the instance, and test it first.
- A reconciler extension: classify an instance as faulted when it stays
  `Stalled` while every workflow host has all patches enabled, and open a
  repair case. Not implemented.

Both are candidates for a future version, together with a Dapr upgrade that
fixes the defect: the Dapr 1.19.2 release notes (19 November 2026) list no
fix for it.

## Restarting a Replica

A replica is an app container plus a sidecar that joins the app's network
namespace. When the app container is restarted or recreated, its sidecar
**must be recreated** too: an old sidecar keeps running in the dead
namespace, its DNS lookups fail, and the app gets "connection refused" from
`127.0.0.1:3500`. Restart the app first, then
`docker compose up -d --no-deps --force-recreate <app>-dapr`. The helpers in
`scripts/lib.sh` (`start_pair`, `restart_pair`) do this and wait for
readiness.

## Reset

SQL data (business databases and Dapr workflow history) and the Scheduler
volume (durable timers and reminders) are always removed together. Removing
only one leaves workflows whose history or timers no longer exist; a lost
Scheduler volume loses pending timers (see [journal.md](journal.md)).

## Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| sidecar exits with `INIT_COMPONENT_FAILURE ... missing connection string` and "Access to env var ... is forbidden" | `secretstores.local.env` refuses `DAPR_`-prefixed names | name component secrets without the `DAPR_` prefix |
| workflow `Failed` at once with `WorkflowNotFound` | the instance was scheduled through a sidecar of an app that does not register the workflow | manage workflows through the workflow host's internal API ([architecture.md](architecture.md)) |
| app exits at start with `Globalization Invariant Mode is not supported` | `Microsoft.Data.SqlClient` in invariant globalization mode | do not set `InvariantGlobalization` |
| every app exits at start with "Signal-specific AddOtlpExporter methods and the cross-cutting UseOtlpExporter method..." | per-signal and cross-cutting OTLP exporters combined | only `UseOtlpExporter` |
| a sidecar exits with "could not determine host IP address" | its app container is not running | start the app, then recreate the sidecar |
| a sidecar exits at first start with "There is already an object named 'sp_Upsert_v5_state'" | several sidecars created the state-store schema at the same time on a fresh `DaprStateDb` | start one sidecar first (as `stack-up.sh` does), then recreate the failed one |
| carrier callbacks answered `401` | the carrier and the order API use different `CARRIER_HMAC_KEY` values, or the clocks differ by more than the accepted skew | recreate both with the same `.env` |
| every shipment request answered 500 by the carrier although the shipment was stored | a transaction isolation level leaked through the connection pool (see [journal.md](journal.md)) | fixed; if it reappears, check for isolation levels above read committed |
