# Checkout & Payment + Spec 07 Follow-ups Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development. Steps use checkbox (`- [ ]`) syntax.

**Goal:** (A) Close every open item left by Spec 07, then (B) turn a live hold into a paid order and issued tickets exactly once (C1–C4, C11), per `specs/08-checkout-and-payment.md`.

**Why one plan, two streams:** Stream A items A1–A3 are prerequisites of Stream B (the `inventory_commit` double-deduct bug sits directly under `OrderPaid`). A4–A6 touch unrelated files and can be done in the same pass. Implementers run **sequentially** (shared files: `RedisInventoryStore`, `Program.cs`, DI, migrations); "parallel" here means one plan, one ledger, and A-tasks reviewed before B-tasks build on them.

**Spec:** `specs/08-checkout-and-payment.md` (binding). Spec 07 design doc: `docs/superpowers/specs/2026-11-04-seat-selection-and-hold-design.md`.

**Branching (ruling):** A on `fix/07-followups` cut from `feat/07-seat-selection-and-hold` (PR into main after 07 merges, or stacked). B on `feat/08-checkout-and-payment` cut from `fix/07-followups`. Nothing is merged or pushed without the user.

## Global Constraints

- Spec 00 coding rules: sealed, primary ctors, `TimeProvider`, `CancellationToken ct` everywhere, methods ≤ 30 lines, `TreatWarningsAsErrors`; Vietnamese UI copy; axe clean in light/dark.
- Money = whole VND `long` (`Money`), prices copied from the hold, never recomputed from the live catalog.
- Error codes exactly as spec 08: `order.hold_invalid`, `order.hold_expired`, `order.not_payable`, `order.expired`, `payment.gateway_unavailable`, `idempotency.key_reused` (422), `idempotency.in_progress` (409). Reuse the existing `IdempotencyFilter`/`RedisIdempotencyStore` (07) — do not build a second one unless the spec's 24 h Postgres table is required (see Task B2 ruling).
- DB is final truth for seats: partial unique index on tickets, conditional `sold_count` update, unique open order per hold.
- Webhook: signature `X-Signature: t=<unix>,v1=<HMAC-SHA256(secret, t + "." + body)>`, |now−t| ≤ 5 min, body ≤ 64 KB, always 200 after a committed decision, 5xx only when nothing committed.
- TDD per task: failing test first. Test baselines to keep green: backend 493, web 552, Playwright 78 pass.
- Commit messages end with `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`.

## Review Focus

1. Same webhook ×5 concurrently → one set of tickets (B6).
2. `Failed` after `Succeeded` leaves `Paid`; late success with seat resold → `RefundPending`, never two tickets per seat (B6).
3. `inventory_commit` re-delivered or with a replaced/expired `holdId` never moves `bought`/GA counters twice (A1).
4. Expiry sweep vs. payment in flight: `GetStatusAsync` is asked before `Expire()` (B8).
5. Event cancel vs. in-flight payments: every hold released AND open orders cancelled AND paid orders → `RefundPending` (A2, B9).

---

## Stream A — Spec 07 follow-ups

### Task A1: `inventory_commit` idempotency (blocks B)
**Files:** `Redis/Scripts/inventory_commit.lua`, `Inventory/InventoryScripts.cs`, `RedisInventoryStore*.cs`, `IInventoryStore.cs`, Redis integration tests.
- Add a per-commit marker key `…:committed:<orderId>` (in `KEYS`, same hash tag, `SET NX`, TTL 7 d). Second call with the same `orderId` returns `{'DUPLICATE', version}` and changes nothing. `IInventoryStore.CommitAsync` gains `orderId`.
- A replaced/expired `holdId` (not live) must not decrement `gaheld`; GA is taken from `remaining` exactly once.
- Tests (written first): double call same orderId ⇒ `bought`, `sold`, GA remaining, version unchanged on 2nd; commit after replace ⇒ counters correct once; expired-hold late commit ⇒ GA decremented once.
- Update the header comment (KEYS/ARGV/returns).

### Task A2: Event-cancel ordering + inventory cleanup
**Files:** `EventCancelledInventoryHandler.cs`, tests.
- Close the queue rooms **before** releasing holds (stop new admissions first), then release holds in batches; make the handler tolerate partial failure (re-run safe). Test: a hold created between release and close can no longer happen (room closed first → `RequireAdmissionToken` rejects).
- Leave a clearly marked extension point for B9 (orders) — no order code here.

### Task A3: Redis durability doc + runtime guard
**Files:** `deploy/*`, `README`/`docs`, `docker-compose.prod.yml`, Worker reconcile test.
- Document the AOF `everysec` ≤ 1 s window and that the reconciler + DB unique index are the repair path (decision R3 stays). Add a startup check that logs a warning if Redis `appendonly` ≠ `yes` in non-dev. Test: reconcile repairs `sold` lost after simulated flush (extend existing reconcile test).

### Task A4: WebSocket through Caddy verified
**Files:** `deploy/Caddyfile`, a smoke script under `tests/` or `deploy/`.
- Beyond `caddy validate`: compose-based smoke test (Testcontainers or `docker compose` profile) that opens `/hubs/inventory` through Caddy, negotiates WebSocket, receives a `SeatDelta`. If Docker is unavailable in the sandbox, ledger it as "not verifiable here" and ship the script + doc.

### Task A5: Organizer layout-preview perf: baseline vs main, then fix or reclassify
**Files:** `web/e2e/organizer.spec.ts`, organizer preview components (only if regression proven).
- Run the 20×30 preview test on `main` and on the branch, 5 runs each, record medians in the ledger. Same machine, nothing else running.
- If main is also ≥ ~500 ms: the 100 ms budget is a pre-existing, environment-dependent assertion → make it a measured budget (tagged `@perf`, run in CI on a stable runner, relaxed locally) and open a note; do **not** touch organizer code.
- If main is fast and branch slow: bisect the 19 commits, fix root cause (suspect: global style/`will-change`/provider re-render from seats work).
- Also: `design-system.spec.ts` offline-font failure — make the font load deterministic offline (self-hosted `next/font` or skip with documented reason). Smallest change.

### Task A6: Seats UI minors (12 items)
**Files:** `web/src/features/seats/*`, tests.
- The 12 Minor items were recorded in the deleted 07 ledger; recover them first from `git log feat/07..` messages + the final-review report (ask the user if not recoverable). Known: focus returns to the section toggle after opening a section. Batch as one dispatch per the "small same-shape work" rule; each item gets a test or an explicit "cosmetic, no test" note.

---

## Stream B — Spec 08

### Task B1: Domain — Order, PaymentAttempt, Ticket, state machine
**Files:** `Domain/Orders/*`, `Domain/Tickets/*`, `tests/TicketRush.Domain.Tests`.
- `Order` per the diagram: `Create`, `StartPayment`, `MarkPaid`, `MarkPaymentFailed`, `Cancel`, `Expire`, `MarkUnfulfillable` (→ `RefundPending`; legal from `Expired` and `AwaitingPayment`/`PaymentFailed`), `MarkRefunded`, `RequestRefundForCancelledEvent` (Paid→RefundPending). Illegal transitions throw `DomainException`. `Paid`/`Refunded`/`Cancelled` terminal for payment events (a late `Failed` is ignored by the handler, a late `Succeeded` on `Paid` is a no-op).
- `PaymentAttempt.ExpiresAt = order.ExpiresAt − 30 s`. `Ticket` with `QrSecret` (32 random bytes). Domain events `OrderPaid`, `RefundRequested`, `OrderExpired` registered in `DomainEventTypeRegistry`.
- Tests: exhaustive transition table (every state × every method).

### Task B2: Persistence + migration + idempotency store decision
**Files:** `Infrastructure/Persistence/Configurations/*`, migration `AddCheckout`, `AppDbContext`, `IAppDbContext`.
- Tables `orders`, `order_items`, `payment_attempts`, `tickets`, `processed_webhooks(provider_event_id unique)`; `xmin` concurrency token on `orders`.
- Guards: partial unique index tickets `(event_id, seat_index) WHERE status <> 'Voided' AND seat_index IS NOT NULL`; unique `orders(hold_id) WHERE status IN ('Pending','AwaitingPayment','PaymentFailed')`; `ticket_tiers.sold_count` conditional-update helper.
- **Ruling:** keep the 07 `IdempotencyFilter` (Redis) for B4–B5 endpoints, because spec 07 already ships it and spec 08's table adds a second source of truth; but the DB guards above make a lost Redis key harmless (unique open order per hold). Cost if wrong: a Postgres-backed store can be swapped behind `IIdempotencyStore`.
- Tests: Testcontainers Postgres — each constraint rejects the violating insert.

### Task B3: Payment gateway abstraction + `MockPaymentGateway` + resilience
**Files:** `Application/Abstractions/Payments/IPaymentGateway.cs`, `Infrastructure/Payments/*`, DI.
- Interface exactly as spec. Mock stores sessions (in memory + DB-backed for multi-process? → use Redis hash with TTL so Api and Worker both see them), signs webhooks, exposes a dev-only endpoint the mock page calls. Resilience: 10 s timeout, 2 retries exponential+jitter, circuit breaker (`Microsoft.Extensions.Http.Resilience` already in packages); open circuit → `payment.gateway_unavailable`, holds untouched.
- Tests: signature verify (good, bad, stale >5 min), breaker opens after N failures, retry count.

### Task B4: Create/read/cancel order (`POST /orders`, `GET /orders/{id}`, `POST /orders/{id}/cancel`, `GET /me/orders`)
**Files:** `Application/Features/Orders/*`, `Api/Features/Orders/OrderEndpoints.cs`.
- Create validates the caller's live hold (`order.hold_invalid`/`order.hold_expired`), copies items/prices from the hold, snapshots buyer, `ExpiresAt` = hold expiry. Cancel releases the hold via `IInventoryStore.ReleaseHoldAsync`. `GET` returns `serverNow`. Keyset list.
- Tests: ownership (404 for other user), double create same hold → same order (unique index + idempotency), cancel releases seats.

### Task B5: `POST /orders/{id}/pay`
- Creates/reuses a live `PaymentAttempt` → `{redirectUrl}`; `AwaitingPayment`; retry from `PaymentFailed` while valid; `order.not_payable`/`order.expired`. Two concurrent requests with the same key → one attempt, same URL.

### Task B6: Webhook + `FulfillOrderHandler` + inventory strategies
**Files:** `Api/Features/Webhooks/*`, `Application/Features/Orders/FulfillOrderHandler.cs`, `IInventoryStrategy`, `SeatedInventoryStrategy`, `GeneralAdmissionInventoryStrategy`.
- Steps 1–6 of the spec in one transaction; `xmin` retry ×3; per-user cap re-check (≤ `MaxTicketsPerUser`); conflict → rollback → new tx `MarkUnfulfillable` + outbox `RefundRequested`.
- Tests (Testcontainers): 5 concurrent identical webhooks → 1 ticket set; Failed-after-Succeeded stays Paid; seat resold → `RefundPending`; late success with seat free → Paid; forged/stale signature → 401 with zero DB change; SQL invariant `count(tickets per seat) ≤ 1`, `sold_count ≤ capacity`.

### Task B7: `OrderPaid` consumers
- Outbox handler calls `IInventoryStore.CommitAsync(orderId, …)` (A1), leaves the queue (06). Email/dashboard consumers are specs 09/15: register no-op hooks only if the outbox requires a handler; otherwise nothing. Idempotent under redelivery (test: handler run twice).

### Task B8: Worker — `OrderExpiryService`, `RefundWorker`
- Expiry every 5 s: for open orders past `ExpiresAt`, `GetStatusAsync` first; succeeded → fulfil path, else `Expire()`. Refund worker: `RefundAsync(idempotencyKey = orderId)` → `MarkRefunded`. Leader-elected like the 07 services. Tests with fake clock + fake gateway.

### Task B9: `EventCancelled` → orders
- Fill the A2 extension point: open orders → `Cancelled`; every `Paid` order → `RefundPending` in batches of 500, tickets `Voided`. Re-run safe; test with 1,200 orders.

### Task B10: Frontend — checkout, result, mock gateway
**Files:** `web/src/features/checkout/*`, `web/src/app/(buyer)/checkout/[orderId]/{page,result/page}.tsx`, `web/src/app/(public)/mock-gateway/[sessionId]/page.tsx` (dev/demo only, 404 in prod), hold-panel "Tiếp tục" wiring, `e2e/checkout.spec.ts`.
- Summary grouped by section, buyer form prefilled, terms checkbox, countdown (reuse seats countdown), "Thanh toán" with a fresh Idempotency-Key per checkout reused on retry. Result page polls every 1.5 s (≤ 90 s) with the five states from the spec. Mock gateway page with the four buttons.
- Tests: Vitest for form/polling/state mapping; Playwright with mock API for happy/fail/expired paths + axe.

### Task B11: `/account/orders`
- List + detail with status timeline; empty state; axe + a Playwright check.

### Task B12: Load/invariant check + docs
- Extend `load-tests/` with a sale scenario ending in the SQL invariant query (spec acceptance #8); update `specs/08` where the build differs; regenerate OpenAPI; update `README` pipeline notes.

## Definition of Done

Spec 08's eight acceptance criteria ticked (or explicitly deferred with reason), A1–A6 closed or reclassified with evidence, suites green (backend ≥ 493 + new, web ≥ 552 + new, Playwright: only the explicitly documented failures), lint/typecheck clean, whole-branch review with no Critical/Important left, then `finishing-a-development-branch` (PR is the user's call).
