# Final Report

What this proof of concept set out to learn about Dapr 1.19 for a distributed
order fulfillment system, what it demonstrated, what it found on the way, and
what we recommend. Written at `v0.6.0` (2026-11-23), after six milestones. It
draws on every phase: the compatibility spike, the release notes in
[releases/](releases/), the [journal](journal.md), the observed runtime
behavior in [versions.md](versions.md), and the measured comparison in
[dapr-vs-alternatives.md](dapr-vs-alternatives.md).

Synthetic data only; not production software. Every guarantee below was
demonstrated on Docker Compose with two or three replicas per role. The
Kubernetes template passed a smoke test on a local kind cluster: the
migrations completed, every role became ready with its sidecar, and one
order completed through the cluster.

## Contents

- [1. Summary](#1-summary)
- [2. What Was Built, Phase by Phase](#2-what-was-built-phase-by-phase)
- [3. Answers to the Seven Questions](#3-answers-to-the-seven-questions)
  - [3.1 Which responsibilities became simpler because of Dapr?](#31-which-responsibilities-became-simpler-because-of-dapr)
  - [3.2 What remains application-owned?](#32-what-remains-application-owned)
  - [3.3 What is the additional sidecar, control-plane, and state-store cost?](#33-what-is-the-additional-sidecar-control-plane-and-state-store-cost)
  - [3.4 How much work was distributed across replicas, with what throughput and latency?](#34-how-much-work-was-distributed-across-replicas-with-what-throughput-and-latency)
  - [3.5 Does Dapr Workflow offer enough value compared with an Orleans process manager or a SQL-backed worker?](#35-does-dapr-workflow-offer-enough-value-compared-with-an-orleans-process-manager-or-a-sql-backed-worker)
  - [3.6 Can durable human approval and operational reconciliation be demonstrated safely without a custom UI?](#36-can-durable-human-approval-and-operational-reconciliation-be-demonstrated-safely-without-a-custom-ui)
  - [3.7 What limitations were observed, and which guarantees were only partially demonstrated?](#37-what-limitations-were-observed-and-which-guarantees-were-only-partially-demonstrated)
- [4. Lessons Learned](#4-lessons-learned)
  - [4.1 About the runtime](#41-about-the-runtime)
  - [4.2 About the design](#42-about-the-design)
  - [4.3 About verification](#43-about-verification)
- [5. Recommendations](#5-recommendations)
- [6. Further Improvements and Optimizations](#6-further-improvements-and-optimizations)
- [7. Explorations Worth Doing](#7-explorations-worth-doing)
- [8. Evidence and Reproduction](#8-evidence-and-reproduction)

## 1. Summary

- **Dapr moved the work; SQL Server kept the truth.** Workflows, actors,
  pub/sub, service invocation, and the sidecar model carried the
  orchestration across replicas, crashes, and restarts. Every business
  decision, every guarantee against duplicates, and every repair stayed in
  application code backed by SQL transactions. This split held through
  every milestone and paid off repeatedly. It even made the worst runtime
  defect we found harmless in normal operation.
- **Dapr Workflow is worth it for long-running, multi-service processes with
  human waits**, if the team accepts its operational surface: pinned
  versions, a spike before each upgrade, versioning discipline, and a few
  runtime behaviors that differ from the documentation. For short pipelines
  without long waits, the SQL-backed leased worker that this PoC built five
  times anyway would be simpler.
- **Seven runtime behaviors differed from what the documentation or common
  expectation suggests.** Each one became a design rule (Section 4.1). The
  most consequential: on 1.19.1, terminating a workflow instance that
  recorded a versioning patch stalls it for good.
- **The verification method was as important as the code.** The full
  verification run (80 steps: gates, unit and integration tests, every
  scenario and failure drill on fresh stacks) found defects that no unit test would have found: a swallowed SQL deadlock, a
  replay loop, startup races, and clock steps. They also found defects in
  the drills themselves, which is why every drill must be able to fail.

## 2. What Was Built, Phase by Phase

| Version | Milestone | Question | What it added | What it found |
|---|---|---|---|---|
| spike | - | Can Dapr 1.19 carry the design? | runtime experiments A1-A18 | SQL Server state store v1 corrupts workflow history; a completed instance id is replaced on reschedule; lost Scheduler data loses timers; service invocation does not retry application 5xx; an instance belongs to the app that scheduled it |
| v0.1.0 | M0 | Does the stack run reproducibly? | Compose stack, app/sidecar pairs, failpoint harness, CI on two providers | sidecars left in a dead network namespace after an app restart; sidecars racing on the state-store schema |
| v0.2.0 | M1 | Accept once, start once, never run a finished order | durable acceptance, start dispatcher with leases, execution fence, reviews and signals, client simulator | the SQL Server clock steps back; awaiting a cancellation in workflow code broke the review wait; a completed id rescheduled starts a new run |
| v0.3.0 | M2 | Never oversell under contention and relocation | inventory with stateless stock actors, guarded SQL holds, sweeper by SQL time, SQL-only mode | range locks on missing keys deadlocked under contention; a refused drill reported PASS |
| v0.4.0 | M3 | External parties fail, so every effect must be keyed and reconcilable | payment provider and carrier simulators, compensation by key, cancellation with a point of no return, intervention cases and repair | a connect timeout taken for a request that may have committed; a serializable transaction leaking into the next statement; a terminated workflow made its case unresolvable |
| v0.5.0 | M4 | Every fact reaches the read side once, in order, provably | outboxes in four databases, leased relays, event history with contiguous per-source state, dead letters, rebuild, reconciler | a deadlock victim the SQL client did not report let the consumer write outside its transaction; marking a row published could swallow a replay; gap detection alone misses a lost last event |
| v0.6.0 | M5 | Can it be operated, scaled, and changed while it runs? | alerts and dashboards checked in drills, escalated end for a failed delivery, workflow versioning with patches, outage and scaling drills, actor versus SQL-only comparison, Kubernetes template | terminating a patched instance stalls it for good; a rejected event replayed forever; replicas racing to create a table |

Each milestone added a new kind of failure to survive. Each answer reused
the same small set of patterns: an intent row committed with the fact, a
leased work queue that drains it, an operation key with a stored result,
and a conditional SQL transition. The schema grew, but no table was
redesigned.

## 3. Answers to the Seven Questions

### 3.1 Which responsibilities became simpler because of Dapr?

| Responsibility | What Dapr provided | Evidence |
|---|---|---|
| Long-running orchestration with waits | workflow code that reads as sequential code, with durable timers and external events; state survives host crashes | R01 (waiting host killed), S00, the review wait |
| Distributing work across replicas | activities and cross-app calls spread over every replica of a role; a killed replica's work moves to the survivors | R18, A2, R06 |
| Serializing access to a hot key | turn-based stock actors, placed and relocated by Placement | S03, R05, S20 |
| Publishing and subscribing | RabbitMQ behind a component, with retries and dead letters | R14, R16 |
| Service-to-service calls and app identity | invocation by app id, API and app tokens | S22, A12 |
| Observability of the runtime | sidecar metrics and traces without application code | A15, the dashboards |

The biggest gain was readability and durability of the orchestration itself.
The fulfillment workflow is one C# method that waits minutes for a review or
days for a delivery, and survives any process dying in between.

### 3.2 What remains application-owned?

Everything that makes the result correct:

- **Transactions.** Workflow history, timers, and business data never commit
  together. Every activity is an idempotent SQL step, and the order's status
  lives in SQL, not in the workflow.
- **Idempotency.** Activities run at least once, and so do starts, signals,
  callbacks, and published events. Every effect has an operation key with a
  stored result; every inbound boundary has an inbox with a payload hash;
  every consumer applies an event once per event id.
- **Ordering.** Timer versus event order in the engine is not a business
  rule. SQL decides races with conditional transitions on one row, by
  database time.
- **Compensation.** Voids and releases by operation key, with a prohibition
  that blocks a late original after its compensation.
- **Validation and security.** Role keys, signatures, per-service database
  logins, and the boundary checks of S22.
- **Repair.** The execution fence, intervention cases, the reconciler, and
  repair workflows. Dapr reports a failed or missing instance; deciding what
  that means for an order, and fixing it, is application code.
- **Retries the runtime does not do.** Service invocation does not retry
  application 5xx responses, so every caller retries in its own durable loop.

### 3.3 What is the additional sidecar, control-plane, and state-store cost?

| Cost | Observed |
|---|---|
| Memory per sidecar | 32-34 MiB at rest; an app plus sidecar pair is about 65-95 MiB |
| Sidecars in the replicas profile | 13, about 430 MiB at rest |
| Control plane (Compose) | Placement about 14 MiB, Scheduler about 42 MiB, plus a persistent Scheduler volume that must be kept together with the state store |
| State store | the `DaprStateDb` database on the same SQL Server; workflow load adds sessions and memory there |
| Whole stack | 37 containers, about 2.4 GiB at rest with replicas; SQL Server dominates and grows to 1.1-1.4 GiB under contention |
| Startup | a fresh replicas stack starts in about 1-1.5 minutes; sidecars must start after their app and the first sidecar must create the state schema alone |
| Operational knowledge | sidecars must be recreated with their app, one component version corrupts history, the Scheduler and state store must never lose data separately, and patched instances must not be terminated |

We did not measure the per-call latency of the sidecar hop in isolation.
End-to-end latency is dominated by the business stages: the simulated
carrier, payment, and packing.

### 3.4 How much work was distributed across replicas, with what throughput and latency?

Work spread as intended, and correctness held while it did. The throughput
figures are small samples, because the PoC tested correctness under load,
not capacity.

- **Compute scaling (R18):** 10 orders each with one, three, and five compute
  replicas, one replica killed in the last run. Every order completed with
  exactly its two checks, spread over the running replicas, with nothing
  lost or repeated. Rates were 43, 26, and 67 orders per minute. With 10
  orders, run-to-run noise exceeds any scaling effect, and the compute pool
  was not the bottleneck.
- **Contention (S03):** 500 one-unit orders for 100 units of one SKU, 32 in
  flight over two order API replicas. 100 completed, 400 rejected, no stock
  oversold, in 63 s with actors and 67 s with SQL-only coordination.
  Reservation p50 412 ms (actors) and 552 ms (SQL only); end-to-end p50
  about 11-12 s per completed order.
- **Concurrency cap (S17):** a sidecar setting caps concurrent activities per
  replica, and the cap holds under a burst.
- **Event processing:** a full verification run publishes and projects
  events for over 2,100 order streams, and a rebuild of every stream equals
  live state.

Not measured: sustained throughput at volume, per-key latency of a hot
actor, and behavior beyond five compute replicas.

### 3.5 Does Dapr Workflow offer enough value compared with an Orleans process manager or a SQL-backed worker?

This is a judgment from building one of the three. Neither alternative was
built.

- **Against a SQL-backed worker:** the PoC built five leased work queues
  anyway: start requests, signals, carrier forwards, outbox relays, and the
  sweeper. They are reliable and easy to reason about. A fulfillment process
  written only as such queues would need its own state machine per order,
  its own timers by SQL time, and its own step bookkeeping. That is the code
  the workflow made unnecessary. For short pipelines, the worker is simpler.
  For processes with several waits, branches, and compensation, the workflow
  is clearer.
- **Against an Orleans process manager:** Orleans grains would give the same
  turn-based model with in-memory state and reminders. Durability of the
  process would then be the application's job: storing progress and
  replaying after a crash. Dapr Workflow's replay gives that for free, at
  the cost of determinism rules and versioning discipline.
- **What Dapr Workflow costs:** runtime behaviors that must be spiked before
  each upgrade (seven found, Section 4.1); a versioning model where an
  in-flight instance and a code change must stay compatible; management only
  through the app that scheduled an instance; and a state store that cannot
  join SQL transactions, so every guarantee still needs SQL-side keys and
  fences.

**Verdict:** enough value for this kind of process: long-running, human
waits, several services, compensation. The decisive condition is
architectural. Keep business truth in SQL and treat the workflow as a
progress engine. With that, the runtime's defects stay contained. Without
it, they become data integrity problems.

### 3.6 Can durable human approval and operational reconciliation be demonstrated safely without a custom UI?

Yes. Reviews are tasks in SQL with a deadline by database time. Decisions
arrive through an operator API, and the client simulator drives them
interactively or from a seed. SQL decides every race: approval against
expiry, two operators deciding at once, a decision against a cancellation.
The workflow is only woken. Intervention cases with explicit resolutions,
the reconciler, and repair workflows cover what automation cannot decide.
Evidence: S29-S31 (operator decisions), S30 (seeded operators), R23 and R24
(signal redelivery, decisions around the deadline), R26 (undeterminable
payment), R29 (lost callbacks, escalation), R30 (faulted, missing, and
mismatched workflows). Grafana dashboards and the operations runbooks serve
as the operator's view.

### 3.7 What limitations were observed, and which guarantees were only partially demonstrated?

**Limitations:**

- On Dapr 1.19.1, a workflow instance that recorded a patch must not be
  terminated or purged: it stays `Stalled` for good. Orders are stopped
  through their own paths instead, and the workflow fences itself. An
  instance terminated anyway is not repaired automatically.
- Lost Scheduler data loses pending timers. The design re-checks deadlines
  in SQL and nudges waits, but the Scheduler volume and the state store must
  be backed up together.
- The event consumer's lock deadlocks under load (retried, harmless, costs
  latency).
- A lost delivery-deadline timer is not nudged.
- Alert and reconciler thresholds are fixed per time profile.
- Static API keys; no mTLS between sidecars on Compose.
- The local SQL Server (2 GiB) has little headroom for long SQL-only runs.

**Partially demonstrated or not demonstrated:**

| Guarantee or property | Status |
|---|---|
| Deployment on Kubernetes with mTLS | template smoke-tested on kind with Dapr 1.19.1 installed by Helm (migrations, every role ready with its sidecar, one order completed); no drills on Kubernetes; mTLS on by default but not exercised by a drill |
| Scheduler volume loss during an outage | not run (deferred variant of R28) |
| SQL-only coordination under Placement or replica loss | not run; by design it does not depend on Placement |
| Service invocation retries on transport failures | unverified |
| Hot-key latency for a busy actor | correctness proven, latency not measured |
| Workflow history growth with large fan-out | not tested |
| Growth of event history and outboxes over long runs | not tested; rows are never deleted |
| Throughput at volume | not measured beyond the samples in 3.4 |
| Name-based workflow versioning | not built; patches only |

**Drills and checks that failed during development** (each recorded with
symptom, cause, and change in the journal):

| Kind of failure | Examples |
|---|---|
| Product defects found by drills | a swallowed deadlock let the event consumer write outside its transaction; marking a row published could swallow a replay; a rejected event replayed forever; a terminated workflow made its case unresolvable; a callback for the wrong order taken for a duplicate; range locks deadlocking under contention; a serializable transaction leaking into the next statement |
| Runtime behavior | terminating a patched instance stalls it; an invocation routed to a killed workflow host hung for 15 s; the execution view lost the workflow after host restarts |
| Startup and environment races | sidecars racing on the state schema; replicas racing to create the failpoint table; sidecars in a dead network namespace; a host port still held by another stack; the laptop sleeping during a run |
| Time | the SQL Server clock stepping back; two clocks in one comparison; a fixed 300 ms wait while the SQL clock steps |
| The drill, not the system | a refused drill reporting PASS; missed failure windows; evidence queries reading logs incompletely; reused instance ids across reruns; an end check reading an audit row as the outcome |

## 4. Lessons Learned

### 4.1 About the runtime

1. **Spike the runtime before designing on it, and again before each
   upgrade.** Seven behaviors differed from the documentation or from
   common expectation:
   - state store v1 corrupts binary workflow history (use v2);
   - rescheduling a completed instance id starts a new run;
   - raising an event to a missing instance fails (the SDK documentation
     says it is discarded);
   - activity concurrency limits work only as a sidecar setting;
   - service invocation does not retry application 5xx responses;
   - an instance can be managed only through the app that scheduled it;
   - terminating a patched instance stalls it for good (1.19.1).
2. **Never rely on a timer alone.** Wait for a wake-up event too, and
   re-check the deadline in SQL by SQL time.
3. **Sidecars are infrastructure with their own failure modes.** A restarted
   app needs a recreated sidecar. Sidecars starting together race on schema
   creation. Their logs are evidence.
4. **Versioning is a design decision from the first deployment.** Patches
   let in-flight instances keep their path and roll out replica by replica.
   They also leave markers that change what operations are safe.

### 4.2 About the design

1. **Business truth in SQL, the workflow as a progress engine.** This one
   decision contained every runtime surprise. A stray, restarted, or
   duplicated workflow cannot change a finished order, because every step
   re-reads SQL and fences itself.
2. **Every boundary fails between "committed" and "acknowledged".** The
   answer was always the same: commit an intent row with the fact, and let a
   leased work queue deliver it at least once to a receiver that applies it
   once.
3. **Use the database clock for every deadline and lease.** Host clocks and
   the SQL Server clock disagree, and the SQL clock itself steps.
4. **Operation keys with stored results make retries boring.** They also make
   compensation possible while an outcome is unknown, via a prohibition by
   key.
5. **Read models must be provably derivable.** Contiguous per-source state,
   dead letters with reasons, and a rebuild that compares every stream with
   live state turned "the timeline looks right" into a check.
6. **The simplest state model that ends a process is better than a complete
   one.** A failed delivery ends `Escalated`, with the case open for people,
   instead of an automated refund and reshipment model nobody had specified.

### 4.3 About verification

1. **A drill must be able to fail.** A refused or early-stopped drill once
   reported PASS. Results carry the commit and tree state, and every
   assertion records what was expected and what was observed.
2. **A failing drill is first a question about the drill.** Many
   development failures were in the drill or its evidence query. The
   evidence rows, not the verdict line, show what happened.
3. **Alerts are tests too.** Checking that every alert fires during its fault
   and resolves afterwards found a real defect (a replay loop) and two kinds
   of noise.
4. **Race conditions appear on fresh infrastructure.** Startup races showed
   only in full runs from fresh volumes. That is the case for running the
   full suite at milestone exits, while day-to-day checks stay focused.
5. **Proportional verification keeps the loop fast.** Focused runs take
   minutes. The full run (80 steps, about 90 minutes) is reserved for
   milestones and release candidates.

## 5. Recommendations

**For a team adopting Dapr Workflow:**

1. Keep business state in your own database and make every activity an
   idempotent, keyed SQL step with a fence against finished work.
2. Pin runtime, SDK, and component versions; run a compatibility spike for
   every upgrade, including the terminate and versioning experiments of this
   PoC.
3. Decide the versioning strategy before the first deployment. Use patches
   for compatible changes and a new workflow name for incompatible
   rewrites. Never terminate patched instances on 1.19.1. Stop orders
   through their own paths.
4. Back up the Scheduler volume and the state store together, and design
   waits so that a lost timer is recovered from SQL.
5. Build the reconciler and intervention cases early. They are what makes
   at-least-once delivery operable.
6. Treat alerts as part of the test suite.

**For taking this codebase towards production:**

1. Run the drills on Kubernetes with mTLS and access-control policies, then
   replace static API keys with an identity provider.
2. Add retention and archiving for event history and outboxes.
3. Replace fixed thresholds with configuration per environment, and alert on
   reconciler counters.
4. Load-test at volume to find the real bottleneck. The current samples
   suggest it is not compute.
5. Re-run the patched-terminate experiment on each Dapr upgrade.

## 6. Further Improvements and Optimizations

| Improvement | Value | Effort | Notes |
|---|---|---|---|
| Recovery path for a patched instance terminated anyway | medium | small to medium | reconciler rule for instances stalled while no patch is held back; test Dapr's force purge |
| Per-stream locking in the event consumer | medium | medium | removes the deadlocks under load and their latency |
| Nudge for a lost delivery-deadline timer | medium | small | the review wait already has one |
| Retention and archiving of event history and outboxes | high for production | medium | today rows are never deleted |
| Configurable reconciler and alert thresholds per environment | medium | small | thresholds are fixed per time profile |
| Faster verification: drill-only alert timings, fewer SQL tool calls, condition waits instead of sleeps | medium | small | an estimated 5-10 minutes off the 90-minute full run |
| Pin CI actions by commit SHA | low | small | supply-chain hygiene |
| Larger SQL Server memory for long SQL-only runs | low | small | the local 2 GiB limit was reached once |
| Partial shipment, capture, and refund | depends on the business | large | out of scope so far |
| Structured resolution outcomes and case ownership for escalated orders | medium | medium | a closed outcome code beside the evidence; case age alerts |
| Operations and demo UI | low for correctness | medium | the API, client simulator, and dashboards suffice today |

## 7. Explorations Worth Doing

- **Dapr's transactional outbox against the custom outboxes.** It could
  replace four outbox tables and their relays, if its guarantees hold in the
  same drills (R14, R15).
- **Name-based versioning next to patches.** Measure the cost of keeping two
  workflow types alive, and confirm that termination works without patch
  markers.
- **A minimal Orleans or SQL-worker version of the fulfillment process**, to
  turn the judgment in 3.5 into a measured comparison.
- **The drills on Kubernetes**: kill pods and nodes, relocate actors, and
  restart the control plane with mTLS on.
- **Large fan-out and continue-as-new**, to see where workflow history size
  starts to matter.
- **Another state store** (for example PostgreSQL), to see which behaviors
  are SQL Server specific.
- **Sustained load at volume** with a load generator, measuring per-stage
  latency and the sidecar hop.

## 8. Evidence and Reproduction

```bash
bash scripts/init-env.sh
bash scripts/verify.sh --suite full --keep-awake   # every scenario and drill on fresh stacks, about 90 min
bash scripts/kind-smoke.sh                         # the Kubernetes template on a local kind cluster
```

- Guarantees and their scenarios and drills: [testing.md](testing.md),
  [resilience-drills.md](resilience-drills.md), and the release notes.
- Observed runtime behavior: [versions.md](versions.md).
- Everything that broke, and why: [journal.md](journal.md).
- Architecture and operations: [architecture.md](architecture.md),
  [operations.md](operations.md).
