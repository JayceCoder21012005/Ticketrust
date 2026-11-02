# 10 — E-tickets & Rotating QR

**Goal:** buyers see their tickets in a wallet, and each ticket shows a QR that **changes every 15 seconds**, so screenshots and copies are worthless (anti-scalping, the SafeTix / DICE approach).

**Depends on:** 08, 09

## QR format

```
TR1.<ticketId base64url>.<step>.<sig>
step = floor(unixSeconds / 15)
sig  = base64url( HMAC-SHA256( QrSecret, ticketId + "|" + step )[0..10] )
```

- `QrSecret` is per ticket (created in 08, encrypted at rest with Data Protection).
- The **client** computes the code with Web Crypto, so it works offline at the venue once the key is cached.
- The gate (11) accepts `step` within ±1 (tolerates 15 s of clock drift). A new secret is created whenever ownership changes (14).
- `QrCodeService` (Infrastructure) owns encode/verify. It is the single place for the format, shared by 11 and the tests.

## API (owner only, 404 otherwise)

| Method | Route | Notes |
|---|---|---|
| GET | `/me/tickets?scope=upcoming\|past` | Grouped by event: event summary, tickets (tier, section/row/seat, status) |
| GET | `/tickets/{id}` | Detail |
| GET | `/tickets/{id}/qr-key` | `{ticketId, secret, stepSeconds: 15, format: "TR1"}`. Only when status `Valid` and the event has not ended. `Cache-Control: no-store`, rate limited (10/min) |

Error codes: `ticket.not_found`, `ticket.not_displayable` (checked in, listed or voided).

## Frontend

- `/account/tickets`: tabs *Sắp diễn ra* / *Đã qua*. Cards with cover, date, venue and ticket count. Empty state links to the catalog.
- `/tickets/[id]` (the "wallet" screen, mobile-first):
  - QR (generated client-side with a small lib, dynamically imported), regenerated every 15 s with a circular progress ring.
  - An animated gradient band and a live clock under the QR, so staff can spot a static screenshot.
  - Seat info in large type (Khu / Hàng / Ghế), tier, holder name, event date + venue, "Thêm vào lịch", tip "Tăng độ sáng màn hình".
  - Status overlays: *Đã check-in lúc 19:42*, *Đang rao bán* (QR hidden), *Đã huỷ*.
  - Swipe/arrow navigation between tickets of the same order.
- Offline: `qr-key` responses are cached in IndexedDB (per ticket, deleted after the event ends). A small service worker caches the wallet route shell so it opens without signal.
- `prefers-reduced-motion` turns off the gradient animation but keeps the clock.

## Acceptance criteria

- [ ] A code generated at step N verifies at N−1, N, N+1 and fails at N+2 (unit tests on `QrCodeService`, shared test vectors with the TypeScript implementation).
- [ ] The TypeScript generator and the C# verifier agree on 1,000 random vectors.
- [ ] Another user's ticket id → 404 on all three endpoints.
- [ ] With the network off, a previously opened ticket still shows a valid rotating QR.
- [ ] `qr-key` is refused for checked-in, listed and voided tickets.
