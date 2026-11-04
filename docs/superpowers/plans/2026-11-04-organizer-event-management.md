# Organizer Event Management Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Organizers create venues and events (tiers, generated seat layout, sale phases, cover), publish through a
checklist, and cancel, through `/organizer` API endpoints and a 5-step autosaving wizard.

**Architecture:** The `Event` aggregate gains draft-only replace operations, a stored layout definition, and an xmin
concurrency token. A pure `SeatLayoutGenerator` (C#, mirrored in TypeScript, kept identical by a shared JSON fixture)
turns sections into `EventSeat`s. Publish runs a `PublishChecklist` of `IPublishRule`s, raises `EventPublished` into
the outbox once, and invalidates the catalog: in-process HybridCache directly, the web ISR cache through a Worker
outbox handler that calls `POST /api/revalidate`. The web adds a `(organizer)` route group: Sidebar shell, client
pages on TanStack Query, react-hook-form + zod.

**Tech Stack:** .NET 10, EF Core 10 + Npgsql, FluentValidation, Aspire AppHost, Next 16.4, React 19.3, TanStack
Query 5, react-hook-form 7 + zod 4, shadcn/radix, Vitest, Playwright + axe.

**Spec:** `specs/05-organizer-event-management.md` + `docs/superpowers/specs/2026-11-03-organizer-event-management-design.md`

## Global Constraints

- Every route is `/api/v1/organizer/*` with policy `RequireOrganizer`. Ownership is filtered in the query: another
  organizer's event or venue ⇒ 404 `event.not_found` / `venue.not_found`. A user without the role ⇒ 403.
- Error codes: `event.not_editable` (409), `event.publish_checklist_failed` (422, with `failures[]`),
  `event.phase_overlap` (409 on PUT phases; listed in `failures[]` on publish), `layout.invalid` (400, with `errors[]`),
  `tier.in_use` (409), `event.concurrent_edit` (409), `venue.not_found` (404).
- Draft: everything is editable. Published: only title, description, lineup and cover. Tiers, layout, phases,
  category, venue and dates ⇒ `event.not_editable`.
- Publish is idempotent (already Published ⇒ 200, no new message). Exactly one `EventPublished` outbox message, even
  when two publishes race.
- Checklist rules: has cover; ≥ 1 tier; every seated tier has ≥ 1 seat; every GA tier has capacity > 0; ≥ 1 phase;
  first phase opens ≥ 10 min after server now; phases don't overlap.
- Presale code: `SHA-256(pepper ‖ UPPER(TRIM(code)))` as lowercase hex. Pepper comes from `Catalog:AccessCodePepper`.
  It lives in user-secrets, an AppHost parameter or env, never in git. Startup fails outside Development when it is
  missing.
- Cover: `image/jpeg|png|webp|avif`, ≤ 5 MB (5 × 1024 × 1024), key prefix `events/{eventId}/`.
- Layout: canvas 1000×700; seat pitch 24, row pitch 28; `curve` 0..0.5; rows 1..100; seats per row 1..100;
  ≤ 20,000 seats per event; section code `^[A-Za-z0-9]{1,8}$`, unique; rows A..Z, AA..; seat numbers from 1;
  `Index` 0..n−1 by (section order, row, number); coordinates rounded to 2 dp.
- Times are entered and shown in Vietnam time (`+07:00`), never the device zone, and minutes are kept exactly.
- Web copy is Vietnamese only. Organizer pages are outside the public Lighthouse budget but pass axe (light and dark).
- One commit for the whole spec, then a PR, the same convention as spec 04. A task's "Commit" step is a ledger
  checkpoint: record the task as complete with its test result, and don't commit. The final commit ends with
  `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. Build .NET with
  `--artifacts-path artifacts`, because the running AppHost locks `src/*/bin`.

## Review Focus

1. **Replacing phases that keep a presale code.** A `PUT phases` where a phase has the same `id` and
   `accessCode: null` must keep its hash. A new phase with `accessCode: null` has no code. `""` removes the code.
   → Task 6 test `ReplacePhases_keeps_hash_for_null_code_and_clears_for_empty`.
2. **Two browser tabs autosaving the same draft.** The second stale write ⇒ 409 `event.concurrent_edit`, never a
   silent overwrite or a 500. → Task 6 test `Stale_version_returns_concurrent_edit`.
3. **Deleting a tier while the layout still uses it.** ⇒ 409 `tier.in_use` naming the sections. The seats must not
   be orphaned. → Task 6 test `Removing_tier_used_by_layout_returns_tier_in_use`.
4. **Datetime inputs near midnight, or with a device zone that isn't UTC+7.** `2026-12-01` `00:30` in the form must
   be sent as `2026-12-01T00:30:00+07:00`. → Task 10 test `toVietnamIso keeps the entered wall time regardless of
   TZ`, run with `TZ=America/New_York`.
5. **A cover URL pointing outside this event's storage prefix** (another event, an external host) ⇒ 400 validation,
   not saved. → Task 5 test `Cover_url_outside_event_prefix_is_rejected`.

---

### Task 1: Domain — editable draft, venue ownership

**Files:** Modify `src/TicketRush.Domain/Events/Event.cs`, `EventDomainEvents.cs`, `EventErrors.cs`,
`TicketTier.cs`, `SalePhase.cs`, `src/TicketRush.Domain/Venues/Venue.cs`. Test
`tests/TicketRush.Domain.Tests/Events/EventEditingTests.cs`, `Venues/VenueTests.cs`.

**Interfaces — Produces:**
- `Venue.Create(name, address, city, mapUrl, capacity, Guid? id = null, Guid? organizerId = null)` and
  `Venue.OrganizerId : Guid?`. `null` means a shared (seed) venue.
- `Event.UpdateInfo(string title, string description, IEnumerable<string> lineup, string? coverImageUrl)`. Allowed in
  Draft and Published; title is trimmed, 3..200 chars. When the event is Published and something changed, it raises
  `EventDetailsChanged(Guid EventId)`. This replaces `UpdateDetails`; update its callers.
- `Event.UpdateSchedule(string category, Venue venue, DateTimeOffset startsAt, DateTimeOffset endsAt)`: Draft only.
  It sets `VenueId` and `City`.
- `sealed record TierDraft(Guid? Id, string Name, Money Price, TicketKind Kind, int? Capacity, string Color)`.
- `Event.ReplaceTiers(IReadOnlyList<TierDraft> tiers)`: Draft only. Upserts by `Id`: a matching id updates in place
  and keeps the id; tiers absent from the list are removed.
  - If a removed tier is referenced by `Layout`, it throws `DomainException("tier.in_use", …)` with the section codes
    in the message.
  - An unknown id throws `tier.not_found`.
- `sealed record PhaseDraft(Guid? Id, string Name, DateTimeOffset OpensAt, DateTimeOffset ClosesAt, bool QueueEnabled,
  int MaxTicketsPerUser, string? AccessCodeHash, bool KeepExistingCode)`.
- `Event.ReplacePhases(IReadOnlyList<PhaseDraft> phases)`: Draft only.
  - Any pairwise overlap throws `event.phase_overlap`. `closesAt > StartsAt` throws `phase.after_event_start`.
  - `KeepExistingCode` with a matching `Id` copies the old hash.
- `TicketTier.Update(name, price, kind, capacity, color)` and `SalePhase` are internal to the domain. Phases are
  recreated on replace, keeping their ids when given.
- `EventErrors.NotEditable = Error.Conflict("event.not_editable", …)`, `ConcurrentEdit` (`event.concurrent_edit`),
  `VenueNotFound` (`venue.not_found`).
- Every draft-only method throws `DomainException("event.not_editable")` on a non-Draft event.

- [ ] **Step 1: Write the failing tests** in `EventEditingTests`:
  - `ReplaceTiers_upserts_by_id_and_removes_missing`: 2 tiers → replace with [first id renamed, new] ⇒ the first id
    is kept with the new name, the second is gone, and a new id was created.
  - `ReplaceTiers_after_publish_throws_not_editable`.
  - `ReplacePhases_overlapping_throws_phase_overlap`: 10:00–12:00 and 11:00–13:00.
  - `ReplacePhases_touching_windows_are_allowed`: 10:00–11:00 and 11:00–12:00.
  - `ReplacePhases_keeps_hash_for_null_code_and_clears_for_empty` (Review Focus 1).
  - `UpdateInfo_on_published_raises_EventDetailsChanged_once_and_not_when_unchanged`.
  - `UpdateSchedule_after_publish_throws_not_editable`.
  - `Venue_created_by_organizer_has_owner`.
- [ ] **Step 2:** `dotnet test tests/TicketRush.Domain.Tests --artifacts-path artifacts` ⇒ FAIL (compile errors:
  methods missing).
- [ ] **Step 3:** Implement. Fix the seed (`SeedEventCatalog`) and other callers for the `UpdateInfo` rename.
- [ ] **Step 4:** Same command ⇒ all Domain tests pass.
- [ ] **Step 5:** Commit `feat(05): draft editing in the event aggregate`.

### Task 2: Seat layout generator (C#) + shared fixture

**Files:** Create `src/TicketRush.Domain/Events/Layout/SeatLayout.cs` (definition records),
`Layout/SeatLayoutGenerator.cs`, `Layout/LayoutErrors.cs`; `shared/fixtures/seat-layout.json` (repo root, read by
both test suites). Modify `Event.cs` (`ReplaceLayout`). Test `tests/TicketRush.Domain.Tests/Events/SeatLayoutGeneratorTests.cs`.

**Interfaces — Produces:**
- Records:
  - `sealed record SeatLayoutDefinition(IReadOnlyList<SectionDefinition> Sections)`.
  - `sealed record SectionDefinition(string Code, Guid TierId, SectionKind Kind, int? Rows, int? SeatsPerRow,
    LayoutPoint? Origin, double Curve)`.
  - `enum SectionKind { Seated, Standing }` (JSON `"seated"` / `"standing"`, default Seated).
  - `record LayoutPoint(double X, double Y)`.
- `sealed record GeneratedSeat(int Index, string Section, string Row, string Number, decimal X, decimal Y, Guid TierId)`.
- `static class SeatLayoutGenerator`:
  - `IReadOnlyList<LayoutError> Validate(SeatLayoutDefinition def, IReadOnlyDictionary<Guid, TicketKind> tiers)`.
  - `IReadOnlyList<GeneratedSeat> Generate(SeatLayoutDefinition def)`, which assumes the definition is valid.
  - `static string RowLabel(int zeroBased)`: 0 → "A", 25 → "Z", 26 → "AA", 27 → "AB".
- `sealed record LayoutError(string Section, string Field, string Message)`.
- `Event.Layout : SeatLayoutDefinition?` (stored as jsonb in Task 4).
- `Event.ReplaceLayout(SeatLayoutDefinition def) : IReadOnlyList<GeneratedSeat>`: Draft only. It validates against
  its own tiers and throws `LayoutInvalidException(IReadOnlyList<LayoutError>)` (a `DomainException` with code
  `layout.invalid`). Then it sets `Layout` and returns the seats. Persisting the seats is the handler's job.
- Seat position (design §4.2). Every value except `n` and `r` stays `double` until the final rounding:

```
W = (seatsPerRow - 1) * 24
t = seatsPerRow == 1 ? 0.5 : n / (seatsPerRow - 1)
x = origin.x + n * 24
y = origin.y + r * 28 + curve * W * (1 - (2t - 1)^2)
round each to 2 dp, MidpointRounding.AwayFromZero
```

- [ ] **Step 1: Write the fixture** `shared/fixtures/seat-layout.json`. It holds `{ cases: [{ name, definition,
  tierKinds, expected: { count, first, last, errors? } }] }` with these cases:
  - `20x30 straight` (600 seats; first `A/1` at origin, index 0; last `T/30`, index 599);
  - `curved 3x5 curve 0.2`, with the exact y of the middle seat;
  - `two sections + standing`: indices continue across sections, and the standing section has no seats;
  - invalid cases: duplicate code, seated section with a GA tier, rows 0, 101 seats per row, 20,001 total.
- [ ] **Step 2: Write the failing tests:** `Generator_matches_shared_fixture` (theory over the fixture cases),
  `RowLabel_wraps_after_Z`, `Indexes_are_contiguous_from_zero`, `ReplaceLayout_after_publish_throws_not_editable`,
  `ReplaceLayout_with_invalid_definition_lists_errors_per_section`.
- [ ] **Step 3:** Run the Domain tests ⇒ FAIL.
- [ ] **Step 4:** Implement. Read the fixture through a path walked up from `AppContext.BaseDirectory` to the folder
  containing `TicketRush.slnx`.
- [ ] **Step 5:** Domain tests pass. Commit `feat(05): seat layout generator`.

### Task 3: Publish checklist

**Files:** Create `src/TicketRush.Domain/Events/Publishing/IPublishRule.cs`, `PublishChecklist.cs`, one file per rule
in `Publishing/Rules/`. Modify `Event.cs`. Test `tests/TicketRush.Domain.Tests/Events/PublishChecklistTests.cs`.

**Interfaces — Produces:**
- `sealed record PublishFailure(string Code, string Step, string Message)`, where `Step` ∈
  `info|tiers|layout|phases`.
- `interface IPublishRule { PublishFailure? Check(PublishContext context); }` with
  `sealed record PublishContext(Event Event, IReadOnlyDictionary<Guid, int> SeatsPerTier, DateTimeOffset Now)`.
- Rules and their codes:
  - `HasCoverRule` → `publish.missing_cover` (info).
  - `HasTierRule` → `publish.no_tiers` (tiers).
  - `SeatedTiersHaveSeatsRule` → `publish.tier_without_seats` (layout).
  - `GaTiersHaveCapacityRule` → `publish.ga_without_capacity` (tiers).
  - `HasPhaseRule` → `publish.no_phases` (phases).
  - `FirstPhaseOpensLaterRule` (≥ 10 min) → `publish.first_phase_too_soon` (phases).
  - `PhasesDoNotOverlapRule` → `event.phase_overlap` (phases).
- `static class PublishChecklist`: `IReadOnlyList<IPublishRule> Rules` (in the order above) and
  `IReadOnlyList<PublishFailure> Run(PublishContext ctx)`.
- `Event.Publish(PublishContext ctx)`:
  - Already Published ⇒ return (no event raised).
  - Not Draft ⇒ `event.not_publishable`.
  - Failures ⇒ throw `PublishChecklistFailedException(IReadOnlyList<PublishFailure>)` (code
    `event.publish_checklist_failed`).
  - Otherwise ⇒ status Published and raise `EventPublished` once.
  - Remove the parameterless `Publish()`. The seed calls it, so update the seed to build a context (or add an
    `internal`/test-only path: prefer passing a context built from seed data, so seeds obey the rules too).

- [ ] **Step 1: Write the failing tests:**
  - one per rule, with a passing and a failing event;
  - `Run_reports_every_failure_in_rule_order`;
  - `Publish_twice_raises_one_EventPublished`;
  - `PhasesDoNotOverlapRule_detects_overlapping_windows`. The aggregate already blocks overlaps, so the rule guards
    rows written outside it. Test `internal static bool PhasesDoNotOverlapRule.AnyOverlap(IEnumerable<(DateTimeOffset
    Opens, DateTimeOffset Closes)>)` through `InternalsVisibleTo`: overlapping ⇒ true, touching ⇒ false. The
    API-level overlap acceptance test is in Task 7.
  - `First_phase_9_minutes_ahead_fails_and_10_passes`.
- [ ] **Step 2:** Run the Domain tests ⇒ FAIL. **Step 3:** Implement and fix the seed. **Step 4:** Pass.
  **Step 5:** Commit `feat(05): publish checklist`.

### Task 4: Persistence + presale hashing

**Files:** Modify `Configurations/EventConfiguration.cs` (layout jsonb, xmin), `VenueConfiguration` (organizer_id +
index), `Seed/DevDataSeeder.cs` (venues stay shared); migration `AddOrganizerEventManagement`. Create
`src/TicketRush.Application/Abstractions/Security/IAccessCodeHasher.cs`,
`src/TicketRush.Infrastructure/Security/AccessCodeHasher.cs`, `Options/CatalogOptions.cs` (pepper, validated on
start). Modify `DependencyInjection.cs`, `src/TicketRush.AppHost/AppHost.cs` (parameter `access-code-pepper`, generated,
secret, persisted → api `Catalog__AccessCodePepper`). Test `tests/.../Persistence/PersistenceTests.cs` (+ cases),
`tests/TicketRush.Application.Tests/Security/AccessCodeHasherTests.cs`.

**Interfaces — Produces:**
- `IAccessCodeHasher.Hash(string code) : string`: 64 lowercase hex chars. Input is trimmed and upper-cased with the
  invariant culture.
- `events.layout jsonb null`. `Event` uses xmin as a concurrency token: `builder.Property<uint>("xmin")
  .IsRowVersion()`, the Npgsql convention.
- `venues.organizer_id uuid null`, indexed.
- The seed's `"seed-presale"` hash becomes `hasher.Hash("SEED-PRESALE")`. Pass the hasher to the seeder.

- [ ] **Step 1: Write the failing tests:**
  - `Hash_is_case_and_whitespace_insensitive`: `" abc "` equals `"ABC"`.
  - `Hash_depends_on_pepper`.
  - `Missing_pepper_fails_validation_outside_development`: options validation.
  - Persistence: `Layout_round_trips_as_jsonb`.
  - Persistence: `Concurrent_update_of_event_throws_DbUpdateConcurrencyException`: two contexts load the same event,
    both change the title and save.
- [ ] **Step 2:** Run ⇒ FAIL. **Step 3:** Implement and add the migration
  (`dotnet ef migrations add AddOrganizerEventManagement --project src/TicketRush.Infrastructure --startup-project
  src/TicketRush.MigrationService`). The dev pepper for tests comes from `TestSettings`.
- [ ] **Step 4:** `dotnet test tests/TicketRush.Application.Tests tests/TicketRush.Api.IntegrationTests --filter
  "Persistence|AccessCode" --artifacts-path artifacts` ⇒ pass.
- [ ] **Step 5:** Commit `feat(05): organizer schema and presale hashing`.

### Task 5: API — venues, events list/create/get/info, cover upload

**Files:** Create `src/TicketRush.Application/Features/Organizer/` (`OrganizerModels.cs`, `Venues/*.cs`,
`Events/ListOrganizerEvents.cs`, `CreateEventDraft.cs`, `GetOrganizerEvent.cs`, `UpdateEventInfo.cs`,
`UpdateEventSchedule.cs`, `CoverUpload.cs`, `EventSlugs.cs`), `src/TicketRush.Api/Features/Organizer/OrganizerEndpoints.cs`.
Modify `Program.cs`/endpoint registration and Application DI. Test
`tests/TicketRush.Api.IntegrationTests/Organizer/OrganizerApi.cs` (helper), `OrganizerEventsTests.cs`.

**Interfaces — Produces (JSON camelCase):**
- `GET /organizer/venues` ⇒ `VenueItem[] { id, name, address, city, mapUrl, capacity, shared }`: shared venues plus
  the caller's own, ordered by name (vi-VN).
- `POST /organizer/venues {name, address, city, mapUrl?, capacity}` ⇒ 201 `VenueItem`. `mapUrl` must be http(s).
- `GET /organizer/events?q=&status=` ⇒ `OrganizerEventItem[] { id, slug, title, status, startsAt, venueName,
  seatCount, coverImageUrl }`, ordered by `startsAt desc`, at most 200 rows. `q` matches
  `immutable_unaccent(lower(title))` with ILIKE.
- `POST /organizer/events {title, category, venueId, startsAt, endsAt}` ⇒ 201 `OrganizerEvent`.
  - The slug is `EventSlugs.From(title)`: lowercase, unaccented, `-` separated, max 80 chars, with `-2`, `-3`, … on
    collision.
  - A venue the caller can't use ⇒ 404 `venue.not_found`.
- `GET /organizer/events/{id}` ⇒ `OrganizerEvent { id, slug, title, status, category, venueId, startsAt, endsAt,
  description, lineup, coverImageUrl, tiers: TierItem[], layout: SeatLayoutDefinition?, seatsPerTier: {tierId:
  count}, phases: PhaseItem[] (hasAccessCode, no hash), version }`.
  - `version` is the xmin as a string. Every PUT accepts it in the body as `version` and returns 409
    `event.concurrent_edit` when it is stale.
- `PUT /organizer/events/{id}/info {version, title, description, lineup[], coverImageUrl?}` ⇒ `OrganizerEvent`.
  - `coverImageUrl` must satisfy `IFileStorage.IsPublicUrlUnder(url, "events/{id}/")`.
  - `description` ≤ 5000 chars, title 3..200, lineup ≤ 20 items of ≤ 100 chars.
- `PUT /organizer/events/{id}/schedule {version, category, venueId, startsAt, endsAt}` ⇒ `OrganizerEvent` (Draft
  only).
- `POST /organizer/events/{id}/cover-upload-url {contentType, size}` ⇒ `UploadTicket`. Same pattern as
  `AvatarUploadHandler`: limit 5 MB, key `events/{id}/{guid}.{ext}`.
- Ownership: every handler loads through `db.Events.Where(e => e.Id == id && e.OrganizerId == userId)`.
- Spec route mapping: the spec's `GET/PUT /organizer/events/{id}` becomes `GET /{id}` plus `PUT /{id}/info` and
  `PUT /{id}/schedule`. Ledger this as a ruling: the split mirrors the wizard and the published/draft rule.
- `OrganizerApi` test helper: `SignInAsOrganizerAsync()` via `AuthApi.SignInWithRoleAsync`, plus `CreateDraftAsync()`,
  `PutAsync<T>()`.

- [ ] **Step 1: Write the failing tests** in `OrganizerEventsTests`:
  - `Customer_gets_403_on_organizer_routes`.
  - `Other_organizers_event_returns_404`.
  - `Create_draft_returns_slug_and_draft_status`.
  - `Duplicate_title_gets_suffixed_slug`.
  - `Venue_of_another_organizer_returns_venue_not_found`.
  - `Shared_venues_are_listed_for_every_organizer`.
  - `Update_info_on_published_event_is_allowed`.
  - `Update_schedule_on_published_event_returns_not_editable`.
  - `Cover_url_outside_event_prefix_is_rejected` (Review Focus 5).
  - `Cover_upload_url_rejects_over_5MB_and_svg`.
  - `List_searches_without_diacritics`: "son tung" finds "Sơn Tùng".
- [ ] **Step 2:** `dotnet test tests/TicketRush.Api.IntegrationTests --filter Organizer --artifacts-path artifacts`
  ⇒ FAIL.
- [ ] **Step 3:** Implement. Map `DomainException` codes to the statuses in Global Constraints, using the existing
  exception-to-problem pipeline. Add the new codes there.
- [ ] **Step 4:** Pass. **Step 5:** Commit `feat(05): organizer venues and event drafts api`.

### Task 6: API — tiers, layout, phases

**Files:** Create `Application/Features/Organizer/Events/ReplaceTiers.cs`, `ReplaceLayout.cs`, `ReplacePhases.cs`.
Modify `OrganizerEndpoints.cs`. Test `OrganizerInventoryTests.cs`.

**Interfaces — Produces:**
- `PUT /organizer/events/{id}/tiers {version, tiers: [{id?, name, price, kind: "Seated"|"GeneralAdmission",
  capacity?, color}]}` ⇒ `OrganizerEvent`.
  - `price` is an integer number of VND, > 0 and ≤ 100,000,000.
  - `color` ∈ `TierColors.All`: 8 hex values, exported to TS in Task 8. Choose them so white text reaches ≥ 4.5:1.
  - Removing a tier used by the layout ⇒ 409 `tier.in_use`.
- `PUT /organizer/events/{id}/layout {version, sections: [...]}` ⇒ `OrganizerEvent`.
  - Invalid ⇒ 400 `layout.invalid` with `errors: [{section, field, message}]`.
  - Valid ⇒ in one transaction: `ExecuteDelete` the event's seats, insert the generated seats with `AddRange` (one
    batched round trip is fine), save the event.
  - The returned `seatsPerTier` reflects the new seats.
- `PUT /organizer/events/{id}/phases {version, phases: [{id?, name, opensAt, closesAt, queueEnabled,
  maxTicketsPerUser, accessCode?: string|null}]}` ⇒ `OrganizerEvent`.
  - Overlap ⇒ 409 `event.phase_overlap`.
  - `accessCode` must match `^[A-Za-z0-9-]{4,32}$` when non-empty.
  - The handler maps the input: `null` ⇒ `KeepExistingCode: true`; `""` ⇒ no hash; a value ⇒
    `hasher.Hash(value)`.
- The catalog read side (04) keeps working: `GetSeatMap`'s version changes when the seats change.

- [ ] **Step 1: Write the failing tests:**
  - `Layout_20x30_generates_600_contiguous_seats`: query `event_seats` ⇒ indexes `0..599`, no gaps.
  - `Layout_with_wrong_tier_kind_returns_layout_invalid_with_errors`.
  - `Relayout_replaces_previous_seats`.
  - `Editing_tiers_after_publish_returns_not_editable` (acceptance).
  - `Removing_tier_used_by_layout_returns_tier_in_use` (Review Focus 3).
  - `Overlapping_phases_return_phase_overlap`.
  - `ReplacePhases_keeps_hash_for_null_code_and_clears_for_empty` (Review Focus 1, through the API: read the hash
    from the DB).
  - `Stale_version_returns_concurrent_edit` (Review Focus 2).
  - `Access_code_is_never_returned`: the response JSON has no `accessCodeHash` and no code.
- [ ] **Step 2:** FAIL. **Step 3:** Implement. **Step 4:** Pass. **Step 5:** Commit
  `feat(05): tiers, layout and phases api`.

### Task 7: API — checklist, publish, cancel, cache invalidation, OpenAPI

**Files:** Create `Application/Features/Organizer/Events/GetChecklist.cs`, `PublishEvent.cs`, `CancelEvent.cs`,
`Application/Abstractions/Catalog/IWebCacheRevalidator.cs`,
`Infrastructure/Catalog/WebCacheRevalidator.cs` (HttpClient → `{Frontend:BaseUrl}/api/revalidate`, header
`x-revalidate-secret`), `Infrastructure/Catalog/CatalogRevalidationHandlers.cs` (handlers for `EventPublished`,
`EventCancelled`, `EventDetailsChanged`, registered in the Worker). Modify `AppHost.cs` (parameter
`revalidate-secret` → web `REVALIDATE_SECRET`, worker `Frontend__RevalidateSecret`, worker `Frontend__BaseUrl`),
`docker-compose.prod.yml` (worker env), `web/openapi/ticketrush-api.json` + `web/src/lib/api/schema.d.ts`
(regenerate). Test `OrganizerPublishTests.cs`, `tests/.../Outbox/CatalogRevalidationTests.cs`.

**Interfaces — Produces:**
- `GET /organizer/events/{id}/checklist` ⇒ `{ ready: bool, failures: PublishFailure[] }` (camelCase
  `code, step, message`).
- `POST /organizer/events/{id}/publish` ⇒ 200 `OrganizerEvent`, or 422 problem `event.publish_checklist_failed` with
  extension `failures`.
  - On `DbUpdateConcurrencyException`: reload; if Published ⇒ 200; otherwise retry once.
  - After commit, call `ICatalogCacheInvalidator` for the event id and slug and for `events:list`.
- `POST /organizer/events/{id}/cancel` ⇒ 200 `OrganizerEvent`. Invalidates like publish. Cancelling an already
  Cancelled event ⇒ 409 `event.not_cancellable`.
- `IWebCacheRevalidator.RevalidateAsync(IReadOnlyList<string> tags, CancellationToken)`. Handlers send
  `["events:list", "event:{id}", "event-slug:{slug}"]`. Non-2xx ⇒ throw, so the outbox retries. A blank secret or
  blank base URL ⇒ log once and skip; handlers stay idempotent.
- `PublishChecklist` needs `SeatsPerTier`: one grouped count query on `event_seats`.

- [ ] **Step 1: Write the failing tests:**
  - `Publish_with_overlapping_phases_fails_and_lists_phase_overlap` (acceptance). Insert an overlapping phase row
    with SQL, because the domain blocks it.
  - `Publish_missing_cover_lists_publish_missing_cover_with_step_info`.
  - `Publish_twice_returns_200_and_one_outbox_message` (acceptance).
  - `Concurrent_publish_creates_exactly_one_EventPublished`: `Task.WhenAll` of 5 publishes ⇒ all 200, one message.
  - `Published_event_appears_in_public_catalog_immediately` (HybridCache invalidated).
  - `Cancel_emits_EventCancelled_and_hides_event_from_catalog`.
  - `Revalidation_handler_posts_tags_with_secret`: fake `HttpMessageHandler`.
  - `Revalidation_handler_throws_on_500_so_outbox_retries`.
- [ ] **Step 2:** FAIL. **Step 3:** Implement. Then run the api, save the OpenAPI doc with the existing script, and
  `npm run api:gen`. `OpenApiDocumentTests` must pass.
- [ ] **Step 4:** Full .NET suite `dotnet test --artifacts-path artifacts` ⇒ all pass.
- [ ] **Step 5:** Commit `feat(05): publish, cancel and catalog revalidation`.

### Task 8: Web — organizer shell, events list, quick create, venues

**Files:** Create `web/src/app/(organizer)/layout.tsx`, `(organizer)/organizer/page.tsx` (redirect to
`/organizer/events`), `organizer/events/page.tsx`, `organizer/venues/page.tsx`; `web/src/features/organizer/`
(`api.ts`, `organizer-shell.tsx`, `organizer-gate.tsx`, `events/events-table.tsx`, `events/create-event-dialog.tsx`,
`venues/venues-table.tsx`, `venues/create-venue-dialog.tsx`, `status-badge.tsx`, `tier-colors.ts`); `web/src/lib/vn-time.ts`.
Tests are colocated `*.test.tsx`.

**Interfaces — Produces:**
- `features/organizer/api.ts`: typed wrappers using `orThrow` like `features/account/api.ts`:
  - `listVenues`, `createVenue`, `listEvents({q, status})`, `createEvent`, `getEvent(id)`;
  - `putInfo`, `putSchedule`, `putTiers`, `putLayout`, `putPhases`;
  - `getChecklist`, `publishEvent`, `cancelEvent`, `uploadCover(id, blob)`.
  - Query keys: `["organizer", "events", filters]`, `["organizer", "event", id]`, `["organizer", "venues"]`,
    `["organizer", "checklist", id]`.
- `OrganizerGate`: reads the session user. Without the `"Organizer"` role it renders an `EmptyState` titled "Bạn
  cần tài khoản nhà tổ chức". With the role it renders its children.
- `OrganizerShell`:
  - shadcn `Sidebar` items: Sự kiện `/organizer/events`, Địa điểm `/organizer/venues`, Dashboard and Cài đặt
    disabled with a "Sắp có" badge.
  - Header with a breadcrumb from a `breadcrumbs` prop.
- `lib/vn-time.ts`:
  - `toVietnamIso(date: "YYYY-MM-DD", time: "HH:mm"): string` ⇒ `"YYYY-MM-DDTHH:mm:00+07:00"`;
  - `fromIsoToVietnam(iso): {date, time}`;
  - `formatVietnam(iso): string`, e.g. "19:30, T7 06/12/2026 (GMT+7)".
  - All of them use `Intl` with `timeZone: "Asia/Ho_Chi_Minh"`, never local getters.
- `TIER_COLORS`: the same 8 hex values as C# `TierColors.All`. A test reads the C# file and compares, the same way
  as the 04 categories test.
- `StatusBadge` labels: Nháp, Đã xuất bản, Đã kết thúc, Đã huỷ.

- [ ] **Step 1: Write the failing tests:**
  - `vn-time.test.ts`: `toVietnamIso("2026-12-01","00:30")` ⇒ `"2026-12-01T00:30:00+07:00"`, and `fromIsoToVietnam`
    round-trips. Run under `TZ=America/New_York` via `vi.stubEnv` plus a separate script
    `"test:tz": "cross-env TZ=America/New_York vitest run src/lib/vn-time.test.ts"`. If `cross-env` is absent, use
    `node -e` to set TZ; do not add a dependency for it.
  - `organizer-gate.test.tsx`: a Customer sees the message, an Organizer sees the children.
  - `create-event-dialog.test.tsx`: an end time before the start shows "Giờ kết thúc phải sau giờ bắt đầu"; a
    valid submit calls `createEvent` with `+07:00` times and navigates to `/organizer/events/{id}/info`.
  - `tier-colors.test.ts` (parity with C#; every colour reaches ≥ 4.5:1 against white using `lib/contrast.ts`).
  - `events-table.test.tsx`: status badges and search input debounced 300 ms.
- [ ] **Step 2:** `npm run test -- organizer vn-time` ⇒ FAIL. **Step 3:** Implement. **Step 4:** Pass, plus
  `npm run lint && npm run typecheck`. **Step 5:** Commit `feat(05): organizer shell, events and venues pages`.

### Task 9: Web — wizard frame, autosave, step 1 (info + cover crop), step 2 (tiers)

**Files:** Create `web/src/app/(organizer)/organizer/events/[id]/[step]/page.tsx`;
`features/organizer/wizard/` (`wizard-layout.tsx`, `stepper.tsx`, `use-autosave.ts`, `save-indicator.tsx`,
`locked-banner.tsx`, `steps.ts`), `wizard/info/info-step.tsx`, `info/description-field.tsx`, `info/cover-cropper.tsx`,
`info/crop.ts`, `wizard/tiers/tiers-step.tsx`.

**Interfaces — Produces:**
- `steps.ts`: `STEPS = ["info","tiers","layout","phases","review"] as const`, labels "Thông tin", "Hạng vé", "Sơ đồ",
  "Mở bán", "Kiểm tra & Xuất bản", and `stepStatus(step, checklist, event): "done"|"error"|"todo"`.
- `useAutosave<T>({ value, valid, save: (v: T) => Promise<void>, delayMs = 800 })` ⇒
  `{ state: "idle"|"saving"|"saved"|"invalid"|"error", savedAt: Date|null, retry(), flush() }`.
  - Saves are serialized: never two in flight.
  - Registers `beforeunload` while unsaved.
  - A 409 `event.concurrent_edit` sets `state: "error"` with `conflict: true`.
- `SaveIndicator` copy: "Đang lưu…", "Đã lưu lúc HH:mm", "Chưa lưu: còn N lỗi", "Lưu thất bại · Thử lại", and on
  conflict "Sự kiện vừa được sửa ở nơi khác · Tải lại".
- `crop.ts`: `coverCropRect(imageW, imageH, zoom: 1..3, offsetX: -1..1, offsetY: -1..1) : {sx, sy, sw, sh}` with
  `sw/sh = 16/9`, kept inside the image. `renderCover(img, rect): Promise<Blob>` draws to a 1600×900 canvas and
  exports `image/jpeg` at 0.85.
- `LockedBanner` copy: "Đã khoá sau khi xuất bản để không thay đổi tồn kho đang bán. Muốn đổi, hãy huỷ và tạo lại sự
  kiện."
- Info step on a published event: category, venue and dates are disabled with a lock icon.
- Description preview renders paragraphs split on blank lines, the same way as the 04 detail page. Reuse that
  helper; extract it to `lib/catalog/paragraphs.ts` if it is inline.

- [ ] **Step 1: Write the failing tests:**
  - `use-autosave.test.ts` (fake timers): debounce 800 ms; no save while invalid; a second change during a save
    queues exactly one more save; a 409 sets conflict; `beforeunload` is registered only while dirty.
  - `crop.test.ts`: a 4000×3000 image at zoom 1 gives a 4000×2250 rect centred; zoom 2 halves it; offsets clamp
    inside the image.
  - `info-step.test.tsx`: a published event disables the venue and date fields and shows the lock icon; the preview
    tab shows 2 paragraphs for "a\n\nb".
  - `tiers-step.test.tsx`: a Seated row hides capacity and shows "Số ghế: 240 (từ sơ đồ)"; a `tier.in_use` error
    shows the section names.
  - `stepper.test.tsx`: ✓/!/○ from the checklist.
- [ ] **Step 2:** FAIL. **Step 3:** Implement. **Step 4:** Pass, plus lint and typecheck. **Step 5:** Commit
  `feat(05): wizard frame, info and tiers steps`.

### Task 10: Web — step 3 layout (TS generator, presets, SVG preview)

**Files:** Create `web/src/lib/organizer/seat-layout.ts`, `seat-layout.test.ts`,
`features/organizer/wizard/layout/layout-step.tsx`, `layout/section-card.tsx`, `layout/layout-preview.tsx`,
`layout/presets.ts`, `layout-preview.test.tsx`.

**Interfaces — Produces:**
- `seat-layout.ts`: `generateSeats(def: SeatLayoutDefinition): GeneratedSeat[]`, `validateLayout(def, tierKinds):
  LayoutError[]`, `rowLabel(n)`. Same constants and formula as Task 2, rounding with `Math.round(v * 100) / 100`
  half away from zero: use `Math.sign(v) * Math.round(Math.abs(v) * 100) / 100`.
- `PRESETS: { id: "theater-3" | "stage-standing" | "stadium-4", name: string, sections(tiers): SectionDefinition[] }[]`
  with the names "Nhà hát 3 khu", "Sân khấu + khu đứng", "Sân vận động 4 phía". Sections get the first tier of the
  matching kind. When no tier of that kind exists, `sections()` returns them with an empty `tierId` and the step
  shows "Tạo hạng vé loại Ngồi/Đứng ở bước Hạng vé trước".
- `LayoutPreview({ sections, tiers, seats, activeSection, onSectionClick })`:
  - a single `<svg viewBox="0 0 1000 700">`, a stage rect labelled "Sân khấu", one `<circle r=8>` per seat filled
    with the tier colour;
  - each section is a `<g data-section>` with one click handler on the `<svg>` (event delegation);
  - standing sections are drawn as a labelled rect at their origin, 240×120.
- Per-tier seat totals are listed under the preview.

- [ ] **Step 1: Write the failing tests:**
  - `seat-layout.test.ts`: `matches the shared fixture`. Import `../../../../shared/fixtures/seat-layout.json`; if
    the path alias blocks it, use `readFileSync` with `resolve(process.cwd(), "../shared/fixtures/seat-layout.json")`.
  - `layout-preview.test.tsx`: `renders 600 seats for a 20x30 section in under 100 ms`. Measure `performance.now()`
    around `generateSeats` plus `render`, warm up once, take the median of 5 runs, assert < 100. Then assert 600
    `circle` elements.
  - `clicking a section scrolls its card into view and marks it active`.
  - `presets fill sections with the first matching tier`.
- [ ] **Step 2:** FAIL. **Step 3:** Implement. **Step 4:** Pass. **Step 5:** Commit `feat(05): layout step with live
  preview`.

### Task 11: Web — step 4 phases (timeline), step 5 review (checklist, preview, publish), cancel

**Files:** Create `features/organizer/wizard/phases/phases-step.tsx`, `phases/phase-timeline.tsx`,
`phases/timeline.ts`, `phases/access-code.ts`, `wizard/review/review-step.tsx`, `review/publish-dialog.tsx`,
`review/preview-frame.tsx`, `features/organizer/events/cancel-event-dialog.tsx`;
`web/src/app/(organizer)/organizer/events/[id]/preview/page.tsx` (renders the 04 `EventDetailView` from the organizer
event mapped to `EventDetail`; `noindex`); `features/organizer/to-event-detail.ts`.

**Interfaces — Produces:**
- `timeline.ts`: `layoutTimeline(phases, now, eventStart): { start, end, blocks: { id, leftPct, widthPct, overlaps:
  boolean, tooSoon: boolean }[], eventMarkPct }`. `tooSoon` means it opens < 10 min after `now`. `now` is the server
  clock via `loadClockOffset` from 04.
- `access-code.ts`: `generateAccessCode(random = crypto.getRandomValues): string`. 8 chars from
  `ABCDEFGHJKLMNPQRSTUVWXYZ23456789`.
- The phase card shows "Đã đặt mã · Đổi mã · Bỏ mã" when `hasAccessCode`, and the note "Mã được mã hoá khi lưu, sau
  đó không xem lại được. Hãy sao chép trước khi lưu."
- Review step:
  - checklist rows ✓/✗ with "Sửa" links to `/organizer/events/{id}/{failure.step}`;
  - `PreviewFrame` with Desktop (1280 wide, `transform: scale` to fit) and Điện thoại (390) toggles, rendering an
    iframe to `/organizer/events/{id}/preview`;
  - a lock-warning box listing "Giá vé, sức chứa, sơ đồ ghế và các đợt mở bán sẽ bị khoá";
  - "Xuất bản" is disabled until `ready`.
- `PublishDialog` summary: seat count, tier count, first opening as `formatVietnam` plus relative "mở bán sau …"
  (`formatOpensIn`). On success: toast "Đã xuất bản" with the action "Xem trang công khai" → `/events/{slug}`.
- `CancelEventDialog`: the confirm button is enabled only when the input equals the title exactly (trimmed). For a
  published event, the body adds "Người mua sẽ được hoàn tiền (spec 08)."

- [ ] **Step 1: Write the failing tests:**
  - `timeline.test.ts`: overlap flags both blocks; `tooSoon` at 9 min but not at 10; the event mark is at 100% when
    the end equals the event start.
  - `access-code.test.ts`: length 8, alphabet only, no 0/O/1/I.
  - `phases-step.test.tsx`: entering overlapping times marks both cards red before saving; times are sent as
    `+07:00`.
  - `review-step.test.tsx`: a failure row links to its step; publish is disabled while not ready.
  - `cancel-event-dialog.test.tsx`: the button stays disabled until the exact title is typed.
  - `to-event-detail.test.ts`: maps tiers, phases (`requiresAccessCode` from `hasAccessCode`) and venue.
- [ ] **Step 2:** FAIL. **Step 3:** Implement. **Step 4:** Pass, plus lint and typecheck. **Step 5:** Commit
  `feat(05): phases timeline, review and publish, cancel`.

### Task 12: E2E, docs, final verification

**Files:** Create `web/e2e/organizer.spec.ts` with `web/e2e/organizer-fake.ts`: an in-memory fake of the organizer
API installed with `page.route("**/api/v1/organizer/**")`, plus the auth refresh/`me` stubs used by `auth.spec.ts`.
Modify `README.md` (05 ✅, organizer walkthrough, `access-code-pepper` and `revalidate-secret` parameters) and
`specs/05-organizer-event-management.md` (tick the acceptance criteria and add implementation notes, including the
route-split ruling).

- [ ] **Step 1: Write the e2e tests:**
  - axe (light and dark) on `/organizer/events`, on each wizard step and on `/organizer/venues`;
  - `a customer sees the organizer-account message`;
  - `quick create → info → tiers → layout preset → phases → review → publish` happy path: the fake checklist turns
    ready once each step has been saved, and the toast appears;
  - `layout preview shows 600 seats for 20x30`;
  - `cancel requires typing the title`.
- [ ] **Step 2:** `npm run build:e2e && npx playwright test` ⇒ all pass, including the existing 42.
- [ ] **Step 3:** Full verification: `dotnet test --artifacts-path artifacts`, then `npm run lint`,
  `npm run typecheck`, `npm run test`, `npm run test:tz` and `npx playwright test`. Lighthouse CI on public pages
  must still pass: the organizer code must not enter public bundles. Check it with `next analyze` on `/`.
- [ ] **Step 4:** Commit `feat(05): organizer e2e and docs`.
