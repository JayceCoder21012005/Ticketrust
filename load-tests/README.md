# Load tests (spec 17)

Five k6 scenarios against the docker-compose stack, an invariant checker that runs after each of them, and a collector that turns a
run into `web/src/features/lab/results.json`, which the public `/lab` page renders.

```bash
make load-test          # or, on Windows without make: bash load-tests/run.sh   (Git Bash)
make load-test-quick    # a few minutes at small scale: proves the pipeline, not the system
```

Needs Docker, [k6](https://grafana.com/docs/k6/latest/set-up/install-k6/), Node and bash, plus a filled-in `.env` (`cp .env.example .env`).

> **Never point this at a real deployment.** Every compose command carries `-p ticketrush-loadtest`, so the stack has its own
> containers and volumes and can never touch a real `ticketrush` project on the same Docker host. Caddy (ports 80/443) is off.
> The scenarios refuse a `BASE_URL` that is not localhost, and the checker refuses a `PSQL_CMD`, unless `LOADTEST_CONFIRM=loadtest`.

## What the runner does

1. Builds and starts the load-test project with **two workers** (so a leader can be killed) and, unless `OBSERVABILITY=0`, the
   observability overlay (Grafana on <http://127.0.0.1:3001>, admin / the password the runner prints).
2. Per scenario: seeds a **fresh event** with `POST /api/v1/loadtest/seed` (5,000 seats + 2 GA tiers, N buyers with pre-issued tokens),
   runs k6, then `check-invariants.sh` right after and again once delayed webhooks, expiries and refunds have settled (`SETTLE_SECONDS`, 180).
3. `collect-results.mjs` writes `results.json`: machine, scale, RPS, p50/p95/p99, errors, every threshold met or missed, every
   invariant verdict, the soak trend, the chaos timeline, and `lab-notes.json` (the hand-written "what broke and how we fixed it").

The seed endpoint exists only when api runs in the `LoadTest` environment (the overlay sets it) and only for a caller with
`LOADTEST_KEY`, which the runner generates fresh for every run. Anywhere else the path answers 404.

## The overlay (`docker-compose.loadtest.yml`)

| Setting | Why |
|---|---|
| `ASPNETCORE_ENVIRONMENT=LoadTest` | maps the seed endpoint |
| mock gateway on, `DelayedWebhookDelay` 90 s, hold TTL 60 s | a delayed payment lands after its hold expired: the late-payment refund path runs |
| `Captcha__Provider=Fake` | the scenarios send Turnstile's test token; nothing goes to Cloudflare |
| `RateLimits__Global__Permit` lifted | all virtual buyers share one source address; real buyers do not. **Per-user** limits (queue, holds, checkout) stay as in production |
| access tokens 60 min (the api's maximum) | each scenario seeds fresh tokens; none runs longer |
| Postgres `max_connections=300` | two .NET pools of up to 100 each overran the default 100 at 100 VUs (spec 08 run) |

## Scenarios (`scenarios/`)

| Scenario | Shape | Pass | Laptop default | Spec scale |
|---|---|---|---|---|
| `01-sale-open-spike` | buyers arrive during the pre-queue, join, poll `/queue/me` as told until admitted | queue status p99 < 50 ms, no error but 429 | `SPIKE_VUS=2000` | 20,000 |
| `02-buy-flow` | admission → hold (30 % on 50 hot seats, or GA) → `/orders` and `/pay` each double-clicked → mock gateway (`MIX`) → poll | hold p99 < 150 ms, checkout p99 < 300 ms | `BUY_BUYERS=2000`, 100 VUs | same |
| `03-webhook-storm` | each payment: the gateway's webhook + a second signed one sent 3× in parallel, then 3× after 0–5 min | tickets issued once per order | `STORM_BUYERS=300` | same |
| `04-soak` | 2 buyers/s for 30 min, half the holds left to expire; `soak-sampler.sh` every 30 s | memory/GC flat, Redis hold keys back to baseline | `SOAK_RATE=2`, `SOAK_DURATION=30m` | same |
| `05-chaos` | the buy flow while `chaos.sh` kills the worker leader (45 s), restarts Redis (100 s), pauses Postgres 10 s (160 s); then a second wave | the second wave has no error, invariants hold | 1,200 + 100 buyers | same |

The budgets are the spec's, for a dedicated machine. On a laptop that runs the stack, the database and k6 together they may be
missed: `/lab` shows them as missed, never relaxed. Only the invariant checker decides correctness.

## The invariant checker

`check-invariants.sh` runs `check-invariants.sql` (zero rows = every invariant holds; the integration test `SaleInvariantTests`
loads the very same file and proves every check fires on planted corruption) and, with `EVENT_ID`, `check-redis-sold.sh`.

| Check | Violated when |
|---|---|
| `seat_sold_twice` | a seat has more than one non-voided ticket |
| `tier_oversold` | `sold_count > capacity` |
| `ga_sold_count_drift` | a GA tier's `sold_count` differs from its non-voided tickets |
| `paid_order_ticket_count`, `paid_order_tier_tickets` | a paid order does not own exactly its tickets (in total, per tier) |
| `ticket_event_mismatch` | a ticket belongs to another event than its order |
| `unpaid_order_has_tickets`, `refund_order_has_tickets` | an unpaid or refunded order still owns a live ticket |
| `user_over_ticket_limit` | a user owns more live tickets of an event than its limit (largest `MaxTicketsPerUser` of its phases) |
| Redis sold (script) | Redis' sold seats differ from the seats with a live ticket, or a GA tier's free + held ≠ capacity − sold |

The Redis check first waits until no outbox message is due: Redis learns a sale from the outbox a moment after the database.

Exit codes: 0 = holds, 1 = violated (rows printed), 2 = could not run.

## Files

| File | What it is |
|---|---|
| `run.sh`, `../Makefile` | the one command |
| `lib/common.js` | fixtures (SharedArray), the buyer's steps, a W3C `traceparent` per purchase, the summary writer |
| `chaos.sh`, `soak-sampler.sh` | faults for 05, samples for 04 |
| `machine.mjs`, `collect-results.mjs`, `lab-notes.json` | the `/lab` data |
| `results/` | raw output of the last run (git-ignored) |

Every purchase sends its own `traceparent`, so with the observability overlay on, Grafana's Tempo explore view shows one trace per
purchase: hold → order → pay → mock gateway delivery → webhook → outbox → email.
