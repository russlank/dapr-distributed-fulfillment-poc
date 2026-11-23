# Versions and Runtime Compatibility

Pinned versions, and what the pinned Dapr runtime and .NET SDK were observed
to do. The compatibility checks are executable: see
[../spikes/dapr-compat/README.md](../spikes/dapr-compat/README.md). Only
behavior that was run and observed is marked `PASS` or `FAIL`; documentation
alone is never treated as evidence.

## Contents

- [Pinned Versions](#pinned-versions)
- [Foundation Versus the Compatibility Spike](#foundation-versus-the-compatibility-spike)
- [Compatibility Checks](#compatibility-checks)
  - [Summary](#summary)
  - [A1: SQL Server state store for workflows and actors](#a1-sql-server-state-store-for-workflows-and-actors)
  - [A2: Cross-app activities and distribution](#a2-cross-app-activities-and-distribution)
  - [A3: Scheduling an existing instance id](#a3-scheduling-an-existing-instance-id)
  - [A4 and A6: Events, timers, and timeouts](#a4-and-a6-events-timers-and-timeouts)
  - [A5: Raising events to missing or completed instances](#a5-raising-events-to-missing-or-completed-instances)
  - [A7: Durable timers, Scheduler restart, Scheduler data loss](#a7-durable-timers-scheduler-restart-scheduler-data-loss)
  - [A8: Concurrency limits](#a8-concurrency-limits)
  - [A9: Purge and instance-id reuse](#a9-purge-and-instance-id-reuse)
  - [A10: RabbitMQ pub/sub](#a10-rabbitmq-pubsub)
  - [A11: Service invocation retries](#a11-service-invocation-retries)
  - [A12: API and app tokens](#a12-api-and-app-tokens)
  - [A14: Actors](#a14-actors)
  - [A17: Workflow tests](#a17-workflow-tests)
  - [A18: Memory](#a18-memory)
  - [Workflow recovery versus activity re-execution](#workflow-recovery-versus-activity-re-execution)
  - [Further observations](#further-observations)

## Pinned Versions

Pinned 2026-11-18. Rule: a release must be at least two weeks old at pin
time.

| Component | Version | Released | Notes |
|---|---|---|---|
| Dapr runtime (`daprio/daprd`, `daprio/placement`, `daprio/scheduler`) | 1.19.1 | 2026-10-31 | |
| Dapr .NET SDK (`Dapr.Workflow`, `Dapr.Actors.AspNetCore`, `Dapr.AspNetCore`) | 1.19.0 | 2026-10-22 | 1.19.1 (2026-11-10) was too recent; same minor version as the runtime |
| .NET SDK image / runtime image | `mcr.microsoft.com/dotnet/sdk:10.0.403` / `aspnet:10.0.14` | image rebuilt 2026-11-05 | image dates are rebuild dates, not release dates; local builds roll forward to the latest 10.0.4xx patch (`global.json`) |
| SQL Server | `mcr.microsoft.com/mssql/server:2022-CU27-ubuntu-22.04` | image 2026-08-26 | |
| RabbitMQ | `rabbitmq:4.3.6-management` | 2026-10-17 | |
| `Microsoft.Data.SqlClient` | 7.1.1 | 2026-09-29 | |
| `System.Security.Cryptography.Pkcs` | 10.0.14 | 2026-10-25 | explicit reference overrides a vulnerable transitive 9.0.4 |
| `xunit.v3` | 4.0.2 | 2026-10-09 | runs on Microsoft.Testing.Platform (`global.json`) |

Added for the solution foundation (pinned 2026-11-18, same rule):

| Component | Version | Released | Notes |
|---|---|---|---|
| OpenTelemetry .NET (`Extensions.Hosting`, `Exporter.OpenTelemetryProtocol`, `Instrumentation.AspNetCore`, `.Http`, `.Runtime`) | 1.20.1 | 2026-10-20/21 | 1.21.0 (2026-11-11) too recent |
| `System.CommandLine` | 2.0.14 | 2026-10-25 | client simulator |
| OpenTelemetry Collector contrib | `otel/opentelemetry-collector-contrib:0.163.2` | 2026-10-21 | |
| Jaeger | `jaegertracing/jaeger:2.22.0` | 2026-10-06 | query API under `/api/v3` |
| Prometheus | `prom/prometheus:v3.15.1` | 2026-10-21 | |
| Alertmanager | `prom/alertmanager:v0.28.1` | 2025-03-07 | pinned 2026-11-23 (M5 alerting) |
| Grafana | `grafana/grafana:13.3.0` | 2026-10-27 | |
| Toxiproxy | `ghcr.io/shopify/toxiproxy:2.13.0` | 2026-10-14 | |

Added for durable order acceptance (pinned 2026-11-18, same rule):

| Component | Version | Released | Notes |
|---|---|---|---|
| EF Core (`Microsoft.EntityFrameworkCore.SqlServer`, `.Design`; tool `dotnet-ef`) | 10.0.14 | 2026-10-25 | same servicing release as the `aspnet:10.0.14` runtime; 10.0.15 (2026-11-10) too recent; requires SqlClient ≥ 6.1.7, the pinned 7.1.1 is used |
| `Microsoft.AspNetCore.Mvc.Testing` | 10.0.14 | 2026-10-25 | integration tests host the order API in-process |
| `Testcontainers.MsSql` | 4.16.0 | 2026-10-08 | 4.17.0 (2026-11-16) too recent; runs the stack's SQL Server image |

## Foundation Versus the Compatibility Spike

The foundation uses the behavior the spike verified, with these deliberate
differences:

| Aspect | Spike | Foundation | Why |
|---|---|---|---|
| State-store table | `state_v2` next to a corrupted v1 table | `state` (fresh database, component v2 from the start) | no v1 rows exist |
| SQL logins | `sa` for everything | one login per owning service; the state store has its own login on `DaprStateDb` only | database ownership |
| State-store path | direct to SQL Server | through Toxiproxy | drills can cut only this path |
| Component secret names | `SPIKE_*` | `STATE_STORE_CONN`, `RABBIT_CONN` | `secretstores.local.env` refuses names starting with `DAPR_` (observed) |
| Scheduler | ran as root | runs as its image user (65532); a one-shot job hands over the volume | no root containers |
| Who schedules workflows | the app that also hosts them | SQL-owning roles call the workflow host's internal management API | an instance belongs to the app whose sidecar scheduled it (observed) |
| Service invocation retries | resiliency policy (not applied to 5xx) | no invocation policy; caller-side bounded retries | as observed in the spike |
| Concurrency limits | `maxConcurrentActivityInvocations: 2` (to observe the limit) | 100 / 100 | production-like default; the setting is the verified sidecar setting |

## Compatibility Checks

Environment for every check: Docker Desktop 29.9.3 (Compose v5.7.0) on
Windows 11, 20 CPUs, 32 GB; the spike stack with two orchestrator replicas
(app id `spike-orch`) and two worker replicas (app id `spike-worker`), each
with its own `daprd` sidecar; SQL Server and RabbitMQ in containers;
`DAPR_API_TOKEN` and `APP_API_TOKEN` set on every sidecar. Commands are run
from `spikes/dapr-compat`; `bash scripts/run.sh <check>` runs one check, and
its evidence lands in `results/<run-id>/`. Run of record: `20261118-final2`
(fresh stack, all checks, final scripts). Figures from earlier development
runs are given where they varied; the journal describes those runs.

Result values: `PASS` (expected behavior observed), `FAIL` (expected
behavior not observed), `OBSERVED` (behavior is not documented or differs
from the documentation; recorded as found), `UNVERIFIED` (not run yet; the
milestone that will run it is named).

### Summary

| Id | Check | Result |
|---|---|---|
| A1 | SQL Server as workflow and actor state store | `PASS` with component `state.sqlserver` **v2**; `FAIL` with v1 (corrupts binary history) |
| A2 | Cross-app activities through `TargetAppId`, spread over replicas | `PASS` |
| A3 | Scheduling an existing instance id | `OBSERVED`: running → `AlreadyExists`; completed → **replaced by a new run** |
| A4 | External event raised before the workflow waits | `PASS` (buffered) |
| A5 | Raising an event to a missing or completed instance | `OBSERVED`: missing → error `no such instance exists` (SDK docs say silently discarded); completed → silent no-op |
| A6 | Event versus timeout | `PASS` (timeout overload and `WhenAny` with a durable timer) |
| A7 | Durable timers across Scheduler restart and data loss | restart `PASS`; volume loss `OBSERVED`: pending timer lost, an external event recovers the workflow |
| A8 | Workflow/activity concurrency limits | `PASS` as a **sidecar** setting; the SDK option is obsolete and inert |
| A9 | Purge and instance-id reuse | `PASS` |
| A10 | RabbitMQ delivery, retries, dead letter | `PASS` |
| A11 | Service invocation retries from a resiliency policy | `FAIL` for application 5xx responses; transport failures `UNVERIFIED` |
| A12 | Dapr API token and app API token | `PASS` |
| A13 | Workflow versioning API | `PASS` (drill R17, 2026-11-23): `IsPatched` records a patch in a new run's history and answers false when an instance started before the patch replays; an instance whose history has a patch that the running code does not request is reported `Stalled` (not failed, not replayed differently) and resumes once a host with the patch runs it. Defect found 2026-11-23: terminating or purging an instance that recorded a patch never answers, and the instance stays `Stalled` across host and sidecar restarts (unpatched instances terminate in about 1 s); patched instances are not terminated; orders are stopped through SQL-owned paths and the workflow fences itself (operations guide). The Dapr 1.19.2 release notes (2026-11-19) list no fix; recheck on each upgrade |
| A14 | Actor placement, host visibility, recovery after killing the host | `PASS` |
| A15 | Dapr workflow metric names | `PASS` (2026-11-22): `dapr_runtime_workflow_*` execution, operation, activity counts and latencies, scheduling latency, lock wait ([monitoring.md](monitoring.md#dapr-metrics)) |
| A16 | Behavior during a state-store outage | `OBSERVED` (drill R07, 2026-11-23): with only the sidecars' path to the state store cut, workflow starts and raised events fail and are retried by their callers, running instances hold, nothing completes; SQL commits continue; about 15 s after the restore everything resumes, no history lost, no effect repeated |
| A17 | .NET workflow test mechanism | `PASS`: no in-memory harness in the SDK; `WorkflowContext` is abstract and a hand-written fake runs workflows deterministically |
| A18 | Memory footprint | `OBSERVED`: 1.2 GiB for 13 containers on a fresh stack, about 2.0 GiB after 20 minutes of experiments |
| - | Workflow recovery versus activity re-execution after `SIGKILL` | `PASS` (three variants) |

### A1: SQL Server state store for workflows and actors

- **Configuration:** `dapr/components/statestore.yaml`, type
  `state.sqlserver`, `actorStateStore: "true"`, database `DaprStateDb`.
- **Command:** `bash scripts/run.sh a1`
- **Expected:** workflow history is persisted and readable from any sidecar
  of the app; actor state is persisted.
- **Observed with component version v1:** workflows ran to `COMPLETED`, but
  reading their state failed with `failed to unmarshal history event from
  history state key 'history-000000': proto: cannot parse invalid
  wire-format data`. The v1 table stores values in `Data NVARCHAR(MAX)`; the
  component converts the value bytes to a string, so every byte sequence
  that is not valid UTF-8 in the protobuf history was replaced by U+FFFD
  (`FDFF` in the stored UTF-16). Actor state (JSON text) was unaffected.
  Running workflows only worked from the sidecar's in-memory history cache;
  recovery after a restart would read the corrupted rows.
- **Observed with component version v2** (`version: v2`, table
  `state_v2`): the table has `BinaryData varbinary` and `isBinary`; history
  rows are stored with `isBinary = 1`; the completed workflow's state reads
  correctly through both orchestrator sidecars; actor state is stored as
  JSON text.
- **Evidence:** `results/<run>/a1.txt` (state through both sidecars,
  history rows with `isBinary`, actor row).
- **Result:** `PASS` with v2; `FAIL` with v1.
- **Implications:** the state-store component must be `state.sqlserver`
  version `v2`. v1 fails silently while workflows are cached, which no smoke
  test of a running workflow would catch.

### A2: Cross-app activities and distribution

- **Configuration:** `CrossAppWorkflow` on `spike-orch` calls
  `RecordActivity` with `new WorkflowTaskOptions(null, "spike-worker")`.
- **Command:** `bash scripts/run.sh a2` (20 workflows).
- **Expected:** every activity runs in the worker app.
- **Observed:** 20/20 completed; 20 activity executions recorded in
  `SpikeDb`; final orchestrator replica 11 × `orch-1`, 9 × `orch-2`; worker
  replica 13 × `worker-1`, 7 × `worker-2` (a development run: 12/8 and
  10/10). A workflow scheduled through `orch-1` regularly ran on `orch-2`.
- **Result:** `PASS`.
- **Implications:** placement spreads work over replicas but the split is
  not guaranteed even; it is measured, not assumed.

### A3: Scheduling an existing instance id

- **Command:** `bash scripts/run.sh a3`
- **Expected:** undocumented; record the behavior.
- **Observed:** while the instance is running, scheduling the same id fails
  with `AlreadyExists` (`an active workflow with ID ... already exists`).
  After the instance completed, scheduling the same id **succeeds**: the
  completed instance is replaced by a new run (status back to `Running`,
  new `createdAt`), which then completes on its own.
- **Result:** `OBSERVED`.
- **Implications:** a late or repeated start request for a finished order
  would start a second execution. The application must guarantee "one
  execution per order" itself: one start request per order, never start a
  terminal order, and a first workflow step that exits when the order is
  already terminal, with every business effect keyed by a stable operation
  id.

### A4 and A6: Events, timers, and timeouts

- **Configuration:** `EventWorkflow` waits for `go` either through
  `WaitForExternalEventAsync<T>(name, TimeSpan)` or through
  `Task.WhenAny(WaitForExternalEventAsync<T>(name, token), CreateTimer(...))`.
- **Command:** `bash scripts/run.sh a4_a6`
- **Expected:** an event raised before the wait is buffered; the event wins
  when raised; the timeout path runs otherwise.
- **Observed:** event raised during an 8 s timer before the wait →
  `event:early` (both forms); raised while waiting → `event:late` (both
  forms); no event → `timeout:TaskCanceledException` (overload) and
  `timeout:whenany`.
- **Result:** `PASS` (A4, A6).
- **Implications:** waits can combine an event with a durable timer; the
  overload signals the timeout with `TaskCanceledException`.

### A5: Raising events to missing or completed instances

- **Command:** `bash scripts/run.sh a5`
- **Expected (SDK documentation):** events for non-existent or completed
  workflows are silently discarded.
- **Observed:** missing instance → `RpcException` with `no such instance
  exists`, and no instance is created; completed instance → call succeeds,
  status stays `Completed`, nothing happens.
- **Result:** `OBSERVED` (differs from the documentation for missing
  instances).
- **Implications:** a signal dispatcher must not infer delivery from
  success: a completed workflow silently ignores the event. It must check the
  instance state first and treat `no such instance exists` as a known,
  non-transient outcome.

### A7: Durable timers, Scheduler restart, Scheduler data loss

- **Configuration:** Scheduler with `--etcd-data-dir /data/scheduler` on the
  named volume `scheduler-data`; `TimerWorkflow` waits on
  `WhenAny(nudge event, durable timer)`.
- **Commands:** `bash scripts/run.sh a7_restart`, `bash scripts/run.sh a7_volume_loss`
- **Expected:** the timer fires after a Scheduler restart; after data loss
  the behavior was unknown.
- **Observed:** restart 5 s into a 30 s timer → fired by the timer 31 s
  after arming. Volume deleted (Scheduler stopped, container and volume
  removed, started empty) during a 20 s timer → still `Running` 60 s later:
  **the timer was lost**. Raising the `nudge` event completed the workflow
  (`output: nudge`).
- **Result:** restart `PASS`; volume loss `OBSERVED`.
- **Implications:** Scheduler data is as important as the state store: it
  must be persistent and reset together with it. Waiting workflows need a
  recovery path that does not depend on their timer: any wake-up event must
  make the workflow re-check its deadline.

### A8: Concurrency limits

- **Observed while building:** `WorkflowRuntimeOptions.MaxConcurrentActivities`
  and `MaxConcurrentWorkflows` are marked obsolete in SDK 1.19 with "no
  longer does anything - please use the options on the Dapr runtime
  instead".
- **Configuration:** `dapr/config/worker.yaml`,
  `spec.workflow.maxConcurrentActivityInvocations: 2`.
- **Command:** `bash scripts/run.sh a8` (12 activities of 3 s fanned out).
- **Expected:** at most 2 concurrent activities per worker replica.
- **Observed:** maximum overlap 2 on `worker-1`, 2 on `worker-2`, 4 overall
  (from start and end rows in `SpikeDb`).
- **Result:** `PASS`.
- **Implications:** concurrency is configured per sidecar in the Dapr
  `Configuration` resource, not in application code.

### A9: Purge and instance-id reuse

- **Command:** `bash scripts/run.sh a9`
- **Observed:** purging a completed instance returns `true`; the instance no
  longer exists and no state rows remain; scheduling the same id again
  creates a new run that completes. Purging a running instance fails with
  an error (`orchestration has not yet completed`), not with `false`.
- **Result:** `PASS`.
- **Implications:** purge makes an id reusable, which is one more way to
  start a second execution for the same business key; the application-level
  "one execution per order" rule covers it.

### A10: RabbitMQ pub/sub

- **Configuration:** `pubsub.rabbitmq`; declarative subscription with
  `deadLetterTopic: spike-dlq`; resiliency `pubsubRetry` (constant 1 s, 2
  retries) on inbound pub/sub.
- **Command:** `bash scripts/run.sh a10`
- **Observed:** `ok-*` delivered once; `retry2-*` (500 twice, then success)
  delivered 3 times; `poison-*` (always 500) delivered 3 times, then once to
  the dead-letter topic; `drop-*` (`{"status":"DROP"}`) delivered once and
  then **also** sent to the dead-letter topic.
- **Result:** `PASS`.
- **Implications:** bounded redelivery and dead-lettering come from the
  sidecar; a `DROP` answer is dead-lettered too, so the dead-letter consumer
  sees both poison and deliberately dropped messages.

### A11: Service invocation retries

- **Configuration:** resiliency target `apps: spike-worker` with
  `invokeRetry` (constant 300 ms, 3 retries, `matching.httpStatusCodes:
  "500-599"`); the call goes through the sidecar HTTP API
  (`/v1.0/invoke/spike-worker/method/flaky`) because
  `DaprClient.InvokeMethodAsync` is obsolete in SDK 1.19.
- **Command:** `bash scripts/run.sh a11`
- **Expected:** a target failing twice succeeds on the third attempt.
- **Observed:** the target saw exactly **one** attempt and the caller got
  the 500. Repeated with `httpStatusCodes: "500"` and without `matching`,
  each time after restarting the calling sidecar (resiliency is reloaded only
  on `SIGHUP`): still one attempt. The same resiliency file's pub/sub retries
  work.
- **Result:** `FAIL` for application 5xx responses; retries on transport
  failures (target unreachable) `UNVERIFIED`, to be tested with the shipping
  integration.
- **Implications:** do not rely on sidecar retries for service invocation.
  Callers retry themselves with backoff, and receivers are idempotent.

### A12: API and app tokens

- **Command:** `bash scripts/run.sh a12`
- **Observed:** sidecar without token → 401, with token → 200; worker app
  called directly without the app token → 401; through the sidecar → 200.
  Workflows, actors, pub/sub, and invocation all worked with both tokens
  enabled.
- **Result:** `PASS`.

### A14: Actors

- **Command:** `bash scripts/run.sh a14`
- **Observed:** 8 actors spread over the two worker replicas (4 and 6; a
  development run: 3 and 7); the
  host is visible in the method response and in each sidecar's
  `/v1.0/metadata`. After `SIGKILL` of the replica hosting an actor, the
  next call ran on the other replica and continued the persisted count
  (2 → 3) at once.
- **Result:** `PASS`.
- **Implications:** actor state survives the host; the actor host can be
  identified from application output or sidecar metadata.

### A17: Workflow tests

- **Observed:** `Dapr.Workflow` 1.19 has no in-memory test harness;
  `Dapr.Testcontainers` 1.19 exists for container-based integration tests.
  `WorkflowContext` is an abstract class with 17 abstract members and an
  accessible constructor.
- **Command:** `cd src && dotnet test DaprCompat.slnx`
- **Result:** `PASS`: 6 deterministic tests run the spike workflows on a
  hand-written `FakeWorkflowContext` (events supplied up front, timers fire
  immediately or never), including the SDK's own timeout overload.
- **Implications:** workflow logic can be unit-tested without a runtime;
  runtime behavior is tested against the real stack.

### A18: Memory

- **Command:** `bash scripts/run.sh a18` (`docker stats`).
- **Observed:** 1.2 GiB for 13 containers right after a fresh start and the
  experiment suite (run of record); about 2.0 GiB after 20 minutes of
  repeated experiments, mostly SQL Server growing to 1.4 GiB. Otherwise:
  RabbitMQ about 175 MiB, Scheduler 42 MiB, Placement 14 MiB, each app
  container 31-60 MiB, each sidecar 32-34 MiB.
- **Implications:** an app+sidecar pair costs about 65-95 MiB at rest; the
  full fulfillment stack fits a laptop comfortably at rest. Under load,
  SQL Server dominates: 1.1-1.4 GiB during the contention scenario
  ([dapr-vs-alternatives.md](dapr-vs-alternatives.md)); the replicas stack
  has 37 containers and about 2.4 GiB at rest.

### Workflow recovery versus activity re-execution

- **Commands:** `bash scripts/run.sh recover_orch recover_worker recover_pool`
- **Orchestrator replica killed (`SIGKILL`) during a 20 s timer:** the
  workflow completed on the other replica; the completed first activity ran
  **once** (the workflow was recovered from history, not re-executed).
- **Worker replica killed while a 15 s activity ran:** the activity started
  again on the other worker about 1 s later and completed; 2 starts, 1 end
  (the activity was **executed again**: at-least-once).
- **Whole orchestrator pool killed for 25 s across a 10 s timer:** after the
  restart the timer fired late, the workflow completed, the first activity
  still ran once.
- **Result:** `PASS`.
- **Implications:** recovery replays history and does not repeat completed
  activities, but an activity interrupted mid-execution runs again; every
  activity with side effects must be idempotent.

### Further observations

- Dapr 1.19 Scheduler can serve actor placement itself
  (`--placement-enabled`); with a separate Placement service the sidecars log
  "Scheduler cluster does not serve actor placement, using the placement
  service".
- Workflows run on internal actor types per app (`…workflow`, `…activity`,
  `…retentioner`); the retentioner is the 1.19 workflow retention component.
- Resiliency resources are hot-reloaded only on `SIGHUP`; changing the file
  alone had no effect.
