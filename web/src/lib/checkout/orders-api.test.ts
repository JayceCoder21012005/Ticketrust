import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { ApiError } from "@/lib/errors";
import { createHold } from "@/lib/seats/holds-api";
import { createOrder, listMyOrders, payOrder } from "./orders-api";
import { resetPurchaseTrace } from "./purchase-trace";

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

afterEach(() => {
  vi.unstubAllGlobals();
  resetPurchaseTrace();
});

describe("the purchase trace (spec 17)", () => {
  it("sends one traceparent on the hold, the order and the payment, and a new one for the next purchase", async () => {
    fetchMock.mockImplementation(async (input: RequestInfo | URL) => {
      calls.push((input as Request).clone());
      return json({ id: "o1", holdId: "h1", seats: [], ga: [], total: "1", ticketCount: null, redirectUrl: "/pay/s1" });
    });
    const body = { eventId: "e1", holdId: "h1", buyer: { name: "An", email: "an@example.com", phone: "0901234567" } };

    await createHold("e1", { seatIndexes: [1], replaceExisting: false }, "adm", "hold-1");
    await createOrder(body, "order-1");
    await payOrder("o1", "pay-1");
    await createHold("e1", { seatIndexes: [2], replaceExisting: false }, "adm", "hold-2");

    const traces = calls.map((c) => c.headers.get("traceparent"));
    expect(traces[0]).toMatch(/^00-[0-9a-f]{32}-[0-9a-f]{16}-01$/);
    expect(traces.slice(0, 3)).toEqual([traces[0], traces[0], traces[0]]);
    expect(traces[3]).not.toBe(traces[0]);
  });

  it("sends no traceparent for an order this tab did not start with a hold", async () => {
    await payOrder("o1", "pay-1").catch(() => undefined);
    expect(calls[0]!.headers.has("traceparent")).toBe(false);
  });
});

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
