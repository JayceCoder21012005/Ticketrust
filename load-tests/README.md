# Load tests

k6 scenarios and SQL checks for the flash sale (spec 17). Today there is one scenario, the checkout sale of spec 08
(acceptance #8: *after a sale load test, `count(tickets per seat) <= 1` and `sold_count <= capacity` for every tier*).
The other spec 17 scenarios (queue spike, webhook storm, soak, chaos), the seeding endpoint and `make load-test` are not built yet.

| File | What it is |
|---|---|
| `sale.js` | k6 scenario: login, waiting room, hold, `POST /orders`, `POST /orders/{id}/pay`, mock gateway outcome, poll the order |
| `check-invariants.sql` | **The** invariant query. Zero rows = every invariant holds. Also loaded by the integration test `SaleInvariantTests` |
| `check-invariants.sh` | Runs that query with `psql`; exit 0 = holds, 1 = violated (rows printed), 2 = could not run |
| `sql/seed-buyers.sql` | Fixture: N confirmed buyers `loadbuyer00001@ticketrush.test` ... (password `Dev@12345`), cloned from the dev seed customer |
| `sql/event-ids.sql` | Prints the `EVENT_ID` and `GA_TIER_ID` the scenario needs |
| `docker-compose.loadtest.yml` | Overlay for `docker-compose.prod.yml`: dev data, api on `:8080`, mock gateway on, short hold, late webhooks |

## What the scenario does

Each iteration is one distinct buyer (`BUYERS` iterations over `VUS` virtual users):

1. `POST /auth/login`, join the waiting room (`/events/{id}/queue/join`) and poll `/queue/me` as the server says until `Admitted`.
2. `POST /events/{id}/holds` with an `Idempotency-Key`. Either general admission (`GA_RATIO` of buyers, 1 to `GA_MAX_QTY` tickets) or 1 or 2 seats,
   `HOT_RATIO` of them from the first `HOT_SEATS` seats, so several buyers fight for the same seats. A lost race (`hold.seats_unavailable`)
   is retried once on a cold seat. GA demand exceeds the tier's capacity once you shrink it (see below).
3. `POST /orders` and `POST /orders/{id}/pay`, each sent **twice at the same instant with the same key** (a double click). Both answers must be the same order / the same `redirectUrl`.
4. The mock gateway page's choice, through `POST /mock-gateway/sessions/{id}/complete`, by `MIX` weights:
   `succeeded` 55, `failed` 15 (half of them retry with a fresh key and pay), `succeeded_delayed` 12, `succeeded_twice` 10 (the webhook is delivered twice), abandon 8.
5. Polls `GET /orders/{id}` every 1.5 s for up to `POLL_SECONDS` and counts the final status.

With the overlay's `Holds__TtlSeconds=60` and `DelayedWebhookDelay=90 s`, a `succeeded_delayed` payment lands **after** its hold and order ended (spec 08 C3): the
expiry sweep finds it through the gateway status, and if the seat was resold meanwhile the order goes `RefundPending` then `Refunded`.

Thresholds (the run fails on any): `sale_unexpected_responses == 0` (any answer of either double-click request that is not a success, `409 idempotency.in_progress`, or, when neither succeeded, a business refusal such as `order.hold_expired` / `order.expired`; lost seat races are expected), `sale_double_click_same_order == 1`,
`sale_hold_duration p99 < HOLD_P99_MS` (150) and `sale_checkout_duration p99 < CHECKOUT_P99_MS` (300), the spec 17 budgets.

## Run it against the docker-compose stack

> **Never point this at a real deployment.** The overlay seeds dev data and opens the mock gateway. Every compose command here carries `-p ticketrush-loadtest`: the project name prefixes
> the volumes and containers (`ticketrush-loadtest_postgres-data`, ...), so the stack cannot reuse or overwrite those of a real `ticketrush` project on the same Docker host. (`-p` on the command
> line beats the `name:` in the compose files; the overlay also sets `name: ticketrush-loadtest` and switches Caddy off, whose ports 80/443 belong to a real deployment. Both facts
> were checked with `docker compose config`.) `sale.js` refuses a `BASE_URL` that is not localhost, and `check-invariants.sh` refuses a `PSQL_CMD`, unless `LOADTEST_CONFIRM=loadtest`.

Needs Docker, [k6](https://grafana.com/docs/k6/latest/set-up/install-k6/) and bash (Git Bash or WSL on Windows). From the repository root:

```bash
cp .env.example .env            # fill in the secrets once
export COMPOSE="docker compose -p ticketrush-loadtest -f docker-compose.prod.yml -f load-tests/docker-compose.loadtest.yml --env-file .env"

# 1. Stack with the dev event and the mock gateway controls (never on a real deployment).
$COMPOSE up -d --build

# 2. Fixtures: 500 buyers, and the ids of the event and its GA tier.
$COMPOSE exec -T postgres psql -U ticketrush -d ticketrush -v buyers=500 -f - < load-tests/sql/seed-buyers.sql
$COMPOSE exec -T postgres psql -U ticketrush -d ticketrush -f - < load-tests/sql/event-ids.sql

# 3. Optional: make the GA demand exceed capacity, then restart so the inventory is rebuilt from the database.
$COMPOSE exec -T postgres psql -U ticketrush -d ticketrush -c "UPDATE ticket_tiers SET capacity = 60 WHERE id = '<GA_TIER_ID>' AND sold_count = 0"
$COMPOSE restart api worker

# 4. The sale.
mkdir -p load-tests/results
k6 run -e BASE_URL=http://localhost:8080 -e EVENT_ID=<EVENT_ID> -e GA_TIER_ID=<GA_TIER_ID> \
       -e BUYERS=500 -e VUS=100 load-tests/sale.js

# 5. The invariants (exit code 0 = no violation).
ENV_FILE=.env load-tests/check-invariants.sh
```

Without Docker for the database, point the checker at any *load-test* Postgres: `LOADTEST_CONFIRM=loadtest PSQL_CMD="psql postgresql://user:pass@host:5432/ticketrush" load-tests/check-invariants.sh`.
Run the check after any other scenario too: it is read-only and the invariants must hold at every instant, not only when the sale is quiet.

Notes:

- The scenario can only buy what the database has: 600 seats and a 2000-ticket GA tier in the dev seed. A second run on the same data finds the hot seats already sold, which is
  fine for the invariants but lowers the success rate. `$COMPOSE down -v` (with the `-p ticketrush-loadtest` project, never a bare `docker compose down -v`) and start again for a clean sale.
- `BUYERS` must not exceed the `buyers` you seeded. `VUS` is clamped to `BUYERS` (k6's shared-iterations executor needs at least one iteration per VU).
- The default p99 thresholds (hold 150 ms, checkout 300 ms) are the spec 17 budgets for a dedicated machine. On a laptop that runs the whole stack plus k6 they will usually fail: raise them
  (`-e HOLD_P99_MS=1000 -e CHECKOUT_P99_MS=2000`) and read a threshold failure as a tuning signal, not as an invariant violation (only the SQL check decides that).
- Buyers log in with a real password hash check, so login dominates the CPU of a small machine. Keep `VUS` modest (50 to 100) on a laptop.
- The summary is also written to `load-tests/results/sale-summary.json` (git-ignored).

Run notes (first real run, 2026-11-10, Windows laptop with 7.8 GB RAM, Docker Desktop capped at 4 GB, k6 on the host):

- Build the images one group at a time (`$COMPOSE build migrations api worker`, then `$COMPOSE build web`): about 3.5 and 3 minutes cold. Set `REGISTRY=ticketrush-lt` and `TAG=loadtest` in `.env` so the images
  cannot be mistaken for, or overwrite, a real `ghcr.io/...:latest` one. A `PUBLIC_HOST=localhost` is enough here (Caddy is off).
- Memory is small: the whole stack idled at about 450 MB and peaked at about 650 MB (api 280 MB, Postgres 200 MB, worker 130 MB, web 50 MB, Redis 12 MB); k6 adds a few hundred MB on the host. Docker's own VM (4 GB) was never the limit.
- Start the worker too: the waiting room admits from the worker, so without it every buyer waits in the queue forever.
- On Windows use `-e BASE_URL=http://127.0.0.1:8080`, not `localhost`: `localhost` resolves to `::1` first, where a stale `wslrelay` can hold port 8080 and hang the request.
- 500 buyers at `VUS=50` took under two minutes. The invariants hold at every instant, so run the check right after k6 **and** again about three minutes later, when the 90 s delayed webhooks, the expiry sweep and the refunds have settled
  (`Paid` 253, `Expired` 64, `Refunded` 2 in that run; `RefundPending` must be 0 once settled).
- `VUS=100` overran the default Postgres `max_connections` (100) against two .NET connection pools: 6 logins answered 500 (`53300 too many clients already`) and `sale_unexpected_responses` failed, while the invariants still held.
  Keep `VUS` at 50 on the default settings, or raise `max_connections` / lower the pool size first.
- The p99 values the thresholds judge are now printed in the summary (`summaryTrendStats` in `sale.js`). On that laptop: hold p99 244 ms, checkout p99 764 ms at `VUS=50`, so only the laptop thresholds
  (`HOLD_P99_MS=1000 CHECKOUT_P99_MS=2000`) pass.
- One run uses each buyer once: the buyers of an earlier run own tickets and may hit the per-user limit. For a clean run, `down -v` the load-test project and seed again. Starting a single service (`$COMPOSE start web`) re-runs the migration container; that is harmless.

## The invariants (`check-invariants.sql`)

| Check name | Violated when |
|---|---|
| `seat_sold_twice` | A seat of an event has more than one non-voided ticket (what the `ux_tickets_event_seat` partial unique index forbids) |
| `tier_oversold` | `sold_count > capacity` for a tier |
| `ga_sold_count_drift` | A general-admission tier's `sold_count` differs from its non-voided tickets without a seat |
| `paid_order_ticket_count`, `paid_order_tier_tickets` | A `Paid` order does not own exactly its item quantity of non-voided tickets (in total, and per tier) |
| `ticket_event_mismatch` | A ticket belongs to a different event than its order |
| `unpaid_order_has_tickets` | A `Pending`, `AwaitingPayment`, `PaymentFailed`, `Cancelled` or `Expired` order owns a non-voided ticket |
| `refund_order_has_tickets` | A `RefundPending` or `Refunded` order owns a non-voided ticket (the seats must have been given back) |

Spec 17 also lists *users holding more than `MaxTicketsPerUser`* and *Redis `sold` = DB sold seats*. Those belong to the full spec 17 checker and are not part of this query.
The integration test `The_invariant_query_reports_every_kind_of_planted_corruption` proves every check above fires, so a green run is not vacuous.
