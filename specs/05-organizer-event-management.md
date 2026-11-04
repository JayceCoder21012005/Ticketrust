# 05 — Organizer Event Management

**Goal:** organizers create events with tiers, seat layouts and sale phases, and publish only when everything is valid.

**Depends on:** 04

## Scope

- In: venues, seat layout generator, event draft wizard, tiers, section → tier mapping, sale phases with presale codes, cover upload, publish, cancel.
- Out: drag-and-drop seat drawing (use a generator + JSON import), dynamic pricing.

## Seat layout generator

An organizer defines sections as data. The server generates `EventSeat`s:

```json
{ "sections": [
  { "code": "A", "tierId": "…", "rows": 10, "seatsPerRow": 20, "origin": { "x": 100, "y": 80 }, "curve": 0.15 },
  { "code": "GA", "tierId": "…", "kind": "standing" }
] }
```

Standing sections map to `GeneralAdmission` tiers (no seats). The generator assigns `Index` 0..n-1 in a stable order (section, row, number). A JSON import of the same shape is accepted for custom layouts.

## API (`/organizer`, policy `RequireOrganizer`, ownership enforced)

| Method | Route | Notes |
|---|---|---|
| GET/POST | `/organizer/venues` | Create venues |
| GET/POST | `/organizer/events` | Create a draft `{title, venueId, startsAt, endsAt, category}` |
| GET/PUT | `/organizer/events/{id}` | Edit details (draft: everything; published: text and cover only) |
| PUT | `/organizer/events/{id}/tiers` | Replace the tier list (draft only) |
| PUT | `/organizer/events/{id}/layout` | Generate seats from the layout JSON (draft only) |
| PUT | `/organizer/events/{id}/phases` | Replace phases (presale codes hashed with SHA-256 + pepper) |
| POST | `/organizer/events/{id}/cover-upload-url` | Presigned upload, image ≤ 5 MB |
| POST | `/organizer/events/{id}/publish` | Runs `PublishChecklist`, then `Draft → Published`, warms Redis inventory (07) |
| POST | `/organizer/events/{id}/cancel` | `Published → Cancelled`, emits `EventCancelled` (refunds handled in 08) |

Error codes: `event.not_editable`, `event.publish_checklist_failed` (with `failures[]`), `event.phase_overlap`, `layout.invalid`.

## Rules

- `PublishChecklist` (each rule is a small class implementing `IPublishRule`): has cover, ≥ 1 tier, every seated tier has ≥ 1 seat, every GA tier has capacity > 0, ≥ 1 sale phase, the first phase opens ≥ 10 min in the future, phases don't overlap.
- After publish, price, capacity, layout and phases are **frozen** (prevents changing inventory while Redis counters are live). To change them, cancel and recreate. This keeps v1 safe.
- Publishing is idempotent: publishing an already published event returns 200 and changes nothing.
- Redis warm-up happens through the outbox (`EventPublished` → 07's `InventoryWarmer`), not inline.

## Frontend (`/organizer`)

- Layout: shadcn `Sidebar` (Sự kiện, Địa điểm, Dashboard, Cài đặt), breadcrumb header.
- Events list: table with status badges, search, "Tạo sự kiện".
- **Event wizard** (steps in a left stepper, each step autosaves the draft):
  1. Thông tin (title, category, dates, venue, description editor with preview, cover upload with crop to 16:9)
  2. Hạng vé (editable table: name, price, kind, capacity, colour)
  3. Sơ đồ (section form → live SVG preview of the generated layout, seat count per tier)
  4. Mở bán (phases: time pickers in Vietnam time, queue toggle, max tickets per user, presale code)
  5. Kiểm tra & Xuất bản (checklist with ✓/✗ links back to the failing step)
- Cancel event: a dialog where the organizer must type the event title to confirm.

## Implementation notes

- UX decisions from organizer feedback (Capterra/G2 reviews of Eventbrite, SimpleTix, seats.io; Luma; Ticket Tailor and Humanitix publish flows): `docs/superpowers/specs/2026-11-03-organizer-event-management-design.md`. Quick create (5 fields → draft), layout presets, buyer preview on the review step, phase timeline.
- Routes: the spec's `GET/PUT /organizer/events/{id}` is `GET /{id}` plus `PUT /{id}/info` (title, description, lineup, cover: Draft and Published) and `PUT /{id}/schedule` (category, venue, dates: Draft only), mirroring the wizard and the freeze rule. `GET /{id}/checklist` feeds the stepper and review step.
- Venues: `venues.organizer_id` null = shared seed venue (everyone may use it, nobody edits it); created venues belong to their organizer. Using another organizer's venue → 404 `venue.not_found`.
- Concurrency: `xmin` is the concurrency token of `events`; every PUT sends the `version` it edited, and a stale one returns 409 `event.concurrent_edit` (also mapped globally from `DbUpdateConcurrencyException`). Publish reloads on a conflict, so racing publishes return 200 and write one `EventPublished`.
- Domain stores every time as UTC (`ToUniversalTime`); the web enters and shows Vietnam time (`+07:00`, fixed offset, `web/src/lib/vn-time.ts`), never the device zone.
- Layout generator: canvas 1000×700, seat pitch 24, row pitch 28, `y += curve · W · (1 − (2t − 1)²)`, coordinates rounded `floor(v·100 + 0.5)/100`, ≤ 20,000 seats, ≤ 50 sections, every seat on the canvas (the far corner `origin + ((perRow − 1)·24, (rows − 1)·28 + curve·W)` too), rows A…Z, AA…. C# (`SeatLayoutGenerator`, source of truth) and TypeScript (`web/src/lib/organizer/seat-layout.ts`, live preview) are kept identical by `shared/fixtures/seat-layout.json`. `layout.invalid` is a ValidationProblem whose `errors` keys are `{section}.{field}`. The definition is kept in `events.layout` (jsonb) so the form reopens; re-layout replaces the seats in the same transaction.
- `PublishChecklist` runs `IPublishRule`s in the handler before `Event.Publish()`: the dev seed publishes cover-less events on purpose (spec 04 generated posters), so the aggregate itself doesn't require the checklist. Failures → 422 `event.publish_checklist_failed` with `failures: [{code, step, message}]`.
- Presale codes: `SHA-256(pepper ‖ UPPER(TRIM(code)))`, pepper `Catalog:AccessCodePepper` (AppHost parameter `access-code-pepper`, env `ACCESS_CODE_PEPPER`), required by every host with persistence. In `PUT phases`, `accessCode: null` keeps the phase's hash, `""` removes it. The code is never returned.
- Cache: publish / cancel / editing a published event evict the api's HybridCache inline; the outbox (`EventPublished`, `EventCancelled`, `EventDetailsChanged`) makes the worker call the web's `POST /api/revalidate` (`Frontend:RevalidateUrl`, `Frontend:RevalidateSecret`, AppHost parameter `revalidate-secret`). Redis warm-up (07) will be another handler of `EventPublished`.
- Web: `(organizer)` route group with shadcn Sidebar; client pages on TanStack Query; autosave 800 ms after the last valid change and on leaving a step, never two saves at once. Organizer copy is inline (not in `messages/vi.json`, which ships with public pages). The review step's preview is the real spec 04 detail page in an iframe at `/organizer/events/{id}/preview` (its own route group, no sign-in), fed the draft by same-origin `postMessage`: a second session restore in the iframe would rotate the refresh token in parallel and trip spec 03's reuse detection.
- Fixed while building: `AuthProvider` reported `anonymous` during hydration (the session-hint cookie can't be read from the server snapshot), so a guard could redirect a signed-in user to login before the refresh started; it now reports `loading` until the client reads the cookie.

## Acceptance criteria

- [x] A user without `Organizer` gets 403. Another organizer's event returns 404.
- [x] Publishing with overlapping phases fails with `event.phase_overlap` and lists the failing rule.
- [x] Editing tiers after publish returns `event.not_editable`.
- [x] A 20×30 section generates 600 seats with contiguous indexes and renders in the preview in < 100 ms (Playwright, real Chromium).
- [x] Publishing sends exactly one `EventPublished` outbox message, even on retry (also with 5 concurrent publishes).
