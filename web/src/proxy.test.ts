import { NextRequest } from "next/server";
import { afterEach, describe, expect, it, vi } from "vitest";
import { proxy, requiresLogin } from "./proxy";

const request = (path: string, cookie?: string) =>
  new NextRequest(new URL(path, "http://localhost:3000"), { headers: cookie ? { cookie } : {} });

describe("requiresLogin", () => {
  it.each(["/account", "/account/tickets", "/my-tickets", "/checkout/123", "/queue/abc", "/events/ha-anh-tuan/queue", "/events/ha-anh-tuan/seats", "/events/ha-anh-tuan/checkout", "/organizer", "/organizer/events", "/admin", "/gate/scan"])(
    "guards %s",
    (path) => expect(requiresLogin(path)).toBe(true),
  );

  it.each(["/", "/events", "/events/abc", "/events/abc/queue-info", "/events/abc/seats-info", "/events/abc/checkout-info", "/login", "/register", "/design", "/administrator-info", "/gateway"])(
    "leaves %s public",
    (path) => expect(requiresLogin(path)).toBe(false),
  );
});

describe("proxy", () => {
  it("redirects a guarded page to login with the path and query as returnUrl when the session hint is missing", () => {
    const response = proxy(request("/account?tab=profile"));

    expect(response.status).toBe(307);
    const location = new URL(response.headers.get("location")!);
    expect(location.pathname).toBe("/login");
    expect(location.searchParams.get("returnUrl")).toBe("/account?tab=profile");
  });

  it("lets the request through when the hint cookie is present (the api stays the real guard)", () => {
    const response = proxy(request("/account", "tr_session=1"));

    expect(response.headers.get("location")).toBeNull();
    expect(response.headers.get("x-middleware-next")).toBe("1");
  });

  it("never redirects public pages", () => {
    expect(proxy(request("/events")).headers.get("location")).toBeNull();
  });
});

describe("proxy: mock gateway", () => {
  afterEach(() => vi.unstubAllEnvs());

  it("answers 404 for the mock gateway when it is not enabled", () => {
    vi.stubEnv("NODE_ENV", "production");
    vi.stubEnv("ENABLE_MOCK_GATEWAY", "");

    expect(proxy(request("/mock-gateway/s1")).status).toBe(404);
  });

  it("lets the mock gateway through when it is enabled, without asking for a login", () => {
    vi.stubEnv("NODE_ENV", "production");
    vi.stubEnv("ENABLE_MOCK_GATEWAY", "1");

    const response = proxy(request("/mock-gateway/s1"));

    expect(response.status).toBe(200);
    expect(response.headers.get("location")).toBeNull();
  });
});
