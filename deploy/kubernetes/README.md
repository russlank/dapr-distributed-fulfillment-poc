# Kubernetes Template

A generic starting point for running the fulfillment services on Kubernetes
with Dapr. It is a Kustomize base (`base/`) that mirrors the Compose stack's
replicas profile.

**Status: template, not verified on a cluster.** It renders with
`kubectl kustomize deploy/kubernetes/base` (24 objects), but it has not been
applied to a cluster or exercised by the drills. Every guarantee in this
repository was demonstrated on the Compose stack only. Treat this as a
template to adapt, not a tested deployment.

## What It Contains

| Path | Objects |
|---|---|
| `base/namespace.yaml` | namespace `fulfillment` |
| `base/settings.yaml` | ConfigMap `fulfillment-settings`: SQL Server host, OTLP endpoint, timing profile |
| `base/dapr/` | Dapr `Configuration` (`fulfillment`), state store (SQL Server, actor store), pub/sub (RabbitMQ), resiliency |
| `base/apps/` | one Deployment per Dapr role, with Dapr sidecar annotations; Service for `order-api` (client API and carrier callbacks) |
| `base/migrations/` | one Job per business database (`migrate` command of the role's image) |
| `base/simulators/` | carrier and payment provider simulators (no sidecar; stand-ins for external parties) |
| `base/*.env.example` | secret values: copy to `*.env` (git-ignored) and fill in |

Replica counts follow the Compose replicas profile: order API 2, workflow
host 2, compute worker 2, inventory 3, payment 2, shipping 2.

## Prerequisites

- Kubernetes with the Dapr control plane **1.19.1** installed (for example
  with the Dapr Helm chart). In Kubernetes, Dapr provides Placement,
  Scheduler (a StatefulSet with persistent volumes), Sentry (mTLS between
  sidecars, on by default) and the sidecar injector.
- SQL Server reachable from the cluster, prepared once with
  `db/init/init.sql` (databases, logins, the Dapr state database). Run it
  with `sqlcmd` as an administrator.
- RabbitMQ reachable from the cluster.
- The role images built with `BUILD_FLAVOR=release` and pushed to a
  registry. Never deploy drill images outside a drill environment.
- An OTLP collector (optional; the Compose collector configuration can be
  reused).

## Deploy

```bash
cd deploy/kubernetes/base
for f in secrets dapr-api-token app-api-token; do cp "$f.env.example" "$f.env"; done
# fill in the three .env files; set SQL_HOST and the OTLP endpoint in settings.yaml
# set your registry and tag under images: in kustomization.yaml
kubectl kustomize . | less   # review what will be applied
kubectl apply -k .
kubectl -n fulfillment wait --for=condition=complete job --all --timeout=5m
kubectl -n fulfillment get pods
```

Each application checks its database schema at startup and exits if it is
not current. Until the migration jobs complete, pods restart; they become
ready afterwards. To avoid the restarts, apply the jobs first or add an init
container that waits for them.

## Differences from the Compose Stack

| Concern | Compose | This template |
|---|---|---|
| Sidecar | explicit `daprd` container per replica | injected by Dapr from the pod annotations |
| Placement, Scheduler | containers in the stack | Dapr control plane |
| mTLS between sidecars | off (one trusted host network) | on (Sentry) |
| Secrets | `.env` file, `secretstores.local.env` | Kubernetes secrets (Dapr's default secret store in Kubernetes) |
| Database setup | `db-init` and `*-db-migrate` one-shot containers | `init.sql` run by an administrator; migration Jobs |
| Toxiproxy, drill failpoints | drill stack only | not included |
| Monitoring (Prometheus, Grafana, Alertmanager, Jaeger) | in the stack | not included; scrape the apps' OTLP export and the sidecars' metrics port 9090; the rules in `monitoring/` apply unchanged |
| Ports | bound to 127.0.0.1 | ClusterIP Services; add an Ingress for `order-api` if clients are outside the cluster |

Not included: Ingress, NetworkPolicies, Dapr access-control policies (see
the comment in `base/dapr/configuration.yaml`), pod disruption budgets,
autoscaling.

## Workflow Changes on Kubernetes

The patch rollout in the operations guide maps to two rolling updates of the
`workflow-host` Deployment:

1. Deploy the new image with `WORKFLOW_PATCHES_DISABLED=<patch>`: new pods
   run the new code with the patch held back, so every host keeps the old
   path while old and new pods overlap.
2. Remove the variable: the second rolling update enables the patch pod by
   pod.

Rolling back is the same: hold the patch back again with the variable.

**Do not terminate or purge a workflow instance that recorded a patch.**
On Dapr 1.19.1 such an instance stays `Stalled` for good. See "Never
Terminate a Patched Workflow" in `docs/operations.md`. This holds on
Kubernetes as on Compose.
