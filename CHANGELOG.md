# Changelog

Releases of the PoC; each tag `v<version>` marks a verified milestone.
Details of a release: `docs/releases/`. What broke on the way:
[docs/journal.md](docs/journal.md).

## Unreleased (0.3.0-alpha)

- Development towards M2 (inventory contention and idempotent activities).

## v0.2.0: Durable order acceptance, execution fence, cross-replica work (M1)

- Idempotent order acceptance on any order API replica; one start request
  per order, started once in effect by a leased start dispatcher through the
  workflow host's management API; an execution fence on terminal orders.
- Simulated fulfillment: validation, parallel compute checks, keyed
  `stub-` effects, one terminal transaction.
- Operator reviews from 1,000.00: SQL-decided approvals with an immutable
  audit, durable signals to the waiting workflow, expiry by SQL time.
- Client simulator: seeded, replayable workloads, duplicates, conflicts,
  approvals, inspection.
- Scenarios S02, S04, S09, S23, S24, S43, S49, S51, S52 and drills R01, R02
  A-D, R03, R06 (compute) `PASS`.
- Limitations and deferred work: [docs/releases/v0.2.0.md](docs/releases/v0.2.0.md).

## v0.1.0: Repositories, runtime compatibility, stack, CI (M0)

- Dapr 1.19 compatibility spike, Docker Compose stack with every role and its
  sidecar, health and identity endpoints, telemetry, failure-injection
  harness (drill builds only), CI gates on two providers.
- Note: the build at this tag reports version `0.1.0-dev` (the prerelease
  suffix was still set); releases from v0.2.0 on have no suffix.
