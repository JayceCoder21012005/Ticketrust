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

## Implementation notes

- UI/UX references and decisions: `docs/superpowers/specs/2026-11-03-event-catalog-and-search-design.md` (Ticketbox prices, Luma event page, DICE single accent, Eventbrite badges, removable filter chips).
- `Event.City` is copied from the venue when the event is created (`CreateDraft` takes the `Venue`): a generated column can't read another table, and the city filter and search then need no join. Categories are a fixed list (`EventCategories`); unknown categories are rejected.
- Search: `events.search_text` and `events.search_vector` are stored generated columns over `catalog_search_text(title, lineup, city)` (lower-cased, `unaccent`ed through an IMMUTABLE wrapper). `catalog_matches()` combines `websearch_to_tsquery('simple', …)` with trigram word similarity (`<%`), so "son tunng" still finds "Sơn Tùng". GIN indexes on both columns.
- Reads go through `IEventCatalogReader` (Infrastructure, EF projections, one SQL query per call) wrapped by `CachedEventCatalogReader` (HybridCache, in-process): list 30 s (`events:list`), detail 10 min, availability 1 s. Detail is looked up by slug before its id is known, so it is tagged `event-slug:{slug}`; seat map and availability use `event:{id}`. `ICatalogCacheInvalidator.InvalidateEventAsync(id, slug)` evicts all three for spec 05.
- Keyset cursor: base64url JSON of `(startsAt, id)`, compared as a Postgres row value. Page size 20 by default, 50 max. Invalid `cursor`, unknown `category`, `to < from`, negative `priceMax` → 400.
- Ended events keep their detail page (`status: "Ended"`, CTA disabled) so old links work; draft and cancelled events are 404.
- Seat map ETag is a content hash of the layout (`version` in the body too). Availability returns every tier as available (capacity or seat count) until spec 07.
- Web data: server components call the api through `"use cache"` functions tagged like the api (`events:list`, `event:{id}`, `event-slug:{slug}`). `POST /api/revalidate` (header `x-revalidate-secret`, `REVALIDATE_SECRET`) calls `revalidateTag(tag, "max")` for catalog tags only.
- Home and event pages are ISR pages (Cache Components): `/` is static HTML regenerated every 30 s; `generateStaticParams` prerenders the 20 soonest events, and any other slug gets the app shell on its first visit, then a complete static page from the ISR cache. This matters for LCP: React 19 reveals streamed Suspense boundaries up to 300 ms late (batching), so the poster/title must be in the initial HTML. The listing (`/events`) reads `searchParams` and stays partially prerendered; its LCP (the h1) is in the static shell.
- When the api is unreachable during `next build` (Docker image builds), reads return empty data with the `seconds` cache profile, which turns that part into a per-request dynamic hole instead of baking an empty catalog into the image. `npm run build:e2e` builds against the mock api.
- Unknown slugs render the 404 page inside the streamed part of the page, so the HTTP status stays 200 and Next adds `noindex` (documented Next.js behaviour for streamed routes). The api answers 404.
- Events without a cover get a generated poster (category hue + title, white text ≥ 4.5:1); the same design is used for the Open Graph image, rendered with a bundled Be Vietnam Pro (the default OG font has no Vietnamese diacritics).
- CTA: `computeCtaState()` (pure) plus `EventCta`, which reads `/availability` for the server clock offset and re-renders on a timer at the next state change. "Nhắc tôi" is the live follow button since spec 09 (a login link for visitors; offered in Upcoming, PreQueue, OnSale and SoldOut, the bell alone in the mobile bar beside the main action); "Vào danh sách chờ" (13) is still a disabled placeholder; `/queue` (06) and `/seats` (07) links point to pages built later.
- JS budget: page-only copy lives in `messages/vi.server.json` (server `t` from `@/lib/i18n.server`), because `messages/vi.json` ships with every client component on every page; a test keeps client components off the server catalog. "Xem thêm" is a server action returning rendered cards, so card/image code isn't client JS. Cross-route catalog links use `prefetch={false}` (Lighthouse counts prefetched route JS). Phone filters use a native `<dialog>` instead of a Radix Sheet. "Thêm vào lịch" is a plain link to `/events/[slug]/calendar.ics`. The FAQ uses `<details>`. Description is shown as plain paragraphs (organizer text is never rendered as HTML).
- E2E and Lighthouse run against `web/e2e/mock-api.mjs` (fixture catalog relative to start-up); CI builds with `API_URL=http://localhost:3199`.
- Dev seed: 3 cities, 15 events covering every status and category; "Sơn Tùng M-TP Live" and "Hà Anh Tuấn" have 600-seat maps; "Hà Anh Tuấn" opens 5 minutes and "Vũ Cát Tường" 20 minutes after seeding.

## Acceptance criteria

- [x] Searching "son tung" finds "Sơn Tùng M-TP Live".
- [x] 1,000 concurrent requests for one uncached event detail cause exactly one DB query (test with HybridCache).
- [x] Draft and cancelled events never appear in `/events` and return 404 on detail.
- [x] Seat-map returns 304 with a matching `If-None-Match`.
- [x] CTA switches from countdown to "Vào phòng chờ" at T-30 min without a page reload, even when the device clock is 5 minutes off.
- [x] Event detail page passes the performance budgets in 02 (Lighthouse CI, median of 5: LCP 2466 ms, TBT 168 ms, CLS 0, JS 162 KB, performance 0.92).
