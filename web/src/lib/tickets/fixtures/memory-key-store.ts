import type { KeyStore, StoreName } from "../key-cache";

/** An in-memory `KeyStore` for tests (jsdom has no IndexedDB; the repo adds no fake-indexeddb). */
export function memoryKeyStore(): KeyStore & { dump: () => Record<StoreName, Map<string, unknown>> } {
  const stores: Record<StoreName, Map<string, unknown>> = { keys: new Map(), tickets: new Map(), meta: new Map() };
  return {
    get: async (store, id) => structuredClone(stores[store].get(id)),
    put: async (store, id, value) => {
      stores[store].set(id, structuredClone(value));
    },
    delete: async (store, id) => {
      stores[store].delete(id);
    },
    clear: async (store) => {
      stores[store].clear();
    },
    entries: async (store) => [...stores[store].entries()].map(([id, value]) => [id, structuredClone(value)] as [string, unknown]),
    dump: () => stores,
  };
}

/** A store whose every call fails the way a private window or a blocked/full storage does. */
export function brokenKeyStore(error: unknown = new DOMException("The connection is closing.", "InvalidStateError")): KeyStore {
  const fail = async () => {
    throw error;
  };
  return { get: fail, put: fail, delete: fail, clear: fail, entries: fail };
}
