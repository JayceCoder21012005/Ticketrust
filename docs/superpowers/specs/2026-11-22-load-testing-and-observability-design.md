# Spec 17 — Load testing & observability: design

**Spec:** `specs/17-load-testing-and-observability.md`. **Branch:** `feat/17-load-testing-and-observability` (from `main` after 13–16 merged).

## Intent

Prove with numbers that a flash sale survives and never oversells, and make a sale observable while it runs. The numbers end up on a public `/lab` page linked from a CV, so they must be **real and honest**: measured on this machine, with the machine named, and with every threshold shown as met or missed.

## Decided with the owner

- **Run for real, at laptop scale.** All five scenarios get built with parameters whose defaults are the spec's scale (20,000 VUs and so on). I run all five on this laptop (16 GB RAM, 12 threads, Docker Desktop, k6 on the host) at a scale it can carry, for example a 2,000-VU spike, a real 30-minute soak and real chaos. `/lab` records the machine, the scale actually used, the numbers, and which budget passed or failed. Every invariant must show ✓.

## What exists today

- `load-tests/`: `sale.js` (the spec 08 buy flow), the invariant SQL (proved by an integration test that it fires on planted corruption), and a compose overlay for a `ticketrush-loadtest` project.
- OpenTelemetry through ServiceDefaults: ASP.NET Core and HttpClient instrumentation, plus Npgsql and Redis instrumentation via the Aspire client packages. Meters and sources are registered as `TicketRush.*`, and some counters already exist (outbox, payments, cancellations).
- The correlation id is already a span tag.
- **The gaps:**
  - The trace breaks at the outbox, because a message keeps no trace context.
  - Each browser request starts its own trace.
  - No spec 17 metric exists yet.
  - There is no Grafana stack, no seeding endpoint, and no `/lab` page.

## Approaches considered

1. **Connected trace: one trace per purchase (chosen).** The checkout client starts a W3C trace and sends `traceparent` on the hold, order and pay calls. ASP.NET adopts it. The mock gateway stores it with the session and sends it on the webhook. The outbox stores the `traceparent` of the transaction that wrote the message, and the worker starts its consumer span under that parent, so the email span hangs there too. This gives one trace from hold to email, which is what the acceptance criterion asks for.
   - *Rejected: span links only.* Grafana and Tempo show links as separate traces, so the purchase would not read as one story.
   - *Rejected: correlation-id search.* It is a log query, not a trace.
2. **Prod stack: a compose overlay (chosen).** `docker-compose.observability.yml` runs otel-collector, Prometheus, Tempo and Grafana, all provisioned from files (datasources, the "Flash sale" dashboard, alert rules). The ports bind to `127.0.0.1` only; reach them through an SSH tunnel.
   - *Rejected: Grafana Cloud.* It needs an external account, and the spec says VPS.
3. **Fixtures: a test-only API endpoint (chosen).** `POST /api/v1/loadtest/seed` is mapped only when the environment is `LoadTest` **and** a secret header matches. It creates an event with 5,000 seats and 2 GA tiers, plus N users, and returns pre-issued access tokens. With tokens, the spike measures the queue instead of password hashing.
   - *Rejected: SQL-only seeding*, as today. Tokens cannot be minted in SQL.

## Design

### Metrics (meter `TicketRush`, names prefixed `ticketrush.`, as the existing ones are)

| Metric | Where it is recorded |
|---|---|
| `queue.length`, `queue.active_shoppers` (gauge) | The worker's admission loop already reads both from Redis. It caches the last values, and observable gauges report them. |
| `queue.admitted_total` | Admission loop, per admitted batch |
| `holds.created_total{result}` (`ok`, `seats_unavailable`, `limit`, …), `holds.expired_total` | Create-hold handler; hold expiry sweep and the Redis expiry release |
| `orders.paid_total`, `orders.refund_total{reason}` | Pay and fulfil path; refund handler |
| `payment.gateway.duration` (histogram, seconds) | Around the gateway client calls |
| `outbox.lag_seconds`, `outbox.dead_letters` (gauge) | The worker queries every 15 s: the age of the oldest due message, and the count of dead letters |
| `inventory.drift_fixed_total` | The reconcile handler, when it corrects Redis against the database |

- `ServiceDefaults` also registers the bare meter name `TicketRush`, because the wildcard `TicketRush.*` does not match it.
- One `SalesMetrics` class lives in Application abstractions with an Infrastructure implementation, so handlers stay testable.

### Traces

- **Outbox:**
  - add a nullable `trace_parent` column to `outbox_messages` (migration), filled from `Activity.Current` when the message is written
  - `OutboxProcessor` parses it and starts `outbox {type}` as a child, so there is no new root
- **Email:** the email sender gets its own span (`email send {template}`).
- **Mock gateway:** the session stores the request's `traceparent` and sends it on webhook delivery.
- **Web checkout:** one `traceparent` per purchase, generated client-side (random 16- and 8-byte ids) and kept for the checkout flow.
  - It goes on hold, order and pay only, through the existing API client.
  - No OTel SDK in the browser, so no bundle cost.
- **Proof:** an integration test with an in-memory exporter runs a purchase through webhook, outbox and email. It asserts one trace id and the parent chain of the spans hold → order → pay → webhook → outbox → email.

### Grafana (prod overlay)

- **Dashboard** "Flash sale":
  - queue length and active shoppers
  - admitted per minute
  - holds by result
  - paid per minute
  - refunds by reason
  - gateway p95
  - outbox lag and dead letters
  - drift fixes
  - API p99 by route
- **Alerts:**
  - outbox lag > 30 s for 1 min
  - dead letters > 0
  - payment p95 > 3 s for 5 min
  - `increase(inventory_drift_fixed_total[5m]) > 0` while queue length > 0, which stands in for "during a sale"
- **Contact point:** none by default; a commented-out email example is included.

### Load tests (`load-tests/`)

- **Seed endpoint:** `LoadTest` environment only, secret header `X-LoadTest-Key`. It returns `{ eventId, gaTierIds[], seatIds[] (hot first), tokens[] }`. Calling it twice makes a fresh event, so earlier runs do not interfere.

| Scenario | How |
|---|---|
| `01-sale-open-spike` | `ramping-vus` up to `VUS` (default 20,000; laptop run ~2,000) join during the pre-queue, then poll `/queue/me` as `Retry-After` says. Thresholds: queue status p99 < 50 ms, errors other than 429 = 0. |
| `02-buy-flow` | The existing `sale.js`, moved and switched to seeded tokens. Hold p99 < 150 ms, checkout p99 < 300 ms. |
| `03-webhook-storm` | Real orders get paid. Each payment's webhook is sent 3× in parallel, then again after random 0–5 min delays, signed with the load-test webhook secret. Pass: tickets issued once per order (invariant SQL). |
| `04-soak` | Moderate constant load for `DURATION` (30 min). A sampler records api and worker memory and GC (from `/metrics` or `docker stats`) and the count of Redis hold keys every 30 s. Pass: no upward trend (slope test over the last 20 min) and hold keys back to baseline after the TTL. |
| `05-chaos` | Runs `02` while a script kills the worker leader container (and lets compose restart it), restarts Redis, and pauses Postgres for 10 s, at fixed offsets. Pass: k6 errors are only the expected outage ones, the run completes, and the invariants hold. |

- **Invariants:** `check-invariants.sql` = today's checks + `user_over_ticket_limit` (non-voided tickets per user per event > `MaxTicketsPerUser` of the phase). A small script also compares Redis `sold` to DB sold seats and GA counts. The existing planted-corruption test gets a row for the new check.
- **Runner:** `load-tests/run.sh`, which brings up the stack, seeds, runs each scenario, runs the invariant check after each (and again after settle), and writes `results.json`.
  - `make load-test` calls it. A `Makefile` is added. On Windows `run.sh` runs directly from Git Bash, since there is no make.
- **`results.json`:** machine spec, date, commit, and per scenario: scale, RPS, p50/p95/p99, error counts, thresholds met or missed, invariant rows (0 = ✓), plus the "what broke" log. It is committed to `web/src/features/lab/results.json`.

### `/lab` page

- A public, static server component in the web app (no client JS beyond the shell), built from `results.json`.
- Sections: machine, scenario table, invariants, "what broke and how we fixed it".
- It meets the spec 02 budgets and gets a Lighthouse URL in `lighthouserc.json`.
- English and Vietnamese, like the public pages.

## Non-goals

- Running at the spec's 20,000 VUs (it needs a dedicated box; the scripts support it).
- Browser-side OTel.
- Paging integrations for alerts.

## Left to the builder

- Exact panel layout.
- Sampler implementation details.
- How "what broke" entries are written. They come from what actually breaks during the runs, and that log is real, not invented.
