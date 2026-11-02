# 04 — Event Catalog & Search

**Goal:** fast, cacheable public pages to discover events, plus the core event data model that specs 05–08 build on.

**Depends on:** 03

## Domain model (shared by later specs)

| Entity | Key fields | Invariants |
|---|---|---|
| `Venue` | `Name`, `Address`, `City`, `MapUrl`, `Capacity` | — |
| `Event` (aggregate root) | `OrganizerId`, `Slug` (unique), `Title`, `Description` (markdown), `Category`, `Lineup[]`, `CoverImageUrl`, `VenueId`, `StartsAt`, `EndsAt`, `Status`, `IsSoldOut` | `EndsAt > StartsAt`. Status: `Draft → Published → Ended`, `Draft/Published → Cancelled` |
| `SalePhase` (owned by Event) | `Name`, `OpensAt`, `ClosesAt`, `QueueEnabled`, `AccessCodeHash?` (presale), `MaxTicketsPerUser` (1–8) | Phases of one event never overlap. `ClosesAt ≤ Event.StartsAt` |
| `TicketTier` (owned by Event) | `Name`, `Price: Money`, `Kind` (`Seated` / `GeneralAdmission`), `Capacity` (GA only), `Color` | Price > 0. Seated capacity = number of seats assigned to it |
| `EventSeat` | `EventId`, `Index` (int, 0..n-1, unique per event), `Section`, `Row`, `Number`, `X`, `Y`, `TierId` | (`EventId`, `Section`, `Row`, `Number`) unique |

`EventSeat.Index` is the compact id used in every realtime and Redis payload (07). Seat state (held/sold) is **not** stored on `EventSeat`. It lives in Redis (holds) and in `tickets` (sold).

## API (public, anonymous)

| Method | Route | Notes |
|---|---|---|
| GET | `/events` | Filters `q`, `city`, `category`, `from`, `to`, `priceMax`. Only `Published`, not ended. Keyset pagination ordered by `StartsAt, Id` |
| GET | `/events/{slug}` | Detail incl. venue, tiers (price, kind, remaining is **not** included), phases (without access codes) |
| GET | `/events/{id}/seat-map` | Static layout: sections + seats (`index, section, row, number, x, y, tierId`). `Cache-Control: public, max-age=3600` + ETag |
| GET | `/events/{id}/availability` | Live: `{ serverNow, version, unavailable: int[] /* seat indexes */, tiers: [{tierId, remaining}] }`. Built from Redis (07), micro-cached 1 s in memory. Until 07 is built, return everything as available |
| GET | `/categories`, `/cities` | Static lists |
| GET | `/time` | `{ serverNow }` for client clock-offset (C10) |

Error codes: `event.not_found`.

## Search & performance

- Postgres extensions `unaccent` + `pg_trgm`. Generated `tsvector` column over unaccented title, lineup, venue city with GIN index, so "son tung" matches "Sơn Tùng". Trigram fallback for typos.
- Indexes: `(status, starts_at, id)`, `(city, starts_at)`, `(category, starts_at)`.
- Read handlers are wrapped by a HybridCache decorator: detail 10 min, list 30 s, tagged `event:{id}` / `events:list`. HybridCache single-flight prevents cache stampede (C6).
- Any event change (spec 05) removes cache tags and calls the web revalidate hook `POST /api/revalidate` (shared secret) → `revalidateTag(tag, 'max')`.
- Read queries use `AsNoTracking()` and project straight to DTOs (`Select`), never load full aggregates.

## Frontend

- **Home `/`**: hero with the next big on-sale event, "Sắp mở bán" row with live countdowns, category chips, city selector, grid of upcoming events. Cached with `"use cache"`.
- **Listing `/events`**: filters live in URL search params (shareable). Filter panel is a `Sheet` on mobile and a sidebar on desktop. "Xem thêm" button for keyset pagination (no page numbers). Debounced search (300 ms).
- **Event detail `/events/[slug]`** (Luma-style): cover image (LCP, `priority`), title, date/time block with "Thêm vào lịch" (.ics download), venue with map link, lineup, description, tier price list ("từ 800.000 ₫"), FAQ/policy accordion. Sticky CTA on mobile.
- **CTA state** is computed on the client from `phases` + server clock offset + live availability:

| State | Condition | CTA |
|---|---|---|
| `Upcoming` | next phase opens in > 30 min | Countdown + "Nhắc tôi" (09) |
| `PreQueue` | phase opens within 30 min and `QueueEnabled` | "Vào phòng chờ" → `/events/[slug]/queue` |
| `OnSale` | phase open, not sold out | "Mua vé" → queue (if enabled) or seats |
| `SoldOut` | all tiers remaining = 0 | "Hết vé — Vào danh sách chờ" (13) |
| `Ended` / `Cancelled` | — | Disabled with explanation |

- Open Graph image per event (`opengraph-image.tsx`) for social sharing.

## Acceptance criteria

- [ ] Searching "son tung" finds "Sơn Tùng M-TP Live".
- [ ] 1,000 concurrent requests for one uncached event detail cause exactly one DB query (test with HybridCache).
- [ ] Draft and cancelled events never appear in `/events` and return 404 on detail.
- [ ] Seat-map returns 304 with a matching `If-None-Match`.
- [ ] CTA switches from countdown to "Vào phòng chờ" at T-30 min without a page reload, even when the device clock is 5 minutes off.
- [ ] Event detail page passes the performance budgets in 02.
