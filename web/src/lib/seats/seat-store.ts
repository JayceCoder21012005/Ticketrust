import type { SeatDelta, SeatState } from "./types";

export type DeltaResult = {
  status: "applied" | "stale" | "gap";
  /** Seats the buyer had selected that someone else just took. */
  lost: number[];
};

export type SeatStore = {
  get(index: number): SeatState;
  /** Keyed: only this seat's listeners run when it changes (spec 07: 60 fps with 10,000 seats). */
  subscribe(index: number, listener: () => void): () => void;
  /** Any change (summary panel, counters); read {@link SeatStore.revision} as the snapshot. */
  subscribeAll(listener: () => void): () => void;
  revision(): number;
  version(): number;
  load(snapshot: { version: number; unavailable: number[]; mine: number[] }): void;
  applyDelta(delta: SeatDelta): DeltaResult;
  toggle(index: number): boolean;
  selected(): number[];
  clearSelection(): void;
  /** The seats of the buyer's own hold. */
  setMine(indexes: number[]): void;
  /** Seats a hold attempt reported as unavailable. */
  markTaken(indexes: number[]): void;
};

/**
 * Seat state outside React, keyed by seat index. A delta touches only the seats it names, and only their listeners
 * run, so the map re-renders a handful of circles instead of thousands. Taken = held or sold by someone else: the
 * availability endpoint does not tell them apart and the buyer cannot pick either.
 */
export function createSeatStore(seatCount: number): SeatStore {
  const taken = new Uint8Array(seatCount);
  const mine = new Set<number>();
  const selection = new Set<number>();
  const listeners = new Map<number, Set<() => void>>();
  const allListeners = new Set<() => void>();
  let version = 0;
  let revision = 0;

  const inRange = (i: number) => Number.isInteger(i) && i >= 0 && i < seatCount;

  const notify = (changed: Iterable<number>) => {
    let any = false;
    for (const index of changed) {
      any = true;
      listeners.get(index)?.forEach((listener) => listener());
    }

    if (any) {
      revision += 1;
      allListeners.forEach((listener) => listener());
    }
  };

  const get = (index: number): SeatState => {
    if (mine.has(index)) return "mine";
    if (selection.has(index)) return "selected";
    return taken[index] ? "taken" : "free";
  };

  return {
    get,
    subscribe(index, listener) {
      let set = listeners.get(index);
      if (!set) listeners.set(index, (set = new Set()));
      set.add(listener);
      return () => set.delete(listener);
    },
    subscribeAll(listener) {
      allListeners.add(listener);
      return () => allListeners.delete(listener);
    },
    revision: () => revision,
    version: () => version,
    load(snapshot) {
      // A resync that left before deltas arrived must not roll them back. While a gap is open no delta is applied, so a
      // store ahead of the snapshot is in sync, and only a snapshot at least as new can teach it something.
      if (snapshot.version < version) return;
      const changed = new Set<number>([...mine, ...selection]);
      for (let i = 0; i < seatCount; i++) if (taken[i]) changed.add(i);
      taken.fill(0);
      mine.clear();
      for (const i of snapshot.unavailable.filter(inRange)) {
        taken[i] = 1;
        changed.add(i);
      }

      for (const i of snapshot.mine.filter(inRange)) {
        mine.add(i);
        changed.add(i);
      }

      for (const i of [...selection]) if (taken[i]) selection.delete(i);
      version = snapshot.version;
      notify(changed);
    },
    applyDelta(delta) {
      if (delta.v <= version) return { status: "stale", lost: [] };
      if (delta.from > version + 1) return { status: "gap", lost: [] };

      const changed: number[] = [];
      const lost: number[] = [];
      for (const i of [...delta.held, ...delta.sold].filter(inRange)) {
        taken[i] = 1;
        if (selection.delete(i)) lost.push(i);
        changed.push(i);
      }

      for (const i of delta.freed.filter(inRange)) {
        taken[i] = 0;
        mine.delete(i);
        changed.push(i);
      }

      version = delta.v;
      notify(changed);
      return { status: "applied", lost };
    },
    toggle(index) {
      if (!inRange(index) || taken[index] || mine.has(index)) return false;
      if (!selection.delete(index)) selection.add(index);
      notify([index]);
      return true;
    },
    selected: () => [...selection].sort((a, b) => a - b),
    clearSelection() {
      const changed = [...selection];
      selection.clear();
      notify(changed);
    },
    setMine(indexes) {
      const changed = new Set<number>(mine);
      mine.clear();
      for (const i of indexes.filter(inRange)) {
        mine.add(i);
        taken[i] = 1;
        selection.delete(i);
        changed.add(i);
      }

      notify(changed);
    },
    markTaken(indexes) {
      const changed = indexes.filter(inRange);
      for (const i of changed) {
        taken[i] = 1;
        selection.delete(i);
      }

      notify(changed);
    },
  };
}
