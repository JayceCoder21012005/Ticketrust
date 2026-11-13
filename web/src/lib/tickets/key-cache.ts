import type { TicketDetail } from "./types";

/**
 * The offline wallet's storage (spec 10): the `qr-key` secret of each ticket the buyer opened, and the ticket detail
 * (seat, holder) the wallet shows next to it, so a ticket opened once keeps showing a valid rotating QR at the venue
 * without signal. Raw IndexedDB behind a tiny `KeyStore` seam, no dependency.
 *
 * The secret is a credential and the detail holds personal data: both stay in this database only (never logged, never
 * copied into the query cache or the service worker), are purged once the event has ended and are wiped on logout and
 * when another user signs in. Storage can fail anywhere (private windows, blocked site data, a full quota, a connection
 * that is closing): every call here swallows that and answers "not cached", so the online path never breaks.
 */

export type StoreName = "keys" | "tickets" | "meta";

/** One object store per name, keyed by id (out-of-line keys). Implementations may throw; the caches below never do. */
export interface KeyStore {
  get(store: StoreName, id: string): Promise<unknown>;
  put(store: StoreName, id: string, value: unknown): Promise<void>;
  delete(store: StoreName, id: string): Promise<void>;
  clear(store: StoreName): Promise<void>;
  entries(store: StoreName): Promise<[string, unknown][]>;
}

/** A ticket's `qr-key` secret (standard base64, as received) and when its event ends (ISO 8601). */
export type CachedKey = { secret: string; endsAt: string };

const DB_NAME = "ticketrush-tickets";
const DB_VERSION = 1;
const STORES: readonly StoreName[] = ["keys", "tickets", "meta"];
const OWNER = "owner";

/** False where IndexedDB is missing (old browsers, jsdom) or touching it throws (some sandboxed frames). */
export function isSupported(): boolean {
  try {
    return typeof indexedDB !== "undefined" && indexedDB !== null;
  } catch {
    return false;
  }
}

const settle = <T>(request: IDBRequest<T>) =>
  new Promise<T>((resolve, reject) => {
    request.onsuccess = () => resolve(request.result);
    request.onerror = () => reject(request.error);
  });

/** The real store: one database, opened lazily and reopened after it was closed (another tab upgrading, the browser). */
export function indexedDbStore(): KeyStore {
  let opening: Promise<IDBDatabase> | null = null;

  const open = () => {
    if (!isSupported()) return Promise.reject(new DOMException("IndexedDB is not available.", "NotSupportedError"));
    opening ??= new Promise<IDBDatabase>((resolve, reject) => {
      const request = indexedDB.open(DB_NAME, DB_VERSION);
      request.onupgradeneeded = () => {
        for (const name of STORES) {
          if (!request.result.objectStoreNames.contains(name)) request.result.createObjectStore(name);
        }
      };
      request.onsuccess = () => {
        const db = request.result;
        db.onversionchange = () => {
          db.close();
          opening = null;
        };
        db.onclose = () => {
          opening = null;
        };
        resolve(db);
      };
      request.onerror = () => reject(request.error);
      request.onblocked = () => reject(new DOMException("IndexedDB open is blocked.", "InvalidStateError"));
    }).catch((error: unknown) => {
      opening = null;
      throw error;
    });
    return opening;
  };

  async function run<T>(name: StoreName, mode: IDBTransactionMode, work: (store: IDBObjectStore) => Promise<T>): Promise<T> {
    const db = await open();
    let transaction: IDBTransaction;
    try {
      transaction = db.transaction(name, mode);
    } catch (error) {
      // A connection that is closing throws InvalidStateError: open a fresh one next time.
      opening = null;
      throw error;
    }
    const done = new Promise<void>((resolve, reject) => {
      transaction.oncomplete = () => resolve();
      transaction.onerror = () => reject(transaction.error);
      transaction.onabort = () => reject(transaction.error ?? new DOMException("The transaction was aborted.", "AbortError"));
    });
    const [result] = await Promise.all([work(transaction.objectStore(name)), done]);
    return result;
  }

  return {
    get: (name, id) => run(name, "readonly", (store) => settle(store.get(id))),
    put: (name, id, value) => run(name, "readwrite", async (store) => void (await settle(store.put(value, id)))),
    delete: (name, id) => run(name, "readwrite", async (store) => void (await settle(store.delete(id)))),
    clear: (name) => run(name, "readwrite", async (store) => void (await settle(store.clear()))),
    entries: (name) =>
      run(name, "readonly", async (store) => {
        const [keys, values] = await Promise.all([settle(store.getAllKeys()), settle(store.getAll())]);
        return keys.map((key, i) => [String(key), values[i]] as [string, unknown]);
      }),
  };
}

/** Runs a storage call; any failure is "not cached". Nothing is logged: the error could carry the stored value. */
async function quietly<T>(work: () => Promise<T>, fallback: T): Promise<T> {
  try {
    return await work();
  } catch {
    return fallback;
  }
}

const isRecord = (value: unknown): value is Record<string, unknown> => typeof value === "object" && value !== null;

function asKey(value: unknown): CachedKey | null {
  if (!isRecord(value) || typeof value.secret !== "string" || typeof value.endsAt !== "string") return null;
  return { secret: value.secret, endsAt: value.endsAt };
}

function asTicket(value: unknown, id: string): TicketDetail | null {
  if (!isRecord(value) || value.id !== id || !isRecord(value.event) || typeof value.event.endsAt !== "string") return null;
  return value as TicketDetail;
}

/** Still worth keeping at `nowMs`: the event has a parseable end in the future. */
const running = (endsAt: string, nowMs: number) => {
  const end = Date.parse(endsAt);
  return Number.isFinite(end) && end > nowMs;
};

/** The two caches over one store. `keyCache.clearAll()` and `purgeEnded()` cover both. */
export function createKeyCache(store: KeyStore) {
  /**
   * Wiping (logout, another user claiming the cache) runs before any later read or write starts, so a key written
   * right after a user change is never wiped by it, and nothing of the previous user is read while it runs.
   */
  let gate: Promise<unknown> = Promise.resolve();
  const afterWipes = <T>(work: () => Promise<T>) => gate.then(work);
  const wipe = <T>(work: () => Promise<T>) => {
    const next = gate.then(work);
    gate = next.catch(() => undefined);
    return next;
  };
  const clearStores = async () => {
    await Promise.allSettled(STORES.map((name) => store.clear(name)));
  };

  const keyCache = {
    /** Fire-and-forget after an online `qr-key` answer. */
    put: (ticketId: string, key: CachedKey) =>
      quietly(() => afterWipes(() => store.put("keys", ticketId, { secret: key.secret, endsAt: key.endsAt })), undefined),
    get: (ticketId: string) => quietly(async () => asKey(await afterWipes(() => store.get("keys", ticketId))), null),
    delete: (ticketId: string) => quietly(() => afterWipes(() => store.delete("keys", ticketId)), undefined),
    /** Logout and user change: every key, every ticket detail and the owner mark. */
    clearAll: () => quietly(() => wipe(clearStores), undefined),
    /** Drops what belongs to an event that has ended (or was cancelled), and anything unreadable. */
    purgeEnded: (nowMs: number) =>
      quietly(async () => {
        await gate;
        const [keys, tickets] = await Promise.all([store.entries("keys"), store.entries("tickets")]);
        const staleKeys = keys.filter(([, value]) => {
          const key = asKey(value);
          return !key || !running(key.endsAt, nowMs);
        });
        const staleTickets = tickets.filter(([id, value]) => {
          const ticket = asTicket(value, id);
          return !ticket || ticket.event.status === "Ended" || ticket.event.status === "Cancelled" || !running(ticket.event.endsAt, nowMs);
        });
        await Promise.allSettled([
          ...staleKeys.map(([id]) => store.delete("keys", id)),
          ...staleTickets.map(([id]) => store.delete("tickets", id)),
        ]);
      }, undefined),
    /**
     * Marks the cache as `userId`'s; what another user left behind (their session ended without a logout here) is wiped
     * first. Called by the wallet screens once the session is known.
     */
    claim: (userId: string) =>
      quietly(
        () =>
          wipe(async () => {
            const owner = await store.get("meta", OWNER);
            if (owner === userId) return;
            if (owner !== undefined) await clearStores();
            await store.put("meta", OWNER, userId);
          }),
        undefined,
      ),
  };

  const ticketCache = {
    /** The ticket detail as the api sent it (it never holds the secret), for the seat info offline. */
    put: (ticket: TicketDetail) => quietly(() => afterWipes(() => store.put("tickets", ticket.id, ticket)), undefined),
    get: (ticketId: string) => quietly(async () => asTicket(await afterWipes(() => store.get("tickets", ticketId)), ticketId), null),
    delete: (ticketId: string) => quietly(() => afterWipes(() => store.delete("tickets", ticketId)), undefined),
  };

  return { keyCache, ticketCache };
}

const defaults = createKeyCache(indexedDbStore());

/** The wallet's cache of `qr-key` secrets (see the module comment). */
export const keyCache = defaults.keyCache;

/** The wallet's cache of ticket details, purged and cleared together with the keys. */
export const ticketCache = defaults.ticketCache;
