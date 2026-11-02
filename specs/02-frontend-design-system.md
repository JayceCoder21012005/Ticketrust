# 02 — Frontend Design System

**Goal:** a modern, calm, fast UI that feels like DICE and Luma (clean, mobile-first, honest pricing) with Ticketmaster-grade flows for queue and seat map. Every later UI spec builds only from this system.

**Depends on:** 01

## UX references (what to borrow)

| Product | Borrow |
|---|---|
| Luma | Event page layout: big cover, title, date/venue block, sticky CTA, generous whitespace |
| DICE | All-in pricing (price shown = price paid), mobile-first, minimal checkout |
| Ticketmaster Smart Queue | Waiting room: "don't refresh", auto-updating position, alert when it's your turn, 10-min shopping window |
| Ticketmaster seat map | Zoom section → seats, price filter, legend, sticky selection summary |
| Linear / Vercel dashboards | Organizer/admin area: sidebar, dense tables, command palette |

## Setup

- `create-next-app` (TypeScript, Tailwind v4, App Router, `src/`), then `npx shadcn@latest init` (style *new-york*, base color *zinc*, CSS variables).
- `next.config.ts`: `reactCompiler: true`, `cacheComponents: true`, `images.remotePatterns` for the storage host, rewrite `/api/:path*` and `/hubs/:path*` → API URL.
- Auth/route guarding lives in `src/proxy.ts` (Next 16 replaced `middleware.ts`).
- Install shadcn components as needed, start with: button, input, label, form, card, badge, dialog, sheet, drawer, dropdown-menu, select, tabs, table, skeleton, sonner, tooltip, popover, calendar, separator, avatar, progress, alert, sidebar, chart, command.
- Extra libs: `next-themes`, `lucide-react`, `@tanstack/react-query`, `react-hook-form`, `zod`, `@hookform/resolvers`, `openapi-fetch` + `openapi-typescript` (typed API client generated from the API's OpenAPI document), `@microsoft/signalr` (lazy-loaded only).

## Folder structure

```
web/src/
  app/(public)/…           # catalog, event pages, auth
  app/(buyer)/…            # queue, seats, checkout, my tickets (auth required)
  app/organizer/…          # organizer area
  app/admin/…              # admin area
  app/gate/…               # gate scanner
  components/ui/           # shadcn (generated, do not edit by hand except tokens)
  components/shared/       # Price, CountdownTimer, EventCard, EmptyState, ErrorState, PageHeader
  features/<feature>/      # components + hooks + api for one feature
  lib/api/                 # generated schema.d.ts + client.ts (auth header, refresh on 401)
  lib/format.ts            # money, dates (vi-VN, Asia/Ho_Chi_Minh)
  lib/realtime.ts          # SignalR connection factory (dynamic import)
```

## Design tokens

- Font: **Be Vietnam Pro** via `next/font/google` (full Vietnamese diacritics), weights 400/500/600/700, `display: swap`.
- Brand primary: violet (`oklch(0.55 0.22 285)`), neutral zinc. Status colors: success emerald, warning amber, danger red, info sky.
- Seat map colors are tokens (`--seat-available-<tier>`, `--seat-held`, `--seat-sold`, `--seat-selected`) and must pass 3:1 contrast with the background in light and dark themes.
- Radius `0.75rem`, spacing on the 4px scale, max content width `1200px`.
- Light and dark themes via `next-themes`, default = system.

## Shared components (build in this spec)

`Price` (formats VND with `Intl.NumberFormat('vi-VN', { style: 'currency', currency: 'VND' })`), `CountdownTimer` (takes `expiresAt` + `serverNow`, corrects clock skew, announces the last 60 s via `aria-live`), `EventCard`, `EmptyState`, `ErrorState` (with retry), `PageHeader`, `SiteHeader` (logo, search, account menu, theme toggle), `SiteFooter`, `StickyActionBar` (mobile bottom CTA).

## UX rules

- UI language Vietnamese (copy in `messages/vi.json`), English secondary (`messages/en.json`).
- Every data view has 4 states: loading (skeleton matching the final layout, never a full-page spinner), empty, error (with retry), success.
- Never show optimistic success for inventory or money actions. Wait for the server, show the pending state on the button.
- Destructive or money actions: confirm dialog; primary buttons disable while pending (prevents double submit, C2).
- Errors: map `ProblemDetails.code` → friendly Vietnamese message in one dictionary (`lib/errors.ts`).
- Forms: react-hook-form + zod, schema mirrors the backend validator, inline errors, submit on Enter.
- Accessibility: WCAG 2.2 AA, visible focus rings, full keyboard support, `prefers-reduced-motion` respected.
- Motion: subtle (150–250 ms), View Transitions for page changes where supported.

## Performance rules (budgets enforced in CI with Lighthouse CI)

| Metric (mobile, 4G) | Budget |
|---|---|
| LCP | < 2.5 s |
| INP | < 200 ms |
| CLS | < 0.1 |
| First-load JS on public pages | < 170 KB gzip |

- Server Components by default. `'use client'` only on interactive leaves.
- Public pages (home, catalog, event detail) use `"use cache"` with tags (`event:<id>`, `events:list`); the API calls a revalidate endpoint on change.
- Heavy client code is dynamically imported: seat map, SignalR, QR scanner, charts.
- `next/image` for every image with explicit `sizes`. The LCP image gets `priority`.
- No layout shift: reserve space for images, skeletons the same size as the content.
- TanStack Query only for user-specific/live data. `staleTime` set per query, no refetch-on-focus during checkout.

## Acceptance criteria

- [ ] `/design` (dev-only) page shows all tokens, shared components and seat colors in light and dark themes.
- [ ] Typed API client generated by `npm run api:gen`. A 401 triggers one silent refresh, then a redirect to login.
- [ ] Lighthouse CI runs on `/` and `/design` and fails the build if a budget is exceeded.
- [ ] axe check passes on `/design` with zero serious violations.
- [ ] Vietnamese text renders with correct diacritics in every weight.
