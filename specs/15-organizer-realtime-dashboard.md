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

- [ ] At 500 orders/s, the dashboard still receives exactly one snapshot per second.
- [ ] After flushing Redis counters, the reconciler restores correct numbers within 1 min.
- [ ] Another organizer can't join the hub group or fetch the dashboard (404).
- [ ] Exporting 100,000 attendees keeps API memory flat (streaming test).
- [ ] Revenue on the dashboard equals `SUM(orders.total)` for paid orders after a load test.
