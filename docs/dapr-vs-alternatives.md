# Stock Actors Versus SQL-Only Coordination

The inventory can coordinate reservations in two ways, switched per replica by
`INVENTORY_COORDINATION` (`Actor`, the default, or `SqlOnly`). This page reports
what was measured, says what follows from the design and what only from the
measurements, and sizes the hold lifetime from the observed hold durations.

## The Two Modes

In both modes **SQL decides**. Every reservation, release, expiry, and
consumption is one transaction under an application lock for the order, with
conditional updates that cannot oversell (`OnHand - Reserved >= quantity`) and
a stored result per operation key ([architecture.md](architecture.md#inventory)).

- **Actor:** a stock actor per warehouse and SKU (Dapr actors, placed by Dapr
  Placement on one inventory replica) serializes the reservations and releases
  of its stock row, so contention for a hot SKU queues in the actor.
- **SqlOnly:** the same operations run directly from whichever inventory
  replica executes the activity; contention for a hot SKU queues in SQL Server
  (lock waits, more concurrent sessions).

**By design, not measured:** correctness is the same in both modes, because the
guarded SQL is the decision and the actor only orders the callers. The actor
adds a dependency on Placement and an activation (or relocation) when its
replica changes.

## Measurement

Contention scenario S03: 500 one-unit orders for 100 units of one SKU (60 in
W1, 40 in W2), 32 requests in flight over both order API replicas, each
order followed to its end. One run per mode on a freshly created drill stack
(`scripts/experiments/coordination-comparison.sh`), 2026-11-23: Docker
Desktop on Windows 11 (20 CPUs, 32 GB), SQL Server 2022 in a container
limited to 2 GiB, three inventory replicas, two order API and two workflow
host replicas, drill time profile, release-equivalent code at `0.6.0-dev`.

| Figure | Actor | SqlOnly |
|---|---|---|
| Outcome (orders, completed, rejected) | 500, 100, 400; stock consistent | 500, 100, 400; stock consistent |
| Wall time, first acceptance to last terminal status | 63.3 s | 67.4 s |
| Reservation step per order, p50 / p95 / max | 412 / 1,334 / 1,647 ms | 552 / 1,088 / 1,777 ms |
| End to end per completed order, p50 / p95 | 12.1 / 13.5 s | 11.2 / 12.9 s |
| Hold lifetime per consumed hold, p50 / p95 / max | 2.3 / 2.8 / 4.8 s | 1.9 / 2.6 / 2.6 s |
| SQL Server memory at peak (container) | 1,115 MiB | 1,357 MiB |
| SQL Server user sessions at peak | 151 | 196 |
| Lock wait accumulated in SQL Server during the run | 78 s | 96 s |

Run-to-run variation is of the order of the differences: an earlier actor run
on another stack took 61.2 s with a reservation p50 of 478 ms and an end-to-end
p50 of 9.5 s.

**Measured, with that caveat:**

- Both modes are correct under hot-SKU contention.
- Throughput and latencies are close; neither mode is clearly faster here. The
  per-order latency is dominated by the rest of the fulfillment (payment,
  packing, dispatch, the carrier's steps), not by the reservation.
- SqlOnly puts the contention on SQL Server: about 30 % more peak sessions,
  about 20 % more lock wait, and about 240 MiB more SQL Server memory at peak.
- An earlier SqlOnly run on a stack that had run drills for three hours
  exhausted SQL Server's memory (error 701) and stopped it; the same run on a
  fresh stack passed ([journal.md](journal.md)). The memory headroom of the
  local SQL Server is a limit of this setup, and SqlOnly reaches it first.

**Observed in drills, actor mode only:** while Placement is down, orders that
need a stock actor wait and then complete once (R28); when an actor's replica
dies under contention, the actor moves to another replica and the stock stays
correct (R05); a hot SKU does not slow orders for other SKUs (S20). The same
outages were not run in SqlOnly mode; by design SqlOnly does not depend on
Placement.

**Choice for this PoC:** actors stay the default (they keep SQL Server's load
lower under contention, and they are part of what the PoC evaluates). SqlOnly
remains a supported fallback, for example while Placement is unavailable, with
identical guarantees and more load on SQL Server.

## Hold Lifetime

A hold lives from the reservation until its order consumes it (after the
capture), is released, or expires. Across every consumed hold of the two
measurement runs (200 holds), the lifetime was p50 1.97 s, p95 2.70 s, max
4.80 s, in the drill time profile (carrier steps of 2 s; the release profile's
steps take longer, but holds are consumed before dispatch).

- The default lifetime (`INVENTORY_HOLD_TTL`, 30 min; drill 60 s) is three
  orders of magnitude above the normal lifetime, and the one extension before
  the capture (30 min; drill 30 s) adds the same again.
- A shorter lifetime would not return stock sooner in the normal flow (holds
  end in seconds); it would only make pauses (an open intervention case, an
  outage of the payment provider or a store) more likely to expire holds and
  compensate orders.
- The lifetime is therefore sized by the pause the system should ride out,
  not by the normal flow: 30 minutes stays, and an order paused longer is
  compensated safely (never oversold, never captured without valid holds).
