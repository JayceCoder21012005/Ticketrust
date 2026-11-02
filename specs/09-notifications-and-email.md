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
| Waitlist offer (13) | ✓ | ✓ |
| Ticket transferred / resold (14) | ✓ | ✓ |
| Event cancelled | ✓ | ✓ |

- `IEmailSender` → MailKit SMTP (Mailpit locally). Templates are Scriban `.html` files with one shared layout, Vietnamese copy, inline CSS, a plain-text alternative, and no remote images except the logo.
- In-app push: SignalR hub `/hubs/user`, group `user:{userId}`, message `Notification`. 08's result page also receives `OrderStatusChanged` here.
- **Follow/reminders**: `POST/DELETE /events/{id}/follow`. **ReminderScheduler** (leader, every 1 min) finds follows whose sale opens in 30 min and emits `ReminderDue` once per (user, event, kind). A unique key prevents duplicates.

## API

| Method | Route | Notes |
|---|---|---|
| GET | `/me/notifications?cursor=` | Keyset list + `unreadCount` |
| POST | `/me/notifications/{id}/read`, `/me/notifications/read-all` | — |
| POST/DELETE | `/events/{id}/follow` | Idempotent |

## Frontend

- Bell icon in `SiteHeader` with an unread badge, `Popover` list (latest 10), "Xem tất cả" → `/account/notifications`.
- New notifications arrive live via `/hubs/user` and show as a `sonner` toast.
- "Nhắc tôi" button on event pages toggles follow, optimistic with rollback (safe: no inventory or money involved).

## Acceptance criteria

- [ ] Replaying the same `OrderPaid` message sends one email and creates one in-app notification.
- [ ] An SMTP outage delays emails (retry) but doesn't block other notification types.
- [ ] A reminder is sent once even if the scheduler runs on two instances.
- [ ] Emails render correctly in Mailpit (HTML + text) with Vietnamese diacritics.
