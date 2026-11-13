import { describe, expect, it } from "vitest";
import { serviceWorkerHeaders } from "./service-worker-headers";

describe("serviceWorkerHeaders", () => {
  it("serves /sw.js as JavaScript, revalidated on every update check, allowed to control the whole origin", () => {
    expect(serviceWorkerHeaders()).toEqual([
      {
        source: "/sw.js",
        headers: [
          { key: "Content-Type", value: "application/javascript; charset=utf-8" },
          { key: "Cache-Control", value: "no-cache" },
          { key: "Service-Worker-Allowed", value: "/" },
        ],
      },
    ]);
  });
});
