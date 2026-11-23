#!/usr/bin/env bash
# Verification runner: runs the gates, integration tests, scenarios, and drills of the chosen suite,
# one step after another, and records every step's exit code and duration.
#
# Usage: scripts/verify.sh [--suite full|dev] [--only LIST] [--reuse] [--keep-awake]
#                          [--release-carrier-step TIMESPAN|default]
#
#   --suite full   milestone verification (default): gates, compose check, integration tests, then
#                  each stack from fresh volumes: release --replicas, drill --replicas (start lease
#                  2 s), core drill; a version/commit check on every stack; clean tree required.
#   --suite dev    development regression: the drill --replicas stack only, every scenario once
#                  (no release or core phase, no gates, no integration tests); volumes are kept.
#   --only LIST    comma-separated step ids and/or groups (smoke, m1-m5, release, core); a step
#                  id runs in the drill phase if it has one, else where it is defined (A:S04 forces
#                  the release phase). Implies the dev behavior for stacks unless --suite full.
#   --reuse        skip stack start-up when the running stack was built from HEAD with the same
#                  tree state and flavor (dev and --only only).
#   --keep-awake   on Windows, keep the machine from idle sleep while the run lasts.
#   --release-carrier-step  pace of the carrier simulator for the release scenarios after S00, OBS,
#                  and ACC, which always run at the simulator's own 10 s (default 00:00:02; `default`
#                  keeps 10 s for every step). The drill stack uses its 2 s profile.
#
# Results: artifacts/verify/<run>/steps.jsonl (one line per step), <phase>-<step>.log, summary.json,
# <phase>-order-api-logs.txt (drill phases: the order API replicas' logs before teardown);
# the closing table compares each step with the previous run of the same suite. Exit 1 if a step failed.
set -uo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.." || exit 2

SUITE=full; ONLY=""; REUSE=0; KEEP_AWAKE=0; RELEASE_CARRIER_STEP=00:00:02; SUITE_SET=0
while (( $# )); do
  case "$1" in
    --suite) SUITE=$2; SUITE_SET=1; shift ;;
    --only) ONLY=$2; shift ;;
    --reuse) REUSE=1 ;;
    --keep-awake) KEEP_AWAKE=1 ;;
    --release-carrier-step) RELEASE_CARRIER_STEP=$2; shift ;;
    -h|--help) sed -n '2,24p' "$0"; exit 0 ;;
    *) echo "unknown option $1" >&2; exit 2 ;;
  esac
  shift
done
[[ "$SUITE" == full || "$SUITE" == dev ]] || { echo "suite must be full or dev" >&2; exit 2; }
[[ -n "$ONLY" && $SUITE_SET == 0 ]] && SUITE=dev
[[ "$RELEASE_CARRIER_STEP" == default ]] && RELEASE_CARRIER_STEP=""

# --- step catalog: phase|id|command ----------------------------------------------------------------
# I: no stack; A: release --replicas; B: drill --replicas (start lease 2 s); C: core drill.
CATALOG=(
  "I|gates|bash ci/dotnet-gates.sh"
  "I|compose|bash ci/compose-config.sh"
  "I|integration|bash ci/integration-tests.sh"
  "A|S00|bash scripts/scenarios/S00-runtime-smoke.sh"
  "A|OBS|bash scripts/checks/observability-check.sh"
  "A|ACC|bash scripts/checks/order-acceptance-check.sh"
  "A|S02|bash scripts/scenarios/S02-parallel-checks.sh"
  "A|S09|bash scripts/scenarios/S09-cross-replica-approval.sh"
  "A|S23|bash scripts/scenarios/S23-deterministic-client-replay.sh"
  "A|S04|bash scripts/scenarios/S04-duplicate-submission.sh"
  "A|S24|bash scripts/scenarios/S24-client-timeout-retry.sh"
  "A|S43|bash scripts/scenarios/S43-idempotency-conflicts.sh"
  "A|S03|bash scripts/scenarios/S03-contention.sh"
  "A|S03-sqlonly|sqlonly_s03"
  "B|S51|bash scripts/scenarios/S51-concurrent-start.sh"
  "B|S52|bash scripts/scenarios/S52-signaling.sh"
  "B|REC|bash scripts/checks/fulfillment-recovery-check.sh"
  "B|S02|bash scripts/scenarios/S02-parallel-checks.sh"
  "B|S04|bash scripts/scenarios/S04-duplicate-submission.sh"
  "B|S24|bash scripts/scenarios/S24-client-timeout-retry.sh"
  "B|S43|bash scripts/scenarios/S43-idempotency-conflicts.sh"
  "B|R01|bash scripts/drills/R01-waiting-workflow-host-loss.sh"
  "B|R02|bash scripts/drills/R02-start-exactly-once.sh"
  "B|R03|bash scripts/drills/R03-lost-response-after-commit.sh"
  "B|R06c|bash scripts/drills/R06-compute-pool-outage.sh"
  "B|S03|bash scripts/scenarios/S03-contention.sh"
  "B|R04|bash scripts/drills/R04-crash-after-inventory-commit.sh"
  "B|R05|bash scripts/drills/R05-actor-relocation-under-contention.sh"
  "B|R06i|bash scripts/drills/R06-inventory-outage.sh"
  "B|R08|bash scripts/drills/R08-partial-reservation.sh"
  "B|S20|bash scripts/scenarios/S20-hot-sku-isolation.sh"
  "B|S13|bash scripts/scenarios/S13-payment-declined.sh"
  "B|S29|bash scripts/scenarios/S29-S31-operator-decisions.sh"
  "B|S30|bash scripts/scenarios/S30-seeded-operators.sh"
  "B|S39|bash scripts/scenarios/S39-cancellation-windows.sh"
  "B|S40|bash scripts/scenarios/S40-S41-expiry-before-capture-and-consume.sh"
  "B|R09|bash scripts/drills/R09-lost-provider-response.sh"
  "B|R10|bash scripts/drills/R10-interrupted-compensation.sh"
  "B|R11|bash scripts/drills/R11-carrier-crash-after-accept.sh"
  "B|R13|bash scripts/drills/R13-callback-anomalies.sh"
  "B|R19|bash scripts/drills/R19-R25-review-expiry-and-escalation.sh"
  "B|R20|bash scripts/drills/R20-R21-decision-while-hosts-down.sh"
  "B|R22|bash scripts/drills/R22-R23-conflicting-and-duplicate-decisions.sh"
  "B|R24|bash scripts/drills/R24-decision-versus-deadline.sh"
  "B|R26|bash scripts/drills/R26-undeterminable-payment.sh"
  "B|R27|bash scripts/drills/R27-cancel-versus-dispatch.sh"
  "B|R31|bash scripts/drills/R31-late-terminal-conflict.sh"
  "B|R33|bash scripts/drills/R33-provider-outage-and-crash.sh"
  "B|S01|bash scripts/scenarios/S01-happy-path-events.sh"
  "B|S48|bash scripts/scenarios/S48-shuffled-events.sh"
  "B|R14|bash scripts/drills/R14-broker-outage.sh"
  "B|R15|bash scripts/drills/R15-repeated-publication.sh"
  "B|R16|bash scripts/drills/R16-poison-events.sh"
  "B|R32|bash scripts/drills/R32-gap-repaired-by-replay.sh"
  "B|R23S|bash scripts/drills/R23-signal-redelivery.sh"
  "B|R12|bash scripts/drills/R12-callback-receiver-down.sh"
  "B|R29|bash scripts/drills/R29-lost-callbacks.sh"
  "B|R30|bash scripts/drills/R30-faulted-missing-mismatch.sh"
  "B|R07|bash scripts/drills/R07-state-store-outage.sh"
  "B|R28|bash scripts/drills/R28-scheduler-placement-outage.sh"
  "C|R00|bash scripts/drills/R00-failpoint-selftest.sh"
  "C|S49|bash scripts/scenarios/S49-execution-fence.sh"
  "C|R06c|bash scripts/drills/R06-compute-pool-outage.sh"
)
declare -A STEP_GROUPS=(
  [smoke]="S02,S04,S13,S29,S39"
  [m1]="S51,S52,REC,S02,S04,S24,S43,R01,R02,R03,R06c"
  [m2]="S03,R04,R05,R06i,R08,S20"
  [m3]="S13,S29,S30,S39,S40,R09,R10,R11,R13,R19,R20,R22,R24,R26,R27,R31,R33"
  [m4]="S01,S48,R14,R15,R16,R32,R23S,R12,R29,R30"
  [m5]="R07,R28"
  [release]="A:S00,A:OBS,A:ACC,A:S02,A:S09,A:S23,A:S04,A:S24,A:S43,A:S03,A:S03-sqlonly"
  [core]="C:R00,C:S49,C:R06c"
)

# --- select steps ----------------------------------------------------------------------------------
SELECTED=()   # "phase|id|command"
in_catalog() { local e; for e in "${CATALOG[@]}"; do [[ "${e%%|*}" == "$1" && "$(cut -d'|' -f2 <<< "$e")" == "$2" ]] && { echo "$e"; return 0; }; done; return 1; }
if [[ -z "$ONLY" ]]; then
  for e in "${CATALOG[@]}"; do
    p="${e%%|*}"
    [[ "$SUITE" == dev && "$p" != B ]] && continue
    SELECTED+=("$e")
  done
else
  wanted=()
  IFS=',' read -r -a items <<< "$ONLY"
  for it in "${items[@]}"; do
    if [[ -n "${STEP_GROUPS[$it]:-}" ]]; then IFS=',' read -r -a g <<< "${STEP_GROUPS[$it]}"; wanted+=("${g[@]}"); else wanted+=("$it"); fi
  done
  for w in "${wanted[@]}"; do
    if [[ "$w" == *:* ]]; then e="$(in_catalog "${w%%:*}" "${w#*:}")" || { echo "unknown step $w" >&2; exit 2; }
    else e="$(in_catalog B "$w" || in_catalog A "$w" || in_catalog C "$w" || in_catalog I "$w")" || { echo "unknown step $w" >&2; exit 2; }
    fi
    dup=0; for s in "${SELECTED[@]}"; do [[ "$s" == "$e" ]] && dup=1; done
    (( dup )) || SELECTED+=("$e")
  done
  # keep catalog order (phases I, A, B, C)
  ordered=(); for e in "${CATALOG[@]}"; do for s in "${SELECTED[@]}"; do [[ "$s" == "$e" ]] && ordered+=("$e"); done; done
  SELECTED=("${ordered[@]}")
fi
phase_used() { local s; for s in "${SELECTED[@]}"; do [[ "${s%%|*}" == "$1" ]] && return 0; done; return 1; }

TREE=clean; [[ -n "$(git status --porcelain)" ]] && TREE=dirty
if [[ "$SUITE" == full && "$TREE" != clean ]]; then echo "suite full needs a clean tree" >&2; exit 2; fi
HEAD10="$(git rev-parse --short=10 HEAD)"
VERSION="$(sed -n 's|.*<VersionPrefix>\(.*\)</VersionPrefix>.*|\1|p' src/Directory.Build.props)"
SUFFIX="$(sed -n 's|.*<VersionSuffix>\(.*\)</VersionSuffix>.*|\1|p' src/Directory.Build.props)"
[[ -n "$SUFFIX" ]] && VERSION="$VERSION-$SUFFIX"

RUN="$SUITE-$(date -u +%Y%m%dT%H%M%SZ)-$HEAD10"
OUT="artifacts/verify/$RUN"; mkdir -p "$OUT"
: > "$OUT/steps.jsonl"
FAILED=0
vr_log() { printf '%s %s\n' "$(date -u +%H:%M:%SZ)" "$*" | tee -a "$OUT/run.log"; }

if (( KEEP_AWAKE )) && command -v powershell.exe >/dev/null 2>&1; then
  # ES_CONTINUOUS | ES_SYSTEM_REQUIRED while this child lives; it ends with the run.
  powershell.exe -NoProfile -Command "\$k = Add-Type -MemberDefinition '[DllImport(\"kernel32.dll\")] public static extern uint SetThreadExecutionState(uint f);' -Name KA -Namespace W -PassThru; \$k::SetThreadExecutionState([uint32]'0x80000001') | Out-Null; while (\$true) { Start-Sleep 30 }" >/dev/null 2>&1 &
  AWAKE_PID=$!
  trap 'kill $AWAKE_PID 2>/dev/null' EXIT
  vr_log "keep-awake held (pid $AWAKE_PID)"
fi

vr_record() { # phase id exit seconds started
  printf '{"phase":"%s","step":"%s","exit":%s,"seconds":%s,"started":"%s"}\n' "$1" "$2" "$3" "$4" "$5" >> "$OUT/steps.jsonl"
  (( $3 == 0 )) || FAILED=1
  vr_log "$1-$2 exit $3 (${4}s)"
}
run_step() { # phase id command...
  local phase=$1 id=$2; shift 2
  local started; started="$(date -u +%FT%TZ)"; local t=$SECONDS
  "$@" > "$OUT/$phase-$id.log" 2>&1
  vr_record "$phase" "$id" $? $((SECONDS - t)) "$started"
}
sqlonly_s03() { # S03 with the inventory replicas switched to SqlOnly, then back to actors
  ( . scripts/lib.sh; export COMPOSE_PROFILES=replicas; local s
    for s in inventory-service inventory-service-2 inventory-service-3; do INVENTORY_COORDINATION=SqlOnly restart_pair "$s" || exit 1; done )
  local rc=$?; (( rc == 0 )) && { bash scripts/scenarios/S03-contention.sh; rc=$?; }
  ( . scripts/lib.sh; export COMPOSE_PROFILES=replicas; local s
    for s in inventory-service inventory-service-2 inventory-service-3; do INVENTORY_COORDINATION=Actor restart_pair "$s" || exit 1; done ) || rc=1
  return $rc
}
version_check() ( # flavor: every running application replica and both simulators report VERSION from HEAD
  . scripts/lib.sh
  local names svc id v c f t bad=0 n=0
  names="$(IFS='|'; echo "${DAPR_APPS[*]}|${SIMULATORS[*]}")"
  for svc in $(docker compose ps --services --status running | grep -E "^($names)(-[0-9]+)?$" | sort); do
    id="$(http_body "$svc" /system/component-identity | tr -d '\r\n')"
    v="$(json_get "$id" version)"; c="$(json_get "$id" commit)"; f="$(json_get "$id" buildFlavor)"; t="$(json_get "$id" treeState)"
    echo "$svc version=$v commit=$c flavor=$f tree=$t"
    [[ "$v" == "$VERSION" && "$c" == "$HEAD10" && "$f" == "$1" && "$t" == "$TREE" ]] || { echo "MISMATCH $svc"; bad=1; }
    n=$((n + 1))
  done
  echo "$n services; expected $VERSION / $HEAD10 / $1 / $TREE"
  (( n >= 8 && bad == 0 ))
)
SHIPPED_PACE_STEPS="S00 OBS ACC"   # release steps that run with the carrier simulator's own pace
set_carrier_pace() ( # applies $CARRIER_SIM_STEP to the carrier simulator (recreated only if it differs)
  . scripts/lib.sh
  docker compose --profile replicas up -d --no-build --no-deps carrier-simulator || exit 1
  wait_until 90 "carrier-simulator ready" ready carrier-simulator || exit 1
  echo "carrier-simulator: CARRIER_SIM_STEP='$(docker compose exec -T carrier-simulator printenv CARRIER_SIM_STEP)' callbacks=$(docker compose exec -T carrier-simulator printenv CarrierSim__CallbackUrls)"
)
stack_matches() { # flavor: the running stack was built from HEAD with this tree state and flavor
  local id; id="$(. scripts/lib.sh; http_body order-api /system/component-identity 2>/dev/null | tr -d '\r\n')"
  [[ "$id" == *"\"commit\":\"$HEAD10\""* && "$id" == *"\"treeState\":\"$TREE\""* && "$id" == *"\"buildFlavor\":\"$1\""* ]]
}
stop_other() { # project: stop the other stack (same host ports), keeping its volumes
  COMPOSE_PROJECT_NAME="$1" docker compose --profile replicas --profile scale down --remove-orphans >/dev/null 2>&1 || true
}
bring_up() { # phase flavor stack-up args...
  local phase=$1 flavor=$2; shift 2
  if [[ "$SUITE" == full ]]; then
    run_step "$phase" reset docker compose --profile replicas --profile scale down -v --remove-orphans
  elif (( REUSE )) && stack_matches "$flavor"; then
    vr_log "$phase reusing the running $flavor stack (built from $HEAD10, $TREE)"; return 0
  fi
  [[ "$SUITE" == full ]] || stop_other fulfillment
  run_step "$phase" up bash scripts/stack-up.sh "$@"
  run_step "$phase" version version_check "$flavor"
}
keep_logs() { # phase: the order API replicas' logs (event dispositions, reconciler) before the stack goes away
  docker compose --profile replicas logs --no-color --timestamps order-api order-api-2 > "$OUT/$1-order-api-logs.txt" 2>&1 || true
}
run_phase_steps() { local e; for e in "${SELECTED[@]}"; do [[ "${e%%|*}" == "$1" ]] || continue
  local id cmd; id="$(cut -d'|' -f2 <<< "$e")"; cmd="$(cut -d'|' -f3- <<< "$e")"
  # shellcheck disable=SC2086
  run_step "$1" "$id" $cmd; done; }

vr_log "verify $RUN: suite $SUITE${ONLY:+, only $ONLY}; version $VERSION; ${#SELECTED[@]} steps; results in $OUT"

if phase_used I; then
  # free the drill stack's memory before the Testcontainers run (milestone runs start from nothing)
  [[ "$SUITE" == full ]] && run_step I drill-down env COMPOSE_PROJECT_NAME=fulfillment-drill docker compose --profile replicas --profile scale down -v --remove-orphans
  run_phase_steps I
fi

if phase_used A; then
  ( unset COMPOSE_PROJECT_NAME BUILD_FLAVOR DAPR_POC_ENV START_DISPATCHER_LEASE
    # The stack starts at the carrier simulator's own pace (10 s per status): S00, OBS, and ACC run
    # the release time profile end to end as shipped; then the carrier switches to the fast pace.
    export CARRIER_SIM_STEP=""
    [[ "$SUITE" == full ]] && run_step A reset-all bash scripts/reset.sh --yes
    if [[ "$SUITE" == full ]] || ! { (( REUSE )) && stack_matches release; }; then
      [[ "$SUITE" == full ]] || stop_other fulfillment-drill
      run_step A up bash scripts/stack-up.sh --replicas
      run_step A version version_check release
    else vr_log "A reusing the running release stack"; fi
    pace=default
    for e in "${SELECTED[@]}"; do [[ "${e%%|*}" == A ]] || continue
      id="$(cut -d'|' -f2 <<< "$e")"; cmd="$(cut -d'|' -f3- <<< "$e")"
      want=fast; [[ " $SHIPPED_PACE_STEPS " == *" $id "* || -z "$RELEASE_CARRIER_STEP" ]] && want=default
      if [[ "$want" != "$pace" || ( "$pace" == default && -z "${PACE_SET:-}" && "$REUSE" == 1 ) ]]; then
        if [[ "$want" == fast ]]; then export CARRIER_SIM_STEP="$RELEASE_CARRIER_STEP"; else export CARRIER_SIM_STEP=""; fi
        run_step A "carrier-$want" set_carrier_pace; pace=$want; PACE_SET=1
      fi
      # shellcheck disable=SC2086
      run_step A "$id" $cmd
    done
    [[ "$SUITE" == full ]] && run_step A down bash scripts/stack-down.sh
    exit $FAILED ) || FAILED=1
fi

if phase_used B; then
  ( export COMPOSE_PROJECT_NAME=fulfillment-drill BUILD_FLAVOR=drill DAPR_POC_ENV=drill START_DISPATCHER_LEASE=00:00:02 CARRIER_SIM_STEP=""
    bring_up B drill --drill --replicas
    run_phase_steps B
    keep_logs B
    [[ "$SUITE" == full ]] && run_step B down docker compose --profile replicas --profile scale down
    exit $FAILED ) || FAILED=1
fi

if phase_used C; then
  ( export COMPOSE_PROJECT_NAME=fulfillment-drill BUILD_FLAVOR=drill DAPR_POC_ENV=drill CARRIER_SIM_STEP=""
    unset START_DISPATCHER_LEASE
    # the core profile must not inherit the replicas of a drill --replicas stack (R00 refuses them)
    [[ "$SUITE" == full ]] || docker compose --profile replicas --profile scale down >/dev/null 2>&1
    bring_up C drill --drill
    run_phase_steps C
    keep_logs C
    exit $FAILED ) || FAILED=1
fi

# --- summary and comparison with the previous run of this suite --------------------------------------
python - "$OUT" "$SUITE" "$FAILED" <<'PY'
import json, os, sys, glob
out, suite, failed = sys.argv[1], sys.argv[2], sys.argv[3] == "1"
steps = [json.loads(l) for l in open(os.path.join(out, "steps.jsonl"), encoding="utf-8") if l.strip()]
prev = None
for d in sorted(glob.glob(os.path.join(os.path.dirname(out), suite + "-*")), reverse=True):
    if d != out and os.path.exists(os.path.join(d, "summary.json")):
        prev = json.load(open(os.path.join(d, "summary.json"), encoding="utf-8")); break
before = {f'{s["phase"]}-{s["step"]}': s["seconds"] for s in (prev or {}).get("steps", [])}
total = sum(s["seconds"] for s in steps)
summary = {"run": os.path.basename(out), "suite": suite, "passed": not failed and all(s["exit"] == 0 for s in steps),
           "seconds": total, "previous": (prev or {}).get("run"), "steps": steps}
json.dump(summary, open(os.path.join(out, "summary.json"), "w", encoding="utf-8"), indent=1)
print(f'{"step":<18}{"exit":>5}{"s":>7}{"prev":>7}')
for s in steps:
    k = f'{s["phase"]}-{s["step"]}'; b = before.get(k)
    flag = "  slower" if b is not None and s["seconds"] > max(1.5 * b, b + 30) else ""
    print(f'{k:<18}{s["exit"]:>5}{s["seconds"]:>7}{"" if b is None else b:>7}{flag}')
print(f'total {total} s ({total / 60:.1f} min); {"PASS" if summary["passed"] else "FAIL"}; previous run: {summary["previous"] or "-"}')
PY
exit $FAILED
