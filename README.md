# Dapr Distributed Fulfillment PoC

A reference implementation of distributed order fulfillment with Dapr
Workflow, Actors, and Pub/Sub on .NET 10, with SQL Server as the source of
business truth, external client, carrier, and payment-provider simulators,
and scripted failure drills. Synthetic data only; not production software.

Status: early. The Dapr runtime has been checked with a small compatibility
spike; the fulfillment services are not built yet.

| Read | For |
|---|---|
| [docs/versions.md](docs/versions.md) | pinned versions and what the pinned Dapr runtime was observed to do |
| [docs/journal.md](docs/journal.md) | what broke and what changed because of it |
| [spikes/dapr-compat/README.md](spikes/dapr-compat/README.md) | the compatibility spike and how to run it |

License: MIT (license file added with the solution skeleton).
