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
- `Delay:<ms>`: the caller is held inside the window (for example past a
  lease expiry).

The operation key `*` matches any key; such a row still fires once, so a
drill arms one row per hold it wants (with distinct run-id suffixes).

`FiredAt` is the persisted proof that the window was reached. Arming is
scoped to one drill run; unfired rows are removed by the drill's cleanup.

| Boundary | Where |
|---|---|
| `AfterProbeEffectCommitBeforeActivityCompletion` | inventory service, after the probe effect transaction commits, before the activity returns |
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

Further boundaries arrive with the functionality they test.

## Drill-Only Controls

| Control | Does |
|---|---|
| `scripts/drills/sql/reset-start-request.sql` (`drill_reset_start_request` in `scripts/drills/lib.sh`) | puts the start request of **one terminal order** back to `Pending` (an operator error or a bug), due after a given delay so the drill can observe it first; refuses unless the database has the drill schema, the order is terminal, and the request is `Closed`; keeps the original row in `drill.StartRequestBackup` |
| `scripts/drills/sql/restore-start-request.sql` (`drill_restore_start_request`) | puts the saved row back |
| `raise_via_sidecar <order-api replica> <instance> <event>` | raises a workflow event through that replica's sidecar and the management API, as the signal dispatcher does (duplicate and early wake-ups) |
| `POST /internal/drill/fulfillment/{orderId}/schedule` on the workflow host (`drill_schedule_fulfillment`) | schedules `fulfill-{orderId:N}` directly through the Dapr workflow API, without the management API's existence check; returns Dapr's answer. Compiled only into drill builds, mapped only in the drill environment, app token required |

## Lifecycle of a Drill Script

1. Preflight (refuses a non-drill stack).
2. Arm the failure; register cleanup steps (they run in reverse order on exit,
   also when the drill fails).
3. Inject; record the time and the hit proof.
4. Recover (restart the killed replica: app first, then a recreated sidecar).
5. Assert against the databases.
6. Write `artifacts/drills/<run>/result.json` (drill id, run id, commit,
   identities, injection time, hit proof, recovery time, assertions, cleanup
   result, outcome, duration). A failed cleanup fails the drill.

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
```

| Drill | Fault | Proof that it happened | PASS when |
|---|---|---|---|
| R01 (S06) | `SIGKILL` of the workflow-host replica (and sidecar) that executed the review wait, identified from its own replay-safe log line | exit code 137; before the kill: `AwaitingApproval`, wake `Initial:Pending`, workflow `Running` with custom status `awaiting-review:1` | the approval through `order-api-2` (one decision) wakes the same instance on the other host (its log), which completes once: one effect set, one execution claim |
| R02 A | `AfterOrderAcceptanceCommit` `FailFast` on `order-api` | `FiredAt`; `order-api` exited | the client's retry on `order-api-2` returns the committed order, which completes once; nothing changes after `order-api` restarts |
| R02 B | `BeforeWorkflowStartCall` `FailFast` on whichever dispatcher claims first | `FiredAt`; the crashed dispatcher's attempt stays unfinished | the other replica starts the workflow after the lease; completes once; nothing changes after the restart |
| R02 C, D | the drill-only start-request reset and direct schedule (through S49) | reset row `Pending`; Dapr accepted the schedule | terminal guard closes the request without a start; the fence rejects the instance (`AlreadyTerminal`, metric +1); fingerprint unchanged |
| R03 (S24) | `AfterOrderAcceptanceCommit` `AbortConnection` on `order-api` | `FiredAt`; `order-api` logged the failpoint and the acceptance | the client's transport error is followed by `200` replayed from `order-api-2` with the committed order; one order, one start, one effect set |
| R06 (S08, compute) | every compute-worker replica and sidecar stopped for 45 s | no compute container running | no check result and no completion during the outage, workflows `Running` (not failed); afterwards every order completes with its two checks, once |

R02's guarantee is about business effects: one order, one start request
that ends `Closed` when the order completes, one set of effect rows; not one
physical workflow execution (D runs a second execution, which the fence
stops). The inventory variant of R06 (holds that expire during an outage)
belongs to M2.
