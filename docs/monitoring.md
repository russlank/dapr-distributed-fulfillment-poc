# Monitoring

## Pipeline

Every .NET service exports logs, traces, and metrics over OTLP (when
`OTEL_EXPORTER_OTLP_ENDPOINT` is set) to the OpenTelemetry Collector. Every
sidecar exports traces to the same Collector (`dapr/config/fulfillment.yaml`)
and serves Prometheus metrics on port 9090.

| Signal | Path | View |
|---|---|---|
| traces | services and sidecars → Collector → Jaeger | http://127.0.0.1:16686 |
| application metrics | services → Collector (Prometheus exporter, port 8889) → Prometheus | http://127.0.0.1:9095 |
| sidecar metrics | Prometheus scrapes `<app>:9090` | Prometheus, Grafana |
| logs | container stdout; also OTLP to the Collector (no log backend yet) | `docker compose logs` |
| alerts | Prometheus rules (`monitoring/prometheus/alerts.yml`) → Alertmanager (a sink: nothing leaves the machine) | http://127.0.0.1:9093, Prometheus `/alerts` |

Services export metrics every 5 s (`OTEL_METRIC_EXPORT_INTERVAL`);
Prometheus scrapes and evaluates rules every 5 s. Application metrics carry
the service in `exported_job` and the replica in `exported_instance`; sidecar
metrics carry `app_id`.

Grafana (http://127.0.0.1:3000, folder "Fulfillment") is provisioned with
Prometheus and Jaeger data sources and eight dashboards: (a) system overview,
(b) workflow timeline, (c) inventory contention and invariants, (d) outbox and
messaging, (e) scaling and client-generated load, (f) failure-drill journal,
(g) carrier callbacks and retries, (h) reviews, decisions, deadlines, and
intervention cases.

## Resource Attributes

`service.name` is the role (the Dapr app id for Dapr roles),
`service.version` the build version, `service.instance.id` the replica
name. Health endpoints are excluded from tracing.

## Application Metrics

| Metric | Meaning |
|---|---|
| `workflow_start_attempts_total{result}` | start attempts by result: `Started`, `AlreadyExists`, `Ambiguous`, `Rejected`, `LeaseLost`, `AlreadyClosed` (the order completed before the start was acknowledged), `Unexpected` |
| `workflow_start_requests_closed_terminal_total` | start requests closed because the order was already terminal |
| `fence_rejections_total` | execution claims rejected because the order was already terminal |
| `workflow_signal_attempts_total{result}` | signal delivery attempts by result: `Forwarded`, `Discarded`, `Stuck`, `NotStarted`, `NotRunning`, `Ambiguous`, `Rejected`, `LeaseLost`, `Unexpected` |
| `pending_signals_oldest_age_seconds` | age of the oldest pending signal (each dispatcher poll) |
| `start_requests_aging` | pending start requests older than the backlog age (2 min; drill 15 s) |
| `pending_signals_aging{kind}` | pending signals older than the backlog age, by kind: `review`, `intervention`, `carrier`, `other` |
| `carrier_forwards_aging` | pending carrier forwards older than the backlog age |
| `event_dead_letters_recent` | event dead letters recorded within the window (30 min; drill 90 s) |
| `projection_gaps_aging` | timeline streams waiting for a missing event longer than the gap age (5 min; drill 5 s), except gaps known to be unresolvable (a `ProjectionGap` case, alert `ProjectionGapUnresolvable`) |
| `intervention_cases_active{kind,state}` | active cases (`Open`, `Reconciling`, `Escalated`) |
| `outbox_pending{source}`, `outbox_aging{source}`, `outbox_stalled{source}` | outbox depth per source; rows older than the aging threshold (5 min; drill 15 s); stalled rows |
| `outbox_published_total{source}`, `outbox_publish_failures_total{source}` | relay publications |
| `event_history_dispositions_total{source,disposition}`, `event_stream_repairs_total{source}` | consumer dispositions; streams repaired on delivery |
| `reconciler_actions_total{action}` | reconciler actions: cases by kind, `Nudge`, `OutboxReplay`, `OrderProgressStalled`, `ProjectionGapUnresolvable` |
| `carrier_callbacks_total{result}`, `carrier_forward_outcomes_total{outcome}` | ingress results; Shipping's outcomes |
| `carrier_callbacks_dead_lettered_total` | callbacks the carrier simulator gave up on |
| `provider_requests_total{call,result}` | payment provider requests by result |
| `holds_overdue` | holds still `Held` longer than the grace after their expiry (5 min; drill 15 s) |

The gauges apply their thresholds in the service, per time profile, so every
alert rule asks only for a value above zero.

## Dapr Metrics

Observed on Dapr 1.19.1 (sidecar port 9090): workflow
`dapr_runtime_workflow_execution_count{workflow_name,status}`,
`..._execution_latency`, `..._scheduling_latency`, `..._lock_wait`,
`..._operation_count`, `..._activity_execution_count{activity_name,status}`,
`..._activity_execution_latency`, `..._local_wake_count`; actors
`dapr_runtime_actor_pending_actor_calls`, `dapr_runtime_actor_reminders_fired_total`;
pub/sub `dapr_component_pubsub_egress_count{success}`,
`dapr_component_pubsub_ingress_count{process_status}` with latencies. The
application metrics stay the primary source for business state.

## Alerts

Seventeen rules; each has a runbook in [operations.md](operations.md#alerts)
and is checked in the drill that causes it ([testing.md](testing.md)).
## Rules

- Identifiers (order, workflow, operation, event ids) go into traces and
  logs, never into metric labels.
- No payloads, connection strings, or tokens in telemetry.
