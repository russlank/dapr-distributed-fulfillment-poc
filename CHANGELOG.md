# Changelog

Releases of the PoC; each tag `v<version>` marks a verified milestone.
Details of a release: `docs/releases/`. What broke on the way:
[docs/journal.md](docs/journal.md).

## Unreleased (0.7.0-dev)

- Development towards M6 (final report and the `v1.0.0` release). Entries are
  added as functionality is verified.
- Final report (`docs/final-report.md`): answers to the evaluation questions,
  lessons learned per phase, recommendations, further improvements, and
  explorations.
- The Kubernetes template passes a smoke test on a local kind cluster
  (`scripts/kind-smoke.sh`): migrations, every role ready with its sidecar,
  one order completed. Secrets moved from the base into overlays
  (`overlays/example`, `overlays/kind-smoke`).
- Documentation: a README with a summary, status, and a grouped map of the
  documents; tables of contents in the longer documents; introductions and
  corrected statements where they were missing or out of date.

## v0.6.0: Observability, scaling, workflow versioning, and comparison (M5)

- Alerts and dashboards: operational gauges in every service (start
  requests, signals, carrier forwards, outbox rows, projection gaps, dead
  letters, active cases, overdue holds, dead-lettered callbacks); seventeen
  Prometheus rules evaluated every 5 s, Alertmanager as a local sink, eight
  Grafana dashboards, runbooks in the operations guide; a unit test refuses
  log placeholders that could carry payloads or secrets. Every rule is
  checked in the drill that causes it (fired during the fault, resolved
  after recovery).
- Workflow versioning with named patches (`IsPatched`), per-host hold-back
  (`WORKFLOW_PATCHES_DISABLED`), `/system/workflow-patches`; the first patch
  records the executing version (`WorkflowStartRequest.ExecutionVersion`).
  R17 rolls the hosts while orders wait. Known limitation: on Dapr 1.19.1 a
  patched instance must not be terminated or purged (it stays `Stalled`);
  orders end through their own paths and the workflow fences itself.
- Drills for a workflow state-store outage (R07) and a Scheduler and
  Placement outage (R28 variant A); scaling under load with 1, 3, and 5
  compute replicas (R18); an activity concurrency cap (S17); the security
  boundary scenario (S22).
- The reconciler no longer reports designed waits as stalled, and reports a
  deterministically rejected gap event once as unresolvable instead of
  replaying it.
- Stock actors compared with SQL-only coordination, measured
  (`docs/dapr-vs-alternatives.md`, `scripts/experiments/coordination-comparison.sh`).
- A generic Kubernetes template (`deploy/kubernetes/`, Kustomize; not
  verified on a cluster).
- Fixes: the failpoint table creation is serialized across replicas; drill
  terminate and purge controls are bounded to 15 s.
- A confirmed delivery failure ends the order in the new terminal status
  `Escalated` (reason `DeliveryFailed`) instead of leaving it awaiting
  delivery: `ConfirmExternalFailure` on a `DeliveryException` or
  `DeliveryOverdue` case escalates the case (it stays open, with the
  operator's confirmation) and closes the automated lifecycle; `Acknowledge`
  later records the business resolution. Nothing is refunded, reshipped, or
  restocked automatically; an open delivery case can no longer be
  acknowledged. Drill R29 covers it.
- Limitations and deferred work: [docs/releases/v0.6.0.md](docs/releases/v0.6.0.md).

## v0.5.0: Reliable event distribution, history, and recovery (M4)

- Event distribution (M4): every owning host writes its events in the
  business transaction into its own outbox (gapless sequence per order and
  source); leased relays in every replica publish them at least once through
  Dapr pub/sub (RabbitMQ), with backoff, a `Stalled` flag, lease takeover,
  and replay by sequence. Rows are never deleted.
- Event history and projection: the order API applies events once per event
  id, contiguously per order and source (early arrivals pending, drained when
  the gap fills), counts duplicates, and records conflicts and invalid
  messages in a dead-letter recorder (ids and reasons only); timeline,
  checksum, rebuild, and replay endpoints; `scripts/ops/rebuild-timeline.sh`.
- Reconciler: one leased instance opens `WorkflowFaulted`, `WorkflowMissing`,
  and `ExecutionMismatch` cases, nudges review waits whose timer was lost, and
  replays outbox events for projection gaps and lagging streams; repairs of
  those cases compensate before the point of no return (`WorkflowFailed`) or
  finalize a delivered order.
- Carrier simulator test modes `Lost` and `LostNoPoll`; a longer drill
  callback retry schedule; a drill profile for the carrier forwarder; a
  `Skip` failpoint action; drill controls to purge a workflow instance and
  to publish raw events.
- Scenarios S01, S48 (with S43 for events); drills R12, R14, R15, R16, R23
  (signal part), R29, R30, R32.
- Verification runner `scripts/verify.sh`: suites `full` (milestone) and
  `dev`, step groups, stack reuse, per-step timings compared with the
  previous run. `CARRIER_SIM_STEP` paces the carrier simulator; the release
  stack's scenarios use 2 s and take about half the time.
- The event consumer refuses to run a statement once SQL Server has ended
  its transaction (a deadlock victim the client was not told about), and a
  duplicate or early delivery repairs a stream left behind its history
  (warning, `event_stream_repairs_total`); a rebuild compares every stream
  with its live state and reports a differing one with its history; the
  runner keeps the order API logs of each drill phase.
- Limitations and deferred work: [docs/releases/v0.5.0.md](docs/releases/v0.5.0.md).

## v0.4.0: Payment, shipping, compensation, cancellation, and interventions (M3)

- Payment (M3): a payment service (`PaymentDb`) against an external payment
  provider simulator (own database, no sidecar). Intent and attempt rows
  before every request; a request without an answer is reconciled by status
  query, a refused connection counts as nothing sent; outcomes `Unavailable`
  and `Ambiguous`. Compensation by operation key: a void or refund against an
  operation that never committed is a committed no-op that prohibits the
  key, so a late original can never commit; a capture is refused after a void.
- Holds are extended once before the capture (never beyond the
  authorization, never shortened); the capture needs enough remaining
  validity; the consumption checks expiry again; a hold lost after the
  capture is refunded.
- Shipping (M3): a shipping service (`ShippingDb`) and a carrier simulator
  with signed callbacks. Ingress writes an inbox row and a forward command in
  one transaction; a leased forwarder applies them through Shipping, whose
  current-state rule keeps versions moving forward and the first terminal
  status; `Delivered` completes the order with an `OrderCompleted` outbox row.
- Cancellation with a point of no return before dispatch (`409
  dispatch-started` afterwards); cancellation during a review.
- Intervention cases (`AmbiguousPayment`, `CompensationExhausted`,
  `AmbiguousDispatch`, delivery exceptions and overdue deliveries, carrier
  terminal conflicts) and their resolution: signal to the waiting workflow,
  a repair workflow with its own keys, or record only.
- Manual review escalation; new compatible `409` codes `review-cancelled`,
  `no-further-round`, `dispatch-started`, `order-terminal`.
- Client simulator: `cancel`, `reviews`, `escalate`, `resolve`, and the
  seeded scripted operator (`operator run`).
- Scenarios S13, S29-S31, S30, S39, S40/S41; drills R09, R10, R11, R13,
  R19-R27, R31, R33. Earlier scenarios and drills follow the full
  fulfillment history.
- Limitations and deferred work: [docs/releases/v0.4.0.md](docs/releases/v0.4.0.md).

## v0.3.0: Inventory contention and idempotent activities (M2)

- Inventory (M2): `InventoryDb` with its own migrations; stock rows guarded
  by check constraints, holds that leave `Held` once, a stored result per
  operation key, a gapless per-order outbox (write side only). Reserve,
  release, consume (all of an order's holds or none) and expire are guarded
  SQL under a per-order application lock.
- Stock actors per warehouse and SKU serialize a hot SKU; Placement moves an
  actor when its host dies. `INVENTORY_COORDINATION=SqlOnly` bypasses them
  with the same guarantees.
- Whole-line allocation by warehouse priority; partial reservations are
  released; new outcomes `Rejected` (`InsufficientStock`) and `Compensated`
  (`ReservationFailed`, `ReservationExpired`).
- Hold expiry by SQL time through a leased sweeper, independent of the Dapr
  Scheduler and Placement.
- Client simulator `contention` command; scenarios S03, S20; drills R04,
  R05, R06 (inventory), R08.
- Drill results: a drill that refuses or stops early is never `PASS`.
- Limitations and deferred work: [docs/releases/v0.3.0.md](docs/releases/v0.3.0.md).

## v0.2.0: Durable order acceptance, execution fence, cross-replica work (M1)

- Idempotent order acceptance on any order API replica; one start request
  per order, started once in effect by a leased start dispatcher through the
  workflow host's management API; an execution fence on terminal orders.
- Simulated fulfillment: validation, parallel compute checks, keyed
  `stub-` effects, one terminal transaction.
- Operator reviews from 1,000.00: SQL-decided approvals with an immutable
  audit, durable signals to the waiting workflow, expiry by SQL time.
- Client simulator: seeded, replayable workloads, duplicates, conflicts,
  approvals, inspection.
- Scenarios S02, S04, S09, S23, S24, S43, S49, S51, S52 and drills R01, R02
  A-D, R03, R06 (compute) `PASS`.
- Limitations and deferred work: [docs/releases/v0.2.0.md](docs/releases/v0.2.0.md).

## v0.1.0: Repositories, runtime compatibility, stack, CI (M0)

- Dapr 1.19 compatibility spike, Docker Compose stack with every role and its
  sidecar, health and identity endpoints, telemetry, failure-injection
  harness (drill builds only), CI gates on two providers.
- Note: the build at this tag reports version `0.1.0-dev` (the prerelease
  suffix was still set); releases from v0.2.0 on have no suffix.
