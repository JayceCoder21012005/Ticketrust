# E-tickets & Rotating QR Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Owners see their tickets in a wallet where each ticket shows a QR that changes every 15 s and keeps rotating offline (spec 10).

**Architecture:** A pure `QrCodeService` pins the `TR1` format (shared vectors with the TypeScript generator); a small owner-only tickets API hands out the per-ticket key; the web app generates the code with Web Crypto, renders it with an in-repo QR encoder, caches the key in IndexedDB and opens the wallet shell offline through a service worker.

**Tech Stack:** .NET 10, EF Core/Npgsql, Redis Lua, xUnit v3, Testcontainers; Next.js 16, React 19, TanStack Query, Vitest, Playwright.

**Spec:** `specs/10-e-tickets-and-rotating-qr.md` (binding). Design with the pinned format and the rulings: `docs/superpowers/specs/2026-11-12-e-tickets-and-rotating-qr-design.md` (read it first: it overrides ambiguous spec text).

## Global Constraints

- Spec 00 rules: sealed, primary constructors, `TimeProvider`, `CancellationToken ct` everywhere, methods ≤ 30 lines, `TreatWarningsAsErrors`, Application references no provider; API: ProblemDetails with `code`, ownership 404, OpenAPI regenerated and the typed client updated; Redis writes fail closed (503 `system.busy`).
- QR format exactly as the design: `TR1.<ticketId b64url of Guid.ToByteArray(bigEndian: true)>.<step>.<sig b64url of HMAC-SHA256(secret, UTF8(lowercase-D-guid + "|" + step))[0..10]>`, `step = floor(unixSeconds / 15)`, gate tolerance ±1.
- Error codes: `ticket.not_found` (404), `ticket.not_displayable` (409); `qr-key` 10/min/user → 429 with `Retry-After`; `Cache-Control: no-store` on the three ticket endpoints; the secret appears only in the `qr-key` body.
- Web: no new dependency; `web/package-lock.json` is NEVER staged (not even if modified); Vietnamese copy with diacritics; axe clean in light and dark; keyboard accessible; loading, empty and error states; `prefers-reduced-motion` honoured; the 170 KB public-page JS budget must not move (wallet/QR code loads on buyer routes only).
- TDD per task. Run Api tests with `-- --max-threads 2`; builds with NuGet audit ON (no `-p:NuGetAudit=false`); web tests from `web/` (`npm test`, `npm run lint`, `npm run typecheck`, `ENABLE_DESIGN_PAGE=1 npm run build:e2e` then `npx playwright test --project=chromium`).
- Branch `feat/10-e-tickets-and-rotating-qr` is stacked on `feat/09-notifications-and-email`. Never `git add -f` under `.superpowers/`, never `git stash`, never push. Commit messages end with `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`.

## Review Focus

1. Another user's ticket id → 404 on all three endpoints, and unknown ids look identical (T2).
2. `qr-key` refused for CheckedIn, Listed, Voided tickets and for ended/cancelled events; allowed only for Valid + running event (T2).
3. Step window: valid at N−1, N, N+1; invalid at N−2 and N+2; wrong prefix, truncated, oversize, non-numeric or padded step, uppercase/odd base64url, tampered sig — all rejected without exceptions (T1).
4. The QR encoder output is decodable by a real QR decoder for the actual payload lengths (T3).
5. Offline: after one online visit the wallet opens with the network off and the QR still rotates; the cached secret is removed on logout and after the event ends (T6).

---

### Task 1: QrCodeService and shared vectors

**Files:**
- Create: `src/TicketRush.Application/Abstractions/Tickets/IQrCodeService.cs`, `src/TicketRush.Infrastructure/Tickets/QrCodeService.cs`, `shared/fixtures/qr-vectors.json`
- Modify: `src/TicketRush.Infrastructure/DependencyInjection.cs` (register singleton)
- Test: `tests/TicketRush.Application.Tests` or `tests/TicketRush.Api.IntegrationTests` (wherever Infrastructure unit tests live — find the pattern, e.g. where `AdmissionTokenFactory` or `OutboxRetryPolicy` tests are), file `QrCodeServiceTests.cs`

**Interfaces:**
- Produces: `IQrCodeService { string Encode(Guid ticketId, ReadOnlySpan<byte> secret, long unixSeconds); bool TryParse(string code, out ParsedQrCode parsed); bool Verify(ParsedQrCode parsed, ReadOnlySpan<byte> secret, long unixSeconds); }`, `record ParsedQrCode(Guid TicketId, long Step, byte[] Signature)`, `const int StepSeconds = 15`, `const string Format = "TR1"`.

- [ ] **Step 1: Failing tests** `QrCodeServiceTests`: `Code_verifies_at_the_previous_current_and_next_step` and `fails_two_steps_away` (N−2, N+2), `Encode_matches_the_documented_example` (fixed secret/ticket/time → exact string, hand-computed with an independent HMAC call in the test), `Parse_rejects_malformed_codes` (theory: empty, wrong prefix `TR2.`, 3 or 5 parts, bad base64url, ticketId not 16 bytes, sig not 10 bytes, step empty/negative/with leading zero/non-numeric/overflow, length > 128, whitespace), `Tampered_signature_or_ticket_id_fails_verification`, `Another_secret_fails_verification`, `Vectors_file_verifies_every_case` (reads `shared/fixtures/qr-vectors.json`, ≥ 1000 cases), `Vectors_file_is_reproducible_from_its_seed` (regenerating with the seed in the file's header gives the same file content, so TS and C# cannot drift silently). Run → FAIL.
- [ ] **Step 2: Implement** `QrCodeService` per the pinned format (BCL only: `HMACSHA256`, `CryptographicOperations.FixedTimeEquals`, `Convert`/`Base64Url`, `Guid.ToByteArray(bigEndian: true)`); register in DI. Generate and commit `shared/fixtures/qr-vectors.json` (header: `{ "seed": <int>, "stepSeconds": 15, "format": "TR1", "cases": [ { "ticketId", "secret" (base64), "unixSeconds", "code" } ] }`, 1 000 cases from a seeded `Random`, deterministic). Run → PASS; run `ArchitectureTests`.
- [ ] **Step 3: Commit** `feat(10): QrCodeService with shared TR1 test vectors`

---

### Task 2: Tickets API (list, detail, qr-key, throttle)

**Files:**
- Create: `Application/Features/Tickets/{ListMyTickets,GetTicket,GetQrKey}/*` (query + handler + models), `Application/Abstractions/Tickets/IQrKeyThrottle.cs`, `Application/Features/Tickets/TicketErrors.cs`, `Infrastructure/Tickets/RedisQrKeyThrottle.cs`, `Infrastructure/Redis/Scripts/qr_key_throttle.lua`, `Api/Features/Tickets/TicketEndpoints.cs`
- Modify: `Api/ApiServiceCollectionExtensions.cs`, `Infrastructure/DependencyInjection.cs`, `IAppDbContext` only if a DbSet is missing, regenerated `web/openapi/ticketrush-api.json` + `web/src/lib/api/schema.d.ts`
- Test: `tests/TicketRush.Api.IntegrationTests/Tickets/{TicketApiTests,QrKeyApiTests}.cs`, `tests/TicketRush.Application.Tests/Tickets/*`

**Interfaces:**
- Consumes: `IAppDbContext` (Tickets, Events, EventSeats, Venues, orders), the seat-label loading used by `OrderViews`, `IQrCodeService`.
- Produces: `GET /api/v1/me/tickets?scope=upcoming|past` → `{ events: [{ eventId, slug, title, coverImageUrl, startsAt, endsAt, venueName, city, status, tickets: [{ id, orderId, tierName, seat: {section,row,number}|null, status, holderName }] }] }`; `GET /api/v1/tickets/{id}` → the ticket above + event summary + `orderTicketIds: Guid[]`; `GET /api/v1/tickets/{id}/qr-key` → `{ ticketId, secret, stepSeconds: 15, format: "TR1" }`; `IQrKeyThrottle.TryAcquireAsync(Guid userId, CancellationToken ct) : Task<ThrottleResult(bool Allowed, TimeSpan RetryAfter)>`.

- [ ] **Step 1: Failing tests (integration, real Postgres/Redis, issued tickets through the real order fulfilment helpers used by existing tests):** `List_groups_tickets_by_event_with_seat_labels`, `Upcoming_and_past_scopes_split_by_event_end_and_cancellation`, `Another_users_ticket_is_404_on_detail_and_qr_key_and_list_excludes_it`, `Unknown_ticket_id_is_404_with_the_same_body`, `Detail_lists_sibling_tickets_of_the_order`, `Qr_key_returns_secret_for_a_valid_ticket_and_no_store`, `Qr_key_is_refused_for_checked_in_listed_and_voided_tickets` (409 `ticket.not_displayable`), `Qr_key_is_refused_after_the_event_ended_or_was_cancelled`, `Qr_key_is_limited_to_10_per_minute_per_user` (11th → 429 with Retry-After; another user unaffected), `Throttle_fails_closed_when_redis_is_down` (503 `system.busy`, via a faulty Redis wrapper if the repo has the pattern), `Anonymous_is_401`, `The_secret_is_never_in_list_or_detail`, `A_code_built_from_the_returned_key_verifies_with_QrCodeService`. Run → FAIL.
- [ ] **Step 2: Implement** handlers, throttle (Lua script with header comment; fixed window 60 s, limit 10; key `qr-key:{userId}` under the repo's Redis key prefix helper), endpoints (`.RequireAuthorization()` on the group; `Cache-Control: no-store` via the existing header helper), DI wiring. Regenerate OpenAPI and the typed client with the repo script (`npm run api:gen` or the documented one) and include only additive changes.
- [ ] **Step 3: Run** the new tests, `ArchitectureTests`, then the full Api suite (output captured to a file) → PASS; report counts.
- [ ] **Step 4: Commit** `feat(10): tickets API with owner-only qr-key`

---

### Task 3: Web core — TR1 generator, QR encoder, ticket client

**Files:**
- Create: `web/src/lib/tickets/{qr-code.ts,qr-matrix.ts,api.ts,types.ts,keys.ts}`, `web/src/lib/tickets/*.test.ts`, `web/src/lib/tickets/fixtures/qr-reference.json` (reference matrices)
- Test: Vitest (`qr-code.test.ts`, `qr-matrix.test.ts`, `api.test.ts`)

**Interfaces:**
- Produces: `qrPayload(ticketId: string, secret: Uint8Array, unixSeconds: number): Promise<string>`; `currentStep(unixSeconds: number): number`; `msUntilNextStep(nowMs: number): number`; `encodeQr(text: string): boolean[][]` (module matrix, byte mode, ECC M, versions 1–10, throws `RangeError` when too long); `getMyTickets(scope)`, `getTicket(id)`, `getQrKey(id)` with TanStack Query hooks `useMyTickets(scope)`, `useTicket(id)`, query keys under `["tickets", …]` dropped on user change (reuse `useDropCacheOnUserChange`).

- [ ] **Step 1: Failing tests:** `qr-code.test.ts`: `Generator_matches_all_1000_shared_vectors` (reads `shared/fixtures/qr-vectors.json` via a relative path from the repo root; uses Node's webcrypto when jsdom lacks `crypto.subtle`), `Step_arithmetic_at_boundaries` (unixSeconds 14/15/29/30; `msUntilNextStep`), `Generator_throws_on_a_non_16_byte_ticket_id_or_empty_secret`. `qr-matrix.test.ts`: `Matches_reference_matrices` (the committed reference file), `Version_grows_with_payload_length_up_to_the_limit`, `Rejects_payloads_longer_than_version_10_M_capacity`, `Finder_timing_and_format_patterns_are_valid` (structural checks: finder patterns, timing rows, dark module, format info BCH-valid for the chosen mask), `Payloads_of_real_TR1_length_encode_to_a_stable_size`. `api.test.ts`: request/response mapping and error mapping (404/409/429 → typed errors incl. `retryAfterSeconds`). Run → FAIL.
- [ ] **Step 2: Implement** the generator (Web Crypto `crypto.subtle.importKey("raw", …, {name:"HMAC", hash:"SHA-256"})`; base64url without padding; Guid → 16 big-endian bytes) and the QR encoder (own implementation of ISO 18004 byte mode: bit stream with mode/length/terminator/padding, Reed–Solomon over GF(256) 0x11D, block interleaving for versions 1–10 at level M, function patterns, alignment patterns, zigzag placement, 8 masks with the standard penalty scoring, BCH format and version information). **Validate with a real decoder outside the repo**: create a throwaway venv in the system temp dir, `pip install zxing-cpp pillow` (or `opencv-python-headless`), render the matrix of several `TR1` payloads (plus short ASCII and 60-char strings) to PNG with a 4-module quiet zone, decode them and compare; fail the task if any decode differs. Commit only `qr-reference.json` (payload → matrix rows as hex/bit strings) generated from the validated encoder. Never commit the venv or the PNGs.
- [ ] **Step 3: Run** `npm test`, `npm run lint`, `npm run typecheck` → PASS. **Step 4: Commit** `feat(10): TR1 generator, in-repo QR encoder and ticket client`

---

### Task 4: `/account/tickets` page

**Files:**
- Create: `web/src/features/tickets/{tickets-view,ticket-card,tickets-tabs}.tsx`, `web/src/app/(buyer)/account/tickets/page.tsx`, i18n copy, tests `web/src/features/tickets/*.test.tsx`, `web/e2e/tickets.spec.ts` (list part)
- Modify: the account page/navigation to link to "Vé của tôi" (find the account hub `web/src/app/(buyer)/account/page.tsx`)

**Interfaces:** consumes `useMyTickets(scope)` (T3). Cards link to `/tickets/{id}` of the first ticket (or a per-ticket list when several); the card shows cover, date (Asia/Ho_Chi_Minh), venue, ticket count.

- [ ] **Step 1: Failing Vitest:** `Tabs_switch_between_upcoming_and_past`, `Cards_show_cover_date_venue_and_ticket_count`, `Empty_state_links_to_the_catalog`, `Loading_and_error_states`, `Tab_state_is_in_the_url_and_keyboard_operable`. Playwright (scripted routes like the other specs): list renders, tab switch, empty state, axe light + dark. Run → FAIL.
- [ ] **Step 2: Implement** the page (client component behind the buyer layout; redirect to login without a session like the other account pages), link from the account hub and the order detail page of a paid order ("Xem vé"). **Step 3: Run** Vitest, lint, typecheck, build:e2e + Playwright → PASS. **Step 4: Commit** `feat(10): my tickets page`

---

### Task 5: `/tickets/[id]` wallet screen

**Files:**
- Create: `web/src/app/(buyer)/tickets/[id]/page.tsx`, `web/src/features/tickets/{wallet-view,rotating-qr,progress-ring,status-overlay,seat-info,sibling-nav,add-to-calendar}.tsx`, `web/src/features/tickets/use-rotating-code.ts`, `web/src/lib/tickets/ics.ts`, tests

**Interfaces:** consumes T3 (`qrPayload`, `encodeQr`, `msUntilNextStep`, `getQrKey`, `useTicket`). `useRotatingCode(ticketId, secret): { payload: string|null, msLeft: number, step: number }` re-generates exactly on each 15 s boundary using a corrected clock (use the repo's server-time offset helper `lib/server-time.ts`/`clock.ts`: spec 00 C10), pauses when the tab is hidden and resyncs on visibility.

- [ ] **Step 1: Failing Vitest** (fake timers, mocked crypto where needed): `Code_changes_exactly_at_each_15_second_boundary`, `Progress_ring_tracks_the_time_left`, `Live_clock_ticks_every_second`, `Reduced_motion_stops_the_gradient_but_keeps_the_clock`, `Listed_ticket_hides_the_qr_and_shows_the_overlay`, `Checked_in_and_voided_overlays`, `Seat_info_shows_Khu_Hang_Ghe_and_tier_and_holder`, `Add_to_calendar_builds_a_valid_ics`, `Sibling_navigation_with_arrows_and_swipe`, `Qr_key_409_shows_not_displayable_state`, `QR_module_is_dynamically_imported` (the QR encoder is not in the page's static imports), `Time_uses_the_server_corrected_clock`. Playwright: wallet screen renders QR, the encoded text changes across a 15 s boundary (use `page.clock` fake time), overlays, axe light + dark, keyboard navigation. Run → FAIL.
- [ ] **Step 2: Implement** the wallet (mobile-first; QR drawn on a canvas or SVG from the matrix with a 4-module quiet zone, high contrast independent of dark mode: black on white; animated gradient band + clock under the QR; large Khu/Hàng/Ghế; tip "Tăng độ sáng màn hình"). **Step 3: Run** all web checks → PASS. **Step 4: Commit** `feat(10): ticket wallet screen with rotating QR`

---

### Task 6: Offline — IndexedDB key cache and service worker

**Files:**
- Create: `web/src/lib/tickets/key-cache.ts` (+ test), `web/public/sw.js` (or the Next 16 recommended location), `web/src/lib/tickets/register-sw.ts`, `web/e2e/tickets-offline.spec.ts`
- Modify: the wallet view/hook to read the cached key when the network fails, the logout path to clear the cache, the next config/headers if the service worker needs `Service-Worker-Allowed`/no-cache headers

**Interfaces:** `keyCache.put(ticketId, { secret, endsAt }) / get(ticketId) / delete(ticketId) / clearAll() / purgeEnded(nowMs)`; the wallet falls back to the cached key when `getQrKey` fails with a network error (not on 404/409), shows a small "Đang dùng mã ngoại tuyến" hint, and refreshes the key online. `clearAll()` runs on logout and on user change.

- [ ] **Step 1: Failing tests:** Vitest with `fake-indexeddb`-free approach (implement the cache over a thin interface with an in-memory fake in tests; do NOT add a dependency): `Key_is_cached_after_a_successful_fetch`, `Network_failure_uses_the_cached_key`, `Not_found_or_not_displayable_does_not_use_the_cache_and_deletes_it`, `Keys_of_ended_events_are_purged`, `Logout_clears_all_keys`, `Cache_failures_never_break_the_online_path`. Playwright `tickets-offline.spec.ts`: open the wallet online, go offline (`context.setOffline(true)`), reload → the wallet still renders from the service worker shell and the QR still rotates across a boundary; online user B after logout of A sees none of A's cached keys; axe. Run → FAIL.
- [ ] **Step 2: Implement** the cache over raw IndexedDB with try/catch around every call (private windows can throw), the service worker (precache the wallet route shell and its static assets, network-first for `/api/*`, never cache `/api/*` responses, version the cache and delete old ones), registration only on buyer routes with a session hint, and logout/user-change cleanup. If Next 16 + the static e2e server make the shell hard to cache, cache the HTML and `/_next/static/*` responses seen while online (runtime caching), and document the limits.
- [ ] **Step 3: Run** all web checks → PASS. **Step 4: Commit** `feat(10): offline ticket wallet (key cache and service worker)`

---

### Task 7: Closeout — docs, links, full verification

**Files:**
- Modify: `specs/10-e-tickets-and-rotating-qr.md` (tick the five criteria with proving test names, API/behaviour notes incl. the pinned format and rulings), `README.md` status row, `docs/runbooks` note if useful (ticket key handling, the Data Protection key ring requirement for reading secrets), the order detail page link if not done in T4
- Test: full verification

- [ ] **Step 1:** Tick every acceptance box only with a named passing test: (1) QrCodeService step tests; (2) the 1 000-vector tests on both sides; (3) the 404 tests; (4) the offline Playwright test; (5) the refusal tests. Add "Implementation notes" (format pinning, in-repo encoder and how it was validated, throttle superseded by spec 12, check-in time deferred to 11).
- [ ] **Step 2: Full verification with COMPLETE output captured to files:** whole-solution `dotnet build` (audit on, 0 warnings), Domain, Architecture, Application, full Api suite (`-- --max-threads 2`), `npm test`, `npm run lint`, `npm run typecheck`, `ENABLE_DESIGN_PAGE=1 npm run build:e2e`, `npx playwright test --project=chromium`. Name any failing/flaky test and whether it passes alone.
- [ ] **Step 3: Commit** `docs(10): acceptance ticked for e-tickets and rotating QR`

---

## Self-review

- **Spec coverage:** QR format and service → T1; API table and error codes → T2; client generator → T3; `/account/tickets` → T4; wallet screen (ring, band, clock, seat info, overlays, calendar, siblings, reduced motion) → T5; offline IndexedDB + service worker → T6; acceptance 1–5 → T1, T1+T3, T2, T6, T2.
- **Type consistency:** `IQrCodeService`/`ParsedQrCode` (T1) are used by T2's key test and by spec 11; `qrPayload`/`encodeQr`/`getQrKey` (T3) are consumed by T5/T6 with the same names.
- **Proportion:** the plan fixes formats, names, signatures and test names; bodies are left to implementers.
