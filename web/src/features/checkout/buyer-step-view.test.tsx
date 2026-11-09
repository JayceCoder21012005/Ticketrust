import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { Order } from "@/lib/checkout/types";
import { ApiError } from "@/lib/errors";
import type { Hold } from "@/lib/seats/types";

const router = vi.hoisted(() => ({ replace: vi.fn(), push: vi.fn() }));
vi.mock("next/navigation", () => ({ useRouter: () => router }));

const auth = vi.hoisted(() => ({ status: "authenticated", user: { fullName: "Nguyễn An", email: "an@example.com", phoneNumber: "0901234567" } }));
vi.mock("@/components/auth/auth-provider", () => ({ useAuth: () => auth }));

const orders = vi.hoisted(() => ({ createOrder: vi.fn() }));
vi.mock("@/lib/checkout/orders-api", () => orders);

const holds = vi.hoisted(() => ({ getMyHold: vi.fn() }));
vi.mock("@/lib/seats/holds-api", () => holds);

// The form loads zod on its first validation (lazyResolver): warm it so the first submit is not slower than a waitFor.
await Promise.all([import("@/lib/auth/schemas"), import("@hookform/resolvers/zod")]);
const { BuyerStepView } = await import("./buyer-step-view");

const NOW = Date.now();
const event = { id: "e1", slug: "su-kien", title: "Đêm nhạc" };

const hold: Hold = {
  holdId: "h1",
  seats: [{ index: 0, section: "A", row: "1", number: "1", tierId: "vip", price: 1_500_000 }],
  ga: [{ tierId: "ga", name: "Đứng", quantity: 2, price: 500_000 }],
  total: 2_500_000,
  expiresAt: new Date(NOW + 480_000).toISOString(),
  serverNow: new Date(NOW).toISOString(),
};

const created = { id: "o9" } as Order;

function renderView() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <BuyerStepView event={event} />
    </QueryClientProvider>,
  );
}

describe("BuyerStepView", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    localStorage.clear();
    holds.getMyHold.mockResolvedValue(hold);
  });

  it("shows the hold and prefills the buyer from the profile", async () => {
    renderView();

    expect(await screen.findByRole("textbox", { name: "Họ và tên" })).toHaveValue("Nguyễn An");
    expect(screen.getByRole("textbox", { name: "Email nhận vé" })).toHaveValue("an@example.com");
    expect(screen.getByRole("textbox", { name: /Số điện thoại/ })).toHaveValue("0901234567");
    expect(screen.getByText("Hàng 1 · Ghế 1")).toBeInTheDocument();
    expect(screen.getByText(/2\.500\.000/)).toBeInTheDocument();
    expect(holds.getMyHold).toHaveBeenCalledWith("e1");
  });

  it("creates the order with the buyer, then goes to its checkout (201 and 200 alike)", async () => {
    orders.createOrder.mockResolvedValue(created);
    const user = userEvent.setup();
    renderView();

    await user.click(await screen.findByRole("button", { name: "Tạo đơn hàng" }));

    await waitFor(() => expect(router.push).toHaveBeenCalledWith("/checkout/o9"));
    expect(orders.createOrder).toHaveBeenCalledWith(
      { eventId: "e1", holdId: "h1", buyer: { name: "Nguyễn An", email: "an@example.com", phone: "0901234567" } },
      expect.stringMatching(/^[0-9a-f-]{36}$/),
    );
    expect(JSON.parse(localStorage.getItem("tr.checkout-events")!)).toEqual([event]);
  });

  it("sends a blank phone as null and validates the email", async () => {
    orders.createOrder.mockResolvedValue(created);
    const user = userEvent.setup();
    renderView();
    const phone = await screen.findByRole("textbox", { name: /Số điện thoại/ });
    await user.clear(phone);
    const email = screen.getByRole("textbox", { name: "Email nhận vé" });
    await user.clear(email);
    await user.type(email, "khong-hop-le");

    await user.click(screen.getByRole("button", { name: "Tạo đơn hàng" }));
    expect(await screen.findByText("Email không hợp lệ.")).toBeInTheDocument();
    expect(orders.createOrder).not.toHaveBeenCalled();

    await user.clear(email);
    await user.type(email, "an@example.com");
    await user.click(screen.getByRole("button", { name: "Tạo đơn hàng" }));
    await waitFor(() => expect(orders.createOrder).toHaveBeenCalled());
    expect(orders.createOrder.mock.calls[0][0].buyer.phone).toBeNull();
  });

  it("keeps the key for a retry of the same order after a network failure, and renews it after a refusal", async () => {
    orders.createOrder
      .mockRejectedValueOnce(new TypeError("offline"))
      .mockRejectedValueOnce(new ApiError(400, { code: "validation.failed" }))
      .mockResolvedValue(created);
    const user = userEvent.setup();
    renderView();

    for (let attempt = 1; attempt <= 3; attempt++) {
      await user.click(await screen.findByRole("button", { name: "Tạo đơn hàng" }));
      await waitFor(() => expect(orders.createOrder).toHaveBeenCalledTimes(attempt));
      await waitFor(() => expect(screen.queryByRole("button", { name: "Đang tạo đơn…" })).not.toBeInTheDocument());
    }

    const keys = orders.createOrder.mock.calls.map((call) => call[1] as string);
    expect(keys[1]).toBe(keys[0]);
    expect(keys[2]).not.toBe(keys[1]);
  });

  it.each(["order.hold_expired", "order.hold_invalid"])("sends the buyer back to the seats on %s", async (code) => {
    orders.createOrder.mockRejectedValue(new ApiError(409, { code }));
    const user = userEvent.setup();
    renderView();

    await user.click(await screen.findByRole("button", { name: "Tạo đơn hàng" }));

    expect(await screen.findByRole("alert")).toHaveTextContent("Lượt giữ chỗ không còn hiệu lực");
    expect(screen.getByRole("link", { name: "Quay lại chọn ghế" })).toHaveAttribute("href", "/events/su-kien/seats");
  });

  it("shows another error without leaving", async () => {
    orders.createOrder.mockRejectedValue(new ApiError(503, { code: "system.busy" }));
    const user = userEvent.setup();
    renderView();

    await user.click(await screen.findByRole("button", { name: "Tạo đơn hàng" }));

    expect(await screen.findByRole("alert")).toHaveTextContent("Hệ thống đang bận");
    expect(router.push).not.toHaveBeenCalled();
  });

  it("explains that there is nothing to pay for without a hold", async () => {
    holds.getMyHold.mockResolvedValue(null);
    renderView();

    expect(await screen.findByRole("heading", { name: "Bạn chưa giữ chỗ nào" })).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Chọn ghế" })).toHaveAttribute("href", "/events/su-kien/seats");
  });
});
