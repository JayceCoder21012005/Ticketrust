# Virtual Waiting Room (core) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Buyers join a per-phase waiting room, get a random (pre-queue) or FIFO position, are admitted at a capped rate, and receive a user-bound admission token; the `/events/[slug]/queue` page shows it all.

**Architecture:** All queue state lives in Redis under hash tag `{sale:<phaseId>}`; every multi-key mutation is one Lua script (`Infrastructure/Redis/Scripts`). Pure-ish logic classes in Infrastructure (`WaitingRoomOpener`, `WaitingRoomAdmitter`, `RedisWaitingRoom`) are driven by thin leader-elected Worker services. Application holds abstractions + handlers (`IWaitingRoom`, `IAdmissionTokenFactory`), the API adds 3 endpoints and the `RequireAdmissionToken` filter. The web polls from a Web Worker that follows the server's `pollAfterMs`.

**Tech Stack:** .NET 10, StackExchange.Redis + Lua, Testcontainers (Redis), Microsoft.IdentityModel JWT, Aspire AppHost, Next 16 / React 19, TanStack Query, Vitest, Playwright.

**Spec:** `specs/06-virtual-waiting-room.md` + `docs/superpowers/specs/2026-11-04-virtual-waiting-room-design.md` (phase A only: inventory listener, waitlist, captcha and wiring the filter onto buy endpoints are deferred to 07/08/12/13).

## Global Constraints

- Keys: `tr:{env}:` prefix from `RedisKeys`, then `q:{sale:<phaseId>}:<name>`. Names: `state` HASH (`status` Pre|Opening|Open|AllHeld|SoldOut|Closed, `opensAt`, `closesAt`, `rateEma`), `pre` SET, `line` HASH userId→number, `seq` STRING, `admitted` STRING, `active` ZSET userId→expiry ms, `tokens` HASH userId→token, plus **`done` SET** (my addition: users who left or expired).
- Position = `number − admitted` (O(1), never `ZRANK`). `aheadOfYou = max(0, position − 1)`.
- `Queue` config defaults: `AdmissionRatePerSecond` 50, `MaxActiveShoppers` 500, `ShoppingWindowMinutes` 10, `PreQueueMinutes` 30. The old `AdmissionsPerSecond` / `AdmissionTokenTtlMinutes` are renamed (no aliases).
- Admission token: JWT HS256, key `AdmissionToken:SigningKey` (≥ 32 chars, separate from `Jwt:SigningKey`), claims `sub`, `evt`, `phase`, `jti`, `exp` = active expiry. Sent in header `X-Admission-Token`.
- `RequireAdmissionToken` checks: signature, not expired, `sub` == authenticated user, `evt` == route `eventId`, user still in `active`. Failures ⇒ 403 `admission.required` (no header) / `admission.invalid` (anything else).
- Errors: `queue.not_open` 409, `queue.closed` 409, `queue.invalid_access_code` 403, `queue.email_not_confirmed` 403 (JWT claim `email_verified` false), `admission.required` 403, `admission.invalid` 403. Redis down on join/me/leave ⇒ 503 `system.busy` (fail closed).
- `pollAfterMs`: position > 10 000 → 15 000; > 1 000 → 8 000; > 100 → 4 000; else 2 000; each ±20 % jitter. In `Pre`: ms until `opensAt + random(0–3 s)` on server clock. `/queue/me` reads Redis only.
- Join window: queue phases from `opensAt − PreQueueMinutes`; non-queue phases from `opensAt`; before ⇒ `queue.not_open`; at/after `closesAt` ⇒ `queue.closed`. `QueueEnabled=false` phases admit instantly (still bounded by `MaxActiveShoppers`).
- Left/expired user: removed from `active`+`tokens`, added to `done`; `/me` returns status `Expired`; a new `join` clears `done` and issues a **new tail number**.
- Web copy Vietnamese only; pages pass axe in light and dark. The strings "Mọi người vào phòng chờ trước giờ mở bán sẽ được xếp thứ tự ngẫu nhiên.", "Còn N người phía trước", "Đừng tải lại trang — bạn sẽ không mất lượt", "Đến lượt bạn!" are fixed by the spec. Title format `(#1.234) TicketRush`.
- Build .NET with `--artifacts-path artifacts` (a running AppHost locks `src/*/bin`). One commit for the whole spec, then a PR (same as 04/05); task "Commit" steps are ledger checkpoints only. Final commit ends with `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`.

## Review Focus

1. **Join during `Opening` (while the shuffle runs).** Gets a number greater than every pre-queue user, no duplicates. → Task 3 `Join_during_opening_gets_number_after_all_pre_users`.
2. **Expired shopper keeps polling.** Must not silently regain a slot because `number ≤ admitted`. → Task 4 `Expired_user_is_not_reclaimed_and_rejoin_goes_to_tail`.
3. **Admitter with `seq = 0`, or `admitted` already `= seq`.** Never admits past `seq`, never goes negative. → Task 4 `Tick_never_admits_beyond_seq`.
4. **Unconfirmed email, wrong/absent presale code, wrong clock.** → Task 7 tests `Unconfirmed_email_is_rejected`, `Wrong_access_code_is_rejected`; Task 9 test `poll scheduler uses server time offset`.
5. **Redis unavailable.** 503 `system.busy`, never an admit. → Task 8 `Redis_down_returns_system_busy`.
6. **Two tabs / two requests racing a claim.** Same token, one `active` entry. → Task 2 `Concurrent_claims_return_same_token`.

---

## File Structure

- `src/TicketRush.Application/Options/ApplicationOptions.cs` — modify `QueueOptions`.
- `src/TicketRush.Application/Abstractions/Queue/` — `IWaitingRoom.cs`, `IAdmissionTokenFactory.cs`, `QueueModels.cs` (enums, records).
- `src/TicketRush.Application/Features/Queue/` — `QueueErrors.cs`, `PhaseSelector.cs`, `PollSchedule.cs`, `JoinQueue.cs`, `GetQueueStatus.cs`, `LeaveQueue.cs`.
- `src/TicketRush.Infrastructure/Redis/Scripts/*.lua` + `RedisKeys.cs` (queue key builders) + `Queue/RedisWaitingRoom.cs`, `Queue/WaitingRoomOpener.cs`, `Queue/WaitingRoomAdmitter.cs`, `Queue/AdmissionTokenFactory.cs`, `Queue/AdmissionTokenOptions.cs`.
- `src/TicketRush.Worker/Queue/QueueOpenerService.cs`, `QueueAdmitterService.cs`.
- `src/TicketRush.Api/Features/Queue/QueueEndpoints.cs`, `RequireAdmissionTokenFilter.cs`.
- Tests: `tests/TicketRush.Api.IntegrationTests/Queue/*`, `tests/TicketRush.Application.Tests/Queue/*`.
- Web: `web/src/lib/queue/{types,poll-schedule,queue-worker,use-queue}.ts(x)`, `web/src/features/queue/*`, `web/src/app/(buyer)/events/[slug]/queue/page.tsx`, `web/src/components/shared/shopping-timer.tsx`.

---

### Task 1: Options, keys, configuration

**Files:** modify `ApplicationOptions.cs`, `RedisKeys.cs`, `Api/appsettings.json`, `Worker/appsettings.json`, `AppHost.cs`, `docker-compose.prod.yml`, `tests/.../Infrastructure/TestSettings.cs`; create `Infrastructure/Queue/AdmissionTokenOptions.cs`; test `tests/TicketRush.Application.Tests/Queue/QueueOptionsTests.cs`.

**Interfaces — Produces:**
- `QueueOptions { int AdmissionRatePerSecond=50 [1..10000]; int MaxActiveShoppers=500 [1..100000]; int ShoppingWindowMinutes=10 [1..60]; int PreQueueMinutes=30 [1..240] }`
- `AdmissionTokenOptions { SectionName="AdmissionToken"; [Required][MinLength(32)] string SigningKey; Issuer="ticketrush-api"; Audience="ticketrush-admission" }`
- `RedisKeys`: `RedisKey QueueState(Guid phaseId)`, `QueuePre`, `QueueLine`, `QueueSeq`, `QueueAdmitted`, `QueueActive`, `QueueTokens`, `QueueDone` (same signature), all `tr:{env}:q:{sale:<phaseId:N>}:<name>`; update the class doc list.

- [ ] **Step 1: Failing test** `QueueOptionsTests`: defaults equal the Global Constraints values; `MaxActiveShoppers = 0` fails `Validator.TryValidateObject`; `RedisKeys` for env `x`, phase `p` gives `tr:x:q:{sale:<p:N>}:line`, and all eight keys share the same `{sale:…}` tag.
- [ ] **Step 2:** Run `dotnet test tests/TicketRush.Application.Tests --filter QueueOptionsTests --artifacts-path artifacts` → FAIL (members missing). (Keys test lives in the integration project if `RedisKeys` isn't visible to Application.Tests; place it there.)
- [ ] **Step 3:** Implement the options/keys above. Register `AdmissionTokenOptions` with `AddValidatedOptions`. AppHost: new secret parameter `admissionTokenSigningKey` → env `AdmissionToken__SigningKey` on api **and** worker. `TestSettings`: add `["AdmissionToken:SigningKey"] = "test-admission-key-long-enough-0123456789"`. compose: `AdmissionToken__SigningKey: ${ADMISSION_TOKEN_SIGNING_KEY:?set ADMISSION_TOKEN_SIGNING_KEY}` (+ `.env.example`). Fix any code/config still using the old field names (`grep AdmissionsPerSecond`).
- [ ] **Step 4:** Run the tests + `dotnet build --artifacts-path artifacts` → PASS.
- [ ] **Step 5:** Ledger checkpoint.

### Task 2: Lua scripts and `RedisWaitingRoom`

**Files:** create `Redis/Scripts/queue_join.lua`, `queue_finalize_open.lua`, `queue_status_claim.lua` (embedded resources, each with a header comment: KEYS, ARGV, returns), `Application/Abstractions/Queue/QueueModels.cs`, `IWaitingRoom.cs`, `Infrastructure/Queue/RedisWaitingRoom.cs`; test `tests/.../Queue/WaitingRoomScriptTests.cs`.

**Interfaces — Produces:**
```csharp
public enum RoomStatus { Pre, Opening, Open, AllHeld, SoldOut, Closed }
public sealed record SaleWindow(Guid EventId, Guid PhaseId, DateTimeOffset OpensAt, DateTimeOffset ClosesAt, bool QueueEnabled);
public sealed record JoinResult(RoomStatus Status, long? Number);      // Number null while in `pre`
public sealed record ClaimResult(RoomStatus Status, long? Number, long Admitted, long Seq, long ActiveCount,
                                 bool Claimed, bool Active, bool Done, DateTimeOffset? ActiveUntil, string? Token);
public interface IWaitingRoom
{
    Task<JoinResult> JoinAsync(SaleWindow sale, Guid userId, CancellationToken ct);
    Task<ClaimResult> StatusAsync(SaleWindow sale, Guid userId, CancellationToken ct); // runs queue_status_claim, then HSETNX token
    Task LeaveAsync(SaleWindow sale, Guid userId, CancellationToken ct);
    Task<bool> IsActiveAsync(Guid phaseId, Guid userId, CancellationToken ct);
    Task<double> GetRateEmaAsync(Guid phaseId, CancellationToken ct);
}
```
`RedisWaitingRoom` ctor: `(IConnectionMultiplexer, RedisKeys, IOptions<QueueOptions>, IAdmissionTokenFactory, TimeProvider)`; wraps `RedisException`/`RedisTimeoutException` into `QueueUnavailableException` (Application) mapped to `CommonErrors.SystemBusy` in handlers.

Script behaviour (from spec, plus my additions): `join` — state is created with `HSETNX` before the script from the clock (`now < opensAt` ⇒ Pre; `< closesAt` ⇒ Open (Open directly for non-queue phases); else Closed). Idempotent: in `line` ⇒ return number; in `pre` ⇒ return null number; `done` member ⇒ `SREM done`, `HDEL line`, treat as new; Pre/Opening ⇒ `SADD pre`; Open/AllHeld ⇒ `INCR seq`, `HSET line`; Closed ⇒ status only. `finalize_open` — assigns `INCR seq` + `HSET line` to every `pre` member not yet in `line`, `DEL pre`, status Open. `status_claim` — returns state, number, admitted, ZCARD, expiry; claims when `(number ≤ admitted or instant=1)` and not `done` and (already active or `ZCARD < max`) using `ZADD NX` with expiry `now+window`; never re-adds a `done` user.

- [ ] **Step 1: Failing tests** (Testcontainers Redis via `ContainersFixture`, unique `RedisOptions.Environment` per test):
  - `Join_before_open_goes_to_pre_without_number` / `Join_is_idempotent_and_returns_same_number`.
  - `Join_during_opening_gets_number_after_all_pre_users` (Review Focus 1): 3 pre users, set status Opening, finalize concurrently with a late join; late number > 3, all numbers unique 1..4.
  - `Claim_respects_MaxActiveShoppers` (max 2, admitted 5, 4 users ⇒ exactly 2 `Claimed`).
  - `Concurrent_claims_return_same_token` (Review Focus 6): 20 parallel `StatusAsync` for one admitted user ⇒ one token value, `ZCARD active == 1`.
  - `Claim_instant_for_non_queue_phase_admits_without_admitter`.
- [ ] **Step 2:** Run `--filter WaitingRoomScriptTests` → FAIL (types missing).
- [ ] **Step 3:** Implement scripts and `RedisWaitingRoom`. Load via `LuaScript`/`IServer.ScriptLoad` once per process (cache SHA; fall back on NOSCRIPT). `StatusAsync`: after script, if `Active` and no token, build with `IAdmissionTokenFactory.Create(userId, sale.EventId, sale.PhaseId, activeUntil)` then `HSETNX tokens`; if HSETNX loses, `HGET` the winner.
- [ ] **Step 4:** Tests PASS.
- [ ] **Step 5:** Ledger checkpoint.

### Task 3: Opener (shuffle at T0)

**Files:** create `Infrastructure/Queue/WaitingRoomOpener.cs`; test `tests/.../Queue/WaitingRoomOpenerTests.cs`.

**Interfaces — Produces:** `public sealed class WaitingRoomOpener(IConnectionMultiplexer, RedisKeys)` with `Task<long> OpenAsync(Guid phaseId, CancellationToken ct)` (returns pre-queue size) and `internal static void Shuffle<T>(Span<T> items)` (Fisher–Yates with `RandomNumberGenerator.GetInt32`). Flow: `state.status = Opening` (only if currently Pre; otherwise no-op, so it is idempotent after a failover) → `SMEMBERS pre` → shuffle → batched `HSET line` (1..n) + `SET seq n` → `queue_finalize_open`.

- [ ] **Step 1: Failing tests:** `Ten_thousand_pre_users_get_unique_numbers_1_to_10000`; `Two_runs_produce_different_orders`; `Open_is_idempotent_when_already_open`; `Late_join_after_open_gets_number_above_every_pre_user`.
- [ ] **Step 2:** Run → FAIL. **Step 3:** Implement. **Step 4:** PASS. **Step 5:** Ledger checkpoint.

### Task 4: Admitter (rate, expiry, EMA)

**Files:** create `Infrastructure/Queue/WaitingRoomAdmitter.cs`; test `tests/.../Queue/WaitingRoomAdmitterTests.cs`.

**Interfaces — Produces:** `public sealed class WaitingRoomAdmitter(IConnectionMultiplexer, RedisKeys, IOptions<QueueOptions>, TimeProvider)` with `Task TickAsync(Guid phaseId, CancellationToken ct)`: only when status `Open`; remove expired from `active` (`ZRANGEBYSCORE -inf now`), and for each also `HDEL tokens`, `SADD done`; `step = min(AdmissionRatePerSecond, MaxActiveShoppers − ZCARD)`; `admitted = min(admitted + step, seq)` (one atomic script `queue_admit.lua`, header-commented; fourth script is my addition to make the tick atomic); update `rateEma` (alpha = 2/(60+1)) with the actual admitted count of the tick. `Task CloseAsync(Guid phaseId, ct)` sets status `Closed`.

- [ ] **Step 1: Failing tests:** `Tick_admits_up_to_rate`, `Tick_never_admits_beyond_seq` (Review Focus 3, includes `seq=0`), `Active_never_exceeds_max_under_load` (500 users claiming concurrently while ticking, `ZCARD ≤ max` at every sample), `Expired_user_is_not_reclaimed_and_rejoin_goes_to_tail` (Review Focus 2; use `FakeTimeProvider`), `Tick_is_noop_unless_open`.
- [ ] **Step 2–4:** FAIL → implement → PASS. **Step 5:** Ledger checkpoint.

### Task 5: Worker services and leader failover

**Files:** create `Worker/Queue/QueueOpenerService.cs`, `QueueAdmitterService.cs`; modify `Worker/Program.cs`; create `Application/Abstractions/Queue/IQueuePhaseSource.cs` + `Infrastructure/Queue/QueuePhaseSource.cs`; test `tests/.../Queue/QueueWorkersTests.cs` (add a `ProjectReference` to the Worker project in the integration test csproj if missing).

**Interfaces:** `IQueuePhaseSource.ListLiveAsync(DateTimeOffset now, TimeSpan lookahead, CancellationToken ct) → IReadOnlyList<SaleWindow>`: phases of **Published** events with `QueueEnabled` where `OpensAt − lookahead ≤ now < ClosesAt`, plus phases that closed within the last minute (so `Closed` is written). Both services extend `LeaderElectedBackgroundService` (locks `queue-opener`, `queue-admitter`, TTL 10 s so failover < 10 s) and loop every 1 s: opener opens phases whose `OpensAt ≤ now`; admitter ticks `Open` ones and closes those past `ClosesAt`. The DB read happens at most once per 5 s per service (cache the list), never on the `/queue/me` path.

- [ ] **Step 1: Failing tests:** `Opener_opens_phase_at_opensAt` (FakeTimeProvider), `Admitter_closes_phase_at_closesAt`, `Leader_failover_takes_over_within_10s_without_skipping_or_duplicating_numbers` (two `QueueAdmitterService` instances on one Redis; dispose the leader mid-run while joins continue; afterwards `line` numbers are exactly 1..seq, `admitted ≤ seq`).
- [ ] **Step 2–4:** FAIL → implement → PASS. **Step 5:** Ledger checkpoint.

### Task 6: Admission token factory

**Files:** create `Application/Abstractions/Queue/IAdmissionTokenFactory.cs`, `Infrastructure/Queue/AdmissionTokenFactory.cs`; test `tests/.../Queue/AdmissionTokenFactoryTests.cs`.

**Interfaces — Produces:** `string Create(Guid userId, Guid eventId, Guid phaseId, DateTimeOffset expiresAt)`; `AdmissionTokenClaims? Validate(string token)` where `record AdmissionTokenClaims(Guid UserId, Guid EventId, Guid PhaseId, string Jti, DateTimeOffset ExpiresAt)`; `Validate` returns null for bad signature, expiry, `alg=none`, or a token signed with the **access-token** key (`Jwt:SigningKey`).

- [ ] **Step 1: Failing tests** for each of those cases + round trip. **Step 2–4:** FAIL → implement with `JsonWebTokenHandler`, HS256 only, zero clock skew → PASS. **Step 5:** Ledger checkpoint.

### Task 7: Application handlers

**Files:** create `Application/Features/Queue/{QueueErrors,PhaseSelector,PollSchedule,JoinQueue,GetQueueStatus,LeaveQueue}.cs`; tests `tests/TicketRush.Application.Tests/Queue/*`.

**Interfaces — Produces:**
- `QueueErrors`: `NotOpen`, `Closed`, `InvalidAccessCode`, `EmailNotConfirmed`, `AdmissionRequired`, `AdmissionInvalid` with the codes/types in Global Constraints.
- `PhaseSelector.Select(IEnumerable<SaleWindow> phases, DateTimeOffset now, TimeSpan preQueue) → SaleWindow?`: the open phase, else the next one whose join window has started, else the last closed (for the `Closed` message).
- `PollSchedule.NextPollMs(RoomStatus status, long position, DateTimeOffset now, DateTimeOffset opensAt, Func<double> random) → int`.
- `record JoinQueueCommand(Guid UserId, Guid EventId, bool EmailVerified, string? AccessCode)`; `record QueueStatusResponse(string Status, long? Position, long? AheadOfYou, int? EstimatedWaitSeconds, DateTimeOffset OpensAt, DateTimeOffset ServerNow, int PollAfterMs, string? AdmissionToken, DateTimeOffset? TokenExpiresAt)`. `Status` values: `Pre|Waiting|Admitted|AllHeld|SoldOut|Closed|Expired`.
- Handlers `JoinQueueHandler`, `GetQueueStatusHandler(Guid userId, Guid eventId)`, `LeaveQueueHandler` return `Result<QueueStatusResponse>` / `Result`. ETA = `ahead / max(rateEma, 1)` rounded up to whole seconds; falls back to `AdmissionRatePerSecond` when EMA is 0. Event must be Published (else `EventErrors.NotFound`); access code compared with `IAccessCodeHasher` when the phase has a hash.

- [ ] **Step 1: Failing tests:** `PollSchedule` table (thresholds 10 001 / 1 001 / 101 / 100 ⇒ 15 000 / 8 000 / 4 000 / 2 000 ± 20 %; Pre ⇒ ms to `opensAt` + 0..3 000, never negative); `PhaseSelector` cases; handler tests with a fake `IWaitingRoom`: `Unconfirmed_email_is_rejected`, `Wrong_access_code_is_rejected`, `Join_before_window_is_not_open`, `Redis_exception_maps_to_system_busy`.
- [ ] **Step 2–4:** FAIL → implement → PASS. **Step 5:** Ledger checkpoint.

### Task 8: API endpoints and `RequireAdmissionToken`

**Files:** create `Api/Features/Queue/QueueEndpoints.cs`, `RequireAdmissionTokenFilter.cs`; modify `ApiServiceCollectionExtensions.cs` (`v1.MapQueueEndpoints()`), `Infrastructure/DependencyInjection.cs` (register `IWaitingRoom`, `IAdmissionTokenFactory`, `IQueuePhaseSource`, opener, admitter, `AdmissionTokenOptions`), `tests/.../Infrastructure/ApiFactory.cs` (test endpoint guarded by the filter); tests `tests/.../Queue/QueueApiTests.cs`.

**Interfaces — Produces:** routes `POST /events/{eventId:guid}/queue/join` (`{accessCode?, captchaToken?}`; `captchaToken` accepted and ignored until 12), `GET .../queue/me`, `POST .../queue/leave`, all `.RequireAuthorization()`. Extension `RouteHandlerBuilder RequireAdmissionToken(this RouteHandlerBuilder b)` (filter reads route value `eventId` or `id`, header `X-Admission-Token`, uses `IAdmissionTokenFactory.Validate` + `IWaitingRoom.IsActiveAsync`).

- [ ] **Step 1: Failing tests** (real api + containers; open a phase by driving `WaitingRoomOpener`/`Admitter` directly): `Join_twice_from_two_tabs_returns_same_number_and_token`, `Late_joiner_gets_number_above_pre_queue_users`, `Token_used_by_another_account_is_403_admission_invalid`, `Missing_token_is_403_admission_required`, `Token_for_other_event_is_403`, `Revoked_after_leave_is_403`, `Me_never_queries_postgres_after_first_call` (use `ApiFactory.Queries` counter), `Redis_down_returns_system_busy`, `Anonymous_is_401`.
- [ ] **Step 2–4:** FAIL → implement → PASS (`dotnet test tests/TicketRush.Api.IntegrationTests --filter Queue --artifacts-path artifacts`). **Step 5:** Ledger checkpoint.

### Task 9: Dev seed + OpenAPI client + web queue library

**Files:** modify `Infrastructure/Persistence/Seed/SeedEventCatalog.cs` (one published event with a queue phase opening ~2 min after seeding, Vietnamese title); regenerate `web/src/lib/api/schema.d.ts` (`npm run api:gen` per `package.json`); create `web/src/lib/queue/{types.ts,poll-schedule.ts,queue-worker.ts,use-queue.ts}`; tests `web/src/lib/queue/*.test.ts`.

**Interfaces — Produces:**
- `nextDelayMs(res: {pollAfterMs:number}, failures: number): number` — follows the server, on errors backs off `min(30_000, 1000 * 2^failures)` plus jitter.
- `queue-worker.ts`: a module Web Worker; messages in `{type:"start", url, token?}|{type:"stop"}`, out `{type:"status", body}|{type:"error", status}`; it does not import React. Its scheduling uses `nextDelayMs` only.
- `useQueue(eventId)` returns `{ status, position, startPosition, token, error, join, leave }`, using server clock offset from `serverNow` (`clockOffsetMs`).

- [ ] **Step 1: Failing tests:** `nextDelayMs follows pollAfterMs`, `backs off on errors and caps at 30s`, `poll scheduler uses server time offset` (device clock 10 min off, `TZ=America/New_York`), `useQueue progress uses first known position` (jsdom, fake Worker).
- [ ] **Step 2–4:** `npx vitest run src/lib/queue` FAIL → implement → PASS. **Step 5:** Ledger checkpoint.

### Task 10: Queue page, components, ShoppingTimer

**Files:** create `web/src/app/(buyer)/events/[slug]/queue/page.tsx`, `web/src/features/queue/{queue-view.tsx,pre-state.tsx,waiting-state.tsx,admitted-state.tsx,closed-state.tsx,use-queue-effects.ts}`, `web/src/components/shared/shopping-timer.tsx`; modify `web/src/app/(buyer)/layout.tsx` (mount `ShoppingTimer`), i18n dictionary in `web/src/lib/i18n.ts` (`queue.*` keys); tests beside each component.

Behaviour (spec): full-screen blurred cover with a centred card; **Pre** countdown on server time with the fairness sentence; **Waiting** progress bar (`startPosition → 0`), "Còn N người phía trước", ETA rounded to "khoảng N phút", reload warning, tab title `(#1.234) TicketRush` (restore on unmount); **Admitted** short chime, title "Đến lượt bạn!", optional `Notification`, auto-redirect to `/events/{slug}/seats` after 3 s with a button; **AllHeld/SoldOut/Closed/Expired** messages (SoldOut waitlist button is deferred to 13 — render nothing). Optional Screen Wake Lock behind `navigator.wakeLock` feature check. `ShoppingTimer` reads token expiry from session storage written by `useQueue` and shows `mm:ss` pill on all buyer pages; hides at 0 and shows an expired toast.

- [ ] **Step 1: Failing tests** (Vitest + Testing Library): `waiting-state renders position, ETA and progress`, `admitted-state redirects after 3s`, `tab title shows (#1.234) TicketRush and is restored`, `shopping-timer counts down on server time and hides at zero`, `pre-state shows fairness sentence`, plus axe checks like the existing catalog tests.
- [ ] **Step 2–4:** FAIL → implement → PASS (`npm test`, `npm run lint`, `npm run typecheck`). **Step 5:** Ledger checkpoint.

### Task 11: End-to-end, docs, acceptance sweep

**Files:** create `web/e2e/queue.spec.ts` (extend `mock-api.mjs` with a scripted queue sequence Pre → Waiting → Admitted); modify `README.md` (config keys, `AdmissionToken__SigningKey`), `specs/06-virtual-waiting-room.md` checkboxes (tick phase-A criteria only, note the rest as deferred).

- [ ] **Step 1:** Playwright flow: open `/events/{slug}/queue`, see countdown, then waiting, then admitted and redirect; background-tab test (page hidden via `visibilitychange` still advances within one poll interval — worker keeps polling).
- [ ] **Step 2:** Run everything: `dotnet test --artifacts-path artifacts`, `npm test`, `npm run lint`, `npm run typecheck`, `npm run test:e2e`; all must pass. Load check: run the Task 4 concurrency test with 10 000 users once manually and confirm cap holds.
- [ ] **Step 3:** Manual smoke via AppHost (`superpowers:verification-before-completion`): seeded phase opens, numbers assigned, admitted, token accepted by the test-only guarded endpoint.
- [ ] **Step 4:** Single commit for the spec, push, PR (body ends with the Claude Code attribution line).

---

## Self-Review

- **Spec coverage:** keys/scripts (T2), workers + failover + close (T3–T5), token + filter (T6, T8), API + errors + polling rules (T7–T8), frontend states/Web Worker/timer (T9–T10), acceptance criteria 1–6 mapped: 10k random (T3), post-T0 ordering (T2/T3/T8), idempotent token (T2/T8), cap (T4), foreign token 403 (T8), failover (T5), background tab (T11). Deferred: inventory listener, waitlist, captcha, filter on buy endpoints.
- **Type consistency:** `SaleWindow`, `RoomStatus`, `ClaimResult`, `IWaitingRoom` signatures are defined once in T2 and reused in T5–T8. `QueueStatusResponse.Status` strings match the `types.ts` union in T9.
- **Proportion:** every task ends with an independently testable deliverable; bodies are left to the implementer except algorithm-defining behaviour.
