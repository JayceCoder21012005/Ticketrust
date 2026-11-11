# Notifications & Email — Design (spec 09)

Source of truth for behaviour: `specs/09-notifications-and-email.md`. This document only fixes **scope and build decisions**.

## Intent

Every notification is created by an idempotent outbox handler (C8), so a replayed or crashed message never produces two in-app rows or two emails. An SMTP outage delays email only.

## What already exists (do not rebuild)

- Outbox + `IIntegrationEventHandler<T>` + `AddIntegrationEventHandler` + `consumer_receipts` (a handler's DB writes commit atomically with its receipt).
- `IEmailSender` / `SmtpEmailSender` (MailKit, stable `Message-ID`), `EmailOptions`, Mailpit in AppHost, `CapturingEmailSender` in the API test factory.
- `SendConfirmationEmailHandler` / `SendPasswordResetEmailHandler` (inline HTML, token generated at send time).
- Domain events: `OrderPaid`, `OrderExpired`, `RefundRequested`, `OrderRefunded`, `PaymentRefunded`, `EventCancelled`, `UserRegistered`, `PasswordResetRequested`.
- `EventsHub` pattern (Infrastructure, Redis backplane, Worker sends through the same hub type), `access_token` query on `/hubs/*`, `LeaderElectedBackgroundService`, web SignalR client pattern in `web/src/lib/seats/realtime.ts`, `sonner` installed.

## Scope

**In:** `notifications`, `event_follows`, `reminder_dispatches`, `email_deliveries` tables; Scriban template renderer + layout + the 8 templates this spec can trigger today; handlers for order/refund/expired/event-cancelled; email delivery worker; `/hubs/user`; `OrderStatusChanged` push; REST API (list, read, read-all, follow); ReminderScheduler; web bell, popover, `/account/notifications`, toast, "Nhắc tôi".

**Out (later specs plug in via the seam below):** waitlist offer (13), ticket transferred/resold (14). Their rows in the spec table are done when those specs add: one enum value, one template pair, one handler. No code for them now.

## Architecture

```
domain event ─outbox─▶ handler ──(one tx)──▶ notifications row  ──▶ push via /hubs/user (best effort)
                                         └─▶ email_deliveries row ──▶ EmailDeliveryService (worker) ──▶ IEmailSender
```

- **`INotificationWriter`** (Application port) `AddAsync(NotificationDraft)`: inserts the `Notification` and, when the draft has an email, an `EmailDelivery`, both with `dedupe_key` (unique index). A duplicate key is swallowed. Handlers never talk to SMTP.
- **Why a delivery table instead of sending inside the handler:** the outbox retries a whole message and blocks later messages of the same aggregate (`NOT EXISTS older` in the claim query). Sending inline would let one SMTP outage stall an order's in-app notices and make a crash-after-send resend the email. With the table, the handler always commits, and `EmailDeliveryService` owns retry (`OutboxRetryPolicy` backoff, `FOR UPDATE SKIP LOCKED`, dead-letter after `MaxAttempts`). This is what makes acceptance #2 true.
- Account emails (confirm, reset) keep sending directly from their handlers: the token must not be stored. They move to Scriban templates only.
- Push is sent by the handler after the insert, inside the batch transaction. If the transaction later rolls back the client may have shown a toast for a row that never existed; the next list fetch corrects it. Accepted (rare, harmless).

## Data

| Table | Columns / constraints |
|---|---|
| `notifications` | `id`, `user_id`, `type` (string enum), `title`, `body`, `link`, `read_at`, `created_at`, `dedupe_key` UNIQUE. Index `(user_id, created_at DESC, id DESC)` for keyset, partial index `(user_id) WHERE read_at IS NULL` for the badge |
| `email_deliveries` | `id`, `dedupe_key` UNIQUE, `user_id`, `to_address`, `template`, `model` (jsonb), `status` (Pending/Sent/DeadLettered), `attempts`, `next_attempt_at`, `last_error`, `sent_at`. Index on `(next_attempt_at) WHERE status='Pending'` |
| `event_follows` | PK `(user_id, event_id)`, `created_at` |
| `reminder_dispatches` | PK `(user_id, event_id, kind)`, `created_at`. `kind` = `SaleOpens30m` \| `EventStarts24h` |

`dedupe_key` shapes: `order-paid:{orderId}`, `refund-requested:{orderId}`, `order-refunded:{orderId}`, `payment-refunded:{attemptId}`, `order-expired:{orderId}`, `event-cancelled:{eventId}:{userId}`, `reminder:{kind}:{eventId}:{userId}`. Keys come from the business entity, not the message id, so even a re-emitted event cannot duplicate.

## Trigger mapping (this spec)

| Event | In-app | Email template |
|---|---|---|
| `UserRegistered` | — | `confirm-email` (3-day link, existing handler) |
| `PasswordResetRequested` | — | `reset-password` (1 h, existing handler) |
| `OrderPaid` | ✓ | `order-paid` — link to `/account/orders/{id}`, **no QR** |
| `RefundRequested` | ✓ | `refund-requested` |
| `OrderRefunded`, `PaymentRefunded` | ✓ | `refunded` |
| `OrderExpired` | ✓ | — (not in the spec table; 08's event doc says "tell the buyer") |
| `EventCancelled` | ✓ | `event-cancelled` to every holder of a Paid/RefundPending/Refunded order + every follower |
| `ReminderDue` (`SaleOpens30m`, `EventStarts24h`) | ✓ | `reminder-sale-opens`, `reminder-event-starts` |

## Reminders

`ReminderSchedulerService` (leader lock, 1 min tick). Per tick, in a transaction: for each published event, followers whose event has a phase with `opens_at ∈ (now, now+30 min]` → `SaleOpens30m`; followers of events with `starts_at ∈ (now, now+24 h]` → `EventStarts24h`. It inserts `ReminderDispatch` rows (`ON CONFLICT DO NOTHING`); only rows actually inserted raise `ReminderDue` through the aggregate, so two instances (or a lost lock mid-tick) cannot send twice. Late catch-up: a follow created after the window opened still gets the reminder if the moment hasn't passed. A sale that opens in under 30 min at follow time is reminded on the next tick (documented, not special-cased).

## API (all `RequireAuthorization`; foreign data → 404)

`GET /me/notifications?cursor=&limit=` → `{items, nextCursor, unreadCount}`; `POST /me/notifications/{id}/read`; `POST /me/notifications/read-all`; `POST|DELETE /events/{id}/follow` (idempotent, 204; follow requires a Published event, else 404/409 `event.not_followable`); `GET /events/{id}/follow` → `{following}` (**added to the spec**: the button needs its initial state without putting per-user data in the cached public event payload).

## Realtime

`UserHub` at `/hubs/user` in Infrastructure (same reason as `EventsHub`). `OnConnectedAsync` adds the connection to `user:{sub}`; clients never choose a group. Methods: `Notification` (payload = the list item) and `OrderStatusChanged {orderId, status}`. `OrderStatusChanged` is sent by the same handlers on `OrderPaid`/`OrderExpired`/`OrderRefunded`/`RefundRequested`; the result page uses it to react immediately, **polling stays as the fallback** (spec 08 behaviour unchanged if the hub is down).

## Frontend

Bell (`NotificationBell`, client island gated by the session hint like `AccountLink`) → Popover with the latest 10, "Xem tất cả" → `/account/notifications` (keyset infinite list, mark read on click, "Đánh dấu tất cả đã đọc"). `useUserHub` mounted once in the buyer layout: invalidates the notifications query and shows a `sonner` toast. "Nhắc tôi" `FollowButton` on the event page: optimistic with rollback. All copy Vietnamese in `lib/i18n`.

## Constraints

- Templates: one `_layout.html`, inline CSS, `.html` + `.txt` per template, UTF-8, no remote images except the logo (`FrontendOptions.BaseUrl` + `/logo-email.png`), every interpolated value HTML-encoded (Scriban `html.escape`).
- No PII or tokens in logs. Email addresses never appear in log messages or metrics tags.
- Vietnamese diacritics must survive HTML + text parts (tested against a real Mailpit, not only the capturing fake).
- Same conventions as 00: sealed, primary ctors, `TimeProvider`, `CancellationToken`, ≤ 30-line methods.

## My calls (not yet agreed — please check)

1. **Email goes through a delivery table + worker**, not inline in the handler (reasoning above). Costs one table and one background service.
2. **`OrderExpired` gets an in-app notification** although the spec table omits it.
3. **`PaymentRefunded` (session-level refund) is notified like `OrderRefunded`**; `PaymentRefundRequested` is not.
4. **`EventStarts24h` reminders go to followers only**, not to ticket holders (holders get `event-cancelled`, and 10/11 will own "your ticket" reminders).
5. **`GET /events/{id}/follow` added** to the API table.
6. **Account emails stay direct-send** (tokens are generated at send time), only restyled with Scriban.
7. **Scriban version**: newest 6.x/7.x release that is at least 2 weeks old at implementation time; pin in `Directory.Packages.props`.

## Left to the builder

File and class naming inside the layout above, exact template copy (follow spec 02 tone), popover visual design.
