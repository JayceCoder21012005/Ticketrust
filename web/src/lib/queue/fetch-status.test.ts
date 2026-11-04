import { describe, expect, it, vi } from "vitest";
import { fetchQueueStatus, parseStatus } from "./fetch-status";

const body = {
  status: "Waiting",
  position: "1234",
  aheadOfYou: 1233,
  estimatedWaitSeconds: 25,
  opensAt: "2026-12-01T10:00:00+00:00",
  serverNow: "2026-12-01T10:05:00+00:00",
  pollAfterMs: 8000,
  admissionToken: null,
  tokenExpiresAt: null,
};

const json = (data: unknown, status = 200) =>
  new Response(JSON.stringify(data), { status, headers: { "Content-Type": "application/json" } });

describe("parseStatus", () => {
  it("turns int64 strings from the api into numbers", () => {
    expect(parseStatus(body)).toEqual({ ...body, position: 1234 });
  });

  it("keeps nulls and the admission token", () => {
    const parsed = parseStatus({ ...body, status: "Admitted", position: null, aheadOfYou: null, admissionToken: "t", tokenExpiresAt: "2026-12-01T10:15:00+00:00" });

    expect(parsed).toMatchObject({ status: "Admitted", position: null, admissionToken: "t", tokenExpiresAt: "2026-12-01T10:15:00+00:00" });
  });
});

describe("fetchQueueStatus", () => {
  it("sends the bearer token to the same-origin api without caching", async () => {
    const fetchImpl = vi.fn(async () => json(body));

    const outcome = await fetchQueueStatus("evt-1", "tok", fetchImpl);

    expect(outcome).toEqual({ kind: "ok", status: expect.objectContaining({ position: 1234 }) });
    const [url, init] = fetchImpl.mock.calls[0] as unknown as [string, RequestInit];
    expect(url).toBe("/api/v1/events/evt-1/queue/me");
    expect(new Headers(init.headers).get("authorization")).toBe("Bearer tok");
    expect(init.cache).toBe("no-store");
  });

  it("maps 401 to unauthorized", async () => {
    expect(await fetchQueueStatus("e", "t", async () => json({}, 401))).toEqual({ kind: "unauthorized" });
  });

  it("maps 404 queue.not_joined to notJoined and a vanished event to gone", async () => {
    expect(await fetchQueueStatus("e", "t", async () => json({ code: "queue.not_joined" }, 404))).toEqual({ kind: "notJoined" });
    expect(await fetchQueueStatus("e", "t", async () => json({ code: "event.not_found" }, 404))).toEqual({ kind: "gone" });
  });

  it("maps server failures and network errors to error outcomes", async () => {
    expect(await fetchQueueStatus("e", "t", async () => json({ code: "system.busy" }, 503))).toEqual({ kind: "error", httpStatus: 503 });
    expect(
      await fetchQueueStatus("e", "t", async () => {
        throw new TypeError("network");
      }),
    ).toEqual({ kind: "error", httpStatus: 0 });
  });
});
