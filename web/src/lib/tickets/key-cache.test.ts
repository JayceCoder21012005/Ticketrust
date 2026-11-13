import { afterEach, describe, expect, it, vi } from "vitest";
import { brokenKeyStore, memoryKeyStore } from "./fixtures/memory-key-store";
import { createKeyCache, isSupported } from "./key-cache";
import type { TicketDetail } from "./types";

const SECRET = btoa(String.fromCharCode(...new Uint8Array(32).fill(9)));
const NOW = Date.UTC(2026, 10, 12, 12, 0, 0);
const iso = (ms: number) => new Date(ms).toISOString();

function detail(id: string, endsAt: string, status: TicketDetail["event"]["status"] = "Published"): TicketDetail {
  return {
    id,
    orderId: "o1",
    tierName: "Hạng VIP",
    seat: { section: "A", row: "1", number: "1" },
    status: "Valid",
    holderName: "Nguyễn An",
    orderTicketIds: [id],
    event: { eventId: "e1", slug: "e", title: "Đêm nhạc", coverImageUrl: null, startsAt: iso(NOW), endsAt, venueName: "V", city: "C", status },
  };
}

afterEach(() => {
  vi.unstubAllGlobals();
  localStorage.clear();
});

describe("keyCache", () => {
  it("stores, reads and deletes a key per ticket", async () => {
    const { keyCache } = createKeyCache(memoryKeyStore());

    await keyCache.put("t1", { secret: SECRET, endsAt: iso(NOW + 60_000) });

    expect(await keyCache.get("t1")).toEqual({ secret: SECRET, endsAt: iso(NOW + 60_000) });
    expect(await keyCache.get("t2")).toBeNull();
    await keyCache.delete("t1");
    expect(await keyCache.get("t1")).toBeNull();
  });

  it("ignores a stored value that is not a key", async () => {
    const store = memoryKeyStore();
    const { keyCache } = createKeyCache(store);
    await store.put("keys", "t1", { secret: 42 });
    await store.put("keys", "t2", "nonsense");

    expect(await keyCache.get("t1")).toBeNull();
    expect(await keyCache.get("t2")).toBeNull();
  });

  it("Keys_of_ended_events_are_purged", async () => {
    const store = memoryKeyStore();
    const { keyCache, ticketCache } = createKeyCache(store);
    await keyCache.put("live", { secret: SECRET, endsAt: iso(NOW + 1) });
    await keyCache.put("ended", { secret: SECRET, endsAt: iso(NOW) });
    await store.put("keys", "garbage", { secret: SECRET, endsAt: "not a date" });
    await ticketCache.put(detail("live", iso(NOW + 1)));
    await ticketCache.put(detail("ended", iso(NOW - 1)));
    await ticketCache.put(detail("cancelled", iso(NOW + 60_000), "Cancelled"));

    await keyCache.purgeEnded(NOW);

    expect([...store.dump().keys.keys()]).toEqual(["live"]);
    expect([...store.dump().tickets.keys()]).toEqual(["live"]);
  });

  it("Logout_clears_all_keys", async () => {
    const store = memoryKeyStore();
    const { keyCache, ticketCache } = createKeyCache(store);
    await keyCache.put("t1", { secret: SECRET, endsAt: iso(NOW + 60_000) });
    await ticketCache.put(detail("t1", iso(NOW + 60_000)));
    await keyCache.claim("u1");

    await keyCache.clearAll();

    expect(store.dump().keys.size).toBe(0);
    expect(store.dump().tickets.size).toBe(0);
    expect(store.dump().meta.size).toBe(0);
  });

  it("drops everything cached for another user when a new one claims the cache", async () => {
    const store = memoryKeyStore();
    const { keyCache, ticketCache } = createKeyCache(store);
    await keyCache.claim("a");
    await keyCache.put("t1", { secret: SECRET, endsAt: iso(NOW + 60_000) });
    await ticketCache.put(detail("t1", iso(NOW + 60_000)));

    await keyCache.claim("a");
    expect(await keyCache.get("t1")).not.toBeNull();

    await keyCache.claim("b");
    expect(await keyCache.get("t1")).toBeNull();
    expect(await ticketCache.get("t1")).toBeNull();
  });

  it("Strict_claim_wipes_unowned_leftovers", async () => {
    const store = memoryKeyStore();
    const { keyCache, ticketCache } = createKeyCache(store);
    // Written before any owner was known (or the owner mark was lost): never adopted by whoever signs in next.
    await store.put("keys", "t1", { secret: SECRET, endsAt: iso(NOW + 60_000) });
    await ticketCache.put(detail("t1", iso(NOW + 60_000)));

    await keyCache.claim("b");

    expect(await keyCache.get("t1")).toBeNull();
    expect(await ticketCache.get("t1")).toBeNull();
    expect(store.dump().meta.get("owner")).toBe("b");
  });

  it("claim keeps a non-secret owner marker in localStorage; clearAll removes it", async () => {
    const { keyCache } = createKeyCache(memoryKeyStore());
    await keyCache.claim("a");
    expect(localStorage.getItem("tr.wallet.owner")).toBe("a");

    expect(await keyCache.clearAll()).toBe(true);
    expect(localStorage.getItem("tr.wallet.owner")).toBeNull();
    expect(await createKeyCache(brokenKeyStore()).keyCache.clearAll()).toBe(false);
  });

  describe("reads for a user (owner check at read time)", () => {
    async function ownedBy(owner: string) {
      const store = memoryKeyStore();
      const caches = createKeyCache(store);
      await caches.keyCache.claim(owner);
      await caches.keyCache.put("t1", { secret: SECRET, endsAt: iso(NOW + 60_000) });
      await caches.ticketCache.put(detail("t1", iso(NOW + 60_000)));
      return { store, ...caches };
    }

    it("serves the signed-in owner, and wipes for another signed-in user", async () => {
      const { keyCache, ticketCache } = await ownedBy("a");
      expect(await keyCache.read("t1", "a")).not.toBeNull();
      expect(await ticketCache.read("t1", "a")).not.toBeNull();

      expect(await ticketCache.read("t1", "b")).toBeNull();
      expect(await keyCache.read("t1", "b")).toBeNull();
      expect(await keyCache.get("t1")).toBeNull();
    });

    it("Owner_mismatch_offline_serves_nothing: without a session, only the last signed-in user's cache is served", async () => {
      const { keyCache, ticketCache } = await ownedBy("a");
      expect(await keyCache.read("t1", null)).not.toBeNull();

      localStorage.setItem("tr.wallet.owner", "b");
      expect(await keyCache.read("t1", null)).toBeNull();
      expect(await ticketCache.read("t1", null)).toBeNull();
      localStorage.removeItem("tr.wallet.owner");
      expect(await keyCache.read("t1", null)).toBeNull();
    });
  });

  it("Cache_failures_never_break_the_online_path", async () => {
    const { keyCache, ticketCache } = createKeyCache(brokenKeyStore());

    await expect(keyCache.put("t1", { secret: SECRET, endsAt: iso(NOW + 60_000) })).resolves.toBeUndefined();
    await expect(keyCache.get("t1")).resolves.toBeNull();
    await expect(keyCache.delete("t1")).resolves.toBeUndefined();
    await expect(keyCache.clearAll()).resolves.toBe(false);
    await expect(keyCache.purgeEnded(NOW)).resolves.toBeUndefined();
    await expect(keyCache.claim("u1")).resolves.toBeUndefined();
    await expect(ticketCache.put(detail("t1", iso(NOW + 60_000)))).resolves.toBeUndefined();
    await expect(ticketCache.get("t1")).resolves.toBeNull();
    await expect(ticketCache.delete("t1")).resolves.toBeUndefined();
  });

  it("Cache_failures_never_log_anything", async () => {
    const spies = (["log", "info", "warn", "error", "debug"] as const).map((level) => vi.spyOn(console, level));
    const { keyCache } = createKeyCache(brokenKeyStore(new Error(`quota ${SECRET}`)));

    await keyCache.put("t1", { secret: SECRET, endsAt: iso(NOW + 60_000) });
    await keyCache.get("t1");

    for (const spy of spies) expect(spy).not.toHaveBeenCalled();
  });

  it("the ticket store keeps the detail by id and drops a mismatched one", async () => {
    const store = memoryKeyStore();
    const { ticketCache } = createKeyCache(store);
    await ticketCache.put(detail("t1", iso(NOW + 60_000)));
    await store.put("tickets", "t2", detail("t1", iso(NOW + 60_000)));

    expect((await ticketCache.get("t1"))?.holderName).toBe("Nguyễn An");
    expect(await ticketCache.get("t2")).toBeNull();
  });
});

describe("isSupported", () => {
  it("is false without IndexedDB (jsdom) and when touching it throws (a sandboxed frame)", () => {
    vi.stubGlobal("indexedDB", undefined);
    expect(isSupported()).toBe(false);

    vi.stubGlobal("indexedDB", {});
    expect(isSupported()).toBe(true);
  });

  it("the default cache resolves to not cached where IndexedDB is missing", async () => {
    vi.stubGlobal("indexedDB", undefined);
    const { keyCache } = await import("./key-cache");

    await expect(keyCache.put("t1", { secret: SECRET, endsAt: iso(NOW + 60_000) })).resolves.toBeUndefined();
    await expect(keyCache.get("t1")).resolves.toBeNull();
  });
});
