import { act, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { Order, OrderStatus } from "@/lib/checkout/types";
import { ApiError } from "@/lib/errors";

const router = vi.hoisted(() => ({ replace: vi.fn(), push: vi.fn() }));
vi.mock("next/navigation", () => ({ useRouter: () => router }));

const auth = vi.hoisted(() => ({ status: "authenticated" }));
vi.mock("@/components/auth/auth-provider", () => ({ useAuth: () => ({ status: auth.status }) }));

const orders = vi.hoisted(() => ({ getOrder: vi.fn(), confirmPayment: vi.fn() }));
vi.mock("@/lib/checkout/orders-api", () => orders);

const { ResultView } = await import("./result-view");

const NOW = Date.parse("2026-12-01T10:00:00Z");

const order = (status: OrderStatus, extra: Partial<Order> = {}): Order => ({
  id: "o1",
  eventId: "e1",
  status,
  seats: [],
  ga: [],
  total: 3_000_000,
  buyer: { name: "An", email: "an@example.com", phone: null },
  expiresAt: new Date(NOW + 300_000).toISOString(),
  serverNow: new Date(NOW).toISOString(),
  createdAt: new Date(NOW).toISOString(),
  paidAt: null,
  refundReason: null,
  ticketCount: null,
  ...extra,
});

function mockMatchMedia(reduce: boolean) {
  vi.stubGlobal("matchMedia", (query: string) => ({
    matches: reduce && query.includes("prefers-reduced-motion"),
    media: query,
    addEventListener: () => undefined,
    removeEventListener: () => undefined,
  }));
}

const flush = (ms = 0) => act(() => vi.advanceTimersByTimeAsync(ms));

async function show(status: OrderStatus, extra?: Partial<Order>) {
  orders.getOrder.mockResolvedValue(order(status, extra));
  render(<ResultView orderId="o1" />);
  await flush();
}

describe("ResultView", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.useFakeTimers({ now: NOW, toFake: ["Date", "setTimeout", "clearTimeout", "setInterval", "clearInterval"] });
    auth.status = "authenticated";
    localStorage.clear();
    mockMatchMedia(false);
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
  });

  it.each(["Pending", "AwaitingPayment"] as const)("%s: asks the buyer to wait and keeps polling", async (status) => {
    await show(status);

    const heading = screen.getByRole("heading", { level: 1, name: "Đang xác nhận thanh toán" });
    expect(heading).toHaveFocus();
    expect(screen.getByText(/Đừng đóng trang/)).toBeInTheDocument();
    await flush(1_500);
    expect(orders.getOrder).toHaveBeenCalledTimes(2);
  });

  it("Paid: celebrates, counts the tickets and offers them", async () => {
    await show("Paid", { ticketCount: 3, paidAt: new Date(NOW).toISOString() });

    expect(screen.getByRole("heading", { level: 1, name: "Thanh toán thành công" })).toHaveFocus();
    expect(screen.getByText("Bạn có 3 vé.")).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Xem vé" })).toHaveAttribute("href", "/account/orders");
    expect(screen.getByTestId("confetti")).toBeInTheDocument();
    expect(orders.getOrder).toHaveBeenCalledTimes(1);
  });

  it("Paid: no confetti for a buyer who prefers reduced motion", async () => {
    mockMatchMedia(true);
    await show("Paid");

    expect(screen.getByRole("heading", { name: "Thanh toán thành công" })).toBeInTheDocument();
    expect(screen.queryByTestId("confetti")).not.toBeInTheDocument();
  });

  it("PaymentFailed: offers a retry on the checkout page while the order is still open", async () => {
    await show("PaymentFailed");

    expect(screen.getByRole("heading", { name: "Thanh toán không thành công" })).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Thử lại" })).toHaveAttribute("href", "/checkout/o1");
  });

  it("PaymentFailed: no retry once the time to pay is over", async () => {
    await show("PaymentFailed", { expiresAt: new Date(NOW - 1_000).toISOString() });

    expect(screen.queryByRole("link", { name: "Thử lại" })).not.toBeInTheDocument();
    expect(screen.getByText(/Thời gian giữ ghế đã hết/)).toBeInTheDocument();
  });

  it("Expired: says so and leads back to the seats of the remembered event", async () => {
    localStorage.setItem("tr.checkout-events", JSON.stringify([{ id: "e1", slug: "su-kien", title: "Đêm nhạc" }]));
    await show("Expired");

    expect(screen.getByRole("heading", { name: "Đơn hàng đã hết hạn" })).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Chọn lại ghế" })).toHaveAttribute("href", "/events/su-kien/seats");
  });

  it("Expired: falls back to the event list when the event is not remembered", async () => {
    await show("Expired");

    expect(screen.getByRole("link", { name: "Xem sự kiện" })).toHaveAttribute("href", "/events");
  });

  it("Cancelled", async () => {
    await show("Cancelled");

    expect(screen.getByRole("heading", { name: "Đơn hàng đã huỷ" })).toBeInTheDocument();
  });

  it.each([
    ["seats_unavailable", /ghế đã có người khác mua/],
    ["event_cancelled", /Nhà tổ chức đã huỷ sự kiện/],
    ["event_ended", /sau khi sự kiện đã kết thúc/],
    ["purchase_limit_exceeded", /vượt quá số vé tối đa/],
    ["something_new", /không còn hợp lệ vào lúc thanh toán được xác nhận/],
    [null, /không còn hợp lệ vào lúc thanh toán được xác nhận/],
  ])("RefundPending (%s): explains honestly why the money is going back", async (reason, text) => {
    await show("RefundPending", { refundReason: reason });

    expect(screen.getByRole("heading", { name: "Đang hoàn tiền" })).toBeInTheDocument();
    expect(screen.getByText(/không thể xuất vé/)).toBeInTheDocument();
    expect(screen.getByText(text)).toBeInTheDocument();
    expect(screen.getByText(/tuỳ thuộc vào ngân hàng/)).toBeInTheDocument();
    expect(orders.getOrder).toHaveBeenCalledTimes(1);
  });

  it("Refunded", async () => {
    await show("Refunded");

    expect(screen.getByRole("heading", { name: "Đã hoàn tiền" })).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Đơn hàng của tôi" })).toHaveAttribute("href", "/account/orders");
  });

  it("announces a change of state and moves focus to the new heading", async () => {
    orders.getOrder.mockResolvedValueOnce(order("AwaitingPayment")).mockResolvedValue(order("Paid", { ticketCount: 1 }));
    render(<ResultView orderId="o1" />);
    await flush();
    expect(screen.getByRole("status")).toHaveTextContent("Đang xác nhận thanh toán");

    await flush(1_500);

    expect(screen.getByRole("heading", { name: "Thanh toán thành công" })).toHaveFocus();
    expect(screen.getByRole("status")).toHaveTextContent("Thanh toán thành công");
  });

  it("after 90 s of waiting says it is still processing, and checking again polls once more", async () => {
    await show("AwaitingPayment");

    await flush(91_000);

    expect(screen.getByRole("heading", { name: "Vẫn đang xử lý" })).toHaveFocus();
    expect(screen.getByText(/kiểm tra lại trong Đơn hàng của tôi/)).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Đơn hàng của tôi" })).toHaveAttribute("href", "/account/orders");
    expect(screen.getByRole("link", { name: "Quay lại thanh toán" })).toHaveAttribute("href", "/checkout/o1");
    const calls = orders.getOrder.mock.calls.length;
    await flush(10_000);
    expect(orders.getOrder).toHaveBeenCalledTimes(calls);

    orders.getOrder.mockResolvedValue(order("Paid"));
    fireEvent.click(screen.getByRole("button", { name: "Kiểm tra lại" }));
    await flush();
    expect(screen.getByRole("heading", { name: "Thanh toán thành công" })).toBeInTheDocument();
  });

  it("says so when the order is not found", async () => {
    orders.getOrder.mockRejectedValue(new ApiError(404, { code: "order.not_found" }));
    render(<ResultView orderId="o1" />);
    await flush();

    expect(screen.getByRole("heading", { name: "Không tìm thấy đơn hàng" })).toBeInTheDocument();
  });

  it("offers a retry when the order never loads", async () => {
    orders.getOrder.mockRejectedValue(new TypeError("offline"));
    render(<ResultView orderId="o1" />);
    await flush(91_000);

    expect(screen.getByRole("button", { name: "Thử lại" })).toBeInTheDocument();
  });

  it("asks the server to confirm the payment while the order awaits it, at most three times, and never once it is paid", async () => {
    orders.getOrder.mockResolvedValue(order("AwaitingPayment"));
    orders.confirmPayment.mockResolvedValue(order("AwaitingPayment"));
    render(<ResultView orderId="o1" />);
    await flush();
    await flush(1);
    expect(orders.confirmPayment).toHaveBeenCalledWith("o1");

    await flush(10_000);
    expect(orders.confirmPayment).toHaveBeenCalledTimes(3);

    vi.clearAllMocks();
    orders.getOrder.mockResolvedValue(order("Paid", { paidAt: new Date(NOW).toISOString(), ticketCount: 2 }));
    render(<ResultView orderId="o1" />);
    await flush(5_000);
    expect(orders.confirmPayment).not.toHaveBeenCalled();
  });

  it("shows the paid order as soon as the confirm call says so, and ignores a failing confirm call", async () => {
    orders.getOrder.mockResolvedValueOnce(order("AwaitingPayment")).mockResolvedValue(order("Paid", { paidAt: new Date(NOW).toISOString(), ticketCount: 2 }));
    orders.confirmPayment.mockRejectedValueOnce(new TypeError("offline")).mockResolvedValue(order("Paid", { paidAt: new Date(NOW).toISOString(), ticketCount: 2 }));
    render(<ResultView orderId="o1" />);
    await flush();
    await flush(1_600);

    expect(screen.getByRole("heading", { level: 1, name: "Thanh toán thành công" })).toBeInTheDocument();
  });

  it("does not ask the api before the session is known", async () => {
    auth.status = "loading";
    render(<ResultView orderId="o1" />);
    await flush(5_000);

    expect(orders.getOrder).not.toHaveBeenCalled();
    expect(orders.confirmPayment).not.toHaveBeenCalled();
  });
});
