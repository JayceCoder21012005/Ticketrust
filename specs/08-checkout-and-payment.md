# 08 — Checkout & Payment

**Goal:** turn a hold into a paid order and issued tickets exactly once, even when users double-click, webhooks repeat or arrive late, or a hold expires mid-payment (C1–C4, C11).

**Depends on:** 07

## Domain

**`Order`** (aggregate root): `UserId`, `EventId`, `HoldId`, `Items[]` (`TierId`, `SeatIndex?`, `UnitPrice`, `Quantity`), `Total: Money`, `Buyer` (name, email, phone snapshot), `Status`, `ExpiresAt` (= hold expiry), `PaidAt?`, `RefundReason?`.

```
Pending ──StartPayment──▶ AwaitingPayment ──MarkPaid──▶ Paid
   │                         │  ▲                        │
   │                         ▼  │ (retry while valid)    └─(event cancelled)─▶ RefundPending ─▶ Refunded
   │                    PaymentFailed
   ├─Cancel──▶ Cancelled
   └─Expire──▶ Expired ──(late success, seats gone)──▶ RefundPending
              (late success, seats still free) ──▶ Paid
```

Each arrow is a method on `Order`. Illegal transitions throw `DomainException` (State pattern). `Paid`, `Refunded`, `Cancelled` are terminal for payment events.

**`PaymentAttempt`**: `OrderId`, `Provider`, `ProviderSessionId`, `Amount`, `Status`, `ExpiresAt` (= order expiry − 30 s, so the gateway normally refuses payment after the hold dies).

**`Ticket`** (created here, UI in 10): `OrderId`, `EventId`, `TierId`, `SeatIndex?`, `OwnerUserId`, `Status` (`Valid`/`CheckedIn`/`Listed`/`Voided`), `QrSecret` (32 random bytes, encrypted with Data Protection).

**DB guards (final truth, C1):**
- Partial unique index `tickets(event_id, seat_index) WHERE status <> 'Voided' AND seat_index IS NOT NULL`.
- GA: `UPDATE ticket_tiers SET sold_count = sold_count + @q WHERE id = @id AND sold_count + @q <= capacity`. 0 rows = conflict.
- Unique `orders(hold_id) WHERE status IN ('Pending','AwaitingPayment','PaymentFailed')`. One open order per hold.

## Idempotency (C2)

`IdempotencyFilter` on every POST here. Table `idempotency_records(user_id, key, request_hash, status, response_code, response_body, created_at)`, unique `(user_id, key)`, kept 24 h.
- New key → insert `InProgress` → run the handler → store the response.
- Same key + same body hash + done → replay the stored response.
- Same key + different body → 422 `idempotency.key_reused`. Still running → 409 `idempotency.in_progress`.

## Payment gateway (Strategy)

```csharp
public interface IPaymentGateway {
  string Provider { get; }
  Task<PaymentSession> CreateSessionAsync(Order order, Uri returnUrl, DateTimeOffset expiresAt, string idempotencyKey, CancellationToken ct);
  Task<PaymentStatus> GetStatusAsync(string sessionId, CancellationToken ct);
  Task<RefundResult> RefundAsync(string sessionId, Money amount, string idempotencyKey, CancellationToken ct);
  WebhookEvent VerifyAndParse(HttpRequest request, string body); // throws on bad signature
}
```

Implement `MockPaymentGateway` now (a VNPay adapter can come later). Gateway calls use a resilience pipeline: 10 s timeout, 2 retries with exponential backoff + jitter, circuit breaker (C11).

**Mock gateway page** `web: /mock-gateway/[sessionId]` (dev/demo only). It shows the amount and buttons: *Thành công*, *Thất bại*, *Thành công – webhook trễ 3 phút* (tests C3), *Gửi webhook 2 lần* (tests C4). Webhooks are signed `X-Signature: t=<unix>,v1=<HMAC-SHA256(secret, t + "." + body)>`.

## API

| Method | Route | Auth | Notes |
|---|---|---|---|
| POST | `/orders` | user + idempotency | `{eventId, holdId, buyer}` → `Pending` order with items and prices copied from the hold. A live hold owned by the user is the proof of admission (only admitted users can create holds) |
| POST | `/orders/{id}/pay` | owner + idempotency | → `{redirectUrl}`. Reuses a live attempt if one exists |
| GET | `/orders/{id}` | owner | Status + items + `expiresAt`, `serverNow` |
| POST | `/orders/{id}/cancel` | owner | Releases the hold → `Cancelled` |
| GET | `/me/orders` | user | Keyset list |
| POST | `/webhooks/payments/{provider}` | signature | Body ≤ 64 KB, exempt from user rate limits |

Error codes: `order.hold_invalid`, `order.hold_expired`, `order.not_payable`, `order.expired`, `payment.gateway_unavailable`, `idempotency.key_reused`, `idempotency.in_progress`.

## Webhook handling (one transaction, `FulfillOrderHandler`)

1. Verify the signature and that `|now − t| ≤ 5 min` (replay protection).
2. Insert into `processed_webhooks(provider_event_id)`. On a unique violation → return 200 (duplicate, C4).
3. Load the order by session id (`xmin` concurrency, up to 3 retries on conflict).
4. **Succeeded**:
   - Already `Paid` → no-op.
   - Otherwise claim inventory through `IInventoryStrategy` (`SeatedInventoryStrategy` inserts tickets, `GeneralAdmissionInventoryStrategy` runs the conditional update + inserts tickets) → `order.MarkPaid()` → outbox `OrderPaid`. Commit.
   - Also re-check the per-user cap in the DB (12): the user's valid tickets for the event + new ones ≤ `MaxTicketsPerUser`, otherwise treat it like a conflict.
   - On a unique or GA conflict (late payment, seats resold) → roll back, then in a new transaction `order.MarkUnfulfillable()` → `RefundPending` + outbox `RefundRequested` (C3).
5. **Failed** → `MarkPaymentFailed()` if `AwaitingPayment`, otherwise ignore (out-of-order safe).
6. Always return 200 after a committed decision. Return 5xx only when nothing was committed (the provider retries).

`OrderPaid` consumers (idempotent, via outbox): `inventory_commit.lua` (07), leave queue (06), ticket email (09), dashboard counters (15).

## Background services

- **OrderExpiryService** (5 s): for open orders past `ExpiresAt`, first ask `GetStatusAsync`. If succeeded → run the fulfil path. Otherwise `Expire()`.
- **RefundWorker**: processes `RefundRequested` with `RefundAsync(idempotencyKey = orderId)` → `Refunded` + notification.
- **EventCancelled handler**: moves every `Paid` order of the event to `RefundPending` in batches of 500 and voids its tickets. Each batch is one transaction under the exclusive event sale lock: status change, ticket void (checked-in tickets too) and GA `sold_count` release commit together, so the RefundWorker never refunds an order with live tickets. Open orders: `Pending` → `Cancelled`; `AwaitingPayment`/`PaymentFailed` → `Expired` (a session may have taken money; a late success then goes to `RefundPending` `event_cancelled`). The organizer's cancel takes the same lock exclusively before the status update, so an in-flight fulfilment commits first and a later one reads `Cancelled`.

## Frontend

- `/checkout/[orderId]`: order summary (event, seats grouped by section, total in large type), buyer form prefilled from the profile, terms checkbox, hold countdown pinned at the top, "Thanh toán" button (disabled + spinner while pending, sends a fresh `Idempotency-Key` per checkout, reused on retry).
- `/checkout/[orderId]/result`: polls `GET /orders/{id}` every 1.5 s (max 90 s) or listens to the user's SignalR channel (09):
  - *Đang xác nhận* — "Đừng đóng trang"
  - *Thành công* — subtle confetti, "Xem vé" button
  - *Thất bại* — "Thử lại" if the hold is still valid
  - *Hết hạn* — back to seats
  - *Đang hoàn tiền* — honest explanation of the late payment and when the refund lands
- `/account/orders`: list + detail with a status timeline.

## Acceptance criteria

- [ ] Double-clicking "Thanh toán" (two requests, same key) creates one attempt and returns the same `redirectUrl`.
- [ ] Sending the same webhook 5 times concurrently issues tickets once.
- [ ] A `Failed` webhook arriving after `Succeeded` leaves the order `Paid`.
- [ ] A late success after the seat was resold → `RefundPending` → `Refunded`, never two tickets for one seat.
- [ ] A late success when the seat is still free → `Paid` with tickets, and Redis `sold` is updated.
- [ ] A forged signature or a timestamp older than 5 min → 401, nothing changes.
- [ ] A gateway outage opens the circuit breaker. Users see `payment.gateway_unavailable` and holds are untouched.
- [ ] After a sale load test: `count(tickets per seat) ≤ 1` and `sold_count ≤ capacity` for every tier (SQL invariant check).
