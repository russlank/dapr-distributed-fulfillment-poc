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
- `Delay:<ms>`: the caller is held inside the window (for example past a
  lease expiry).

The operation key `*` matches any key; such a row still fires once, so a
drill arms one row per hold it wants (with distinct run-id suffixes).

`FiredAt` is the persisted proof that the window was reached. Arming is
scoped to one drill run; unfired rows are removed by the drill's cleanup.

| Boundary | Where |
|---|---|
| `AfterProbeEffectCommitBeforeActivityCompletion` | inventory service, after the probe effect transaction commits, before the activity returns |
| `BeforeWorkflowStartCall` | order API start dispatcher, after the attempt is recorded, before the workflow host is called (key: order id, `N` format) |
| `BeforeStubFulfillmentStep` | order API, before a simulated step's transaction (key: the step's operation key) |
| `AfterStubEffectCommitBeforeActivityCompletion` | order API, after a simulated step committed, before the activity returns |
| `AfterOrderStatusUpdate`, `AfterOrderStatusHistoryInsert`, `AfterStubEffectInsert`, `AfterStartRequestClose` | order API, between the statements of a fulfillment transaction (before its commit) |
| `AfterTerminalCommitBeforeActivityCompletion` | order API, after the terminal transaction committed, before the activity returns |

Further boundaries arrive with the functionality they test.

## Drill-Only Controls

| Control | Does |
|---|---|
| `scripts/drills/sql/reset-start-request.sql` (`drill_reset_start_request` in `scripts/drills/lib.sh`) | puts the start request of **one terminal order** back to `Pending` (an operator error or a bug), due after a given delay so the drill can observe it first; refuses unless the database has the drill schema, the order is terminal, and the request is `Closed`; keeps the original row in `drill.StartRequestBackup` |
| `scripts/drills/sql/restore-start-request.sql` (`drill_restore_start_request`) | puts the saved row back |
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
```
