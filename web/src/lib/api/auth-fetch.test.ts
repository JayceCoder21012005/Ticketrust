import { describe, expect, it, vi } from "vitest";
import { createAuthFetch } from "./auth-fetch";

const ok = () => new Response("{}", { status: 200 });
const unauthorized = () => new Response("{}", { status: 401 });

function setup(responses: Array<() => Response>, refreshResult: string | null = "new-token") {
  let token: string | null = "old-token";
  const seen: Array<string | null> = [];
  const baseFetch = vi.fn(async (request: Request) => {
    seen.push(request.headers.get("Authorization"));
    return (responses.shift() ?? ok)();
  });
  const refresh = vi.fn(async () => {
    token = refreshResult;
    return refreshResult;
  });
  const onUnauthenticated = vi.fn();
  const authFetch = createAuthFetch({ baseFetch, getAccessToken: () => token, refresh, onUnauthenticated });
  return { authFetch, baseFetch, refresh, onUnauthenticated, seen };
}

const get = () => new Request("http://localhost/api/v1/me");

describe("createAuthFetch", () => {
  it("sends the in-memory access token as a bearer header", async () => {
    const { authFetch, seen } = setup([ok]);

    await authFetch(get());

    expect(seen).toEqual(["Bearer old-token"]);
  });

  it("refreshes silently once on 401 and retries with the new token", async () => {
    const { authFetch, refresh, seen } = setup([unauthorized, ok]);

    const response = await authFetch(get());

    expect(response.status).toBe(200);
    expect(refresh).toHaveBeenCalledTimes(1);
    expect(seen).toEqual(["Bearer old-token", "Bearer new-token"]);
  });

  it("shares one in-flight refresh between parallel 401s", async () => {
    const { authFetch, refresh } = setup([unauthorized, unauthorized, unauthorized, ok, ok, ok]);

    await Promise.all([authFetch(get()), authFetch(get()), authFetch(get())]);

    expect(refresh).toHaveBeenCalledTimes(1);
  });

  it("gives up and redirects to login when the refresh fails", async () => {
    const { authFetch, onUnauthenticated, baseFetch } = setup([unauthorized], null);

    const response = await authFetch(get());

    expect(response.status).toBe(401);
    expect(onUnauthenticated).toHaveBeenCalledTimes(1);
    expect(baseFetch).toHaveBeenCalledTimes(1);
  });

  it("does not loop when the retried request is still 401", async () => {
    const { authFetch, refresh, onUnauthenticated } = setup([unauthorized, unauthorized]);

    const response = await authFetch(get());

    expect(response.status).toBe(401);
    expect(refresh).toHaveBeenCalledTimes(1);
    expect(onUnauthenticated).toHaveBeenCalledTimes(1);
  });

  it("never tries to refresh a failed refresh call itself", async () => {
    const { authFetch, refresh } = setup([unauthorized]);

    await authFetch(new Request("http://localhost/api/v1/auth/refresh", { method: "POST" }));

    expect(refresh).not.toHaveBeenCalled();
  });

  it("replays the request body on retry", async () => {
    const bodies: string[] = [];
    const responses = [unauthorized, ok];
    const authFetch = createAuthFetch({
      baseFetch: async (request) => {
        bodies.push(await request.text());
        return responses.shift()!();
      },
      getAccessToken: () => "t",
      refresh: async () => "t2",
      onUnauthenticated: () => {},
    });

    await authFetch(new Request("http://localhost/api/v1/orders", { method: "POST", body: '{"a":1}' }));

    expect(bodies).toEqual(['{"a":1}', '{"a":1}']);
  });
});
