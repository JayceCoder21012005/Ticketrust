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

`outbox_messages(id, type, payload jsonb, aggregate_id, occurred_at, processed_at, attempts, next_attempt_at, last_error)`

`aggregate_id` makes per-aggregate ordering enforceable across workers: a message is only claimable when no older message of the same aggregate is still pending (`NOT EXISTS` in the claim query). Events raised in one `SaveChanges` get `occurred_at` stepped by 1 µs so raise order is kept.

- A `SaveChanges` interceptor converts the aggregates' domain events into outbox rows **in the same transaction**.
- **OutboxDispatcher** (Worker): loop with `SELECT … WHERE processed_at IS NULL AND next_attempt_at <= now ORDER BY occurred_at LIMIT 100 FOR UPDATE SKIP LOCKED`, so several worker instances never take the same message.
- Each message type maps to one or more `IIntegrationEventHandler<T>`. Every handler **must be idempotent**. Guard: table `consumer_receipts(message_id, handler)` with a unique constraint.
- Retry with exponential backoff + jitter (1 s → max 10 min). After 10 attempts → dead letter (`processed_at` set, `last_error` kept) + metric. Admin view in 16.
- Messages for the same aggregate are handled in `occurred_at` order.
- Each handler runs in a savepoint of the batch transaction through a fresh `AppDbContext` sharing the connection, so its DB writes and its `consumer_receipts` row commit atomically with the message state (exactly-once for DB effects; external effects use the message id as idempotency key).

## Implementation notes

- Seed needs users and events before specs 03–05, so 01 ships minimal `Event` / `SalePhase` / `TicketTier` / `Venue` aggregates and Identity (`AppUser`, `AppRole`, tables `users`, `roles`, …). Later specs extend them.
- Seed users are created with a pre-hashed `Dev@12345` (bypasses the stricter password policy of spec 03).
- `IDistributedLock` + `LeaderElectedBackgroundService` (Worker) are the leader-election helper. The outbox dispatcher does **not** need it (`SKIP LOCKED`).
- The web rewrite falls back to `http://localhost:5080` when `API_URL` is unset (tooling such as `next typegen` loads the config). Docker bakes `API_URL` at build time.
- EF retries are disabled (`DisableRetry`) because use cases own explicit transactions.

## Configuration sections (validated on start)

`ConnectionStrings`, `Jwt`, `Redis`, `Payment`, `Email`, `Storage`, `Queue`, `Holds`, `RateLimits`, `Frontend:BaseUrl`.

## Acceptance criteria

- [x] `aspire run` starts all resources (verified with `dotnet run --project src/TicketRush.AppHost`); Aspire dashboard shows traces from api → postgres/redis (OTel wired; check visually).
- [x] `GET /alive` returns 503 when Redis is stopped, 200 when back.
- [x] Throwing in an endpoint returns ProblemDetails with `traceId` and no stack trace in non-Development.
- [x] `ArchitectureTests` fail if Domain references any other project or EF Core.
- [x] Seed runs twice without duplicates.
- [x] Killing the worker between commit and dispatch → the message is handled after restart, exactly once.
- [x] Two worker instances process 10,000 outbox messages, and no handler runs twice for the same message.
- [x] A handler that always throws ends in dead letter after 10 attempts. Other messages keep flowing.
- [ ] CI pipeline green on a fresh clone (needs a GitHub remote); integration tests use Testcontainers, no shared DB.
- [x] `web` opens at its Aspire URL and calls `api` through the Next.js rewrite `/api/* → api` (same origin, see 03).
