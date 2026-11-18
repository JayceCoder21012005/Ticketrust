import { deleteOfflineDatabase, readOwnerMarker } from "./owner-marker";

/**
 * Wipes the offline wallet (cached keys, ticket details, the service worker and its caches) through `register-sw.ts`,
 * loaded on demand so the shared bundles (auth, session, providers) carry none of the wallet code. If that module
 * cannot be loaded, the database is deleted directly. `onlyIfStored` skips the import when this device has no owner
 * marker, i.e. nothing was ever stored offline here.
 */
export function forgetOfflineTicketsLater({ onlyIfStored = false } = {}): Promise<void> {
  if (onlyIfStored && readOwnerMarker() === null) return Promise.resolve();
  return import("./register-sw").then(
    (module) => module.forgetOfflineTickets(),
    () => deleteOfflineDatabase(),
  );
}
