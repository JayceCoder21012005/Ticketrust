# 15 — Organizer Realtime Dashboard

**Goal:** during a sale, organizers watch sales, revenue and the queue update every second. After the sale, they see history and export data.

**Depends on:** 08 (06 and 07 for queue/hold metrics)

## Metrics

| Metric | Source |
|---|---|
| Tickets sold, revenue (total + per tier) | `OrderPaid` consumer → Redis `dash:{evt}` HASH `HINCRBY` |
| Funnel: queue joined → admitted → held → order created → paid | Counters incremented by each step's handler (06, 07, 08) |
| Queue length, active shoppers, admission rate | Read from 06 keys |
| Remaining per tier | 07 inventory keys |
| Sales per minute (time series) | Table `sales_per_minute(event_id, minute, tier_id, tickets, revenue)` upserted by the `OrderPaid` consumer (`ON CONFLICT … DO UPDATE`) |

Redis counters are the live view. Postgres (`orders`, `sales_per_minute`) is the truth. **DashboardReconciler** (every 1 min) recomputes the counters from Postgres to fix drift.

## Realtime push

**DashboardBroadcaster** (Worker, leader): every 1 s, for each event that is on sale **and** has at least one viewer, read the counters and send one snapshot `{ at, sold, revenue, tiers[], funnel, queue }` to SignalR group `org:{eventId}` (hub `/hubs/organizer`, organizer must own the event). One snapshot per second no matter how many sales happened (event collapsing). Viewer presence is tracked by `JoinDashboard` / disconnect in a Redis set.

## API (organizer, ownership enforced)

| Method | Route | Notes |
|---|---|---|
| GET | `/organizer/events/{id}/dashboard` | Current snapshot (initial render) |
| GET | `/organizer/events/{id}/sales-series?from=&to=&bucket=1m\|1h` | Chart data |
| GET | `/organizer/events/{id}/orders?cursor=&status=` | Paid orders table |
| GET | `/organizer/events/{id}/export.csv` | Streams attendees: order id, buyer, tier, seat, paid at, check-in status. Generated with `IAsyncEnumerable`, never fully loaded in memory |

## Frontend `/organizer/events/[id]/dashboard`

- KPI row (shadcn cards): Đã bán / Doanh thu / Còn lại / Đang trong hàng chờ / Đang mua. Numbers animate on change (respect reduced motion).
- Sales per minute: line chart (shadcn `chart`, Recharts) with one series per tier.
- Funnel: horizontal bar showing each step and the drop-off % between steps.
- Tier table: capacity, sold, held, remaining, revenue, with a progress bar per tier.
- Live seat heatmap (reuses the 07 seat map in read-only mode, coloured by state).
- Recent orders table (last 20, live) + "Xuất CSV".
- A "Live" badge pulses while connected. When disconnected, it shows "Đang kết nối lại…" and the last update time.
- Charts are dynamically imported. The page shell renders from the initial snapshot without waiting for SignalR.

## Acceptance criteria

- [x] At 500 orders/s, the dashboard still receives exactly one snapshot per second.
- [x] After flushing Redis counters, the reconciler restores correct numbers within 1 min.
- [x] Another organizer can't join the hub group or fetch the dashboard (404).
- [x] Exporting 100,000 attendees keeps API memory flat (streaming test).
- [x] Revenue on the dashboard equals `SUM(orders.total)` for paid orders after a load test.

## Implementation notes (what differs from the text above)

- **Primary sales only.** Resale orders (spec 14) move no seat and earn the organizer nothing, so sold, revenue and `sales_per_minute` skip them. "Revenue equals `SUM(orders.total)`" is over `Paid` primary orders, which is also what the reconciler counts (a refunded order drops out at the next reconcile).
- **Counters per order, once.** The `OrderPaid` consumer upserts `sales_per_minute` in the outbox transaction, then counts the order in `dash:{evt}` with a Lua script guarded by a per-order marker (7 days), so a redelivery never double counts. It runs last among the `OrderPaid` consumers.
- **Funnel.** Queue joined and admitted are read from the 06 keys (`seq`, `admitted`, `pre`, `opening`), active shoppers from the admissions ZSET. Held and order created are counters bumped by the hold and order handlers (best effort); the reconciler rewrites order created, paid, sold and revenue from Postgres and keeps held (Redis-only).
- **Presence.** `JoinDashboard` checks ownership, joins `org:{eventId}` and records the connection in a viewers ZSET; the page repeats the join every 30 s as a heartbeat and a viewer silent for 90 s is dropped. The broadcaster sends only for Published events that have not ended.
- **Heatmap.** The 07 seat map in read-only mode, fed by the same seat feed (`/hubs/events`), not by the snapshot.
- **CSV.** One row per live ticket (buyer of its order and its current holder, which differ after a transfer), UTF-8 BOM, RFC 4180 quoting and formula-injection guarding. Acceptance test: 100,000 tickets streamed with < 10 MB retained.
- **Load acceptance.** "One snapshot per second at 500 orders/s" is tested at the broadcaster tick: 500 concurrent paid orders between two ticks give exactly one snapshot per tick with all 500 counted.
