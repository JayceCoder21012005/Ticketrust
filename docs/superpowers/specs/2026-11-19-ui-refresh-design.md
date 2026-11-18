# UI refresh: discovery home, instant search, cards, motion, account hub

Date: 2026-11-19 · Status: draft for approval · Builds on specs 02 (design system), 04 (catalog & search), 10 (wallet)

## Intent and why

The owner finds the current UI "fairly good" but not distinctive: it lacks modern interaction and polished cards. When a buyer
lands, they should see **events laid out with intent** (what is hot, what opens soon, what is on this week…) under a
**prominent, very fast search**. Cards must feel crafted (shadcn components are the base), motion must feel modern, and the
colours must be researched rather than inherited. The owner supplied five Unsplash photos to make the site look richer.

## Goals

1. **Discovery home** with purposeful sections, in this order: hero with search, Hot now, Opening soon, This week, Explore by
   city, By category, For you (signed-in only).
2. **Instant search**: suggestions while typing (target: answer under 100 ms p95 on the dev box for the seeded catalog,
   server time under 30 ms), grouped results (events, artists, venues, cities), prefix matching ("son tu" → "Sơn Tùng"),
   typo tolerance, diacritics-insensitive, keyboard-first (⌘K / Ctrl+K anywhere), recent searches.
3. **One card system** used everywhere an event appears (home, listing, account), built on shadcn `Card` anatomy, consistent
   so price and date are always in the same place (NN/g: consistency for scanning).
4. **Modern motion** that costs almost no JS: shared-element morph card → event page, skeleton → content reveal, card
   hover/press feedback, pointer-following highlight, scroll reveals; all within NN/g durations and reduced-motion aware.
5. **Account hub**: one place with navigation between Overview, My tickets, Orders, Notifications, Profile; the overview
   shows the next event the buyer holds tickets for, recent orders and followed events. Avatar shown in the header.
6. **Researched colour system** (OKLCH tokens, light and dark, contrast-checked).
7. Keep every existing budget green: JS ≤ 170 KB per public page, LCP ≤ 2.5 s, CLS ≤ 0.1, TBT ≤ 300 ms, Lighthouse
   accessibility ≥ 0.9, axe clean in light and dark.

## Non-goals

- Buyer flow layouts (queue, seat picking, checkout, wallet) are **not** redesigned; they only inherit the new cards,
  tokens and motion. The organizer area and gate scanner keep their layout and get the tokens for free.
- No new search service (Meilisearch/Typesense/Orama), no AI or vector search.
- No new animation library (Motion/Framer, GSAP): the JS budget forbids it.
- No personalised ranking beyond "For you" built from followed events and their categories.

## Anti-goals (would be a failure even if it "works")

- Decorative motion that delays a buyer during a flash sale (anything over 500 ms, anything that blocks input).
- Search that is fast on the box but flickers, jumps or loses the query while typing.
- Photos that ruin LCP (the originals are 3–14 MB each).
- Breaking the e2e and axe suites, or the ISR/static nature of the public pages.

## Constraints (from the project)

- Next.js 16.4, React 19.3, Tailwind 4, shadcn (radix-ui), `tw-animate-css`; React Compiler on; `cacheComponents` on.
- Public home/listing/event pages are static/ISR; anything per-user loads on the client after hydration.
- `messages/vi.json` is bundled on every public page (copy for one area goes in that area's catalog: spec 02 / `i18n.ts`).
- Search data: `events.search_text` (unaccented, lower-cased, trigram GIN index) and `events.search_vector` (simple
  tsvector, GIN) already exist (spec 04, `catalog_search_text()`).
- CI on GitHub is paused; `scripts/ci-local.ps1` must pass before a push.

## Approaches considered

### Search engine (owner picked 1)

1. **Postgres + instant UI (chosen).** A suggestion endpoint over the existing generated columns: prefix tsquery
   (`'son':* & 'tu':*`) plus `pg_trgm` `word_similarity` for typos, ranked by match then popularity; a ⌘K palette on
   shadcn `Command`. No new service; Postgres answers in tens of ms for thousands of events.
2. Meilisearch: best typo tolerance and relevance out of the box, but a new service, outbox sync, more RAM (the dev box
   already runs out with the production stack and Aspire together). Worth it at hundreds of thousands of items.
3. Orama in the browser: ~24 KB gzip plus the index on every page (JS budget), data goes stale against ticket counts.

### Overall scope (owner picked 2)

1. Polish in place (cards + motion only) — safe but not distinctive.
2. **New home + card system + motion (chosen)**, extended by the owner with the account hub, search and colours.
3. Whole new visual language for every area — most distinctive, hundreds of files and most e2e/axe rewrites.

## Design

### 1. Colour system (my call, from the research)

- Keep the **violet brand** (`--primary` hue 285): it is already in the logo, emails and e2e snapshots, and purple reads
  "entertainment / premium" in event products. Retune it in **OKLCH** so every shade has predictable lightness:
  light `--primary: oklch(0.55 0.22 285)` stays; dark mode raises lightness (`oklch(0.72 0.17 285)`) and lowers chroma so it
  does not glow on near-black (research: colours look more saturated on dark backgrounds; dark primaries need higher L).
- Add an **accent "live" hue** for urgency and energy: hot pink/coral `oklch(0.68 0.21 15)` (dark `oklch(0.74 0.17 15)`),
  used only for "Hot", "selling fast", countdowns and the live sale dot — never for body text.
- A **signature gradient** `violet → pink` (`from-primary to-accent-live`) used sparingly: hero search glow, Hot badge,
  focus/active ring of the search palette, the card's pointer highlight.
- Neutrals: slightly cool (hue 285, chroma ≤ 0.01) instead of pure grey, so surfaces feel part of the brand.
- Semantic tokens unchanged in name (`success`, `warning`, `destructive`), retuned to the same lightness steps.
- Every text/background pair is checked: WCAG AA 4.5:1 for body, 3:1 for large text and UI borders; the existing
  `design-tokens.test.ts` is extended to assert these pairs for light and dark.

### 2. Photos (owner's five Unsplash images)

- Originals stay out of git. A one-off script (`web/scripts/prepare-photos.mjs`, using `sharp`, already a Next dependency)
  writes 2560 px max, quality-tuned JPEGs into `web/public/photos/` (target ≤ 400 KB each); `next/image` serves AVIF/WebP
  at the needed sizes.
- The photos show Venice, a US small town, Cinque Terre, a Bavarian village and Bukchon (Seoul), and the catalog's cities are
  Vietnamese (the seed has three: Hà Nội, Hồ Chí Minh, Đà Nẵng). They are therefore used as **mood backdrops only, never
  labelled as a city**: hero background (rotating daily among the five, each with a dark gradient veil for contrast), auth
  pages artwork, the "Hot now" band background (blurred, low opacity) and empty states. Event posters are never replaced.
- "Explore by city" tiles get a generated treatment instead: city name, event count, the brand gradient tinted per city.
- Credit: a small "Ảnh nền: Unsplash" line in the footer (the licence does not require it; it is courteous).

### 3. Discovery home (`/`)

```
┌────────────── HERO (photo + gradient veil) ───────────────┐
│  Săn vé nhanh, công bằng.                                  │
│  [🔍 Tìm sự kiện, nghệ sĩ, địa điểm…            ⌘K ]      │
│  chips: Concert · Thể thao · Sân khấu · Hà Nội · TP.HCM    │
└────────────────────────────────────────────────────────────┘
🔥 Đang hot        [card lớn][card][card][card]   (bento: 1 large + 3)
⏰ Sắp mở bán      [card + countdown] × 4           (horizontal scroll on mobile)
📅 Tuần này        [card] × 6
🏙 Khám phá theo thành phố  [city tile: tên + số sự kiện] × N
🎭 Theo thể loại   [category tiles]
✨ Dành cho bạn    (signed-in, client-side; hidden when empty)
```

- **Hot score** (server, recomputed in the catalog read model, cached with the home ISR): tickets sold in the last 24 h
  (paid orders) × 3 + sold ratio of the event × 100 + followers × 0.5, only events on sale or opening within 7 days.
  Exposed as `GET /api/v1/catalog/home` returning the sections already picked (one call, ISR 30 s as today), so the page
  stays static and the ranking stays on the server.
- Sections with no events are not rendered; the page never shows an empty heading.
- "For you": after hydration, for a signed-in buyer, events in categories of the events they follow, excluding ones
  already in their tickets; loaded from a small authenticated endpoint; no layout shift (reserved height only when the
  section has items, revealed with the scroll animation).

### 4. Instant search

- **Endpoint** `GET /api/v1/search/suggest?q=` (anonymous, `global` rate-limit policy, `Cache-Control: public, max-age=30`):
  returns up to 6 events, 4 artists (from `lineup`), 3 venues, 3 cities, each with the matched part for highlighting.
  Query: normalise with the same `catalog_search_text()`; candidates where `search_vector @@ prefix tsquery` OR
  `search_text %> q` (trigram word similarity); rank `ts_rank` + similarity + hot score; published, not ended.
  Minimum 2 characters; empty query returns trending searches (top hot events). Target server time < 30 ms
  (measured by an integration test over the seeded catalog and a 5,000-event synthetic set).
- **UI**: the hero search and a header search button both open one **command palette** (shadcn `Command` in a `Dialog`,
  ⌘K/Ctrl+K anywhere, `/` on the listing page). Typing debounces 120 ms, cancels the previous request (AbortController),
  keeps showing the last results while the next load (no flicker), highlights matches, groups by type, arrow keys + Enter
  navigate, Esc closes. Recent searches (last 5) in `localStorage`, clearable. "See all results" goes to
  `/events?q=` (the existing listing search remains the full results page).
- The palette code is **lazy-loaded** on first open/focus so it does not count against the public JS budget.
- Accessible: combobox pattern from `cmdk`, live region announcing result counts.

### 5. Card system

- `EventCard` rebuilt on shadcn `Card`: 16:9 poster, status chip (On sale / Opening in 2 h / Sold out / Hot), title (2 lines),
  date-time, venue · city, and a fixed footer row "Từ 800.000 ₫" + follow button. Same anatomy everywhere; variants only
  change size (`default`, `large` for the bento lead, `compact` for horizontal rows and the account hub).
- Whole card is the link (NN/g); the follow button is a separate, clearly secondary control.
- Interaction (CSS only): hover lifts 2 px with a soft shadow and the poster zooms 3%, 200 ms ease-out; press scales to
  0.98 for 100 ms; a pointer-following radial highlight on the border (a CSS variable set by one shared pointermove
  listener, ~1 KB); focus ring uses the brand gradient. Sold-out cards are desaturated.
- Poster images keep fixed aspect boxes (CLS) and `priority` only for the first card above the fold.

### 6. Motion (no animation library)

- **Shared element** card poster → event page hero with React 19.3 `<ViewTransition name="poster-{slug}">` (works in the
  App Router with no config, per the Next 16.4 guide); title morphs too.
- **Suspense reveal**: skeletons cross-fade into content (`<ViewTransition>` around Suspense boundaries).
- **Scroll reveal**: sections fade/slide 12 px in with CSS `animation-timeline: view()` and bounded `animation-range`;
  falls back to visible where unsupported.
- Durations from NN/g: 100 ms feedback, 200–300 ms enter, exits ~25% shorter, ease-out to enter.
- `prefers-reduced-motion: reduce` turns all of it off (view transitions included).

### 7. Account hub (`/account/*`) — my call

```
┌ sidebar (desktop) / tabs (mobile) ┐ ┌──────── Tổng quan ─────────────────────┐
│ ◉ Tổng quan                        │ │ [avatar] Xin chào, An  · email ✔      │
│ 🎟 Vé của tôi                       │ │ Sự kiện sắp tới của bạn (card lớn +   │
│ 🧾 Đơn hàng                         │ │   "Mở vé" → wallet, đếm ngược)         │
│ 🔔 Thông báo                        │ │ Đơn gần đây (3 dòng) · Xem tất cả     │
│ 👤 Hồ sơ                            │ │ Bạn đang theo dõi (compact cards)      │
└────────────────────────────────────┘ └─────────────────────────────────────────┘
```

- `/account` becomes the overview; the profile form and avatar move to `/account/profile`.
- Avatar: crop-to-square preview before upload (client-side canvas, no library), shown in the header account button.
- Unconfirmed email: a banner with "Gửi lại email" at the top of every hub page.

## Left to the builder

- Exact OKLCH values after contrast tests; exact hot-score weights after looking at the seeded data (keep the formula's
  shape).
- File layout of the new components; whether the suggest endpoint lives in Catalog or its own feature folder.
- Which photo is the default hero and the rotation order.
- How `design-tokens.test.ts` asserts contrast (compute from OKLCH in the test).

## Verification

- Unit/component tests for the card, palette (keyboard, debounce/abort, recent searches), hot ranking, account hub.
- Integration tests: suggest endpoint (prefix, typo, diacritics, grouping, timing budget), home sections endpoint.
- E2E: home sections render, palette search → event page with view transition, account hub navigation; axe light/dark.
- `scripts/ci-local.ps1` green, including Lighthouse budgets.
