import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { ApiError, NetworkError } from "@/lib/errors";
import type { ScanResponse } from "@/lib/gate/api";

const api = vi.hoisted(() => ({ listGateEvents: vi.fn(), scanTicket: vi.fn(), getGateStats: vi.fn() }));
vi.mock("@/lib/gate/api", () => api);

const feedback = vi.hoisted(() => ({ giveFeedback: vi.fn() }));
vi.mock("@/lib/gate/feedback", () => feedback);

import { ScannerScreen } from "./scanner-screen";

const EVENT_ID = "11111111-1111-4111-8111-111111111111";
const admitted: ScanResponse = {
  result: "Admitted",
  reason: null,
  ticket: { tier: "VIP", section: "A", row: "3", seat: "12", holderName: "Lê Thị Hương" },
  checkedInAt: "2026-11-20T12:42:00Z",
  gateName: null,
};

function renderScreen() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <ScannerScreen eventId={EVENT_ID} />
    </QueryClientProvider>,
  );
}

async function scanByHand(code: string) {
  const user = userEvent.setup();
  await user.type(screen.getByLabelText("Mã vé"), code);
  await user.click(screen.getByRole("button", { name: "Kiểm tra" }));
}

beforeEach(() => {
  vi.resetAllMocks();
  api.listGateEvents.mockResolvedValue([
    { eventId: EVENT_ID, slug: "x", title: "Sơn Tùng M-TP Live", startsAt: "2026-11-20T12:00:00Z", endsAt: "2026-11-20T16:00:00Z", venueName: "SVĐ", city: "Hà Nội", gateName: "Cổng B" },
  ]);
  api.getGateStats.mockResolvedValue({ checkedIn: 1234, total: 5000, lastMinute: 3 });
});

describe("the gate scanner", () => {
  it("shows the gate, the event and the live counter", async () => {
    renderScreen();

    expect(await screen.findByText("Cổng B")).toBeInTheDocument();
    expect(screen.getByText("Sơn Tùng M-TP Live")).toBeInTheDocument();
    await waitFor(() => expect(screen.getByTestId("gate-counter")).toHaveTextContent("1.234 / 5.000"));
    expect(screen.getByRole("status")).toHaveTextContent("Đã kết nối");
  });

  it("answers an admitted ticket in green with the seat in big type, and gives feedback", async () => {
    api.scanTicket.mockResolvedValue(admitted);
    renderScreen();

    await scanByHand("TR1.abc");

    expect(api.scanTicket).toHaveBeenCalledWith(EVENT_ID, "TR1.abc");
    const overlay = await screen.findByTestId("scan-result");
    expect(overlay).toHaveTextContent("Hợp lệ");
    expect(overlay).toHaveAttribute("data-tone", "ok");
    expect(overlay).toHaveTextContent("Khu A · Hàng 3 · Ghế 12");
    expect(feedback.giveFeedback).toHaveBeenCalledWith("ok");
  });

  it("answers an already checked-in ticket in amber with the time and the gate", async () => {
    api.scanTicket.mockResolvedValue({ ...admitted, result: "AlreadyCheckedIn", gateName: "Cổng A" });
    renderScreen();

    await scanByHand("TR1.abc");

    const overlay = await screen.findByTestId("scan-result");
    expect(overlay).toHaveTextContent("Đã check-in lúc 19:42 – Cổng A");
    expect(overlay).toHaveAttribute("data-tone", "warn");
  });

  it("answers a refused ticket in red with the reason", async () => {
    api.scanTicket.mockResolvedValue({ result: "Expired", reason: null, ticket: null, checkedInAt: null, gateName: null });
    renderScreen();

    await scanByHand("TR1.abc");

    const overlay = await screen.findByTestId("scan-result");
    expect(overlay).toHaveTextContent("Mã đã hết hạn");
    expect(overlay).toHaveAttribute("data-tone", "bad");
    expect(feedback.giveFeedback).toHaveBeenCalledWith("bad");
  });

  it("says the ticket was NOT checked when the request fails", async () => {
    api.scanTicket.mockRejectedValue(new NetworkError());
    renderScreen();

    await scanByHand("TR1.abc");

    expect(await screen.findByText(/Vé CHƯA được kiểm tra/)).toBeInTheDocument();
  });

  it("tells staff to slow down on a 429", async () => {
    api.scanTicket.mockRejectedValue(new ApiError(429, { retryAfterSeconds: 1 }));
    renderScreen();

    await scanByHand("TR1.abc");

    expect(await screen.findByText("Quét quá nhanh")).toBeInTheDocument();
  });

  it("shows the lost-connection banner when the stats cannot be fetched", async () => {
    api.getGateStats.mockRejectedValue(new NetworkError());
    renderScreen();

    expect(await screen.findByText(/Mất kết nối — vé quét lúc này chưa được kiểm tra/)).toBeInTheDocument();
    expect(screen.getByRole("status")).toHaveTextContent("Mất kết nối");
  });

  it("explains a camera that cannot be opened and still offers manual entry", async () => {
    renderScreen();

    expect(await screen.findByText("Không mở được camera")).toBeInTheDocument();
    expect(screen.getByLabelText("Mã vé")).toBeEnabled();
  });
});
