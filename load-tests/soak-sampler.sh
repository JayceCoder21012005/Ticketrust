#!/usr/bin/env bash
# Spec 17 scenario 04's sampler: every INTERVAL seconds (30) until the file STOP_FILE appears, one JSON line with the elapsed time,
# the api and worker memory (docker stats, MiB), the api's .NET GC heap and collection count (from Prometheus, when the
# observability overlay runs; null otherwise), the Redis hold hashes and the Redis key count. collect-results.mjs fits a line
# through the last 20 minutes of samples to judge "no growth".
set -uo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo="$(cd "$here/.." && pwd)"
compose=(docker compose -p ticketrush-loadtest -f "$repo/docker-compose.prod.yml" -f "$here/docker-compose.loadtest.yml" ${ENV_FILE:+--env-file "$ENV_FILE"})
out="${SOAK_LOG:-$here/results/soak-samples.jsonl}"
stop="${STOP_FILE:-$here/results/soak.stop}"
interval="${INTERVAL:-30}"
prom="${PROMETHEUS_URL:-http://127.0.0.1:9090}"
password=""
if [[ -n "${ENV_FILE:-}" && -f "$ENV_FILE" ]]; then
  password="$(grep -E '^REDIS_PASSWORD=' "$ENV_FILE" | head -n1 | cut -d= -f2- | tr -d '\r')"
fi
: >"$out"
rm -f "$stop"
start=$(date +%s)

mib() { # "123.4MiB / 7.6GiB" -> 123.4
  local v="${1%% *}"
  case "$v" in
    *GiB) awk -v x="${v%GiB}" 'BEGIN { printf "%.1f", x * 1024 }' ;;
    *MiB) echo "${v%MiB}" ;;
    *KiB) awk -v x="${v%KiB}" 'BEGIN { printf "%.1f", x / 1024 }' ;;
    *) echo 0 ;;
  esac
}

while [[ ! -f "$stop" ]]; do
  api=$("${compose[@]}" ps -q api | head -n1)
  api_mem=$(mib "$(docker stats --no-stream --format '{{.MemUsage}}' "$api" 2>/dev/null)")
  # Every worker, summed: with two of them, work (and memory) moves with the leader locks, and one alone would look like growth.
  worker_mem=0
  for worker in $("${compose[@]}" ps -q worker); do
    worker_mem=$(awk -v a="$worker_mem" -v b="$(mib "$(docker stats --no-stream --format '{{.MemUsage}}' "$worker" 2>/dev/null)")" 'BEGIN { printf "%.1f", a + b }')
  done
  holds=$("${compose[@]}" exec -T redis redis-cli --no-auth-warning ${password:+-a "$password"} --scan --pattern 'tr:*:inv:*:hold:*' 2>/dev/null | wc -l | tr -d ' ')
  keys=$("${compose[@]}" exec -T redis redis-cli --no-auth-warning ${password:+-a "$password"} DBSIZE 2>/dev/null | tr -d '\r ')
  heap=$(curl -s --max-time 3 "$prom/api/v1/query" --data-urlencode 'query=sum(dotnet_gc_last_collection_heap_size_bytes{service_name="api"})' 2>/dev/null \
    | sed -n 's/.*"value":\[[^,]*,"\([0-9.e+]*\)"\].*/\1/p')
  gcs=$(curl -s --max-time 3 "$prom/api/v1/query" --data-urlencode 'query=sum(dotnet_gc_collections_total{service_name="api"})' 2>/dev/null \
    | sed -n 's/.*"value":\[[^,]*,"\([0-9.e+]*\)"\].*/\1/p')
  worker_heap=$(curl -s --max-time 3 "$prom/api/v1/query" --data-urlencode 'query=sum(dotnet_gc_last_collection_heap_size_bytes{service_name="worker"})' 2>/dev/null \
    | sed -n 's/.*"value":\[[^,]*,"\([0-9.e+]*\)"\].*/\1/p')
  printf '{"t":%d,"apiMiB":%s,"workerMiB":%s,"holdKeys":%s,"redisKeys":%s,"gcHeapBytes":%s,"workerGcHeapBytes":%s,"gcCollections":%s}\n' \
    "$(( $(date +%s) - start ))" "${api_mem:-0}" "${worker_mem:-0}" "${holds:-0}" "${keys:-0}" "${heap:-null}" "${worker_heap:-null}" "${gcs:-null}" >>"$out"
  for ((i = 0; i < interval; i++)); do
    [[ -f "$stop" ]] && break
    sleep 1
  done
done
