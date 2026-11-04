import { describe, expect, it, vi } from "vitest";
import { createSeatStore } from "./seat-store";
import type { SeatDelta } from "./types";

const delta = (from: number, v: number, change: Partial<SeatDelta> = {}): SeatDelta => ({ from, v, held: [], freed: [], sold: [], tiers: [], ...change });

describe("createSeatStore", () => {
  it("loads availability and my hold", () => {
    const store = createSeatStore(10);

    store.load({ version: 4, unavailable: [1, 2, 3], mine: [3] });

    expect([0, 1, 3].map(store.get)).toEqual(["free", "taken", "mine"]);
    expect(store.version()).toBe(4);
  });

  it("wakes only the listeners of the seats a delta names", () => {
    const store = createSeatStore(10);
    store.load({ version: 1, unavailable: [], mine: [] });
    const seat1 = vi.fn();
    const seat2 = vi.fn();
    store.subscribe(1, seat1);
    store.subscribe(2, seat2);

    store.applyDelta(delta(2, 2, { held: [1] }));

    expect(seat1).toHaveBeenCalledTimes(1);
    expect(seat2).not.toHaveBeenCalled();
    expect(store.get(1)).toBe("taken");
  });

  it("ignores deltas it has already seen", () => {
    const store = createSeatStore(10);
    store.load({ version: 5, unavailable: [], mine: [] });

    expect(store.applyDelta(delta(4, 5, { held: [1] })).status).toBe("stale");
    expect(store.get(1)).toBe("free");
  });

  it("reports a gap instead of applying a delta that skips versions", () => {
    const store = createSeatStore(10);
    store.load({ version: 5, unavailable: [], mine: [] });

    expect(store.applyDelta(delta(7, 9, { held: [1] })).status).toBe("gap");
    expect(store.get(1)).toBe("free");
  });

  it("accepts a collapsed delta that starts right after its version", () => {
    const store = createSeatStore(10);
    store.load({ version: 5, unavailable: [2], mine: [] });

    expect(store.applyDelta(delta(6, 9, { held: [1], freed: [2] })).status).toBe("applied");
    expect([1, 2].map(store.get)).toEqual(["taken", "free"]);
    expect(store.version()).toBe(9);
  });

  it("deselects and reports selected seats someone else just took", () => {
    const store = createSeatStore(10);
    store.load({ version: 1, unavailable: [], mine: [] });
    store.toggle(4);
    store.toggle(5);

    const result = store.applyDelta(delta(2, 2, { held: [5] }));

    expect(result.lost).toEqual([5]);
    expect(store.selected()).toEqual([4]);
  });

  it("toggles free seats only", () => {
    const store = createSeatStore(10);
    store.load({ version: 1, unavailable: [2], mine: [] });

    expect(store.toggle(2)).toBe(false);
    expect(store.toggle(3)).toBe(true);
    expect(store.get(3)).toBe("selected");
    store.toggle(3);
    expect(store.get(3)).toBe("free");
  });

  it("keeps my seats mine when the feed reports them held, and frees them when released", () => {
    const store = createSeatStore(10);
    store.load({ version: 1, unavailable: [], mine: [] });
    store.setMine([6]);

    store.applyDelta(delta(2, 2, { held: [6] }));
    expect(store.get(6)).toBe("mine");

    store.applyDelta(delta(3, 3, { freed: [6] }));
    expect(store.get(6)).toBe("free");
  });

  it("marks seats taken after a conflict and drops them from the selection", () => {
    const store = createSeatStore(10);
    store.load({ version: 1, unavailable: [], mine: [] });
    store.toggle(1);
    store.toggle(2);

    store.markTaken([2]);

    expect(store.selected()).toEqual([1]);
    expect(store.get(2)).toBe("taken");
  });

  it("bumps the revision on any change for whole-map subscribers", () => {
    const store = createSeatStore(10);
    const all = vi.fn();
    store.subscribeAll(all);
    const before = store.revision();

    store.toggle(1);

    expect(all).toHaveBeenCalled();
    expect(store.revision()).toBeGreaterThan(before);
  });

  it("stops notifying after unsubscribe", () => {
    const store = createSeatStore(10);
    const listener = vi.fn();
    const off = store.subscribe(1, listener);

    off();
    store.toggle(1);

    expect(listener).not.toHaveBeenCalled();
  });
});
