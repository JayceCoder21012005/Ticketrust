# 06 — Virtual Waiting Room

**Goal:** absorb the sale-open spike (C5) and admit buyers at a rate the system can serve. The order must be fair: everyone who arrives before opening gets a random position, later arrivals are first-in-first-out.

**Depends on:** 04 (05 for real data; seed data works too)

## Flow

```
Pre-queue (T-30 → T0) ──T0: shuffle──▶ Line (FIFO numbers) ──admitter──▶ Admitted ──claim──▶ Active shopper (admission token, 10 min)
                                                                                       └─ sold out ─▶ SoldOut (→ waitlist, 13)
```

## Redis keys (hash tag `{sale:<phaseId>}`)

| Key | Type | Meaning |
|---|---|---|
| `q:{sale}:state` | HASH | `status` = `Pre`/`Opening`/`Open`/`AllHeld`/`SoldOut`/`Closed`, `opensAt` |
| `q:{sale}:pre` | SET | userIds that joined before opening |
| `q:{sale}:line` | HASH | userId → ticket number (1, 2, 3… contiguous) |
| `q:{sale}:seq` | STRING | last ticket number issued |
| `q:{sale}:admitted` | STRING | every number ≤ this value is admitted |
| `q:{sale}:active` | ZSET | userId → token expiry (ms). Size is capped by `MaxActiveShoppers` |
| `q:{sale}:tokens` | HASH | userId → current admission token (multiple tabs get the same token) |

Position = `number − admitted`. This is O(1), so there is no `ZRANK` on a 100k-member set.

## Lua scripts (one file each, in `Infrastructure/Redis/Scripts`)

- `queue_join.lua`: idempotent. If the user is in `line` → return the number. If status is `Pre`/`Opening` → `SADD pre`. If `Open`/`AllHeld` → `INCR seq` and `HSET line`.
- `queue_finalize_open.lua`: after the shuffle, assigns `INCR seq` to any `pre` members added during `Opening`, then sets status `Open`.
- `queue_status_claim.lua`: returns `{status, number, admitted, activeCount}`. If `number ≤ admitted` and (the user is already active **or** `ZCARD active < max`) → `ZADD active`. Returns a `claimed` flag.

## Workers (leader-elected, `IDistributedLock`)

- **QueueOpener** at `opensAt`: set `Opening` → read `pre` → Fisher–Yates shuffle with `RandomNumberGenerator` → pipeline `HSET line` numbers 1..n, `SET seq n` → `queue_finalize_open`.
- **QueueAdmitter** every 1 s while `Open`: remove expired members from `active`. Then `step = min(AdmissionRatePerSecond, MaxActiveShoppers − ZCARD active)` and `admitted = min(admitted + step, seq)`. Records the actual rate (EMA over 60 s) for ETAs.
- **Inventory listener**: when 07 reports `remaining == 0`, set `SoldOut` if `sold == capacity`, else `AllHeld` (tickets may come back when holds expire). It flips back to `Open` when inventory returns.
- At `ClosesAt` → `Closed`.

Config `Queue`: `AdmissionRatePerSecond` (default 50), `MaxActiveShoppers` (default 500), `ShoppingWindowMinutes` (10), `PreQueueMinutes` (30).

## Admission token

JWT signed with a **separate** key (`AdmissionToken:SigningKey`), claims `sub` (userId), `evt`, `phase`, `jti`, `exp` (= now + shopping window). It is created by `AdmissionTokenFactory`. The endpoint filter `RequireAdmissionToken` (used by 07, 08) checks: valid signature, not expired, `sub` == the authenticated user, `evt` == the route event, and the user is still in `active` (revocation). A shared token is useless to anyone else (C7).

Phases with `QueueEnabled = false` use the same flow but admit instantly. Every buy path is then enforced the same way.

## API (auth + confirmed email required)

| Method | Route | Notes |
|---|---|---|
| POST | `/events/{eventId}/queue/join` | `{accessCode?, captchaToken?}` → status. Idempotent per user. `captchaToken` becomes required in 12 |
| GET | `/events/{eventId}/queue/me` | `{status, position?, aheadOfYou?, estimatedWaitSeconds?, opensAt, serverNow, pollAfterMs, admissionToken?, tokenExpiresAt?}` |
| POST | `/events/{eventId}/queue/leave` | Removes the user from `active` and frees the slot |

`pollAfterMs` is chosen by the server. Position > 10k → 15 s, > 1k → 8 s, > 100 → 4 s, otherwise 2 s, with ±20 % jitter. During `Pre`, the server returns a time that wakes the client at `opensAt + random(0–3 s)`, which spreads the T0 spike. The status endpoint touches only Redis (target p99 < 20 ms).

Error codes: `queue.not_open`, `queue.closed`, `queue.invalid_access_code`, `queue.email_not_confirmed`, `admission.required`, `admission.invalid`.

## Frontend `/events/[slug]/queue`

- Full-screen, calm layout: event cover blurred in the background, card in the centre.
- **Pre**: big countdown. Explain fairness: "Mọi người vào phòng chờ trước giờ mở bán sẽ được xếp thứ tự ngẫu nhiên."
- **Waiting**: progress bar (from the first known position to 0), "Còn N người phía trước", ETA rounded ("khoảng 4 phút"), and "Đừng tải lại trang — bạn sẽ không mất lượt". The tab title shows `(#1.234) TicketRush`.
- **Admitted**: success state, short chime, title "Đến lượt bạn!", optional browser notification, auto-redirect to seats after 3 s (with a button).
- **AllHeld / SoldOut / Closed**: clear message. SoldOut offers the waitlist (13).
- Polling runs in a **Web Worker** (background tabs throttle main-thread timers), follows `pollAfterMs` and retries with backoff on errors. Optional Screen Wake Lock on mobile.
- After admission, a global `ShoppingTimer` pill shows the remaining shopping window on all buyer pages.

## Acceptance criteria

Phase A (core) is done; the items that need specs 07, 12 and 13 are listed under "Deferred" below.

- [x] 10,000 users join during `Pre`. After opening, all get unique numbers 1..10,000, and two runs produce different orders. (`WaitingRoomOpenerTests`)
- [x] A user who joins after T0 gets a number greater than every pre-queue user. (`WaitingRoomOpenerTests`, `QueueApiTests`)
- [x] Joining twice or from two tabs returns the same number and the same token. (`WaitingRoomScriptTests`, `QueueApiTests`)
- [x] Active shoppers never exceed `MaxActiveShoppers` (load test). (`WaitingRoomAdmitterTests.Active_never_exceeds_max_under_load`; the 10,000-user load run belongs to spec 17)
- [x] A token used by another account → 403 `admission.invalid`. (`QueueApiTests`)
- [x] Killing the admitter leader → another instance takes over within 10 s. No numbers are skipped or duplicated. (`QueueWorkersTests`)
- [x] A background tab is still admitted within one poll interval. (polling runs in a Web Worker; `e2e/queue.spec.ts`)

### Deferred to later specs

- [x] Inventory listener: `AllHeld` / `SoldOut` and back to `Open` (built with 07: Worker `QueueRoomStatusService` writes the room status from the Redis inventory every second; `QueueRoomStatusTests`).
- [ ] SoldOut offers the waitlist (13).
- [ ] `captchaToken` is required on join (12). The field is accepted and ignored today.
- [x] `RequireAdmissionToken` is applied to `POST /events/{id}/holds` (07; `HoldApiTests`). Checkout (08) does not need it: a live hold is the proof of admission.
- [x] Event cancellation: `EventCancelledInventoryHandler` releases every hold and closes every room of the event, which revokes admissions on their next check (`QueueRoomStatusTests`). The API may still serve joins for up to 30 s from its sale cache.
- [x] Adjacent phases (presale then general sale with no gap): while one phase is open and the next QueueEnabled phase is inside its `PreQueueMinutes` window, a buyer who is in no room yet is queued for the NEXT phase (random pre-queue, that phase's access code, status `Pre`); otherwise `PhaseSelector.Select` as before. A user already in a room keeps it: `/queue/me`, `/queue/leave` and re-join check active membership over [open, next] (`IsMemberAsync`), then (status and leave only) the room they left or expired from (`IsDoneAsync`), so they see `Expired` there and never `queue.not_joined`; a join by someone only done in the open phase is a new joiner for the next one. With a single candidate phase there is no extra Redis call. Accepted consequence: a presale buyer who first arrives in the last `PreQueueMinutes` of the presale is queued for the next phase, not the presale. Rules: `docs/superpowers/specs/2026-11-12-adjacent-sale-phases-design.md`. Proved by `QueueLogicTests.Join_target_*`, `QueueHandlerTests` (member/leave/code/single-candidate cases) and `AdjacentPhasesApiTests` (`A_new_joiner_inside_the_next_phases_window_is_queued_for_the_next_phase`, `A_member_of_the_open_phase_keeps_it_when_the_next_window_starts`, `Leave_inside_the_window_frees_the_room_the_user_is_in`, `A_second_tab_rejoin_inside_the_window_returns_the_same_number`, `The_next_phases_access_code_is_the_one_checked_for_a_new_joiner`, `Pre_queue_members_of_the_next_phase_get_unique_random_numbers_when_it_opens`, `A_user_who_left_the_open_phase_inside_the_window_sees_expired_then_the_next_phases_pre_queue_after_joining`, `Single_phase_events_behave_as_before`).
- [x] Phases without a queue (`QueueEnabled = false`): the catalog CTA links to `/queue` (it admits instantly), so every buyer holds a token; the `/seats` page exists (07).
