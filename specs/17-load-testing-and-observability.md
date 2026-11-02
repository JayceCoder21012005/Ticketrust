# 17 — Load Testing & Observability

**Goal:** prove with numbers that the system survives a flash sale and never oversells, and make every sale observable while it happens. The results go in the README and on a public `/lab` page.

**Depends on:** 08 (rerun after every V2 spec)

## Observability

- OpenTelemetry via ServiceDefaults (01): traces across web → api → postgres/redis/worker, with the correlation id as a span attribute.
- Custom metrics (`System.Diagnostics.Metrics`, meter `TicketRush`):

| Metric | Type |
|---|---|
| `queue.length`, `queue.active_shoppers` | gauge |
| `queue.admitted_total` | counter |
| `holds.created_total{result}`, `holds.expired_total` | counter |
| `orders.paid_total`, `orders.refund_total{reason}` | counter |
| `payment.gateway.duration` | histogram |
| `outbox.lag_seconds`, `outbox.dead_letters` | gauge |
| `inventory.drift_fixed_total` | counter |

- Dev: Aspire dashboard. Prod (VPS): OTLP → Grafana + Prometheus + Tempo via docker-compose. One Grafana dashboard "Flash sale" with the metrics above.
- Alerts (Grafana): outbox lag > 30 s, dead letters > 0, payment p95 > 3 s, any `inventory.drift_fixed_total` increase during a sale.

## k6 scenarios (`load-tests/`)

| Scenario | Shape | Pass criteria |
|---|---|---|
| `01-sale-open-spike` | 20,000 virtual users join during pre-queue, then poll as the server says | Queue status p99 < 50 ms, 0 errors other than 429 |
| `02-buy-flow` | Admitted users: availability → hold (random seats, 30 % target the same hot seats) → order → mock pay | Hold p99 < 150 ms, checkout p99 < 300 ms |
| `03-webhook-storm` | Each payment webhook sent 3× in parallel + random 0–5 min delays | Tickets issued once per order |
| `04-soak` | Moderate load for 30 min | Memory and GC stable, no growth in Redis keys for expired holds |
| `05-chaos` | During `02`: kill the worker leader, restart Redis, pause Postgres for 10 s | System recovers. Invariants hold |

Fixtures: a seeding command creates an event with 5,000 seats + 2 GA tiers and N test users with pre-issued tokens (test-only endpoint, enabled only in the `LoadTest` environment).

## Invariant checker (runs after every scenario)

`load-tests/check-invariants.sql`, which must return zero rows:
- seats with more than one non-voided ticket
- tiers where `sold_count > capacity`
- paid orders without tickets, or tickets without a paid order
- users holding more tickets than `MaxTicketsPerUser`
- Redis `sold` ≠ DB sold seats (checked by a small script)

## `/lab` page (public)

Static page generated from the latest `results.json`: machine spec, scenario table (RPS, p50/p95/p99, errors), the invariant check results (all ✓), and a short "what broke and how we fixed it" log. This is the page to link on a CV.

## Acceptance criteria

- [ ] All five scenarios run with one command (`make load-test`) against the docker-compose stack.
- [ ] Invariant checker returns zero rows after every scenario, including chaos.
- [ ] A trace for one purchase shows hold → order → payment → webhook → outbox → email as connected spans.
- [ ] `/lab` renders from `results.json` and meets the 02 performance budgets.
