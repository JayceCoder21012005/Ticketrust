import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { ApiError, NetworkError, toFriendlyMessage } from "@/lib/errors";
import { getMyTickets, getQrKey, getTicket, retryAfterSeconds, ticketFailure } from "./api";
import { ticketKeys } from "./keys";

const json = (body: unknown, status = 200, headers: Record<string, string> = {}) =>
  new Response(JSON.stringify(body), { status, headers: { "content-type": "application/json", ...headers } });
const calls: Request[] = [];
const fetchMock = vi.fn();

const answer = (response: () => Response | Promise<Response>) =>
  fetchMock.mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) => {
    calls.push(input instanceof Request ? input.clone() : new Request(new URL(String(input), "http://localhost:3000"), init));
    return response();
  });

const ticketId = "0192f0e0-aaaa-7bbb-8ccc-0123456789ab";
const item = { id: ticketId, orderId: "o1", tierName: "VIP", seat: { section: "A", row: "3", number: "12" }, status: "Valid", holderName: "Nguyễn Văn A" };
const event = {
  eventId: "e1",
  slug: "dem-nhac",
  title: "Đêm nhạc",
  coverImageUrl: null,
  startsAt: "2026-12-20T12:00:00Z",
  endsAt: "2026-12-20T15:00:00Z",
  venueName: "Nhà hát Lớn",
  city: "Hà Nội",
  status: "Published",
};

beforeEach(() => {
  calls.length = 0;
  vi.stubGlobal("fetch", fetchMock);
});

afterEach(() => vi.unstubAllGlobals());

describe("getMyTickets", () => {
  it("asks for one scope without caching and returns the event groups", async () => {
    answer(() => json({ events: [{ ...event, tickets: [item] }] }));

    const groups = await getMyTickets("upcoming");

    const url = new URL(calls[0]!.url);
    expect(url.pathname).toBe("/api/v1/me/tickets");
    expect(url.searchParams.get("scope")).toBe("upcoming");
    expect(calls[0]!.cache).toBe("no-store");
    expect(groups).toEqual([{ ...event, tickets: [item] }]);
  });

  it("throws the api's problem for a bad scope", async () => {
    answer(() => json({ code: "ticket.invalid_scope" }, 400));

    await expect(getMyTickets("past")).rejects.toMatchObject({ status: 400, code: "ticket.invalid_scope" });
  });
});

describe("getTicket", () => {
  it("GETs the ticket by id", async () => {
    const detail = { ...item, event, orderTicketIds: [ticketId, "t2"] };
    answer(() => json(detail));

    expect(await getTicket(ticketId)).toEqual(detail);
    expect(new URL(calls[0]!.url).pathname).toBe(`/api/v1/tickets/${ticketId}`);
  });

  it("maps 404 to ticket.not_found", async () => {
    answer(() => json({ code: "ticket.not_found", status: 404 }, 404));

    const error = await getTicket(ticketId).catch((e: unknown) => e);
    expect(error).toBeInstanceOf(ApiError);
    expect(ticketFailure(error)).toBe("not_found");
  });
});

describe("getQrKey", () => {
  it("returns the key with a numeric step", async () => {
    answer(() => json({ ticketId, secret: "AAH/", stepSeconds: "15", format: "TR1" }));

    expect(await getQrKey(ticketId)).toEqual({ ticketId, secret: "AAH/", stepSeconds: 15, format: "TR1" });
    expect(new URL(calls[0]!.url).pathname).toBe(`/api/v1/tickets/${ticketId}/qr-key`);
    expect(calls[0]!.cache).toBe("no-store");
  });

  it("maps 409 to not_displayable", async () => {
    answer(() => json({ code: "ticket.not_displayable" }, 409));

    const error = await getQrKey(ticketId).catch((e: unknown) => e);
    expect(ticketFailure(error)).toBe("not_displayable");
    expect(toFriendlyMessage(error)).toBe("Vé chưa thể hiển thị lúc này.");
  });

  it("maps 429 to rate_limited with the body's retryAfterSeconds", async () => {
    answer(() => json({ code: "ticket.qr_key_rate_limited", retryAfterSeconds: 42 }, 429, { "retry-after": "40" }));

    const error = await getQrKey(ticketId).catch((e: unknown) => e);
    expect(ticketFailure(error)).toBe("rate_limited");
    expect(retryAfterSeconds(error)).toBe(42);
  });

  it("falls back to the Retry-After header when the body has no retryAfterSeconds", async () => {
    answer(() => json({ code: "ticket.qr_key_rate_limited" }, 429, { "retry-after": "17" }));

    expect(retryAfterSeconds(await getQrKey(ticketId).catch((e: unknown) => e))).toBe(17);
  });

  it("maps 503 to busy", async () => {
    answer(() => json({ code: "system.busy" }, 503));

    expect(ticketFailure(await getQrKey(ticketId).catch((e: unknown) => e))).toBe("busy");
  });

  it("tells a network failure apart from an HTTP error", async () => {
    answer(() => Promise.reject(new TypeError("Failed to fetch")));

    const error = await getQrKey(ticketId).catch((e: unknown) => e);
    expect(error).toBeInstanceOf(NetworkError);
    expect(error).not.toBeInstanceOf(ApiError);
    expect(ticketFailure(error)).toBe("network");
    expect(retryAfterSeconds(error)).toBeNull();
    expect(toFriendlyMessage(error)).toMatch(/kết nối/i);
  });

  it("lets an abort through untouched", async () => {
    answer(() => Promise.reject(new DOMException("aborted", "AbortError")));

    const error = await getQrKey(ticketId).catch((e: unknown) => e);
    expect(error).toBeInstanceOf(DOMException);
    expect(ticketFailure(error)).toBe("other");
  });
});

describe("ticketKeys", () => {
  it("keeps every key under tickets", () => {
    expect(ticketKeys.all).toEqual(["tickets"]);
    expect(ticketKeys.list("past")).toEqual(["tickets", "list", "past"]);
    expect(ticketKeys.detail("t1")).toEqual(["tickets", "detail", "t1"]);
  });
});

describe("ticket messages", () => {
  it.each([
    ["ticket.not_found", "Không tìm thấy vé."],
    ["ticket.not_displayable", "Vé chưa thể hiển thị lúc này."],
    ["ticket.invalid_scope", "Danh sách vé không hợp lệ. Vui lòng tải lại trang."],
    ["ticket.qr_key_rate_limited", "Bạn mở mã QR quá nhiều lần. Vui lòng thử lại sau ít phút."],
    ["system.busy", "Hệ thống đang bận. Vui lòng thử lại sau giây lát."],
  ])("%s has its Vietnamese message", (code, message) => {
    expect(toFriendlyMessage(new ApiError(418, { code }))).toBe(message);
  });
});
