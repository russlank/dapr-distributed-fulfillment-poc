# Client Simulator

`src/Fulfillment.ClientSimulator` is the external customer and operator
client: a console tool that talks to the order API over plain HTTP. It has no
Dapr sidecar or package (a unit test checks the reference closure) and never
touches a database.

```bash
bash scripts/stack-up.sh --replicas
set -a; . ./.env; set +a                      # keys come from the environment only
dotnet build src/Fulfillment.ClientSimulator -c Release -o artifacts/tools/client-simulator
sim() { dotnet artifacts/tools/client-simulator/Fulfillment.ClientSimulator.dll "$@"; }
sim --help
```

Scripts use the same build through `client_sim` in `scripts/lib.sh`.

## Commands

| Command | Does | Expectation (exit 0 when it holds) |
|---|---|---|
| `single` | one order (`--line SKU:QTY`, `--customer`, `--key`, or generated from `--run-id`/`--seed`; `--review` for an order from 1,000.00); `--follow` polls it until terminal | `--expect new`: created; `--expect replay`: an earlier order returned; `any` (default) |
| `duplicates` | each key sent `--copies` times at once across the replicas, then once with the same key and another body; `--follow` follows each order | one order per key (at most one `201`, every other copy `200` with the same order id); the other body `422 idempotency-key-reused` |
| `conflicts` | per key, distinct bodies (other customer, quantity, extra line, line order, ...) sent at once across the replicas, then each once more on another replica | exactly one body accepted per key, every other `422 idempotency-key-reused` twice; the accepted body replayed with the same order |
| `contention` | `--orders` one-line orders for one SKU (`--sku`, `--quantity`, default 1) with `--concurrency` in flight and an optional `--rate`; `--follow` follows each to its end (S03, R05, S20) | every order created (accepted once); the final statuses are reported, the scenario judges them |
| `load` | a seeded workload (`--orders`, `--clients`, `--max-lines`, `--review-rate`) with `--concurrency` requests in flight and an optional `--rate` | new run: every order created; replay: every order returned as before |
| `approve` | decides the pending review of `--order` ids, `--review` ids, or every order of a result file (`--from`); `--outcome approve\|reject`, `--operator`, `--wait` | accepted, or replayed (same decision again) |
| `inspect` | business view (customer key), reviews (operator key), execution diagnostics (admin key) of `--order` ids or a result file | the order exists; a section whose key is not set is `skipped` |
| `cancel` | requests cancellation of `--order` ids or every order of a result file (customer key) | `202` recorded or `200` already requested; `409 dispatch-started` / `order-terminal` and `404` are reported as failed expectations |
| `reviews` | lists the reviews of `--order` ids or a result file, or every review with `--status` (operator key) | the list was answered |
| `escalate` | escalates the pending round of `--order` ids, or `--review` ids, before the deadline (`--operator`, `--reason`) | `escalated`, or `replayed` (the same next round again); `409 no-further-round` for the last round |
| `resolve` | resolves intervention cases (`--case`, `--action ConfirmExternalSuccess\|ConfirmExternalFailure\|Acknowledge`, `--evidence`, `--operator`, `--resolution-seed`) | `resolved:<path>` (Signal, Repair, RecordOnly) or `replayed:<path>` |
| `operator run` | the seeded scripted operator over `--order` ids or a result file (`--seed`, weights `--approve`/`--reject`/`--escalate`/`--ignore`, `--operators`, `--wait`; `--plan-only` prints the plan without sending) | every planned decision accepted or replayed |
| `identity`, `probe` | build identity; the M0 runtime probe through the admin API | |

Common options: `--url` (repeat or comma-separate one URL per replica;
default `ORDER_API_URLS`, else `http://127.0.0.1:8080`), `--replica N` (send
everything to the N-th URL), `--request-timeout`, `--max-attempts`,
`--output <file>`.

## Runs, Seeds, and Replay

A logical run is named by its run id (`--run-id`, 3 to 40 lower-case
letters, digits, or `-`; a fresh one is generated when omitted). Order `i` of
a run has the idempotency key `<run-id>-o<i:00000>`; customers are
`<run-id>-c<nn>`. The payloads come from the seed through a fully specified
generator (SplitMix64, one stream per order), so the same run id, seed, and
workload options give byte-identical requests on every machine and .NET
version; the result records the workload and its SHA-256 digest.

- **New run** (default): every request must create an order. If an answer is
  a replay, the run id was used before: exit 3, and nothing new was created.
  The exception is a replay right after this run's own ambiguous attempt
  (the first attempt took effect but its answer was lost).
- **Replay**: `--replay-of <result.json>` regenerates the workload from the
  recorded spec, refuses to send anything if the digest differs, and expects
  every request back as the order recorded in the file. `--replay` with
  `--run-id`/`--seed` does the same without the file (no order-id check).
- **Dry run**: `--dry-run` prints the generated requests and sends nothing.

Replica assignment is part of the run: round robin by index (default), or
`--distribution seeded`. Retries go to the next replica unless `--replica`
pins one.

## Scripted Operators

`operator run` decides, per order, one of approve, reject, escalate (then
approve the next round), or ignore (the deadline policy decides). The choice
for an order depends only on the seed, the weights, the operator list, and the
order id (SplitMix64 seeded from the order id's hash), not on the other orders
or their order in the input. The plan and its SHA-256 digest are part of the
result (`workloadDigest`), and `--plan-only` prints them without sending
anything. Decision ids derive from the review, the outcome, the operator, and
`operator-run-<seed>`, so running the same command again returns every
decision as a replay and makes no second decision; an escalation repeated for
the same round returns the same next round. Resolution ids of `resolve` derive
the same way from the case, action, operator, and `--resolution-seed`.

## Ambiguous Outcomes

A timeout, a transport error, or `408`, `425`, `429`, or `5xx` leaves open
whether the request took effect. The simulator then resends it unchanged
(same body, same idempotency key, or the same decision id), up to
`--max-attempts`, with growing delays (250 ms to 2 s). The order API turns a
repeat into a replay of the original, so a retry never creates a second
order. `4xx` answers are definite and never retried. When no attempt gets a
definite answer, the request is `unresolved` (exit 4); replaying the run
resolves it. Decision ids are derived from the review, outcome, operator,
and `--decision-seed`, so a repeated `approve` is a replay (also when the
review is no longer pending: the same decision is sent to the decided round,
and another decision gets `409 review-already-decided`).

## Results and Exit Codes

Each command prints one JSON document on stdout (and writes it to
`--output`) and one summary line on stderr. Per request it records the key,
payload hash, first replica, every attempt (replica, HTTP status, class
`answered`/`ambiguous`/`not-sent`, problem code, duration, and the replica
that answered as the server reports it in `X-Served-By`), the acceptance
(`created`, `replayed`, `conflict`, `refused`, `unresolved`, `not-sent`),
the order id, and, with `--follow`, the final status and history. The
summary separates acceptance (`accepted`, `created`, `replayed`) from
completion (`completed`, `finalStatuses`, `notReached`).

| Exit | Meaning |
|---|---|
| 0 | every expectation held |
| 1 | a definite answer differed (refusal, conflict, not terminal in time, no pending review) |
| 2 | invalid arguments, or a required key is not set |
| 3 | run identity mismatch: a new run met an earlier one, or a replay differs from its original |
| 4 | unresolved: some request has no definite answer after every retry |

When several apply, the lowest row wins.

## Credentials

The role keys are read only from `CUSTOMER_API_KEY`, `OPERATOR_API_KEY`, and
`ADMIN_API_KEY` (never from arguments, which other processes can see), sent
only as the `X-Api-Key` header, and masked in everything the tool writes.
URLs with user information are refused without being echoed. S23 checks
every output file for the keys.
