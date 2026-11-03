import { afterEach, describe, expect, it, vi } from "vitest";
import { getAccessToken, loginRedirectUrl, refreshAccessToken, setAccessToken } from "./session";

afterEach(() => setAccessToken(null));

describe("session", () => {
  it("keeps the access token in memory only", () => {
    setAccessToken("abc");

    expect(getAccessToken()).toBe("abc");
    expect(window.localStorage.length).toBe(0);
    expect(window.sessionStorage.length).toBe(0);
  });

  it("refresh posts with the CSRF header and stores the new token", async () => {
    const fetchMock = vi.fn().mockResolvedValue(new Response(JSON.stringify({ accessToken: "new" }), { status: 200 }));

    const token = await refreshAccessToken(fetchMock);

    const [url, init] = fetchMock.mock.calls[0];
    expect(url).toBe("/api/v1/auth/refresh");
    expect(init.method).toBe("POST");
    expect(new Headers(init.headers).get("X-Requested-With")).toBe("fetch");
    expect(token).toBe("new");
    expect(getAccessToken()).toBe("new");
  });

  it("refresh returns null and clears the token when the session is gone", async () => {
    setAccessToken("old");

    const token = await refreshAccessToken(vi.fn().mockResolvedValue(new Response("", { status: 401 })));

    expect(token).toBeNull();
    expect(getAccessToken()).toBeNull();
  });

  it("login redirect keeps the current page as a same-origin returnUrl", () => {
    expect(loginRedirectUrl("/events/abc?x=1")).toBe("/login?returnUrl=%2Fevents%2Fabc%3Fx%3D1");
  });
});
