# 10 — E-tickets & Rotating QR

**Goal:** buyers see their tickets in a wallet, and each ticket shows a QR that **changes every 15 seconds**, so screenshots and copies are worthless (anti-scalping, the SafeTix / DICE approach).

**Depends on:** 08, 09

## QR format

```
TR1.<ticketId base64url>.<step>.<sig>
step = floor(unixSeconds / 15)
sig  = base64url( HMAC-SHA256( QrSecret, ticketId + "|" + step )[0..10] )
```

The text above left three details open. They are fixed here, and the shared vectors (`shared/fixtures/qr-vectors.json`) pin them for both implementations:

- `ticketId` in the code is the 16 bytes of the Guid in **network (big-endian) byte order** (`Guid.ToByteArray(bigEndian: true)`), base64url without padding: 22 characters.
- The `ticketId` inside the HMAC input is the **canonical lowercase hyphenated** form (`D` format, for example `0192f0e0-aaaa-7bbb-8ccc-0123456789ab`), UTF-8 encoded.
- `step` is a plain decimal integer: no padding, no leading zeros (`0` for step 0). `sig` is 10 bytes, 14 base64url characters.

A code is about 50 characters. A verifier compares signatures in constant time and accepts `|step - currentStep| <= 1`.

- `QrSecret` is per ticket (created in 08, encrypted at rest with Data Protection).
- The **client** computes the code with Web Crypto, so it works offline at the venue once the key is cached.
- The gate (11) accepts `step` within ±1 (tolerates 15 s of clock drift). A new secret is created whenever ownership changes (14).
- `QrCodeService` (Infrastructure, behind `IQrCodeService` in Application) owns encode/verify. It is the single place for the format, shared by 11 and the tests.

## API (owner only, 404 otherwise)

Every route requires a signed-in user, and every response, errors included, carries `Cache-Control: no-store`.

| Method | Route | Notes |
|---|---|---|
| GET | `/me/tickets?scope=upcoming\|past` | `{events: [...]}` grouped by event: `eventId, slug, title, coverImageUrl, startsAt, endsAt, venueName, city, status, tickets[]`. A ticket is `{id, orderId, tierName, seat {section,row,number} \| null, status, holderName}` (`seat` is null for general admission). `scope` defaults to `upcoming`; any other value is 400 `ticket.invalid_scope`. Upcoming = the event has not ended and is not cancelled, and the ticket is not voided, sorted by event start ascending; past = everything else the user owns, descending. At most 200 tickets |
| GET | `/tickets/{id}` | `{id, orderId, tierName, seat, status, holderName, event {eventId, slug, title, coverImageUrl, startsAt, endsAt, venueName, city, status}, orderTicketIds[]}`. `orderTicketIds` lists the caller's tickets of the same order, for sibling navigation |
| GET | `/tickets/{id}/qr-key` | `{ticketId, secret, stepSeconds: 15, format: "TR1"}`, with `secret` as standard base64 of the 32 bytes. Only when the ticket is `Valid`, its event is `Published` and its `endsAt` is in the future. Rate limited to 10 requests per minute per user |

Error codes:

- `ticket.not_found` (404): unknown id, and also another user's ticket, so ids cannot be probed.
- `ticket.not_displayable` (409): a checked-in, listed or voided ticket, or an event that has ended, was cancelled or is not published.
- `ticket.invalid_scope` (400).
- `ticket.qr_key_rate_limited` (429): carries a `Retry-After` header in seconds, and `retryAfterSeconds` in the problem body.
- `system.busy` (503): the throttle store (Redis) is unreachable; `qr-key` fails closed.

`qr-key` checks the throttle before it reads the database, so a 404 or a 409 also uses one of the 10 slots of the minute.

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

- [x] A code generated at step N verifies at N−1, N, N+1 and fails at N+2 (unit tests on `QrCodeService`, shared test vectors with the TypeScript implementation). — `QrCodeServiceTests.Code_verifies_at_the_previous_current_and_next_step` and `QrCodeServiceTests.Code_fails_two_steps_away` (the window is checked at both ends of each step, and N−2 and N+2 fail), plus `QrKeyApiTests.A_code_built_from_the_returned_key_verifies_with_QrCodeService`
- [x] The TypeScript generator and the C# verifier agree on 1,000 random vectors. — `QrCodeServiceTests.Vectors_file_verifies_every_case` (C# encodes, parses and verifies all 1,000 cases of `shared/fixtures/qr-vectors.json`) and `qr-code.test.ts` `Generator_matches_all_1000_shared_vectors` (the Web Crypto generator reproduces every code exactly); `QrCodeServiceTests.Vectors_file_is_reproducible_from_its_seed` keeps the file honest
- [x] Another user's ticket id → 404 on all three endpoints. — `TicketApiTests.Another_users_ticket_is_404_on_detail_and_qr_key_and_list_excludes_it` (detail and `qr-key` answer 404 `ticket.not_found`; the list leaves the ticket out for both scopes), with `TicketApiTests.Unknown_ticket_id_is_404_with_the_same_body` showing the answer is the same as for an unknown id
- [x] With the network off, a previously opened ticket still shows a valid rotating QR. — Playwright `web/e2e/tickets-offline.spec.ts`: `a ticket opened online keeps showing a valid rotating QR with the network off` (the context is offline, the reload is served by the service worker, and the drawn QR equals what the app's own generator makes at step N and N+1), with `logout clears the keys, the ticket cache and the worker; the next user sees none of it`, `another user signing in on the login page never gets the previous user's offline ticket` and `a ticket whose event has ended shows the ended state offline, and is purged` for what must not work offline
- [x] `qr-key` is refused for checked-in, listed and voided tickets. — `QrKeyApiTests.Qr_key_is_refused_for_checked_in_listed_and_voided_tickets` (409 `ticket.not_displayable` for each status), with `QrKeyApiTests.Qr_key_is_refused_after_the_event_ended_or_was_cancelled`

## Implementation notes (what differs from the text above)

- **In-repo QR encoder.** The wallet draws the code with `web/src/lib/tickets/qr-matrix.ts` (byte mode, error correction level M, versions 1 to 10, all eight masks scored) instead of a library, because a new package would have changed `web/package-lock.json`. It is loaded with a dynamic `import()` by the wallet only. How it was validated: a throwaway Python environment (not committed) rendered the matrices the encoder produces and decoded them with zxing-cpp, which read **1049 of 1049** symbols back to the exact payload (all 1,000 shared-vector codes, the capacity boundary of every version, short strings and Vietnamese UTF-8). python-qrcode, forced to the same version and mask, produced **bit-identical** matrices for the same 1049 inputs, which shows that the codewords, error correction, placement and format/version bits are exact, not merely readable by a tolerant decoder. Penalty rule 3 is implemented differently from library to library, so another library may pick another mask for the same payload; every mask gives a valid symbol. Eighteen matrices (versions 1 to 10) are committed in `web/src/lib/tickets/fixtures/qr-reference.json` and compared exactly by `qr-matrix.test.ts`.
- **Throttle.** The `qr-key` limit of 10 per minute is a Redis fixed-window counter (`IQrKeyThrottle`, `RedisQrKeyThrottle`, key `tr:{env}:qr-key:{userId:N}`, one Lua script doing INCR and PEXPIRE) and fails closed. It is a stop-gap: spec 12's named policy `qr-key` supersedes it. Refused requests do not extend the window.
- **Check-in time.** The overlay for a checked-in ticket says "Đã check-in" without a time. The response has no `checkedInAt`, because check-in belongs to spec 11; spec 11 adds the field and the time in the overlay.
- **Owner-scoped queries.** Every ticket read goes through one owner-scoped query (`TicketQueries.Owned`). The secret is read by a separate owner-scoped query and is never part of the list or detail projections.
- **Offline model.** After a successful online `qr-key` the wallet stores the key (`{secret, endsAt}`) and the ticket detail in IndexedDB (database `ticketrush-tickets`, stores `keys`, `tickets`, `meta`). When the network fails, and only then, it renders the ticket from the cache and keeps rotating the code from the cached secret on the last known server-corrected clock (the offset is kept for 7 days). It retries the network in the background and replaces the cached key as soon as it can. A 404 or 409 deletes the cached key and detail, and an event that has ended purges them. Sign-out, a change of user and a refused session refresh wipe the cache, the service worker's caches and its registration.
- **Service worker.** `web/public/sw.js` (served `no-cache`, scope `/`) is registered only by `/account/tickets` and the wallet. It caches only GET navigations to `/tickets/<uuid>` (network first; the stored HTML is used when the network fails or takes more than 3 s; the HTML holds no user data) and same-origin `/_next/static/*` (cache first). It never caches `/api/*`, `/hubs/*`, other pages or other origins.
- **Limits of the offline wallet.**
  - A ticket works offline only if it was opened online on this device first. Moving to a sibling ticket offline works only if that sibling was opened online too.
  - The `qr-key` secret is stored in IndexedDB as the base64 the API sent, without client-side encryption. It is same-origin storage; an XSS could read it, but an XSS could also read the secret from memory while the wallet is open. Hardening idea: import the secret once as a non-extractable HMAC `CryptoKey` and store that instead.
  - Offline, with no session, the wallet trusts the device's last signed-in owner (the `tr.wallet.owner` marker in `localStorage`, matched against the cache's owner record), because there is no server to ask. If a session ends without a sign-out, and another user signs in but never opens a ticket screen before going offline, the first user's cached ticket could still open for them. Any session start of another user, a sign-out and a refused session refresh wipe the cache.
  - A ticket's secret is created once, at issue (spec 08). Nothing rotates it yet; spec 14 creates a new ticket with a new secret on every ownership change.
- See `docs/runbooks/ticket-qr-keys.md` for how the secrets are stored (the Data Protection key ring), the throttle, clearing a device's offline cache and adding a gate check.
