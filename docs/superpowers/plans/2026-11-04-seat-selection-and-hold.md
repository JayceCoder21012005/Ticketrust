# Seat Selection & Hold Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Admitted buyers pick seats (or GA quantities) on a live map and hold them for 8 minutes with zero chance of a double hold (C1), plus the spec 06 hooks that 07 unlocks.

**Architecture:** All hold state lives in Redis under one hash tag per event and changes only through Lua scripts that also bump a version and publish a delta. API endpoints wrap an `IInventoryStore` port; leader-elected Worker services expire holds, collapse deltas and push them over SignalR (Redis backplane), keep the queue room status in step, and repair drift. The web page renders an SVG map whose seats subscribe to a keyed external store.

**Tech Stack:** .NET 10 Minimal API, StackExchange.Redis + Lua, SignalR + `Microsoft.AspNetCore.SignalR.StackExchangeRedis`, EF Core/Postgres, xUnit v3 + Shouldly + NSubstitute + Testcontainers; Next 16 / React 19, TanStack Query, `@microsoft/signalr`, Vitest + Testing Library, Playwright + axe.

**Spec:** `specs/07-seat-selection-and-hold.md`; decisions and evidence: `docs/superpowers/specs/2026-11-04-seat-selection-and-hold-design.md` (approved 2026-11-04, scope includes the 06 hooks; SVG target is a goal to meet, canvas only after asking).

## Global Constraints

- Keys: `tr:{env}:inv:{evt:<eventId:N>}:<name>`; every multi-key mutation is one Lua script with a header comment (KEYS, ARGV, returns). All keys a script touches are passed in `KEYS` (spec 00, redis.io).
- Seat indexes sorted ascending before every script call (C9).
- Redis unavailable on any hold path → 503 `system.busy`; never fall back to Postgres for inventory (spec 00).
- Hold TTL from `Holds:TtlSeconds`, default **480**. Max seats per hold = `min(Holds:MaxSeatsPerHold (8), SalePhase.MaxTicketsPerUser − bought)`.
- `POST /holds` needs auth + `RequireAdmissionToken` + `Idempotency-Key` (UUID). `GET`/`DELETE holds/me` need auth only.
- Error codes exactly: `hold.seats_unavailable` (+ `unavailable: int[]`), `hold.insufficient_quantity`, `hold.limit_exceeded`, `hold.already_exists`, `hold.empty_selection`, `hold.invalid_seat`, `hold.not_found`, `system.busy`; idempotency: `idempotency.key_required` (400), `idempotency.key_reused` (422), `idempotency.in_progress` (409).
- Delta payload `{ v, held[], freed[], sold[], tiers[{tierId, remaining}] }`, SignalR method `SeatDelta`, group `event:{id}`, collapse window 250 ms.
- Coding rules of spec 00: sealed, primary ctors, `TimeProvider`, `CancellationToken ct` everywhere, methods ≤ 30 lines, `TreatWarningsAsErrors`.
- Vietnamese UI copy; seat `aria-label` format `"Khu A, hàng 3, ghế 12, 1.500.000 ₫, còn trống"`; buyer pages pass axe in light and dark.
- Commit messages end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. One branch `feat/07-seat-selection-and-hold`; PR at the end.

## Review Focus

1. **Replace racing another buyer:** while user A replaces {1,2} with {2,3}, user B hammering seat 2 must never get it (no instant where 2 is free).
2. **Stale old hold between read and script:** the hold expires or is replaced after C# read `userhold` but before `hold_create` runs → script must refuse (`STALE`) and the wrapper retry once, never orphan seats.
3. **Expired-then-reheld seat released by the old owner:** `DELETE holds/me` (or the expiry sweep) on an old hold must not free a seat now owned by another hold.
4. **Missed deltas:** Redis/backplane blip or tab sleeping → client sees a `v` gap or reconnects and must refetch `/availability`, ending in the same state as a fresh load.
5. **Idempotent retry after timeout:** the same `Idempotency-Key` resent while the first call is still running gets 409, and after it finished gets the same hold back, never a second hold.

Each line has a test in the owning task (Tasks 3, 3, 3, 13/16, 6).

---

### Task 0: Spikes (throwaway, not merged)

**Files:** scratch only, under `.superpowers/spikes/07/` (git-ignored). Results go to the design doc section "Spike results" and the ledger.

- [ ] **S1 seat map benchmark:** a bare Next page (or static HTML + React 19 from the web app's node_modules) rendering 10,000 `<circle>` seats in one SVG with the keyed store (Task 14 design: `Map<index, Set<listener>>`, `memo` seat, one delegated click handler, pan/zoom by setting one `<g>` transform from a ref). Playwright + Chromium measures (a) frame times over a 3 s scripted wheel-zoom/pan via `requestAnimationFrame` deltas, (b) the time to apply a 200-seat delta every 250 ms for 5 s.
  Expected: p95 frame time ≤ 18 ms (≈ 55 fps) for both. If not met, tune (CSS `will-change: transform`, drop per-seat `<title>`, render only the zoomed section) and re-measure; only if still failing stop and ask the user about a canvas seat layer.
- [ ] **S2 cross-process hub:** a console Worker host with `AddSignalR().AddStackExchangeRedis(prefix)` sending to group `event:x` via `IHubContext<EventsHub>`, the hub type from a shared assembly; an API host maps the hub; a .NET `HubConnection` client joined to the group receives the message.
  Expected: message arrives. Confirms the hub type must be shared (backplane channel names include the hub's full type name).
- [ ] **S3 Caddy route:** `docker compose -f docker-compose.prod.yml` with the Caddyfile change from Task 12 and the standalone web build; browser client connects to `/hubs/events`.
  Expected: DevTools shows a `101 Switching Protocols` for the WebSocket. If Docker is unavailable on this machine, record "not run" and keep the Task 12 test (`Caddyfile` routes `/hubs/*` to the api) as the only evidence.

---

### Task 1: Hold options and inventory keys

**Files:**
- Modify: `src/TicketRush.Application/Options/ApplicationOptions.cs` (`HoldsOptions`)
- Modify: `src/TicketRush.Api/appsettings.json`, `src/TicketRush.Worker/appsettings.json` (section `Holds`)
- Modify: `src/TicketRush.Infrastructure/Redis/RedisKeys.cs`
- Test: `tests/TicketRush.Application.Tests/Holds/HoldsOptionsTests.cs`, `tests/TicketRush.Api.IntegrationTests/Holds/InventoryKeysTests.cs`

**Interfaces:**
- Produces: `HoldsOptions { int TtlSeconds = 480 [Range(60, 3600)]; int MaxSeatsPerHold = 8 [Range(1, 8)]; TimeSpan Ttl => TimeSpan.FromSeconds(TtlSeconds); }` (`HoldTtlMinutes` removed).
- Produces on `RedisKeys`: `InvSold(eventId)` SET, `InvHeld(eventId)` HASH index→holdId, `InvGa(eventId, tierId)` STRING, `InvGaHeld(eventId)` HASH tierId→qty held, `InvHold(eventId, holdId)` HASH, `InvExpiry(eventId)` ZSET, `InvUserHold(eventId, userId)` STRING, `InvBought(eventId, userId)` STRING, `InvVersion(eventId)` STRING, `InvMeta(eventId)` HASH (`warm`, `seatCount`, `gaCap:<tierId>`), and `InvChanges(eventId)` → `RedisChannel` `tr:{env}:inv:{evt:<id>}:changes`; `InvChangesPattern` → `tr:{env}:inv:*:changes`. Private helper `Inv(eventId, name) => $"{Prefix}inv:{{evt:{eventId:N}}}:{name}"`.
- `InvGaHeld` and `InvMeta` are additions to the spec's key table (reconciler needs held GA per tier; listener needs totals) — Ruling, document them in `specs/07` in Task 17.

- [ ] **Step 1: Write the failing tests**
  - `HoldsOptionsTests.Defaults_match_the_spec`: `new HoldsOptions().TtlSeconds.ShouldBe(480)`, `.MaxSeatsPerHold.ShouldBe(8)`, `.Ttl.ShouldBe(TimeSpan.FromMinutes(8))`.
  - `HoldsOptionsTests.Out_of_range_values_fail_validation` (`TtlSeconds = 30` and `MaxSeatsPerHold = 9` fail `Validator.TryValidateObject`).
  - `InventoryKeysTests.Every_key_of_one_event_hashes_to_the_same_slot` (use `RedisKey`→ `HashSlot` via `ServerSelectionStrategy` like `QueueKeysTests`), and `Keys_of_two_events_do_not_collide`.
- [ ] **Step 2: Run** `dotnet test tests/TicketRush.Application.Tests --filter HoldsOptions` and `dotnet test tests/TicketRush.Api.IntegrationTests --filter InventoryKeys`. Expected: compile errors / FAIL.
- [ ] **Step 3: Implement** the options and keys; update both appsettings to `"Holds": { "TtlSeconds": 480, "MaxSeatsPerHold": 8 }`; fix any reference to `HoldTtlMinutes` (`grep -rn HoldTtlMinutes src tests` must be empty).
- [ ] **Step 4: Run** the same commands. Expected: PASS.
- [ ] **Step 5: Commit** `feat(07): hold options and inventory keys`.

---

### Task 2: Inventory port, catalog snapshot and Redis failure mapping

**Files:**
- Create: `src/TicketRush.Application/Abstractions/Inventory/InventoryModels.cs`, `IInventoryStore.cs`, `IHoldCatalog.cs`, `InventoryUnavailableException.cs`
- Create: `src/TicketRush.Infrastructure/Inventory/HoldCatalog.cs` (EF + HybridCache, tag `CachedEventCatalogReader.EventTag(eventId)`, 10 min)
- Test: `tests/TicketRush.Api.IntegrationTests/Holds/HoldCatalogTests.cs`

**Interfaces (Produces, used by Tasks 3–11):**
```csharp
public sealed record GaLine(Guid TierId, int Quantity);
public sealed record HoldRequest(Guid EventId, Guid UserId, IReadOnlyList<int> SeatIndexes, IReadOnlyList<GaLine> Ga,
    IReadOnlyList<Guid> EventGaTiers, int MaxTickets, bool ReplaceExisting, DateTimeOffset Now, TimeSpan Ttl);
public enum HoldStatus { Ok, SeatUnavailable, GaInsufficient, LimitExceeded, HasHold }
public sealed record HoldOutcome(HoldStatus Status, Guid? HoldId, DateTimeOffset? ExpiresAt, IReadOnlyList<int> Unavailable, Guid? Tier);
public sealed record ActiveHold(Guid HoldId, Guid UserId, IReadOnlyList<int> Seats, IReadOnlyList<GaLine> Ga, DateTimeOffset ExpiresAt);
public sealed record InventorySnapshot(long Version, IReadOnlyList<int> Held, IReadOnlyList<int> Sold, IReadOnlyDictionary<Guid, int> GaRemaining, bool Warm);
public sealed record InventorySeed(Guid EventId, int SeatCount, IReadOnlyDictionary<Guid, int> GaCapacity,
    IReadOnlyList<int> SoldSeats, IReadOnlyDictionary<Guid, int> GaSold, IReadOnlyDictionary<Guid, int> BoughtByUser);
public sealed record ReconcileReport(int SoldAdded, int SoldRemoved, IReadOnlyDictionary<Guid, (int Before, int After)> GaFixed);

public interface IInventoryStore
{
    Task<HoldOutcome> CreateHoldAsync(HoldRequest request, CancellationToken ct);
    /// <returns>false when the hold no longer exists (idempotent).</returns>
    Task<bool> ReleaseHoldAsync(Guid eventId, Guid holdId, IReadOnlyList<Guid> eventGaTiers, CancellationToken ct);
    Task<ActiveHold?> GetUserHoldAsync(Guid eventId, Guid userId, CancellationToken ct);
    Task CommitAsync(Guid eventId, Guid holdId, Guid userId, IReadOnlyList<int> seats, IReadOnlyList<GaLine> ga, IReadOnlyList<Guid> eventGaTiers, CancellationToken ct);
    Task<InventorySnapshot> GetSnapshotAsync(Guid eventId, IReadOnlyList<Guid> eventGaTiers, CancellationToken ct);
    Task<IReadOnlyList<Guid>> DueHoldsAsync(Guid eventId, DateTimeOffset now, int limit, CancellationToken ct);
    Task<ReconcileReport> ReconcileAsync(InventorySeed seed, CancellationToken ct);
}

public sealed record CatalogSeat(int Index, string Section, string Row, string Number, decimal X, decimal Y, Guid TierId);
public sealed record CatalogTier(Guid Id, string Name, long Price, TicketKind Kind, int? Capacity);
public sealed record CatalogPhase(Guid Id, DateTimeOffset OpensAt, DateTimeOffset ClosesAt, int MaxTicketsPerUser);
public sealed record HoldCatalogEntry(Guid EventId, string Slug, IReadOnlyList<CatalogSeat> Seats, IReadOnlyList<CatalogTier> Tiers, IReadOnlyList<CatalogPhase> Phases)
{ public IReadOnlyList<Guid> GaTierIds { get; } /* tiers with Kind == GeneralAdmission */ }
public interface IHoldCatalog { Task<HoldCatalogEntry?> GetAsync(Guid eventId, CancellationToken ct); } // Published events only
public sealed class InventoryUnavailableException(string message, Exception inner) : Exception(message, inner);
```

- [ ] **Step 1: Failing tests** `HoldCatalogTests` against the dev seed: `Son_tung_has_600_seats_with_tiers_and_its_phase` (Seats.Count 600, index 0..599 contiguous, 2 seated tiers, ≥1 phase), `Draft_and_unknown_events_return_null`, `A_second_read_runs_no_query` (`_factory.Queries`).
- [ ] **Step 2: Run** `dotnet test tests/TicketRush.Api.IntegrationTests --filter HoldCatalog`. Expected: FAIL (types missing).
- [ ] **Step 3: Implement** models, port, `HoldCatalog` (one projection query, `AsNoTracking`), register `services.AddSingleton<IHoldCatalog, HoldCatalog>()` — scoped if it needs `AppDbContext` (follow `SaleWindowProvider`: scoped).
- [ ] **Step 4: Run** again. Expected: PASS.
- [ ] **Step 5: Commit** `feat(07): inventory port and hold catalog`.

---

### Task 3: `hold_create` and `hold_release` scripts + `RedisInventoryStore`

**Files:**
- Create: `src/TicketRush.Infrastructure/Redis/Scripts/hold_create.lua`, `hold_release.lua`
- Create: `src/TicketRush.Infrastructure/Inventory/InventoryScripts.cs`, `RedisInventoryStore.cs`
- Modify: `src/TicketRush.Infrastructure/DependencyInjection.cs` (`IInventoryStore` singleton)
- Test: `tests/TicketRush.Api.IntegrationTests/Holds/InventoryHarness.cs`, `HoldScriptTests.cs`

**Interfaces:**
- Consumes: Task 1 keys, Task 2 port.
- Produces: `RedisInventoryStore : IInventoryStore` (ctor `IConnectionMultiplexer, RedisKeys, TimeProvider`); `internal static Task<T> GuardAsync<T>(Func<Task<T>>)` mapping `RedisException or TimeoutException or ObjectDisposedException` → `InventoryUnavailableException` (same as `RedisWaitingRoom.GuardAsync`).
- Delta JSON published by every mutating script (via `cjson.encode`): `{"v":<long>,"held":[..],"freed":[..],"sold":[..],"ga":{"<tierId>":<remaining>}}`; channel name passed in ARGV (channels are not keys).

**Algorithm (scripts):**
- C# first `GET userhold`; passes the old hold key (or an unused placeholder key in the same slot) plus every GA tier key of the event (`EventGaTiers`, ≤ 20) in `KEYS`, and the expected old holdId in ARGV.
- `hold_create.lua` KEYS: 1 sold, 2 held, 3 expiry, 4 userhold, 5 bought, 6 version, 7 gaheld, 8 new hold, 9 old hold, 10.. ga tier keys. ARGV: holdId, userId, seats csv (sorted), ga json, maxTickets, replace (0/1), nowMs, ttlMs, expectedOldHoldId (or ""), channel, gaTierIds csv (same order as KEYS 10..).
  1. If `GET userhold` ≠ expectedOld → return `{'STALE'}`. If an old hold exists and replace = 0 → `{'HAS_HOLD'}`.
  2. `oldSeats` = old hold's seats (treated as free for this user); `requested = #seats + Σga`; `bought + requested > maxTickets` → `{'LIMIT_EXCEEDED'}`.
  3. Each requested seat: in `sold` → unavailable; `HGET held` set and ≠ old holdId → unavailable. Any → `{'SEAT_UNAVAILABLE', idx...}` (nothing written).
  4. Each GA line: `remaining + oldQty(tier) ≥ qty` else `{'GA_INSUFFICIENT', tierId}`.
  5. Writes: `HSET held` for new seats not in old; `HDEL held` for old seats not in new (only where value == old holdId); GA: `INCRBY` old qty back, `DECRBY` new qty, adjust `gaheld`; delete old hold hash + `ZREM`; create new hold hash (`userId, seats, ga, expiresAt`), `PEXPIRE` hold hash to ttl + 60 s; `ZADD expiry`; `SET userhold holdId PX ttl`; `INCR version`; `PUBLISH` delta (held = new − old, freed = old − new); return `{'OK', expiresAtMs, v}`.
- `hold_release.lua` KEYS: 1 held, 2 expiry, 3 userhold, 4 version, 5 gaheld, 6 hold, 7.. ga keys. ARGV: holdId, channel, gaTierIds csv. Missing hold → `{'NOT_FOUND'}`. Else `HDEL` only seats whose value == holdId; GA back; `DEL` hold; `ZREM`; clear `userhold` only if it equals holdId; `INCR version`; `PUBLISH`; `{'OK', v}`.
- Wrapper: on `STALE` re-read and retry once; a second `STALE` returns `HasHold` (concurrent create from another tab — safe, nothing written).

- [ ] **Step 1: Failing tests** (`HoldScriptTests`, Testcontainers Redis, isolated env prefix like `QueueRoomHarness`; `InventoryHarness.CreateAsync(containers)` warms an event with N seats and GA tiers via `ReconcileAsync` once Task 5 exists — until then via a direct `HSET meta warm 1` + `SET ga` helper):
  - `Free_seats_are_held_and_published_with_a_new_version` (status Ok, `HGET held` = holdId for each, version 1, one message on the channel with `held` = seats).
  - `A_thousand_concurrent_claims_for_one_seat_yield_exactly_one_ok` (1,000 users, `Task.WhenAll`, `Count(Ok) == 1`, others `SeatUnavailable` with `[seat]`) — **acceptance 1**.
  - `A_partially_taken_selection_writes_nothing` (seats {1,2}, 2 held by B → A gets `Unavailable=[2]`, seat 1 still free).
  - `Replacing_keeps_overlapping_seats_held_throughout` — **acceptance 2 / Review Focus 1**: A holds {1,2}; loop 200×: A replaces with {2,3} then {1,2}; concurrently B tries {2} every iteration; B never Ok; final `HGET held 2` == A's current hold.
  - `A_stale_old_hold_is_refused_and_retried` — **Review Focus 2**: call the script directly with a wrong `expectedOldHoldId` → `STALE`, nothing written; wrapper with a hold replaced between read and call ends Ok without orphaned `held` fields (count of `held` fields == seats of live holds).
  - `Second_hold_without_replace_is_HasHold`.
  - `Ga_holds_decrement_remaining_and_reject_over_claim` (cap 5: hold 3 Ok, hold 3 by another user `GaInsufficient` with tier, remaining 2, `gaheld` 3).
  - `Limit_counts_bought_plus_requested` (`SET bought 3`, max 4, request 2 → `LimitExceeded`).
  - `Releasing_frees_only_seats_still_owned_by_the_hold` — **acceptance 4 / Review Focus 3**: A holds {5}; force-expire by deleting A's `held` field and letting B hold {5}; `ReleaseHoldAsync(A)` → true, `HGET held 5` == B's hold.
  - `Releasing_an_unknown_hold_returns_false_and_changes_nothing` (version unchanged).
  - `Seat_indexes_are_sent_sorted` (request {9,3,7} → hold hash `seats` = "3,7,9") — C9.
  - `Redis_errors_map_to_InventoryUnavailable` (like 06's GuardAsync test; `RedisTimeoutException`, `RedisConnectionException`, `ObjectDisposedException` mapped; `InvalidOperationException` not).
- [ ] **Step 2: Run** `dotnet test tests/TicketRush.Api.IntegrationTests --filter "FullyQualifiedName~Holds.HoldScript"`. Expected: FAIL.
- [ ] **Step 3: Implement** both scripts (header comment: KEYS/ARGV/returns), `InventoryScripts` typed wrappers, `RedisInventoryStore.CreateHoldAsync/ReleaseHoldAsync/GetUserHoldAsync/GetSnapshotAsync/DueHoldsAsync`. Snapshot: one batch (`HKEYS held`, `SMEMBERS sold`, `MGET ga…`, `GET version`, `HGET meta warm`).
- [ ] **Step 4: Run** again. Expected: PASS (12/12).
- [ ] **Step 5: Commit** `feat(07): atomic hold create and release scripts`.

---

### Task 4: `inventory_commit` script

**Files:** Create `Redis/Scripts/inventory_commit.lua`; modify `InventoryScripts.cs`, `RedisInventoryStore.cs`; test `HoldScriptTests.cs` (add).

**Interfaces:** Produces `CommitAsync` (Task 2 signature), used by spec 08.

KEYS: 1 sold, 2 held, 3 expiry, 4 userhold, 5 bought, 6 version, 7 gaheld, 8 hold, 9.. ga keys. ARGV: holdId, userId, seats csv, ga json, channel, gaTierIds csv. If the hold exists: `SADD sold` its seats, `HDEL held` where value == holdId, `gaheld` down (no GA change: already decremented), delete hold, `ZREM`, clear `userhold` if it matches. If it does not (late payment): `SADD sold` seats from ARGV, `DECRBY` GA from ARGV. Both: `INCRBY bought` (seats + Σga), `INCR version`, `PUBLISH` (`sold` = seats). Returns `{'OK', v}`.

- [ ] **Step 1: Failing tests** `Commit_moves_held_seats_to_sold_and_counts_them_as_bought`, `A_late_commit_after_expiry_still_sells_and_takes_ga`, `Committed_seats_cannot_be_held_again`.
- [ ] **Step 2: Run** filter `HoldScript`. Expected: 3 FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run.** Expected: PASS.
- [ ] **Step 5: Commit** `feat(07): inventory commit script for checkout`.

---

### Task 5: Warmer and reconciler

**Files:**
- Create: `Redis/Scripts/inventory_reconcile.lua`; `src/TicketRush.Application/Abstractions/Inventory/ISoldInventorySource.cs`; `src/TicketRush.Infrastructure/Inventory/NoTicketsSoldInventorySource.cs`, `InventorySeeder.cs` (builds `InventorySeed` from `IHoldCatalog` + `ISoldInventorySource`), `InventoryWarmHandler.cs` (`IIntegrationEventHandler<EventPublished>`)
- Create: `src/TicketRush.Worker/Inventory/InventoryReconcilerService.cs`, `src/TicketRush.Worker/Inventory/LiveInventoryEvents.cs` (events with a phase open or opening within `PreQueueMinutes`, refreshed every 5 s — reuse `IQueuePhaseSource`-style query but **without** the `QueueEnabled` filter)
- Modify: `DependencyInjection.cs` (`AddIntegrationEventHandler<EventPublished, InventoryWarmHandler>()`), `Worker/Program.cs`
- Test: `tests/TicketRush.Api.IntegrationTests/Holds/InventoryReconcileTests.cs`

**Interfaces:**
- `public interface ISoldInventorySource { Task<SoldInventory> GetAsync(Guid eventId, CancellationToken ct); }` with `SoldInventory(IReadOnlyList<int> Seats, IReadOnlyDictionary<Guid,int> GaSold, IReadOnlyDictionary<Guid,int> BoughtByUser)`. Until spec 08 the only implementation returns empty (Ruling: no tickets table exists; 08 replaces it).
- `InventorySeeder.SeedAsync(Guid eventId, CancellationToken ct) : Task<ReconcileReport?>` (null for unknown/draft events).
- `inventory_reconcile.lua` KEYS: 1 sold, 2 meta, 3 version, 4 gaheld, 5.. ga keys, then bought keys; ARGV: sold csv, seatCount, gaTierIds csv, ga capacities csv, ga sold csv, bought pairs, channel. Replaces `sold` with the DB set (reports added/removed), sets `ga = cap − soldDb − gaheld`, `SET bought` per user from DB (max of Redis and DB, so an in-flight commit isn't undone), sets `meta warm 1, seatCount, gaCap:*`; publishes a delta and bumps version only when something changed.
- `InventoryReconcilerService` (leader, lock `inventory-reconciler`): on start and every 5 min reconcile every live event; every 5 s reconcile live events whose `meta warm` is missing. Logs drift at Warning with counts (never user ids).

- [ ] **Step 1: Failing tests**
  - `Warming_an_event_sets_ga_to_capacity_and_marks_it_warm`.
  - `Deleted_sold_set_is_restored_from_the_source` — **acceptance 6** with a test `ISoldInventorySource` returning seats {1,2}: `DEL sold`, reconcile → `SMEMBERS sold` = {1,2}, report `SoldAdded = 2`.
  - `Ga_drift_is_corrected_from_capacity_sold_and_held` (cap 10, sold 2, 3 held via a real hold, `SET ga 99` → 5).
  - `Reconcile_without_drift_publishes_nothing`.
  - `Publishing_an_event_warms_it` (outbox handler invoked through `OutboxProcessor` on the seed's opening-soon event after publish, or directly `InventoryWarmHandler.HandleAsync`).
- [ ] **Step 2: Run** filter `InventoryReconcile`. Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run.** Expected: PASS.
- [ ] **Step 5: Commit** `feat(07): inventory warmer and reconciler`.

---

### Task 6: `Idempotency-Key` filter

**Files:**
- Create: `src/TicketRush.Application/Abstractions/Idempotency/IIdempotencyStore.cs`; `src/TicketRush.Infrastructure/Idempotency/RedisIdempotencyStore.cs`; `src/TicketRush.Api/Common/Idempotency/IdempotencyFilter.cs`
- Modify: `RedisKeys.cs` (`Idempotency(userId, key)` → `tr:{env}:idem:{userId:N}:{key}`), `ApiFactory.cs` (test endpoint `POST /_test/idempotent` that counts calls and echoes a new Guid)
- Test: `tests/TicketRush.Api.IntegrationTests/Holds/IdempotencyTests.cs`

**Interfaces:**
```csharp
public abstract record IdempotencyBegin { Started; InProgress; Replay(int Status, string ContentType, byte[] Body); Mismatch; }
public interface IIdempotencyStore
{
    Task<IdempotencyBegin> BeginAsync(Guid userId, Guid key, string fingerprint, TimeSpan ttl, CancellationToken ct); // SET NX PX "pending|<fp>"
    Task CompleteAsync(Guid userId, Guid key, string fingerprint, int status, string contentType, byte[] body, TimeSpan ttl, CancellationToken ct);
    Task AbandonAsync(Guid userId, Guid key, CancellationToken ct); // on 5xx or exception, so a retry can run
}
public static TBuilder RequireIdempotencyKey<TBuilder>(this TBuilder builder, TimeSpan? ttl = null) where TBuilder : IEndpointConventionBuilder;
```
Fingerprint = SHA-256 of method + route path + request body bytes. Stores 2xx and 4xx responses (the client must not get a different answer for the same key); 5xx and exceptions abandon. TTL default = `HoldsOptions.Ttl` for holds. Redis down → 503 `system.busy`.

- [ ] **Step 1: Failing tests** `Missing_or_malformed_key_is_400_key_required`, `Same_key_and_body_replays_the_first_response_and_runs_once` (counter == 1, same body), `Same_key_with_another_body_is_422_key_reused`, `A_retry_while_the_first_runs_is_409_in_progress` — **Review Focus 5** (test endpoint awaits a `TaskCompletionSource` the test releases), `Keys_are_scoped_per_user` (user B with A's key runs normally), `A_5xx_is_not_stored`.
- [ ] **Step 2: Run** filter `Idempotency`. Expected: FAIL.
- [ ] **Step 3: Implement** (buffer the response by swapping `HttpResponse.Body` for a `MemoryStream` inside the filter, copy back after).
- [ ] **Step 4: Run.** Expected: PASS.
- [ ] **Step 5: Commit** `feat(07): idempotency-key endpoint filter`.

---

### Task 7: Hold use cases and best available

**Files:**
- Create: `src/TicketRush.Application/Features/Holds/HoldModels.cs`, `HoldErrors.cs`, `CreateHoldHandler.cs`, `CreateHoldValidator.cs`, `MyHoldHandlers.cs` (`GetMyHoldHandler`, `ReleaseMyHoldHandler`), `BestAvailableSeatPicker.cs`
- Modify: `src/TicketRush.Domain/Primitives/Error.cs` (optional `IReadOnlyDictionary<string, object?>? Extensions`), `src/TicketRush.Api/Common/Http/ResultExtensions.cs` (copy `Extensions` to top-level problem extensions)
- Modify: `src/TicketRush.Application/DependencyInjection.cs` (register handlers + picker)
- Test: `tests/TicketRush.Application.Tests/Holds/CreateHoldHandlerTests.cs`, `BestAvailableSeatPickerTests.cs`, `HoldValidatorTests.cs`

**Interfaces:**
```csharp
public sealed record BestAvailableRequest(Guid TierId, int Quantity);
public sealed record CreateHoldCommand(Guid UserId, Guid EventId, Guid PhaseId, IReadOnlyList<int>? SeatIndexes,
    IReadOnlyList<GaLine>? Ga, BestAvailableRequest? BestAvailable, bool ReplaceExisting);
public sealed record HeldSeat(int Index, string Section, string Row, string Number, Guid TierId, long Price);
public sealed record HeldGa(Guid TierId, string Name, int Quantity, long Price);
public sealed record HoldResponse(Guid HoldId, IReadOnlyList<HeldSeat> Seats, IReadOnlyList<HeldGa> Ga, long Total, DateTimeOffset ExpiresAt, DateTimeOffset ServerNow);
public interface IBestAvailablePicker { IReadOnlyList<IReadOnlyList<int>> Candidates(HoldCatalogEntry catalog, ISet<int> unavailable, Guid tierId, int quantity, int max = 3); }
```
- `HoldErrors`: `SeatsUnavailable(IReadOnlyList<int>)` (Conflict, Extensions `unavailable`), `InsufficientQuantity(Guid tierId)` (Conflict), `LimitExceeded` (Conflict), `AlreadyExists` (Conflict), `EmptySelection` (Validation), `InvalidSeat` (Validation), `NotFound` (NotFound).
- Handler: catalog → phase by `PhaseId` must be open now (else `queue.closed`/`queue.not_open` reused) → validate indexes exist and GA tiers belong to the event (`hold.invalid_seat`) → selection non-empty and ≤ `MaxSeatsPerHold` → for `BestAvailable` try up to 3 candidates (`SeatUnavailable` → next) → map outcome → response with prices from `CatalogTier.Price` (frozen tiers, spec 05). `InventoryUnavailableException` → `CommonErrors.SystemBusy`.
- Picker: seats of the tier grouped by (Section, Row), runs of consecutive `Index` of length N with none unavailable; score = |centreX − 500| + |centreY − 42| (stage centre on the 1000×700 canvas, from `SeatLayoutGenerator`/organizer preview) + row weight; return best `max` runs.

- [ ] **Step 1: Failing tests**
  - Picker: `Picks_adjacent_seats_in_one_row`, `Never_spans_a_gap_or_a_row_end`, `Prefers_seats_nearest_the_stage_centre`, `Returns_nothing_when_no_run_is_long_enough`, `Returns_at_most_three_candidates`.
  - Validator: `Empty_selection_is_rejected`, `More_than_MaxSeatsPerHold_is_rejected`, `Negative_quantities_are_rejected`.
  - Handler (NSubstitute `IInventoryStore`, fake catalog): `Unknown_seat_index_is_invalid_seat`, `Seat_conflict_maps_to_seats_unavailable_with_the_indexes`, `Best_available_tries_the_next_candidate_on_conflict` (store returns SeatUnavailable, then Ok; 2 calls), `Best_available_gives_up_after_three_attempts`, `Prices_come_from_the_frozen_tier_and_sum_to_total`, `Limit_uses_the_phase_from_the_admission`, `Redis_down_is_system_busy`, `A_closed_phase_is_rejected`.
- [ ] **Step 2: Run** `dotnet test tests/TicketRush.Application.Tests --filter Holds`. Expected: FAIL.
- [ ] **Step 3: Implement** (+ `Error.Extensions`, and a `ResultExtensions` unit test `Error_extensions_appear_on_the_problem` in the Api test project if a ResultExtensions test file exists, else in `HoldApiTests`).
- [ ] **Step 4: Run.** Expected: PASS; `dotnet test tests/TicketRush.ArchitectureTests` still PASS.
- [ ] **Step 5: Commit** `feat(07): hold use cases and best-available picker`.

---

### Task 8: Hold endpoints

**Files:**
- Create: `src/TicketRush.Api/Features/Holds/HoldEndpoints.cs`
- Modify: `src/TicketRush.Api/Features/Queue/RequireAdmissionTokenFilter.cs` (store validated claims in `HttpContext.Items[AdmissionClaimsKey]`, add `HttpContext.AdmissionClaims()` extension), `ApiServiceCollectionExtensions.cs` (`v1.MapHoldEndpoints()`)
- Modify: `web/openapi/ticketrush-api.json`, `web/src/lib/api/schema.d.ts` (regenerated)
- Test: `tests/TicketRush.Api.IntegrationTests/Holds/HoldApiTests.cs`

**Interfaces:**
- `POST /api/v1/events/{eventId:guid}/holds` body `CreateHoldRequest(int[]? SeatIndexes, GaLineRequest[]? Ga, BestAvailableRequest? BestAvailable, bool ReplaceExisting)` → 200 `HoldResponse`. Filters in order: `RequireAuthorization` → `RequireAdmissionToken` → `RequireIdempotencyKey(HoldsOptions.Ttl)`.
- `GET .../holds/me` → 200 `HoldResponse` | 404 `hold.not_found`. `DELETE .../holds/me` → 204 always (idempotent).

- [ ] **Step 1: Failing tests** (dev seed `son-tung-mtp-live`; admission via the queue API flow as in `QueueApiTests`, or the existing test helper):
  - `Holding_seats_returns_the_hold_with_prices_and_server_time`.
  - `Without_an_admission_token_is_403_admission_required`.
  - `Another_users_token_is_403_admission_invalid`.
  - `Missing_idempotency_key_is_400`.
  - `Taken_seats_are_409_with_the_unavailable_indexes` (`problem.unavailable` is a JSON int array).
  - `A_second_hold_without_replace_is_409_already_exists`; `ReplaceExisting_swaps_the_hold`.
  - `Get_me_returns_the_hold_or_404`.
  - `Delete_me_is_204_twice` and frees the seats (availability shows them free).
  - `An_expired_hold_releases_with_204_and_does_not_free_a_new_owners_seat` — **acceptance 4** over HTTP.
  - `Redis_down_is_503_system_busy_and_nothing_reaches_the_database` — **acceptance 5**: a second `ApiFactory` with Redis connection string `127.0.0.1:1,abortConnect=false,connectTimeout=200,syncTimeout=200`; count rows in `outbox_messages` and `audit` tables before/after = equal.
  - `Openapi_document_lists_the_hold_routes` (existing OpenAPI snapshot test updated with `UPDATE_OPENAPI=1`).
- [ ] **Step 2: Run** filter `HoldApi`. Expected: FAIL.
- [ ] **Step 3: Implement**; regenerate OpenAPI: `UPDATE_OPENAPI=1 dotnet test tests/TicketRush.Api.IntegrationTests --filter OpenApi` then `cd web && npm run api:gen`.
- [ ] **Step 4: Run** filter `HoldApi|Queue|OpenApi`. Expected: PASS (06 filter tests still green).
- [ ] **Step 5: Commit** `feat(07): hold endpoints`.

---

### Task 9: Live availability

**Files:**
- Modify: `src/TicketRush.Infrastructure/Catalog/EventCatalogReader.cs` / `CachedEventCatalogReader.cs` (availability from `IInventoryStore.GetSnapshotAsync`: `Unavailable = held ∪ sold` sorted, tiers: GA from Redis, seated = seat count − unavailable in tier; `Version = snapshot.Version`), remove the 1 s HybridCache entry for availability (keep the DB part — tiers/seat→tier map — cached via `IHoldCatalog`)
- Test: `tests/TicketRush.Api.IntegrationTests/Catalog/CatalogApiTests.cs` (adjust), `Holds/AvailabilityTests.cs`

- [ ] **Step 1: Failing tests** `Held_and_sold_seats_are_unavailable_immediately` (hold → GET availability within the same test shows them, version > 0), `Ga_remaining_reflects_holds`, `A_cold_event_is_warmed_on_first_read` (snapshot `Warm=false` → seeder runs once), `Redis_down_availability_is_503`.
- [ ] **Step 2: Run** filter `Availability|Catalog`. Expected: new ones FAIL; existing catalog tests still pass.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** `dotnet test tests/TicketRush.Api.IntegrationTests --filter "Availability|Catalog"`. Expected: PASS (incl. `A_thousand_concurrent_requests_for_one_uncached_event_run_one_database_query`).
- [ ] **Step 5: Commit** `feat(07): live seat availability from redis`.

---

### Task 10: Hold expiry service

**Files:** Create `src/TicketRush.Worker/Inventory/HoldExpiryService.cs`; modify `Worker/Program.cs`; test `tests/TicketRush.Api.IntegrationTests/Holds/HoldWorkersTests.cs`.

**Interfaces:** leader lock `hold-expiry`, TTL 6 s, retry 1 s; every 1 s for each event of `LiveInventoryEvents`: `DueHoldsAsync(now, 500)` → `ReleaseHoldAsync` each; public `RunOnceAsync(ct)` and `TickCount` like `QueueAdmitterService`.

- [ ] **Step 1: Failing tests** `Due_holds_are_released_and_seats_published_as_freed` (FakeTimeProvider past `expiresAt`, one `RunOnceAsync`, delta `freed` contains the seats), `Holds_expire_within_two_seconds_of_expiresAt` — **acceptance 3 part 1** (real time, real hosted service, TTL 2 s via options, poll `GetSnapshot` until free, elapsed − ttl ≤ 2 s), `Only_the_leader_releases`.
- [ ] **Step 2: Run** filter `HoldWorkers`. Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run.** Expected: PASS.
- [ ] **Step 5: Commit** `feat(07): hold expiry worker`.

---

### Task 11: Events hub, backplane and delta broadcaster

**Files:**
- Modify: `Directory.Packages.props` (`Microsoft.AspNetCore.SignalR.StackExchangeRedis` at `$(AspNetCoreVersion)`), `src/TicketRush.Infrastructure/TicketRush.Infrastructure.csproj` (package + `<FrameworkReference Include="Microsoft.AspNetCore.App" />` if not present), `src/TicketRush.Worker/TicketRush.Worker.csproj` (FrameworkReference)
- Create: `src/TicketRush.Infrastructure/Realtime/EventsHub.cs` (`[Authorize]` hub: `JoinEvent(Guid eventId)` → group `event:{id}`, `LeaveEvent`), `Realtime/RealtimeSetup.cs` (`AddTicketRushSignalR(this IServiceCollection, IConfiguration)`: `AddSignalR().AddStackExchangeRedis(...)` reusing the `redis` connection string, `ChannelPrefix = RedisChannel.Literal($"{keys.Prefix}signalr")`)
- Create: `src/TicketRush.Worker/Inventory/SeatDeltaBroadcaster.cs`, `src/TicketRush.Worker/Inventory/DeltaCollapser.cs` (pure)
- Modify: `src/TicketRush.Api/Program.cs` / `ApiServiceCollectionExtensions.cs` (`app.MapHub<EventsHub>("/hubs/events")`)
- Test: `tests/TicketRush.Api.IntegrationTests/Holds/SeatDeltaTests.cs`, `tests/TicketRush.Application.Tests/Holds/DeltaCollapserTests.cs` (if `DeltaCollapser` must live in Worker, test it via the Worker alias like 06's `QueueWorkersTests`)

**Interfaces:**
- Ruling: `EventsHub` lives in Infrastructure, not Api, because the Redis backplane names channels after the hub's full type name and the Worker cannot reference Api (confirmed by spike S2).
- `public sealed record SeatDelta(long V, int[] Held, int[] Freed, int[] Sold, TierRemaining[] Tiers);`
- `DeltaCollapser.Add(Guid eventId, RawDelta d)`; `Drain() : IReadOnlyList<(Guid EventId, SeatDelta Delta)>` — per event, final state per seat wins within the window (held then freed → only `freed` if it ended free; freed then held → only `held`), `V` = max `v` seen, `Tiers` = last remaining per tier.
- Broadcaster (leader `seat-delta-broadcaster`): `SubscribeAsync(RedisChannel.Pattern(keys.InvChangesPattern))`, every 250 ms `Drain()` → `hub.Clients.Group($"event:{id}").SendAsync("SeatDelta", delta)`.

- [ ] **Step 1: Failing tests**
  - Collapser: `Held_then_released_inside_a_window_sends_only_the_final_state`, `Version_is_the_highest_seen`, `Events_are_kept_apart`.
  - `SeatDeltaTests.A_hold_reaches_a_joined_client_within_one_second` (API host from `ApiFactory` with the hub; Worker broadcaster hosted in the test; `HubConnection` with the test access token joins `event:{id}`; hold created → `SeatDelta` with the seat in `Held` within 1 s), `An_expired_hold_shows_as_freed_on_other_clients_within_one_second` — **acceptance 3 part 2**, `Unauthenticated_connections_are_rejected`.
- [ ] **Step 2: Run** filter `SeatDelta|DeltaCollapser`. Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run.** Expected: PASS.
- [ ] **Step 5: Commit** `feat(07): seat delta broadcast over signalr`.

---

### Task 12: Queue room status listener, event cancellation and Caddy route

**Files:**
- Create: `src/TicketRush.Infrastructure/Redis/Scripts/queue_set_status.lua`; add `QueueScripts.SetStatusAsync(db, keys, phaseId, RoomStatus status, nowMs)`
- Create: `src/TicketRush.Worker/Inventory/QueueRoomStatusService.cs` (leader `queue-room-status`, every 1 s for live queued sales: snapshot → `available = seatCount + Σ gaCap − sold − held`; `available > 0` → `Open`; `available == 0 && heldTotal > 0` → `AllHeld`; `available == 0 && heldTotal == 0` → `SoldOut`; only writes when the status changes; never touches `Pre`/`Opening`/`Closed`)
- Create: `src/TicketRush.Infrastructure/Inventory/EventCancelledInventoryHandler.cs` (`IIntegrationEventHandler<EventCancelled>`: release every hold of the event, set each phase's room `Closed`)
- Modify: `deploy/Caddyfile` (`handle /hubs/* { reverse_proxy api:8080 }` before the web proxy)
- Test: `tests/TicketRush.Api.IntegrationTests/Holds/QueueRoomStatusTests.cs`, `tests/TicketRush.Api.IntegrationTests/Infrastructure/DeployConfigTests.cs` (or extend an existing deploy-config test if one exists)

Ruling: the spec's `InventoryDepleted` / `InventoryRestored` signals are implemented as this status service writing the room state directly, instead of new outbox events — the queue reads only Redis, and an outbox hop would add seconds of lag for no consumer that needs durability.

`queue_set_status.lua` KEYS: 1 state. ARGV: status, allowed-from list. Sets `status` only when the current one is in the allowed list (`Open`, `AllHeld`, `SoldOut`, or anything for `Closed`); returns the previous status.

- [ ] **Step 1: Failing tests** `Holding_every_seat_turns_the_room_AllHeld`, `Selling_every_seat_turns_it_SoldOut`, `Releasing_a_seat_reopens_it`, `Pre_and_closed_rooms_are_never_touched`, `Cancelling_an_event_releases_its_holds_and_closes_its_rooms`, `Caddy_routes_hubs_straight_to_the_api` (reads `deploy/Caddyfile`, asserts a `/hubs/*` handle with `reverse_proxy api:8080` appears before `reverse_proxy web:3000`).
- [ ] **Step 2: Run** filter `QueueRoomStatus|DeployConfig`. Expected: FAIL.
- [ ] **Step 3: Implement.** Also confirm the 06 queue UI already renders `AllHeld`/`SoldOut` (it does: `queue-states`); no web change.
- [ ] **Step 4: Run.** Expected: PASS. Then full backend: `dotnet test TicketRush.slnx`. Expected: all green.
- [ ] **Step 5: Commit** `feat(07): queue room status from inventory and cancellation cleanup`.

---

### Task 13: Web hold API, seat store and realtime client

**Files:**
- Create: `web/src/lib/seats/types.ts`, `seat-store.ts`, `availability.ts`, `holds-api.ts`, `realtime.ts`, `format.ts` (+ `*.test.ts` each)

**Interfaces:**
```ts
export type SeatState = "free" | "held" | "sold" | "mine" | "selected";
export type SeatStore = {
  get(index: number): SeatState;
  subscribe(index: number, listener: () => void): () => void;   // keyed: only this seat's listeners run
  subscribeAll(listener: () => void): () => void;              // summary panel
  load(snapshot: { version: number; unavailable: number[]; mine: number[] }): void;
  applyDelta(delta: SeatDelta): "applied" | "stale" | "gap";   // gap when delta.v > version + 1
  toggle(index: number): void; selected(): number[]; clearSelection(): void;
  markTaken(indexes: number[]): void;
};
export function createSeatStore(seatCount: number): SeatStore;
export function useSeat(store: SeatStore, index: number): SeatState; // useSyncExternalStore
export async function createHold(eventId: string, body: CreateHoldBody, admissionToken: string, idempotencyKey: string): Promise<Hold>;
export async function getMyHold(eventId: string): Promise<Hold | null>; export async function releaseMyHold(eventId: string): Promise<void>;
export function connectSeatFeed(eventId: string, opts: { accessToken: () => Promise<string>; onDelta(d: SeatDelta): void; onResync(): void }): { stop(): Promise<void> };
export function seatLabel(seat: SeatInfo, price: number, state: SeatState): string; // "Khu A, hàng 3, ghế 12, 1.500.000 ₫, còn trống"
```
- `applyDelta` ignores `v ≤ version` ("stale"); `gap` triggers a refetch by the caller. `connectSeatFeed` uses `withAutomaticReconnect()`, calls `onResync` on `onreconnected` and on first connect, invokes `JoinEvent`.
- `createHold` sends headers `X-Admission-Token` and `Idempotency-Key`; a 409 `hold.seats_unavailable` throws an `ApiError` whose `problem.unavailable` is the index list.

- [ ] **Step 1: Failing tests** `seat-store.test.ts`: `a delta wakes only the listeners of seats it names` (1 listener on seat 1, 1 on seat 2, delta held [1] → only first called), `stale deltas are ignored`, `a version gap is reported`, `selected seats that become held are deselected and reported`, `load replaces the state`; `holds-api.test.ts`: headers sent, unavailable parsed; `realtime.test.ts` (mock `@microsoft/signalr` builder): joins the group, resync on reconnect; `format.test.ts`: label text exactly as the spec.
- [ ] **Step 2: Run** `cd web && npx vitest run src/lib/seats`. Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run.** Expected: PASS.
- [ ] **Step 5: Commit** `feat(07): web seat store, hold api and realtime feed`.

---

### Task 14: Seat map component

**Files:**
- Create: `web/src/features/seats/seat-map/seat-map.tsx` (dynamically imported), `section-overview.tsx`, `seat-layer.tsx`, `seat.tsx`, `use-pan-zoom.ts`, `keyboard.ts`, `legend.tsx` (+ tests)

**Interfaces:**
- `<SeatMap seatMap={SeatMap} tiers={TierInfo[]} store={SeatStore} priceFilter={Set<string>} onToggle={(index) => void} />`.
- Overview: section rectangles (bounding box of each section's seats) with remaining counts; click/Enter zooms into the section; `Esc` / "Toàn cảnh" button returns.
- Zoomed: `<SeatLayer>` renders the section's seats as `memo` `<circle>` using `useSeat`; one delegated `onClick`/`onKeyDown` on the `<svg>`; pan/zoom via wheel, pinch (pointer events) and drag updating one `<g>` `transform` through a ref (no React state per frame).
- Keyboard (`keyboard.ts`, pure): roving tabindex; arrows move to the nearest seat in that direction (same row first for ←/→, nearest by Y then X for ↑/↓); Space/Enter toggles.
- Each seat: `role="button"`, `aria-pressed` when selected, `aria-label = seatLabel(...)`, `aria-disabled` when taken; tier colour + state pattern (not colour only: taken seats get a cross).
- Price chips: seats of other tiers get `opacity .25`.

- [ ] **Step 1: Failing tests** `seat-map.test.tsx`: `renders sections then seats of the zoomed section`, `clicking a free seat calls onToggle`, `taken seats are disabled`, `a delta updates the changed seat's state and label` (the "only that seat re-renders" property is proven by Task 13's keyed-listener test and measured by spike S1, not by test hooks in production code), `price chips dim other tiers`; `keyboard.test.ts`: arrow movement cases on a 3×4 grid incl. row ends; `use-pan-zoom.test.ts`: wheel zoom clamps 0.5–8×.
- [ ] **Step 2: Run** `npx vitest run src/features/seats`. Expected: FAIL.
- [ ] **Step 3: Implement** following spike S1's tuned settings.
- [ ] **Step 4: Run.** Expected: PASS.
- [ ] **Step 5: Commit** `feat(07): accessible seat map`.

---

### Task 15: `/events/[slug]/seats` page

**Files:**
- Create: `web/src/app/(buyer)/events/[slug]/seats/page.tsx`, `web/src/features/seats/seats-view.tsx`, `summary-panel.tsx`, `ga-stepper.tsx`, `hold-countdown.tsx`, `hold-expired-dialog.tsx`, `use-seat-page.ts` (+ tests)
- Modify: `web/src/proxy.ts` (+ test) to guard `/events/<slug>/seats`; `web/messages/{vi,en}.json` (`seats.*`); `web/src/lib/errors.ts` (hold error messages)

**Behaviour:**
- Needs a stored admission for this event (`loadAdmission()`); none or expired → redirect to `/events/<slug>/queue`.
- Loads seat map (ETag cached) + availability + `getMyHold`; seats of my hold show as `mine`.
- Desktop: map left, summary right; mobile (`< md`): summary in `Drawer`. `ShoppingTimer` pill is already in the buyer layout.
- GA tiers: stepper (0..limit) and "Chọn chỗ tốt nhất" per seated tier (calls `createHold` with `bestAvailable`).
- "Tiếp tục": `createHold` (new `crypto.randomUUID()` per selection change, reused on retry of the same selection); on `seats_unavailable` mark them taken, keep the rest, toast `"{n} ghế vừa có người chọn"`; on success navigate to `/checkout?hold=<id>` (page arrives in 08; until then a placeholder route is not created — Ruling: show the hold summary with countdown on the same page and a disabled "Thanh toán (sắp ra mắt)" button, since 08 owns checkout).
- Countdown from `expiresAt` with the server offset (C10); at 0 → `Dialog` "Hết thời gian giữ chỗ" → back to selection (refetch availability).
- Loading skeleton, empty (no seats / GA only), error (503 → retry button) states.

- [ ] **Step 1: Failing tests** `seats-view.test.tsx`: `redirects to the queue without an admission`, `shows my held seats as mine`, `continue holds the selection and shows the countdown`, `a seat conflict keeps the other seats and toasts the count`, `the hold expiry dialog returns to selection`, `best available calls the api with the tier`, `ga stepper respects the limit`, `system busy shows retry`; `proxy.test.ts`: `/events/x/seats` requires sign-in.
- [ ] **Step 2: Run** `npx vitest run src/features/seats src/proxy.test.ts`. Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** `npm test && npm run lint && npm run typecheck`. Expected: PASS.
- [ ] **Step 5: Commit** `feat(07): seat selection page`.

---

### Task 16: Event page CTA and end-to-end

**Files:**
- Modify: `web/src/features/catalog/detail/event-detail-view.tsx` (+ test): a "Mua vé" button when a phase is open, or opens within `PreQueueMinutes`, linking to `/events/<slug>/queue` (instant phases admit at once and the 06 Admitted screen redirects to `/seats`); "Mở bán lúc …" disabled state otherwise.
- Create: `web/e2e/seats.spec.ts`

- [ ] **Step 1: Failing tests** detail view: `shows the buy button for an open sale`, `shows the opening time before the sale`; e2e (mock-built web against the dev stack, as `queue.spec.ts`): `buyer goes event → queue → seats, holds two seats, sees the countdown`, `a seat taken by another buyer turns unavailable without reload` (second browser context holds it via API; first sees it disabled within 2 s) — **Review Focus 4** path, `keyboard-only selection and continue` — **acceptance 7**, `axe passes on /seats in light and dark`.
- [ ] **Step 2: Run** `npx vitest run src/features/catalog` and `npm run build:e2e && npx playwright test e2e/seats.spec.ts`. Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** the same + full `npx playwright test`. Expected: seats specs PASS; only the known offline font failure in `design-system.spec.ts` may remain (report it by name).
- [ ] **Step 5: Commit** `feat(07): buy button and seat selection e2e`.

---

### Task 17: Spec, docs and final verification

**Files:** Modify `specs/07-seat-selection-and-hold.md` (tick acceptance criteria; "Implementation notes": key names, `gaheld`/`meta` keys, version from Lua, hub in Infrastructure, `TtlSeconds`, empty sold source until 08, Caddy route, checkout button placeholder), `specs/06-virtual-waiting-room.md` (move listener/CTA/filter/cancellation from Deferred to done), `README.md` (seats page), `.env.example` if any new setting.

- [ ] **Step 1:** `dotnet test TicketRush.slnx` → all green; `cd web && npm test && npm run lint && npm run typecheck` → green; `npx playwright test` → green except the named known failure.
- [ ] **Step 2:** Update the docs above.
- [ ] **Step 3: Commit** `docs(07): seat selection and hold`, then final whole-branch review and the finishing skill (push + PR on the user's choice).
