import { beforeEach, describe, expect, it, vi } from "vitest";
import { ApiError } from "@/lib/errors";

const post = vi.fn();
const get = vi.fn();
const del = vi.fn();
vi.mock("@/lib/api/client", () => ({ api: { POST: post, GET: get, DELETE: del } }));

const { createHold, getMyHold, releaseMyHold, unavailableOf } = await import("./holds-api");

const hold = {
  holdId: "h1",
  seats: [{ index: 3, section: "A", row: "1", number: "4", tierId: "t", price: "1500000" }],
  ga: [],
  total: "1500000",
  expiresAt: "2026-12-01T10:08:00Z",
  serverNow: "2026-12-01T10:00:00Z",
};

const response = (status: number) => new Response(null, { status });

describe("holds api", () => {
  beforeEach(() => {
    post.mockReset();
    get.mockReset();
    del.mockReset();
  });

  it("sends the admission token and the idempotency key and normalises int64 strings", async () => {
    post.mockResolvedValue({ data: hold, response: response(200) });

    const result = await createHold("e1", { seatIndexes: [3], replaceExisting: false }, "adm", "key-1");

    expect(post).toHaveBeenCalledWith("/api/v1/events/{eventId}/holds", {
      params: { path: { eventId: "e1" } },
      headers: { "X-Admission-Token": "adm", "Idempotency-Key": "key-1", traceparent: expect.stringMatching(/^00-[0-9a-f]{32}-[0-9a-f]{16}-01$/) },
      body: { seatIndexes: [3], ga: null, bestAvailable: null, replaceExisting: false },
    });
    expect(result.total).toBe(1_500_000);
    expect(result.seats[0].price).toBe(1_500_000);
  });

  it("throws an ApiError whose unavailable seats can be read", async () => {
    post.mockResolvedValue({ error: { code: "hold.seats_unavailable", unavailable: [3, 4] }, response: response(409) });

    const error = await createHold("e1", { seatIndexes: [3, 4], replaceExisting: false }, "adm", "k").catch((e: unknown) => e);

    expect(error).toBeInstanceOf(ApiError);
    expect(unavailableOf(error)).toEqual([3, 4]);
    expect(unavailableOf(new Error("x"))).toEqual([]);
  });

  it("returns null when there is no hold", async () => {
    get.mockResolvedValue({ error: { code: "hold.not_found" }, response: response(404) });

    expect(await getMyHold("e1")).toBeNull();
  });

  it("returns the current hold", async () => {
    get.mockResolvedValue({ data: hold, response: response(200) });

    expect((await getMyHold("e1"))?.holdId).toBe("h1");
  });

  it("releases idempotently and surfaces other failures", async () => {
    del.mockResolvedValueOnce({ response: response(204) });
    await expect(releaseMyHold("e1")).resolves.toBeUndefined();

    del.mockResolvedValueOnce({ error: { code: "system.busy" }, response: response(503) });
    await expect(releaseMyHold("e1")).rejects.toBeInstanceOf(ApiError);
  });
});
