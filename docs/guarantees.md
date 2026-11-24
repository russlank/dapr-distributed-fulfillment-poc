# Guarantees

What the system guarantees, who provides each guarantee (Dapr or the
application with SQL Server), and which scenario or drill demonstrates it.
Only behavior that was run and observed counts as demonstrated; where a
guarantee rests on design alone, or was only partly exercised, this page
says so. The [v1.0.0 release notes](releases/v1.0.0.md) report the
acceptance run and focused reruns.

## Contents

- [How to Read This Page](#how-to-read-this-page)
- [What Dapr Provides and What the Application Provides](#what-dapr-provides-and-what-the-application-provides)
- [Business Invariants](#business-invariants)
- [Operational Guarantees](#operational-guarantees)
- [Partly Demonstrated or Not Demonstrated](#partly-demonstrated-or-not-demonstrated)
- [Re-running the Evidence](#re-running-the-evidence)

## How to Read This Page

- **Demonstrated:** a scenario (`Sxx`) or drill (`Rxx`) violates the
  precondition on purpose (a crash, an outage, a race, a duplicate) and the
  check passed during release verification on fresh volumes. What each check does:
  [testing.md](testing.md), [resilience-drills.md](resilience-drills.md).
- **Observed:** runtime behavior seen in the compatibility checks or a
  drill, recorded in [versions.md](versions.md).
- **By design:** follows from the implementation, not exercised by a
  failure test.
- Every check ran on Docker Compose with two or three replicas per role
  (five compute replicas for scaling).

## What Dapr Provides and What the Application Provides

| Concern | Dapr 1.19.1 provides (observed) | The application and SQL Server provide |
|---|---|---|
| Workflow progress | durable, replayed orchestration; activities **at least once** (an activity interrupted by `SIGKILL` ran again); durable timers; external events buffered before the wait | idempotent activities keyed by operation key; SQL re-read after every wake-up; the workflow carries no business truth |
| Workflow identity | caller-chosen instance id; a running id is refused, a **completed id is replaced by a new run**, a purged id is reusable | one start request per order; a dispatcher that checks state first; the execution fence (`ClaimExecution`) |
| Signals to workflows | raise to a missing instance fails; to a completed one it is silently ignored | signals carry no exclusive data; the decision is in SQL first; the dispatcher reports state instead of raising blindly |
| Workflow management | an instance can be managed only through the app that scheduled it | management through the workflow host's API, by service invocation |
| Workflow versioning | named patches (`IsPatched`); an instance whose recorded patch is missing is `Stalled`, not failed; **terminating or purging a patched instance stalls it for good** | per-host hold-back of patches; orders stopped through their own paths, never by terminating a workflow |
| Cross-app activities | activities on other apps by app id, spread over their replicas | activity contracts |
| Actors | turn-based access per actor id; placement and relocation | the guarded SQL update decides; actors hold no state |
| Timers and reminders | survive a Scheduler restart; **lost with the Scheduler's data** | deadlines stored in SQL and compared with SQL time; review nudges; a hold sweeper independent of Dapr |
| Pub/sub | at-least-once delivery, redelivery, dead letters (RabbitMQ) | outboxes written in the business transaction; history applied once per event id; contiguous per-source state; gap repair by replay |
| Service invocation | discovery, tokens, tracing; **no retry of application 5xx** responses despite a resiliency policy | caller-side retries in durable loops; idempotent receivers with stored results |
| State store | workflow and actor state in SQL Server, **only with component version v2** (v1 corrupts binary history) | business truth never in the state store |
| Security | API and app tokens; mTLS with Sentry on Kubernetes | role keys, signed callbacks, one database login per owner |
| Exactly once | **not provided** | effectively-once business effects per operation key or event id |

## Business Invariants

Each invariant is enforced in SQL Server by the service that owns the data,
and each demonstration breaks the precondition on purpose.

| Invariant | Enforced by | Demonstrated by |
|---|---|---|
| **No overselling:** `0 <= Reserved <= OnHand` per stock row | check constraint and a guarded update (`OnHand - Reserved >= quantity`) | S03 with actors and with SQL-only coordination, R05 (actor host killed under contention), S20 |
| **Idempotent reservation:** one reservation effect per order, line, and allocation | operation keys with stored results (`ProcessedOperation`) | R04 (process killed after the commit, before Dapr learned the result); integration tests |
| **No double consumption** of a hold, and one terminal transition per hold | conditional hold state update plus the operation key | S40/S41, R08; integration tests |
| **No double payment effect:** authorize, capture, void, and refund once per operation key | provider-side keys; status query before a retry; compensation by key with a prohibition | R09 (lost provider answers), R10 (interrupted compensation), R26, R33, S13 |
| **No duplicate shipment** | one shipment per order, keyed by the order id at the carrier | R11 (carrier crash after accepting) |
| **Local atomicity:** business change, operation marker, and outbox row commit together | one transaction per step in the owning database | R04, R15; integration tests |
| **No lost accepted order:** an accepted request is in `OrderDb` with a start intent until its workflow runs | durable acceptance with an idempotency key; start requests drained by a leased dispatcher | S04, S24, S43, S51, R02 A/B, R03 |
| **No lost accepted external signal:** an acknowledged carrier callback is recorded before the answer | callback inbox with payload hash and a forward queue | R12, R13, R29, R31 |
| **Every unfinished order is recoverably pending** with a next action or a visible case | stored deadlines, nudges, the reconciler and its cases | R06, R07, R28, R30 |
| **No duplicate completion:** `OrderCompleted` at most once per order | one terminal transition in SQL; events applied once per id | R15, R31, S01 |
| **One review outcome** per review round, decided before its deadline by SQL time | conditional transition on the review row; unique decision id | S29/S31, S52, R22 |
| **No lost accepted decision** | decision, audit, and pending signal in one transaction | R20 (decision while every workflow host is down), S52 |
| **No stale review authorization:** a late or repeated decision cannot advance another round or approve an expired one | round number and deadline in the conditional update | R19/R25, R23, R24 |
| **No unsafe retry after an ambiguous external effect:** an undeterminable outcome becomes a case, never a second attempt | status queries, prohibitions by key, intervention cases | R09, R26, R33 |
| **Cancel or dispatch, never both** | conditional updates on one order row; a check constraint refuses both | S39, R27 |
| **Idempotency keys are bound to their payload** | stored payload hashes and unique keys | S43, R13 |
| **No signal to a missing or finished workflow changes business state** | dispatcher rules, SQL re-read, signals without exclusive data | S52, R23, R31 |
| **One active case per order and kind; each resolution executed once** | filtered unique index (including `Escalated`); resolution transaction | R26, R29, R30, R31 |
| **Outbox recoverability:** every committed outbox row is eventually published or visibly stalled, never deleted unpublished | outbox states without a delete path; relays with leases; alerts | R14, R15 |
| **Carrier facts survive the workflow:** every valid carrier status is recorded once; a terminal status never changes | inbox, forward queue, Shipping's unique keys and current-state rule | R12, R13, R31 |
| **History completeness and contiguous state:** every valid event is in the history once; current state equals the contiguous prefix, whatever the arrival order | unique event ids; apply-in-sequence with drain; rebuild comparison | S01, S48, R32 |
| **Execution fence:** after an order is final, no new business effect except through an authorized repair | start-request guard, dispatcher check, `ClaimExecution`, keyed effects | S49, S51, R02 C/D |

## Operational Guarantees

| Guarantee | Demonstrated by |
|---|---|
| Business commits continue while the broker is down; nothing is lost and the event sets converge afterwards | R14 |
| Business commits continue while the workflow state store is unreachable; afterwards every order completes once | R07 |
| With the Scheduler down, review deadlines expire late but correctly; with Placement down, orders wait and complete once | R28 variant A |
| A waiting workflow survives the loss of the host running it | R01, S00 |
| Work stays pending while a whole role is down and completes once afterwards | R06 (compute, inventory) |
| Correctness holds while the compute pool scales and a replica dies under load | R18 |
| A sidecar setting caps concurrent activities per replica | S17 |
| Orders waiting across a rolling workflow upgrade keep their recorded path; a rollback stalls a patched instance safely until rolled forward | R17 |
| A faulted, missing, or mismatched workflow becomes one case and is repaired | R30 |
| Each alert rule was observed firing during its designated drill and clearing after recovery | R06, R07, R08, R12-R16, R20, R28, R29, R30, R32, R33 |
| Every trust boundary refuses an unauthorized caller; no secret reaches the logs | S22 |
| The Kubernetes template deploys and completes an order | the kind smoke test (`scripts/kind-smoke.sh`) |

## Partly Demonstrated or Not Demonstrated

| Property | Status |
|---|---|
| Behavior on Kubernetes under failures, with mTLS | smoke test only (one order); no drill on Kubernetes |
| Scheduler data loss during an outage | not run as a drill; the pending timer is lost (observed in the compatibility checks), and a nudge recovers review waits |
| A lost delivery-deadline timer | not nudged; the order waits until something else wakes it |
| SQL-only coordination under Placement or replica loss | not run; by design it does not depend on Placement |
| Service invocation retries on transport failures | unverified |
| Hot-key latency of a busy actor | correctness demonstrated, latency not measured |
| Workflow history growth with large fan-out | not tested |
| Growth of event history and outboxes over long runs | not tested; rows are never deleted |
| Recovery of a patched workflow instance that was terminated anyway | none: it stays `Stalled` and no case opens ([operations.md](operations.md#never-terminate-a-patched-workflow)) |
| Clearing a workflow-fault alert after another case repairs the same order | R26 left a `WorkflowFaulted` case open on a compensated order. R30 demonstrated alert recovery on a fresh stack. [Journal](journal.md#a-repaired-r26-order-kept-the-workflow-fault-alert-firing-in-r30) |
| Throughput at volume | samples only ([final-report.md](final-report.md#34-how-much-work-was-distributed-across-replicas-with-what-throughput-and-latency)) |

## Re-running the Evidence

```bash
bash scripts/verify.sh --suite full --keep-awake   # every scenario and drill on fresh stacks, about 90 min
bash scripts/verify.sh --only R26 --reuse          # one drill on a running drill stack
```

Each run writes its assertions, the commit, and the tree state to
`artifacts/`. What broke during development, and why:
[journal.md](journal.md).
