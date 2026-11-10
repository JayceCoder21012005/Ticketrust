import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { Order, OrderStatus } from "@/lib/checkout/types";
import { ApiError } from "@/lib/errors";

const router = vi.hoisted(() => ({ replace: vi.fn(), push: vi.fn() }));
vi.mock("next/navigation", () => ({ useRouter: () => router }));

const auth = vi.hoisted(() => ({ status: "authenticated" }));
vi.mock("@/components/auth/auth-provider", () => ({ useAuth: () => ({ status: auth.status }) }));

const api = vi.hoisted(() => ({ getOrder: vi.fn() }));
vi.mock("@/lib/checkout/orders-api", () => api);

const { OrderDetailView } = await import("./order-detail-view");

const NOW = Date.parse("2026-12-01T10:00:00Z");

const order = (status: OrderStatus, extra: Partial<Order> = {}): Order => ({
  id: "0f3a9c1e-aaaa-bbbb-cccc-dddddddddddd",
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
  expiresAt: new Date(NOW + 480_000).toISOString(),
  serverNow: new Date(NOW).toISOString(),
  createdAt: "2026-12-01T03:00:00Z",
  paidAt: status === "Paid" || status === "RefundPending" || status === "Refunded" ? "2026-12-01T03:02:00Z" : null,
  refundReason: null,
  ticketCount: status === "Paid" ? 4 : null,
  ...extra,
});

function renderView(client = new QueryClient({ defaultOptions: { queries: { retry: false } } })) {
  return render(
    <QueryClientProvider client={client}>
      <OrderDetailView orderId="0f3a9c1e-aaaa-bbbb-cccc-dddddddddddd" />
    </QueryClientProvider>,
  );
}

async function show(status: OrderStatus, extra?: Partial<Order>) {
  api.getOrder.mockResolvedValue(order(status, extra));
  renderView();
  return screen.findByRole("heading", { level: 1 });
}

const timeline = () => within(screen.getByRole("list", { name: "Các bước của đơn hàng" }));
const stepLabels = () => timeline().getAllByRole("listitem").map((li) => li.querySelector("[data-step-title]")?.textContent);

describe("OrderDetailView", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    auth.status = "authenticated";
    localStorage.clear();
  });

  it("shows the order: code, status, seats grouped by section, general admission, total and the buyer snapshot", async () => {
    const heading = await show("Paid");

    expect(heading).toHaveTextContent("Đơn hàng #0F3A9C1E");
    await waitFor(() => expect(heading).toHaveFocus());
    expect(screen.getAllByText("Đã thanh toán").length).toBeGreaterThan(0);
    expect(screen.getByRole("heading", { name: "Khu A" })).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Khu B" })).toBeInTheDocument();
    expect(screen.getByText(/5\.800\.000/)).toBeInTheDocument();
    const buyer = within(screen.getByRole("region", { name: "Người mua" }));
    expect(buyer.getByText("Nguyễn An")).toBeInTheDocument();
    expect(buyer.getByText("an@example.com")).toBeInTheDocument();
    expect(buyer.getByText("0901234567")).toBeInTheDocument();
    expect(api.getOrder).toHaveBeenCalledWith("0f3a9c1e-aaaa-bbbb-cccc-dddddddddddd");
  });

  it("names the event from the list cache, else from the remembered checkout event, else 'Sự kiện'", async () => {
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    client.setQueryData(["my-orders"], {
      pages: [{ items: [{ id: "0f3a9c1e-aaaa-bbbb-cccc-dddddddddddd", eventId: "e1", eventTitle: "Đêm nhạc A", eventSlug: "dem-nhac-a" }], nextCursor: null }],
      pageParams: [undefined],
    });
    api.getOrder.mockResolvedValue(order("Paid"));
    renderView(client);

    expect(await screen.findByText("Đêm nhạc A")).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Xem sự kiện" })).toHaveAttribute("href", "/events/dem-nhac-a");
  });

  it("falls back to the remembered event, and without any memory to a generic name and no event link", async () => {
    localStorage.setItem("tr.checkout-events", JSON.stringify([{ id: "e1", slug: "su-kien", title: "Đêm nhạc B" }]));
    await show("Paid");
    expect(await screen.findByText("Đêm nhạc B")).toBeInTheDocument();
  });

  it("uses a generic event name when nothing is known", async () => {
    await show("Paid");
    expect(screen.getByText("Sự kiện")).toBeInTheDocument();
    expect(screen.queryByRole("link", { name: "Xem sự kiện" })).not.toBeInTheDocument();
  });

  describe("status timeline", () => {
    it("is an ordered list whose current step is marked with aria-current and spelled out in words", async () => {
      await show("AwaitingPayment");

      expect(timeline().getAllByRole("listitem")).toHaveLength(3);
      expect(stepLabels()).toEqual(["Đặt chỗ", "Chờ thanh toán", "Đã thanh toán"]);
      const current = timeline().getAllByRole("listitem").filter((li) => li.getAttribute("aria-current") === "step");
      expect(current).toHaveLength(1);
      expect(current[0]).toHaveTextContent("Chờ thanh toán");
      expect(current[0]).toHaveTextContent("bước hiện tại");
      expect(timeline().getAllByRole("listitem")[0]).toHaveTextContent("đã qua");
      expect(timeline().getAllByRole("listitem")[2]).toHaveTextContent("sắp tới");
      expect(screen.getByRole("list", { name: "Các bước của đơn hàng" }).tagName).toBe("OL");
    });

    it.each<[OrderStatus, string[], string]>([
      ["Pending", ["Đặt chỗ", "Chờ thanh toán", "Đã thanh toán"], "Đặt chỗ"],
      ["PaymentFailed", ["Đặt chỗ", "Chờ thanh toán", "Thanh toán thất bại", "Đã thanh toán"], "Thanh toán thất bại"],
      ["Paid", ["Đặt chỗ", "Chờ thanh toán", "Đã thanh toán"], "Đã thanh toán"],
      ["Cancelled", ["Đặt chỗ", "Đã huỷ"], "Đã huỷ"],
      ["Expired", ["Đặt chỗ", "Hết hạn"], "Hết hạn"],
      ["RefundPending", ["Đặt chỗ", "Chờ thanh toán", "Đã thanh toán", "Đang hoàn tiền", "Đã hoàn tiền"], "Đang hoàn tiền"],
      ["Refunded", ["Đặt chỗ", "Chờ thanh toán", "Đã thanh toán", "Đang hoàn tiền", "Đã hoàn tiền"], "Đã hoàn tiền"],
    ])("%s", async (status, labels, currentLabel) => {
      await show(status);

      expect(stepLabels()).toEqual(labels);
      const current = timeline().getAllByRole("listitem").find((li) => li.getAttribute("aria-current") === "step");
      expect(current?.querySelector("[data-step-title]")?.textContent).toBe(currentLabel);
    });

    it("stamps the creation and payment times the api returns (Vietnam time) and nothing it does not", async () => {
      await show("Paid");

      const items = timeline().getAllByRole("listitem");
      expect(items[0]).toHaveTextContent("10:00 01/12/2026");
      expect(items[1]).not.toHaveTextContent(/\d{2}:\d{2}/);
      expect(items[2]).toHaveTextContent("10:02 01/12/2026");
    });

    it("explains the failed, cancelled and expired ends", async () => {
      await show("PaymentFailed");
      expect(timeline().getByText(/Bạn có thể thử lại cho đến hết giờ thanh toán/)).toBeInTheDocument();
    });

    it("explains a refund with the reason the api gives and no promise of days", async () => {
      await show("RefundPending", { refundReason: "seats_unavailable" });

      const refunding = timeline().getAllByRole("listitem")[3]!;
      expect(refunding).toHaveTextContent("Thanh toán đến sau khi thời gian giữ ghế đã hết và ghế đã có người khác mua.");
      expect(refunding).toHaveTextContent("Thời gian tiền về tuỳ thuộc vào ngân hàng của bạn");
      expect(refunding).not.toHaveTextContent(/\d+ ngày/);
    });

    it.each(["event_cancelled", "event_ended", "purchase_limit_exceeded"])("covers the refund reason %s", async (reason) => {
      await show("Refunded", { refundReason: reason });
      expect(timeline().getAllByRole("listitem")[3]).not.toHaveTextContent("checkout.result");
    });

    it("keeps an unknown (future) refund reason on the general sentence", async () => {
      await show("RefundPending", { refundReason: "brand_new_reason" });
      expect(timeline().getAllByRole("listitem")[3]).toHaveTextContent("Đơn hàng không còn hợp lệ vào lúc thanh toán được xác nhận.");
    });
  });

  describe("actions", () => {
    it.each<OrderStatus>(["Pending", "AwaitingPayment", "PaymentFailed"])("%s can continue to payment before the deadline", async (status) => {
      await show(status);
      expect(screen.getByRole("link", { name: "Tiếp tục thanh toán" })).toHaveAttribute("href", "/checkout/0f3a9c1e-aaaa-bbbb-cccc-dddddddddddd");
    });

    it("cannot pay once the deadline has passed on server time, and says the order will be updated", async () => {
      await show("AwaitingPayment", { expiresAt: new Date(NOW - 1_000).toISOString() });
      expect(screen.queryByRole("link", { name: "Tiếp tục thanh toán" })).not.toBeInTheDocument();
      expect(screen.getByText("Đã quá hạn thanh toán, đơn hàng sẽ sớm được cập nhật.")).toBeInTheDocument();
    });

    it.each<OrderStatus>(["Cancelled", "Expired", "RefundPending", "Refunded"])("%s has no pay link", async (status) => {
      await show(status);
      expect(screen.queryByRole("link", { name: "Tiếp tục thanh toán" })).not.toBeInTheDocument();
    });

    it("a paid order shows its ticket count and a disabled 'Xem vé' while the tickets page does not exist", async () => {
      await show("Paid");

      expect(screen.getByText("Bạn có 4 vé.")).toBeInTheDocument();
      expect(screen.getByRole("button", { name: "Xem vé" })).toBeDisabled();
      expect(screen.getByText("Trang xem vé sẽ có trong thời gian tới.")).toBeInTheDocument();
    });

    it("links back to all orders", async () => {
      await show("Paid");
      expect(screen.getByRole("link", { name: "Tất cả đơn hàng" })).toHaveAttribute("href", "/account/orders");
    });
  });

  describe("states", () => {
    it("shows a busy skeleton with a status while loading", () => {
      api.getOrder.mockReturnValue(new Promise(() => undefined));
      renderView();
      expect(screen.getByRole("status")).toHaveTextContent("Đang tải đơn hàng…");
    });

    it("says the order is not found on 404, with focus on the heading and a way back", async () => {
      api.getOrder.mockRejectedValue(new ApiError(404, { code: "order.not_found" }));
      renderView();

      const heading = await screen.findByRole("heading", { name: "Không tìm thấy đơn hàng" });
      await waitFor(() => expect(heading).toHaveFocus());
      expect(screen.getByRole("link", { name: "Về đơn hàng của tôi" })).toHaveAttribute("href", "/account/orders");
    });

    it("offers a retry when loading fails", async () => {
      const user = userEvent.setup();
      api.getOrder.mockRejectedValueOnce(new ApiError(500, {})).mockResolvedValueOnce(order("Paid"));
      renderView();

      expect(await screen.findByText("Không tải được đơn hàng.")).toBeInTheDocument();
      await user.click(screen.getByRole("button", { name: "Thử lại" }));
      expect(await screen.findByRole("heading", { level: 1, name: /Đơn hàng #/ })).toBeInTheDocument();
    });

    it("sends a signed-out visitor to login and does not fetch", () => {
      auth.status = "anonymous";
      renderView();

      expect(router.replace).toHaveBeenCalledWith("/login?returnUrl=%2Faccount%2Forders%2F0f3a9c1e-aaaa-bbbb-cccc-dddddddddddd");
      expect(api.getOrder).not.toHaveBeenCalled();
    });
  });
});
