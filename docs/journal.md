# Journal

What broke, what surprised us, and what we changed. Newest first. Each entry
keeps the expectation that turned out wrong.

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
