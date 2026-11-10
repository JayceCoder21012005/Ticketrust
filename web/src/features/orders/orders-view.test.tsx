import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { OrderPage, OrderStatus, OrderSummary } from "@/lib/checkout/types";
import { ApiError } from "@/lib/errors";

const router = vi.hoisted(() => ({ replace: vi.fn(), push: vi.fn() }));
vi.mock("next/navigation", () => ({ useRouter: () => router }));

const auth = vi.hoisted(() => ({ status: "authenticated" }));
vi.mock("@/components/auth/auth-provider", () => ({ useAuth: () => ({ status: auth.status }) }));

const api = vi.hoisted(() => ({ listMyOrders: vi.fn() }));
vi.mock("@/lib/checkout/orders-api", () => api);

const { OrdersView } = await import("./orders-view");

const summary = (n: number, status: OrderStatus = "Paid", extra: Partial<OrderSummary> = {}): OrderSummary => ({
  id: `0000000${n}-aaaa-bbbb-cccc-dddddddddddd`,
  eventId: "e1",
  eventTitle: `Đêm nhạc ${n}`,
  eventSlug: "dem-nhac",
  eventStartsAt: "2026-12-20T13:00:00Z",
  status,
  total: 3_000_000 * n,
  itemCount: 2,
  createdAt: "2026-12-01T03:00:00Z",
  paidAt: status === "Paid" ? "2026-12-01T03:02:00Z" : null,
  ...extra,
});

const page = (items: OrderSummary[], nextCursor: string | null = null): OrderPage => ({ items, nextCursor });

function renderView() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <OrdersView />
    </QueryClientProvider>,
  );
}

describe("OrdersView", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    auth.status = "authenticated";
  });

  it("lists orders in the order the api gives (newest first) with event, short id, date, total, status and a link to the detail", async () => {
    api.listMyOrders.mockResolvedValue(page([summary(2), summary(1, "Cancelled")]));
    renderView();

    const rows = await screen.findAllByRole("listitem");
    expect(rows).toHaveLength(2);
    const first = within(rows[0]!);
    const link = first.getByRole("link", { name: /Đêm nhạc 2/ });
    expect(link).toHaveAttribute("href", "/account/orders/00000002-aaaa-bbbb-cccc-dddddddddddd");
    expect(first.getByText(/#00000002/)).toBeInTheDocument();
    expect(first.getByText(/01\/12\/2026/)).toBeInTheDocument();
    expect(first.getByText(/2 vé/)).toBeInTheDocument();
    expect(first.getByText(/6\.000\.000/)).toBeInTheDocument();
    expect(first.getByText("Đã thanh toán")).toBeInTheDocument();
    expect(within(rows[1]!).getByText("Đã huỷ")).toBeInTheDocument();
  });

  it("asks for the first page without a cursor and focuses the heading when the list has loaded", async () => {
    api.listMyOrders.mockResolvedValue(page([summary(1)]));
    renderView();

    const heading = await screen.findByRole("heading", { level: 1, name: "Đơn hàng của tôi" });
    await waitFor(() => expect(heading).toHaveFocus());
    expect(api.listMyOrders.mock.calls[0]![0]).toBeUndefined();
  });

  it("loads more with the cursor the api returned, appends the rows and announces the new total", async () => {
    const user = userEvent.setup();
    api.listMyOrders.mockResolvedValueOnce(page([summary(3), summary(2)], "cursor-2")).mockResolvedValueOnce(page([summary(1)], null));
    renderView();

    await user.click(await screen.findByRole("button", { name: "Xem thêm" }));

    await waitFor(() => expect(screen.getAllByRole("listitem")).toHaveLength(3));
    expect(api.listMyOrders.mock.calls[1]![0]).toBe("cursor-2");
    expect(screen.queryByRole("button", { name: "Xem thêm" })).not.toBeInTheDocument();
    expect(screen.getByRole("status")).toHaveTextContent("Đã tải thêm 1 đơn hàng, đang hiển thị 3.");
    // The button is gone, so focus moves to the first new row instead of falling back to the page.
    await waitFor(() => expect(within(screen.getAllByRole("listitem")[2]!).getByRole("link", { name: /Đêm nhạc 1/ })).toHaveFocus());
  });

  it("offers no 'load more' on a single page", async () => {
    api.listMyOrders.mockResolvedValue(page([summary(1)]));
    renderView();
    await screen.findByRole("listitem");
    expect(screen.queryByRole("button", { name: "Xem thêm" })).not.toBeInTheDocument();
  });

  it("keeps the rows and offers a retry when loading more fails", async () => {
    const user = userEvent.setup();
    api.listMyOrders.mockResolvedValueOnce(page([summary(2)], "c2")).mockRejectedValueOnce(new ApiError(503, { code: "system.busy" }));
    renderView();

    await user.click(await screen.findByRole("button", { name: "Xem thêm" }));

    expect(await screen.findByRole("alert")).toHaveTextContent("Không tải thêm được đơn hàng");
    expect(screen.getAllByRole("listitem")).toHaveLength(1);

    api.listMyOrders.mockResolvedValueOnce(page([summary(1)]));
    await user.click(screen.getByRole("button", { name: "Xem thêm" }));
    await waitFor(() => expect(screen.getAllByRole("listitem")).toHaveLength(2));
    expect(api.listMyOrders.mock.calls[2]![0]).toBe("c2");
  });

  it("shows the empty state with a way to the events", async () => {
    api.listMyOrders.mockResolvedValue(page([]));
    renderView();

    expect(await screen.findByText("Bạn chưa có đơn hàng nào")).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Khám phá sự kiện" })).toHaveAttribute("href", "/events");
    expect(screen.queryByRole("list")).not.toBeInTheDocument();
  });

  it("shows an error with a retry that loads again", async () => {
    const user = userEvent.setup();
    api.listMyOrders.mockRejectedValueOnce(new ApiError(500, {})).mockResolvedValueOnce(page([summary(1)]));
    renderView();

    expect(await screen.findByText("Không tải được danh sách đơn hàng.")).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Thử lại" }));

    expect(await screen.findByRole("listitem")).toBeInTheDocument();
  });

  it("shows a busy skeleton with a spoken status while loading, and does not fetch before sign-in is known", () => {
    auth.status = "loading";
    renderView();

    expect(screen.getByRole("status")).toHaveTextContent("Đang tải đơn hàng…");
    expect(api.listMyOrders).not.toHaveBeenCalled();
  });

  it("sends a signed-out visitor to login", () => {
    auth.status = "anonymous";
    renderView();
    expect(router.replace).toHaveBeenCalledWith("/login?returnUrl=%2Faccount%2Forders");
  });

  describe("open orders", () => {
    it.each<OrderStatus>(["Pending", "AwaitingPayment", "PaymentFailed"])("%s has a link to continue paying", async (status) => {
      api.listMyOrders.mockResolvedValue(page([summary(1, status)]));
      renderView();

      const pay = await screen.findByRole("link", { name: /Tiếp tục thanh toán/ });
      expect(pay).toHaveAttribute("href", "/checkout/00000001-aaaa-bbbb-cccc-dddddddddddd");
    });

    it.each<OrderStatus>(["Paid", "Cancelled", "Expired", "RefundPending", "Refunded"])("%s has none", async (status) => {
      api.listMyOrders.mockResolvedValue(page([summary(1, status)]));
      renderView();

      await screen.findByRole("listitem");
      expect(screen.queryByRole("link", { name: /Tiếp tục thanh toán/ })).not.toBeInTheDocument();
    });

    it("names the order in the pay link for screen readers, since several rows may be open", async () => {
      api.listMyOrders.mockResolvedValue(page([summary(1, "Pending"), summary(2, "Pending")]));
      renderView();

      const links = await screen.findAllByRole("link", { name: /Tiếp tục thanh toán/ });
      expect(links.map((l) => l.textContent)).toEqual([expect.stringContaining("#00000001"), expect.stringContaining("#00000002")]);
    });
  });
});
