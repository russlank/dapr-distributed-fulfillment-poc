# Walkthrough

A guided tour of the system in six steps, on the release stack with two order
API replicas. `scripts/walkthrough.sh` explains each step as it runs it and
checks the result in the databases, so the tour doubles as an acceptance test
for a fresh clone.

```bash
bash scripts/walkthrough.sh --fresh   # from scratch: reset, build, start, tour (about 10 minutes)
bash scripts/walkthrough.sh           # the tour only, on a running release stack with --replicas
```

`--fresh` removes the stack and its data, creates `.env` if it is missing,
and starts the stack with `scripts/stack-up.sh --replicas`. The run ends with
exit code 0 when every step passed and writes its results to
`artifacts/walkthrough/<run>/results.jsonl`.

## Contents

- [The Steps](#the-steps)
- [What to Look at Afterwards](#what-to-look-at-afterwards)
- [When a Step Fails](#when-a-step-fails)

## The Steps

| Step | What happens | What it shows | Checked |
|---|---|---|---|
| W1 identity | every role and simulator reports its build | each role runs as an app with its own sidecar, all from one commit | every service reports the current commit and the release flavor |
| W2 order | an ordinary order (one unit of `SKU-1`) is submitted | acceptance in one SQL commit, the start dispatcher, the workflow across compute, inventory, payment, packing, and shipping, delivery by the carrier simulator | `Completed` with the full status history and one `OrderCompleted` event |
| W3 idempotency | the same request again, with the same `Idempotency-Key`, through the second replica | the stored answer is replayed from any replica | same order id, `replayed: true`, one order for the customer |
| W4 review | an order from 1,000.00; the approval goes through the other replica | the review task and its deadline live in SQL; the workflow waits durably and is only woken | `AwaitingApproval` in the history, approval `200`, `Completed` |
| W5 durability | while a review order waits, both workflow hosts are killed (app and sidecar); the approval is sent; the hosts are started again | a decision is accepted even with no workflow host running (decision, audit, and pending signal commit in SQL); the workflow resumes from its history | approval `200` while the hosts are down; `Completed` once, one `OrderCompleted` event |
| W6 cancellation | an order waiting for review is cancelled | cancellation until dispatch starts; the order is still in review, so nothing was reserved or charged | cancellation `202`; `Cancelled`; no hold |

## What to Look at Afterwards

- **Traces:** http://127.0.0.1:16686 (Jaeger). Search for the order API and
  follow an order across the services and sidecars.
- **Dashboards:** http://127.0.0.1:3000 (Grafana; user `admin`, password
  `GRAFANA_ADMIN_PASSWORD` from `.env`), for example the workflow timeline and
  reviews and cases dashboards.
- **An order's timeline:** the run prints the order ids; then
  `curl -s http://127.0.0.1:8080/system/orders/<orderId>/timeline -H "X-Api-Key: $ADMIN_API_KEY"`.
- **The databases:** SQL Server on `127.0.0.1:14330` (`sa` password in
  `.env`), for example `orders.OrderStatusHistory` and `orders.PendingSignal`.

Next steps: the scenarios and drills in [testing.md](testing.md), and what
they guarantee in [guarantees.md](guarantees.md).

## When a Step Fails

The run continues after a failed step and reports every result. Look at the
step's `observed` value in `results.jsonl`, then at the order's history
(`GET /orders/<orderId>`) and the service logs (`docker compose logs`).
Startup problems are covered in [operations.md](operations.md#troubleshooting).
