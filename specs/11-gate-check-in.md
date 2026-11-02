# 11 — Gate Check-in

**Goal:** gate staff scan a ticket QR on their phone and get a clear green/red answer in under 300 ms. A ticket can never be admitted twice.

**Depends on:** 10

## Domain

- `GateAssignment(eventId, userId, gateName)`: the organizer assigns `GateStaff` users to an event.
- `CheckIn(id, ticketId, eventId, staffUserId, gateName, scannedAt, result, reason)`: a log of **every** scan, valid or not.
- Ticket transition `Valid → CheckedIn` through `ticket.CheckIn(staff, at)`.

## API

| Method | Route | Auth | Notes |
|---|---|---|---|
| POST/DELETE | `/organizer/events/{id}/gate-staff` | organizer | `{email, gateName}` |
| GET | `/gate/events` | gate staff | Events assigned to me (today first) |
| POST | `/gate/events/{eventId}/scan` | gate staff + assigned | `{payload}` → `{result, reason?, ticket?: {tier, section, row, seat, holderName}, checkedInAt?}` |
| GET | `/gate/events/{eventId}/stats` | gate staff | `{checkedIn, total, lastMinute}` |

`result`: `Admitted`, `AlreadyCheckedIn` (+ time and gate of the first scan), `Invalid` (bad format or signature), `Expired` (step outside ±1), `WrongEvent`, `NotValid` (voided or listed).

## Scan rules

1. Parse and verify with `QrCodeService` (10). Format and signature are checked **before** any DB access.
2. Atomic transition: `UPDATE tickets SET status='CheckedIn', checked_in_at=@now WHERE id=@id AND event_id=@evt AND status='Valid'`. 1 row → `Admitted`. 0 rows → read the ticket to explain why.
3. Always insert a `CheckIn` log row (same transaction for `Admitted`).
4. The same payload scanned twice in 3 s by the same staff returns the first result (camera double-read), not `AlreadyCheckedIn`.
5. Rate limit per staff: 5 scans/s.

## Frontend `/gate/[eventId]` (mobile, full screen)

- Camera via the `BarcodeDetector` API, falling back to a small ZXing build (dynamically imported). Torch toggle, front/back camera switch.
- Result takes the full screen for 1.5 s: **green** "Hợp lệ" + seat in huge type, **amber** "Đã check-in lúc 19:42 – Cổng B", **red** + reason. Plus vibration patterns and distinct sounds.
- Manual entry fallback (ticket code typed in) for broken screens.
- Header: gate name, live counter `1.234 / 5.000`, connection indicator. When offline: a "Mất kết nối" banner. Offline scanning is out of scope for v1.
- Screen Wake Lock while the page is open.

## Acceptance criteria

- [ ] Two staff scanning the same ticket at the same moment → exactly one `Admitted`, one `AlreadyCheckedIn`.
- [ ] A screenshot older than 30 s → `Expired`.
- [ ] A ticket from another event → `WrongEvent`. Staff not assigned to the event → 404.
- [ ] Every scan (including failures) appears in the `CheckIn` log.
- [ ] Scan endpoint p95 < 100 ms server-side with 50 scans/s.
