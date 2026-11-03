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
