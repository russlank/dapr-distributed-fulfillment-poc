#!/usr/bin/env bash
# A guided tour of the PoC on the release stack (two order API replicas), with a check after each step.
#
#   W1 identity      every service reports the same commit and the release flavor
#   W2 order         an ordinary order runs to Completed: validation, checks, reservation, payment,
#                    packing, dispatch, delivery by the carrier simulator
#   W3 idempotency   the same request again (same Idempotency-Key) returns the same order, nothing new
#   W4 review        an order from 1,000.00 waits for an operator; the approval goes through the other
#                    order API replica and the order completes
#   W5 durability    while a review order waits, every workflow host is killed; the approval is accepted
#                    anyway (SQL first), and once the hosts are back the order completes exactly once
#   W6 cancellation  an order waiting for review is cancelled and ends Cancelled, with nothing reserved or charged
#
# Usage: scripts/walkthrough.sh [--fresh]
#   --fresh  removes the stack and its data first (scripts/reset.sh --yes), creates .env if missing,
#            and starts the release stack with replicas (scripts/stack-up.sh --replicas)
# Without --fresh it runs on an already running release stack with replicas.
# Evidence: artifacts/walkthrough/<timestamp>/results.jsonl. Exit 0 when every step passed.
set -uo pipefail
FRESH=0
[[ "${1:-}" == --fresh ]] && FRESH=1
cd "$(dirname "$0")/.."
if (( FRESH )); then
  [[ -f .env ]] || bash scripts/init-env.sh || exit 2
fi
. scripts/lib.sh
. scripts/scenarios/m3-lib.sh

explain() { printf '\n== %s\n%s\n' "$1" "$2" >&2; }
if (( FRESH )); then
  explain "Fresh start" "Removing the stack and its data, then building and starting the release stack with replicas (a few minutes)."
  bash scripts/reset.sh --yes >&2 || die "reset failed"
  bash scripts/stack-up.sh --replicas >&2 || die "stack-up failed"
fi
ready order-api && ready order-api-2 || { log "REFUSED: the release stack with replicas is not running (bash scripts/walkthrough.sh --fresh)"; exit 2; }

RUN_ID="walkthrough-$(date -u +%Y%m%dT%H%M%SZ)"
OUT_DIR="$REPO_ROOT/artifacts/walkthrough/$RUN_ID"
mkdir -p "$OUT_DIR"
RESULTS_FILE="$OUT_DIR/results.jsonl"
SCENARIO_ID=walkthrough
failures=0
TAG="wt-$(date -u +%H%M%S)-$RANDOM"
review_of() { sql OrderDb "SELECT ReviewId FROM orders.ReviewTask WHERE OrderId = '$1' AND Status = 'Pending'"; }
has_review() { [[ -n "$(review_of "$1")" ]]; }
order_json() { curl -s -m 10 "$ORDER_API/orders/$1" -H "X-Api-Key: $CUSTOMER_API_KEY"; }
show_history() { log "history of $1: $(history_of "$1")"; }

# --- W1 ------------------------------------------------------------------------------------------
explain "W1 Identity" "Every role runs as an app plus its own Dapr sidecar and reports its build. All must come from the same commit."
bad=()
for svc in "${DAPR_APPS[@]}" order-api-2 "${SIMULATORS[@]}"; do
  id="$(http_body "$svc" /system/component-identity)"
  echo "$svc $id" >> "$OUT_DIR/identity.txt"
  [[ "$(json_get "$id" commit)" == "$GIT_COMMIT"* && "$(json_get "$id" buildFlavor)" == release ]] || bad+=("$svc")
done
expect W1-identity "every service reports commit $GIT_COMMIT, release flavor" \
  "$(( ${#DAPR_APPS[@]} + 1 + ${#SIMULATORS[@]} - ${#bad[@]} )) services match${bad:+; mismatched: ${bad[*]}}" "$(( ${#bad[@]} == 0 ? 0 : 1 ))"

# --- W2 ------------------------------------------------------------------------------------------
explain "W2 An ordinary order" "Submitted to the order API, accepted in one SQL commit, started by the start dispatcher, and carried by the workflow through every service. The carrier simulator reports PickedUp, InTransit, Delivered 10 s apart."
key2="$TAG-order"
body2="$(order_body "$TAG-c2" SKU-1 1)"
o2="$(submit "$ORDER_API" "$key2" "$body2")"
log "order $o2 accepted"
wait_status 180 "$o2" 'Completed|Compensated|Rejected|ManualInterventionRequired' || true
show_history "$o2"
expect W2-order "Completed after Submitted, Validating, Reserving, PaymentAuthorized, Packing, Dispatched, AwaitingDelivery" \
  "$(end_of "$o2"); history $(history_of "$o2"); OrderCompleted events $(completed_events "$o2")" \
  "$([[ "$(order_status "$o2")" == Completed && "$(completed_events "$o2")" == 1 ]]; echo $?)"

# --- W3 ------------------------------------------------------------------------------------------
explain "W3 Idempotency" "The same request with the same Idempotency-Key, this time through the second replica: the stored answer is replayed, no second order exists."
again="$(curl -s -m 30 -X POST "$ORDER_API_2/orders" -H "X-Api-Key: $CUSTOMER_API_KEY" -H "Idempotency-Key: $key2" -H 'content-type: application/json' -d "$body2")"
echo "$again" > "$OUT_DIR/replay.json"
orders_for_customer="$(sql OrderDb "SELECT COUNT(*) FROM orders.[Order] WHERE CustomerId = '$TAG-c2'")"
expect W3-idempotency "same order id, replayed, one order for the customer" \
  "order $(json_get "$again" orderId), replayed $(json_get "$again" replayed), orders $orders_for_customer" \
  "$([[ "$(json_get "$again" orderId)" == "$o2" && "$(json_get "$again" replayed)" == True && "$orders_for_customer" == 1 ]]; echo $?)"

# --- W4 ------------------------------------------------------------------------------------------
explain "W4 Human review" "An order from 1,000.00 needs an operator's approval. The review task and its deadline are in SQL; the workflow waits durably. The approval is sent to the other order API replica."
o4="$(submit "$ORDER_API" "$TAG-review" "$(review_order_body "$TAG-c4")")"
wait_until 120 "review of $o4" has_review "$o4" || true
log "order $o4 is $(order_status "$o4"), review $(review_of "$o4")"
d4="$(decide "$ORDER_API_2" "$(review_of "$o4")" approve)"
log "approval through order-api-2: ${d4%% *}"
wait_status 180 "$o4" 'Completed|Compensated|Rejected|ManualInterventionRequired' || true
show_history "$o4"
expect W4-review "AwaitingApproval, approval 200 through the other replica, Completed" \
  "decision ${d4%% *}; $(end_of "$o4"); history $(history_of "$o4")" \
  "$([[ "${d4%% *}" == 200 && "$(order_status "$o4")" == Completed && "$(history_of "$o4")" == *AwaitingApproval* ]]; echo $?)"

# --- W5 ------------------------------------------------------------------------------------------
explain "W5 Durability" "While a review order waits, both workflow hosts are killed (SIGKILL, app and sidecar). The approval is still accepted: decision, audit, and a pending signal commit in SQL. When the hosts return, the workflow resumes from its history, receives the signal, and the order completes once."
o5="$(submit "$ORDER_API" "$TAG-durable" "$(review_order_body "$TAG-c5")")"
wait_until 120 "review of $o5" has_review "$o5" || true
kill_pair workflow-host; kill_pair workflow-host-2
d5="$(decide "$ORDER_API" "$(review_of "$o5")" approve)"
pending="$(sql OrderDb "SELECT COUNT(*) FROM orders.PendingSignal WHERE OrderId = '$o5' AND State <> 'Forwarded'")"
log "approval with every workflow host down: ${d5%% *}; undelivered signals: $pending"
start_pair workflow-host; start_pair workflow-host-2
wait_status 240 "$o5" 'Completed|Compensated|Rejected|ManualInterventionRequired' || true
show_history "$o5"
expect W5-durability "approval 200 while the hosts are down; Completed once after they return" \
  "decision ${d5%% *}; undelivered signals while down $pending; $(end_of "$o5"); OrderCompleted events $(completed_events "$o5")" \
  "$([[ "${d5%% *}" == 200 && "$(order_status "$o5")" == Completed && "$(completed_events "$o5")" == 1 ]]; echo $?)"

# --- W6 ------------------------------------------------------------------------------------------
explain "W6 Cancellation" "A customer cancels an order that waits for review. Cancellation is possible until dispatch starts; the workflow ends the order Cancelled. It is still in review, so nothing was reserved or charged."
o6="$(submit "$ORDER_API" "$TAG-cancel" "$(review_order_body "$TAG-c6")")"
wait_until 120 "review of $o6" has_review "$o6" || true
c6="$(cancel_order "$ORDER_API" "$o6")"
log "cancellation: ${c6%% *}"
wait_status 180 "$o6" 'Cancelled|Completed|Compensated|Rejected' || true
show_history "$o6"
expect W6-cancellation "cancellation 202; Cancelled; no hold" \
  "cancel ${c6%% *}; $(end_of "$o6"); holds '$(holds_of "$o6")'" \
  "$([[ "${c6%% *}" == 202 && "$(order_status "$o6")" == Cancelled && -z "$(holds_of "$o6")" ]]; echo $?)"

# --- where to look ---------------------------------------------------------------------------------
explain "Where to look next" "Traces of these orders: http://127.0.0.1:16686 (Jaeger). Dashboards: http://127.0.0.1:3000 (Grafana, admin / GRAFANA_ADMIN_PASSWORD from .env). The timeline of an order: curl -s $ORDER_API/system/orders/$o4/timeline -H \"X-Api-Key: \$ADMIN_API_KEY\". Orders: $o2 $o4 $o5 $o6."
scenario_finish
