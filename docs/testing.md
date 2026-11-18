# Testing

Tests verify behavior, not that a call returned 200. Checks read the
authoritative databases, prove that an injected failure actually happened,
and end with an explicit `PASS`, `FAIL`, or `UNVERIFIED`.

## Identifiers

| Prefix | Meaning | Where |
|---|---|---|
| `Sxx` | scenario: expected behavior, run on the stack | `scripts/scenarios/` |
| `Rxx` | resilience drill: one injected failure with proof that its window was hit | `scripts/drills/` |
| `Ixx` | invariant checked by scenarios and drills | named in check output |
| `Axx` | Dapr runtime compatibility check | [versions.md](versions.md), `spikes/dapr-compat/` |

## Unit Tests

```bash
cd src && dotnet test --project Fulfillment.UnitTests -c Release
```

Deterministic, no infrastructure. They cover order request parsing, contract
bounds, the idempotency hash (invariant to member order and whitespace,
sensitive to every field and to line order), messages that never quote
values, the order model and instance-id rule, the fulfillment workflow shell
on a fake context (claim on `order-api`, fenced end), the classification of
workflow-host answers (always the management API, one idempotency key),
dispatcher backoff, the management policy entry for the fulfillment workflow, operation keys, build identity,
the caller-side invocation retries (5xx and transport errors retried with the
same idempotency key, 4xx not retried, bounded attempts), the runtime probe
workflow on a fake workflow context (execution claim rejection, step order,
target apps, timer), host construction with and without an OTLP endpoint,
that the external simulators reference no Dapr assembly, and that failure
injection exists only in drill builds. `ci/dotnet-gates.sh` runs them in the
release flavor and again in the drill flavor; the flavor tests assert the
opposite result in each.

## Integration Tests

```bash
bash ci/integration-tests.sh
```

The real order API (its `Program`, DI, endpoints, and EF Core model) runs
in-process against SQL Server: a container from the stack's pinned image
(Testcontainers, Docker required), or the server named by
`FULFILLMENT_TEST_SQL_SERVER` (a master-database connection string, for CI
service containers). Each test class gets its own freshly migrated database
with read committed snapshot on, like `OrderDb`. Assertions read the
persisted rows with plain SQL, independent of the model under test.

| Area | What is proven |
|---|---|
| acceptance | one transaction writes order, lines, first status row, key with the expected hash, and a `Pending` start request with the derived instance id; the business view carries no execution state |
| duplicates | same key and payload (other member order and whitespace) returns the original order and writes nothing; same key with another quantity, line set, or customer returns `422` and leaves the original untouched |
| concurrency | two API hosts on one database (two replicas): 24 concurrent submissions with one key create one order; with conflicting payloads, one wins and the rest agree or get `422`; 24 distinct keys create 24 orders and start requests |
| forced key race | another transaction holds the key uncommitted; the submission's insert is shown blocked on it, the commit turns it into a unique violation, and the result is the replay or `422`, with no rows of the loser |
| rollback | a failure injected after the inserts executed and before the commit leaves no row of any table and does not consume the key; the retry is accepted |
| restart | after the host is disposed and connection pools cleared, a new host returns the same order for the retry |
| contract | invalid JSON, unknown, duplicate, missing, or null members, bounds, missing or invalid keys, oversize bodies, missing or wrong API key: refused with a stable code, nothing persisted, no input echoed |
| dispatcher | real dispatchers (separate identities, like replicas) on one database against a fake workflow host: start and mark `Started`; existing instance; lost answer after the instance was created (`Pending` with backoff, then found on the next attempt, one instance); refusal (long backoff); terminal guard (closed, no call); a held lease is not claimed; crash after starting (lease expiry, recovery by another dispatcher, the old token changes nothing); a slow start outliving its lease (overlapping attempts, the stale one `LeaseLost`); three concurrent dispatchers start 30 orders exactly once; the hosted dispatcher starts accepted orders and is not blocked by a slow start |
| fence | the claim counts every claim of a non-terminal order and audits it; terminal orders (all four statuses) are rejected, counted, and audited; 20 concurrent claims all count; an unknown order fails without writing |
| schema | migrations apply twice without change; the API refuses to start on an unmigrated database; the database rejects a second start request, a second order per key, duplicate instance ids or history sequences, out-of-range lines, unknown statuses, and inconsistent start-request lifecycle or lease values |

## Scenarios

### S00 Runtime Smoke

`bash scripts/scenarios/S00-runtime-smoke.sh` on a ready stack. Evidence:
`artifacts/scenarios/S00-<time>/` (results, identities, probe states).

| Check | Condition | Proof | PASS when |
|---|---|---|---|
| S00-1 identity | every service of the stack | `/system/component-identity` of each service | each reports the checked-out commit; release builds report `failpoints: absent` |
| S00-2 recovery | `SIGKILL` of the workflow host (app and sidecar) inside the probe's 45 s durable timer | step 2's effect does not exist yet at kill time (otherwise the check fails as "window missed") | probe `Completed`; 2 effects; 1 committed attempt per step, so the completed step 1 was not executed again |
| S00-3 persistence | cold restart of the order API and workflow host (new app and sidecar processes, empty caches) | workflow state read back through the state store afterwards | history rows exist, all stored binary, none containing U+FFFD; the state reads `Completed`. A `state.sqlserver` v1 component would fail here |
| S00-4 fence | the completed probe's instance id is scheduled again on purpose (Dapr replaces the completed instance) | the claim's rejection counter in `OrderDb` | the new run ends `AlreadyTerminal`; the effect count is unchanged |
| S00-5 access | the workflow host's management API is called through real service invocation by `compute-worker` (query, start), and by `order-api` with an unknown workflow type and with a replacement of a non-probe instance | HTTP status seen by the caller | `403`, `403`, `400`, `403`; the authorized query by `order-api` returns `200` |

### Order Acceptance Check

`bash scripts/checks/order-acceptance-check.sh` on a ready stack (start it
with `--replicas` for ACC-4). Evidence: `artifacts/checks/ACC-<time>/`.

| Check | PASS when (from `OrderDb` rows) |
|---|---|
| ACC-1 accept | `201`; order `Submitted`, both lines, history row 1, key, start request (`Pending`, or already `Started`) with `fulfill-{orderId:N}` |
| ACC-2 replay | same key and payload through the second replica: `200`, same order, row counts unchanged |
| ACC-3 conflict | same key, other payload: `422 idempotency-key-reused`, row counts unchanged |
| ACC-4 replicas | 20 concurrent submissions with one key, alternating `order-api` and `order-api-2`: one `201`, nineteen `200`, one order (`UNVERIFIED` without the second replica) |
| ACC-5 restart | after a cold restart of the order API the retry returns the same order |
| ACC-6 dispatched | the start request becomes `Started`; the workflow ran (`Completed`, `ShellClaimed`, at least one claim); its state in `DaprStateDb` belongs to `workflow-host` only, none to `order-api`'s own sidecar |

### S51 Concurrent Workflow Start

```bash
START_DISPATCHER_LEASE=00:00:02 bash scripts/stack-up.sh --drill --replicas
COMPOSE_PROJECT_NAME=fulfillment-drill BUILD_FLAVOR=drill DAPR_POC_ENV=drill START_DISPATCHER_LEASE=00:00:02 \
  bash scripts/scenarios/S51-concurrent-start.sh
```

20 orders are accepted on both order API replicas. The first start attempt of
each is held for 6 s before its call (failpoint `BeforeWorkflowStartCall`,
`Delay`), three times the lease, so either replica claims the request again
while the first attempt is still in flight. Evidence:
`artifacts/drills/S51-<run>/` (attempt log, execution views, result).

| Check | PASS when (from `OrderDb` and `DaprStateDb` rows) |
|---|---|
| S51-race | at least one order has overlapping attempts from both replicas, and the earlier ended `LeaseLost` (otherwise `UNVERIFIED`, which fails the run) |
| S51-start | 20 orders, 20 start requests, all `Started` without error code or lease; every `LeaseLost` attempt has another attempt that recorded the start |
| S51-instance | every instance `Completed` on `workflow-host` with the shell outcome; claim rows equal `ExecutionClaims` (at least 1); no fence rejection; no workflow state under `order-api` |
| S51-accept | 5 keys × 4 concurrent identical submissions over both replicas: one order per key (5 × `201`, 15 × `200`) |

With the fulfillment steps in place, S51 also asserts that every order ends
`Completed` with one effect per operation key.

## Drills

Drills run only against a drill stack (`bash scripts/stack-up.sh --drill`);
see [resilience-drills.md](resilience-drills.md).

| Drill | Question | PASS when |
|---|---|---|
| R00 failpoint self-test | when the inventory process dies right after its effect commits and before Dapr gets the activity result, does the activity run again, and does the effect stay single? | the failpoint fired (persisted `FiredAt`) and the process exited; after restart, 2 committed attempts and 1 effect; probe `Completed`; execution claimed once |

## Build Gates

`bash ci/dotnet-gates.sh` (restore, build with warnings as errors, unit
tests, format check, drill-flavor build and unit tests),
`bash ci/integration-tests.sh` (SQL Server integration tests), and
`bash ci/compose-config.sh` (Compose file valid for every profile). All run
in CI: on GitHub Actions the integration tests use the runner's Docker; on
Woodpecker they use a SQL Server service container.
