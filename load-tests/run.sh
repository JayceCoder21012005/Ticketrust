#!/usr/bin/env bash
# Spec 17: every scenario with one command (`make load-test`, or this script from Git Bash / WSL / Linux / macOS):
#   1. builds and starts the load-test compose project (never the real one: `-p ticketrush-loadtest`), two workers
#   2. per scenario: seeds a fresh event and buyers (POST /api/v1/loadtest/seed), runs k6, then the invariant checker right after
#      and again once delayed webhooks, expiries and refunds have settled
#   3. writes web/src/features/lab/results.json (collect-results.mjs) for the /lab page
#
# Environment (all optional): SCENARIOS ("01 02 03 04 05"), ENV_FILE (.env), SKIP_BUILD=1, KEEP_STACK=1, SETTLE_SECONDS (180),
# and each scenario's knobs (see the scenario files), e.g. SPIKE_VUS=2000 BUY_BUYERS=2000 SOAK_DURATION=30m.
set -uo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo="$(cd "$here/.." && pwd)"
cd "$repo"
export ENV_FILE="${ENV_FILE:-$repo/.env}"
results="$here/results"
mkdir -p "$results"
compose=(docker compose -p ticketrush-loadtest -f docker-compose.prod.yml -f load-tests/docker-compose.loadtest.yml --env-file "$ENV_FILE")
api="http://127.0.0.1:8080"
settle="${SETTLE_SECONDS:-180}"
scenarios="${SCENARIOS:-01 02 03 04 05}"

for tool in docker k6 node curl; do
  command -v "$tool" >/dev/null || { echo "run.sh: $tool is required" >&2; exit 2; }
done
[[ -f "$ENV_FILE" ]] || { echo "run.sh: $ENV_FILE not found (cp .env.example .env and fill it in)" >&2; exit 2; }
env_value() { grep -E "^$1=" "$ENV_FILE" | head -n1 | cut -d= -f2- | tr -d '\r'; }

# A fresh key per run: the seed endpoint only exists in this stack, and only for whoever started it.
LOADTEST_KEY="$(node -e "process.stdout.write(require('crypto').randomBytes(24).toString('hex'))")"
export LOADTEST_KEY

echo "==> stack (project ticketrush-loadtest)"
if [[ -z "${SKIP_BUILD:-}" ]]; then
  "${compose[@]}" build migrations api worker || exit 2
fi
"${compose[@]}" up -d --scale worker=2 postgres redis migrations api worker || exit 2
for ((i = 0; i < 120; i++)); do
  curl -sf "$api/health" >/dev/null && break
  sleep 2
done
curl -sf "$api/health" >/dev/null || { echo "run.sh: api did not come up" >&2; exit 2; }

node "$here/machine.mjs" >"$results/machine.json"

# seed <name> <json body>: writes results/fixtures/<name>/{seed,buyers}.json and prints the event id.
seed() {
  local dir="$results/fixtures/$1"
  mkdir -p "$dir"
  curl -sf -X POST "$api/api/v1/loadtest/seed" -H "X-LoadTest-Key: $LOADTEST_KEY" -H 'Content-Type: application/json' -d "$2" -o "$dir/raw.json" \
    || { echo "run.sh: seeding $1 failed" >&2; return 1; }
  node -e "
    const fs = require('fs'); const d = JSON.parse(fs.readFileSync('$dir/raw.json', 'utf8'));
    fs.writeFileSync('$dir/buyers.json', JSON.stringify(d.buyers));
    delete d.buyers; fs.writeFileSync('$dir/seed.json', JSON.stringify(d));
    fs.unlinkSync('$dir/raw.json'); process.stdout.write(d.eventId);"
}

# check <name> <event id> <phase>: runs the invariant checker and stores its verdict for collect-results.mjs.
check() {
  local output code
  output="$(EVENT_ID="$2" "$here/check-invariants.sh" 2>&1)"
  code=$?
  echo "$output"
  node -e "
    const fs = require('fs'); const f = '$results/$1.invariants.json';
    const all = fs.existsSync(f) ? JSON.parse(fs.readFileSync(f, 'utf8')) : [];
    all.push({ phase: '$3', exitCode: $code, output: process.argv[1] });
    fs.writeFileSync(f, JSON.stringify(all, null, 2));" "$output"
}

k6run() { # k6run <name> <script> <fixtures name> [k6 -e args...]
  local name="$1" script="$2" fixtures="$3"
  shift 3
  k6 run --quiet -e FIXTURES_DIR="$results/fixtures/$fixtures" -e RESULTS_DIR="$results" -e BASE_URL="$api" "$@" "$here/scenarios/$script" \
    2>&1 | tee "$results/$name.log"
  echo "${PIPESTATUS[0]}" >"$results/$name.exit"
}

rm -f "$results"/*.invariants.json

for s in $scenarios; do
  case "$s" in
    01)
      vus="${SPIKE_VUS:-2000}"
      event=$(seed spike "{\"users\":$vus,\"seats\":5000,\"gaTiers\":2,\"gaCapacity\":2000,\"opensInSeconds\":${SPIKE_OPENS_IN:-90},\"queueEnabled\":true}") || exit 2
      echo "==> 01-sale-open-spike ($vus VUs, event $event)"
      k6run 01-sale-open-spike 01-sale-open-spike.js spike -e VUS="$vus" -e ARRIVAL_SECONDS="${SPIKE_ARRIVAL:-60}"
      check 01-sale-open-spike "$event" "after"
      ;;
    02)
      buyers="${BUY_BUYERS:-2000}"
      event=$(seed buy "{\"users\":$buyers,\"seats\":5000,\"gaTiers\":2,\"gaCapacity\":2000,\"hotSeats\":50}") || exit 2
      echo "==> 02-buy-flow ($buyers buyers, event $event)"
      k6run 02-buy-flow 02-buy-flow.js buy -e BUYERS="$buyers" -e VUS="${BUY_VUS:-100}"
      check 02-buy-flow "$event" "after"
      sleep "$settle"
      check 02-buy-flow "$event" "settled"
      ;;
    03)
      buyers="${STORM_BUYERS:-300}"
      event=$(seed storm "{\"users\":$buyers,\"seats\":5000,\"gaTiers\":2,\"gaCapacity\":2000,\"hotSeats\":20}") || exit 2
      echo "==> 03-webhook-storm ($buyers buyers, event $event)"
      k6run 03-webhook-storm 03-webhook-storm.js storm -e BUYERS="$buyers" -e VUS="${STORM_VUS:-100}" \
        -e WEBHOOK_SECRET="$(env_value PAYMENT_WEBHOOK_SECRET)" -e MAX_DELAY_SECONDS="${STORM_MAX_DELAY:-300}"
      check 03-webhook-storm "$event" "after"
      sleep "$settle"
      check 03-webhook-storm "$event" "settled"
      ;;
    04)
      duration="${SOAK_DURATION:-30m}"
      rate="${SOAK_RATE:-2}"
      event=$(seed soak "{\"users\":${SOAK_USERS:-4000},\"seats\":5000,\"gaTiers\":2,\"gaCapacity\":3000,\"hotSeats\":0}") || exit 2
      echo "==> 04-soak ($rate buyers/s for $duration, event $event)"
      STOP_FILE="$results/soak.stop" "$here/soak-sampler.sh" &
      sampler=$!
      k6run 04-soak 04-soak.js soak -e DURATION="$duration" -e RATE="$rate"
      # Holds left behind expire after the overlay's 60 s TTL: keep sampling until they are gone (or 3 more minutes).
      sleep "$settle"
      touch "$results/soak.stop"
      wait "$sampler"
      check 04-soak "$event" "settled"
      ;;
    05)
      event=$(seed chaos "{\"users\":${CHAOS_USERS:-1300},\"seats\":5000,\"gaTiers\":2,\"gaCapacity\":2000,\"hotSeats\":30}") || exit 2
      echo "==> 05-chaos (event $event)"
      "$here/chaos.sh" &
      chaos=$!
      k6run 05-chaos 05-chaos.js chaos -e BUYERS="${CHAOS_BUYERS:-1200}" -e AFTER_BUYERS="${CHAOS_AFTER:-100}" -e CHAOS_SECONDS=240
      wait "$chaos"
      check 05-chaos "$event" "after"
      sleep "$settle"
      check 05-chaos "$event" "settled"
      ;;
    *) echo "run.sh: unknown scenario $s" >&2; exit 2 ;;
  esac
done

echo "==> results"
node "$here/collect-results.mjs" "$results" "$repo/web/src/features/lab/results.json"

if [[ -z "${KEEP_STACK:-}" ]]; then
  "${compose[@]}" stop >/dev/null
fi
