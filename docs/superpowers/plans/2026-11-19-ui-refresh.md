# UI Refresh Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.
> Owner's standing rule for this repo: **no subagents** — execute natively (superpowers:executing-plans), one task at a time.

**Goal:** A discovery home with purposeful sections, an instant ⌘K search, one crafted event card, near-zero-JS motion, an
account hub, and a researched OKLCH colour system — inside every existing performance and accessibility budget.

**Architecture:** Two new anonymous read endpoints (`GET /api/v1/catalog/home`, `GET /api/v1/search/suggest`) on the existing
Postgres search columns, plus one authenticated (`GET /api/v1/me/follows`). The web keeps public pages static/ISR; the search
palette and every per-user part load on the client after hydration. Motion uses React 19.3 `<ViewTransition>` and CSS
scroll timelines, no animation library.

**Tech Stack:** .NET 10 Minimal API, EF Core/Npgsql (`pg_trgm`, tsvector), HybridCache; Next.js 16.4, React 19.3, Tailwind 4,
shadcn (radix-ui, `cmdk`), Vitest, Playwright, xUnit + Testcontainers.

**Spec:** `docs/superpowers/specs/2026-11-19-ui-refresh-design.md`

## Global Constraints

- Public pages: JS ≤ 174,080 bytes (`resource-summary:script:size`), LCP ≤ 2,500 ms, CLS ≤ 0.1, TBT ≤ 300 ms, Lighthouse
  performance ≥ 0.9 and accessibility ≥ 0.9 (`web/lighthouserc.json`); axe: no serious/critical violations, light and dark.
- No new animation or search dependency (no Motion/Framer, GSAP, Meilisearch, Typesense, Orama). `cmdk` and `sharp` are
  already installed.
- Motion: feedback 100 ms, enter 200–300 ms, exit ~25% shorter, ease-out to enter, nothing over 500 ms;
  `prefers-reduced-motion: reduce` disables all of it, view transitions included.
- Text contrast WCAG AA: 4.5:1 body, 3:1 large text and UI borders, light and dark.
- Copy: Vietnamese UI strings live in `web/messages/*.json` by area (`vi.json` only for public chrome; buyer area in
  `vi.buyer.json`; server-only in `vi.server.json`), `en.*` kept key-for-key in sync.
- Photos: originals in `/assets` never committed; Unsplash photos are mood backdrops only, never labelled as a city.
- Buyer-flow layouts (queue, seats, checkout, wallet) unchanged.
- Before any push: `powershell -File scripts/ci-local.ps1` green (GitHub CI is paused).

## Review Focus

1. A search query of only punctuation, quotes, `%`, `&`, `:*`, emoji or 100+ characters → 200 with empty groups, never a 500
   or a tsquery syntax error (Task 3).
2. Fast typing: a slower earlier response must never replace the results of a later query; the input never loses characters
   (Task 6).
3. A catalog where a section has no events (no hot, nothing this week, a brand-new install with zero events) → no empty
   heading, no layout hole (Tasks 2 and 7).
4. Dark mode: the new live accent and the gradient text/borders keep AA contrast; sold-out (desaturated) cards still read
   (Task 1, Task 5).
5. Reduced-motion users get no card lift, no scroll reveal, no view-transition morph (Task 8).

---

### Task 1: Colour tokens (OKLCH, live accent, gradient)

**Files:**
- Modify: `web/src/app/globals.css` (`:root` and `.dark` blocks, `@theme inline`)
- Test: `web/src/app/design-tokens.test.ts`

**Interfaces:**
- Produces: CSS tokens `--live`, `--live-foreground` (Tailwind `bg-live`, `text-live`, `text-live-foreground`); utility
  classes `bg-brand-gradient` (linear 135deg primary → live) and `text-brand-gradient` (same, clipped to text).

- [ ] **Step 1: Failing tests** in `design-tokens.test.ts`, inside the existing `describe.each([":root", ".dark"])`:
  `live passes 3:1 against the background` (`contrastRatio(t.live, t.background) >= 3`),
  `live-foreground passes 4.5:1 on live`, and `primary passes 4.5:1 against card` (links inside cards).
- [ ] **Step 2: Run** `npx vitest run src/app/design-tokens.test.ts` (PowerShell, in `web/`). Expected: FAIL, `--live is missing`.
- [ ] **Step 3: Implement.** Light: `--live: oklch(0.6 0.22 15)`, `--live-foreground: oklch(0.985 0 0)`. Dark: `--primary:
  oklch(0.72 0.17 285)`, `--live: oklch(0.74 0.17 15)`, `--live-foreground: oklch(0.2 0.05 15)`. Tint neutrals to hue 285 with
  chroma ≤ 0.01 (`--background`, `--card`, `--muted`, `--border` in both blocks). Map `--color-live*` in `@theme inline`; add
  the two gradient utilities with `@utility`. Adjust lightness only (never hue) until the tests pass.
- [ ] **Step 4: Run** the test file. Expected: PASS (all existing pairs too).
- [ ] **Step 5: Commit** `feat(ui): OKLCH tokens with a live accent and brand gradient`.

### Task 2: Home sections endpoint with a hot score

**Files:**
- Create: `src/TicketRush.Application/Features/Catalog/Home.cs` (models + handler)
- Modify: `IEventCatalogReader.cs` (add `GetHomeAsync`), `EventCatalogReader.cs`, `CachedEventCatalogReader.cs`,
  `src/TicketRush.Api/Features/Catalog/CatalogEndpoints.cs`
- Test: `tests/TicketRush.Api.IntegrationTests/Catalog/HomeApiTests.cs`; regenerate `web/openapi/ticketrush-api.json`
  (`UPDATE_OPENAPI=1`) and `web/src/lib/api/schema.d.ts` (`npm run api:gen`)

**Interfaces:**
- Produces: `GET /api/v1/catalog/home` → `HomeSections(IReadOnlyList<EventSummary> Hot, IReadOnlyList<EventSummary>
  OpeningSoon, IReadOnlyList<EventSummary> ThisWeek, IReadOnlyList<EventSummary> Upcoming, IReadOnlyList<CityCount> Cities,
  IReadOnlyList<CategoryCount> Categories)`; `CityCount(string City, int Events)`; `CategoryCount(string Category, int Events)`.
  `Task<HomeSections> IEventCatalogReader.GetHomeAsync(CancellationToken ct)`. Limits: Hot 4, OpeningSoon 4, ThisWeek 6,
  Upcoming 6. Cache like the list (HybridCache, 30 s), `Cache-Control: public, max-age=30`.

- [ ] **Step 1: Failing tests** in `HomeApiTests` (seed with `DevDataSeeder`, then arrange with SQL/EF):
  `Hot_ranks_by_recent_sales_then_sold_ratio_then_followers` (two on-sale events; give one 5 tickets created in the last 24 h →
  it is `Hot[0]`); `Hot_only_holds_events_on_sale_or_opening_within_7_days`; `This_week_holds_events_starting_in_the_next_7_days_ordered_by_start`;
  `Opening_soon_matches_the_listing_rule` (sale not open, not sold out, ordered by `NextSaleOpensAt`);
  `Cities_and_categories_count_published_upcoming_events`; `An_empty_catalog_returns_empty_lists_not_an_error`.
- [ ] **Step 2: Run** `dotnet test --project tests/TicketRush.Api.IntegrationTests -c Release -- --filter-class "*HomeApiTests"`. Expected: FAIL (404).
- [ ] **Step 3: Implement** `GetHomeAsync`. Hot score per event = `3 × tickets created in the last 24 h` + `100 × sold / capacity`
  (seated: seats count; GA: tier capacity) + `0.5 × followers`; candidates are on sale now or with a phase opening within 7 days,
  not sold out; ties by `StartsAt`. One round trip per list is fine (catalog is small); reuse the `EventSummary` projection of
  `ListAsync`.
- [ ] **Step 4: Run** the tests. Expected: PASS. Regenerate OpenAPI + `schema.d.ts`; run `*OpenApiDocumentTests`. Expected: PASS.
- [ ] **Step 5: Commit** `feat(catalog): home sections endpoint with a hot ranking`.

### Task 3: Search suggestions endpoint

**Files:**
- Create: `src/TicketRush.Application/Features/Catalog/Suggest.cs`, `src/TicketRush.Api/Features/Catalog/SearchEndpoints.cs`
- Modify: `IEventCatalogReader.cs` (`SuggestAsync`), `EventCatalogReader.cs`, `CatalogDbFunctions.cs` (prefix query helper),
  `ApiServiceCollectionExtensions.cs` (map), OpenAPI files as in Task 2
- Test: `tests/TicketRush.Api.IntegrationTests/Catalog/SuggestApiTests.cs`

**Interfaces:**
- Produces: `GET /api/v1/search/suggest?q=` (anonymous; `q` 0–100 chars) →
  `SearchSuggestions(IReadOnlyList<EventSuggestion> Events, IReadOnlyList<string> Artists, IReadOnlyList<VenueSuggestion> Venues,
  IReadOnlyList<string> Cities)`; `EventSuggestion(string Slug, string Title, DateTimeOffset StartsAt, string VenueName, string City,
  string? CoverImageUrl)`; `VenueSuggestion(string Name, string City)`. Limits 6 / 4 / 3 / 3. `q` shorter than 2 normalised
  characters → `Events` = top 6 of the hot ranking (Task 2), other groups empty. `Cache-Control: public, max-age=30`.

- [ ] **Step 1: Failing tests:** `Prefix_of_words_finds_the_event` (`"son tu"` → `son-tung-mtp-live` first);
  `Diacritics_and_case_do_not_matter` (`"SƠN TÙNG"`, `"son tung"`); `A_typo_still_finds_it` (`"son tng"`);
  `Artists_venues_and_cities_are_grouped` (`"my dinh"` → venue `Sân vận động Mỹ Đình`, city absent unless matched);
  `Ended_draft_and_cancelled_events_never_appear`; `Short_query_returns_hot_events`;
  `Hostile_queries_return_empty_groups_not_errors` (Review Focus 1: `"'"`, `"%"`, `"&|!"`, `"a:*"`, `"🎤"`, 100 × `"x"` → 200);
  `Answers_within_30_ms_on_5000_events` (insert 5,000 synthetic published events, warm up once, median of 20 calls of the
  handler < 30 ms).
- [ ] **Step 2: Run** with `--filter-class "*SuggestApiTests"`. Expected: FAIL (404).
- [ ] **Step 3: Implement.** Normalise `q` with `catalog_search_text(q, '{}', '')` in SQL; build the prefix tsquery from
  normalised words that are `[a-z0-9]+` only (drop everything else, so no user text reaches `to_tsquery` syntax):
  `word1:* & word2:*`. Events: `search_vector @@ to_tsquery('simple', prefix) OR word_similarity(q, search_text) > 0.4`,
  order by `ts_rank` + `word_similarity` desc then hot score. Artists: distinct `lineup` elements whose unaccented lower form
  starts with or is word-similar to `q`. Venues: same on `venues.name`. Cities: distinct `events.city` likewise. Published and
  not ended only.
- [ ] **Step 4: Run** the tests. Expected: PASS. Regenerate OpenAPI + `schema.d.ts`; `*OpenApiDocumentTests` PASS.
- [ ] **Step 5: Commit** `feat(search): grouped, typo-tolerant suggestions endpoint`.

### Task 4: My follows endpoint

**Files:**
- Modify: `src/TicketRush.Api/Features/Follows/FollowEndpoints.cs`, the follows Application feature (list query)
- Test: `tests/TicketRush.Api.IntegrationTests/Follows/` (new test in the existing follow API test class)

**Interfaces:**
- Produces: `GET /api/v1/me/follows` (authenticated, `NoStoreFilter`) → `IReadOnlyList<EventSummary>` of followed events that
  are published and not ended, ordered by `StartsAt`, max 20.

- [ ] **Step 1: Failing test** `My_follows_lists_upcoming_followed_events_only` (follow two events, end one → only the other).
- [ ] **Step 2: Run** it. Expected: FAIL (404).
- [ ] **Step 3: Implement** with the same `EventSummary` projection as the catalog.
- [ ] **Step 4: Run** it, regenerate OpenAPI + `schema.d.ts`. Expected: PASS.
- [ ] **Step 5: Commit** `feat(follows): list my followed events`.

### Task 5: Event card v2

**Files:**
- Modify: `web/src/components/shared/event-card.tsx`
- Create: `web/src/components/shared/card-spotlight.ts` (one shared `pointermove` listener setting `--x/--y` on the hovered
  `[data-spotlight]` element), `web/src/components/shared/event-card.test.tsx`
- Modify: every `EventCard` caller (`home-sections.tsx`, listing grid) to pass the variant

**Interfaces:**
- Produces: `EventCard({ event, variant = "default" | "large" | "compact", priority, headingLevel, className })` with
  `EventCardData` extended by `isHot?: boolean`; the poster wrapper carries `style={{ viewTransitionName: undefined }}` slot for
  Task 8 via a `posterTransitionName` prop (`string | undefined`).

- [ ] **Step 1: Failing tests:** one link whose accessible name is the title; status chip text for on-sale / opening
  (`"Mở bán sau …"` from `SaleBadge`) / sold out / hot (`"Đang hot"`); price row always last; sold-out card has
  `data-sold-out` and the grayscale class; `compact` has no poster `priority`.
- [ ] **Step 2: Run** `npx vitest run src/components/shared/event-card.test.tsx`. Expected: FAIL.
- [ ] **Step 3: Implement** on shadcn `Card` (`Card`, `CardContent`, `CardFooter`); hover `-translate-y-0.5 shadow-lg` +
  poster `scale-[1.03]` 200 ms ease-out; `active:scale-[0.98]` 100 ms; spotlight border via
  `radial-gradient(circle at var(--x) var(--y), …)` on a pseudo element; gradient focus ring; all transforms inside
  `motion-safe:`. Hot chip uses `bg-live text-live-foreground`. Add copy key `catalog.card.hot`.
- [ ] **Step 4: Run** the card tests and `npx vitest run src/features/catalog`. Expected: PASS.
- [ ] **Step 5: Commit** `feat(ui): event card v2 with spotlight and status chips`.

### Task 6: Search palette (⌘K)

**Files:**
- Create: `web/src/features/search/search-palette.tsx` (dialog + `Command`), `web/src/features/search/use-suggestions.ts`,
  `web/src/features/search/recent-searches.ts`, `web/src/features/search/search-trigger.tsx` (button + ⌘K/Ctrl+K listener,
  lazy-imports the palette), tests next to each
- Modify: `web/src/components/shared/site-header.tsx` (replace the header search box with `SearchTrigger`),
  `web/src/lib/catalog/api.ts` (`getSuggestions(q, signal)`)

**Interfaces:**
- Consumes: Task 3 `SearchSuggestions`.
- Produces: `SearchTrigger({ variant: "header" | "hero" })`; `useSuggestions(query: string): { data: SearchSuggestions | null;
  loading: boolean }` (debounce 120 ms, `AbortController`, keeps last data while loading); `recentSearches.get(): string[]`,
  `.add(q)`, `.clear()` (localStorage key `tr-recent-searches`, max 5, every access in try/catch).

- [ ] **Step 1: Failing tests:** `useSuggestions` — debounces (one request for "son" typed fast), aborts the previous request,
  **a slow earlier response never replaces a later one** (Review Focus 2: resolve "so" after "son" → shows "son"), keeps
  previous results while loading; palette — ⌘K and Ctrl+K open it, Esc closes, arrow + Enter navigates to `/events/{slug}`,
  groups have headings "Sự kiện", "Nghệ sĩ", "Địa điểm", "Thành phố", matched text wrapped in `<mark>`, "Xem tất cả kết quả"
  goes to `/events?q=`, recent searches shown on empty input and clearable; storage throwing does not break it.
- [ ] **Step 2: Run** `npx vitest run src/features/search`. Expected: FAIL.
- [ ] **Step 3: Implement.** Palette in a shadcn `Dialog` with `Command shouldFilter={false}` (server ranks); trigger imports it
  with `next/dynamic` on first focus/hover/shortcut; live region announces "N kết quả"; `/` also opens it on `/events`. The palette is public, so its copy goes under
  `search.*` in `vi.json` / `en.json` (keep it short: the catalog ships on every public page).
- [ ] **Step 4: Run** the tests, `npm run typecheck`, `npm run lint`. Expected: PASS.
- [ ] **Step 5: Commit** `feat(search): instant ⌘K search palette`.

### Task 7: Discovery home and photos

**Files:**
- Create: `web/scripts/prepare-photos.mjs`, `web/public/photos/{venice,main-street,cinque-terre,bavaria,bukchon}.jpg`,
  `web/src/features/catalog/home/hero.tsx`, `city-tiles.tsx`, `category-tiles.tsx`, `for-you.tsx` (client), tests
- Modify: `web/src/app/(public)/page.tsx`, `web/src/features/catalog/home/home-sections.tsx` (use Task 2 endpoint),
  `web/src/lib/catalog/api.ts` (`getHomeSections()`), `web/src/components/shared/site-footer.tsx` ("Ảnh nền: Unsplash"),
  `web/e2e/home.spec.ts` (or the spec that covers `/`), `.gitignore` (`/assets/`)

**Interfaces:**
- Consumes: Task 2 `HomeSections`, Task 4 `/me/follows`, Task 5 `EventCard`, Task 6 `SearchTrigger variant="hero"`.
- Produces: `heroPhotoFor(date: Date): Photo` (`{ src, alt }`, rotates daily over the five, deterministic).

- [ ] **Step 1: Failing tests:** sections render in the spec order with headings; **a section with no events renders nothing**
  (Review Focus 3, including all-empty → the existing empty state); hero has the search trigger and an `<Image priority>`
  backdrop with `alt=""`; `heroPhotoFor` is stable for a day and changes the next; city tiles show name + count and link to
  `/events?city=`; "Dành cho bạn" is absent when signed out and when the follows list is empty.
- [ ] **Step 2: Run** `npx vitest run src/features/catalog/home`. Expected: FAIL.
- [ ] **Step 3: Implement.** `prepare-photos.mjs`: `sharp(...).resize({ width: 2560, withoutEnlargement: true }).jpeg({ quality:
  72, mozjpeg: true })`, fail if any output > 400 KB. Hero: photo with `bg-gradient-to-t from-background` veil, headline, hero
  search, category chips. Hot band: `EventCard variant="large"` + 3 default (bento grid `lg:grid-cols-4`, large spans 2×2).
  Hot band background: another of the five photos, blurred and at low opacity behind the cards. Opening soon keeps
  `SaleCountdown`. City tiles: brand gradient with the hue rotated per city index. For you: client component fetching
  `/me/follows` categories → `/events?category=` first page, minus followed events and events in `/me/tickets`, hidden until
  it has items.
- [ ] **Step 4: Run** unit tests; `ENABLE_DESIGN_PAGE=1 npm run build:e2e`; `npx playwright test e2e/home.spec.ts` (update its
  selectors to the new headings); `npm run lighthouse`. Expected: PASS, budgets green.
- [ ] **Step 5: Commit** `feat(home): discovery home with hot ranking, cities, categories and photos`.

### Task 8: Motion

**Files:**
- Modify: `web/src/components/shared/event-card.tsx` (poster in `<ViewTransition name={"poster-" + slug}>`),
  `web/src/features/catalog/detail/event-detail-view.tsx` (hero poster, same name), `web/src/app/(public)/page.tsx` (Suspense
  boundaries wrapped in `<ViewTransition>`), `web/src/app/globals.css` (view-transition and scroll-reveal CSS)
- Test: `web/e2e/motion.spec.ts`

**Interfaces:**
- Consumes: Task 5 `posterTransitionName`.
- Produces: CSS class `reveal` (scroll-driven fade/slide 12 px, `animation-timeline: view()`, `animation-range: entry 0% cover
  30%`), applied to home sections.

- [ ] **Step 1: Failing e2e:** clicking a home card on Chromium starts a view transition (`document.startViewTransition`
  observed via an init script, or `::view-transition-group(poster-<slug>)` present) and lands on the event page; with
  `page.emulateMedia({ reducedMotion: "reduce" })` no transition runs and `.reveal` sections are fully visible
  (Review Focus 5); axe still clean.
- [ ] **Step 2: Run** `npx playwright test e2e/motion.spec.ts`. Expected: FAIL.
- [ ] **Step 3: Implement** per the Next 16.4 guide (`node_modules/next/dist/docs/01-app/02-guides/view-transitions.md`):
  `import { ViewTransition } from "react"`; durations 250 ms enter / 180 ms exit, `cubic-bezier(0.2, 0, 0, 1)`; wrap everything
  in `@media (prefers-reduced-motion: no-preference)` and set `::view-transition-group(*) { animation: none }` under `reduce`.
- [ ] **Step 4: Run** the motion e2e, the full `npm run test:e2e`, `npm run lighthouse`. Expected: PASS.
- [ ] **Step 5: Commit** `feat(ui): poster morph, suspense reveal and scroll reveal`.

### Task 9: Account hub

**Files:**
- Create: `web/src/app/(buyer)/account/layout.tsx` (sidebar on `lg`, scrollable tabs below), `web/src/app/(buyer)/account/profile/page.tsx`,
  `web/src/features/account/account-nav.tsx`, `web/src/features/account/overview.tsx`, `web/src/features/account/avatar-crop.tsx`, tests
- Modify: `web/src/app/(buyer)/account/page.tsx` (overview), `web/src/features/account/account-view.tsx` (becomes the profile
  page body), `web/src/components/shared/account-link.tsx` (avatar image when set), e2e specs that visit `/account`

**Interfaces:**
- Consumes: `/me/tickets` (`MyTicketsResponse`), `/me/orders` (existing list), Task 4 `/me/follows`, Task 5 `EventCard compact`.
- Produces: `cropToSquare(file: File, size = 512): Promise<Blob>` (canvas, `createImageBitmap(file, { imageOrientation:
  "from-image" })`, centre crop, `image/webp` 0.9).

- [ ] **Step 1: Failing tests:** nav lists Tổng quan, Vé của tôi, Đơn hàng, Thông báo, Hồ sơ with `aria-current="page"` on the
  active one; overview shows the next upcoming ticketed event (soonest `StartsAt` in the future) with a link to its first ticket,
  the 3 latest orders, followed events; empty states for each; unconfirmed-email banner with "Gửi lại email" on every hub page;
  header shows the avatar image when `avatarUrl` is set, initials otherwise; `cropToSquare` returns a 512 × 512 webp for a
  landscape, a portrait and an EXIF-rotated input.
- [ ] **Step 2: Run** `npx vitest run src/features/account src/components/shared`. Expected: FAIL.
- [ ] **Step 3: Implement.** Avatar upload: preview the crop in a shadcn `Dialog`, upload the cropped blob (the api accepts webp).
  The layout reuses `proxy.ts`'s auth redirect (no new guard).
- [ ] **Step 4: Run** unit tests, `npx playwright test e2e/account*.spec.ts e2e/auth.spec.ts`. Expected: PASS.
- [ ] **Step 5: Commit** `feat(account): account hub with overview, nav and cropped avatars`.

### Task 10: Auth artwork and final verification

**Files:**
- Modify: the `(auth)` layout artwork panel to use `/photos/bukchon.jpg` with the veil; `web/lighthouserc.json` untouched.

- [ ] **Step 1: Failing test** in the auth layout test: artwork renders an `<Image>` of `/photos/bukchon.jpg` with `alt=""`
  and is hidden below `lg`.
- [ ] **Step 2: Run** it. Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** `powershell -File scripts/ci-local.ps1` (Docker running; close other heavy apps — the build needs ~3 GB
  free). Expected: "All CI checks passed".
- [ ] **Step 5: Commit** `feat(auth): photo artwork`; update `README.md` ("Những gì có thể thử": ⌘K search, hot section) and push.
