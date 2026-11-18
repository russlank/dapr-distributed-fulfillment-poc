# Dapr 1.19 Compatibility Spike

A deliberately small experiment that checks how the pinned Dapr runtime and
.NET SDK actually behave before the fulfillment services are built. It
contains no business logic. Results and their consequences are recorded in
[../../docs/versions.md](../../docs/versions.md) and
[../../docs/journal.md](../../docs/journal.md).

## What Runs

| Container | Role |
|---|---|
| `orch-1`, `orch-2` (+ `-dapr` sidecars, app id `spike-orch`) | workflows, a local activity, and a `/ctl` control API used by the scripts |
| `worker-1`, `worker-2` (+ `-dapr` sidecars, app id `spike-worker`) | cross-app activities, `CounterActor`, pub/sub subscriber, invocation target |
| `sqlserver` (+ one-shot `sql-init`) | `DaprStateDb` (Dapr state store, component `state.sqlserver` v2) and `SpikeDb.dbo.Evidence` (every counted side effect) |
| `rabbitmq` | pub/sub broker |
| `placement`, `scheduler` | Dapr control plane; Scheduler data on the named volume `scheduler-data` |

One executable (`src/DaprCompat.App`) serves both roles (`ROLE=orchestrator`
or `worker`). Each replica is an app container plus its own `daprd` sidecar
sharing its network namespace.

## Versions

Pinned on 2026-11-18. Each version was released at least two weeks earlier,
except where noted.

| Component | Version |
|---|---|
| Dapr runtime images (`daprd`, `placement`, `scheduler`) | 1.19.1 |
| Dapr .NET SDK (`Dapr.Workflow`, `Dapr.Actors.AspNetCore`, `Dapr.AspNetCore`) | 1.19.0 (1.19.1 was published 2026-11-10, too recent) |
| .NET SDK / runtime images | `sdk:10.0.403` / `aspnet:10.0.14` (`global.json` rolls forward to the latest 10.0.4xx patch locally) |
| SQL Server image | `2022-CU27-ubuntu-22.04` |
| RabbitMQ image | `4.3.6-management` |
| `Microsoft.Data.SqlClient` / `System.Security.Cryptography.Pkcs` | 7.1.1 / 10.0.14 (overrides a vulnerable transitive version) |
| `xunit.v3` | 4.0.2 |

## Run It

Requirements: Docker with Compose v2, bash (Git Bash on Windows), Python 3,
.NET 10 SDK for the unit tests.

```bash
cd spikes/dapr-compat
cp .env.example .env            # then replace every change-me value
docker compose up -d --build
bash scripts/run.sh             # all experiments, about 15 minutes
bash scripts/run.sh a3 a5       # selected experiments
cd src && dotnet test DaprCompat.slnx   # deterministic workflow unit tests
docker compose down -v          # remove everything, including the Scheduler volume
```

Each run writes `results/<run-id>/results.jsonl`, `run.log`, and one evidence
file per experiment (git-ignored). `a7_volume_loss` deletes the Scheduler
volume on purpose and runs last.

## Experiments

| Function | Checks |
|---|---|
| `a1` | SQL Server state store for workflow history (binary) and actor state |
| `a2` | cross-app activity through `TargetAppId`; spread over replicas |
| `a3` | scheduling an existing instance id, running and completed |
| `a4_a6` | event raised before and after the wait; event versus timeout |
| `a5` | raising an event to a missing or completed instance |
| `a7_restart`, `a7_volume_loss` | durable timer across a Scheduler restart and after Scheduler data loss |
| `a8` | activity concurrency limit set on the sidecar |
| `a9` | purge and instance-id reuse |
| `a10` | RabbitMQ delivery, retries, dead letter, DROP |
| `a11` | service invocation retries from a resiliency policy |
| `a12` | `DAPR_API_TOKEN` and `APP_API_TOKEN` |
| `a14` | actor placement, host visibility, state after killing the host |
| `recover_orch`, `recover_worker`, `recover_pool` | workflow recovery versus activity re-execution after `SIGKILL` |
| `a18` | memory footprint |

## Limits

Local experiment only: the SQL `sa` login, host ports bound to `127.0.0.1`
(including sidecar and Placement ports, published only to observe them), and
the Scheduler container running as root (a fresh named volume is root-owned
and the image is distroless).
