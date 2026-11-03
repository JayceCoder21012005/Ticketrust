# Event Catalog & Search Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Public event catalog (API + search + cache) and the home, listing and detail pages of spec 04.

**Architecture:** Domain gains `EventSeat`, categories and a denormalized `Event.City`. Reads go through an
Application port `IEventCatalogReader` implemented in Infrastructure with EF projections and Postgres FTS
(`unaccent` + `pg_trgm` over generated columns), wrapped by a HybridCache decorator. Minimal API endpoints
expose it anonymously. Next.js server components fetch the api with `"use cache"` + `cacheTag`; client
islands do countdowns, CTA state, filters and "Xem thêm".

**Tech Stack:** .NET 10, EF Core 10 + Npgsql (+ Trigrams plugin), Microsoft.Extensions.Caching.Hybrid, Next 16.4
(`cacheComponents`), TanStack Query, Vitest, Playwright + axe, Lighthouse CI.

**Spec:** `specs/04-event-catalog-and-search.md` + `docs/superpowers/specs/2026-11-03-event-catalog-and-search-design.md`

## Global Constraints

- API routes under `/api/v1`, anonymous. Error code `event.not_found` (404).
- `/events`: only `Published` with `EndsAt > now`; keyset on `(StartsAt, Id)`; page size 20 (max 50).
- Detail cache 10 min, list 30 s, tags `event:{id}` / `events:list`; 1,000 concurrent uncached detail requests ⇒ 1 DB query.
- Detail of Draft/Cancelled ⇒ 404; Ended ⇒ 200 with `status: "Ended"` (design call).
- Seat map: `Cache-Control: public, max-age=3600` + strong ETag; matching `If-None-Match` ⇒ 304.
- Availability: `{ serverNow, version, unavailable: [], tiers: [{tierId, remaining}] }`, micro-cached 1 s.
- Read queries `AsNoTracking` + `Select` projections, never full aggregates.
- Web: Vietnamese copy only, budgets of spec 02 (JS ≤ 170 KB blocks on public pages; LCP ≤ 2.5 s; TBT ≤ 300 ms; CLS ≤ 0.1).
- CTA switches at T-30 min using server clock offset (device clock may be ±5 min off).

## Review Focus

1. Search input with only punctuation/whitespace or SQL-ish text (`'&|!`) ⇒ treated as "no query", never a 500 (websearch_to_tsquery is safe; empty after trim ⇒ no filter).
2. Malformed `cursor` / `from` > `to` / negative `priceMax` ⇒ 400 validation problem, not 500.
3. Event with no tiers (`priceFrom` null) or no phases ⇒ card/detail render, CTA "Chưa mở bán".
4. Device clock 5 min fast/slow ⇒ CTA state identical to the correct-clock case (unit test with offsets).
5. Unknown slug / GUID-looking slug / slug of cancelled event ⇒ web shows the 404 page, not an error boundary.

---

### Task 1: Domain — city, categories, seats, errors

**Files:** Modify `src/TicketRush.Domain/Events/Event.cs`; Create `Events/EventSeat.cs`, `Events/EventCategories.cs`, `Events/EventErrors.cs`; Test `tests/TicketRush.Domain.Tests/Events/*`.

**Interfaces — Produces:**
- `Event.CreateDraft(Guid organizerId, string slug, string title, string category, Venue venue, DateTimeOffset startsAt, DateTimeOffset endsAt)` sets `VenueId` and `City = venue.City`; unknown category ⇒ `DomainException("event.invalid_category")`.
- `EventCategories.All : IReadOnlyList<EventCategory(string Slug, string Name)>` — concert "Nhạc sống", festival "Lễ hội", theater "Sân khấu", sports "Thể thao", comedy "Hài kịch", workshop "Hội thảo"; `EventCategories.IsKnown(slug)`.
- `EventSeat.Create(Guid eventId, int index, string section, string row, string number, decimal x, decimal y, Guid tierId)`; index < 0 ⇒ `seat.invalid_index`.
- `EventErrors.NotFound = Error.NotFound("event.not_found", "Event not found.")`.

- [ ] Tests: city copied from venue; unknown category throws; seat with negative index throws. Run, see fail, implement, pass, commit.

### Task 2: Persistence — seats, search columns, indexes, migration

**Files:** Modify `Configurations/EventConfiguration.cs`, `AppDbContext.cs`, `IAppDbContext.cs`; Create migration `AddCatalog`; Test `tests/.../Persistence/CatalogSchemaTests.cs`.

- Migration SQL: `CREATE EXTENSION IF NOT EXISTS unaccent; pg_trgm;` function
  `catalog_search_text(title text, lineup text[], city text) RETURNS text LANGUAGE sql IMMUTABLE PARALLEL SAFE`
  = `lower(public.unaccent('public.unaccent'::regdictionary, title || ' ' || array_to_string(lineup, ' ') || ' ' || city))`;
  `immutable_unaccent(text)` same style.
- Generated stored columns on `events`: `search_text = catalog_search_text(title, lineup, city)`,
  `search_vector = to_tsvector('simple'::regconfig, search_text-expression)`; GIN on `search_vector`, GIN `gin_trgm_ops` on `search_text`.
- Indexes `(status, starts_at, id)` (exists), `(city, starts_at)`, `(category, starts_at)`; `event_seats` unique `(event_id, index)` and `(event_id, section, row, number)`.
- [ ] Test: insert "Sơn Tùng M-TP Live" ⇒ `search_text` contains `son tung m-tp live`.

### Task 3: Catalog reader + HybridCache decorator

**Files:** Create `Application/Features/Catalog/{CatalogModels.cs, IEventCatalogReader.cs, ListEventsQuery.cs (+validator), EventCursor.cs}`; `Infrastructure/Catalog/{EventCatalogReader.cs, CachedEventCatalogReader.cs, CatalogDbFunctions.cs}`; Tests: Application `EventCursorTests`, `ListEventsValidatorTests`; Integration `Catalog/CatalogReaderTests.cs`.

**Interfaces — Produces:**
- `ListEventsQuery(string? Q, string? City, string? Category, DateTimeOffset? From, DateTimeOffset? To, long? PriceMax, string? Cursor, int Limit = 20)`.
- `EventSummary(Guid Id, string Slug, string Title, string Category, DateTimeOffset StartsAt, string VenueName, string City, string? CoverImageUrl, long? PriceFrom, DateTimeOffset? NextSaleOpensAt, bool IsOnSale, bool IsSoldOut)`; `EventPage(IReadOnlyList<EventSummary> Items, string? NextCursor)`.
- `EventDetail(..., VenueDto Venue, IReadOnlyList<TierDto> Tiers, IReadOnlyList<PhaseDto> Phases, string Status, string Description, IReadOnlyList<string> Lineup, DateTimeOffset EndsAt)`; `PhaseDto` has `RequiresAccessCode` bool, never the hash.
- `SeatMap(Guid EventId, IReadOnlyList<SeatMapSection> Sections, IReadOnlyList<SeatDto> Seats)`; `SeatDto(int Index, string Section, string Row, string Number, decimal X, decimal Y, Guid TierId)`.
- `IEventCatalogReader`: `ListAsync(ListEventsQuery, DateTimeOffset now, ct)`, `GetBySlugAsync(string slug, ct) : EventDetail?`, `GetSeatMapAsync(Guid id, ct) : SeatMap?`, `GetTierCapacitiesAsync(Guid id, ct)`, `ListCitiesAsync(ct)`.
- `EventCursor.Encode(DateTimeOffset startsAt, Guid id)` / `TryDecode(string, out ...)` base64url JSON.
- Cached decorator: keys `catalog:event:{slug}` (tags `event:{id}`, `events:list` not needed), `catalog:list:{hash}` (tag `events:list`), detail 10 min / list 30 s; `ICatalogCacheInvalidator.InvalidateEventAsync(Guid id)` removes both tags.
- [ ] Integration tests: "son tung" finds Sơn Tùng; "son tunng" (typo) finds it; draft/cancelled/ended excluded; city/category/priceMax/from-to filters; keyset across 3 pages with equal `StartsAt` neither repeats nor skips; detail of cancelled ⇒ null; 1,000 concurrent `GetBySlugAsync` through the decorator ⇒ 1 command counted by a `DbCommandInterceptor`.

### Task 4: Endpoints

**Files:** Create `Api/Features/Catalog/CatalogEndpoints.cs`; modify `ApiServiceCollectionExtensions.cs`; Test `Integration/Catalog/CatalogApiTests.cs`.

- `GET /events`, `/events/{slug}`, `/events/{id:guid}/seat-map`, `/events/{id:guid}/availability`, `/categories`, `/cities`.
- Seat map ETag = `"` + base64url(SHA-256 of serialized seat map)[..22] + `"`.
- [ ] Tests: 404 code `event.not_found`; 304 on matching If-None-Match with cache headers; invalid cursor ⇒ 400; availability returns every tier with remaining = capacity / seat count and empty `unavailable`.

### Task 5: Dev seed

- Third venue "Cung thể thao Tiên Sơn" (Đà Nẵng); ~14 events over 6 categories; two seated events get generated seats (sections A/B/C × rows × seats) mapped to VIP/Standard tiers. Existing seed test updated (counts).

### Task 6: Web data layer

**Files:** `web/src/lib/catalog/{api.ts, filters.ts, cta-state.ts, ics.ts, poster.ts}` + tests; regenerate `openapi/ticketrush-api.json` and `schema.d.ts`.

- `parseFilters(URLSearchParams) / filtersToSearchParams(filters)` round-trip; unknown keys dropped.
- `computeCtaState({ status, phases, isSoldOut }, serverNowMs, availability?) → { kind: "Upcoming"|"PreQueue"|"OnSale"|"SoldOut"|"Ended"|"Cancelled"|"NotScheduled", phase?, opensAt?, nextChangeAt? }`; PreQueue window 30 min, only when `queueEnabled`.
- `buildIcs(event) : string` (RFC 5545, UTC, CRLF, escaped text).
- `posterStyle(slug, category) → { from, to, pattern }` deterministic.
- Server fetch: `fetchEvents`, `fetchEvent`, `fetchCategories`, `fetchCities` hitting `${API_URL}/api/v1/...` inside `"use cache"` with `cacheTag` / `cacheLife`.

### Task 7: Pages & components

- Components: `EventPoster`, `SaleBadge`, `EventCard` (poster fallback + badge), `CategoryChips`, `CitySelect`, `EventCta` (client, CTA state + countdown + timer to `nextChangeAt`), `AddToCalendarButton`.
- `/` home, `/events` listing (sidebar/Sheet filters, chips, debounced search, load more), `/events/[slug]` detail + `opengraph-image.tsx` + `not-found`.
- `POST /api/revalidate` route handler (secret `REVALIDATE_SECRET`, constant-time compare, `revalidateTag(tag, "max")`).
- Tests (Vitest): filters panel updates URL; load more appends; CTA flips at T-30 with fake timers; revalidate rejects bad secret.

### Task 8: E2E, Lighthouse, docs

- `web/e2e/mock-api.mjs` serving fixtures on port 3199; Playwright + LHCI start it and set `API_URL`.
- E2E: home → category chip → listing filter → detail; axe on all three; mobile sticky CTA.
- Lighthouse URLs: `/`, `/events`, `/events/son-tung-mtp-live`, `/login`, `/design`.
- README progress + specs/04 notes; full verification; commit; PR body.
