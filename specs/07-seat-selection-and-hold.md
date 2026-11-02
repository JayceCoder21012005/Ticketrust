# 07 — Seat Selection & Hold

**Goal:** let admitted buyers pick seats on a live map and hold them for 8 minutes, with **zero chance of two people holding the same seat** (C1).

**Depends on:** 06

## Concepts

- **Hold**: a temporary, exclusive claim on seats (seated tiers) or quantities (GA tiers) for one user. It lives only in Redis and expires on its own.
- **Inventory** per event in Redis is a fast mirror of the DB. Postgres stays the final truth (08 adds the DB guards).
- **One active hold per user per event.** Changing seats replaces the hold atomically.

## Redis keys (hash tag `{evt:<eventId>}`)

| Key | Type | Meaning |
|---|---|---|
| `inv:{evt}:sold` | SET | Sold seat indexes |
| `inv:{evt}:held` | HASH | seatIndex → holdId |
| `inv:{evt}:ga:<tierId>` | STRING | GA tickets remaining (capacity − sold − held) |
| `inv:{evt}:hold:<holdId>` | HASH | `userId`, `seats` (csv), `ga` (json), `expiresAt` |
| `inv:{evt}:expiry` | ZSET | holdId → expiresAt (ms) |
| `inv:{evt}:userhold:<userId>` | STRING | the user's active holdId (PX = TTL) |
| `inv:{evt}:bought:<userId>` | STRING | tickets already bought (for the per-user limit) |
| `inv:{evt}:changes` | PUB/SUB | seat deltas published **inside** the Lua scripts |

## Lua scripts

| Script | Does (atomically) | Return codes |
|---|---|---|
| `hold_create.lua` | If `replace` → treat the user's old hold seats as free. Check limit `bought + requested ≤ max`. Every seat ∉ `sold` and ∉ `held`. Every GA `remaining ≥ qty`. Then write `held`, `DECRBY` GA, create the hold hash, `ZADD expiry`, set `userhold`, release the old hold, `PUBLISH` the delta | `OK`, `SEAT_UNAVAILABLE` + indexes, `GA_INSUFFICIENT` + tierId, `LIMIT_EXCEEDED`, `HAS_HOLD` |
| `hold_release.lua` | Remove seats from `held` **only where the value == holdId**, `INCRBY` GA back, delete the hold, `ZREM`, clear `userhold` if it matches, `PUBLISH` | `OK`, `NOT_FOUND` (idempotent) |
| `inventory_commit.lua` | Called by 08 after the DB commit: `SADD sold`, remove the hold, `INCRBY bought`. If the hold already expired, still `SADD sold` and `DECRBY` GA (late payment path) | `OK` |

Seat indexes are sorted ascending before calling a script (C9). Scripts take all keys as `KEYS[]` (cluster-safe). The TTL is passed as an argument (normal holds use config, waitlist offers in 13 use 15 min). The C# wrapper is `IInventoryStore` (Infrastructure), so handlers never touch Redis directly.

## Background services

- **HoldExpiryService** (Worker, leader): every 1 s, for each event currently on sale, read `ZRANGEBYSCORE expiry 0 now LIMIT 500` and call `hold_release` for each.
- **InventoryWarmer** (on `EventPublished`) and **InventoryReconciler** (startup + every 5 min): rebuild `sold` from `tickets`, then `ga = capacity − soldInDb − heldQty`. Log and fix drift.
- **SeatDeltaBroadcaster** (Worker, leader): subscribes to `inv:*:changes`, collapses deltas per event every **250 ms** (a seat held then released inside the window sends only the final state), and sends `{ v, held[], freed[], sold[], tiers[{tierId, remaining}] }` to SignalR group `event:{id}` through `IHubContext` with the Redis backplane. `v` is a per-event version. Clients that see a gap in `v` refetch `/availability`.
- Emits `InventoryDepleted` / `InventoryRestored` for 06.

## API (auth + `RequireAdmissionToken`)

| Method | Route | Notes |
|---|---|---|
| POST | `/events/{eventId}/holds` | Header `Idempotency-Key`. Body: `{ seatIndexes?: int[], ga?: [{tierId, quantity}], bestAvailable?: {tierId, quantity}, replaceExisting: bool }` → `{ holdId, seats[{index, section, row, number, tierId, price}], ga[], total, expiresAt, serverNow }` |
| GET | `/events/{eventId}/holds/me` | Current hold or 404 |
| DELETE | `/events/{eventId}/holds/me` | Release (idempotent) |
| Hub | `/hubs/events` → `JoinEvent(eventId)` / `LeaveEvent` | Receives `SeatDelta` |

Error codes: `hold.seats_unavailable` (+ `unavailable: int[]`), `hold.insufficient_quantity`, `hold.limit_exceeded`, `hold.already_exists` (user has a hold and `replaceExisting` is false), `hold.empty_selection`, `hold.invalid_seat`, `hold.not_found`, `system.busy` (Redis down → fail closed).

## Rules

- TTL from config `Holds:TtlSeconds` (default 480). A valid admission token is needed to **create** a hold. An existing hold stays valid until its own expiry, even if the token expires.
- Max seats per hold = `SalePhase.MaxTicketsPerUser − bought`.
- **Best available** (`BestAvailableSeatPicker`, Strategy): choose N adjacent seats in the same row of the tier, scored by distance to the stage centre and by row. Try `hold_create`. On `SEAT_UNAVAILABLE`, try the next candidate (max 3 attempts).
- Prices come from the frozen tiers (05), so the price shown is the price paid.

## Frontend `/events/[slug]/seats`

- Layout: seat map (left/top), summary panel (right on desktop, bottom `Drawer` on mobile), `ShoppingTimer` pill.
- **Seat map** (dynamically imported): SVG, overview of sections → click to zoom into a section, pinch/scroll zoom, pan. Legend + price filter chips per tier (non-matching seats dim).
- Seat state lives in an external store keyed by index (`useSyncExternalStore`). Each seat subscribes to its own index, so a delta re-renders only the changed seats. Must stay at 60 fps with 10,000 seats.
- Seat accessibility: focusable, arrow keys move between seats, `aria-label="Khu A, hàng 3, ghế 12, 1.500.000 ₫, còn trống"`.
- GA tiers: quantity stepper. "Chọn chỗ tốt nhất" button per tier.
- Clicking "Tiếp tục" calls `POST /holds`. On `seats_unavailable`, mark those seats taken, keep the rest selected, toast "2 ghế vừa có người chọn". On success, go to checkout.
- After a hold, a countdown shows from `expiresAt`. At 0, show a modal "Hết thời gian giữ chỗ" and go back to seat selection.
- SignalR connects only on this page. On reconnect, refetch `/availability`.

## Acceptance criteria

- [ ] 1,000 concurrent requests for the same seat → exactly one `OK` (integration test against real Redis).
- [ ] A hold replaced with overlapping seats keeps the overlapping seats and never shows them as free to others.
- [ ] Holds expire within 2 s of `expiresAt`, and the seats turn available on other clients within 1 s.
- [ ] Releasing an already-expired hold returns 204 and doesn't free seats that now belong to another hold.
- [ ] Killing Redis during a hold request returns 503 `system.busy`. Nothing is written to the DB.
- [ ] After a manual drift (delete `sold` in Redis), the reconciler restores it from the DB.
- [ ] Keyboard-only users can select seats and continue.
