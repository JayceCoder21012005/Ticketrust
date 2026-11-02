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

- [ ] 10,000 users join during `Pre`. After opening, all get unique numbers 1..10,000, and two runs produce different orders.
- [ ] A user who joins after T0 gets a number greater than every pre-queue user.
- [ ] Joining twice or from two tabs returns the same number and the same token.
- [ ] Active shoppers never exceed `MaxActiveShoppers` (load test).
- [ ] A token used by another account → 403 `admission.invalid`.
- [ ] Killing the admitter leader → another instance takes over within 10 s. No numbers are skipped or duplicated.
- [ ] A background tab is still admitted within one poll interval.
