# TicketRush

Flash-sale ticketing platform: tens of thousands of fans hit "Buy" at the same second, and the system must
stay fair, fast and never sell the same seat twice. Build specs live in [specs/](specs/README.md).

## Prerequisites

- .NET SDK 10.0.403+ (`global.json`), Node.js 24+, Docker Desktop running
- Optional: Aspire CLI (`dotnet tool install -g Aspire.Cli`) for `aspire run`

## Run everything locally

```bash
cd web && npm install && cd ..
aspire run                                   # or: dotnet run --project src/TicketRush.AppHost
```

The Aspire dashboard link (with login token) is printed on start. It starts postgres (+ pgAdmin),
redis (+ RedisInsight), mailpit, a one-shot `migrations` job (migrates + seeds dev data), `api`,
`worker` and `web`. Postgres and Redis keep their data in named volumes between runs.

Dev accounts (password `Dev@12345`): `customer@`, `organizer@`, `admin@`, `gate@` `ticketrush.dev`.
The seeded event "Hà Anh Tuấn - Chân Trời Rực Rỡ" opens its sale 5 minutes after the first seed
(waiting-room testing). Re-seeding is idempotent; drop the `ticketrush-postgres-data` volume to start fresh.

## Tests

```bash
dotnet test --project tests/TicketRush.Domain.Tests
dotnet test --project tests/TicketRush.Application.Tests
dotnet test --project tests/TicketRush.ArchitectureTests
dotnet test --project tests/TicketRush.Api.IntegrationTests   # needs Docker (Testcontainers)
cd web && npm run lint && npm run typecheck && npm test && npm run build
```

## Layout

| Path | What |
|---|---|
| `src/TicketRush.Domain` | Aggregates, value objects, domain events, `Result`/`Error`. No dependencies |
| `src/TicketRush.Application` | Use cases, ports (`IAppDbContext`, `IDistributedLock`, `IIntegrationEventHandler<T>`), options |
| `src/TicketRush.Infrastructure` | EF Core (Npgsql, snake_case, `xmin`), outbox, Redis, Identity, dev seed |
| `src/TicketRush.Api` | Minimal API endpoints, middleware, ProblemDetails |
| `src/TicketRush.Worker` | Background services (outbox dispatcher, leader-elected jobs) |
| `src/TicketRush.MigrationService` | One-shot migrator (+ dev seed) |
| `src/TicketRush.AppHost`, `src/TicketRush.ServiceDefaults` | Aspire orchestration, OpenTelemetry, health checks |
| `web/` | Next.js 16 app; `/api/*` is rewritten to the api (same origin) |

## Production

`docker-compose.prod.yml` runs the stack on a single VPS behind Caddy (TLS + HSTS). Copy `.env.example`
to `.env`, fill in secrets, then `docker compose -f docker-compose.prod.yml --env-file .env up -d`.
CI publishes images to GHCR on every push to `main`.
