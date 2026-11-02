import { afterEach, describe, expect, it, vi } from "vitest";
import { fetchServerTime } from "./server-time";

afterEach(() => vi.unstubAllGlobals());

describe("fetchServerTime", () => {
  it("calls the same-origin /api path (never the api host directly)", async () => {
    const fetchMock = vi.fn().mockResolvedValue(
      new Response(JSON.stringify({ serverNow: "2026-11-03T00:00:00+00:00" }), { status: 200 }),
    );
    vi.stubGlobal("fetch", fetchMock);

    const result = await fetchServerTime();

    expect(fetchMock).toHaveBeenCalledWith("/api/v1/time", expect.objectContaining({ cache: "no-store" }));
    expect(result.serverNow.toISOString()).toBe("2026-11-03T00:00:00.000Z");
  });

  it("throws with the status when the api is unavailable", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(new Response("", { status: 503 })));

    await expect(fetchServerTime()).rejects.toThrow(/503/);
  });
});
