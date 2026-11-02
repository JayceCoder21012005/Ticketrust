import { describe, expect, it } from "vitest";
import { apiRewrites } from "./api-rewrites";

describe("apiRewrites", () => {
  it("proxies every /api path to the api origin so the browser stays same-origin", () => {
    expect(apiRewrites("http://localhost:5080")).toEqual([
      { source: "/api/:path*", destination: "http://localhost:5080/api/:path*" },
    ]);
  });

  it("ignores a trailing slash on the api origin", () => {
    expect(apiRewrites("http://api:8080/")[0].destination).toBe("http://api:8080/api/:path*");
  });

  it("falls back to the api's local dev port so tooling (typegen, lint, CI build) can load the config", () => {
    expect(apiRewrites(undefined)[0].destination).toBe("http://localhost:5080/api/:path*");
  });
});
