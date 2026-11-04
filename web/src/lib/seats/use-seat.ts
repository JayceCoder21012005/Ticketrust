import { useCallback, useSyncExternalStore } from "react";
import type { SeatStore } from "./seat-store";
import type { SeatState } from "./types";

/** One seat's state; re-renders only when that seat changes. */
export function useSeat(store: SeatStore, index: number): SeatState {
  const subscribe = useCallback((listener: () => void) => store.subscribe(index, listener), [store, index]);
  const get = () => store.get(index);
  return useSyncExternalStore(subscribe, get, get);
}

/** A number that changes whenever any seat changes: for the summary panel, never for seats. */
export function useStoreRevision(store: SeatStore): number {
  const subscribe = useCallback((listener: () => void) => store.subscribeAll(listener), [store]);
  return useSyncExternalStore(subscribe, store.revision, store.revision);
}
