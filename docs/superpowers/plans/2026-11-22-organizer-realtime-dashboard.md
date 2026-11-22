# Organizer Realtime Dashboard Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans. Steps use checkbox (`- [ ]`) syntax.

**Goal:** live sales, revenue, funnel and queue for the organizer every second, plus history and a streamed attendee CSV (spec 15).

**Architecture:** Redis hash `dash:{evt}` (live counters, idempotent per order) + Postgres `sales_per_minute` (truth with
`orders`); a 1 s leader broadcaster sends one snapshot per watched event to `org:{eventId}` on `/hubs/organizer`; a 1 min
reconciler rewrites the counters from Postgres.

**Spec:** `specs/15-organizer-realtime-dashboard.md`; design `docs/superpowers/specs/2026-11-22-organizer-realtime-dashboard-design.md`.

## Global Constraints

- Ownership: another organizer gets 404 on every route and cannot join the hub group.
- One snapshot per second per watched event, whatever the order rate.
- CSV is streamed from `IAsyncEnumerable`; never a list of all rows.
- Resale orders (`OrderKind.Resale`) count in neither sold nor revenue.
- Copy: "Đã bán", "Doanh thu", "Còn lại", "Đang trong hàng chờ", "Đang mua", "Xuất CSV", "Live", "Đang kết nối lại…".

## Review Focus

1. `OrderPaid` redelivered → counters and `sales_per_minute` count it once.
2. Redis down during `OrderPaid` → the consumer throws, the outbox retries, nothing half-written in Postgres.
3. A viewer whose API node died → presence expires (heartbeat), the broadcaster stops.
4. An event with no sales yet → snapshot of zeros, empty series, CSV with only the header.
5. Hub join for someone else's event or a Customer → refused.

### Task 1: Counters, consumer, reconciler (backend core)
`SalesMinute` entity + migration; `IDashboardStore` (Redis: paid script with NX marker, held/created increments, read,
overwrite, recent orders, viewers); `DashboardOrderPaidHandler` (upsert + script); held/created hooks; `DashboardReconcileHandler`.
Integration tests: paid once on redelivery, resale skipped, reconcile after flush, revenue = SUM.

### Task 2: Snapshot, API, hub, broadcaster
`DashboardSnapshotBuilder`; `GET dashboard`, `sales-series`, `orders`, `export.csv`; `OrganizerHub`; Worker broadcaster and
reconciler services. Integration tests: 404 for a stranger (routes and hub), one snapshot per tick at 500 paid/s, series buckets,
orders paging, CSV streaming memory with 100,000 tickets.

### Task 3: Web
`/organizer/events/[id]/dashboard`: KPI cards, dynamic chart + funnel, tier table, read-only seat heatmap, recent orders, CSV
link, live badge. Vitest + Playwright with scripted endpoints. `scripts/ci-local.ps1` green, push.
