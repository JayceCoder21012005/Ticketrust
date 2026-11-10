# 08 — Checkout & Payment

**Goal:** turn a hold into a paid order and issued tickets exactly once, even when users double-click, webhooks repeat or arrive late, or a hold expires mid-payment (C1–C4, C11).

**Depends on:** 07

## Domain

**`Order`** (aggregate root): `UserId`, `EventId`, `HoldId`, `Items[]` (`TierId`, `SeatIndex?`, `UnitPrice`, `Quantity`), `Total: Money`, `Buyer` (name, email, phone snapshot), `Status`, `ExpiresAt` (= hold expiry), `PaidAt?`, `RefundReason?`, refund retry state (`refund_attempts`, `refund_next_attempt_at`, `refund_last_error`, `refund_needs_attention`). Prices are copied from the hold, never recomputed from the live catalog.

```
Pending ──StartPayment──▶ AwaitingPayment ──MarkPaid──▶ Paid ──(event cancelled)──▶ RefundPending ─▶ Refunded
   │                         │  ▲
   │                         ▼  │ (retry while valid)
   │                    PaymentFailed
   ├─Cancel──▶ Cancelled                    (from Pending only)
   └─Expire──▶ Expired ──(late success, seats gone)──▶ RefundPending
              (late success, seats still free) ──▶ Paid
```

Each arrow is a method on `Order`. Illegal transitions throw `DomainException` (State pattern). `Paid`, `Refunded`, `Cancelled` are terminal for payment events. `AwaitingPayment`, `PaymentFailed` and `Expired` can also go straight to `RefundPending` (`MarkUnfulfillable(reason)`) when a success arrives that cannot be fulfilled.

**Cancel is `Pending`-only.** Once payment started the buyer cannot cancel (409 `order.not_payable`), so a `Cancelled` order has never taken money. The UI hides "Huỷ đơn" while the order is `AwaitingPayment`.

**Refund reasons** (`Order.RefundReason`, `RefundReasons`): `seats_unavailable` (late success, the seat was resold or the tier is full), `event_cancelled`, `event_ended` (success for an event no longer on sale), `purchase_limit_exceeded` (the tickets would take the buyer over `MaxTicketsPerUser`), `duplicate_payment` (a second session paid an order that is already paid; only that session's money goes back), `order_closed` (money for a cancelled order, or one that never started payment), `amount_mismatch` (the provider's amount or currency differs from the session's; nothing is issued).

**`PaymentAttempt`**: `OrderId`, `Provider`, `ProviderSessionId` (unique), `RedirectUrl`, `Amount`, `Status` (`Pending`, `Succeeded`, `Failed`, `Expired`, `RefundPending`, `Refunded`), `ExpiresAt` (= order expiry − 30 s, so the gateway normally refuses payment after the hold dies), the same refund retry state as the order. An attempt is always saved before the buyer sees the gateway page, so a webhook can always find its order by session id.

**`Ticket`** (created here, UI in 10): `OrderId`, `EventId`, `TierId`, `SeatIndex?`, `OwnerUserId`, `Status` (`Valid`/`CheckedIn`/`Listed`/`Voided`), `QrSecret` (32 random bytes, encrypted at rest with ASP.NET Data Protection; see the README on the key ring).

**DB guards (final truth, C1):**
- Partial unique index `ux_tickets_event_seat` on `tickets(event_id, seat_index) WHERE status <> 'Voided' AND seat_index IS NOT NULL`.
- GA: `UPDATE ticket_tiers SET sold_count = sold_count + @q WHERE id = @id AND sold_count + @q <= capacity`. 0 rows = conflict. Every void or refund of a GA ticket releases the count in the same transaction (`ITierInventory.ReleaseAsync`).
- Unique `ux_orders_open_hold` on `orders(hold_id) WHERE status IN ('Pending','AwaitingPayment','PaymentFailed')`. One open order per hold.

## Idempotency (C2)

The existing Redis `IdempotencyFilter` (07) guards every POST here; no second implementation and no Postgres `idempotency_records` table (decision of task B2: the DB guards above already cover a lost key, and the store sits behind `IIdempotencyStore` if it ever has to move). The key must be a UUID in `Idempotency-Key`, stored per user.
- New key → run the handler → store the response. Missing or malformed key → 400 `idempotency.key_required`.
- Same key + same body hash + done → replay the stored response (status and body; `Location` is not replayed).
- Same key + different body → 422 `idempotency.key_reused`. Still running → 409 `idempotency.in_progress`.
- 5xx answers are not stored, so a retry can succeed. Redis down → 503 `system.busy`.
- TTL: `POST /orders` and `/pay` keep the answer for the hold TTL (a replayed redirect into a dead session is useless after that); `/cancel` keeps it 24 h.
- A retry after a **failed payment** must use a fresh key: the same key replays the dead session's URL. The frontend keeps one key per request it means and renews it after any stored answer.

## Payment gateway (Strategy)

```csharp
public interface IPaymentGateway {
  string Provider { get; }
  Task<PaymentSession> CreateSessionAsync(Order order, Uri returnUrl, DateTimeOffset expiresAt, string idempotencyKey, CancellationToken ct);
  Task<PaymentStatus> GetStatusAsync(string sessionId, CancellationToken ct);
  Task<RefundResult> RefundAsync(string sessionId, Money amount, string idempotencyKey, CancellationToken ct);
  WebhookEvent VerifyAndParse(WebhookHeaders headers, string body); // throws InvalidWebhookSignatureException / InvalidWebhookPayloadException
}
```

`VerifyAndParse` takes the already-extracted `WebhookHeaders` and the decoded body instead of an `HttpRequest`, so the port stays free of ASP.NET types. It is pure and synchronous, so it is never wrapped by the resilience pipeline and keeps working while the circuit is open.

Implement `MockPaymentGateway` now (a VNPay adapter can come later). Gateway calls use a resilience pipeline (`ResilientPaymentGateway`, options `Payments:Resilience`): 10 s timeout, 2 retries with exponential backoff + jitter, circuit breaker (C11). A gateway outage answers `503 payment.gateway_unavailable` and touches neither the order nor the hold. A gateway that *refuses* a request (`PaymentRejectedException`, e.g. an expired window) is neither retried nor counted by the breaker.

Refunds are retried with the **same** key (the order id, or `attempt:{attemptId}` for a session-level refund). That assumes the gateway replays only a stored success for a key, as the mock does; a provider that replays a stored failure needs an adapter that derives a fresh provider key per retry (documented on `IPaymentGateway.RefundAsync`).

**Mock gateway** (dev/demo only; `Payments:Mock:Enabled`, refused in Production unless `AllowInProduction`). The api maps `GET /mock-gateway/sessions/{id}` and `POST /mock-gateway/sessions/{id}/complete {outcome}` (`succeeded`, `failed`, `succeeded_delayed`, `succeeded_twice`); the worker's `MockWebhookDeliveryService` delivers the signed webhooks with retries and backoff. The session status flips when you press the button, also for `succeeded_delayed` (the money moved, only the notification is late), so `GetStatusAsync` can find a late payment before its webhook arrives.

**Mock gateway page** `web: /mock-gateway/[sessionId]` shows the amount and four buttons: *Thành công*, *Thất bại*, *Thành công – webhook trễ 3 phút* (`DelayedWebhookDelay`, tests C3), *Gửi webhook 2 lần* (tests C4). The web returns a real 404 for `/mock-gateway/*` (proxy) unless `ENABLE_MOCK_GATEWAY=1` or it runs outside production, and the page shows an unavailable state when the api's endpoints are not mapped. Webhooks are signed `X-Signature: t=<unix>,v1=<HMAC-SHA256(secret, t + "." + body)>`, freshly at every delivery attempt.

## API

| Method | Route | Auth | Notes |
|---|---|---|---|
| POST | `/orders` | user + idempotency | `{eventId, holdId, buyer}` → `Pending` order with items and prices copied from the hold; 201, or 200 with the existing order when the hold already has an open one. A live hold owned by the user is the proof of admission (only admitted users can create holds). `order.hold_invalid` / `order.hold_expired` otherwise |
| POST | `/orders/{id}/pay` | owner + idempotency | → `{redirectUrl, attemptId, expiresAt, serverNow}`. Reuses a live attempt if one exists; refuses an event that is no longer published (`order.not_payable`) and a closed window (`order.expired`) |
| GET | `/orders/{id}` | owner | Status + items + `expiresAt`, `serverNow`, `paidAt`, `refundReason`, `ticketCount` |
| POST | `/orders/{id}/cancel` | owner + idempotency | `Pending` only → releases the hold → `Cancelled`; any other state 409 `order.not_payable` |
| GET | `/me/orders` | user | Keyset list (`cursor`, `limit` ≤ 50) with event title, slug and start |
| POST | `/webhooks/payments/{provider}` | signature | Body ≤ 64 KB (413), exempt from rate limits. 404 unknown provider, 401 bad signature or timestamp, 400 unknown event, else 200 `{outcome}` |

Error codes: `order.hold_invalid`, `order.hold_expired`, `order.not_payable`, `order.expired`, `payment.gateway_unavailable`, `payment.rejected`, `idempotency.key_required`, `idempotency.key_reused` (422), `idempotency.in_progress` (409).

## Webhook handling (one transaction, `FulfillOrderHandler`)

1. Verify the signature and that `|now − t| ≤ 5 min` (replay protection). The body is decoded as strict UTF-8 exactly as signed. A bad signature, a stale timestamp or a malformed header is 401 and nothing is read or written.
2. Find the attempt by `(provider, session id)`. An unknown session answers 200 `unknown_session` and records nothing.
3. Insert into `processed_webhooks`, keyed `(provider, provider_event_id)` (ids are unique per provider only). On a conflict → 200 `duplicate` (C4). The row commits together with the decision, so a crash in between never swallows an event.
4. Load the order (`xmin` concurrency; a concurrent change or deadlock re-runs the whole decision, up to 3 times, then answers 5xx with nothing committed). Each transaction runs with `Fulfilment:LockTimeout` (5 s) so a stuck lock becomes a retryable 5xx.
5. **Succeeded**, in this order:
   - The attempt is already `Succeeded`, `RefundPending` or `Refunded` (the same payment under another event id) → ignored; never twice.
   - The amount or currency differs from the session's, or is missing → nothing is issued and only **that session's** money goes back (`PaymentRefundRequested`, reason `amount_mismatch`); the order stays as it is.
   - The order is `Paid`, `RefundPending` or `Refunded` (another session already settled it) → refund that session only (`duplicate_payment`). The order is `Pending` or `Cancelled` (it can take no money) → refund that session only (`order_closed`). Order and tickets stay as they are.
   - The order is `AwaitingPayment`, `PaymentFailed` or `Expired` → claim inventory through `IInventoryStrategy` (`SeatedInventoryStrategy` inserts tickets, `GeneralAdmissionInventoryStrategy` runs the conditional update + inserts tickets) under the event's shared sale lock, require the event to be `Published`, re-check the per-user cap in the DB (`MaxTicketsPerUser`), then `order.MarkPaid()` + attempt `Succeeded` → outbox `OrderPaid`. Commit.
   - On a unique or GA conflict, an exceeded cap or an event that is no longer on sale → roll back everything of that transaction, then in a new transaction `order.MarkUnfulfillable(reason)` → `RefundPending` + outbox `RefundRequested` (C3). The reason is `seats_unavailable`, `purchase_limit_exceeded`, `event_cancelled` or `event_ended`. A conflict nobody can explain is a bug: 5xx, nothing refunded.
6. **Failed** → only a `Pending` attempt changes (it becomes `Failed`); the order becomes `PaymentFailed` if it was `AwaitingPayment` and no other live attempt can still pay it, otherwise the event is ignored (out-of-order safe: a `Failed` after a `Succeeded` leaves the order `Paid`).
7. Refund confirmations are recorded and ignored.
8. Always return 200 after a committed decision. Return 5xx only when nothing was committed (the provider retries).

Decisions taken from a *status read* (the expiry sweep, below) go through the same handler under the synthetic event id `status:{sessionId}`. A status read carries no captured amount, so if the provider's real webhook later reports another amount for such a session the outcome is `amount_mismatch_after_status`: logged as an error with a metric and flagged for a person, not prevented (limitation; widening `GetStatusAsync` was rejected to keep the port as specified).

`OrderPaid` consumers (idempotent, via outbox, separate receipts so one failing never blocks the other): `OrderPaidInventoryCommitHandler` runs `inventory_commit.lua` (07), `OrderPaidLeaveQueueHandler` leaves the waiting room of every phase of the event (06). Ticket email (09) and dashboard counters (15) hook in later.

## Background services

- **OrderExpiryService** (Worker, leader, every 5 s): for open orders past `ExpiresAt`, first ask `GetStatusAsync` for each session. A succeeded session runs the fulfil path (paid late, or `RefundPending`); a gateway outage leaves the order open until a later pass; otherwise `Expire()` alone, and the order's sessions stay `Pending`. After the orders, the same pass reconciles **attempts**: a session still `Pending` more than `Payments:Expiry:AttemptGrace` (10 min) after its own expiry, whose order is no longer open, is asked again (Succeeded → the same decision table, Failed/Expired → closed). That finds money that reached a session after its order closed, or a lost webhook.
- **RefundWorker** (Worker, leader, every 5 s, 50 per pass): driven by **database state**, not by the outbox. It scans orders and payment attempts in `RefundPending` (partial indexes `ix_orders_refund_pending`, `ix_payment_attempts_refund_pending`), oldest first, and calls `RefundAsync` → `Refunded` (+ `OrderRefunded` for orders). Order-level refunds use idempotency key = order id; **session-level** refunds (a duplicate or unusable payment on a session) use key `attempt:{attemptId}` and the amount recorded when the refund was requested. A *declined* refund backs off exponentially (`Refunds:RetryBaseDelay` 30 s doubling up to `RetryMaxDelay` 1 h) and is **parked** after `Refunds:MaxDeclines` (8) declines (`refund_needs_attention`); missing data (no succeeded session, several succeeded sessions, no recorded amount, unknown provider) parks at once. A gateway outage is retried on the next pass and never counted. A parked refund stays `RefundPending` and is never dropped; gauge `ticketrush.payments.refunds_parked`. Runbook: `docs/runbooks/parked-refunds.md`. Safe on two nodes at once (idempotency key + `xmin`).
- **EventCancelled handler**: moves every `Paid` order of the event to `RefundPending` in batches of 500 and voids its tickets. Each batch is one transaction under the exclusive event sale lock: status change, ticket void (checked-in tickets too) and GA `sold_count` release commit together, so the RefundWorker never refunds an order with live tickets. Open orders: `Pending` → `Cancelled`; `AwaitingPayment`/`PaymentFailed` are left to the OrderExpiryService (it asks every session at `ExpiresAt`; a success goes to `RefundPending` `event_cancelled`, otherwise `Expired`). Settling needs only the database, so it runs even when the Redis clean-up fails; a leader-elected worker pass (`Payments:CancelledEvents`) settles cancelled events whose message was dead-lettered. The organizer's cancel takes the same lock exclusively before the status update, so an in-flight fulfilment commits first and a later one reads `Cancelled` (a cancel that waits past the lock timeout answers 409 `event.cancel_busy`).

## Frontend

- Seats page → **`/events/[slug]/checkout`** (buyer step): reads the live hold, shows seats/GA and the hold countdown, a form (name, email, phone) prefilled from the profile. "Tiếp tục" calls `POST /orders` (accepts 201 and 200) and goes to `/checkout/[orderId]`. An order keeps its buyer, hence the extra step before the order exists.
- **`/checkout/[orderId]`**: order summary (event, seats grouped by section, total in large type), buyer shown read-only, terms checkbox, hold countdown pinned at the top, "Thanh toán" button (disabled + spinner while pending, sends a fresh `Idempotency-Key` per request it means, reused on retry), "Huỷ đơn" while the order is `Pending`. Orders that are no longer payable are handed to the result page.
- **`/checkout/[orderId]/result`**: polls `GET /orders/{id}` every 1.5 s (max 90 s; SignalR is not used) and shows:
  - *Đang xác nhận* — "Đừng đóng trang"
  - *Thành công* — subtle confetti (not with reduced motion), "Xem vé" to `/account/orders` (the tickets page is spec 10)
  - *Thất bại* — "Thử lại" if the hold is still valid (judged on server time)
  - *Hết hạn* — back to seats
  - *Đang hoàn tiền* — honest explanation per refund reason (`seats_unavailable`, `event_cancelled`, `event_ended`, `purchase_limit_exceeded`, `order_closed`, `amount_mismatch`, `duplicate_payment`)
- **`/account/orders`** (list, keyset, "Xem thêm") and **`/account/orders/[orderId]`** (status timeline, items, buyer, actions). The api records no timestamps for failure, cancellation or refund, so those timeline steps carry no time.
- **`/mock-gateway/[sessionId]`**: see above; 404 when the mock gateway is disabled.

## Acceptance criteria

All eight are covered by tests that need Docker (Testcontainers: Postgres 17 + Redis 7.4), so none was ticked from a machine without it; "covered" below means the named test exists and builds. Mark each `[x]` once the integration suite has run green in CI.

- [ ] Double-clicking "Thanh toán" (two requests, same key) creates one attempt and returns the same `redirectUrl`. — `PayOrderApiTests.A_double_click_with_the_same_key_creates_one_attempt_and_one_url`, also `Different_keys_at_the_same_moment_still_make_one_attempt_and_one_url`
- [ ] Sending the same webhook 5 times concurrently issues tickets once. — `PaymentWebhookTests.The_same_webhook_five_times_at_once_issues_tickets_once`
- [ ] A `Failed` webhook arriving after `Succeeded` leaves the order `Paid`. — `PaymentWebhookTests.A_failed_webhook_after_success_leaves_the_order_paid`
- [ ] A late success after the seat was resold → `RefundPending` → `Refunded`, never two tickets for one seat. — `PaymentWebhookTests.A_late_success_after_the_seat_was_resold_goes_to_refund_and_leaves_no_claim_behind` (decision) and `OrderExpiryServiceTests.A_late_success_after_the_seat_was_resold_goes_to_refund_and_the_refund_worker_returns_the_money_once` (through to `Refunded`)
- [ ] A late success when the seat is still free → `Paid` with tickets, and Redis `sold` is updated. — `OrderExpiryServiceTests.A_late_success_with_the_seats_still_free_is_paid_through_the_webhook_path_and_never_expired`, `PaymentWebhookTests.A_late_success_after_expiry_with_the_seats_still_free_pays_the_order`, and for Redis `OrderPaidConsumersTests.A_late_success_with_the_seat_still_free_is_paid_and_the_outbox_marks_the_seat_sold_and_empties_the_queue`
- [ ] A forged signature or a timestamp older than 5 min → 401, nothing changes. — `PaymentWebhookTests.A_webhook_that_fails_the_signature_check_is_401_and_changes_nothing` (forged, tampered body, six minutes old or ahead, missing, malformed), `WebhookSignatureTests`
- [ ] A gateway outage opens the circuit breaker. Users see `payment.gateway_unavailable` and holds are untouched. — `ResilientPaymentGatewayTests.The_breaker_opens_at_the_failure_ratio_and_then_fails_fast_without_calling_the_gateway`, `PayOrderApiTests.A_gateway_outage_is_503_and_leaves_the_order_pending_and_the_hold_alive_then_recovers`
- [ ] After a sale load test: `count(tickets per seat) ≤ 1` and `sold_count ≤ capacity` for every tier (SQL invariant check). — automated: `SaleInvariantTests.A_concurrent_sale_with_contention_duplicates_and_late_payments_never_oversells` (40 concurrent buyers over 8 hot seats and a 6-ticket GA tier, double clicks, duplicate and late webhooks, resale, expiry sweep, refunds, then `load-tests/sql/sale-invariants.sql` must return zero rows) and `SaleInvariantTests.The_invariant_query_reports_every_kind_of_planted_corruption` (the query is not vacuous). The k6 run (`load-tests/sale.js` + `load-tests/check-sale-invariants.sh`, see `load-tests/README.md`) needs the compose stack and has **not** been run yet.

## Implementation notes (what differs from the text above)

- Idempotency reuses the Redis filter of 07 (see above); there is no `idempotency_records` table.
- `VerifyAndParse(WebhookHeaders, string)` replaces the `HttpRequest` parameter.
- Refunds are DB-state-driven with backoff and parking, and have an order level and a session level; there is no `RefundRequested`-outbox consumer doing the actual refund.
- The expiry service also reconciles stale attempts; status-read decisions are recorded under `status:{sessionId}`.
- `processed_webhooks` is keyed `(provider, provider_event_id)`.
- The sold-seat source of the inventory reconciler (07) is now the database (`DbSoldInventorySource`: non-voided tickets), not the empty placeholder.
- The inventory commit is idempotent per order (`committed:<orderId>` marker, 7-day TTL; see 07). A reconcile racing the asynchronous commit can leave `bought` drifted for at most one reconcile interval (tracked follow-up: subtract paid orders whose `OrderPaid` message is still pending).
- Known limits: `amount_mismatch_after_status` is detected, not prevented; releasing a hold while an order is `AwaitingPayment` is allowed (a later payment then meets the unique index and is refunded); the order response has no event slug or title, so `/checkout/[orderId]` remembers the event of the buyer step in local storage and deep links in other browsers fall back to generic links; the mock-gateway control surface and its 3-minute delay are for dev and demo only.
