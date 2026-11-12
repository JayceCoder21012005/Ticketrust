import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { ApiError } from "@/lib/errors";
import {
  clearSession,
  getAccessToken,
  getSession,
  login,
  loginRedirectUrl,
  logout,
  refreshAccessToken,
  subscribe,
  type Session,
} from "./session";

const user = {
  id: "0193",
  email: "an@example.com",
  fullName: "Nguyễn An",
  phoneNumber: null,
  avatarUrl: null,
  emailConfirmed: true,
  roles: ["Customer"],
};
const session = (accessToken: string): Session => ({ accessToken, expiresAt: "2026-11-03T10:10:00Z", user });
const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { "content-type": "application/json" } });

beforeEach(() => {
  window.localStorage.clear();
  window.sessionStorage.clear();
});

afterEach(() => {
  clearSession();
  vi.restoreAllMocks();
});

describe("login", () => {
  it("posts the credentials and keeps the session in memory only, never in web storage", async () => {
    const setItem = vi.spyOn(Storage.prototype, "setItem");
    const fetchMock = vi.fn().mockResolvedValue(json(session("jwt-1")));

    await login({ email: "an@example.com", password: "matkhau123" }, fetchMock);

    const [url, init] = fetchMock.mock.calls[0];
    expect(url).toBe("/api/v1/auth/login");
    expect(JSON.parse(init.body)).toEqual({ email: "an@example.com", password: "matkhau123" });
    expect(getSession()).toEqual(session("jwt-1"));
    expect(getAccessToken()).toBe("jwt-1");
    expect(setItem).not.toHaveBeenCalled();
    expect(window.localStorage.length + window.sessionStorage.length).toBe(0);
  });

  it("throws an ApiError carrying the problem code and leaves the session empty", async () => {
    const fetchMock = vi.fn().mockResolvedValue(json({ code: "auth.invalid_credentials", status: 401 }, 401));

    const error = await login({ email: "an@example.com", password: "x" }, fetchMock).catch((e: unknown) => e);

    expect(error).toBeInstanceOf(ApiError);
    expect((error as ApiError).code).toBe("auth.invalid_credentials");
    expect(getSession()).toBeNull();
  });

  it("notifies subscribers when the session changes", async () => {
    const listener = vi.fn();
    const unsubscribe = subscribe(listener);

    await login({ email: "an@example.com", password: "matkhau123" }, vi.fn().mockResolvedValue(json(session("jwt-1"))));
    unsubscribe();
    clearSession();

    expect(listener).toHaveBeenCalledTimes(1);
  });
});

describe("refreshAccessToken", () => {
  it("posts with the CSRF header and stores the new session", async () => {
    const fetchMock = vi.fn().mockResolvedValue(json(session("new")));

    const token = await refreshAccessToken(fetchMock);

    const [url, init] = fetchMock.mock.calls[0];
    expect(url).toBe("/api/v1/auth/refresh");
    expect(init.method).toBe("POST");
    expect(new Headers(init.headers).get("X-Requested-With")).toBe("fetch");
    expect(token).toBe("new");
    expect(getSession()?.user.email).toBe("an@example.com");
  });

  it("shares one in-flight request between parallel callers (the rotated token is never sent twice)", async () => {
    let resolve!: (response: Response) => void;
    const fetchMock = vi.fn().mockReturnValue(new Promise<Response>((r) => (resolve = r)));

    const calls = [refreshAccessToken(fetchMock), refreshAccessToken(fetchMock), refreshAccessToken(fetchMock)];
    resolve(json(session("shared")));

    expect(await Promise.all(calls)).toEqual(["shared", "shared", "shared"]);
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it("returns null and clears the session when the refresh is rejected", async () => {
    await login({ email: "an@example.com", password: "matkhau123" }, vi.fn().mockResolvedValue(json(session("old"))));

    const token = await refreshAccessToken(vi.fn().mockResolvedValue(json({ code: "auth.refresh_invalid" }, 401)));

    expect(token).toBeNull();
    expect(getSession()).toBeNull();
  });

  it("returns null when the network fails", async () => {
    expect(await refreshAccessToken(vi.fn().mockRejectedValue(new TypeError("offline")))).toBeNull();
  });
});

/** A Web Locks stand-in with a real queue: a request runs only after the previous holder of the same name has finished. */
function lockManager() {
  const tails = new Map<string, Promise<unknown>>();
  return {
    request: vi.fn(<T,>(name: string, callback: () => Promise<T>): Promise<T> => {
      const previous = tails.get(name) ?? Promise.resolve();
      const run = previous.then(callback, callback);
      tails.set(name, run.catch(() => undefined));
      return run;
    }),
  };
}

/**
 * The refresh endpoint with rotation and reuse detection (spec 03): the cookie jar is shared by every tab of the origin, each
 * success rotates it, and presenting an already-rotated token is refused (in production that also revokes the whole family).
 */
function rotatingServer() {
  let current = 1;
  let inFlight = 0;
  const presented: string[] = [];
  let maxInFlight = 0;
  const fetchImpl = vi.fn(async () => {
    const sent = `rt-${current}`; // what the browser attaches when the request starts
    presented.push(sent);
    inFlight++;
    maxInFlight = Math.max(maxInFlight, inFlight);
    await new Promise((resolve) => setTimeout(resolve, 5));
    inFlight--;
    if (sent !== `rt-${current}`) {
      return json({ code: "auth.refresh_reused", status: 401 }, 401);
    }

    current++;
    return json(session(`jwt-${current}`));
  });
  return { fetchImpl, presented, maxInFlight: () => maxInFlight };
}

/** A second tab: the same module, loaded again, so it has its own memory and its own single-flight promise. */
async function openTab() {
  vi.resetModules();
  return import("./session");
}

describe("refreshAccessToken across tabs (Web Locks)", () => {
  afterEach(() => {
    Reflect.deleteProperty(navigator, "locks");
  });

  it("runs two tabs' refreshes one after the other, so the second sends the cookie the first already rotated", async () => {
    const locks = lockManager();
    Object.defineProperty(navigator, "locks", { value: locks, configurable: true });
    const server = rotatingServer();
    const [tabA, tabB] = [await openTab(), await openTab()];

    const tokens = await Promise.all([tabA.refreshAccessToken(server.fetchImpl), tabB.refreshAccessToken(server.fetchImpl)]);

    expect(tokens).toEqual(["jwt-2", "jwt-3"]);
    expect(server.presented).toEqual(["rt-1", "rt-2"]);
    expect(server.maxInFlight()).toBe(1);
    expect(locks.request).toHaveBeenCalledTimes(2);
    expect(locks.request.mock.calls.map(([name]) => name)).toEqual(["tr-refresh", "tr-refresh"]);
    expect(tabA.getAccessToken()).toBe("jwt-2");
    expect(tabB.getAccessToken()).toBe("jwt-3");
  });

  it("still shares one request between the callers of one tab while holding the lock", async () => {
    const locks = lockManager();
    Object.defineProperty(navigator, "locks", { value: locks, configurable: true });
    const server = rotatingServer();
    const tab = await openTab();

    const tokens = await Promise.all([tab.refreshAccessToken(server.fetchImpl), tab.refreshAccessToken(server.fetchImpl)]);

    expect(tokens).toEqual(["jwt-2", "jwt-2"]);
    expect(server.fetchImpl).toHaveBeenCalledTimes(1);
    expect(locks.request).toHaveBeenCalledTimes(1);
  });

  it("falls back to the per-tab single flight where the browser has no Web Locks", async () => {
    expect("locks" in navigator).toBe(false);
    const server = rotatingServer();
    const tab = await openTab();

    expect(await tab.refreshAccessToken(server.fetchImpl)).toBe("jwt-2");
    expect(server.fetchImpl).toHaveBeenCalledTimes(1);
  });

  it("releases the lock when a refresh fails, so the next one is not stuck behind it", async () => {
    const locks = lockManager();
    Object.defineProperty(navigator, "locks", { value: locks, configurable: true });
    const tab = await openTab();

    expect(await tab.refreshAccessToken(vi.fn().mockRejectedValue(new TypeError("offline")))).toBeNull();
    expect(await tab.refreshAccessToken(vi.fn().mockResolvedValue(json(session("after"))))).toBe("after");
  });
});

describe("logout", () => {
  it("posts with the CSRF header and clears the session even if the request fails", async () => {
    await login({ email: "an@example.com", password: "matkhau123" }, vi.fn().mockResolvedValue(json(session("jwt"))));
    const fetchMock = vi.fn().mockRejectedValue(new TypeError("offline"));

    await logout(fetchMock);

    const [url, init] = fetchMock.mock.calls[0];
    expect(url).toBe("/api/v1/auth/logout");
    expect(new Headers(init.headers).get("X-Requested-With")).toBe("fetch");
    expect(getSession()).toBeNull();
  });
});

describe("loginRedirectUrl", () => {
  it("encodes the current path as returnUrl", () => {
    expect(loginRedirectUrl("/account?tab=1")).toBe("/login?returnUrl=%2Faccount%3Ftab%3D1");
  });
});
