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
cd src && dotnet test Fulfillment.slnx -c Release
```

Deterministic, no infrastructure. They cover operation keys, build identity,
the caller-side invocation retries (5xx and transport errors retried with the
same idempotency key, 4xx not retried, bounded attempts), the runtime probe
workflow on a fake workflow context (execution claim rejection, step order,
target apps, timer), host construction with and without an OTLP endpoint,
that the external simulators reference no Dapr assembly, and that failure
injection exists only in drill builds. `ci/dotnet-gates.sh` runs them in the
release flavor and again in the drill flavor; the flavor tests assert the
opposite result in each.

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

## Drills

Drills run only against a drill stack (`bash scripts/stack-up.sh --drill`);
see [resilience-drills.md](resilience-drills.md).

| Drill | Question | PASS when |
|---|---|---|
| R00 failpoint self-test | when the inventory process dies right after its effect commits and before Dapr gets the activity result, does the activity run again, and does the effect stay single? | the failpoint fired (persisted `FiredAt`) and the process exited; after restart, 2 committed attempts and 1 effect; probe `Completed`; execution claimed once |

## Build Gates

`bash ci/dotnet-gates.sh` (restore, build with warnings as errors, unit
tests, format check, drill-flavor tests) and `bash ci/compose-config.sh`
(Compose file valid for every profile). Both run in CI.
