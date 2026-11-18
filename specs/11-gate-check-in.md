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

- [x] Two staff scanning the same ticket at the same moment → exactly one `Admitted`, one `AlreadyCheckedIn`. — `GateApiTests.Two_staff_scanning_the_same_ticket_at_once_admit_it_exactly_once` (five rounds, two staff each, against real Postgres)
- [x] A screenshot older than 30 s → `Expired`. — `GateApiTests.A_screenshot_older_than_30_seconds_is_expired` (the ticket stays `Valid`)
- [x] A ticket from another event → `WrongEvent`. Staff not assigned to the event → 404. — `GateApiTests.A_ticket_of_another_event_is_wrong_event_and_stays_valid`, `GateApiTests.Staff_who_are_not_assigned_get_404_and_nothing_is_logged`
- [x] Every scan (including failures) appears in the `CheckIn` log. — `GateApiTests.Garbage_and_forged_codes_are_invalid_and_still_logged` and the log assertions of the other scan tests. A double read answered from the 3 s shield is the same scan and is not logged twice (`The_same_code_read_twice_in_a_row_gets_the_first_answer_and_one_log_line`)
- [ ] Scan endpoint p95 < 100 ms server-side with 50 scans/s. — **Not measured.** The scan path is an assignment lookup, one Redis round trip, one ticket read and one short transaction (an UPDATE and an INSERT), but no load test was run: that belongs to spec 17 (k6 against the running stack). Note that 50 scans/s needs at least 10 staff, because the limit is 5 scans/s per staff member.

## Implementation notes (what differs from the text above)

- **Where the rules live.** `ScanHandler` (`Application/Features/Gate/Scan`). Order: assignment (404), rate limit and double-read shield (both in Redis, one Lua script `scan_guard.lua`, one round trip), shape of the code (no database), ticket read, signature, event, freshness, then the atomic `UPDATE … WHERE status='Valid'` and the log line in one transaction. When the UPDATE touches no row the handler reads the ticket and answers `AlreadyCheckedIn` (with the time and gate of the first `Admitted` log line) or `NotValid` (`voided` / `listed`).
- **The double-read shield.** The answer to a payload is kept in Redis for 3 s per (staff, SHA-256 of the payload) and returned for the same payload from the same staff member, as the spec says. Such a repeat is the same scan, so it writes no log line. Redis down means the scan endpoint answers 503 `system.busy` (fail closed, like the `qr-key` throttle); losing only the shield (a failed write) is logged and ignored.
- **Rate limit.** 5 scans per second per staff member, a fixed one-second window; 429 `rate_limited` with `Retry-After`. Spec 12 will give it a named policy.
- **Wrong signature versus unknown ticket.** Both answer `Invalid`; the `reason` (`bad_signature`, `unknown_ticket`, `bad_format`) goes in the response and the log. A log line for a code that did not prove a ticket has no `ticket_id`.
- **Wrong event.** Checked after the signature and before freshness: a genuine ticket of another event is `WrongEvent` even when its code is old.
- **Staff management.** `POST` and `DELETE /organizer/events/{id}/gate-staff` are idempotent; assigning an assigned user renames the gate; removing keeps the past log. The email must belong to an account with the `GateStaff` role (404 `gate.staff_not_found` otherwise). An organizer only manages their own events (404 for another's). `DELETE` takes `{email}` in the body.
- **Stats.** `checkedIn` and `total` count the event's tickets that are not voided (`total` counts `Valid`, `Listed` and `CheckedIn`); `lastMinute` counts `Admitted` log lines of the last 60 s. The screen polls every 5 s and refreshes at once after an admission.
- **Camera and ZXing.** `@zxing/library` is a dependency (a new entry in `web/package-lock.json`, added on purpose). It is loaded with a dynamic `import()` by the scanner only, and only when the browser has no `BarcodeDetector` that reads QR codes (Safari, Firefox); the public bundles are untouched. `qr-decoder.test.ts` has ZXing decode the QR codes the wallet draws (the in-repo encoder of spec 10), at phone size and small and low-contrast.
- **Manual entry** takes the same text a scan would read (`TR1.…`): the answer is the same as for a scan. The wallet does not show a typeable code, so this covers a code received as text or a camera that cannot read, not a buyer whose screen is broken; that case needs a lookup by ticket id, which the spec does not define.
- **Scanner screen.** The camera keeps reading while a code is in front of it, so the same code is sent once (client-side filter, 1.5 s quiet time) and the screen stays calm; the server shield is the second line. While the result is on screen nothing is read. Failures are red and say the ticket was **not** checked, so a failed request can never look like an admission.
- **Not done.** Offline scanning (out of scope for v1); the ticket wallet still shows "Đã check-in" without a time (the tickets API has no `checkedInAt` yet, although the column exists); a screen for the organizer to manage gate staff (only the API); the load test above.
