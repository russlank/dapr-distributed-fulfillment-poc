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

Grafana (http://127.0.0.1:3000) is provisioned with Prometheus and Jaeger
data sources. Dashboards and alert rules arrive with the observability
milestone.

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

## Rules

- Identifiers (order, workflow, operation, event ids) go into traces and
  logs, never into metric labels.
- No payloads, connection strings, or tokens in telemetry.
