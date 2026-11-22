# Load Testing & Observability Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Spec 17. Add the sale metrics and one connected trace per purchase, a Grafana stack for prod, five k6 scenarios with an invariant checker and a one-command runner, and a public `/lab` page fed by real results.

**Architecture:**
- `ISalesMetrics` (Application) is implemented on meter `TicketRush` (Infrastructure).
- The outbox stores the W3C `traceparent`, and the worker continues it.
- The mock gateway stores the `traceparent` and sends it back on the webhook.
- The web checkout sends one `traceparent` per purchase.
- A test-only seed endpoint exists only in the `LoadTest` environment.
- The k6 scripts, the SQL and bash checks, and `run.sh` produce `results.json`, which the static `/lab` page renders.

**Tech Stack:** .NET 10, OpenTelemetry, EF Core/Npgsql, Redis, k6 2.2, Grafana/Prometheus/Tempo/otel-collector, Next 16.

**Spec:** `specs/17-load-testing-and-observability.md`; design `docs/superpowers/specs/2026-11-22-load-testing-and-observability-design.md`.

## Global Constraints

- Meter name `TicketRush`; instrument names prefixed `ticketrush.` (e.g. `ticketrush.queue.length`).
- Seed endpoint is mapped only when `IHostEnvironment.EnvironmentName == "LoadTest"`, and it also requires the `X-LoadTest-Key` header to equal `LoadTest:Key` (min 32 chars). Any other environment answers 404.
- k6 scripts refuse a non-localhost `BASE_URL` unless `LOADTEST_CONFIRM=loadtest` (same rule as `sale.js`).
- Observability ports bind to `127.0.0.1` only.
- `/lab` meets the spec 02 budgets and is listed in `lighthouserc.json`.
- Numbers on `/lab` come only from real runs on this machine. The "what broke" log comes only from what really broke.

## Review Focus

- A message written outside any request (worker sweeps) has no `traceparent`. The outbox must still process it as a new root.
- A malformed `trace_parent` must not stop the outbox. Ignore it.
- Seed endpoint in Development or Production → 404 even with the key.
- Gauges read cached values. A Redis outage must not throw in the metrics callback.
- `/lab` with a scenario that has no number (not run) renders "—", not a crash.

---

### Task 1: Sale metrics
**Files:**
- Create: `Application/Abstractions/Observability/ISalesMetrics.cs` and `Infrastructure/Observability/SalesMetrics.cs`
- Modify: the recording points (create hold, hold expiry, admission loop, pay/fulfil, refund, gateway client, outbox gauges in the worker, inventory reconciler) and `ServiceDefaults` (`AddMeter("TicketRush")`)
- Test: Application tests with a fake `ISalesMetrics`; Infrastructure-level `MeterListener` tests in integration tests

**Interfaces:**
- Produces `ISalesMetrics`:
  - `HoldCreated(string result)`, `HoldsExpired(int n)`
  - `Admitted(int n)`, `QueueObserved(Guid eventId, long length, long active)`
  - `OrderPaid()`, `Refunded(string reason)`
  - `GatewayCall(TimeSpan d, string op)`
  - `OutboxObserved(double lagSeconds, long deadLetters)`
  - `DriftFixed(int n)`

### Task 2: Connected purchase trace
**Files:**
- Modify: `OutboxMessage` (+`TraceParent`); `OutboxPostCommit` / message creation; `OutboxProcessor` (parent context); migration `AddOutboxTraceParent`; `SmtpEmailSender` (span); `MockPaymentGateway` / session (+`TraceParent`); `MockWebhookDeliverer` (header)
- Test: an integration test, with an in-memory exporter, that a purchase's spans share one trace id, hold → order → pay → webhook → outbox → email

### Task 3: Web checkout traceparent
- One `traceparent` per purchase, kept for the checkout and sent on the hold, order and pay calls.
- Vitest: the same header across the three calls, and a new one for the next purchase.

### Task 4: LoadTest seed endpoint
- `POST /api/v1/loadtest/seed {users, seats=5000, gaTiers=2, gaCapacity}` → `{eventId, phaseId, gaTierIds, seatIds, hotSeatIds, tokens}`. It needs the `LoadTest` environment and the key.
- Integration tests: 404 outside `LoadTest`; 404 with a wrong key; it creates a published event whose sale is open.

### Task 5: Invariants
- `load-tests/check-invariants.sql`: the existing checks plus `user_over_ticket_limit`. `SaleInvariantTests` plants a new corruption for it.
- `load-tests/check-redis-sold.sh`: Redis sold seats and GA counts against the database.

### Task 6: k6 scenarios + runner
- `load-tests/scenarios/01..05`, plus `lib/` shared helpers. `sale.js` becomes `02-buy-flow.js` and keeps all of its checks.
- `run.sh`, `Makefile` (`load-test`), `chaos.sh`, `soak-sampler.sh`, `collect-results.mjs` → `web/src/features/lab/results.json`.

### Task 7: Observability overlay
- `docker-compose.observability.yml`, `observability/{otel-collector.yaml, prometheus.yml, tempo.yaml, grafana/provisioning/...}`, the "Flash sale" dashboard JSON, and the alert rules.
- Verify: `docker compose config` is valid, and Grafana starts with the dashboard (smoke run).

### Task 8: `/lab` page
- `app/(public)/lab/page.tsx` plus `features/lab/*`: a static server component, with copy in both languages, and a Lighthouse URL.
- Vitest: it renders the scenario table and the ✓ invariants, and shows "—" for a scenario that has no number. Also an e2e axe check.

### Task 9: Real runs, results, docs, CI, push
- Run all five at laptop scale, commit `results.json`, and update the load-tests README and the main README.
- Then a full `ci-local`, then push.
