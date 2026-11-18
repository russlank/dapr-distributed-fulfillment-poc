# Getting Started

## Prerequisites

- Docker with Compose v2 (Docker Desktop on Windows or macOS, or Docker Engine
  on Linux); about 4 GB of free memory for the core profile.
- bash: Git Bash on Windows, any bash on Linux or macOS.
- Python 3 (used by the scripts for JSON and secrets).
- .NET SDK 10.0.4xx for building and unit tests outside containers
  (`src/global.json` pins 10.0.403 and rolls forward to the latest patch).

## Start the Stack

```bash
bash scripts/init-env.sh        # writes .env with random secrets (git-ignored); keeps an existing one
bash scripts/stack-up.sh        # builds the images, runs the init jobs, waits until every service is ready
```

`stack-up.sh` prints each service's identity (component, commit, tree state,
build flavor) once it is ready. Ready means the app is up, its sidecar
answers, and its own database accepts connections.

Options: `--replicas` (default replica counts; a second order API on port
8081), `--scale` (maximum counts), `--drill` (drill images with failure
injection, as a separate Compose project `fulfillment-drill`), `--no-build`.

## Check It

```bash
bash scripts/scenarios/S00-runtime-smoke.sh     # about 3 minutes; PASS/FAIL per check
```

Or run one probe by hand (the admin key is in `.env`):

```bash
source .env
curl -s -X POST http://127.0.0.1:8080/system/probes/runtime/hello-1 -H "X-Api-Key: $ADMIN_API_KEY" \
  -H 'content-type: application/json' -d '{"timerSeconds":3}'
curl -s http://127.0.0.1:8080/system/probes/runtime/hello-1 -H "X-Api-Key: $ADMIN_API_KEY"
```

## Local Endpoints

All bound to `127.0.0.1`.

| URL | What |
|---|---|
| http://127.0.0.1:8080 | order API (`/health/live`, `/health/ready`, `/system/component-identity`, probes) |
| http://127.0.0.1:8090, :8091 | carrier and payment provider simulators |
| http://127.0.0.1:16686 | Jaeger (traces) |
| http://127.0.0.1:9095 | Prometheus |
| http://127.0.0.1:3000 | Grafana (user `admin`, password `GRAFANA_ADMIN_PASSWORD` in `.env`) |
| http://127.0.0.1:15672 | RabbitMQ management (user `fulfillment`) |
| 127.0.0.1:14330 | SQL Server (for inspection; `sa` password in `.env`) |

Sidecar ports are not published.

## Stop and Reset

```bash
bash scripts/stack-down.sh      # stop; keep data
bash scripts/reset.sh --yes     # stop and delete all data (SQL and Scheduler volumes together)
```

## Build and Test Without Containers

```bash
bash ci/dotnet-gates.sh         # restore, build, unit tests, format check, drill-flavor tests
```
