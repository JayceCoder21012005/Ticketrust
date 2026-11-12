import { useSyncExternalStore } from "react";
import { getSession, subscribe } from "./session";

/** The in-memory session, so user data is only asked for once there is a token (and not for a stale session hint). */
export function useSessionPresent(): boolean {
  return useSyncExternalStore(subscribe, () => getSession() !== null, () => false);
}
