# Adjacent sale phases in the waiting room — Design (spec 06 leftover)

Source of truth: `specs/06-virtual-waiting-room.md`, "Deferred" item: *adjacent phases (presale then general sale with no gap): the room always shows the open phase, so the next phase has no pre-queue and opens FIFO. Needs a rule for which phase a buyer is queued for.* This document fixes that rule. No new endpoint, no client field.

## Problem

`PhaseSelector.Select` returns the open phase first, else the next to open, else the last closed. When phase B opens right after (or shortly after) phase A, everyone who arrives while A is still open is queued in A. B's `PreQueueMinutes` window (30 min before `opensAt`) therefore never forms: B opens with no random pre-queue, first come first served — the unfair order spec 06 exists to prevent.

## Rule (server-side, decided by the controller; the user chose "server prefers the upcoming phase")

1. **Join target** for a user who is in no room of the event yet: let `open` be the open phase (if any) and `next` the earliest QueueEnabled phase with `OpensAt > now`. If both exist and `next.OpensAt − now ≤ PreQueueMinutes`, the target is **`next`** (its pre-queue is running, so the buyer gets a random position like everyone else who arrived early). Otherwise the target is the existing `PhaseSelector` result. `next` must be `QueueEnabled`; with no queue there is nothing to pre-queue for.
2. **Members keep their room.** `GET /queue/me`, `POST /queue/leave` and an idempotent re-join must find the phase the user is **already in**. Candidates are the two phases above (`open`, `next`). For each candidate in order [`open`, `next`] check `IWaitingRoom.IsMemberAsync`; the first hit is the user's phase. Only when the user is in neither does rule 1 apply. A user in A's line or holding an A admission token is never silently moved to B, and must never see `NotJoined` because the selector changed its mind.
3. **Cost.** Outside the ambiguous window (no `next` within `PreQueueMinutes` while another phase is open) there is exactly one candidate and no extra Redis call: the hot path of `/queue/me` stays as cheap as today (spec: Redis only, p99 < 20 ms). In the window it costs at most one extra membership check.
4. **Access codes.** Only the TARGET phase's code applies: joining `next` needs `next`'s code if it has one (and none if it has none); a member's re-join skips the check as today. Accepted consequence of the rule: a presale buyer who first arrives in the last `PreQueueMinutes` of the presale is queued for the next phase, not the presale.
5. **Out of scope:** admission tokens already issued keep their own `phase` claim; no change to `RequireAdmissionToken`, holds, checkout, or the workers (they already iterate every live phase). No frontend change (the client never sends or reads a phase id).

## Where it lives

`Application/Features/Queue`: a pure `PhaseSelector.SelectJoinTarget(phases, now, preQueue)` (+ unit tests) and a small async resolver in `QueueSales` that applies rule 2 using `IWaitingRoom.IsMemberAsync`; `JoinQueueHandler`, `GetQueueStatusHandler`, `LeaveQueueHandler` use it. Existing `PhaseSelector.Select` keeps its meaning for callers that want "the phase of the moment".

## Tests

Unit (`QueueLogicTests`): no `next` in window → unchanged; `open` + `next` within window → `next`; `next` just outside the window → `open`; `next` not QueueEnabled → `open`; two upcoming phases → the earliest; boundary exactly `PreQueueMinutes`.
Integration (`QueueApiTests`, real Redis/Postgres): presale open + general sale opening in 20 min → a new joiner lands in general's pre-queue with status `Pre`; a user who joined the presale **before** the window and polls `/queue/me` inside the window still gets their presale status (not `NotJoined`); `/queue/leave` frees the right room; a second tab idempotent re-join returns the same number; when the general sale opens, pre-queue members are shuffled with unique numbers (reuse the opener test style); presale joiners' admission still works after the window.
