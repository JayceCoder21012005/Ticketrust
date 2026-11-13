import { act, renderHook } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { ApiError, NetworkError } from "@/lib/errors";
import type { QrKey } from "@/lib/tickets/types";

const api = vi.hoisted(() => ({ getQrKey: vi.fn() }));
vi.mock("@/lib/tickets/api", async (importOriginal) => ({ ...(await importOriginal<typeof import("@/lib/tickets/api")>()), ...api }));

/** The real cache module over an in-memory store (jsdom has no IndexedDB). */
const cache = vi.hoisted(() => ({ store: null as null | ReturnType<typeof import("@/lib/tickets/fixtures/memory-key-store").memoryKeyStore> }));
vi.mock("@/lib/tickets/key-cache", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@/lib/tickets/key-cache")>();
  const { memoryKeyStore } = await import("@/lib/tickets/fixtures/memory-key-store");
  cache.store = memoryKeyStore();
  return { ...actual, ...actual.createKeyCache(cache.store) };
});

const { keyCache, ticketCache } = await import("@/lib/tickets/key-cache");
const { loadQrKey, useQrKey } = await import("./use-qr-key");

const ID = "0192f0e0-0002-7bbb-8ccc-0123456789ab";
const USER = "u1";
const SECRET = btoa(String.fromCharCode(...new Uint8Array(32).fill(42)));
const OLD_SECRET = btoa(String.fromCharCode(...new Uint8Array(32).fill(1)));
const T0 = 1_794_484_800_000 + 7_000;
const ENDS_AT = new Date(T0 + 3_600_000).toISOString();

const key = (extra: Partial<QrKey> = {}): QrKey => ({ ticketId: ID, secret: SECRET, stepSeconds: 15, format: "TR1", ...extra });
const notFound = () => new ApiError(404, { code: "ticket.not_found", status: 404 });
const notDisplayable = () => new ApiError(409, { code: "ticket.not_displayable", status: 409 });

beforeEach(async () => {
  vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout", "setInterval", "clearInterval", "Date"] });
  vi.setSystemTime(T0);
  api.getQrKey.mockReset().mockResolvedValue(key());
  await keyCache.clearAll();
  await keyCache.claim(USER);
});

afterEach(() => {
  vi.useRealTimers();
});

const flush = async () => {
  for (let i = 0; i < 4; i++) await act(() => vi.advanceTimersByTimeAsync(0));
};
const context = { endsAt: ENDS_AT, serverNow: () => Date.now(), userId: USER };
const signal = () => new AbortController().signal;

describe("loadQrKey", () => {
  it("Key_is_cached_after_a_successful_fetch", async () => {
    expect(await loadQrKey(ID, signal(), context)).toEqual({ status: "ready", secret: SECRET, source: "network" });
    await flush();

    expect(await keyCache.get(ID)).toEqual({ secret: SECRET, endsAt: ENDS_AT });
  });

  it("Network_failure_uses_the_cached_key", async () => {
    await keyCache.put(ID, { secret: OLD_SECRET, endsAt: ENDS_AT });
    api.getQrKey.mockRejectedValue(new NetworkError());

    expect(await loadQrKey(ID, signal(), context)).toEqual({ status: "ready", secret: OLD_SECRET, source: "cache" });
  });

  it("Claim_before_put_keeps_the_new_users_first_key", async () => {
    // A's key was stored here; B is signed in now and gets a fresh key: B's must survive the wipe of A's cache.
    await keyCache.clearAll();
    await keyCache.claim("user-a");
    await keyCache.put(ID, { secret: OLD_SECRET, endsAt: ENDS_AT });

    expect(await loadQrKey(ID, signal(), { ...context, userId: "user-b" })).toMatchObject({ status: "ready", source: "network" });
    await flush();

    expect(await keyCache.read(ID, "user-b")).toEqual({ secret: SECRET, endsAt: ENDS_AT });
  });

  it("a network failure never serves another user's cached key", async () => {
    await keyCache.put(ID, { secret: OLD_SECRET, endsAt: ENDS_AT });
    api.getQrKey.mockRejectedValue(new NetworkError());

    expect(await loadQrKey(ID, signal(), { ...context, userId: "someone-else" })).toEqual({ status: "error", failure: "network" });
    // Signed out and offline: only the last signed-in user's cache (the marker is now someone-else's).
    localStorage.setItem("tr.wallet.owner", "u1");
    expect(await loadQrKey(ID, signal(), { ...context, userId: null })).toEqual({ status: "error", failure: "network" });
  });

  it("a network failure without a cached key, or with one of an ended event, stays an error", async () => {
    api.getQrKey.mockRejectedValue(new NetworkError());
    expect(await loadQrKey(ID, signal(), context)).toEqual({ status: "error", failure: "network" });

    await keyCache.put(ID, { secret: OLD_SECRET, endsAt: new Date(T0 - 1).toISOString() });
    expect(await loadQrKey(ID, signal(), context)).toEqual({ status: "error", failure: "network" });
    // On the server clock: the device is behind, the server says the event is over.
    await keyCache.put(ID, { secret: OLD_SECRET, endsAt: new Date(T0 + 10_000).toISOString() });
    expect(await loadQrKey(ID, signal(), { ...context, serverNow: () => Date.now() + 20_000 })).toEqual({ status: "error", failure: "network" });
  });

  it("Not_found_or_not_displayable_does_not_use_the_cache_and_deletes_it", async () => {
    const detail = { id: ID, event: { endsAt: ENDS_AT } } as Parameters<typeof ticketCache.put>[0];
    for (const [error, code] of [
      [notFound(), "not_found"],
      [notDisplayable(), "not_displayable"],
    ] as const) {
      await keyCache.put(ID, { secret: OLD_SECRET, endsAt: ENDS_AT });
      await ticketCache.put(detail);
      api.getQrKey.mockRejectedValueOnce(error);

      expect(await loadQrKey(ID, signal(), context)).toEqual({ status: "refused", code });
      await flush();
      expect(await keyCache.get(ID)).toBeNull();
      expect(await ticketCache.get(ID)).toBeNull();
    }
  });

  it("an http error answer (429, 503) never falls back to the cache", async () => {
    await keyCache.put(ID, { secret: OLD_SECRET, endsAt: ENDS_AT });

    api.getQrKey.mockRejectedValueOnce(new ApiError(429, { code: "ticket.qr_key_rate_limited", status: 429, retryAfterSeconds: 5 }));
    expect(await loadQrKey(ID, signal(), context)).toEqual({ status: "rateLimited", retryAt: T0 + 5_000 });

    api.getQrKey.mockRejectedValueOnce(new ApiError(503, { code: "system.busy", status: 503 }));
    expect(await loadQrKey(ID, signal(), context)).toEqual({ status: "error", failure: "busy" });
    // Still there for a later network failure.
    expect(await keyCache.get(ID)).not.toBeNull();
  });

  it("rejects a key of another format or step, and does not cache it", async () => {
    api.getQrKey.mockResolvedValueOnce(key({ format: "TR2" }));
    expect(await loadQrKey(ID, signal(), context)).toEqual({ status: "error", failure: "other" });
    api.getQrKey.mockResolvedValueOnce(key({ stepSeconds: 30 }));
    expect(await loadQrKey(ID, signal(), context)).toEqual({ status: "error", failure: "other" });
    await flush();

    expect(await keyCache.get(ID)).toBeNull();
  });

  it("a broken cache never breaks the online path", async () => {
    const spy = vi.spyOn(keyCache, "put").mockRejectedValue(new Error("quota"));
    expect(await loadQrKey(ID, signal(), context)).toEqual({ status: "ready", secret: SECRET, source: "network" });
    spy.mockRestore();
  });
});

describe("useQrKey", () => {
  it("marks a cached key and keeps asking the network in the background, then switches to the fresh one", async () => {
    await keyCache.put(ID, { secret: OLD_SECRET, endsAt: ENDS_AT });
    api.getQrKey.mockRejectedValue(new NetworkError());
    const { result, unmount } = renderHook(() => useQrKey(ID, true, ENDS_AT, 0, USER));
    await flush();

    expect(result.current).toMatchObject({ status: "ready", secret: OLD_SECRET, source: "cache" });
    expect(api.getQrKey).toHaveBeenCalledTimes(1);

    // Backoff: 5 s, then 10 s, then 20 s...; the cached code stays on screen meanwhile.
    await act(() => vi.advanceTimersByTimeAsync(5_000));
    await flush();
    expect(api.getQrKey).toHaveBeenCalledTimes(2);
    expect(result.current).toMatchObject({ status: "ready", source: "cache" });
    await act(() => vi.advanceTimersByTimeAsync(9_999));
    expect(api.getQrKey).toHaveBeenCalledTimes(2);

    api.getQrKey.mockResolvedValue(key());
    await act(() => vi.advanceTimersByTimeAsync(1));
    await flush();
    expect(api.getQrKey).toHaveBeenCalledTimes(3);
    expect(result.current).toMatchObject({ status: "ready", secret: SECRET, source: "network" });
    expect(await keyCache.get(ID)).toEqual({ secret: SECRET, endsAt: ENDS_AT });

    unmount();
  });

  it("the background retry backs off to a cap and stops on unmount", async () => {
    await keyCache.put(ID, { secret: OLD_SECRET, endsAt: ENDS_AT });
    api.getQrKey.mockRejectedValue(new NetworkError());
    const { unmount } = renderHook(() => useQrKey(ID, true, ENDS_AT, 0, USER));
    await flush();

    await act(() => vi.advanceTimersByTimeAsync(10 * 60_000));
    await flush();
    const calls = api.getQrKey.mock.calls.length;
    // 5 + 10 + 20 + 40 + 60 + 60 ... within ten minutes: bounded, never a tight loop.
    expect(calls).toBeGreaterThan(5);
    expect(calls).toBeLessThan(15);

    unmount();
    await act(() => vi.advanceTimersByTimeAsync(10 * 60_000));
    expect(api.getQrKey).toHaveBeenCalledTimes(calls);
  });

  it("Old_secret_is_cleared_when_the_ticket_stops_being_displayable", async () => {
    const { result, rerender } = renderHook(({ enabled }) => useQrKey(ID, enabled, ENDS_AT, 0, USER), { initialProps: { enabled: true } });
    await flush();
    expect(result.current).toMatchObject({ status: "ready", secret: SECRET });

    rerender({ enabled: false });
    expect(result.current.status).toBe("idle");

    // Displayable again: nothing of the old answer shows before the new one is in.
    api.getQrKey.mockReturnValue(new Promise(() => undefined));
    rerender({ enabled: true });
    expect(result.current.status).toBe("loading");
    expect(JSON.stringify(result.current)).not.toContain(SECRET);
    await flush();
    expect(result.current.status).toBe("loading");
  });
});
