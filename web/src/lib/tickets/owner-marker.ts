/**
 * Who the offline wallet's cache belongs to: the id of the user signed in when it was last claimed (not a secret, an
 * id). Tiny and dependency-free on purpose: the auth code reads it on every session start to decide whether the
 * cleanup (`register-sw.ts`, loaded on demand) is needed at all, so no wallet code ships with the shared bundles.
 */
const OWNER_KEY = "tr.wallet.owner";

/** The offline wallet's IndexedDB database (`key-cache.ts`). */
export const OFFLINE_DATABASE = "ticketrush-tickets";

export function readOwnerMarker(): string | null {
  try {
    return globalThis.localStorage?.getItem(OWNER_KEY) ?? null;
  } catch {
    return null;
  }
}

export function writeOwnerMarker(userId: string): void {
  try {
    globalThis.localStorage?.setItem(OWNER_KEY, userId);
  } catch {
    // Blocked storage: without a marker, the offline wallet serves nothing without a session (the safe side).
  }
}

export function clearOwnerMarker(): void {
  try {
    globalThis.localStorage?.removeItem(OWNER_KEY);
  } catch {
    // Nothing to clear.
  }
}

/**
 * Last-resort wipe when the cleanup module cannot be loaded or the stores cannot be cleared: drops the whole database
 * (keys and ticket details) and the marker, with no import.
 */
export function deleteOfflineDatabase(): void {
  clearOwnerMarker();
  try {
    globalThis.indexedDB?.deleteDatabase(OFFLINE_DATABASE);
  } catch {
    // Storage is unavailable: there is nothing stored either.
  }
}
