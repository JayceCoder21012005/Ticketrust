# Seat Selection & Hold — Research, Design and Plan (spec 07)

Source of truth for behaviour: `specs/07-seat-selection-and-hold.md`. This document records **what I found before building**, the **decisions that follow from it**, and the **build order**. Status: **approved 2026-11-04.** User answers: (1) include the 06 hooks (room-status listener, CTA, admission filter on holds); (2) meet the SVG 10,000-seat target — canvas only if the spike still fails after tuning, and only after asking; (3) plan approved. Implementation plan: `docs/superpowers/plans/2026-11-04-seat-selection-and-hold.md`.

## 1. What the codebase already gives us (verified by reading it)

| # | Finding | Consequence for 07 |
|---|---|---|
| E1 | Spec 04 already ships `GET /events/{id}/seat-map` (static, ETag, 1 h cache) and `GET /events/{id}/availability`. `EventAvailability.Unavailable` is a stub ("spec 07 fills it") and the reader caches it 1 s in HybridCache. | Do not add new read endpoints. Make `availability` read **live Redis** (held ∪ sold + GA remaining + version `v`) and drop the 1 s DB cache for it. |
| E2 | `HoldsOptions` has `HoldTtlMinutes = 10` and `MaxSeatsPerHold = 8`; the spec says `Holds:TtlSeconds`, default **480** (8 min). Nothing else reads the old property. | Follow the spec: `TtlSeconds = 480`. Keep `MaxSeatsPerHold = 8` as a hard cap on top of the per-phase limit. C3 needs payment window < hold TTL (08 will read this). |
| E3 | `RequireAdmissionTokenFilter` (06) already validates signature, expiry, user, event and "is the current token". It reads `{eventId}` or `{id}` from the route. | Put it on `POST /events/{eventId}/holds` only. `GET`/`DELETE holds/me` need auth but **no** token (spec: a hold outlives its token). The token also carries `phaseId`, which gives the per-phase `MaxTicketsPerUser`. |
| E4 | There is **no `Ticket` entity or table yet** (spec 08/10). | Warmer/reconciler rebuild `sold` and `bought` through a port (`ISoldSeatSource`) that returns what exists today (nothing). The "delete `sold` → reconciler restores it from the DB" criterion is proven with a seeded fake source now and with real tickets once 08 lands. |
| E5 | No `Idempotency-Key` support exists. Spec 00 requires it on inventory POSTs; spec 08 owns the full design. | Build a small shared `Idempotency-Key` endpoint filter (Redis, per user) now; 08 reuses it. |
| E6 | Package `Microsoft.AspNetCore.SignalR.StackExchangeRedis` is **not** referenced; `@microsoft/signalr` **is** in `web/package.json`. The API already accepts `access_token` on `/hubs/*`; Next rewrites `/hubs/*` to the API. | Add the backplane package. Hub auth for the browser is already in place. |
| E7 | `web/Dockerfile` uses `output: "standalone"` and `deploy/Caddyfile` sends **everything** (including `/hubs`) to `web:3000`. | See R4: WebSocket upgrades will not survive that path. Add a Caddy `handle /hubs/*` route straight to the API. |
| E8 | Production Redis runs `--appendonly yes` (fsync every second), no replica. | A Redis crash can lose ≤ 1 s of holds. Accepted by the spec's design (Postgres is final truth, unique index in 08 + reconciler); documented, not "fixed" here. |
| E9 | 06 left these for 07: inventory listener (`AllHeld`/`SoldOut`/`Open`), the `/seats` page the Admitted screen already redirects to, the CTA on the event page, and the filter on buy endpoints. `QueueEnabled=false` phases admit instantly through the same `/queue` flow. | The CTA can always link to `/events/<slug>/queue`, for queued and instant phases alike. |

## 2. What the outside evidence says

| # | Source finding | Decision |
|---|---|---|
| R1 | Redis runs a script atomically and blocks the server meanwhile; every key a script touches must be passed as `KEYS` (and, with a `#!` shebang, must share one slot); scripts replicate as **effects**, so `PUBLISH` inside a script is safe; the script cache is volatile (lost on restart/failover). [redis.io scripting](https://redis.io/docs/latest/develop/programmability/eval-intro/) | One Lua script per mutation, all keys under `{evt:<id>}`, `EVALSHA` with the `NOSCRIPT` fallback we already wrote in 06 (`LuaScripts`). Keep scripts O(seats in the request) — a hold is ≤ 8 seats, so the blocking cost is tiny. |
| R2 | Ticketing designs converge on: all-or-nothing multi-seat check inside Lua, owner-checked release, TTL on the hold, shard by event, and a reconciliation job against the durable store. (Secondary sources: [AWS flash-sale write-up](https://builder.aws.com/content/3JnLIIiUojSd8QGb32xPTQdumEZ/designing-a-flash-sale-seat-reservation-system-in-aws-part-2-never-sell-a-seat-twice), [Ticketmaster design](https://dilipkumar.medium.com/ticket-master-system-design-e794c51d79f7).) | Matches the spec; no deviation. The sources are blog-level, so correctness is proven by our own tests (1,000-way race, overlap replace, expired release), not by citation. |
| R3 | AOF `everysec` can lose up to one second of writes; RDB alone can lose minutes. [redis.io persistence](https://redis.io/docs/latest/operate/oss_and_stack/management/persistence/) | Keep prod as is (E8); document the 1 s window; reconciler repairs `sold`; 08's DB unique index is the last guard (C1). |
| R4 | SignalR's backplane needs a channel prefix per app on shared Redis, sticky sessions with several servers, and **drops messages while Redis is down** (it reconnects by itself). [Microsoft Learn](https://learn.microsoft.com/aspnet/core/signalr/redis-backplane) Next `rewrites` proxy WebSockets under `next dev`/`next start`, but **not** under `output: "standalone"` (Next issue [#44209](https://github.com/vercel/next.js/issues/44209), closed "not planned"). | (a) The broadcaster must not be the only source of truth: every delta carries a version `v`, and a client that sees a gap (or reconnects) refetches `/availability`. (b) Add `handle /hubs/*` → `api:8080` in `deploy/Caddyfile`; Caddy proxies WebSockets natively. (c) Set a backplane channel prefix. |
| R5 | `Idempotency-Key` (IETF draft -07): missing key → 400; same key with a different payload → 422; retry while the first is still running → 409. [datatracker](https://datatracker.ietf.org/doc/html/draft-ietf-httpapi-idempotency-key-header) It is still a draft. | Implement exactly those three behaviours, store the first response for the hold TTL. |
| R6 | For big seat maps, vendors advise canvas above ~3,000 seats because thousands of DOM nodes slow SVG; an open-source React map still renders 15k seats as SVG by using one small circle per seat, memoized sections and **no React state on hover**; Felt moved to canvas because React re-rendering on every pan/zoom step was too slow. [seatmap.pro](https://seatmap.pro/blog/seating-plan-rendering), [Felt](https://felt.com/blog/from-svg-to-canvas-part-1-making-felt-faster) I found **no** source testing per-seat `useSyncExternalStore`. | The spec's SVG + per-seat subscription is plausible but **unproven at 10,000 seats**. Run a throwaway benchmark first (Phase 0) and decide with numbers. |

## 3. Design decisions (my calls — flagged so you can overrule)

1. **Version counter lives in Lua.** Each mutating script `INCR`s `inv:{evt}:ver` and publishes `{v, ...}` in the same atomic step. The spec says the broadcaster assigns `v`; I move it into Redis so that a leader failover or a second broadcaster cannot reset or fork the sequence, which is what makes gap detection trustworthy.
2. **Replace flow = diff, not release-then-hold.** `hold_create` with `replace` keeps overlapping seats held the whole time (acceptance: overlapping seats never appear free), releases only seats leaving the hold and claims only new ones, after checking everything first.
3. **Key names** follow 06's convention: `tr:{env}:inv:{evt:<id:N>}:<name>`; the spec's short names are the `<name>` part.
4. **Worker owns expiry, broadcast and warm/reconcile** (leader-elected, same `LeaderElectedBackgroundService` as 06). The Worker sends through `IHubContext<EventsHub>` over the Redis backplane; Phase 0 proves this works across processes, because Microsoft's page does not state it and I only found third-party tutorials.
5. **Best available** is a Strategy (`IBestAvailablePicker`): consecutive `Index` values inside one section+row (the generator numbers seats in that order), scored by distance to the stage centre (500, 42 on the 1000×700 canvas) and row; up to 3 `hold_create` attempts.
6. **Inventory listener for 06** is a small Worker service that, per changed event, computes available = total − sold − held and sets the live phase's room to `AllHeld` / `SoldOut` / `Open` through a new `queue_set_status.lua` (the queue and inventory use different hash tags, so this is two scripts, eventually consistent).
7. **Frontend:** seat state in a keyed external store (`Map<index, Set<listener>>`, so a delta wakes only the seats it names). Pan/zoom moves one `<g transform>` without React re-renders. Section overview first, seats rendered only for the zoomed section. One delegated click handler (same trick as the organizer preview).

## 4. Build order

**Phase 0 — spikes (throwaway, no tests, not committed to `src/`):**
- S1: 10,000 SVG circles in Chromium via Playwright — pan/zoom frame time and 200 seat changes per 250 ms. Gate: ≥ 55 fps. If it fails, fall back to a canvas seat layer with an SVG overlay for focus and labels (this deviates from the spec's "SVG"; I will ask before taking it).
- S2: Worker → `IHubContext` → backplane → API-hosted hub → browser client.
- S3: SignalR through Caddy `/hubs/*` with a standalone web build.

**Phase A — inventory core (backend, Testcontainers Redis):** options and keys, `hold_create` / `hold_release` / `inventory_commit` scripts, `IInventoryStore`, hold handlers, `POST/GET/DELETE` hold endpoints (+ filter on POST), idempotency filter, best-available picker, live availability, warmer/reconciler, `HoldExpiryService`. Tests: 1,000 concurrent claims → exactly one `OK`; overlap replace; expired-hold release is a no-op for a new owner; Redis down → 503 `system.busy` with nothing written to the DB; drift repair; GA over-claim; limit checks; seat indexes sorted before calling a script (C9).

**Phase B — realtime and the 06 hooks:** `EventsHub` (`JoinEvent` / `LeaveEvent`), backplane, `SeatDeltaBroadcaster` (250 ms collapse), inventory listener + `queue_set_status.lua`, Caddy route. Tests: delta arrives ≤ 1 s after expiry, collapse behaviour, gap → refetch contract, status transitions `Open → AllHeld → SoldOut → Open`.

**Phase C — frontend:** `/events/[slug]/seats` (guarded by `proxy.ts`; sends `X-Admission-Token` from `tr.admission`; redirects to `/queue` when there is none), seat store, map with section overview and zoom/pan, legend and price chips, summary panel / mobile Drawer, GA stepper and "Chọn chỗ tốt nhất", hold countdown with the expiry modal, `seats_unavailable` toast, keyboard navigation and labels, the CTA on the event page, SignalR client with reconnect → refetch. Vitest for store and logic; Playwright flow with axe in light and dark.

**Definition of done:** the spec's seven acceptance criteria ticked or explicitly marked deferred, suite green, OpenAPI regenerated, `specs/07` updated where the build differs. One branch `feat/07-seat-selection-and-hold`, one PR at the end (as 04, 05 and 06).

## 5. Known limits I will state instead of hiding

- Until 08 creates tickets, `sold` and `bought` are empty in real use; their paths are tested with a fake source.
- With one Redis and `everysec`, a crash can drop the last second of holds (R3).
- SignalR does not buffer while Redis is down (R4); the version/refetch contract covers it.
