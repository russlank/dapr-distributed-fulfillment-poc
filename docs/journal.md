# Journal

What broke, what surprised us, and what we changed. Newest first. Each entry
keeps the expectation that turned out wrong.

## 2026-11-19: Reviews, decisions, signals, and the review wait

### The SQL Server clock steps back regularly, not only under load

- **Expected:** a row whose due time is the server's own `SYSUTCDATETIME()`
  at insert is due for a dispatcher that polls right afterwards.
- **Observed:** 2 of 15 full integration runs failed in the signal-dispatcher
  tests: signals stayed `Pending` without any attempt. With the dispatcher's
  log in the test output and a dump of the rows, the unclaimed rows had been
  created at 11.883 to 12.287 s, while the server's clock, read afterwards,
  said 11.827 s. A probe in the Docker Desktop VM (WSL2) measured the wall
  clock running fast and being stepped back by about 0.6 s every 32 s (five
  steps in 150 s), independent of load. The earlier lease failure (about
  1.8 s back under load) was the same mechanism.
- **Consequence for the design:** none for correctness. Due times,
  deadlines, and leases all use the database clock; a backward step only
  delays a delivery, an expiry, or a takeover by its size.
- **Fix (tests):** tests no longer rely on a timestamp the server just wrote,
  or on an exact boundary, being in the past: freshly written start requests
  and signals are made due explicitly, and moved times use a 30 s margin.
  The "decision exactly at the deadline" test became "after the deadline";
  the boundary itself is the `<` in the conditional update. Afterwards 20
  full runs had no such failure (one run failed entirely at fixture start
  because the resource reaper container vanished, the other open
  intermittent failure).

### Awaiting CancelAsync in workflow code broke the review wait

- **Expected:** a review signal raised to a waiting run wakes it; otherwise
  its timer does. (Unit tests on a fake context passed.)
- **Observed (first live S09):** the signals were raised and accepted by the
  workflow host (`Forwarded`), but no run woke. Raising the event again (in
  lower and mixed case) changed nothing. At the deadline the runs failed with
  `InvalidOperationException: An attempt was made to transition a task to a
  final state when it had already completed`. The decoded history showed
  `TimerCreated`, two `EventRaised`, `TimerFired`, and the failure, with no
  activity scheduled after either event.
- **Cause:** after `Task.WhenAny(timer, signal)` the code awaited
  `CancellationTokenSource.CancelAsync()`. `CancelAsync` runs the
  cancellation callbacks on the thread pool, and awaiting it resumes the
  workflow code off the orchestration thread, after the runtime already
  collected the episode's actions: the next activity was never scheduled. On
  the next episode the asynchronous cancellation had already marked the
  timer's task cancelled while it stayed registered, and `TimerFired`
  completed it a second time. (The SDK source confirmed that event names
  match case-insensitively, events are buffered, and its own cancellation
  callbacks use `TrySetCanceled`; the M0 spike used the synchronous
  `Cancel()` and passed.)
- **Fix:** synchronous `Cancel()`, with the rule that workflow code awaits
  only workflow tasks. The next S09 run: `PASS` (woken by the signal 0.1 to
  0.9 s after the decision).

### A decision without a comment was refused

- **Expected:** `{"decisionId", "operatorId", "outcome"}` is a valid decision;
  the comment is optional.
- **Observed (live S09):** `400 invalid-decision` ("decisionId: a non-empty
  GUID is required") for every decision. The integration tests passed because
  they always sent `"comment": null`.
- **Cause:** strict JSON reading treats every constructor parameter without a
  default as required; the comment had no default, the deserializer threw, and
  the error was reported as a missing decision id.
- **Fix:** the comment has a default; malformed bodies get their own message;
  an integration test posts a body without a comment.

### Prometheus queries hung during S49

- **Observed:** S49's wait of 150 s for `fence_rejections_total` took 10
  minutes and failed; right afterwards the metric was present (value 1) and
  the query answered in 60 ms. The rerun passed (22 s).
- **Cause:** not determined; the queries had no timeout, so a hung connection
  to the published Prometheus port stalled each poll.
- **Fix:** each query is bounded to 10 s.

### Workflow-host recovery and signal delivery (follow-up of the earlier view delay)

- **Measured:** after every workflow-host replica was killed and started
  again, invocation through the order API sidecars answered `500
  ERR_DIRECT_INVOKE` until shortly after the hosts reported ready: a signal
  attempt that started 2.2 s after readiness still failed (its four invoker
  calls spanned about 1.5 s), while direct probes 1 to 3 s after readiness
  succeeded. Delivery came 12.6 to 15.7 s after readiness, because the
  dispatcher's exponential backoff had grown during the outage (4 failed
  attempts, then 8 to 11 s until the next one). The earlier 15 s gap of the
  execution view was not reproduced.
- **Run of record:** the attempts during the outage recorded the sidecar's
  answer (`ERR_DIRECT_INVOKE`, "connection reset by peer" and "dial tcp" to
  the dead containers' addresses); the first attempt after readiness
  succeeded, 2.5 s after the hosts were ready, and woke the run by its signal.
- **Consequence:** correctness does not depend on it (the decision is in
  SQL; in two earlier runs the workflow's own timer applied the approval first
  and the signal ended `Discarded`). Expectation recorded: delivery within
  60 s of readiness, bounded in practice by the dispatcher's backoff (at most
  30 s between attempts); the root cause of the short post-readiness window
  is not established.

### The integration-test fixture failed because the container reaper vanished

- **Observed (first integration run of the run of record):** all 123 tests
  failed in 0 ms: the SQL Server fixture threw `Could not find resource
  'DockerContainer'`. The stack trace shows the container that disappeared:
  Testcontainers' resource reaper (`ResourceReaper.GetAndStartNewAsync`,
  waiting for its log message), not SQL Server. The next run passed.
- **Cause:** not determined (the reaper container is started with automatic
  removal; it ended before its readiness message was read). This is the
  first failure of this kind with a stack trace; the earlier "container
  disappeared at fixture start" failures had none.

## 2026-11-18: Fulfillment steps, terminal transaction, execution fence on completed orders

### A start acknowledged after the order completed looked like a lost lease

- **Expected (before):** a token-fenced update of a start request that
  changes nothing means another dispatcher took the lease over.
- **Wrong because:** with the fulfillment steps in place, a fast workflow
  can complete its order, and the terminal transaction close the start
  request (clearing the lease token), before the start answer reaches the
  dispatcher. Its update then changes nothing although no other dispatcher
  was involved.
- **Fix:** when a fenced update changes nothing, the dispatcher reads the
  request in the same batch: a later claim (the claim counter moved past its
  own) is `LeaseLost`; otherwise a `Closed` request is `AlreadyClosed`;
  anything else is `Unexpected` (logged as an error). The request is never
  reopened. An integration test forces the order of events (the fake
  workflow host runs the real steps to completion before it answers); no
  timing is involved.

### The drill reset of a start request failed on a filtered index

- **Expected:** the drill-only SQL reset runs through `sqlcmd` like the
  other drill statements.
- **Observed:** `UPDATE failed because the following SET options have
  incorrect settings: 'QUOTED_IDENTIFIER'` (first S49 run, `FAIL` before any
  change: the transaction rolled back).
- **Cause:** `sqlcmd` runs with `QUOTED_IDENTIFIER OFF` unless `-I` is given;
  updating a table with a filtered index (the dispatcher's due-work index on
  `WorkflowStartRequest`) requires it `ON`. Earlier drill statements only
  touched `drill.Failpoint`, which has no such index.
- **Fix:** drill SQL scripts run with `-I`. The next S49 run: `PASS`.

### Scheduling a completed fulfillment instance again replaces it

- **Observed (S49 variant D):** the workflow host's drill-only control
  scheduled `fulfill-{orderId:N}` of a completed order directly through the
  Dapr workflow API: Dapr accepted it (`scheduled: true`, previous status
  `Completed`) and started a new run with a new creation time, as it did for
  the runtime probe. The new run's claim found the order `Completed` and
  ended `AlreadyTerminal`: one more fence rejection and audit row, no other
  activity, effects and history unchanged.
- **Consequence:** none; the design does not depend on Dapr refusing the
  schedule. It confirms that the execution claim, not the runtime, keeps a
  completed order from running again.

### The execution view lost the workflow for a while after the workflow hosts restarted

- **Expected:** once a run completed after every workflow-host replica was
  killed and started again, the order API's execution view shows it.
- **Observed (first fulfillment recovery check):** the order was already
  `Completed` with correct rows, but the view's `workflow` was empty about
  15 s after both hosts were ready again; a minute later it showed the
  completed instance. The run itself had resumed and completed meanwhile.
- **Cause:** not determined. The view returns no workflow when the workflow
  host's management API cannot be reached through service invocation (it
  catches that case), so the call failed for a while after the restart.
  Possibly stale name resolution in the calling sidecar; not verified.
- **Consequence:** the business state does not depend on it. Start
  requests issued in such a window stay `Pending` and are retried. The
  recovery check now waits up to 60 s for the view before judging.

## 2026-11-18: Workflow start dispatcher and execution claim

### Every replica re-claimed its own expired leases

- **Expected:** when a start attempt outlives its lease, either order API
  replica claims the request again.
- **Observed:** first S51 run: 15 orders had overlapping attempts and all 20
  stale attempts ended `LeaseLost`, but every re-claim was made by the replica
  that held the lease (S51-race `UNVERIFIED`).
- **Cause:** each dispatcher polled at a fixed interval in phase with its own
  claims, so it woke right after its own lease expired, ahead of the other
  replica. A stuck replica would keep its own work.
- **Fix:** jittered poll interval (0.5-1.5×). The next two S51 runs: 10 and
  6 orders with overlapping attempts from both replicas; both `PASS`.

### A slow start call stalled the dispatcher

- **Expected:** a dispatcher keeps starting other orders while one call is
  slow.
- **Observed (review, before the first S51 run):** each poll awaited all of
  its starts, so one hung call stopped the replica's dispatcher, and it could
  never take over an expired lease while waiting.
- **Fix:** starts run concurrently up to `MaxInFlight` while the loop keeps
  polling; an integration test shows a second order started while the first
  call is still in flight.

### R00 measured something else on a stack with inventory replicas

- **Expected:** R00 behaves the same on any drill stack.
- **Observed:** on a drill stack with the replicas profile R00 failed:
  2 committed attempts before the killed inventory replica was restarted.
  The effect stayed single and the probe completed.
- **Cause:** another inventory replica ran the activity again at once. R00
  measures re-execution after the restart of the only replica.
- **Fix:** R00 refuses stacks with additional inventory replicas; it passes
  on the core drill stack.

### The SQL Server clock stepped back under load

- **Expected:** a lease of 800 ms set by one statement has expired 1.2 s
  later by the same server's clock.
- **Observed:** intermittently (about 1 in 7 full integration runs), the
  crash-recovery test found the lease still valid: the row expired 1.4 s
  *after* the server's current time, so `SYSUTCDATETIME()` had gone back by
  about 1.8 s between the two statements (SQL Server on Linux in Docker
  Desktop's WSL2 VM, under the parallel test load).
- **Consequence for the design:** none for correctness. Leases use the
  database clock only; a backward step only delays a takeover, and an early
  expiry (forward step) is covered by lease tokens, the state-first start,
  and the execution claim.
- **Fix (tests):** lease tests wait until the database reports the lease
  expired instead of sleeping on the client; the slow-call test holds the
  call on an explicit gate; the backoff in tests is 10 s. Afterwards: 20
  isolated and 21 full-suite runs without this failure.

### Open: two intermittent test failures without a known cause

- `RunOnce_HostRefuses_ReleasesWithLongBackoff` failed once in about 40
  full-suite runs (`LastErrorCode` was not `StartRejected`; the run took 4 s
  instead of milliseconds). Not reproduced in 21 further runs. The test now
  reports the start-request row and the dispatcher's log on failure. A
  transient SQL error in the release step would produce exactly this (the
  dispatcher then leaves the lease to expire, which is the intended
  behavior); unconfirmed.
- Twice, every test failed at fixture start: the Testcontainers SQL Server
  container disappeared during start-up (`No such container`). Both times the
  run started right after a previous test process had ended; memory was not
  short. Not reproduced in 21 further runs.

### Smaller problems

- The first concurrent-dispatcher test passed its correctness assertions but
  failed its own guard: one dispatcher claimed all 30 orders in one poll, so
  no concurrency happened. Smaller batches make the dispatchers interleave.
- Scheduling can fail when another start created the instance between the
  management API's check and its schedule call; the API now reports the
  existing instance instead of a 500.

## 2026-11-18: Durable order acceptance

### Woodpecker failed a green run because of the SQL Server service's exit

- **Expected:** a service's exit at the end of the workflow does not affect
  the result.
- **Observed:** on the first push with integration tests, every step passed
  (unit gates; integration tests 46/46), yet the Woodpecker run was red: the
  `sqlserver` service showed exit code 137. Its log ended mid-run without a
  shutdown message, after the last test database, with no errors and ample
  free memory. GitHub Actions was green.
- **Cause:** Woodpecker stops services with SIGTERM and then SIGKILL; SQL
  Server did not exit in that window, and the killed service counted as a
  failure.
- **Fix:** `failure: ignore` on the service (Woodpecker's documented option for
  services that do not shut down cleanly). A server that does not work still
  fails the integration-tests step.

### Container builds did not see the analyzer exclusion for migrations

- **Expected:** marking `Persistence/Migrations` as generated code in the
  repository's `.editorconfig` applies to every build.
- **Observed:** local builds passed; the image build of the order API failed
  with `CA1861` in the generated migration (warnings are errors).
- **Cause:** the image build context is `src/`; the root `.editorconfig` is
  not part of it, so container builds ran the analyzers on generated code.
- **Fix:** the section lives in `src/.editorconfig`, inside the build context.

### Existing `.env` files lacked the new customer key

- **Expected:** adding a name to `.env.example` is enough.
- **Observed:** `init-env.sh` never touches an existing `.env`, so Compose
  would refuse to start (`CUSTOMER_API_KEY` missing) on every machine with an
  older `.env`.
- **Fix:** `init-env.sh` appends missing names with new random values and
  never changes existing ones; `stack-up.sh` runs it every time.

### Concurrent tests alone did not prove the unique-violation path

- **Expected:** 24 concurrent submissions with one key exercise the path
  where the key's insert fails with a unique violation.
- **Observed:** the tests pass whether or not that path runs: a request that
  arrives after the winner's commit takes the lookup path instead. Passing
  proves the outcome, not the mechanism.
- **Fix:** an added test holds the key in an uncommitted transaction, proves
  that the submission is blocked on it (lock wait in
  `sys.dm_exec_requests`), then commits, so the violation path runs every
  time (replay and conflict variants).

### Smaller problems

- The first constraint tests failed because their raw inserts omitted the
  start request's counter columns (`NOT NULL`, no database default): the
  tests were wrong, not the schema; they now supply the counters.
- `dotnet ef migrations remove` connects to the database to check whether the
  migration was applied; with no database, `--force` removes it.
- With `--namespace`, `dotnet ef migrations add` wrote the model snapshot to a
  folder derived from the namespace (`Fulfillment/OrderApi/...`) instead of
  next to the migration; it compiled, so nothing failed. Moved; a unit test now
  fails whenever the model has changes that no migration covers.

## 2026-11-18: Foundation (solution skeleton, Compose stack, runtime smoke)

### A workflow instance belongs to the app that scheduled it

- **Expected:** the order API, which owns the SQL start records, would
  schedule workflows that run on the workflow host.
- **Observed:** the runtime probe, scheduled through the order API's
  sidecar, failed within two seconds: `WorkflowNotFound: Workflow
  'RuntimeProbeWorkflow' is not registered in this app`. The order API's
  sidecar logged that it had started the instance itself.
- **Cause:** a Dapr workflow instance is owned by the app whose sidecar
  scheduled it; the .NET workflow client cannot target another app. The
  compatibility spike did not show this because its control API ran in the
  same app as its workflows.
- **Change:** the workflow host exposes an internal workflow-management API
  (start with a state check first, state, events), reached through service
  invocation with caller-side retries; the order API keeps the SQL records
  and calls that API ([architecture.md](architecture.md)).

### The local.env secret store refuses DAPR_-prefixed names

- **Expected:** a component secret named `DAPR_STATE_CONN` would resolve from
  the sidecar's environment, as `SPIKE_STATE_CONN` did in the spike.
- **Observed:** every sidecar exited: `Access to env var DAPR_STATE_CONN is
  forbidden`, then `missing connection string`.
- **Change:** `STATE_STORE_CONN`.

### Every host crashed when the OTLP endpoint was set

- **Observed:** all apps exited at start in Compose: "Signal-specific
  AddOtlpExporter methods and the cross-cutting UseOtlpExporter method being
  invoked on the same IServiceCollection is not supported." Unit tests passed
  because none built a host with an OTLP endpoint.
- **Change:** only `UseOtlpExporter` (it covers logs too); a unit test now
  builds a host with and without the endpoint and fails on the old code.

### SqlClient does not work in invariant globalization mode

- **Observed:** apps with a database exited with `Globalization Invariant Mode
  is not supported`.
- **Change:** `InvariantGlobalization` removed from the shared build
  properties.

### Restarted apps left their sidecars in a dead network namespace

- **Expected:** restarting an app container (or letting Compose recreate it)
  and then its sidecar would give a working pair.
- **Observed:** twice the sidecar showed as running while its DNS lookups
  failed and the app got "connection refused" from `127.0.0.1:3500`: once
  after `docker compose restart <app>` with the sidecar recreated in a
  separate call that failed silently, once when Compose recreated an app and
  its sidecar in the same `up`.
- **Cause:** the sidecar joins the namespace of one specific app container;
  when that container is replaced, the sidecar must be recreated after the
  new app container runs.
- **Change:** `stack-up.sh` starts everything except sidecars, waits for the
  apps, then force-recreates every sidecar; `start_pair` and `restart_pair`
  recreate the sidecar after the app runs and wait for readiness, and no
  longer hide errors.

### Sidecars starting together race on the state-store schema

- **Expected:** every sidecar initializes the `state.sqlserver` v2 component
  independently (the first start of the release stack worked).
- **Observed:** on the first start of a fresh drill stack, one sidecar exited:
  `failed to create stored procedures: mssql: There is already an object
  named 'sp_Upsert_v5_state' in the database`. Its app never became ready, and
  the drill's runtime probe hung at the step that needed it.
- **Cause:** the component creates its tables and stored procedures at start;
  concurrent first starts on an empty `DaprStateDb` collide.
- **Change:** `stack-up.sh` starts one sidecar alone, waits until it is
  ready (the schema then exists), and only then starts the others. The drill
  preflight now also requires every role to be ready, not only to report a
  drill identity; it had let the drill run against a broken role.

### The first recovery check missed its failure window

- **Expected:** killing the workflow host right after the first effect
  appeared would land inside the probe's 20 s durable timer.
- **Observed:** the check failed, and the evidence showed why: each SQL poll
  through `docker exec` takes several seconds on Windows, so the kill came
  after the timer had fired and the second effect had been written. The check
  would have passed for the wrong reason had the rest worked.
- **Change:** the check records whether the second effect exists at kill time
  and fails as "window missed" if it does; the timer is 45 s.

### Smaller problems

- **Image tags:** `stack-up.sh` tagged images `local-release`, while other
  scripts ran Compose without that variable and looked for `local`. Build and
  run variables now come from one place (`scripts/lib.sh`), and the tag is
  derived from the flavor inside the Compose file.
- **A dirty working tree looked clean:** the commit was shortened to ten
  characters, which cut off a `-dirty` suffix. Identity now has a separate
  tree state (`clean` or `dirty`).
- **Scheduler volume permissions:** the Scheduler image runs as uid 65532 and
  a fresh named volume is root-owned; a one-shot job (using the SQL Server
  image, which has a shell) hands the directory to that uid, so the Scheduler
  no longer runs as root.

## 2026-11-18: Dapr 1.19 compatibility spike

Context: a small two-role spike (`spikes/dapr-compat`) to observe the
pinned Dapr runtime before building on it. Details and evidence per check:
[versions.md](versions.md).

### Lost Scheduler data loses pending timers

- **Expected:** unknown; possibly timers re-created from workflow history.
- **Observed:** after deleting the Scheduler volume during a 20 s timer, the
  workflow was still waiting 60 s later. An external event woke it up.
- **Cause:** durable timers live in the Scheduler's embedded etcd, not in
  the state store.
- **Change:** Scheduler data stays on a persistent volume and is reset
  together with the state store. Waiting workflows always also wait for a
  wake-up event and re-check their deadline, so a lost timer can be
  recovered by sending that event.

### Service invocation is not retried on application 5xx responses

- **Expected:** a resiliency retry policy (3 retries, matching 500-599) for
  the target app would retry a failing call.
- **Observed:** the target saw one attempt; the caller got the 500. The same
  result without `matching` and with a single status code, also after
  restarting the calling sidecar. Pub/sub retries from the same resiliency
  file work.
- **Cause:** not determined in the time box. Retries on transport failures
  were not tested.
- **Change:** callers retry service invocation themselves with backoff;
  receivers are idempotent. Transport-failure retries will be checked with
  the shipping integration.

### Raising an event to a missing instance fails, contrary to the SDK docs

- **Expected:** the SDK documentation says events for non-existent or
  completed workflows are silently discarded.
- **Observed:** a missing instance gives `no such instance exists`; a
  completed instance silently ignores the event.
- **Change:** signal dispatch checks the instance state first and treats
  "no such instance" as a known outcome.

### Scheduling the id of a completed workflow starts a new run

- **Expected:** "already exists", as for a running instance.
- **Observed:** for a completed instance the call succeeds and replaces it
  with a new run. Purge also makes an id reusable.
- **Change:** "one execution per order" is enforced by the application (one
  start request per order, no start for a terminal order, a first workflow
  step that exits on a terminal order, keyed effects), not by Dapr.

### Activity concurrency limits moved to the sidecar

- **Expected:** `WorkflowRuntimeOptions.MaxConcurrentActivities` in code.
- **Observed:** the property is obsolete and does nothing in SDK 1.19 (build
  error with warnings as errors). `spec.workflow.maxConcurrentActivityInvocations`
  in the sidecar `Configuration` enforced 2 per replica.
- **Change:** concurrency limits live in `dapr/config`.

### SQL Server state store v1 corrupts workflow history

- **Expected:** `state.sqlserver` with `actorStateStore: "true"` works for
  workflows, as the support matrix says.
- **Observed:** a workflow completed, but reading its state failed with
  `proto: cannot parse invalid wire-format data`. The stored history held
  U+FFFD replacement characters.
- **Cause:** component version v1 stores values as `NVARCHAR(MAX)` text and
  converts the value bytes to a string; binary protobuf history does not
  survive. The running workflow worked only from the sidecar's cache, so the
  problem appears only on read-back, for example after a restart.
- **Fix:** `version: v2` of the component, which stores binary values in a
  `varbinary` column (`isBinary = 1`). Workflows and actors then pass.
- **Lesson:** a passing "workflow completes" smoke test proves nothing about
  persistence; read state back through another sidecar.

### The recovery check failed in the first clean run: two bugs in the test

- **Expected:** the check that kills the orchestrator replica running a
  workflow would pass, as it had in the development run.
- **Observed:** in the first full run from a fresh stack it failed. The
  evidence showed the workflow had recovered (timer fired on the other
  replica, the first activity ran once, the second ran), but the script
  recorded no final state.
- **Cause 1:** the script polled the workflow through `orch-1`'s control
  API, and this time `orch-1` was the replica it had killed; in the
  development run `orch-2` happened to be killed.
- **Cause 2 (found while rerunning):** reruns reused the same instance ids,
  because the run tag was taken from the last characters of the run id.
  Scheduling a completed id replaces the instance (the behavior recorded
  above), so activity counts added up across reruns and looked like
  repeated execution.
- **Fix:** poll through the surviving replica; derive the run tag from a
  hash of the whole run id. Four reruns then passed, two with each replica
  killed. The full suite was then rerun from a fresh stack.
- **Lesson:** a failing drill is first a question about the drill; the
  evidence rows, not the verdict line, show what happened.

### Smaller problems on the way

- **NuGet source mapping:** the machine's NuGet configuration has a second
  (private) feed; with central package management and warnings as errors,
  restore failed (NU1507). Fixed with a repository `nuget.config` that clears
  sources and uses nuget.org only.
- **Host `obj/` in the image build:** `.dockerignore` listed `bin/` and
  `obj/`, which match only top-level folders; the Windows `obj/` was copied
  into the Linux build and restore looked for a Windows fallback folder.
  Fixed with `**/bin/` and `**/obj/`.
- **Actor needs an actor interface:** registering an actor class without an
  `IActor` interface crashes the host at startup
  (`does not implement any actor interfaces`), even when callers use the
  untyped proxy.
- **Git Bash path conversion:** exporting `MSYS_NO_PATHCONV=1` for the whole
  script also stopped `/dev/null` from being translated for the Windows
  `curl.exe` (exit code 23), so the readiness check never passed. The
  variable is now set only for the `docker compose exec` call that needs it.
- **Scheduler volume permissions:** the Scheduler image is distroless and a
  fresh named volume is root-owned; the spike runs the Scheduler as root.
- **Resiliency reload:** changing the resiliency file is picked up only on
  `SIGHUP` or a sidecar restart.
