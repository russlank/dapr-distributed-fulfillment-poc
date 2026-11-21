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

## Verification Runs

`scripts/verify.sh` runs the checks of a suite one after another, writes each
step's log, exit code, and duration to `artifacts/verify/<run>/`
(`steps.jsonl`, `summary.json`), and ends with a table that compares every
step with the previous run of the same suite (a step more than 50 % and 30 s
slower is flagged). It exits 1 if any step failed.

| Command | What runs | Use |
|---|---|---|
| `scripts/verify.sh --suite full --keep-awake` | build gates, Compose check, integration tests; then from fresh volumes: release `--replicas` (S00, OBS, ACC, S02, S09, S23, S04, S24, S43, S03 with actors and SQL-only), drill `--replicas` with a 2 s start lease (every M1-M3 scenario and drill), core drill (R00, S49, R06 compute); a version and commit check of every service on each stack. Needs a clean tree | milestone and release verification |
| `scripts/verify.sh --suite dev` | the drill `--replicas` phase only, every scenario and drill once; volumes kept | regression after a larger change |
| `scripts/verify.sh --only m3 --reuse` | a group (`smoke`, `m1`, `m2`, `m3`, `release`, `core`) or step ids (`R09,S39`; `A:S04` forces the release stack) | the area being changed |

`--reuse` skips the stack start when the running stack was built from `HEAD`
with the same tree state and flavor. With uncommitted changes it cannot tell
whether the code changed since the build: rebuild (omit `--reuse`) after
changing anything under `src/`; script changes need no rebuild.
`--keep-awake` keeps a Windows machine from idle sleep while the run lasts.

On the release stack S00, OBS, and ACC run with the carrier simulator's own
pace (10 s per status), so the release configuration is exercised end to end
as shipped; the runner then recreates the carrier with
`CARRIER_SIM_STEP=00:00:02` (step `carrier-fast`) for the remaining release
scenarios, which only need completed orders and would otherwise wait 30 s
per order. The system under test keeps its release time profile; only the
external simulator's pace changes. `--release-carrier-step default` keeps
10 s for every release step. The timing-sensitive carrier tests (callback
anomalies, late conflicts, lost callbacks) run on the drill stack with its
own 2 s profile, as before.

Typical durations on the development laptop:

| Run | Before | After |
|---|---|---|
| `--suite full` (fresh volumes) | 65.3 min (65 steps) | 56.1 min (63 steps: the SQL-only switch and switch-back are part of the `S03-sqlonly` step; the same scenarios and drills) |
| release phase of `full` (reset, start-up, 11 steps, teardown) | 23.0 min | 12.6 min |
| drill `--replicas` phase of `full` (33 scenarios and drills) | 36.7 min | 37.3 min (unchanged within run-to-run variation) |
| `--only release` on a stopped stack (start-up included) | - | 10.9 min |
| `--only m3 --reuse` (17 scenarios and drills) | - | 13.6 min (sum of the steps in the full run) |
| fulfillment recovery check (REC) | 166 s | 123 s (the diagnostic call during the outage is bounded to 10 s) |
| `--only smoke --reuse` (S02, S04, S13, S29, S39) | - | 2.0 min |

The remaining time is in the guarantees themselves: review deadlines (20 s
per round), deliberate outages (R06: 50 s), kills and restarts of replicas
(7-20 s each), contention runs of 200-500 orders, and the start-up of a stack
(about 80-90 s including the image build). These are not shortened.

## Unit Tests

```bash
cd src && dotnet test --project Fulfillment.UnitTests -c Release
```

Deterministic, no infrastructure. They cover order request parsing, contract
bounds, the idempotency hash (invariant to member order and whitespace,
sensitive to every field and to line order), messages that never quote
values, the order model and instance-id rule, the fulfillment workflow on a
fake context (every step on its owning app in order; both checks scheduled
before either completes; a terminal order found at any step ends the run;
rejection ends it before the checks; the stored risk result, not the recomputed one, decides about the review in both directions; the review loop: approval by signal after a Pending read, a decision before the wait, a timer before the SQL deadline (waits again, never in the past), an approval seen after the deadline, expiry into round 2, final expiry, an expiry that finds the review decided, a rejection; the same input twice gives the same calls, inputs, and timers), the signal dispatcher's decision table, the decision hash and contract, the review time profiles, the
key scheme and the stub price list and risk rule, the validation rules, the
classification of a fenced dispatcher update (`LeaseLost`, `AlreadyClosed`,
`Unexpected`), the classification of workflow-host answers (always the
management API, one idempotency key), dispatcher backoff, the management policy entry for the fulfillment workflow, operation keys, build identity,
the caller-side invocation retries (5xx and transport errors retried with the
same idempotency key, 4xx not retried, bounded attempts), the runtime probe
workflow on a fake workflow context (execution claim rejection, step order,
target apps, timer), host construction with and without an OTLP endpoint,
that the external simulators reference no Dapr assembly, the client simulator (conflict variants distinct for the server's request hash and valid, the conflict verdicts, the generator's reference values and a pinned workload digest, equal requests for equal run id and seed, other seeds and run ids, independence of the workload size, 500 generated orders valid for the order API with distinct SKUs, its stub price list equal to the compute worker's, run ids, which answers are ambiguous, retry delays, replica assignment, exit-code priority, decision ids, masking of keys in results, the new-run and replay judgements, and its HTTP layer on a scripted handler: an ambiguous answer resent byte for byte with the same key to the next replica, transport errors and timeouts ambiguous, refusals not retried, nothing sent without a key, CLI usage errors exit 2 without echoing credentials), and that failure
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
| start acknowledgement | forced order of events, no timing: the instance is created, the workflow's real steps complete the order (the request is closed), and only then does the start answer reach the dispatcher: the attempt ends `AlreadyClosed`, the request stays `Closed` (never `Started`), nothing is started again; the same with a lost answer (not reopened with backoff); a stale dispatcher overtaken by a later claim is still `LeaseLost` after the completion; an unknown writer's change is `Unexpected` (logged as an error) and left alone |
| fulfillment steps | all steps: `Completed`, history `Submitted`/`Validating`/`Completed`, one effect per key, request `Closed`; repeated and 20 concurrent validations transition once; a duplicate SKU is `Rejected` with the request closed in the same transaction; a repeated simulated step is applied once and counted as a replay; a crash after its commit (before the result is returned) is retried on another replica and finds the effect; a fault after each statement of the terminal transaction (status, history, effect, request close) rolls all of them back and the retry completes; a crash after the terminal commit changes nothing on retry; 20 concurrent completions make one transition; simulated steps racing the completion never add an effect after it; steps on a terminal order change nothing; completion of an order that is not `Validating` fails without change; repeated check results keep the first |
| reviews | no-review orders create no review; the stored risk result governs although a replay disagrees (both directions; a required review cannot be skipped); round 1 created once under 10 concurrent creations with one transition; the decision endpoint's contract (`200`, replay, `422` reuse, `409` already decided, `404`, `403` operator, `401` customer key, `400`); 20 concurrent conflicting decisions: one commits, one decision, one signal; a decision at the SQL deadline is refused without writing, one just before is accepted and cannot be expired afterwards; a fault after each statement of the decision transaction rolls back review, decision, and signal; a crash after the commit keeps the decision and its pending signal; decisions are immutable (update refused); approval → resolve → Reserving → Completed with history `Submitted, Validating, AwaitingApproval, Reserving, Completed`; rejection rejects the order and closes its start request; expiry only by SQL time, then round 2 (`409 review-superseded` for round 1), then final rejection (`409 review-expired`) |
| signals | running instance: raised once, `Forwarded`; workflow not started: kept `Pending` without a raise, delivered after the start; terminal instance: `Discarded`, decision kept; missing instance: `Stuck`; an ambiguous or not-running answer: backoff, then `Forwarded`; a crash after the raise: raised again after the lease, the stale owner `LeaseLost`; three concurrent dispatchers raise each of 12 signals once |
| client simulator | against two API hosts on one database: a seeded run creates 12 orders over both replicas; its replay from the recorded result (other replica assignment) returns the same order for each request; a new run under the used run id exits 3 and creates nothing (12 orders, keys, start requests); responses lost after commit: each retry is a replay, one order per key; requests left unresolved are reported (exit 4) and a replay resolves them; 6 concurrent copies per key on both replicas: one order, five replays, the other body `422`; 5 distinct bodies raced per key: one accepted, the rest `422` in the race and on repeat, the order holds the winner's customer and lines; approve decides the pending review once, a rerun (by order or by review) is a replay, a later reject is refused `409 review-already-decided` (one decision); inspect shows order and reviews and skips diagnostics without the admin key; results never contain a key |
| fence | the claim counts every claim of a non-terminal order and audits it; terminal orders (all four statuses) are rejected, counted, and audited; 20 concurrent claims all count; an unknown order fails without writing |
| inventory | against a migrated `InventoryDb` (the inventory's own operations, no actors): 60 concurrent one-unit reservations on a row of 20 succeed exactly 20 times (`Reserved` 20, never above `OnHand`); 20 racing repeats of one operation key make one hold and one stored result, the rest replays; `InsufficientStock` is stored and replayed; release racing the sweeper and consumption racing the sweeper close each hold once; consumption is all of the order's holds or none (one expired or missing hold consumes nothing); a closed order refuses late reservations; the outbox stays gapless per order with one creation and one closing event per hold; a second sweeper takes over only after the lease; whole-line allocation by warehouse priority; the schema's check constraints and unique indexes refuse invalid rows |
| payment provider | the provider simulator in-process against its own database: a repeat of a key returns the stored result, another payload `409`; 20 concurrent requests of one key commit once; authorize/void, capture/void, capture/refund races (30 rounds each) never leave both effects; a late original after its compensation is `Prohibited`; a capture after a void is refused; expiry, decline, and every fault mode |
| payment service | the executor against the real provider: a lost answer is adopted by a status query; an unreachable provider is `Unavailable` (nothing sent); an undeterminable outcome is `Ambiguous` and later reconciled |
| hold extension | extend all holds or none under the order's lock, capped by the authorization, never shortening a hold; `HoldExpired`, `Incomplete`, `InsufficientValidity`; one stored result and `HoldExtended` events; a race with the sweeper |
| lifecycle | forward-only status chain; the point of no return refuses a dispatch after a cancellation and a cancellation after the dispatch (a delayed `BeginDispatch` racing a cancellation); cases open once per kind; finalization on `Delivered` writes the completion event once |
| carrier and Shipping | signed ingress: one inbox row and forward command per event, duplicates counted, another payload `409` and audited; Shipping's current-state rule (versions forward only, first terminal stays) and every outcome, including a wrong-order callback at a version already taken; the forwarder's outcomes (signal only for `TerminalApplied` while awaiting delivery, a case for `TerminalConflict` and for an unknown shipment after its retries) |
| cancellation | `202`, `200` repeat, `409 dispatch-started`, `409 order-terminal`, `404`; a cancellation during a review wakes the workflow and ends the round `Cancelled` |
| interventions | one transaction resolves a case and takes one path: `Signal` while the workflow runs, `Repair` when it is missing, terminated, or failed, `RecordOnly` on a terminal order; replays and other resolution ids; an action not allowed for the kind; an unreachable workflow host leaves the case open |
| escalation | a round escalated before its deadline opens the next round and signals the waiter in one transaction; a repeat returns the same round; the old round's decision is superseded; the last round cannot be escalated |
| outbox relay | (inventory and shipping outboxes) an append rolled back leaves no row and the sequence gapless; a live lease keeps a second relay away, an expired one is taken over; marking is idempotent and a replay requested meanwhile is not swallowed; only the lease holder records a failure, with backoff, `Stalled` after the threshold and still retried, then published; three competing relays publish every row of 50 at least once and end with all `Published`; a crash after the acknowledgement leaves the row `Pending` and the next relay publishes it again |
| event consumer | a gap holds later events `Pending` (state at the last contiguous sequence, the gap view shows the missing sequence) and filling it drains them in one transaction; duplicates are counted; payload, sequence, and source conflicts are dead-lettered without changing state; every event delivered twice at once on separate connections: applied once, serialized by the stream's state row; a rebuild gives the live checksum |
| schema | migrations apply twice without change; the API refuses to start on an unmigrated database; the database rejects a second start request, a second order per key, duplicate instance ids or history sequences, out-of-range lines, unknown statuses, inconsistent start-request lifecycle or lease values, a second effect per operation key, a simulated step not named `stub-…`, a second result per check, unknown attempt results, a second review round with one number, an inconsistent review lifecycle, an unknown review purpose, a decision without review, and an unknown signal state |

## Scenarios

### S03 Contention

`bash scripts/scenarios/S03-contention.sh` on any stack with replicas: 500
one-unit orders for a fresh SKU with 60 units in `W1` and 40 in `W2`, 32 in
flight over both order API replicas. Evidence: `artifacts/scenarios/S03-<time>/`.
With `INVENTORY_COORDINATION=SqlOnly` the last check becomes `S03-sqlonly`.

| Check | PASS when |
|---|---|
| S03-orders | exactly 100 `Completed` and 400 `Rejected` (`InsufficientStock`), every order accepted once |
| S03-stock | both rows `0/0`; 60 holds consumed from `W1`, 40 from `W2` |
| S03-invariants | `Reserved` equals the held units per row; one creation and one closing event per hold; gapless outbox; one hold per completed order, none per rejected one |
| S03-actors | each warehouse's reservations were written by one replica (its stock actor's host), activations logged |
| S03-sqlonly | no stock actor activated, and each warehouse's reservations were written concurrently by several replicas: the guarantees held from SQL alone |

### S20 Hot SKU Isolation

`bash scripts/scenarios/S20-hot-sku-isolation.sh` on a drill stack with
replicas (it arms failpoints). A hot SKU with 16 units; P1 asks for 5 plus a
SKU nobody stocks; P2 asks for 1 plus a SKU whose stock actor fails every
attempt; 10 ordinary one-unit orders are sent while P2 retries.

| Check | PASS when |
|---|---|
| S20-normal | all 10 ordinary orders `Completed` while P2 still held its unit |
| S20-failures | P1 `Rejected` (`InsufficientStock`), P2 `Compensated` (`ReservationFailed`), each hot-SKU hold released once; 5 failures fired |
| S20-stock | hot SKU `6/0`: 10 consumed, 6 released; invariants clean |

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
| ACC-1 accept | `201 Submitted`; both lines, history row 1, key, start request with `fulfill-{orderId:N}` (the order may already be further on when the rows are read: no transient status or request state is required) |
| ACC-2 replay | same key and payload through the second replica: `200`, same order, acceptance row counts unchanged |
| ACC-3 conflict | same key, other payload: `422 idempotency-key-reused`, row counts unchanged |
| ACC-4 replicas | 20 concurrent submissions with one key, alternating `order-api` and `order-api-2`: one `201`, nineteen `200`, one order (`UNVERIFIED` without the second replica) |
| ACC-5 restart | after a cold restart of the order API the retry returns the same order |
| ACC-6 completed | started by the dispatcher; the order is `Completed` with history `Submitted`, `Validating`, `Reserving`, `PaymentAuthorized`, `Packing`, `Dispatched`, `AwaitingDelivery`, `Completed`; the start request `Closed` without error code (`Started` need not be observed); one simulated packing effect per line (two lines); workflow `Completed` with outcome `Completed`; its state in `DaprStateDb` belongs to `workflow-host` only, none to `order-api`'s own sidecar |

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
| S51-start | 20 orders, 20 start requests, all `Closed` by the completion without error code or lease; every `LeaseLost` attempt has another attempt that recorded the start or found the request closed |
| S51-instance | every instance `Completed` on `workflow-host` (outcome `Completed`, or `AlreadyTerminal` if a second schedule replaced the finished instance); claim rows equal claims plus fence rejections, at least one claim (more are allowed); no workflow state under `order-api` |
| S51-effects | every order `Completed` with one history row per status (`Submitted`, `Validating`, `Reserving`, `Completed`) and exactly one effect per operation key and one result per check |
| S51-accept | 5 keys × 4 concurrent identical submissions over both replicas: one order per key (5 × `201`, 15 × `200`) |

### S09 Cross-Replica Approval

`bash scripts/scenarios/S09-cross-replica-approval.sh` on a stack with
`--replicas`. Three orders that require a review are accepted, each on one
order API replica, and approved through the other. Evidence:
`artifacts/scenarios/S09-<time>/` (execution views, timings).

| Check | PASS when |
|---|---|
| S09-waiting | every order `AwaitingApproval`, one `Pending` round, workflow `Running`, resolved once (`Initial:Pending`) and not again: it waits |
| S09-decision | the approval through the other replica: `200`; review `Approved`, one decision with the operator, one pending signal |
| S09-resumed | the workflow woke by the signal and read SQL again (`Initial:Pending`, `Signal:Approved`); the signal `Forwarded` after a raise |
| S09-completed | history `Submitted, Validating, AwaitingApproval, Reserving, Completed`; one effect per key; request `Closed`; workflow `Completed` on `workflow-host` only |

### S52 Signaling Through the Management API

`bash scripts/scenarios/S52-signaling.sh` on a drill stack with `--replicas`
(review deadline 20 s). Evidence: `artifacts/drills/S52-<run>/`.

| Check | Condition | PASS when |
|---|---|---|
| S52-A | the run is held right after its review round committed; the approval is committed and delivered meanwhile | no wake row before release; the first resolution reads `Approved`; completed once without waiting |
| S52-B | the replica that raised the signal ends before marking it (`FailFast`) | a second attempt by another replica after the lease (two raise calls received by the workflow host); one resolution after the signal (`Signal:Approved`; a wake row repeats only when the crash also lost that resolution activity's answer and Dapr ran it again); completed once |
| S52-B2 | the review event is raised directly from both order API replicas before any decision | two wake-ups read `Pending` and change nothing; the approval afterwards completes the order once |
| S52-C | the approval is committed shortly before the deadline and its delivery held past it | the timer path reads `Approved` (decided in time) and completes; the late raise finds the instance terminal: `Discarded` (`WorkflowTerminal`) |
| S52-C2 | a decision exactly at the last round's deadline | either committed and the order completes, or `409 review-expired` and the order is `Rejected` (`ReviewExpired`); never both; no signal without a decision |
| S52-signals | | no signal of the run left `Pending` |

### S04, S24, S43 Replica Scenarios

On a stack with both order API replicas (`bash scripts/stack-up.sh
--replicas`). The client simulator drives them; each scenario proves from
the servers' own answers (`X-Served-By`), each replica's container log
(acceptance outcomes by order id), and OrderDb rows that both replicas took
part and that no request added a business effect. Evidence:
`artifacts/scenarios/S04|S24|S43-<time>/` (simulator results, server
outcomes, SQL rows, `results.jsonl` with `PASS`/`FAIL`/`UNVERIFIED`).

| Check | Proves |
|---|---|
| S04-client | 6 keys × 8 identical copies at once, alternating replicas: per key one `201`, seven replays, one order id; answers from both replicas |
| S04-servers | each order accepted (committed) by exactly one replica; the other replica answered copies of it as replays |
| S04-sql | per key one order, key row, `Closed` start request; one `Completed` transition; one effect per line plus completion; no fence rejection |
| S04-race | copies reached the insert in SQL and lost to the unique key (`UNVERIFIED` if all came after the first commit) |
| S24-lost | answers of `order-api` dropped after its commit (Toxiproxy, downstream): timeout, retry on `order-api-2` → `200` replayed with the same order; `order-api` logged the acceptance |
| S24-unsent | requests to `order-api` dropped before the server (upstream): timeout, retry on `order-api-2` → `201` created; `order-api` never logged them |
| S24-delayed | hedged retry: the original delayed 4 s towards `order-api`, the same request on `order-api-2` after 1 s creates the order; the late original is answered as a replay of it |
| S24-unresolved | both replicas' answers dropped: unresolved (exit 4) while SQL holds each order once; after recovery the replay returns exactly those orders, which complete |
| S24-sql | every order of the scenario consistent as in S04-sql |
| S43-existing | other bodies (customer, quantity, extra line, line order) for a committed key: `422` from both replicas, at once and one by one; the original body replayed; stored order, key hash, history unchanged |
| S43-race | per fresh key 5 distinct bodies at once across the replicas: one accepted, four `422`, again on the other replica; the accepted body replayed |
| S43-servers | refusals logged by both replicas for every order; one acceptance per order |
| S43-sql | each order holds the accepted body; consistent as in S04-sql |

S24 uses network faults on the client path; the failpoint drill R03 (lost
answer after the acceptance commit, inside the server) is separate.

### S23 Deterministic Client Replay

`bash scripts/scenarios/S23-deterministic-client-replay.sh` on a release or
drill stack (both order API replicas when `--replicas` runs). Evidence: every
simulator result in `artifacts/scenarios/S23-<time>/`.

| Check | Proves |
|---|---|
| S23-plan | two dry runs of one run id and seed: equal workload digest, keys, payload hashes; another seed: same keys, other payloads |
| S23-new | a new run of 12 orders: all created over the replicas and followed to `Completed`; accepted and completed reported separately |
| S23-replay | the replay from the recorded result (seeded replica assignment): same digest, every request `200` replayed with its original order id |
| S23-reused | a new run under the used run id: exit 3, nothing created |
| S23-effects | per request one order, key, and `Closed` start request; history `Submitted, Validating, Reserving, Completed`; one effect per line plus completion; no effect row added by the replay |
| S23-operator | review orders followed until they wait, approved through the operator API, the approval repeated (replayed, one decision each), replayed and `Completed`; inspect shows reviews and execution diagnostics; no key in any output file |

### S02 Parallel Independent Checks

`bash scripts/scenarios/S02-parallel-checks.sh` on any ready stack. 10 orders
are fulfilled; each check stores where and when it ran (compute replica
clock; replicas share the host clock) and takes a simulated 300 ms.
Evidence: `artifacts/scenarios/S02-<time>/`.

| Check | PASS when (from `OrderDb` rows) |
|---|---|
| S02-1 completed | every order `Completed`, with a price and a risk result |
| S02-2 overlap | for every order the two execution intervals overlap (`max(start) < min(end)`) and the check phase is shorter than the sum of both: they ran concurrently, not one after the other |

### S49 Execution Fence on a Completed Order

`bash scripts/scenarios/S49-execution-fence.sh` on a drill stack. A fresh
order is fulfilled; its business rows are fingerprinted (status, history,
effects with replay counts, check results). Evidence:
`artifacts/drills/S49-<run>/` (fingerprints, execution views, Dapr's answer).

| Check | PASS when |
|---|---|
| S49-setup | order `Completed`, request `Closed`, workflow `Completed` |
| S49-C-reset | the drill-only reset makes the request `Pending` while the order is `Completed` (observed before the dispatcher acts) |
| S49-C-guard | the dispatcher closes it again (`OrderTerminal`) without a start attempt; the instance is unchanged |
| S49-C-unchanged | fingerprint, claims, and attempts unchanged |
| S49-D-schedule | the drill-only control's direct schedule of the existing id was accepted by Dapr (else D is `UNVERIFIED`) |
| S49-D-fenced | the new run ended `AlreadyTerminal` at the claim: one more fence rejection and one audit row, no new `Proceed` claim |
| S49-D-unchanged | fingerprint unchanged |
| S49-D-metric | `fence_rejections_total` rose in Prometheus |
| S49-restore | the cleanup puts the start request back to its saved row |

### Fulfillment Recovery Check

`bash scripts/checks/fulfillment-recovery-check.sh` on a drill stack with
`--replicas`. Each check fulfils a fresh two-line order and injects one fault;
the order must end `Completed` once, with one effect per key. Evidence:
`artifacts/drills/REC-<run>/`.

| Check | Fault | PASS when |
|---|---|---|
| REC-1 crash after commit | the order API replica running the first simulated step ends right after the step committed (`FailFast`) | the step ran again, found its effect (replay counted, committed by the crashed replica), the order completed once |
| REC-2 workflow host down | every workflow-host replica is `SIGKILL`ed while a step is held (10 s), restarted after 5 s | the run resumed from its history and completed once |
| REC-3 order API replica | the replica holding a step (before its transaction) is `SIGKILL`ed | the step ran on the other replica; completed once |
| REC-4 hosts down in review | every workflow-host replica is `SIGKILL`ed while an order awaits approval; the approval is decided during the outage | after the restart the run reads the approval and completes once; the signal is delivered (or made moot by the timer) within 60 s of readiness; attempt log, sidecar errors, and timing recorded |
| REC-5 invocation after restart (diagnostic) | every workflow-host replica restarts | invocation from both order API sidecars works again within 60 s of readiness; time to the first success and the failure answers recorded |

### Payment, Shipment, Reviews, Cancellation (drill stack)

| Scenario | PASS when |
|---|---|
| S13 payment declined | the provider declines the authorization: `Rejected` (`PaymentDeclined`), hold released once, nothing captured or voided, no case |
| S29, S31 operator approval and rejection | through the client simulator: both pending rounds listed; the approved order completes through payment and delivery (9 history rows, one capture), the rejected one ends `Rejected` (`ReviewRejected`) without hold or payment; repeats are replays |
| S30 seeded scripted operators | the same seed and orders give the same plan and digest; every order ends as its choice implies (approve, reject, escalate then approve, ignore until both rounds expire); the same run again returns only replays |
| S39 cancellation windows | during a review, after the authorization, after the dispatch started, after completion, unknown order: `202`/`200`/`409`/`404` with the matching end state |
| S40, S41 expiry before capture and consumption | holds expired during an intervention pause before the capture (voided) and after it (refunded); an authorization expired before the capture: nothing captured |

### Events, Projection, Reconciler (drill stack)

| Scenario or drill | PASS when |
|---|---|
| S01 happy path with events | three orders (one approved by an operator) `Completed` with one `OrderCompleted` event each; per order and source the published event ids equal the history's, all `Applied`, current state at the source's last sequence; timeline stage `Completed`; rebuild checksum equal |
| S48 shuffled stream (and S43 for events) | through the drill-only publisher: 1, 3, 3 again, 5, a conflicting payload for 3, 4, then 2 after 5 s: 3, 4, 5 held as `Pending` with the gap at 2, then drained to 5; the duplicate counted; the conflict dead-lettered (`EventIdPayloadConflict`) without changing state; rebuild equal. The permutation part (720 arrival orders) is the unit suite `TimelineProjectionTests` |
| R14 broker outage (S11) | RabbitMQ stopped: orders accepted meanwhile complete; their outbox rows stay `Pending` with failed attempts; after the restart every row is `Published` and published = history per order and source; rebuild equal |
| R15 repeated publication (S12, S18) | one inventory event published three more times through `AfterBrokerAckBeforeOutboxMarkSent` (`FailFast`, relay replicas restarted) and a replay: in history once, duplicates counted (at least 3), checksum unchanged; a replay of all four sources leaves history and checksum unchanged |
| R16 poison events | malformed and unknown-schema messages dead-lettered at once, the poison sentinel redelivered a bounded number of times and then dead-lettered (`DeliveryFailed`); ordinary orders meanwhile complete with their events applied; no payload marker in the logs or the recorder |
| R32 gap repaired by replay (S48, S18) | `ConsumerAckWithoutApply` (`Skip`) loses inventory sequence 2: 3 and 4 held `Pending`, state at 1, timeline 2 pending; the reconciler's replay fills the gap and drains 3 and 4; 1..4 applied once; converged; rebuild equal; no `ProjectionGap` case |
| R23 redelivered signal (signal part; S34, S10) | after the approval's signal was delivered and the order moved on, the row is reset to `Pending`: the signal is raised again, and there is still one decision, one `AwaitingApproval` → `Reserving`, one completion |
| R12 receiver or Shipping unavailable (S28) | A: both order API replicas stopped while scripted callbacks fall due: failing carrier attempts, callbacks kept; after the restart all three `Sent`, none dead-lettered, inbox and Shipping hold each event once, the order completes. B: both shipping replicas stopped: the ingress answers, forward rows wait `Pending` with attempts, then are processed once and the order completes |
| R29 lost callbacks (S42) | carrier mode `Lost`: every callback dead-lettered, the poll at the delivery deadline completes the order (terminal event from source `poll`); mode `LostNoPoll`: a `DeliveryOverdue` case, resolved, the order completes |
| R30 faulted, missing, mismatched workflows (S44) | A workflow `Failed` after reservation, B instance purged, C run completed without finalizing: one reconciler case each (`WorkflowFaulted`, `WorkflowMissing`, `ExecutionMismatch`); repairs compensate A and B (`Compensated`/`WorkflowFailed`, void with the repair key) and finalize C after Shipping shows `Delivered` |

The remaining M3 scenarios run inside their drills: S10 (carrier part), S27,
S43 (carrier part) in R13; S14 in R10; S25, S26 in R11; S32 in R19/R25; S33
in R22; S34 in R23; S35 in R20/R21; S36 in R24/R25; S37 in R26; S38 in R09;
S47 in R31; S50 in R33.

## Drills

Drills run only against a drill stack (`bash scripts/stack-up.sh --drill`);
see [resilience-drills.md](resilience-drills.md).

| Drill | Question | PASS when |
|---|---|---|
| R00 failpoint self-test | when the inventory process dies right after its effect commits and before Dapr gets the activity result, does the activity run again, and does the effect stay single? | the failpoint fired (persisted `FiredAt`) and the process exited; after restart, 2 committed attempts and 1 effect; probe `Completed`; execution claimed once |
| R01 waiting host loss (S06) | does a waiting workflow survive the loss of the host running it, and does an approval through the other replica reach it? | see [resilience-drills.md](resilience-drills.md#run) |
| R02 start exactly-once in effect (A-D) | crash after acceptance, crash after claiming the start, reset start request, direct schedule of a completed instance | one order, one `Closed` start request, one effect set in every variant |
| R03 lost answer after commit (S24) | does a lost answer after the acceptance commit duplicate the order? | the retry on the other replica returns the committed order; one of everything |
| R06 compute pool outage (S08) | does work stay pending while every compute replica is down, and complete once afterwards? | no checks or completions during the outage; all complete once afterwards |
| R04 crash after the inventory commit (S05) | is a committed reservation applied once when Dapr never learns that the activity finished? | the retry replays the stored result; one hold and stock moved once |
| R05 stock actor relocation (S07) | does a hot SKU stay correct when its stock actor's host dies under contention? | the actor moves to another replica; successes equal the stock |
| R06 inventory outage (S08) | do holds expire correctly while the whole inventory is down? | nothing changes during the outage; expired holds compensate their orders, later orders complete |
| R08 partial reservation (S45, S15) | are partial reservations compensated, and do holds expire without the Scheduler? | released once, `Rejected` or `Compensated`; the sweeper expires an orphan hold with Placement and Scheduler stopped |
| R09, R10, R11, R13, R19-R27, R31, R33 | payment, compensation, carrier, review, cancellation, and intervention failures | see [resilience-drills.md](resilience-drills.md#run) |

## Build Gates

`bash ci/dotnet-gates.sh` (restore, build with warnings as errors, unit
tests, format check, drill-flavor build and unit tests),
`bash ci/integration-tests.sh` (SQL Server integration tests), and
`bash ci/compose-config.sh` (Compose file valid for every profile). All run
in CI: on GitHub Actions the integration tests use the runner's Docker; on
Woodpecker they use a SQL Server service container.
