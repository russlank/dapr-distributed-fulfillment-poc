# Operations

## Scripts

| Script | Does |
|---|---|
| `scripts/init-env.sh` | writes `.env` with random secrets if missing |
| `scripts/stack-up.sh [--replicas] [--scale] [--drill] [--no-build]` | builds, starts, waits for the one-shot jobs, then for every service's readiness; prints identities |
| `scripts/stack-down.sh` | stops the stack, keeps data |
| `scripts/reset.sh --yes` | stops the stack and deletes all data volumes |
| `scripts/scenarios/S00-runtime-smoke.sh` | runtime smoke checks (see [testing.md](testing.md)) |
| `scripts/drills/*.sh` | resilience drills, drill stack only |

## Health

Every service has `GET /health/live` (process up) and `GET /health/ready`
(process up, local sidecar healthy where it has one, its own database
reachable), and `GET /system/component-identity` (component, replica,
version, commit, tree state, branch, build flavor, failpoints, environment).
Container health checks use the live endpoint; `stack-up.sh` waits for ready.

## Startup Order

`sqlserver` (healthy) → `db-init` (databases and logins; must exit 0) and
`scheduler-volume-init` (volume ownership; must exit 0) → control plane
(Placement, Scheduler), RabbitMQ, Toxiproxy, telemetry → apps → their
sidecars. `docker compose up --wait` cannot be used alone because it fails
as soon as a one-shot job exits, even with code 0; `stack-up.sh` waits for
each job explicitly.

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
