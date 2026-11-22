import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import type { ReactNode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { Order, OrderStatus } from "@/lib/checkout/types";
import { ApiError } from "@/lib/errors";

const router = vi.hoisted(() => ({ replace: vi.fn(), push: vi.fn() }));
vi.mock("next/navigation", () => ({ useRouter: () => router }));

const auth = vi.hoisted(() => ({ status: "authenticated" }));
vi.mock("@/components/auth/auth-provider", () => ({ useAuth: () => ({ status: auth.status }) }));

const orders = vi.hoisted(() => ({ getOrder: vi.fn(), payOrder: vi.fn(), cancelOrder: vi.fn() }));
vi.mock("@/lib/checkout/orders-api", () => orders);

const navigation = vi.hoisted(() => ({ navigateTo: vi.fn() }));
vi.mock("@/lib/checkout/navigate", () => navigation);

const { CheckoutView } = await import("./checkout-view");

const NOW = Date.parse("2026-12-01T10:00:00Z");

const order = (status: OrderStatus = "Pending", expiresInMs = 480_000): Order => ({
  id: "o1",
  eventId: "e1",
  status,
  seats: [
    { index: 0, section: "A", row: "1", number: "1", tierId: "vip", price: 1_500_000 },
    { index: 1, section: "A", row: "1", number: "2", tierId: "vip", price: 1_500_000 },
    { index: 40, section: "B", row: "3", number: "7", tierId: "std", price: 800_000 },
  ],
  ga: [{ tierId: "ga", name: "Đứng", quantity: 2, price: 500_000 }],
  total: 5_800_000,
  buyer: { name: "Nguyễn An", email: "an@example.com", phone: "0901234567" },
  expiresAt: new Date(NOW + expiresInMs).toISOString(),
  serverNow: new Date(NOW).toISOString(),
  createdAt: new Date(NOW).toISOString(),
  paidAt: null,
  refundReason: null,
  ticketCount: null,
});

const redirect = { redirectUrl: "https://pay.test/s/1", attemptId: "a1", expiresAt: "x", serverNow: "y" };

function renderView(node: ReactNode = <CheckoutView orderId="o1" />) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(<QueryClientProvider client={client}>{node}</QueryClientProvider>);
}

const payButton = () => screen.findByRole("button", { name: /^Thanh toán$|Đang chuyển/ });
const acceptTerms = async (user: ReturnType<typeof userEvent.setup>) => user.click(await screen.findByRole("checkbox", { name: /điều khoản/ }));
const rememberEvent = () => localStorage.setItem("tr.checkout-events", JSON.stringify([{ id: "e1", slug: "su-kien", title: "Đêm nhạc" }]));

describe("CheckoutView", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    auth.status = "authenticated";
    localStorage.clear();
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it("shows the order: seats grouped by section, general admission, the total, the buyer and the countdown", async () => {
    orders.getOrder.mockResolvedValue(order());
    renderView();

    expect(await screen.findByRole("heading", { level: 1, name: /Thanh toán/ })).toBeInTheDocument();
    const summary = screen.getByRole("region", { name: "Tóm tắt đơn hàng" });
    expect(within(summary).getByRole("heading", { name: "Khu A" })).toBeInTheDocument();
    expect(within(summary).getByRole("heading", { name: "Khu B" })).toBeInTheDocument();
    expect(within(summary).getByText("Hàng 1 · Ghế 1")).toBeInTheDocument();
    expect(within(summary).getByText("Đứng × 2")).toBeInTheDocument();
    expect(within(summary).getByText(/5\.800\.000/)).toBeInTheDocument();
    expect(screen.getByText("Nguyễn An")).toBeInTheDocument();
    expect(screen.getByText("an@example.com")).toBeInTheDocument();
    expect(screen.getByText("0901234567")).toBeInTheDocument();
    await waitFor(() => expect(screen.getByRole("timer")).toHaveTextContent("08:00"));
  });

  it("says payment goes through MoMo's test environment only when MoMo is the provider", async () => {
    orders.getOrder.mockResolvedValue(order());
    renderView();
    await payButton();
    expect(screen.queryByRole("note")).not.toBeInTheDocument();

    vi.stubEnv("NEXT_PUBLIC_PAYMENT_PROVIDER", "momo");
    try {
      renderView();
      const note = await screen.findByRole("note");
      expect(note).toHaveTextContent(/MoMo/);
      const link = within(note).getByRole("link", { name: "Xem thẻ test" });
      expect(link).toHaveAttribute("href", "https://developers.momo.vn/v3/docs/payment/onboarding/test-instructions/");
      expect(link).toHaveAttribute("target", "_blank");
      expect(link).toHaveAttribute("rel", "noopener noreferrer");
    } finally {
      vi.unstubAllEnvs();
    }
  });

  it("names the event when the seat flow left it behind", async () => {
    rememberEvent();
    orders.getOrder.mockResolvedValue(order());
    renderView();

    expect(await screen.findByText(/Đêm nhạc/)).toBeInTheDocument();
  });

  it("counts down on server time and, at zero, replaces the form with the expired state", async () => {
    vi.useFakeTimers({ now: NOW, toFake: ["Date", "setTimeout", "clearTimeout", "setInterval", "clearInterval"] });
    orders.getOrder.mockResolvedValue(order("Pending", 3_000));
    renderView();
    await act(() => vi.advanceTimersByTimeAsync(50));
    expect(screen.getByRole("timer")).toHaveTextContent("00:03");

    await act(() => vi.advanceTimersByTimeAsync(3_000));

    expect(screen.getByRole("heading", { name: "Hết thời gian thanh toán" })).toHaveFocus();
    expect(screen.queryByRole("button", { name: "Thanh toán" })).not.toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Xem sự kiện" })).toHaveAttribute("href", "/events");
  });

  it("links the expired state back to the seats when the event is known", async () => {
    rememberEvent();
    orders.getOrder.mockResolvedValue(order());
    orders.payOrder.mockRejectedValue(new ApiError(409, { code: "order.hold_expired" }));
    const user = userEvent.setup();
    renderView();
    await acceptTerms(user);
    await user.click(await payButton());

    expect(await screen.findByRole("heading", { name: "Hết thời gian thanh toán" })).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Chọn lại ghế" })).toHaveAttribute("href", "/events/su-kien/seats");
  });

  it("asks for the terms before paying and sends nothing without them", async () => {
    orders.getOrder.mockResolvedValue(order());
    const user = userEvent.setup();
    renderView();

    await user.click(await payButton());

    expect(await screen.findByText("Vui lòng đồng ý với điều khoản để thanh toán.")).toBeInTheDocument();
    expect(screen.getByRole("checkbox", { name: /điều khoản/ })).toHaveFocus();
    expect(orders.payOrder).not.toHaveBeenCalled();
  });

  it("pays with an Idempotency-Key and goes to the gateway, staying disabled meanwhile", async () => {
    orders.getOrder.mockResolvedValue(order());
    orders.payOrder.mockResolvedValue(redirect);
    const user = userEvent.setup();
    renderView();
    await acceptTerms(user);

    await user.click(await payButton());

    expect(orders.payOrder).toHaveBeenCalledWith("o1", expect.stringMatching(/^[0-9a-f-]{36}$/));
    expect(navigation.navigateTo).toHaveBeenCalledWith("https://pay.test/s/1");
    expect(await screen.findByRole("button", { name: /Đang chuyển đến cổng thanh toán/ })).toBeDisabled();
  });

  it("sends one request for a double click", async () => {
    orders.getOrder.mockResolvedValue(order());
    orders.payOrder.mockReturnValue(new Promise(() => undefined));
    const user = userEvent.setup();
    renderView();
    await acceptTerms(user);

    await user.dblClick(await payButton());

    expect(orders.payOrder).toHaveBeenCalledTimes(1);
  });

  it("reuses the key after a network failure or a 503, but uses a fresh one after a stored refusal", async () => {
    orders.getOrder.mockResolvedValue(order());
    orders.payOrder
      .mockRejectedValueOnce(new TypeError("Failed to fetch"))
      .mockRejectedValueOnce(new ApiError(503, { code: "payment.gateway_unavailable" }))
      .mockRejectedValueOnce(new ApiError(409, { code: "payment.rejected" }))
      .mockResolvedValue(redirect);
    const user = userEvent.setup();
    renderView();
    await acceptTerms(user);

    for (let attempt = 1; attempt <= 4; attempt++) {
      await user.click(await screen.findByRole("button", { name: "Thanh toán" }));
      await waitFor(() => expect(orders.payOrder).toHaveBeenCalledTimes(attempt));
      await screen.findByRole("button", { name: attempt < 4 ? "Thanh toán" : /Đang chuyển/ });
    }

    const keys = orders.payOrder.mock.calls.map((call) => call[1] as string);
    expect(keys[1]).toBe(keys[0]);
    expect(keys[2]).toBe(keys[0]);
    expect(keys[3]).not.toBe(keys[2]);
  });

  it("says the gateway is down on a 503 and lets the buyer try again", async () => {
    orders.getOrder.mockResolvedValue(order());
    orders.payOrder.mockRejectedValue(new ApiError(503, { code: "payment.gateway_unavailable" }));
    const user = userEvent.setup();
    renderView();
    await acceptTerms(user);

    await user.click(await payButton());

    expect(await screen.findByRole("alert")).toHaveTextContent("Cổng thanh toán tạm thời gián đoạn, thử lại sau");
    expect(screen.getByRole("button", { name: "Thanh toán" })).toBeEnabled();
  });

  it.each([
    ["payment.rejected", 409, "Cổng thanh toán từ chối giao dịch"],
    ["system.busy", 503, "Hệ thống đang bận"],
  ])("explains %s", async (code, status, text) => {
    orders.getOrder.mockResolvedValue(order());
    orders.payOrder.mockRejectedValue(new ApiError(status, { code }));
    const user = userEvent.setup();
    renderView();
    await acceptTerms(user);

    await user.click(await payButton());

    expect(await screen.findByRole("alert")).toHaveTextContent(text);
  });

  it.each(["order.expired", "order.hold_expired"])("shows the expired state on %s", async (code) => {
    orders.getOrder.mockResolvedValue(order());
    orders.payOrder.mockRejectedValue(new ApiError(409, { code }));
    const user = userEvent.setup();
    renderView();
    await acceptTerms(user);

    await user.click(await payButton());

    expect(await screen.findByRole("heading", { name: "Hết thời gian thanh toán" })).toBeInTheDocument();
  });

  it("reloads the order on order.not_payable and moves on when it is no longer open", async () => {
    orders.getOrder.mockResolvedValueOnce(order("Pending")).mockResolvedValue(order("Paid"));
    orders.payOrder.mockRejectedValue(new ApiError(409, { code: "order.not_payable" }));
    const user = userEvent.setup();
    renderView();
    await acceptTerms(user);

    await user.click(await payButton());

    await waitFor(() => expect(router.replace).toHaveBeenCalledWith("/checkout/o1/result"));
  });

  it("reloads the order once on order.not_payable, even when it still reads as payable", async () => {
    orders.getOrder.mockResolvedValue(order("Pending"));
    orders.payOrder.mockRejectedValue(new ApiError(409, { code: "order.not_payable" }));
    const user = userEvent.setup();
    renderView();
    await acceptTerms(user);

    await user.click(await payButton());
    await screen.findByRole("alert");
    await new Promise((resolve) => setTimeout(resolve, 300));

    expect(orders.getOrder).toHaveBeenCalledTimes(2);
  });

  it("does not hand a gateway address with a script scheme to the browser", async () => {
    orders.getOrder.mockResolvedValue(order());
    orders.payOrder.mockResolvedValue({ ...redirect, redirectUrl: "javascript:alert(1)" });
    const user = userEvent.setup();
    renderView();
    await acceptTerms(user);

    await user.click(await payButton());

    expect(await screen.findByRole("alert")).toHaveTextContent("Không mở được cổng thanh toán");
    expect(navigation.navigateTo).not.toHaveBeenCalled();
    expect(screen.getByRole("button", { name: "Thanh toán" })).toBeEnabled();
  });

  it("frees the pay button when the page comes back from the back/forward cache", async () => {
    orders.getOrder.mockResolvedValue(order());
    orders.payOrder.mockResolvedValue(redirect);
    const user = userEvent.setup();
    renderView();
    await acceptTerms(user);
    await user.click(await payButton());
    expect(await screen.findByRole("button", { name: /Đang chuyển/ })).toBeDisabled();

    act(() => {
      const event = new Event("pageshow");
      Object.defineProperty(event, "persisted", { value: true });
      window.dispatchEvent(event);
    });

    expect(await screen.findByRole("button", { name: "Thanh toán" })).toBeEnabled();
  });

  it("offers cancelling only while the order is Pending", async () => {
    orders.getOrder.mockResolvedValue(order("AwaitingPayment"));
    renderView();

    await payButton();

    expect(screen.queryByRole("button", { name: "Huỷ đơn" })).not.toBeInTheDocument();
  });

  it("cancels a Pending order after confirming and shows the result", async () => {
    orders.getOrder.mockResolvedValue(order("Pending"));
    orders.cancelOrder.mockResolvedValue(order("Cancelled"));
    const user = userEvent.setup();
    renderView();

    await user.click(await screen.findByRole("button", { name: "Huỷ đơn" }));
    const dialog = await screen.findByRole("alertdialog");
    await user.click(within(dialog).getByRole("button", { name: "Huỷ đơn" }));

    await waitFor(() => expect(orders.cancelOrder).toHaveBeenCalledWith("o1"));
    await waitFor(() => expect(router.replace).toHaveBeenCalledWith("/checkout/o1/result"));
  });

  it("keeps the order when the buyer backs out of cancelling", async () => {
    orders.getOrder.mockResolvedValue(order("Pending"));
    const user = userEvent.setup();
    renderView();

    await user.click(await screen.findByRole("button", { name: "Huỷ đơn" }));
    await user.click(within(await screen.findByRole("alertdialog")).getByRole("button", { name: "Giữ đơn" }));

    expect(orders.cancelOrder).not.toHaveBeenCalled();
  });

  it("reminds the buyer that the last payment failed and still lets them pay", async () => {
    orders.getOrder.mockResolvedValue(order("PaymentFailed"));
    renderView();

    expect(await screen.findByText(/Lần thanh toán trước không thành công/)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Thanh toán" })).toBeEnabled();
    expect(screen.queryByRole("button", { name: "Huỷ đơn" })).not.toBeInTheDocument();
  });

  it.each(["Paid", "Cancelled", "Expired", "RefundPending", "Refunded"] as const)("hands a %s order to the result page", async (status) => {
    orders.getOrder.mockResolvedValue(order(status));
    renderView();

    await waitFor(() => expect(router.replace).toHaveBeenCalledWith("/checkout/o1/result"));
  });

  it("says so when the order does not exist", async () => {
    orders.getOrder.mockRejectedValue(new ApiError(404, { code: "order.not_found" }));
    renderView();

    expect(await screen.findByRole("heading", { name: "Không tìm thấy đơn hàng" })).toBeInTheDocument();
  });

  it("offers a retry when the order cannot be loaded", async () => {
    orders.getOrder.mockRejectedValueOnce(new TypeError("offline")).mockResolvedValue(order());
    const user = userEvent.setup();
    renderView();

    await user.click(await screen.findByRole("button", { name: "Thử lại" }));

    expect(await payButton()).toBeInTheDocument();
  });
});
