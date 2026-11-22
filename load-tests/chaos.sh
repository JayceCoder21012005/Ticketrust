#!/usr/bin/env bash
# Spec 17 scenario 05's faults, at fixed offsets from the start (seconds, overridable):
#   KILL_AT (45)        kill the worker that leads the waiting room (the last one to log "Became leader for queue-admitter");
#                       compose's restart policy brings it back, and the other worker takes the lead meanwhile
#   REDIS_AT (100)      restart Redis (AOF on: it comes back with its data; the reconciler repairs what the last second lost)
#   PG_PAUSE_AT (160)   pause Postgres for PG_PAUSE_SECONDS (10)
# Each fault and its time is appended to CHAOS_LOG (one JSON object per line) for the results page.
set -uo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo="$(cd "$here/.." && pwd)"
compose=(docker compose -p ticketrush-loadtest -f "$repo/docker-compose.prod.yml" -f "$here/docker-compose.loadtest.yml" ${ENV_FILE:+--env-file "$ENV_FILE"})
log="${CHAOS_LOG:-$here/results/chaos.jsonl}"
: >"$log"
start=$(date +%s)

at() { local wait=$(( $1 - ($(date +%s) - start) )); (( wait > 0 )) && sleep "$wait"; }
note() { printf '{"at":%d,"fault":"%s","detail":"%s"}\n' "$(( $(date +%s) - start ))" "$1" "$2" >>"$log"; echo "chaos: $1 ($2)"; }

at "${KILL_AT:-45}"
leader=""
for id in $("${compose[@]}" ps -q worker); do
  if docker logs --since 30m "$id" 2>&1 | grep -q "Became leader for queue-admitter"; then
    last=$(docker logs --since 30m --timestamps "$id" 2>&1 | grep "Became leader for queue-admitter" | tail -n1 | cut -d' ' -f1)
    if [[ -z "$leader" || "$last" > "${leader_at:-}" ]]; then leader="$id"; leader_at="$last"; fi
  fi
done
leader="${leader:-$("${compose[@]}" ps -q worker | head -n1)}"
docker kill "$leader" >/dev/null && note "worker_leader_killed" "${leader:0:12}"
# `docker kill` is not a failure exit to the restart policy's eyes in every Docker version: start it explicitly if it stays down.
sleep 5
docker start "$leader" >/dev/null 2>&1 || true

at "${REDIS_AT:-100}"
"${compose[@]}" restart redis >/dev/null && note "redis_restarted" "compose restart redis"

at "${PG_PAUSE_AT:-160}"
pg=$("${compose[@]}" ps -q postgres)
docker pause "$pg" >/dev/null && note "postgres_paused" "${PG_PAUSE_SECONDS:-10}s"
sleep "${PG_PAUSE_SECONDS:-10}"
docker unpause "$pg" >/dev/null && note "postgres_resumed" "after ${PG_PAUSE_SECONDS:-10}s"
