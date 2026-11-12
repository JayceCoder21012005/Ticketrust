# 09 — Notifications & Email

**Goal:** users hear about everything that matters (orders, refunds, reminders, offers) in the app and by email. Every notification is an idempotent outbox handler (outbox from 01), so nothing is lost or sent twice.

**Depends on:** 08

## Notifications

`Notification(id, user_id, type, title, body, link, read_at, created_at)`

| Trigger | In-app | Email |
|---|---|---|
| Registered | — | Confirm email |
| Forgot password | — | Reset link (expires in 1 h) |
| `OrderPaid` | ✓ | Order confirmation + "Xem vé" link (no QR in email; QR rotates, see 10) |
| `RefundRequested` / `Refunded` | ✓ | ✓ |
| Event reminder (follow) | ✓ | ✓ 30 min before sale open, 24 h before the event |
| Waitlist offer (13) | ✓ | ✓ (delivered by spec 13 through the seam below) |
| Ticket transferred / resold (14) | ✓ | ✓ (delivered by spec 14 through the seam below) |
| Event cancelled | ✓ | ✓ |

The waitlist-offer and ticket-transfer rows are not built here. Specs 13 and 14 add them through the seam this spec leaves: a `NotificationType` value, a template pair, a handler that calls `INotificationWriter`, and a dedupe key (see `docs/runbooks/email-delivery.md`, "Add a new notification type").

- `IEmailSender` → MailKit SMTP (Mailpit locally). Templates are Scriban `.html` files with one shared layout, Vietnamese copy, inline CSS, a plain-text alternative, and no remote images except the logo.
- In-app push: SignalR hub `/hubs/user`, group `user:{userId}`, message `Notification`. 08's result page also receives `OrderStatusChanged` here.
- **Follow/reminders**: `POST/DELETE /events/{id}/follow`. **ReminderScheduler** (leader, every 1 min) finds follows whose sale opens in 30 min and emits `ReminderDue` once per (user, event, kind). A unique key prevents duplicates.

## API

| Method | Route | Notes |
|---|---|---|
| GET | `/me/notifications?cursor=` | Keyset list + `unreadCount` |
| POST | `/me/notifications/{id}/read`, `/me/notifications/read-all` | — |
| POST/DELETE | `/events/{id}/follow` | Idempotent |
| GET | `/events/{id}/follow` | `{following}` for the signed-in user. Added by the design doc: the button needs its initial state without per-user data in the cached public event payload |

## Frontend

- Bell icon in `SiteHeader` with an unread badge, `Popover` list (latest 10), "Xem tất cả" → `/account/notifications`.
- New notifications arrive live via `/hubs/user` and show as a `sonner` toast.
- "Nhắc tôi" button on event pages toggles follow, optimistic with rollback (safe: no inventory or money involved).

## Acceptance criteria

- [x] Replaying the same `OrderPaid` message sends one email and creates one in-app notification. — `OrderNotificationTests.Replaying_the_same_OrderPaid_message_creates_one_notification_and_one_email_delivery` (one notice, one `email_deliveries` row, and `EmailDeliveryTests.Delivery_is_sent_once_and_marked_sent` for the one send)
- [x] An SMTP outage delays emails (retry) but doesn't block other notification types. — `EmailDeliveryTests.Smtp_failure_reschedules_with_backoff_and_does_not_touch_other_deliveries`, `EmailDeliveryTests.Backoff_grows_and_dead_letters_after_max_attempts`, `EmailDeliveryTests.A_row_stays_sent_when_a_later_row_fails`, `EmailDeliveryTests.A_send_that_exceeds_the_timeout_is_a_failure_and_the_next_row_is_still_sent`
- [x] A reminder is sent once even if the scheduler runs on two instances. — `ReminderPlannerTests.Two_planners_in_parallel_emit_once` (two planners racing on the same data) and `ReminderPlannerTests.Scheduler_runs_only_on_the_leader`
- [x] Emails render correctly in Mailpit (HTML + text) with Vietnamese diacritics. — `MailpitRenderTests.Every_template_arrives_in_mailpit_with_html_text_and_vietnamese_diacritics` (real `SmtpEmailSender` into a pinned `axllent/mailpit` container, all 8 templates, then the delivery processor run twice leaves one message each), with `EmailRenderingTests` for the renderer alone

## Implementation notes (what differs from the text above)

- Email goes through a delivery table and a worker: a handler writes the notice and an `email_deliveries` row together, and `EmailDeliveryService` sends the rows (one row per transaction, 30 s send timeout, backoff for about a day before dead-lettering, immediate dead-letter for errors retrying cannot fix, an outage breaker, reminders that expire unsent once their moment has passed). Handlers never talk to SMTP. `confirm-email` and `reset-password` are the exception: their outbox handlers still send directly, because the token is created at send time. See `docs/runbooks/email-delivery.md`.
- Reminders: the planner inserts `reminder_dispatches` rows (unique `(user, event, kind)`) and writes the `ReminderDue` outbox messages with a NULL aggregate id, so a fan-out to many followers is not serialised.
- The "Nhắc tôi" follow button is offered in every open event state (not only before the sale).
- `OrderExpired` creates an in-app notice only, no email (the spec table has no row for it).
- `PaymentRefunded` is notified like `OrderRefunded` (the `refunded` template).
- `EventStarts24h` reminders go to followers only, not to ticket holders (holders get `event-cancelled` if the event is cancelled).
- The HTML part leaves Vietnamese letters and `₫` as raw UTF-8; only `& < > " '` are escaped.
