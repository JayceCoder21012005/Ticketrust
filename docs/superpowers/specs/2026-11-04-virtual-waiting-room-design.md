# Virtual Waiting Room — Core Design (spec 06, phase A)

Source of truth for behaviour: `specs/06-virtual-waiting-room.md`. This document only fixes **scope and build decisions**.

## Intent

Absorb the sale-open spike (C5) and make token sharing useless (C7). Everyone who joins before opening gets a uniformly random position; later arrivals are FIFO. Admission is rate-limited and capped by `MaxActiveShoppers`.

## Scope split

**Phase A — this build (core):**
- Redis keys + 3 Lua scripts (`queue_join`, `queue_finalize_open`, `queue_status_claim`) in `Infrastructure/Redis/Scripts`.
- `QueueOptions` extended: `AdmissionRatePerSecond`, `MaxActiveShoppers`, `ShoppingWindowMinutes`, `PreQueueMinutes` (old `AdmissionsPerSecond` / `AdmissionTokenTtlMinutes` renamed; config + docker-compose updated).
- Workers (leader-elected): `QueueOpener` (Pre → Opening → shuffle → Open), `QueueAdmitter` (1 s tick, expiry sweep, EMA rate), close at `ClosesAt`.
- `AdmissionTokenFactory` (separate signing key `AdmissionToken:SigningKey`) and the `RequireAdmissionToken` endpoint filter, checked against `active` (revocation). Built and tested now; wired onto real buy endpoints by 07/08.
- API: `POST /events/{id}/queue/join`, `GET .../queue/me`, `POST .../queue/leave`; auth + confirmed email; access-code check against `SalePhase.AccessCodeHash`; `pollAfterMs` with jitter; error codes from the spec.
- `QueueEnabled = false` phases admit instantly through the same flow.
- Frontend `/events/[slug]/queue`: Pre / Waiting / Admitted / Closed states, Web Worker polling with backoff, tab title, chime, redirect after 3 s, optional notification and Wake Lock; global `ShoppingTimer` pill on buyer pages.
- Tests: Lua scripts and workers against Testcontainers Redis; API integration tests; the acceptance criteria that don't need 07 (10k random join, post-T0 ordering, idempotent join/token, active cap, token reuse by another user → 403 `admission.invalid`, leader failover with no skipped/duplicated numbers); Vitest for UI and the worker; one Playwright flow.

**Phase B — deferred, after phase A is done:**
- Inventory listener (`AllHeld` / `SoldOut` / back to `Open`) — needs 07. Status values exist in Redis and the API now; nothing sets them yet.
- SoldOut → waitlist button (13).
- `captchaToken` enforcement (12).
- Applying `RequireAdmissionToken` to seat/hold/checkout endpoints (07, 08).

## Non-goals / anti-goals

- No per-IP or per-user rate limiting here (12).
- Never read the queue from Postgres on `/queue/me` (Redis only, p99 < 20 ms target).
- Redis down on join/claim → fail closed, 503 `system.busy`. No fallback that admits users.

## Constraints

- Hash tag `{sale:<phaseId>}` on every queue key; each multi-key mutation is one Lua script with a header comment (keys, args, returns).
- Dependency rule: Application holds abstractions (`IWaitingRoom`, `IAdmissionTokenFactory`); StackExchange.Redis and JWT signing live in Infrastructure.
- Keys follow `RedisKeys` prefix `tr:{env}:`.
- Vietnamese UI copy only; buyer pages pass axe in light and dark.
- Same convention as specs 04/05: one commit for the spec, then a PR.

## My calls (not yet agreed — please check)

1. **Rename** old `QueueOptions` fields rather than keep both.
2. **Phase state lives in Redis only** (`q:{sale}:state`), created lazily on first join from the DB phase (`opensAt`, `closesAt`, `QueueEnabled`); workers discover active phases by scanning DB for phases opening within `PreQueueMinutes`.
3. **Join is allowed from `opensAt − PreQueueMinutes`**; earlier → 409 `queue.not_open`. After `ClosesAt` → `queue.closed`.
4. **Admission token jti/exp**: one token per user per claim, stored in `tokens`; a second tab gets the same token; leaving or expiry deletes it.
5. **Admitted → "Active" claim happens on `/queue/me`** (as the spec's `queue_status_claim`), so a user who never polls never occupies a slot beyond their expiry.
6. **Frontend polling Web Worker** is a plain module worker (`new Worker(new URL(...))`), unit-tested by extracting the scheduling logic into a pure function.
7. **Seed data**: dev seeder gets one phase with a queue opening ~2 min after startup so the flow can be tried manually.

## Left to the builder

File/class naming inside the existing layout, EMA implementation details, exact visual design of the queue card (follow spec 02 tokens).
