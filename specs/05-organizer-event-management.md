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

## Acceptance criteria

- [ ] A user without `Organizer` gets 403. Another organizer's event returns 404.
- [ ] Publishing with overlapping phases fails with `event.phase_overlap` and lists the failing rule.
- [ ] Editing tiers after publish returns `event.not_editable`.
- [ ] A 20×30 section generates 600 seats with contiguous indexes and renders in the preview in < 100 ms.
- [ ] Publishing sends exactly one `EventPublished` outbox message, even on retry.
