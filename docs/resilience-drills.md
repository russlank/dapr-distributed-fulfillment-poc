# Resilience Drills

A drill breaks one thing on purpose, proves that the failure happened at the
intended moment, and checks the business state in the owning databases.

## Safety

- **Failure injection exists only in drill builds.** Failpoint code is
  compiled only with `-p:EnableFailpoints=true -p:BuildFlavor=drill`; a
  release build has no failpoint implementation (unit test, and a build
  error if failpoints are enabled without the drill flavor).
- **Drill builds act only in the drill environment.** A compiled failpoint
  does nothing unless `DAPR_POC_ENV=drill`.
- **Drills run only on a drill stack.** `scripts/stack-up.sh --drill` starts a
  separate Compose project, `fulfillment-drill`, with drill images. The drill
  preflight refuses to run unless every Dapr role reports
  `buildFlavor: drill`, `environment: drill`, and `failpoints: compiled, active`.

## Failpoints

A failpoint is armed by inserting a row into `drill.Failpoint` in the owning
service's database (name, drill run id, operation key, action). When the code
reaches the named boundary for that operation key, a conditional update sets
`FiredAt` once and performs the action:

- `FailFast`: the process ends at once (as abruptly as `SIGKILL`);
- `Throw`: a transient error;
- `AbortConnection`: an HTTP host resets the client's connection instead of
  answering; everything committed before the failpoint stays (a lost answer);
- `Skip`: the caller drops the work it was about to do and answers as if it
  had succeeded (a consumer acknowledges a message without storing it);
- `Delay:<ms>`: the caller is held inside the window (for example past a
  lease expiry).

The operation key `*` matches any key; such a row still fires once, so a
drill arms one row per hold it wants (with distinct run-id suffixes).

`FiredAt` is the persisted proof that the window was reached. Arming is
scoped to one drill run; unfired rows are removed by the drill's cleanup.

| Boundary | Where |
|---|---|
| `AfterProbeEffectCommitBeforeActivityCompletion` | inventory service, after the probe effect transaction commits, before the activity returns |
| `AfterBusinessCommitBeforeActivityCompletion` | inventory service, after a line's reservation committed, before `ReserveOrderLine` returns (key `{orderId:N}:reserve:line-N`) |
| `AfterConsumeCommitBeforeActivityCompletion` | inventory service, after the consumption committed, before `ConsumeOrderHolds` returns |
| `BeforeStockReserve` | inventory service, inside the reserve turn before its SQL (key: the stock actor id `{warehouse}\|{sku}`); `Throw` fails that SKU's reservations only |
| `AfterOrderAcceptanceCommit` | order API, after the acceptance transaction committed, before the answer (key: the idempotency key) |
| `BeforeWorkflowStartCall` | order API start dispatcher, after the attempt is recorded, before the workflow host is called (key: order id, `N` format) |
| `BeforeStubFulfillmentStep` | order API, before a simulated step's transaction (key: the step's operation key) |
| `AfterStubEffectCommitBeforeActivityCompletion` | order API, after a simulated step committed, before the activity returns |
| `AfterOrderStatusUpdate`, `AfterOrderStatusHistoryInsert`, `AfterStubEffectInsert`, `AfterStartRequestClose` | order API, between the statements of a fulfillment transaction (before its commit) |
| `AfterTerminalCommitBeforeActivityCompletion` | order API, after the terminal transaction committed, before the activity returns |
| `AfterReviewTaskCreateCommit` | order API, after a review round committed, before the activity returns (holds the run before its first wait) |
| `AfterReviewDecisionUpdate`, `AfterReviewDecisionInsert`, `AfterPendingSignalInsert` | order API, between the statements of the decision transaction |
| `AfterReviewDecisionCommitBeforeSignalDispatch` | order API, after a decision committed, before the response |
| `BeforeSignalRaise`, `AfterSignalRaiseBeforeMarkForwarded` | order API signal dispatcher, around the raise (key: signal id, `N` format) |
| `AfterReleaseOneHoldBeforeNext` | inventory service, after one hold of a compensation was released, before the next (key `{orderId:N}:release:{holdId:N}`) |
| `BeforeBeginDispatchCommit` | order API, inside the point-of-no-return transaction after the order is locked (key `{orderId:N}:dispatch-start:1`); `Delay` holds the lock while a cancellation arrives |
| `AfterForwardResponseBeforeMark` | order API carrier forwarder, after Shipping committed and answered, before the forward row is marked (key: the carrier event id, `N` format) |
| `AfterCarrierAcceptCommitBeforeResponse` | carrier simulator, after the shipment and its callback schedule committed, before the answer (key: shipment id, `N` format) |
| `AfterBrokerAckBeforeOutboxMarkSent` | every outbox relay (order API, inventory, payment, shipping), after the broker acknowledged a publication, before the row is marked `Published` (key: event id) |
| `ConsumerAckWithoutApply` | order API event consumer, before an event is stored (key: `<source>:<order id>:<sequence>`); with action `Skip` the event is acknowledged but not stored, as if the delivery had been lost |
| `FinalizeReportsTerminal` | order API, in the finalizing transaction of a delivered order (key: its operation key); with action `Skip` the step reports the order terminal without finalizing it, so the run completes while SQL stays `AwaitingDelivery` |

Further boundaries arrive with the functionality they test.

## Simulator Fault Modes

The external simulators keep their faults in their own databases (they
survive restarts) and expose test controls only in the drill environment
(`DAPR_POC_ENV=drill`), behind their API keys.

| Simulator | Control | Faults |
|---|---|---|
| payment provider (`127.0.0.1:8091`) | `POST /provider/test/faults` `{label, match, mode, param, uses}`; `DELETE /provider/test/faults?labelPrefix=`; `GET /provider/test/operations?prefix=` | `match` is an operation key or `kind:Authorize\|Capture\|Void\|Refund`; modes `DropResponseAfterCommit` (commit, then close the connection), `DelayResponse` (ms), `StatusUnknown` (status queries for that key answer Unknown), `CrashAfterCommit` (the process ends after the commit; drill builds only), `RejectBeforeCommit` (503, nothing committed), `Decline`, `ExpireAuthorization` (validity in seconds); `uses` limits how many requests it applies to |
| carrier (`127.0.0.1:8090`) | `PUT /carrier/test/shipments/{id}/mode/Manual\|Auto`; `POST /carrier/test/shipments/{id}/script?label=` with steps `{status, version, delayMs, truth, badSignature, orderIdOverride, sameEventIdAs}`; `GET /carrier/test/shipments/{id}/sends` | manual mode (no automatic callbacks); scripted callbacks in any order, duplicates (a repeated step with the same status and version is byte-identical), payload conflicts, wrong order ids, bad signatures; the send log shows each attempt's response code |

`scripts/scenarios/m3-lib.sh` wraps these controls (`provider_fault`,
`provider_clear`, `carrier_mode`, `carrier_script`, `carrier_sends`) and the
SQL views the M3 scripts assert on.

## Drill-Only Controls

| Control | Does |
|---|---|
| `scripts/drills/sql/reset-start-request.sql` (`drill_reset_start_request` in `scripts/drills/lib.sh`) | puts the start request of **one terminal order** back to `Pending` (an operator error or a bug), due after a given delay so the drill can observe it first; refuses unless the database has the drill schema, the order is terminal, and the request is `Closed`; keeps the original row in `drill.StartRequestBackup` |
| `scripts/drills/sql/restore-start-request.sql` (`drill_restore_start_request`) | puts the saved row back |
| `scripts/drills/sql/insert-orphan-hold.sql` | creates a `Held` hold (with its reserved units and `HoldCreated` event) that no workflow owns, as a run that died after its reservation leaves behind; refuses outside a drill database |
| `raise_via_sidecar <order-api replica> <instance> <event>` | raises a workflow event through that replica's sidecar and the management API, as the signal dispatcher does (duplicate and early wake-ups) |
| `POST /internal/drill/fulfillment/{orderId}/schedule` on the workflow host (`drill_schedule_fulfillment`) | schedules `fulfill-{orderId:N}` directly through the Dapr workflow API, without the management API's existence check; returns Dapr's answer. Compiled only into drill builds, mapped only in the drill environment, app token required |
| `POST /internal/drill/fulfillment/{orderId}/purge` on the workflow host (`drill_purge_fulfillment`) | terminates (if running) and purges `fulfill-{orderId:N}`, so the instance no longer exists (a lost instance); same restrictions as above |
| `POST /system/drill/publish/{topic}` on the order API (`drill_publish` in `scripts/scenarios/m4-lib.sh`, admin key) | publishes a body as given to an event topic through the sidecar (shuffled streams, malformed, unknown-schema, and poison events); mapped only in the drill environment. An event whose data contains `"drillPoison": true` is refused by the consumer every time (bounded redelivery, then the dead-letter topic) |
| `PUT /carrier/test/shipments/{id}/mode/{mode}` on the carrier simulator | `Auto`, `Manual` (no automatic callbacks; script them), `Lost` (callbacks are dropped until they are dead-lettered), `LostNoPoll` (also the status query answers 503) |
| `POST /internal/drill/fulfillment/{orderId}/terminate` on the workflow host (`drill_terminate_fulfillment`) | terminates `fulfill-{orderId:N}` and waits for it to end (a workflow that stopped without finishing its order, the case for the repair workflow); same restrictions as above |

## Lifecycle of a Drill Script

1. Preflight (refuses a non-drill stack).
2. Arm the failure; register cleanup steps (they run in reverse order on exit,
   also when the drill fails).
3. Inject; record the time and the hit proof.
4. Recover (restart the killed replica: app first, then a recreated sidecar).
5. Assert against the databases.
6. Write `artifacts/drills/<run>/result.json` (drill id, run id, commit,
   identities, injection time, hit proof, recovery time, assertions, cleanup
   result, script exit status, outcome, duration). A failed cleanup fails
   the drill. A script that refuses (exit 2) is `REFUSED`; one that exits
   with another error or records no assertion is `FAIL`, never `PASS`.

## Run

```bash
bash scripts/stack-up.sh --drill
bash scripts/drills/R00-failpoint-selftest.sh      # core profile only (one inventory replica)
bash scripts/scenarios/S49-execution-fence.sh
bash scripts/stack-up.sh --drill --replicas
bash scripts/checks/fulfillment-recovery-check.sh   # replicas profile
bash scripts/scenarios/S52-signaling.sh             # replicas profile
bash scripts/drills/R01-waiting-workflow-host-loss.sh
bash scripts/drills/R02-start-exactly-once.sh       # runs S49 for variants C and D
bash scripts/drills/R03-lost-response-after-commit.sh
bash scripts/drills/R06-compute-pool-outage.sh      # also on the core profile
bash scripts/drills/R04-crash-after-inventory-commit.sh
bash scripts/drills/R05-actor-relocation-under-contention.sh
bash scripts/drills/R06-inventory-outage.sh         # sets a 20 s hold lifetime itself
bash scripts/drills/R08-partial-reservation.sh
bash scripts/scenarios/S20-hot-sku-isolation.sh     # needs failpoints
# payment, shipping, cancellation, reviews, interventions (replicas profile)
bash scripts/scenarios/S13-payment-declined.sh
bash scripts/scenarios/S29-S31-operator-decisions.sh
bash scripts/scenarios/S30-seeded-operators.sh
bash scripts/scenarios/S39-cancellation-windows.sh
bash scripts/scenarios/S40-S41-expiry-before-capture-and-consume.sh   # sets a 20 s hold lifetime itself
# event distribution, projection, reconciler (replicas profile)
bash scripts/scenarios/S01-happy-path-events.sh
bash scripts/scenarios/S48-shuffled-events.sh
bash scripts/drills/R14-broker-outage.sh
bash scripts/drills/R15-repeated-publication.sh
bash scripts/drills/R16-poison-events.sh
bash scripts/drills/R32-gap-repaired-by-replay.sh
bash scripts/drills/R23-signal-redelivery.sh
bash scripts/drills/R12-callback-receiver-down.sh
bash scripts/drills/R29-lost-callbacks.sh
bash scripts/drills/R30-faulted-missing-mismatch.sh
bash scripts/drills/R07-state-store-outage.sh
bash scripts/drills/R28-scheduler-placement-outage.sh
bash scripts/drills/R09-lost-provider-response.sh
bash scripts/drills/R10-interrupted-compensation.sh
bash scripts/drills/R11-carrier-crash-after-accept.sh
bash scripts/drills/R13-callback-anomalies.sh
bash scripts/drills/R19-R25-review-expiry-and-escalation.sh
bash scripts/drills/R20-R21-decision-while-hosts-down.sh
bash scripts/drills/R22-R23-conflicting-and-duplicate-decisions.sh
bash scripts/drills/R24-decision-versus-deadline.sh
bash scripts/drills/R26-undeterminable-payment.sh
bash scripts/drills/R27-cancel-versus-dispatch.sh
bash scripts/drills/R31-late-terminal-conflict.sh
bash scripts/drills/R33-provider-outage-and-crash.sh
```

| Drill | Fault | Proof that it happened | PASS when |
|---|---|---|---|
| R01 (S06) | `SIGKILL` of the workflow-host replica (and sidecar) that executed the review wait, identified from its own replay-safe log line | exit code 137; before the kill: `AwaitingApproval`, wake `Initial:Pending`, workflow `Running` with custom status `awaiting-review:1` | the approval through `order-api-2` (one decision) wakes the same instance on the other host (its log), which completes once: one effect set, one execution claim |
| R02 A | `AfterOrderAcceptanceCommit` `FailFast` on `order-api` | `FiredAt`; `order-api` exited | the client's retry on `order-api-2` returns the committed order, which completes once; nothing changes after `order-api` restarts |
| R02 B | `BeforeWorkflowStartCall` `FailFast` on whichever dispatcher claims first | `FiredAt`; the crashed dispatcher's attempt stays unfinished | the other replica starts the workflow after the lease; completes once; nothing changes after the restart |
| R02 C, D | the drill-only start-request reset and direct schedule (through S49) | reset row `Pending`; Dapr accepted the schedule | terminal guard closes the request without a start; the fence rejects the instance (`AlreadyTerminal`, metric +1); fingerprint unchanged |
| R03 (S24) | `AfterOrderAcceptanceCommit` `AbortConnection` on `order-api` | `FiredAt`; `order-api` logged the failpoint and the acceptance | the client's transport error is followed by `200` replayed from `order-api-2` with the committed order; one order, one start, one effect set |
| R06 (S08, compute) | every compute-worker replica and sidecar stopped for 45 s | no compute container running | no check result and no completion during the outage, workflows `Running` (not failed); afterwards every order completes with its two checks, once |
| R04 (S05) | `AfterBusinessCommitBeforeActivityCompletion` `FailFast`: the inventory process ends right after the reservation committed | `FiredAt`; one inventory replica exited | the retry on a live replica reads the stored reservation (replay count +1); one hold, one `HoldCreated` and one `HoldConsumed`, stock moved once, order `Completed` once; nothing changes after the restart |
| R05 (S07, S03) | `SIGKILL` of the replica hosting the hot SKU's stock actor (from its activation log) while 200 one-unit orders compete for 150 units | exit 137; reservations committed by that replica before the kill | the actor activates on another replica and goes on reserving there; 150 `Completed`, 50 `Rejected` (`InsufficientStock`), stock `0/0`, invariants clean |
| R06 (S08, inventory) | every inventory replica and sidecar stopped for 50 s while two orders hold units (20 s lifetime) and wait in a delayed step | no inventory container running; the holds' expiry passes during the outage | nothing changes during the outage, no workflow fails; afterwards the holding orders are `Compensated` (`ReservationExpired`, holds expired by the sweeper), orders accepted during the outage `Completed`; stock `8/0`, invariants clean |
| R08 A (S45) | an order whose second line has no stock | the line's `InsufficientStock` | the first line's hold released once, none for the second; `Rejected` (`InsufficientStock`) |
| R08 B | `BeforeStockReserve` `Throw` x5 on the second line's stock actor | 5 `FiredAt` | the first line's hold released once; `Compensated` (`ReservationFailed`) |
| R08 C (S15) | an orphan hold with Placement and the Scheduler stopped | both stopped while the hold expires | the sweeper expires it by SQL time (`ClosedBy sweeper:<replica>`), units free again, one `HoldExpired` |

| R09 (S38) | provider `DropResponseAfterCommit` on the authorization, then on the capture | provider row with the fault marker; the payment service's first send unanswered | a status query adopts the committed result; one operation per key, no void or refund; `Completed` |
| R10 (S14) | A: `AfterReleaseOneHoldBeforeNext` `FailFast` during a compensation; B: the void refused (`RejectBeforeCommit`) until the rounds run out | A: `FiredAt`, an inventory replica exited; B: a `CompensationExhausted` case | A: both holds released once, voided once, `Compensated`; B: after `ConfirmExternalFailure` the void commits and the order ends `Compensated` |
| R11 (S25, S26) | carrier `AfterCarrierAcceptCommitBeforeResponse` `FailFast` | `FiredAt`; carrier exited | one carrier shipment (the resend a replay); the callbacks scheduled before the crash arrive; `Completed` |
| R13 (S10, S27, S43) | scripted duplicates, early and late versions, a conflicting terminal status, a payload conflict, a wrong order id, a bad signature | the carrier's send log | `202`/`409`/`401` as specified; Shipping records each forwarded event with its outcome, terminal stays `Delivered`; one completion; one `CarrierTerminalConflict` case, acknowledged record-only |
| R19, R25 (S32, S36) | a review ignored for both rounds; a round escalated, then decided | SQL timestamps of `Expired`/`Escalated` | ignored: `Rejected` (`ReviewExpired`) without reservation or payment; escalated: the old round's decision `409 review-superseded`, the next round decides |
| R20, R21 (S35) | every workflow host stopped during the approval; `AfterReviewDecisionCommitBeforeSignalDispatch` `FailFast` | hosts exited; `FiredAt` and the order API exited | the decision committed while no host ran is delivered after their start; the one committed before the crash after the restart; one decision, one transition |
| R22, R23 (S33, S34) | an approval and a rejection released at the same instant on both replicas, 20 times; every decision repeated | both answers recorded per pair | one `200` and one `409` per review, the workflow follows SQL; repeats replayed, no second decision |
| R24 (S36) | approvals around the deadline (SQL clock), signal raises delayed 3 s | decision and expiry timestamps against `DeadlineAt` | never both approved and expired; the workflow follows SQL; late decisions `409` |
| R26 (S37) | provider `DropResponseAfterCommit` plus `StatusUnknown` on the authorization of two orders; the second's workflow terminated | committed authorization, Unknown status answers, an `AmbiguousPayment` case | while waiting: `ConfirmExternalSuccess` signals the workflow, which reconciles and completes; after termination: the repair workflow voids with its repair key, `Compensated` |
| R27 (S39) | `BeforeBeginDispatchCommit` `Delay:2000` while cancellations arrive at seeded offsets, 20 orders at once | `FiredAt` rows; both outcomes occur | never both cancellation and dispatch; `202` means `Cancelled` with the authorization voided, `409 dispatch-started` means `Completed` |
| R31 (S47) | a conflicting terminal callback after completion; variant with `AfterForwardResponseBeforeMark` `FailFast` | Shipping's `TerminalConflict` row; `FiredAt`, a replica exited, the forward row still `Pending` | one case, no signal, no workflow started, the order stays `Completed`; the retried forward gets Shipping's stored outcome |
| R33 (S50) | A: provider stopped before the authorization; B: answer delayed past the timeout, provider killed; C: `CrashAfterCommit` on the capture | A: refused sends; B: a committed row with no answer; C: the provider exited | A: `Compensated` (`PaymentProviderUnavailable`), the void by key prohibits a late authorization; B: a case, resolved with the provider's record, `Completed`; C: the capture adopted, never twice |

R02's guarantee is about business effects: one order, one start request
that ends `Closed` when the order completes, one set of effect rows; not one
physical workflow execution (D runs a second execution, which the fence
stops).
