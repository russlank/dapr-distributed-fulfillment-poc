#!/usr/bin/env bash
# Observability baseline check (run after at least one runtime probe, e.g. after S00):
#   OBS-1 traces    Jaeger knows every Dapr role; at least one order-api trace of the last hour also
#                   contains workflow-host spans (trace context crossed Dapr service invocation)
#   OBS-2 metrics   Prometheus scrapes the Collector and every core sidecar successfully
# Jaeger 2.x serves its query API under /api/v3 (OTLP JSON); /api/services returns 404.
# Prints PASS/FAIL per check; exit 0 only if both pass.
. "$(dirname "$0")/../lib.sh"

failures=0
check() { [[ "$2" == PASS ]] || failures=$((failures + 1)); record "$@"; }

now="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
from="$(date -u -d '-1 hour' +%Y-%m-%dT%H:%M:%SZ)"
services="$(curl -s http://127.0.0.1:16686/api/v3/services)"
mkdir -p "$REPO_ROOT/artifacts/checks"
traces_file="$REPO_ROOT/artifacts/checks/jaeger-order-api-traces.json"
curl -s -o "$traces_file" "http://127.0.0.1:16686/api/v3/traces?query.service_name=order-api&query.start_time_min=$from&query.start_time_max=$now&query.num_traces=20"
summary="$(python - "$services" "$traces_file" "${DAPR_APPS[*]}" <<'PY'
import json, sys
known = set(json.loads(sys.argv[1] or "{}").get("services") or [])
expected = set(sys.argv[3].split())
by_trace = {}
for rs in (json.load(open(sys.argv[2], encoding="utf-8")).get("result") or {}).get("resourceSpans", []):
    name = next((a["value"].get("stringValue") for a in rs["resource"]["attributes"] if a["key"] == "service.name"), None)
    for ss in rs.get("scopeSpans", []):
        for span in ss.get("spans", []):
            by_trace.setdefault(span["traceId"], set()).add(name)
crossing = sum(1 for s in by_trace.values() if {"order-api", "workflow-host"} <= s)
missing = sorted(expected - known)
print(f"{len(known)}|{','.join(missing)}|{len(by_trace)}|{crossing}")
PY
)"
IFS='|' read -r known missing count crossing <<< "$summary"
if [[ -z "$missing" && "${crossing:-0}" -ge 1 ]]; then
  check OBS-1 PASS "every Dapr role in Jaeger; a trace crosses order-api and workflow-host" \
    "$known services known; order-api traces: $count, crossing into workflow-host: $crossing"
else
  check OBS-1 FAIL "every Dapr role in Jaeger; a crossing trace" "missing: ${missing:-none}; traces: ${count:-0}, crossing: ${crossing:-0}"
fi

# A target restarted moments ago stays "down" until its next scrape (every 15 s), so the check
# allows three scrape intervals before judging (observed right after S00's cold restarts).
waited=0
while :; do
  targets="$(curl -s http://127.0.0.1:9095/api/v1/targets)"
  health="$(python - "$targets" <<'PY'
import json, sys
active = json.loads(sys.argv[1])["data"]["activeTargets"]
down = [t["labels"]["instance"] for t in active if t["health"] != "up"]
print(f"{len(active)}|{len(down)}|{','.join(down)}")
PY
)"
  IFS='|' read -r total down which <<< "$health"
  [[ "${total:-0}" -ge 7 && "$down" == 0 ]] && break
  (( waited >= 45 )) && break
  sleep 5; waited=$((waited + 5))
done
if [[ "${total:-0}" -ge 7 && "$down" == 0 ]]; then
  check OBS-2 PASS "Prometheus scrapes the Collector and all core sidecars" "$total targets up (after ${waited}s)"
else
  check OBS-2 FAIL "all targets up" "$total targets, $down down: $which"
fi

(( failures == 0 ))
