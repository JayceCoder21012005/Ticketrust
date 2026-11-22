#!/usr/bin/env bash
# Spec 17 invariant "Redis sold = DB sold": for one event, the seats Redis marks sold are exactly the seats with a live ticket,
# and for every GA tier Redis' free + held equals capacity - sold_count. Exit 0 = equal, 1 = differ (printed), 2 = could not run.
#
#   EVENT_ID     the event to compare (the runner passes the seeded one)
#   REDIS_ENV    the key segment of the stack (Redis__Environment; `prod` in docker-compose.prod.yml)
#   ENV_FILE     the compose --env-file (REDIS_PASSWORD is read from it)
#
# Redis learns a sale from the outbox (OrderPaidInventoryCommitHandler), a moment after the database committed it. The check
# waits (up to SETTLE_SECONDS, default 90) until no outbox message is due, so it compares a settled system, not one in flight.
set -uo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo="$(cd "$here/.." && pwd)"
: "${EVENT_ID:?set EVENT_ID}"
redis_env="${REDIS_ENV:-prod}"
settle="${SETTLE_SECONDS:-90}"

compose=(docker compose -p ticketrush-loadtest -f "$repo/docker-compose.prod.yml" -f "$here/docker-compose.loadtest.yml" ${ENV_FILE:+--env-file "$ENV_FILE"})
password=""
if [[ -n "${ENV_FILE:-}" && -f "$ENV_FILE" ]]; then
  password="$(grep -E '^REDIS_PASSWORD=' "$ENV_FILE" | head -n1 | cut -d= -f2- | tr -d '\r')"
fi

sql() { "${compose[@]}" exec -T postgres psql -U ticketrush -d ticketrush -X -A -t -v ON_ERROR_STOP=1 -c "$1"; }
rcli() { "${compose[@]}" exec -T redis redis-cli --no-auth-warning ${password:+-a "$password"} "$@"; }

if ! [[ "$EVENT_ID" =~ ^[0-9a-fA-F-]{36}$ ]]; then
  echo "redis sold: EVENT_ID is not a uuid" >&2
  exit 2
fi

for ((i = 0; i < settle; i++)); do
  due="$(sql "SELECT count(*) FROM outbox_messages WHERE processed_at IS NULL AND next_attempt_at <= now()")" || exit 2
  [[ "$due" == "0" ]] && break
  sleep 1
done

evt="${EVENT_ID//-/}"
prefix="tr:${redis_env}:inv:{evt:${evt}}"
failed=0

db_seats="$(sql "SELECT seat_index FROM tickets WHERE event_id = '$EVENT_ID' AND status <> 'Voided' AND seat_index IS NOT NULL ORDER BY 1")" || exit 2
redis_seats="$(rcli SMEMBERS "$prefix:sold" | tr -d '\r' | sed '/^$/d' | sort -n)" || exit 2
if [[ "$db_seats" != "$redis_seats" ]]; then
  echo "redis sold: seats differ (db $(wc -w <<<"$db_seats"), redis $(wc -w <<<"$redis_seats"))" >&2
  diff <(echo "$db_seats") <(echo "$redis_seats") | head -n 20 >&2
  failed=1
fi

while IFS='|' read -r tier capacity sold; do
  [[ -z "$tier" ]] && continue
  n="${tier//-/}"
  free="$(rcli GET "$prefix:ga:$n" | tr -d '\r')"
  held="$(rcli HGET "$prefix:gaheld" "$n" | tr -d '\r')"
  free="${free:-0}"
  held="${held:-0}"
  if (( free + held != capacity - sold )); then
    echo "redis sold: GA tier $tier free $free + held $held != capacity $capacity - sold $sold" >&2
    failed=1
  fi
done < <(sql "SELECT id, capacity, sold_count FROM ticket_tiers WHERE event_id = '$EVENT_ID' AND kind = 'GeneralAdmission'")

if (( failed )); then
  exit 1
fi

echo "redis sold: OK (seats and GA counts match the database)"
