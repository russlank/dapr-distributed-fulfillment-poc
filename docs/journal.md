# Journal

What broke, what surprised us, and what we changed. Newest first. Each entry
keeps the expectation that turned out wrong.

## 2026-11-21: Event distribution, projection, reconciler

### Two streams differed from their rebuild once (open)

- **Expected:** with the rebuild now comparing each stream under its own lock, a
  rebuild after any amount of traffic reports no differing stream.
- **Observed (full run on `54807a6`, fresh volumes):** S01's rebuild, the first
  rebuild after the M1-M3 regression on that stack, reported 2 of 2,115 streams
  whose live state differed from the state rebuilt from their history. Everything
  else in S01 passed (every event in history once, applied, current state at the
  last sequence for its three orders). The report named only the counts, and the
  rebuild replaced the live state, so which streams and how they differed was lost.
- **Not reproduced:** the same M1-M3 regression followed by S01 on the drill stack
  (2,147 streams); 700 fresh streams with 2,800 events published shuffled, and
  then with each stream's four events at the same instant, to both consumer
  replicas (3,247 streams). Every rebuild was equal. Reading the code found no
  writer besides the consumer and the rebuild, and every read in the consumer
  happens after the stream's state row is locked.
- **Change:** a differing stream is now reported with its live and rebuilt state
  and its history (sequence, disposition, type, event id, duplicates; no
  payloads), and the scenarios keep the whole rebuild answer, so a recurrence
  explains itself.

### The M4 run of record: two missed windows and an unsound rebuild check

The full suite on `448911f` (fresh volumes) passed 72 of 75 steps. The three
others, with what they showed; the fixes below were prepared afterwards and have
not been run on a stack yet.

- **S01 rebuild:** every order completed and converged, but the rebuild
  compared one checksum over all 2,115 streams before and after. Events of
  other orders were still arriving in between (earlier drills' orders and
  replays), so the totals differed without saying whether any stream
  diverged. **Change:** the rebuild compares each stream's rebuilt state with
  its live state inside the stream's own transaction and reports the streams
  that differ; the overall checksums stay informational.
- **R24 split (window missed):** all ten approvals committed before the
  deadline, although half were timed up to 1.4 s after it. The script measured
  the SQL-to-local clock difference once and waited about 40 s; the SQL Server
  clock in the WSL2 VM steps (see the clock note in the integration tests), and the measured difference across runs
  ranged from -0.54 s to +1.46 s, more than the ±1.5 s spread. The safety checks
  (never both approved and expired; workflow equals SQL) passed. **Change:**
  each approval's wait is measured by SQL itself shortly before it is sent.
- **S52-C (UNVERIFIED, window missed):** the held signal (12 s) landed while
  the run was still finishing, so the "raise finds the instance terminal" case
  was not reached; the outcome was still correct (one decision, applied once).
  Since M3 the run after the timer includes payment, packing, and three carrier
  callbacks 2 s apart, which left about 7 s of margin; the added event work
  of M4 used it up. **Change:** the raise is held 20 s (within the dispatcher's
  30 s lease).

### A malformed event's payload reached the log

- **Expected (drill R16):** dead letters are recorded with ids and reason
  codes only; no payload appears in logs.
- **Observed:** the log line for a malformed message carried the JSON
  parser's error message, which quotes the start of the input
  (`'not json r16marker…' is an invalid JSON literal`). The recorder table was
  clean; the log was not.
- **Fix:** the consumer logs only the exception type for unreadable messages,
  and only the SQL error number when an event cannot be stored (some SQL
  messages, such as truncation errors, quote values). R16 checks the logs of
  both order API replicas for a unique marker carried in the data.

### Restarting the carrier dropped a callback receiver

- **Expected:** after R11 restarts the crashed carrier simulator, later drills
  on the replicas stack still have callbacks sent to both order API replicas.
- **Observed:** the script started the carrier again without the callback
  list that `stack-up.sh --replicas` sets, so Compose saw a changed
  configuration and recreated the carrier with the single default receiver.
  The drills still passed, with half the intended receivers.
- **Fix:** the shared script library derives the list from the running stack;
  `stack-up.sh` chooses it by profile unless it is set explicitly.

### Gap detection alone misses a lost last event

- **Expected (plan):** a lost event shows as a gap, because later events of
  the stream arrive and wait as pending.
- **Observed (design review):** when the lost event is the stream's last one
  (for example the delivery that completes an order), nothing arrives after
  it, no pending row exists, and the projection lags silently.
- **Change:** the reconciler also compares each source's highest published and
  settled sequence per recent order with current state and requests a replay
  for a lagging stream; the source answers through its internal heads
  endpoint.

### Marking a row published could swallow a replay

- **Expected:** marking `Published` after the broker's acknowledgement is
  idempotent and safe.
- **Observed (design review):** a replay requested between the publication and
  the mark would be overwritten by the mark, so the requested republication
  never happens.
- **Change:** every replay increments a generation counter on the row; the
  mark applies only to the generation that was published.

## 2026-11-20: Payment provider, payment service, hold extension

### A callback for the wrong order was taken for a duplicate

- **Expected (drill R13):** a carrier callback naming another order than the
  shipment's is recorded by Shipping as `CorrelationMismatch`.
- **Observed:** the callback (PickedUp v1, wrong order id) was accepted and
  forwarded, but Shipping answered `Duplicate` and recorded nothing. The
  version check ("this shipment already has an event at v1") ran before the
  correlation check.
- **Fix:** the correlation check comes first; a mismatch is always recorded,
  whatever its version. The integration test now also sends a wrong-order
  callback at a version already taken.

### The carrier's scripted repeats were not byte-identical

- **Expected:** three scripted PickedUp callbacks with the same event id are
  duplicates, answered `202` and counted by the ingress.
- **Observed:** the second and third got `409` (payload conflict): each
  scripted step had its own occurrence time, so the signed body differed.
- **Fix (simulator):** a scripted step that repeats an earlier one (same event
  id, status, version, and order) reuses its occurrence time; all steps of a
  script share one base time. A repeat with another status stays a payload
  conflict.

### The extension before the capture changed nothing in the drill stack

- **Expected:** every drill order shows a `HoldExtended` event before its
  capture.
- **Observed:** none did. The drill hold lifetime (60 s) outlasts the drill
  extension (30 s), and an extension never shortens a hold. The decision path
  (remaining validity checked, capture or compensation) still ran.
- **Change:** R04 expects no extension event on the default drill stack. S40
  and S41 recreate the inventory replicas with a 20 s hold lifetime for their
  run, as R06 already did, so the extension really lengthens the holds there.
  With the authorization already expired, S41 showed that no capture is sent
  when the extension (capped at the authorization's expiry) leaves too little
  validity. When a hold still has enough validity of its own, the provider's
  refusal of the capture is the backstop.

### A terminated workflow made its intervention case unresolvable

- **Expected (drill R26):** after an order's workflow is terminated, resolving
  its AmbiguousPayment case finds no workflow waiting and starts the repair
  workflow.
- **Observed:** the resolution answered `503 workflow-state-unknown` and the
  case stayed open. The workflow host's state endpoint answered 500 for the
  terminated instance: it read the output of every completed instance as a
  `JsonElement`, a terminated one has none, and serializing the undefined
  element threw.
- **Fix:** an instance without an output reports none. The case that had been
  refused was resolved afterwards with the same request and took the Repair
  path (void with the repair key, order Compensated).
- **What held:** the resolution never guessed. Without the workflow host's
  answer it changed nothing and asked for a retry.

### A carrier forwarding test failed once in the full suite

- **Expected:** the integration test that drains the carrier forward queue
  with its own forwarder (and a scripted Shipping) sees the `Delivered`
  callback applied and one carrier signal written.
- **Observed:** one failure in a full integration run (no signal row); the
  same test class passed six times in a row on its own. The test's in-process
  order API host also ran the real background forwarder (and the repair
  dispatcher) against the same database. Under load, that forwarder claimed
  the row first, held its lease while calling a sidecar that does not exist
  in tests, and the test's drain loop ran out of iterations before the
  lease expired.
- **Fix:** the test host disables the carrier forwarder and the repair
  dispatcher (`CarrierForwarder:Enabled`, the new `RepairDispatcher:Enabled`),
  as it already did for the start and signal dispatchers. The next full run
  passed (203/203).
- **Lesson:** every background worker a test host starts is a competitor for
  the rows the test drives; a new hosted service needs its switch in the test
  host on the day it is added.

### A serializable transaction leaked into the carrier's next statement

- **Expected:** the carrier simulator accepts a shipment in a serializable
  transaction, then checks its (drill) failpoint and answers.
- **Observed (first order on a drill stack with shipping):** every shipment
  request got a 500 after the carrier had committed the shipment and its
  callback schedule; the callbacks were then sent and applied, while Shipping
  still saw the dispatch as unanswered. The failpoint check reads with
  `READPAST`, which SQL Server refuses outside read committed and repeatable
  read, and the pooled connection still carried the serializable isolation
  level of the accept transaction.
- **Fix:** the accept transaction uses the default isolation; its
  `UPDLOCK, HOLDLOCK` read already serializes requests of one shipment id.
  No other code sets an isolation level above read committed.
- **What held:** Shipping classified a 500 after sending as "may have been
  accepted", sent the same shipment id again, and after its budget the
  workflow opened an `AmbiguousDispatch` case instead of guessing; no second
  shipment was created.

### A connect that timed out was taken for a request that may have committed

- **Expected:** a payment request that gets no answer within its timeout may
  have reached the provider, so the payment service queries the status before
  it sends again; a request that never connected cannot have committed, and
  after the budget the order is compensated (nothing to reconcile).
- **Observed (integration test, provider address with nothing listening):**
  the operation ended `Ambiguous` after one request instead of `Unavailable`
  after three. On Windows a refused connection to a closed local port takes
  about two seconds (the connect is retried), longer than the request timeout,
  so the request was recorded as `Timeout`, the payment service started status
  queries, and those timed out too. The same happens for any host that does
  not answer the connect at all.
- **Fix:** connecting has its own limit (half the request timeout). A failure
  while establishing the connection (refused, name not resolved, connect
  timeout) is recorded as `Refused`: no request bytes were sent. A request
  timeout or a connection broken after sending stays "may have committed".
  .NET reports the connect timeout as a cancellation with an inner
  `TimeoutException` while the request's own timeout token is still unset;
  that is how the two are told apart.
- **Lesson:** "no answer" is two different facts depending on whether the
  connection existed; only one of them can hide a committed payment.

### An in-process aborted response arrives as a timeout

- **Observed:** the provider's `DropResponseAfterCommit` (the server aborts
  the connection after committing) reached the in-process test client as a
  timeout, not as a broken connection.
- **Consequence:** none for correctness: both answers mean "may have
  committed" and lead to a status query, which found the committed
  authorization. The test accepts either answer.

## 2026-11-19: Inventory (holds, stock actors, sweeper)

### Range locks on missing keys deadlocked under contention

- **Expected:** reading an operation key's stored result, the order's
  closure, and the order's last outbox sequence with `UPDLOCK, HOLDLOCK`
  serializes repeats of one key and keeps the outbox sequence gapless,
  without affecting other orders.
- **Observed (integration test, 60 concurrent reservations of one stock
  row):** transactions deadlocked faster than three retries could absorb
  (24 s, then a deadlock error). On a missing key, `HOLDLOCK` locks the gap
  between existing keys, and different orders' missing keys share gaps: one
  transaction held a gap lock and waited for the hot stock row, another held
  the stock row and needed to insert into that gap.
- **Fix:** every operation of one order takes an exclusive, transaction-scoped
  application lock on the order first (`sp_getapplock`); under it, plain
  reads see the latest committed rows, so no range locks are needed. The
  sweeper finds a due hold without locking it, then takes the order's lock and
  expires the hold only if it is still held and due. Afterwards: the
  21 inventory tests passed in 8 of 8 repeated runs, including 60 concurrent
  reservations, 20 racing repeats of one key, a release or a consume racing
  the sweeper, and a close racing a late reservation.

### S52-B: the crash also hit the resolution activity

- **Expected:** after the dispatcher's replica dies right after raising the
  review signal, the review is resolved once: wake rows `Initial:Pending`,
  `Signal:Approved`.
- **Observed (run on the inventory commit, drill replicas):** three rows,
  `Signal:Approved` twice; decision and completion once; the workflow host
  logged one resolution. Timeline: raise 17.460, the resolution activity on
  the same replica committed its wake row 17.501, the failpoint ended that
  process 17.506, the sidecar reported the activity's reminder aborted 18.33,
  the activity ran again on the other replica 18.8. Both order API replicas
  host the workflow's activities; earlier runs passed because the resolution
  happened to run on the surviving replica.
- **Fix:** the check counts resolutions from the workflow's replay-safe log
  (exactly one after the signal) and accepts repeated `Signal:Approved` wake
  rows only together with that single resolution. A wake row records an
  activity execution, not a wake-up; nothing is decided from it.

### The laptop slept during a run

- **Observed:** R06 (compute) on the core drill stack restored the pool at
  15:18:31Z; the 240 s wait for completion ended after 43 minutes, and the
  queued check activities ran at 16:01:29Z. Windows had entered Modern
  Standby (15:13Z) and suspended the Docker VM from about 15:18:33Z until it
  left standby at 16:01:22Z.
- **Fix:** none in the code. Long runs hold a keep-awake request for their
  duration; a run interrupted by standby is repeated, not counted.

### A refused drill reported PASS

- **Expected:** a drill that refuses (exit 2), for example because the
  stack lacks a setting it needs, reports that it did not run.
- **Observed (first R06 inventory run):** the refusal came after the exit
  trap was installed; `drill_finish` saw no failed assertion and wrote
  `outcome: PASS`, and the script exited 0. The verdict line said `R06 PASS`
  although nothing had been tested.
- **Fix:** `drill_finish` reads the script's exit status on entry. A refusal
  is `REFUSED` (exit 2); any other non-zero exit, or a run that recorded no
  assertion, is `FAIL`; `result.json` records `scriptExit`. Checked with a
  refusing, an empty, and a passing synthetic drill. All 83 recorded drill
  results were scanned afterwards: only this run had `PASS` without an
  assertion, so no earlier evidence was affected.
- **Lesson:** "no failure seen" is not a pass; a verdict needs positive
  evidence that the checks ran.

### Two clocks in one comparison

- **Expected (R05):** after the stock actor's host is killed, every
  reservation of the hot SKU is written by another replica.
- **Observed:** reservations stamped by SQL (`ProcessedAt`) after the kill
  time taken from the host clock were still attributed to the killed
  replica (10 of about 100). SQL Server's clock in the Docker VM differs
  from the host's, and `docker compose kill` takes about a second to deliver
  `SIGKILL`, during which the old host goes on committing.
- **Fix:** the drill takes the kill instant from SQL itself and records the
  old host's last write and its container's end time next to the counts; the
  verdict rests on the new host's activation and its writes, not on a clean
  cut-off.

## 2026-11-19: M1 drills R01, R02 A-D, R03, R06

### An invocation routed to a killed workflow host hung for 15 s

- **Expected:** with one of two workflow-host replicas killed, service
  invocation from an order API replica reaches the surviving one.
- **Observed (first R01 run):** the execution view's call to the workflow
  host hung until the invoker's client timeout of 15 s and ended as an
  unhandled `500`, about 2 to 17 s after the kill, while the other replica
  was healthy. The invoker retried only `5xx` and transport errors; a
  timeout escaped it. (The signal raised in the same window was delivered:
  its first call failed fast, the second succeeded.)
- **Cause:** the sidecar still routed some calls to the dead replica's
  address, where the TCP connect hangs instead of being refused (not
  confirmed in Dapr's code; consistent with the short invocation window after a workflow-host restart, recorded earlier).
- **Fix:** each invoker attempt has its own timeout (5 s); a timed-out
  attempt counts as a transport failure and is retried with the same
  idempotency key, which every caller already relies on. The next R01 run:
  the view answered after 5 s (one timed-out attempt, then the other
  replica).

### The workflow waits at the compute call while the pool is down

- **Observed (R06):** with every compute worker stopped for 45 s, the four
  orders stayed `Validating` with no check results; their workflows stayed
  `Running`, custom status `validating` and last update at validation: the
  episode that schedules the two checks was not persisted while no
  compute-worker instance existed, and nothing failed. After the pool was
  back the orders completed within 9 s, each check run once (no replays).

### A crashed order API replica's sidecar did not stop the work

- **Observed (R02 A, B):** `FailFast` ends only the app container (exit code
  139 on Linux); its sidecar keeps running. Starts, activities, and
  invocations still reached the surviving replica; the orders completed
  within seconds, and restarting the crashed replica (with a fresh sidecar)
  changed nothing.

### Drill harness pitfalls

- `wait_until ... bash -c "...sql..."` always timed out: the child shell does
  not have the script's functions. In R01 the 30 s wasted wait outlasted the
  20 s review deadline, so the run reached round 2 before the kill. Waits use
  functions now.
- Windows Python writes CRLF; `mapfile` kept the CR in order ids, so R06's
  status queries went to wrong URLs and returned nothing (the check passed
  without its evidence). Id lists strip the CR now.
- The commit-before-loss proof in R03 does not compare SQL timestamps (the
  SQL clock steps back, see the clock entries): it uses the rows that exist although the
  replica never answered, and the replica's acceptance log line.

## 2026-11-19: Replica scenarios S04, S24, S43

### Nothing on the server said which replica answered, and replays left no trace

- **Expected:** the replica scenarios can prove from server-side evidence
  that both replicas took part.
- **Observed:** the client knew only which URL it used, and a replay (the
  most common answer to a duplicate) wrote nothing and logged nothing; only
  acceptances, refusals, and lost insert races were logged.
- **Fix:** every order API answer carries `X-Served-By` (the replica name,
  already public at `/system/component-identity`), recorded per attempt by
  the client simulator; the replay path logs the order id. The scenarios
  read each replica's container log by order id.

### Simultaneous duplicates all race into the insert

- **Observed (S04):** with 8 identical copies sent at once, all 7 losers per
  key ran the full insert (order, lines, key, start request) and lost to the
  key's primary key (42 of 42 copies), instead of finding the committed key
  on the fast path. Correctness holds (one order per key, the losers' rows
  rolled back, verified in SQL); the cost is one rolled-back insert per
  simultaneous duplicate.

### A delayed request is dropped when its client gives up

- **Expected:** a request delayed on its way to a replica (Toxiproxy latency)
  arrives after the client's retry on the other replica, as a late duplicate.
- **Observed:** when the client timed out and closed the connection, the
  proxy discarded the buffered request with it; the late arrival never
  happened (the check reported `UNVERIFIED`).
- **Fix (scenario):** the delayed case is a hedged retry: the original keeps
  waiting behind a 4 s delay while the same request goes to the other
  replica after 1 s and creates the order; the original then arrives and is
  answered as a replay of that order.

## 2026-11-19: Client simulator (seeded runs, replay, operator commands)

### A replay after the run's own lost answer is not a reused run id

- **Expected:** in a new run, an answer `200 replayed` means the run id was
  used before (a new run must create every order).
- **Observed (integration test with responses dropped after the commit):**
  every order of a fresh run was reported as "run id used before" (exit 3),
  although the run created all of them: the first attempt committed, its
  answer was lost, and the retry with the same key was answered as a replay.
- **Fix:** a replay that follows an ambiguous attempt of the same request
  counts as that request taking effect; only a replay without an earlier
  ambiguous attempt is a run identity mismatch.

### Repeating an approval by order found "no pending review"

- **Expected:** repeating `approve --order` is a replay, like any retry with
  the same decision id.
- **Observed (first live S23):** the repeat looked only for a `Pending`
  round; after the first approval there was none, and the command failed
  with `no-pending-review` instead of reaching the server.
- **Fix:** when the latest round is decided, the command sends its
  deterministic decision to it; the server answers with a replay (same
  decision) or `409 review-already-decided` (another one). An expired latest
  round is still waited on, because the next round may be about to open.

### Smaller findings before the first run

- Generated run ids contained an upper-case `T` (time format) that the
  simulator's own run-id rule refuses; found by a unit test.
- A URL with user information was echoed in the usage error; the value is no
  longer printed (a unit test checks that no credential appears).

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
