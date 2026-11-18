# Security

A local laboratory with synthetic data. These measures keep the trust
boundaries explicit; they are not production hardening.

## Trust Boundaries

| Boundary | Protection |
|---|---|
| host → public endpoints | ports bound to `127.0.0.1` only; admin endpoints require `X-Api-Key` (`ADMIN_API_KEY`); customer, operator, and carrier credentials arrive with their endpoints |
| app → own sidecar | the sidecar requires `DAPR_API_TOKEN` on its API |
| sidecar → app | the sidecar sends `APP_API_TOKEN`; the app rejects requests to sidecar-only routes (`/internal`, `/actors`, `/dapr`, `/events`) without it |
| app → database | one SQL login per owning service, `db_owner` on its own database only; the state store has its own login limited to `DaprStateDb` |
| sidecars | ports not published to the host |
| external simulators | no Dapr sidecar or SDK; ordinary HTTP only |

Dapr mTLS and access-control policies need the Sentry service and are not
used in Compose.

## Secrets

Secrets live only in `.env` (git-ignored, generated with random values).
Component files reference them by name through the `secretstores.local.env`
store; Compose files reference them as `${NAME:?...}` so a missing value stops
startup. Logs, traces, and metrics carry ids and status codes, never
connection strings, tokens, or payloads.

## Failure Injection

Failure injection is compiled only into drill images and acts only in the
drill environment; see [resilience-drills.md](resilience-drills.md).
