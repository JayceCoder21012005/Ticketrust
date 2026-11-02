# 01 — Project Setup & Infrastructure

**Goal:** one command (`aspire run`) starts the whole system locally with seeded data, and CI proves every PR builds and tests green.

**Depends on:** 00

## Scope

- Solution skeleton exactly as in 00, Central Package Management (`Directory.Packages.props`), `Directory.Build.props` (nullable, warnings as errors, analyzers `latest-recommended`), `.editorconfig`.
- Aspire AppHost resources: `postgres` (+ pgAdmin), `redis` (+ RedisInsight), `mailpit` (SMTP sink), `api`, `worker`, `web` (Next.js via JavaScript app resource). Persistent volumes for postgres/redis in dev.
- ServiceDefaults: OpenTelemetry (traces, metrics, logs), `/health` (liveness) and `/alive` (readiness: postgres + redis), standard HTTP resilience.
- `AppDbContext` with snake_case naming, `xmin` concurrency, `SaveChanges` interceptor for audit timestamps (`CreatedAt`, `UpdatedAt`).
- **Transactional outbox** (used by every later spec, see below).
- Migrations applied by a one-shot `MigrationService` resource in dev (not at API startup in prod).
- Dev seed (idempotent): 4 users (customer, organizer, admin, gate staff — password `Dev@12345`), 2 venues, 6 events across all statuses, one event whose sale opens 5 minutes after seeding (to test the waiting room).
- Global middleware: exception → ProblemDetails, correlation id (`X-Correlation-Id` echoed + logged), request logging without bodies.
- Result → HTTP mapping helper (`result.ToHttpResult()`), shared by all endpoints.
- Leader election helper `IDistributedLock` (Redis `SET NX PX` + renew loop) for singleton workers.
- Worker host registered but with no jobs yet (each spec adds its own `BackgroundService`).
- Docker: multi-stage `Dockerfile` for api, worker, web; `docker-compose.prod.yml` for a single VPS.
- CI (GitHub Actions): restore → build → unit tests → integration tests (Testcontainers) → web lint + typecheck + build → publish images on `main`.

## Transactional outbox (C8, C12)

`outbox_messages(id, type, payload jsonb, occurred_at, processed_at, attempts, next_attempt_at, last_error)`

- A `SaveChanges` interceptor converts the aggregates' domain events into outbox rows **in the same transaction**.
- **OutboxDispatcher** (Worker): loop with `SELECT … WHERE processed_at IS NULL AND next_attempt_at <= now ORDER BY occurred_at LIMIT 100 FOR UPDATE SKIP LOCKED`, so several worker instances never take the same message.
- Each message type maps to one or more `IIntegrationEventHandler<T>`. Every handler **must be idempotent**. Guard: table `consumer_receipts(message_id, handler)` with a unique constraint.
- Retry with exponential backoff + jitter (1 s → max 10 min). After 10 attempts → dead letter (`processed_at` set, `last_error` kept) + metric. Admin view in 16.
- Messages for the same aggregate are handled in `occurred_at` order.

## Configuration sections (validated on start)

`ConnectionStrings`, `Jwt`, `Redis`, `Payment`, `Email`, `Storage`, `Queue`, `Holds`, `RateLimits`, `Frontend:BaseUrl`.

## Acceptance criteria

- [ ] `aspire run` starts all resources; Aspire dashboard shows traces from api → postgres/redis.
- [ ] `GET /alive` returns 503 when Redis is stopped, 200 when back.
- [ ] Throwing in an endpoint returns ProblemDetails with `traceId` and no stack trace in non-Development.
- [ ] `ArchitectureTests` fail if Domain references any other project or EF Core.
- [ ] Seed runs twice without duplicates.
- [ ] Killing the worker between commit and dispatch → the message is handled after restart, exactly once.
- [ ] Two worker instances process 10,000 outbox messages, and no handler runs twice for the same message.
- [ ] A handler that always throws ends in dead letter after 10 attempts. Other messages keep flowing.
- [ ] CI pipeline green on a fresh clone; integration tests use Testcontainers, no shared DB.
- [ ] `web` opens at its Aspire URL and calls `api` through the Next.js rewrite `/api/* → api` (same origin, see 03).
