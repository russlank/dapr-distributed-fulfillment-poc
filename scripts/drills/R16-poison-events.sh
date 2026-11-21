#!/usr/bin/env bash
# R16 (S18, S20) a poison event is dead-lettered without blocking others, and no payload reaches the logs.
# Drill stack: bash scripts/stack-up.sh --drill --replicas
# Through the drill-only publisher, on the inventory topic: a malformed message (not JSON), an envelope with an
# unknown schema version, and a sentinel event the consumer fails on every time (drillPoison). Each carries a
# unique marker string in its data. Meanwhile ordinary orders run.
#   R16-recorded      dead-letter rows: Malformed, UnknownSchemaVersion (rejected at once), and DeliveryFailed for
#                     the sentinel (via the dead-letter topic after bounded redelivery)
#   R16-bounded       the sentinel was redelivered a bounded number of times (more than one, at most the
#                     resiliency's retries plus one), then stopped
#   R16-others        ordinary orders submitted around the poison events Completed, their events applied
#   R16-no-payload    neither the order API logs nor the dead-letter table contain the marker (ids and reasons only)
DRILL_ID=R16
. "$(dirname "$0")/lib.sh"
. "$REPO_ROOT/scripts/scenarios/m3-lib.sh"
. "$REPO_ROOT/scripts/scenarios/m4-lib.sh"
drill_preflight
trap 'drill_finish; exit $?' EXIT
RUN="r16-$(date -u +%H%M%S)-$((RANDOM % 1000))"
MARK="r16marker$RANDOM$RANDOM"
SKU="$(fresh_sku R16 10)"
TOPIC=fulfillment.inventory.v1
fake="$(new_uuid)"; e_schema="$(new_uuid)"; e_poison="$(new_uuid)"; m_bad="$(new_uuid)"
since="$(sql OrderDb "SELECT CONVERT(varchar(23), SYSUTCDATETIME(), 126)")"

o1="$(submit "$ORDER_API" "$RUN-1" "$(order_body "$RUN-c1" "$SKU" 1)")"
INJECTED_AT="$(now_iso)"
s1="$(drill_publish "$TOPIC" "not json $MARK" "$m_bad" text/plain)"
s2="$(drill_publish "$TOPIC" "$(envelope "$e_schema" inventory "$fake" 1 HoldCreated 9 "{\"holdId\":\"$MARK\"}")")"
s3="$(drill_publish "$TOPIC" "$(envelope "$e_poison" inventory "$fake" 1 HoldCreated 1 "{\"holdId\":\"$MARK\",\"drillPoison\":true}")")"
o2="$(submit "$ORDER_API" "$RUN-2" "$(order_body "$RUN-c2" "$SKU" 1)")"
printf '%s\n%s\n' "$o1" "$o2" > "$DRILL_DIR/orders.txt"

dead_reasons() {
  sqln OrderDb "SELECT STRING_AGG(CONCAT(Reason, '@', ISNULL(LOWER(CONVERT(varchar(36), EventId)), MessageId)), ',') WITHIN GROUP (ORDER BY Id)
    FROM orders.EventDeadLetter WHERE RecordedAt >= '$since' AND (EventId IN ('$e_schema', '$e_poison') OR MessageId = '$m_bad')"
}
all_three() { local d; d="$(dead_reasons)"; [[ "$d" == *Malformed* && "$d" == *UnknownSchemaVersion* && "$d" == *DeliveryFailed* ]]; }
wait_until 180 "three dead letters recorded" all_three || true
dead="$(dead_reasons)"
FAULT_OBSERVED="publish statuses $s1/$s2/$s3; dead letters $dead"
aexpect R16-recorded "Malformed, UnknownSchemaVersion, DeliveryFailed (sentinel) recorded" "$FAULT_OBSERVED" "$(all_three; echo $?)"

sleep 5
refusals="$(docker compose --profile replicas logs --since 10m order-api order-api-2 2>/dev/null | grep -c "drill poison event $e_poison refused")"
aexpect R16-bounded "sentinel refused more than once and at most 6 times (5 retries + first), then dead-lettered" "refusals $refusals" \
  "$([[ "$refusals" -ge 2 && "$refusals" -le 6 ]]; echo $?)"

ends=""; bad=0
for o in "$o1" "$o2"; do
  wait_status 180 "$o" 'Completed|Compensated|Rejected|ManualInterventionRequired'; wait_until 60 "events of $o" is_converged "$o" || true
  ends+="$(end_of "$o") $(converged "$o"); "; [[ "$(end_of "$o")" == Completed/Delivered ]] && is_converged "$o" || bad=1
done
RECOVERED_AT="$(now_iso)"
aexpect R16-others "both ordinary orders Completed/Delivered with their events applied" "$ends" "$bad"

inlogs="$(docker compose --profile replicas logs --since 10m order-api order-api-2 2>/dev/null | grep -c "$MARK")"
intable="$(sql OrderDb "SELECT COUNT(*) FROM orders.EventDeadLetter WHERE CONCAT(MessageId, Source, Type, Reason, Topic) LIKE '%$MARK%'")"
aexpect R16-no-payload "marker absent from order API logs and the dead-letter table" "log lines $inlogs; table rows $intable" \
  "$([[ "$inlogs" == 0 && "$intable" == 0 ]]; echo $?)"
