# Dapr Distributed Fulfillment PoC

A reference implementation of distributed order fulfillment with Dapr
Workflow, Actors, and Pub/Sub on .NET 10, with SQL Server as the source of
business truth, external client, carrier, and payment-provider simulators,
and scripted failure drills. Synthetic data only; not production software.

**Status:** durable orders with a simulated fulfillment. Orders are accepted
idempotently, started once in effect from any order API replica, validated,
checked in parallel on the compute workers, and completed by simulated
fulfillment steps, with every business effect keyed and stored once.
Reservation, payment, shipping, human review, and event distribution are not
built yet. Every role runs with its own Dapr sidecar, reports its identity
and health, and exports telemetry; scenario and drill scripts prove the
behavior from the databases.

## Quick Start

```bash
bash scripts/init-env.sh                      # .env with random secrets
bash scripts/stack-up.sh                      # build, start, wait until ready
bash scripts/scenarios/S00-runtime-smoke.sh   # PASS/FAIL runtime checks
bash scripts/reset.sh --yes                   # remove everything, including data
```

Details: [docs/getting-started.md](docs/getting-started.md).

## Documentation

| Read | For |
|---|---|
| [docs/architecture.md](docs/architecture.md) | roles, authority, workflow management, runtime components |
| [docs/getting-started.md](docs/getting-started.md) | prerequisites, startup, endpoints |
| [docs/configuration.md](docs/configuration.md) | secrets, settings, Dapr components, profiles, build flavors |
| [docs/operations.md](docs/operations.md) | scripts, health, restarts, reset, troubleshooting |
| [docs/testing.md](docs/testing.md) | unit tests, scenarios, drills, what each check proves |
| [docs/resilience-drills.md](docs/resilience-drills.md) | failure injection and its safety |
| [docs/monitoring.md](docs/monitoring.md) | traces, metrics, dashboards |
| [docs/security.md](docs/security.md) | trust boundaries and secrets |
| [docs/versions.md](docs/versions.md) | pinned versions and observed Dapr runtime behavior |
| [docs/journal.md](docs/journal.md) | what broke and what changed because of it |
| [spikes/dapr-compat/README.md](spikes/dapr-compat/README.md) | the Dapr compatibility spike |

## Versions

Pinned 2026-11-18 (each release at least two weeks old); all pins, dates, and
observed runtime behavior: [docs/versions.md](docs/versions.md).

| Component | Version |
|---|---|
| .NET SDK / runtime | 10.0.403 / 10.0.14 |
| Dapr runtime (daprd, Placement, Scheduler) | 1.19.1 |
| Dapr .NET SDK | 1.19.0 |
| EF Core | 10.0.14 |
| SQL Server | 2022 CU27 |
| RabbitMQ | 4.3.6 |
| OpenTelemetry .NET / Collector | 1.20.1 / 0.163.2 |
| Jaeger / Prometheus / Grafana | 2.22.0 / 3.15.1 / 13.3.0 |
| Toxiproxy | 2.13.0 |

## Layout

```text
src/            .NET solution (Fulfillment.slnx): roles, simulators, shared libraries, tests
dapr/           Dapr components and sidecar configuration
db/init/        database and login creation
deploy/compose/ Compose support files (Toxiproxy)
monitoring/     Collector, Prometheus, Grafana provisioning
scripts/        stack, scenario, drill, and check scripts
ci/             build gates shared by CI providers
docs/           documentation
spikes/         the Dapr compatibility spike (kept as reproducible evidence)
```

License: [MIT](LICENSE).
