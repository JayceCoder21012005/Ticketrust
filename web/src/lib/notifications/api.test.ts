import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { ApiError } from "@/lib/errors";
import { listNotifications, markAllNotificationsRead, markNotificationRead } from "./api";

const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { "content-type": "application/json" } });
const calls: Request[] = [];
const fetchMock = vi.fn();

const item = { id: "n1", type: "OrderPaid", title: "Thanh toán thành công", body: "Đơn #1 đã thanh toán.", link: "/account/orders/o1", read: false, createdAt: "2026-12-01T10:00:00Z" };

/** Every request is recorded; the api answers with `response`. */
const answer = (response: () => Response) =>
  fetchMock.mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) => {
    calls.push(input instanceof Request ? input.clone() : new Request(new URL(String(input), "http://localhost:3000"), init));
    return response();
  });

beforeEach(() => {
  calls.length = 0;
  answer(() => json({ items: [item], nextCursor: "c2", unreadCount: "3" }));
  vi.stubGlobal("fetch", fetchMock);
});

afterEach(() => vi.unstubAllGlobals());

describe("listNotifications", () => {
  it("passes the cursor and limit on and turns the count into a number", async () => {
    const page = await listNotifications("c1", 10);

    const url = new URL(calls[0]!.url);
    expect(url.pathname).toBe("/api/v1/me/notifications");
    expect(url.searchParams.get("cursor")).toBe("c1");
    expect(url.searchParams.get("limit")).toBe("10");
    expect(page).toMatchObject({ nextCursor: "c2", unreadCount: 3 });
    expect(page.items[0]).toEqual(item);
  });

  it("asks for the first page without a cursor", async () => {
    await listNotifications();
    expect(new URL(calls[0]!.url).searchParams.has("cursor")).toBe(false);
  });

  it("throws the api's problem", async () => {
    answer(() => json({ code: "validation.failed" }, 400));
    await expect(listNotifications("bad")).rejects.toBeInstanceOf(ApiError);
  });
});

describe("marking read", () => {
  it("POSTs the notification's read endpoint", async () => {
    answer(() => new Response(null, { status: 204 }));
    await markNotificationRead("n1");

    expect(calls[0]!.method).toBe("POST");
    expect(new URL(calls[0]!.url).pathname).toBe("/api/v1/me/notifications/n1/read");
  });

  it("POSTs read-all", async () => {
    answer(() => new Response(null, { status: 204 }));
    await markAllNotificationsRead();

    expect(calls[0]!.method).toBe("POST");
    expect(new URL(calls[0]!.url).pathname).toBe("/api/v1/me/notifications/read-all");
  });

  it("throws when the api refuses", async () => {
    answer(() => json({ code: "notification.not_found" }, 404));
    await expect(markNotificationRead("n1")).rejects.toMatchObject({ status: 404 });
  });
});
