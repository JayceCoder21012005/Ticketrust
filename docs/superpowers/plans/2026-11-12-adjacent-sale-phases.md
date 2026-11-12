# Adjacent Sale Phases Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A buyer arriving while a phase is open but the next phase's pre-queue window is running is queued for the next phase (random position), without ever moving or hiding members already in a room (spec 06 leftover).

**Architecture:** A pure join-target selector plus an async resolver that first looks for the phase the user is already a member of, then applies the rule. Handlers for join, status and leave use the resolver; no API, frontend or worker change.

**Tech Stack:** .NET 10, xUnit v3, Shouldly, Testcontainers (Redis, Postgres).

**Spec:** `specs/06-virtual-waiting-room.md` (Deferred: adjacent phases). Design: `docs/superpowers/specs/2026-11-12-adjacent-sale-phases-design.md` (binding: read it first).

## Global Constraints

- Spec 00 rules: sealed, primary constructors, `TimeProvider`, `CancellationToken ct` everywhere, methods ≤ 30 lines, `TreatWarningsAsErrors`, Application references no provider.
- `/queue/me` stays Redis-only on the hot path; no extra Redis call when no `next` phase is within `PreQueueMinutes` of an open phase.
- Redis unavailable → fail closed (`system.busy`), as today (`QueueSales.GuardAsync`).
- Baselines before this plan (main at 0838349): run the suites once at the start of the task and record the counts; they must stay green. Run Api tests with `-- --max-threads 2`.
- Never stage `web/package-lock.json`; no `git stash`; do not push. Commit messages end with `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`.

## Review Focus

1. A user who joined phase A before the window and polls `/queue/me`/`/queue/leave` inside the window is never reported `NotJoined` or moved (design rule 2).
2. Two tabs / repeated join in the window return the same number and the same room (idempotent).
3. Boundaries: exactly `PreQueueMinutes` before `next.OpensAt`, `now == open.ClosesAt`, `next` not QueueEnabled, several upcoming phases.
4. Access-code check uses the TARGET phase's hash (not the open phase's).
5. Event with a single phase or non-overlapping far-apart phases behaves exactly as before (all existing queue tests unchanged and green).

---

### Task 1: Join-target selection that respects membership

**Files:**
- Modify: `src/TicketRush.Application/Features/Queue/QueueLogic.cs` (PhaseSelector), `src/TicketRush.Application/Features/Queue/QueueHandlers.cs` (QueueSales + the three handlers)
- Test: `tests/TicketRush.Application.Tests/Queue/QueueLogicTests.cs`, `tests/TicketRush.Api.IntegrationTests/Queue/` (extend the existing queue API test class / fixtures; find them with `QueueApiTests`)
- Modify (doc): `specs/06-virtual-waiting-room.md` (tick the adjacent-phases item with the test names, update the rule text)

**Interfaces:**
- Produces: `PhaseSelector.SelectJoinTarget(IEnumerable<SaleWindow> phases, DateTimeOffset now, TimeSpan preQueue) : SaleWindow?` — pure; rule 1 of the design; falls back to `PhaseSelector.Select`.
- Produces: `QueueSales.ResolveForUserAsync(ISaleWindowProvider provider, IWaitingRoom room, IOptions<QueueOptions>/QueueOptions, Guid eventId, Guid userId, DateTimeOffset now, CancellationToken ct) : Task<Result<SalePhaseInfo>>` — rule 2: candidates [open, next-within-window]; first where `room.IsMemberAsync` is true; else the `SelectJoinTarget` phase; same `event.not_found` / `queue.not_open` errors as `ResolveAsync`. With a single candidate it makes NO membership call.
- Consumes: `IWaitingRoom.IsMemberAsync(SaleWindow, Guid, CancellationToken) : Task<bool>`, `ISaleWindowProvider.GetAsync`, `QueueOptions.PreQueueMinutes`.

- [ ] **Step 1: Failing unit tests** in `QueueLogicTests`: `Join_target_is_the_open_phase_when_no_next_phase_is_near`, `Join_target_is_the_next_phase_when_its_pre_queue_window_is_running`, `Join_target_stays_open_when_next_phase_is_just_outside_the_window` (PreQueueMinutes + 1 s), `Join_target_at_exactly_the_window_boundary_is_the_next_phase`, `Join_target_ignores_a_next_phase_without_a_queue`, `Join_target_picks_the_earliest_of_several_upcoming_phases`, `Join_target_without_an_open_phase_matches_the_existing_selector`. Run `dotnet test tests/TicketRush.Application.Tests` → FAIL (method missing).
- [ ] **Step 2: Implement** `SelectJoinTarget` in `QueueLogic.cs` exactly per design rule 1. Run the unit tests → PASS.
- [ ] **Step 3: Failing integration tests** (Redis + Postgres, reuse the existing fixtures that create events with phases and a controllable clock): `A_new_joiner_inside_the_next_phases_window_is_queued_for_the_next_phase` (status `Pre`), `A_member_of_the_open_phase_keeps_it_when_the_next_window_starts` (joined before the window; `/queue/me` inside the window returns their status, never `queue.not_joined`), `Leave_inside_the_window_frees_the_room_the_user_is_in`, `A_second_tab_rejoin_inside_the_window_returns_the_same_number`, `The_next_phases_access_code_is_the_one_checked_for_a_new_joiner`, `Pre_queue_members_of_the_next_phase_get_unique_random_numbers_when_it_opens` (reuse the opener test pattern), `Single_phase_events_behave_as_before`. Run → FAIL.
- [ ] **Step 4: Implement** `QueueSales.ResolveForUserAsync` and switch `JoinQueueHandler`, `GetQueueStatusHandler`, `LeaveQueueHandler` to it (join keeps its access-code and "closed/not open" checks against the resolved phase). Keep `ResolveAsync` for any other caller. Run the new tests, then the whole `Queue` test namespace and `Architecture` tests → PASS.
- [ ] **Step 5: Full verification**: `dotnet build` (audit on, 0 warnings), Domain, Architecture, Application and the full Api.IntegrationTests suite with output captured to a file; report counts and any failing/flaky test by name.
- [ ] **Step 6: Docs** — in `specs/06-virtual-waiting-room.md` tick the "Adjacent phases" deferred item and rewrite it to state the rule and the accepted consequence (presale joiners arriving in the last `PreQueueMinutes` are queued for the next phase), citing the proving tests.
- [ ] **Step 7: Commit** `feat(06): queue buyers for the next phase when its pre-queue window is running`
