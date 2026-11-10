import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { ApiError } from "@/lib/errors";
import { listMyOrders } from "./orders-api";

const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { "content-type": "application/json" } });
const calls: Request[] = [];
const fetchMock = vi.fn();

beforeEach(() => {
  calls.length = 0;
  fetchMock.mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) => {
    calls.push(input instanceof Request ? input.clone() : new Request(new URL(String(input), "http://localhost:3000"), init));
    return json({ items: [{ id: "o1", eventId: "e1", eventTitle: "Live", eventSlug: "live", eventStartsAt: "2026-12-20T13:00:00Z", status: "Paid", total: "3000000", itemCount: "2", createdAt: "2026-12-01T10:00:00Z", paidAt: null }], nextCursor: "c2" });
  });
  vi.stubGlobal("fetch", fetchMock);
});

afterEach(() => vi.unstubAllGlobals());

describe("listMyOrders", () => {
  it("passes the cursor and limit on and turns int64 into numbers", async () => {
    const page = await listMyOrders("c1", 20);

    const url = new URL(calls[0]!.url);
    expect(url.pathname).toBe("/api/v1/me/orders");
    expect(url.searchParams.get("cursor")).toBe("c1");
    expect(url.searchParams.get("limit")).toBe("20");
    expect(page.nextCursor).toBe("c2");
    expect(page.items[0]).toMatchObject({ total: 3_000_000, itemCount: 2 });
  });

  it("asks for the first page without a cursor", async () => {
    await listMyOrders();
    expect(new URL(calls[0]!.url).searchParams.has("cursor")).toBe(false);
  });

  it("throws the api's problem", async () => {
    fetchMock.mockResolvedValueOnce(json({ code: "validation.failed" }, 400));
    await expect(listMyOrders("bad")).rejects.toBeInstanceOf(ApiError);
  });
});
