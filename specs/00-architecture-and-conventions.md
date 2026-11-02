# 00 — Architecture & Conventions

Every other spec assumes this file. Read it first, follow it always.

## Stack (pin these)

| Layer | Choice |
|---|---|
| Runtime | .NET 10 (LTS), C# 14, ASP.NET Core Minimal APIs |
| Data | PostgreSQL 17 + EF Core 10 (Npgsql). Redis 7 (StackExchange.Redis) |
| Realtime | SignalR + Redis backplane |
| Orchestration | Aspire 13.x (AppHost + ServiceDefaults), OpenTelemetry |
| Libraries | FluentValidation, Polly (via `Microsoft.Extensions.Http.Resilience`), HybridCache, MailKit, Scriban |
| Tests | xUnit v3, Shouldly, NSubstitute, Testcontainers, NetArchTest, k6 |
| Frontend | Next.js 16 (App Router), React 19, TypeScript strict, Tailwind v4, shadcn/ui, TanStack Query v5, react-hook-form + zod, `@microsoft/signalr` |

**Do not use:** MediatR, AutoMapper, FluentAssertions (license changes), a generic `Repository<T>`, `moment`, full `lodash`.

## Solution layout

```
TicketRush/
├─ src/
│  ├─ TicketRush.AppHost/          # Aspire: wires postgres, redis, mailpit, api, worker, web
│  ├─ TicketRush.ServiceDefaults/  # OTel, health checks, resilience defaults
│  ├─ TicketRush.Domain/           # Entities, value objects, domain events, errors. ZERO dependencies
│  ├─ TicketRush.Application/      # Use cases (handlers), ports (interfaces), DTOs, validators
│  ├─ TicketRush.Infrastructure/   # EF Core, Redis scripts, payment gateway, email, QR, storage
│  ├─ TicketRush.Api/              # Endpoints, auth, SignalR hubs, middleware, filters
│  └─ TicketRush.Worker/           # BackgroundServices only
├─ web/                            # Next.js app
├─ tests/  (Domain.Tests, Application.Tests, Api.IntegrationTests, ArchitectureTests)
├─ load-tests/                     # k6
└─ specs/
```

**Dependency rule** (enforced by `ArchitectureTests`): Domain ← Application ← Infrastructure/Api/Worker. Domain references nothing. Application references only Domain and abstractions (`Microsoft.Extensions.*.Abstractions`).

### Feature folders (vertical slices inside layers)

```
Application/Features/Orders/CreateOrder/
  CreateOrderCommand.cs      // record
  CreateOrderHandler.cs      // sealed class, one public HandleAsync
  CreateOrderValidator.cs    // FluentValidation
Api/Features/Orders/OrderEndpoints.cs   // MapGroup("/api/v1/orders") — thin: bind → handler → map Result
```

## Design patterns (use exactly where listed)

| Pattern | Where | Why |
|---|---|---|
| Aggregate root + rich domain model | `Event`, `Order`, `Ticket`, `QueueEntry` | Invariants live in the entity, not in handlers. No public setters |
| State | `Order`, `Ticket`, `Event` status transitions | Methods like `order.MarkPaid()` throw `DomainException` on illegal transitions |
| Strategy | `IPaymentGateway` (Mock, later VNPay), `IInventoryStrategy` (Seated vs GeneralAdmission) | Swap behaviour without `if/else` chains |
| Result | All handlers return `Result<T>` / `Result` with `Error(Code, Message, Type)` | Expected failures are values, not exceptions |
| Transactional Outbox | Domain events → `outbox_messages` in the same transaction | No dual writes between DB and Redis/email |
| Unit of Work | `AppDbContext` behind `IAppDbContext` | One `SaveChangesAsync` per use case |
| Decorator | Caching around read handlers via HybridCache | Keep handlers cache-agnostic |
| Options | Every config section → `IOptions<T>` validated on start (`ValidateDataAnnotations().ValidateOnStart()`) | Fail fast on bad config |
| Factory | `TicketFactory`, `AdmissionTokenFactory` | Centralise creation rules |

## Coding rules

- `Nullable` enable, `TreatWarningsAsErrors`, file-scoped namespaces, `sealed` by default, primary constructors for DI.
- DTOs and commands are `record`s. Map manually with static `ToDto()` extension methods.
- Every async method takes `CancellationToken ct` and passes it down. Never `.Result`, `.Wait()`, `async void`.
- Time: inject `TimeProvider`; never `DateTime.Now`. Store `timestamptz`, use `DateTimeOffset` UTC. Display in `Asia/Ho_Chi_Minh`.
- IDs: `Guid.CreateVersion7()` (ordered, index friendly). Public URLs for events use a `slug`.
- Money: value object `Money(long Amount, string Currency = "VND")`. VND has no minor unit → store `bigint`. Never `double`.
- Concurrency tokens: Postgres `xmin` mapped as row version on every aggregate.
- Methods ≤ 30 lines, classes do one thing, names say intent (`TryHoldSeatsAsync`, not `Process`).
- Comments explain *why*, not *what*. Each Redis Lua script has a header comment describing keys, args and return codes.

## API conventions

- Base path `/api/v1`, kebab-case routes, camelCase JSON, enums as strings.
- Errors: RFC 9457 `ProblemDetails` with extension `code` (e.g. `seat.already_held`). Each spec lists its codes.
- Map `Result` error types: Validation→400, Unauthorized→401, Forbidden→403, NotFound→404, Conflict→409, RateLimited→429, Unavailable→503.
- Lists: keyset pagination `?cursor=&limit=` (default 20, max 100), response `{ items, nextCursor }`.
- Any POST with money or inventory effect requires header `Idempotency-Key` (UUID). See 08.
- OpenAPI via built-in `Microsoft.AspNetCore.OpenApi`; the frontend generates its typed client from it.
- Ownership check on every resource read/write by id (prevent IDOR). Return 404, not 403, for other users' resources.

## Redis conventions

- Key prefix `tr:{env}:`. Document every key in the spec that owns it.
- Multi-key mutations **must** be one Lua script (atomic). Keys used by one script share a hash tag (`{evt:<eventId>}` for inventory, `{sale:<phaseId>}` for the queue) so scripts work on Redis Cluster.
- Redis unavailable on a write path → **fail closed** (503 `system.busy`). Never fall back to a path that could oversell.

## Classic failure scenarios (global checklist)

Every spec that touches these must handle them explicitly and test them.

| # | Scenario | Defence |
|---|---|---|
| C1 | Overselling a seat | Redis atomic hold → conditional DB update → unique index (07, 08) |
| C2 | Double click / client retry | `Idempotency-Key` + disabled buttons (08) |
| C3 | Hold expires while paying | Payment window < hold TTL; late success → re-claim or auto-refund (08) |
| C4 | Duplicate / out-of-order webhooks | Dedupe by provider event id + state machine guards (08) |
| C5 | Thundering herd at sale open | Waiting room + admission rate (06) |
| C6 | Cache stampede | HybridCache (single-flight) (04) |
| C7 | Bots, scalpers, token sharing | Admission token bound to user, limits, CAPTCHA (06, 12) |
| C8 | Dual writes DB ↔ Redis/email | Outbox + idempotent consumers (09) |
| C9 | Deadlock on multi-seat claims | Always sort seat ids before claiming |
| C10 | Clock skew between client/server | Server sends `expiresAt` + `serverNow`; client computes offset |
| C11 | Retry storms | Exponential backoff + jitter, circuit breaker on payment gateway |
| C12 | Worker crash mid-job | Jobs idempotent, leader lock with TTL, reconciliation jobs |

## Security baseline

HTTPS + HSTS, strict CORS (web origin only), security headers (CSP, X-Content-Type-Options, Referrer-Policy, frame-ancestors none), validate all input with FluentValidation, parameterised queries only (EF), secrets via user-secrets/env (never in git), no PII or tokens in logs, Data Protection keys persisted to Redis, dependency scanning in CI.

## Definition of done (every spec)

Acceptance criteria met · unit + integration tests green · no analyzer warnings · OpenAPI updated · frontend: loading, empty, error states done, keyboard accessible, Lighthouse ≥ 90 on touched pages · spec updated if the implementation differs.
