# Organizer realtime dashboard — design (spec 15)

Spec: `specs/15-organizer-realtime-dashboard.md`. Branch `feat/15-organizer-realtime-dashboard` (from spec 14).

## Intent

During a sale the organizer watches sold, revenue, the queue and the funnel move every second; afterwards they read history and
export attendees. Postgres is the truth, Redis counters are the live view, and the live view repairs itself every minute.

## What exists

- SignalR with the Redis backplane; hubs live in Infrastructure so the Worker can send through them (`EventsHub`, `UserHub`).
  `SeatDeltaBroadcaster` is the leader-elected 250 ms collapser to copy.
- Queue keys (06): `seq` (numbers issued), `admitted` (all numbers up to it admitted), `pre`/`opening` sets, `active` ZSET of
  admission expiries. Inventory keys (07): GA remaining, held hashes, sold set. `IInventoryStore.GetCountsAsync` reads them.
- `OrderPaid` carries lines, total and `Kind` (spec 14). Organizer routes check ownership with `LoadOwnedAsync` (404 otherwise).

## Decisions (mine, the spec leaves them open)

- **Revenue and sold count primary sales only.** A resale order (`Kind = Resale`) moves no seat and earns the organizer
  nothing, so the dashboard skips it. "Revenue equals `SUM(orders.total)`" is over `Paid` primary orders.
- **Funnel: stored where Postgres cannot answer, read where Redis or Postgres already can.**
  - queue joined = Σ phases (`seq` + |`pre`| + |`opening`|), admitted = Σ phases min(`admitted`, `seq`), read from the 06 keys
    (instant phases admit at join, so joined = admitted for them).
  - held = `HINCRBY dash:{evt} held 1` when a hold is created (07, best effort). It is the one step only Redis knows.
  - order created / paid = counters bumped by `CreateOrderHandler` (new order only) and the `OrderPaid` consumer; the reconciler
    rewrites both, with sold and revenue, from Postgres.
- **Counters are idempotent per order.** The `OrderPaid` consumer does `HINCRBY` inside a Lua script guarded by
  `SET dash:{evt}:paid:<order> NX` (like `inventory_commit`), so outbox redeliveries never double count; the
  `sales_per_minute` upsert runs in the consumer's DB transaction (the outbox receipt commits with it).
- **Reconciler** (Worker, leader, 1 min): for events on sale or ended in the last day, recompute sold, revenue, per-tier and
  created/paid from Postgres and overwrite the hash (`held` stays).
- **Broadcaster** (Worker, leader, 1 s): events with viewers (`dash:viewers:{evt}` ZSET of connection → last seen, pruned after
  2 min) that are Published and on sale. One snapshot per event per tick, whatever happened in between.
- **Hub** `/hubs/organizer` (`OrganizerHub`, organizer policy): `JoinDashboard(eventId)` checks ownership in Postgres before
  adding to `org:{eventId}`; a stranger gets a `HubException("not_found")`. Disconnect removes presence.
- **CSV export** streams `IAsyncEnumerable` rows (`AsAsyncEnumerable`, no tracking) straight to the response with UTF-8 BOM so
  Excel shows Vietnamese; one row per ticket (order id, buyer name, email, tier, seat, paid at, check-in status).
- **Orders table**: paid orders keyset-paged like `/me/orders`; status filter `Paid | RefundPending | Refunded`.

## Web

`/organizer/events/[id]/dashboard`: server-free shell rendered from `GET …/dashboard`, then SignalR. KPI cards with animated
numbers (none under reduced motion), Recharts line chart per tier and the funnel loaded with `next/dynamic`, tier table with
progress bars, read-only seat heatmap reusing the 07 seat map, recent orders (last 20, live from snapshots) and "Xuất CSV",
"Live" badge / "Đang kết nối lại…" with the last update time.

## Left to the builder

Key names beyond the spec's, snapshot shape details, intervals' exact values, copy beyond the spec's.
