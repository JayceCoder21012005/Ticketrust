import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { createPoller, type PollOutcome } from "./poller";
import type { QueueStatus } from "./types";

const status = (overrides: Partial<QueueStatus> = {}): QueueStatus => ({
  status: "Waiting",
  position: 500,
  aheadOfYou: 499,
  estimatedWaitSeconds: 60,
  opensAt: "2026-12-01T10:00:00Z",
  serverNow: "2026-12-01T10:05:00Z",
  pollAfterMs: 4_000,
  admissionToken: null,
  tokenExpiresAt: null,
  ...overrides,
});

const ok = (overrides: Partial<QueueStatus> = {}): PollOutcome => ({ kind: "ok", status: status(overrides) });

function setup(outcomes: PollOutcome[]) {
  const queue = [...outcomes];
  const poll = vi.fn(async () => queue.shift() ?? ok());
  const events = {
    onStatus: vi.fn(),
    onUnauthorized: vi.fn(),
    onNotJoined: vi.fn(),
    onError: vi.fn(),
  };
  const poller = createPoller({ poll, events, random: () => 0.5 });
  return { poller, poll, events };
}

describe("createPoller", () => {
  beforeEach(() => vi.useFakeTimers());
  afterEach(() => vi.useRealTimers());

  it("polls at once, then again after the server-chosen delay", async () => {
    const { poller, poll, events } = setup([ok({ pollAfterMs: 4_000 }), ok({ position: 400, pollAfterMs: 2_000 })]);

    poller.start("tok");
    await vi.advanceTimersByTimeAsync(0);
    expect(poll).toHaveBeenCalledTimes(1);
    expect(poll).toHaveBeenLastCalledWith("tok");
    expect(events.onStatus).toHaveBeenCalledTimes(1);

    await vi.advanceTimersByTimeAsync(3_999);
    expect(poll).toHaveBeenCalledTimes(1);
    await vi.advanceTimersByTimeAsync(1);
    expect(poll).toHaveBeenCalledTimes(2);
    expect(events.onStatus).toHaveBeenLastCalledWith(expect.objectContaining({ position: 400 }));
  });

  it("backs off after errors and recovers on the next success", async () => {
    const { poller, poll, events } = setup([{ kind: "error", httpStatus: 503 }, { kind: "error", httpStatus: 503 }, ok()]);

    poller.start("tok");
    await vi.advanceTimersByTimeAsync(0);
    expect(events.onError).toHaveBeenLastCalledWith(1);
    await vi.advanceTimersByTimeAsync(1_000);
    expect(poll).toHaveBeenCalledTimes(2);
    expect(events.onError).toHaveBeenLastCalledWith(2);
    await vi.advanceTimersByTimeAsync(2_000);
    expect(poll).toHaveBeenCalledTimes(3);
    expect(events.onStatus).toHaveBeenCalledTimes(1);
  });

  it("treats a thrown poll as an error, not a crash", async () => {
    const poll = vi.fn().mockRejectedValueOnce(new Error("offline")).mockResolvedValue(ok());
    const events = { onStatus: vi.fn(), onUnauthorized: vi.fn(), onNotJoined: vi.fn(), onError: vi.fn() };
    const poller = createPoller({ poll, events, random: () => 0.5 });

    poller.start("tok");
    await vi.advanceTimersByTimeAsync(0);
    expect(events.onError).toHaveBeenCalledWith(1);
    await vi.advanceTimersByTimeAsync(1_000);
    expect(events.onStatus).toHaveBeenCalledTimes(1);
  });

  it("pauses on 401 until it receives a fresh token, then polls with it immediately", async () => {
    const { poller, poll, events } = setup([{ kind: "unauthorized" }, ok()]);

    poller.start("old");
    await vi.advanceTimersByTimeAsync(0);
    expect(events.onUnauthorized).toHaveBeenCalledTimes(1);
    await vi.advanceTimersByTimeAsync(60_000);
    expect(poll).toHaveBeenCalledTimes(1);

    poller.setToken("fresh");
    await vi.advanceTimersByTimeAsync(0);
    expect(poll).toHaveBeenCalledTimes(2);
    expect(poll).toHaveBeenLastCalledWith("fresh");
  });

  it("stops and reports when the server no longer knows the user", async () => {
    const { poller, poll, events } = setup([{ kind: "notJoined" }]);

    poller.start("tok");
    await vi.advanceTimersByTimeAsync(0);
    await vi.advanceTimersByTimeAsync(120_000);

    expect(events.onNotJoined).toHaveBeenCalledTimes(1);
    expect(poll).toHaveBeenCalledTimes(1);
  });

  it("stops for good when the event is gone (cancelled or unpublished) instead of retrying forever", async () => {
    const { poller, poll, events } = setup([{ kind: "gone" }]);

    poller.start("tok");
    await vi.advanceTimersByTimeAsync(0);
    await vi.advanceTimersByTimeAsync(300_000);

    expect(events.onNotJoined).toHaveBeenCalledTimes(1);
    expect(events.onError).not.toHaveBeenCalled();
    expect(poll).toHaveBeenCalledTimes(1);
  });

  it.each(["Closed", "Expired"] as const)("stops polling once the status is %s", async (terminal) => {
    const { poller, poll, events } = setup([ok({ status: terminal, position: null, pollAfterMs: 60_000 })]);

    poller.start("tok");
    await vi.advanceTimersByTimeAsync(0);
    await vi.advanceTimersByTimeAsync(300_000);

    expect(events.onStatus).toHaveBeenCalledTimes(1);
    expect(poll).toHaveBeenCalledTimes(1);
  });

  it("keeps polling after Admitted so the token stays alive for the timer", async () => {
    const { poller, poll } = setup([ok({ status: "Admitted", position: null, pollAfterMs: 30_000 }), ok({ status: "Admitted", position: null })]);

    poller.start("tok");
    await vi.advanceTimersByTimeAsync(30_000);

    expect(poll).toHaveBeenCalledTimes(2);
  });

  it("stop() cancels the pending poll and ignores a response still in flight", async () => {
    let release: (outcome: PollOutcome) => void = () => {};
    const poll = vi.fn(() => new Promise<PollOutcome>((resolve) => (release = resolve)));
    const events = { onStatus: vi.fn(), onUnauthorized: vi.fn(), onNotJoined: vi.fn(), onError: vi.fn() };
    const poller = createPoller({ poll, events });

    poller.start("tok");
    await vi.advanceTimersByTimeAsync(0);
    poller.stop();
    release(ok());
    await vi.advanceTimersByTimeAsync(60_000);

    expect(events.onStatus).not.toHaveBeenCalled();
    expect(poll).toHaveBeenCalledTimes(1);
  });

  it("pollNow() polls right away instead of waiting for the timer", async () => {
    const { poller, poll } = setup([ok({ pollAfterMs: 15_000 }), ok()]);

    poller.start("tok");
    await vi.advanceTimersByTimeAsync(0);
    poller.pollNow();
    await vi.advanceTimersByTimeAsync(0);

    expect(poll).toHaveBeenCalledTimes(2);
  });
});
