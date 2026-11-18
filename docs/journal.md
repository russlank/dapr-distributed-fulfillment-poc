# Journal

What broke, what surprised us, and what we changed. Newest first. Each entry
keeps the expectation that turned out wrong.

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
