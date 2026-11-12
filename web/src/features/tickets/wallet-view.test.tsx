import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, fireEvent, render, screen, within } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { ApiError, NetworkError } from "@/lib/errors";
import type { QrKey, TicketDetail } from "@/lib/tickets/types";

const router = vi.hoisted(() => ({ replace: vi.fn(), push: vi.fn(), prefetch: vi.fn() }));
vi.mock("next/navigation", () => ({ useRouter: () => router, usePathname: () => "/tickets/x" }));

const auth = vi.hoisted(() => ({ status: "authenticated" }));
vi.mock("@/components/auth/auth-provider", () => ({ useAuth: () => ({ status: auth.status }) }));
vi.mock("@/lib/api/use-session-present", () => ({ useSessionPresent: () => true }));

const api = vi.hoisted(() => ({ getTicket: vi.fn(), getQrKey: vi.fn(), getMyTickets: vi.fn() }));
vi.mock("@/lib/tickets/api", async (importOriginal) => ({ ...(await importOriginal<typeof import("@/lib/tickets/api")>()), ...api }));

/** The api clock: `skew` ms ahead of the device. */
const server = vi.hoisted(() => ({ skew: 0, fetchServerTime: vi.fn() }));
vi.mock("@/lib/server-time", () => ({ fetchServerTime: server.fetchServerTime }));

/** A readable stand-in for the HMAC (the real one is checked against the shared vectors and in e2e). */
const code = vi.hoisted(() => ({
  qrPayload: vi.fn(async (ticketId: string, _secret: Uint8Array, unixSeconds: number) => `TR1.${ticketId.slice(0, 8)}.${Math.floor(unixSeconds / 15)}.sig`),
}));
vi.mock("@/lib/tickets/qr-code", async (importOriginal) => ({ ...(await importOriginal<typeof import("@/lib/tickets/qr-code")>()), qrPayload: code.qrPayload }));

const { WalletView } = await import("./wallet-view");

const ids = ["0192f0e0-0001-7bbb-8ccc-0123456789ab", "0192f0e0-0002-7bbb-8ccc-0123456789ab", "0192f0e0-0003-7bbb-8ccc-0123456789ab", "0192f0e0-0004-7bbb-8ccc-0123456789ab"];
const SECRET = btoa(String.fromCharCode(...new Uint8Array(32).fill(42)));
/** 12:00:07 UTC = 19:00:07 in Hà Nội; the 15-second step started 7 s ago. */
const T0 = 1_794_484_800_000 + 7_000;
const stepAt = (ms: number) => Math.floor(ms / 15_000);

function ticket(extra: Partial<TicketDetail> = {}, event: Partial<TicketDetail["event"]> = {}): TicketDetail {
  return {
    id: ids[1]!,
    orderId: "o1",
    tierName: "Hạng VIP",
    seat: { section: "A", row: "3", number: "12" },
    status: "Valid",
    holderName: "Nguyễn Văn An",
    orderTicketIds: ids,
    event: {
      eventId: "e1",
      slug: "dem-nhac-mua-thu",
      title: "Đêm nhạc Mùa Thu",
      coverImageUrl: null,
      startsAt: "2099-12-20T13:00:00Z",
      endsAt: "2099-12-20T16:00:00Z",
      venueName: "Nhà hát Hòa Bình",
      city: "TP.HCM",
      status: "Published",
      ...event,
    },
    ...extra,
  };
}

const key = (id: string): QrKey => ({ ticketId: id, secret: SECRET, stepSeconds: 15, format: "TR1" });

let reducedMotion = false;

beforeEach(() => {
  vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout", "setInterval", "clearInterval", "Date"] });
  vi.setSystemTime(T0);
  auth.status = "authenticated";
  reducedMotion = false;
  server.skew = 0;
  server.fetchServerTime.mockImplementation(async () => ({ serverNow: new Date(Date.now() + server.skew) }));
  router.replace.mockClear();
  router.push.mockClear();
  code.qrPayload.mockClear();
  api.getTicket.mockReset().mockImplementation(async (id: string) => ticket({ id }));
  api.getQrKey.mockReset().mockImplementation(async (id: string) => key(id));
  vi.stubGlobal("matchMedia", (query: string) => ({
    matches: query.includes("prefers-reduced-motion") ? reducedMotion : false,
    media: query,
    addEventListener: () => undefined,
    removeEventListener: () => undefined,
  }));
});

afterEach(() => {
  vi.useRealTimers();
  vi.unstubAllGlobals();
});

/** Lets promises, the lazily imported encoder and React settle without moving the clock. */
async function settle() {
  for (let i = 0; i < 5; i++) {
    await act(async () => {
      await vi.dynamicImportSettled();
      await vi.advanceTimersByTimeAsync(0);
    });
  }
}

const advance = async (ms: number) => {
  await act(() => vi.advanceTimersByTimeAsync(ms));
  await settle();
};

function renderWallet(id = ids[1]!) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const view = render(
    <QueryClientProvider client={client}>
      <WalletView ticketId={id} />
    </QueryClientProvider>,
  );
  return { client, ...view };
}

const qr = () => screen.queryByRole("img", { name: "Mã QR vào cửa, đổi mỗi 15 giây" });
const qrPath = () => qr()?.querySelector("path[data-modules]")?.getAttribute("d") ?? null;
const clock = () => screen.getByRole("timer");
/** The 15-second step the last code was computed for. */
const stepOfLastCode = () => {
  const call = code.qrPayload.mock.lastCall!;
  expect(call[0]).toBe(ids[1]);
  return Math.floor(call[2] / 15);
};

describe("WalletView", () => {
  it("draws the QR from the rotating code and redraws it at the boundary", async () => {
    renderWallet();
    await settle();

    expect(qr()).toBeInTheDocument();
    const before = qrPath();
    expect(before).toMatch(/^M/);
    expect(api.getQrKey).toHaveBeenCalledWith(ids[1], expect.anything());

    await advance(8_000);
    expect(qrPath()).not.toBe(before);
    expect(stepOfLastCode()).toBe(stepAt(T0 + 8_000));
  });

  it("Progress_ring_tracks_the_time_left", async () => {
    renderWallet();
    await settle();

    const ring = screen.getByRole("progressbar", { name: "Thời gian đến mã mới" });
    expect(ring).toHaveAttribute("aria-valuemax", "15");
    expect(ring).toHaveAttribute("aria-valuenow", "8");
    expect(ring).toHaveAttribute("aria-valuetext", "Còn 8 giây");

    await advance(1_000);
    expect(ring).toHaveAttribute("aria-valuenow", "7");

    await advance(7_000);
    expect(ring).toHaveAttribute("aria-valuenow", "15");
  });

  it("Live_clock_ticks_every_second", async () => {
    renderWallet();
    await settle();

    expect(clock()).toHaveTextContent("19:00:07");
    // The clock is not a live region: a screen reader is not told the time every second.
    expect(clock()).toHaveAttribute("aria-live", "off");

    await advance(1_000);
    expect(clock()).toHaveTextContent("19:00:08");
    await advance(1_000);
    expect(clock()).toHaveTextContent("19:00:09");
  });

  it("Time_uses_the_server_corrected_clock", async () => {
    server.skew = 20_000;
    renderWallet();
    await settle();

    expect(server.fetchServerTime).toHaveBeenCalledTimes(1);
    expect(clock()).toHaveTextContent("19:00:27");
    expect(stepOfLastCode()).toBe(stepAt(T0 + 20_000));
    expect(screen.getByRole("progressbar")).toHaveAttribute("aria-valuenow", "3");
  });

  it("falls back to the device clock when the server time is unavailable", async () => {
    server.fetchServerTime.mockRejectedValue(new Error("offline"));
    renderWallet();
    await settle();

    expect(clock()).toHaveTextContent("19:00:07");
    expect(qr()).toBeInTheDocument();
  });

  it("Reduced_motion_stops_the_gradient_but_keeps_the_clock", async () => {
    const view = renderWallet();
    await settle();
    expect(screen.getByTestId("wallet-band")).toHaveAttribute("data-animated", "true");
    view.unmount();

    reducedMotion = true;
    renderWallet();
    await settle();

    expect(screen.getByTestId("wallet-band")).toHaveAttribute("data-animated", "false");
    expect(clock()).toHaveTextContent("19:00:07");
    await advance(1_000);
    expect(clock()).toHaveTextContent("19:00:08");
  });

  it("Listed_ticket_hides_the_qr_and_shows_the_overlay", async () => {
    api.getTicket.mockResolvedValue(ticket({ status: "Listed" }));
    renderWallet();
    await settle();

    expect(screen.getByText("Đang rao bán")).toBeInTheDocument();
    expect(qr()).not.toBeInTheDocument();
    expect(screen.queryByRole("timer")).not.toBeInTheDocument();
    expect(api.getQrKey).not.toHaveBeenCalled();
  });

  it("Checked_in_and_voided_overlays", async () => {
    const cases: [Partial<TicketDetail>, Partial<TicketDetail["event"]>, string][] = [
      [{ status: "CheckedIn" }, {}, "Đã check-in"],
      [{ status: "Voided" }, {}, "Đã huỷ"],
      [{ status: "Valid" }, { status: "Cancelled" }, "Đã huỷ"],
      [{ status: "Valid" }, { status: "Ended" }, "Sự kiện đã kết thúc"],
      [{ status: "Valid" }, { startsAt: "2020-03-01T13:00:00Z", endsAt: "2020-03-01T16:00:00Z" }, "Sự kiện đã kết thúc"],
    ];

    for (const [extra, event, text] of cases) {
      api.getTicket.mockResolvedValue(ticket(extra, event));
      const view = renderWallet();
      await settle();

      expect(screen.getByRole("heading", { level: 2, name: text })).toBeInTheDocument();
      expect(qr()).not.toBeInTheDocument();
      view.unmount();
    }

    // No check-in time is invented: spec 11 adds it.
    api.getTicket.mockResolvedValue(ticket({ status: "CheckedIn" }));
    renderWallet();
    await settle();
    expect(screen.getByRole("heading", { level: 2, name: "Đã check-in" }).parentElement).not.toHaveTextContent(/lúc|\d{2}:\d{2}/);
    expect(api.getQrKey).not.toHaveBeenCalled();
  });

  it("Seat_info_shows_Khu_Hang_Ghe_and_tier_and_holder", async () => {
    const view = renderWallet();
    await settle();

    const seat = screen.getByRole("region", { name: "Thông tin chỗ ngồi" });
    const value = (label: string) => within(seat).getByText(label).nextElementSibling;
    expect(value("Khu")).toHaveTextContent("A");
    expect(value("Hàng")).toHaveTextContent("3");
    expect(value("Ghế")).toHaveTextContent("12");
    expect(seat).toHaveTextContent("Hạng VIP");
    expect(seat).toHaveTextContent("Nguyễn Văn An");
    expect(screen.getByText("Tăng độ sáng màn hình để quét mã nhanh hơn.")).toBeInTheDocument();
    expect(screen.getByText(/Nhà hát Hòa Bình, TP\.HCM/)).toBeInTheDocument();
    expect(screen.getByText(/20:00/)).toBeInTheDocument();
    view.unmount();

    api.getTicket.mockResolvedValue(ticket({ seat: null, tierName: "Vé đứng GA" }));
    renderWallet();
    await settle();
    const ga = screen.getByRole("region", { name: "Thông tin chỗ ngồi" });
    expect(within(ga).getByText("Vé đứng")).toBeInTheDocument();
    expect(within(ga).queryByText("Ghế")).not.toBeInTheDocument();
    expect(ga).toHaveTextContent("Vé đứng GA");
  });

  it("Add to calendar downloads an .ics named after the event", async () => {
    const created: Blob[] = [];
    const createObjectURL = vi.fn((blob: Blob) => {
      created.push(blob);
      return "blob:ics";
    });
    vi.stubGlobal("URL", Object.assign(URL, { createObjectURL, revokeObjectURL: vi.fn() }));
    const click = vi.spyOn(HTMLAnchorElement.prototype, "click").mockImplementation(() => undefined);
    renderWallet();
    await settle();

    fireEvent.click(screen.getByRole("button", { name: "Thêm vào lịch" }));

    expect(click).toHaveBeenCalledTimes(1);
    const anchor = click.mock.contexts[0] as HTMLAnchorElement;
    expect(anchor.download).toBe("dem-nhac-mua-thu.ics");
    expect(created[0]!.type).toBe("text/calendar;charset=utf-8");
    const text = await created[0]!.text();
    expect(text).toContain("SUMMARY:Đêm nhạc Mùa Thu");
    expect(text).toContain("DTSTART:20991220T130000Z");
    expect(text).not.toContain(SECRET);
  });

  it("Sibling_navigation_with_arrows_and_swipe", async () => {
    renderWallet();
    await settle();

    expect(screen.getByText("2/4")).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Vé trước" })).toHaveAttribute("href", `/tickets/${ids[0]}`);
    expect(screen.getByRole("link", { name: "Vé sau" })).toHaveAttribute("href", `/tickets/${ids[2]}`);

    // The heading has focus after load: the arrow keys work from inside the wallet.
    const heading = screen.getByRole("heading", { level: 1 });
    expect(heading).toHaveFocus();
    fireEvent.keyDown(heading, { key: "ArrowRight" });
    expect(router.replace).toHaveBeenLastCalledWith(`/tickets/${ids[2]}`, { scroll: false });
    fireEvent.keyDown(heading, { key: "ArrowLeft" });
    expect(router.replace).toHaveBeenLastCalledWith(`/tickets/${ids[0]}`, { scroll: false });

    // A swipe to the left goes forward, to the right goes back; a short or vertical drag does nothing.
    const wallet = screen.getByTestId("wallet");
    router.replace.mockClear();
    fireEvent.pointerDown(wallet, { pointerId: 1, pointerType: "touch", clientX: 250, clientY: 300 });
    fireEvent.pointerUp(wallet, { pointerId: 1, pointerType: "touch", clientX: 120, clientY: 310 });
    expect(router.replace).toHaveBeenLastCalledWith(`/tickets/${ids[2]}`, { scroll: false });

    fireEvent.pointerDown(wallet, { pointerId: 2, pointerType: "touch", clientX: 100, clientY: 300 });
    fireEvent.pointerUp(wallet, { pointerId: 2, pointerType: "touch", clientX: 230, clientY: 290 });
    expect(router.replace).toHaveBeenLastCalledWith(`/tickets/${ids[0]}`, { scroll: false });

    router.replace.mockClear();
    fireEvent.pointerDown(wallet, { pointerId: 3, pointerType: "touch", clientX: 200, clientY: 300 });
    fireEvent.pointerUp(wallet, { pointerId: 3, pointerType: "touch", clientX: 180, clientY: 300 });
    fireEvent.pointerDown(wallet, { pointerId: 4, pointerType: "touch", clientX: 200, clientY: 100 });
    fireEvent.pointerUp(wallet, { pointerId: 4, pointerType: "touch", clientX: 120, clientY: 400 });
    expect(router.replace).not.toHaveBeenCalled();
  });

  it("has no previous ticket on the first one and no navigation for a single ticket", async () => {
    api.getTicket.mockImplementation(async (id: string) => ticket({ id }));
    const view = renderWallet(ids[0]);
    await settle();

    expect(screen.getByText("1/4")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Vé trước" })).toBeDisabled();
    fireEvent.keyDown(screen.getByRole("heading", { level: 1 }), { key: "ArrowLeft" });
    expect(router.replace).not.toHaveBeenCalled();
    view.unmount();

    api.getTicket.mockImplementation(async (id: string) => ticket({ id, orderTicketIds: [id] }));
    renderWallet(ids[0]);
    await settle();
    expect(screen.queryByRole("navigation", { name: "Các vé trong đơn" })).not.toBeInTheDocument();
  });

  it("Qr_key_409_shows_not_displayable_state", async () => {
    api.getQrKey.mockRejectedValue(new ApiError(409, { code: "ticket.not_displayable", status: 409 }));
    renderWallet();
    await settle();

    expect(screen.getByRole("heading", { level: 2, name: "Không thể hiển thị mã QR" })).toBeInTheDocument();
    expect(qr()).not.toBeInTheDocument();
    // The ticket changed under us (checked in, listed): reload it so the matching overlay replaces this one.
    expect(api.getTicket).toHaveBeenCalledTimes(2);
  });

  it("a 404 from qr-key shows the not-found state", async () => {
    api.getQrKey.mockRejectedValue(new ApiError(404, { code: "ticket.not_found", status: 404 }));
    renderWallet();
    await settle();

    expect(screen.getByRole("heading", { name: "Không tìm thấy vé" })).toBeInTheDocument();
  });

  it("an unknown or foreign ticket shows the not-found state", async () => {
    api.getTicket.mockRejectedValue(new ApiError(404, { code: "ticket.not_found", status: 404 }));
    renderWallet();
    await settle();

    expect(screen.getByRole("heading", { name: "Không tìm thấy vé" })).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Về vé của tôi" })).toHaveAttribute("href", "/account/tickets");
  });

  it("Rate_limited_shows_countdown_and_retries", async () => {
    api.getQrKey.mockRejectedValueOnce(new ApiError(429, { code: "ticket.qr_key_rate_limited", status: 429, retryAfterSeconds: 3 }));
    renderWallet();
    await settle();

    expect(screen.getByText("Thử lại sau 3 giây")).toBeInTheDocument();
    expect(qr()).not.toBeInTheDocument();
    await advance(1_000);
    expect(screen.getByText("Thử lại sau 2 giây")).toBeInTheDocument();

    await advance(2_000);
    expect(api.getQrKey).toHaveBeenCalledTimes(2);
    expect(qr()).toBeInTheDocument();
  });

  it("a network error or a busy api offers a retry", async () => {
    api.getQrKey.mockRejectedValueOnce(new NetworkError()).mockRejectedValueOnce(new ApiError(503, { code: "system.busy", status: 503 }));
    renderWallet();
    await settle();

    expect(screen.getByText("Không tải được mã QR")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Thử lại" }));
    await settle();
    expect(screen.getByText("Không tải được mã QR")).toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: "Thử lại" }));
    await settle();
    expect(api.getQrKey).toHaveBeenCalledTimes(3);
    expect(qr()).toBeInTheDocument();
  });

  it("Missing_crypto_subtle_shows_a_clear_error", async () => {
    vi.stubGlobal("crypto", { getRandomValues: globalThis.crypto.getRandomValues.bind(globalThis.crypto) });
    renderWallet();
    await settle();

    expect(screen.getByText("Cần kết nối an toàn (HTTPS) để hiển thị mã")).toBeInTheDocument();
    expect(qr()).not.toBeInTheDocument();
  });

  it("Secret_never_enters_the_query_cache", async () => {
    const log = vi.spyOn(console, "log");
    const info = vi.spyOn(console, "info");
    const { client } = renderWallet();
    await settle();
    expect(qr()).toBeInTheDocument();

    const cached = JSON.stringify(client.getQueryCache().getAll().map((query) => [query.queryKey, query.state.data]));
    expect(cached).not.toContain(SECRET);
    expect(client.getMutationCache().getAll()).toHaveLength(0);
    // Neither the secret nor the code itself is in the page (the QR is drawn as shapes).
    const html = document.documentElement.outerHTML;
    expect(html).not.toContain(SECRET);
    expect(html).not.toContain(`.${stepAt(T0)}.sig`);
    expect(log).not.toHaveBeenCalled();
    expect(info).not.toHaveBeenCalled();
  });

  it("sends a signed-out visitor to login", async () => {
    auth.status = "anonymous";
    renderWallet();
    await settle();

    expect(router.replace).toHaveBeenCalledWith(`/login?returnUrl=${encodeURIComponent(`/tickets/${ids[1]}`)}`);
  });

  it("shows a skeleton while loading", () => {
    api.getTicket.mockReturnValue(new Promise(() => undefined));
    renderWallet();

    expect(screen.getByText("Đang tải vé…")).toBeInTheDocument();
  });
});
