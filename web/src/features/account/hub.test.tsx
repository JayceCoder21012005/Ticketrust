import { render, screen, within } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

const nav = vi.hoisted(() => ({ pathname: "/account" }));
vi.mock("next/navigation", () => ({ usePathname: () => nav.pathname, useRouter: () => ({ replace: vi.fn(), push: vi.fn() }) }));

const auth = vi.hoisted(() => ({
  status: "authenticated" as "authenticated" | "anonymous" | "loading",
  user: { id: "u1", email: "an@example.com", fullName: "Nguyễn An", phoneNumber: null, avatarUrl: null as string | null, emailConfirmed: true, roles: ["Customer"] },
}));
vi.mock("@/components/auth/auth-provider", () => ({ useAuth: () => ({ status: auth.status, user: auth.user }) }));

const data = vi.hoisted(() => ({ tickets: vi.fn(), orders: vi.fn(), follows: vi.fn() }));
vi.mock("@/lib/tickets/api", async (original) => ({ ...(await original<object>()), getMyTickets: data.tickets }));
vi.mock("@/lib/checkout/orders-api", async (original) => ({ ...(await original<object>()), listMyOrders: data.orders }));
vi.mock("./hub-api", () => ({ getMyFollows: data.follows }));

import { AccountNav } from "./account-nav";
import { AccountOverview } from "./overview";

const group = (slug: string, startsAt: string) => ({
  eventId: slug,
  slug,
  title: `Sự kiện ${slug}`,
  coverImageUrl: null,
  startsAt,
  endsAt: startsAt,
  venueName: "Nhà hát",
  city: "Hà Nội",
  status: "Published",
  tickets: [{ id: `${slug}-t1`, orderId: "o1", tierName: "VIP", seat: null, status: "Valid", holderName: "Nguyễn An" }],
});

const order = (id: string) => ({
  id,
  eventId: "e",
  eventTitle: `Đơn ${id}`,
  eventSlug: "e",
  eventStartsAt: "2026-12-31T13:00:00Z",
  status: "Paid",
  total: 1_500_000,
  itemCount: 2,
  createdAt: "2026-11-01T03:00:00Z",
  paidAt: "2026-11-01T03:01:00Z",
});

beforeEach(() => {
  data.tickets.mockResolvedValue([group("soonest", "2026-12-01T13:00:00Z"), group("later", "2026-12-20T13:00:00Z")]);
  data.orders.mockResolvedValue({ items: [order("a"), order("b"), order("c")], nextCursor: "x" });
  data.follows.mockResolvedValue([]);
});

afterEach(() => {
  auth.user.emailConfirmed = true;
  vi.clearAllMocks();
});

describe("AccountNav", () => {
  it.each([
    ["/account", "Tổng quan"],
    ["/account/tickets", "Vé của tôi"],
    ["/account/orders/123", "Đơn hàng"],
    ["/account/waitlist", "Danh sách chờ"],
    ["/account/notifications", "Thông báo"],
    ["/account/profile", "Hồ sơ"],
  ])("marks %s as the current page", (pathname, label) => {
    nav.pathname = pathname;
    render(<AccountNav />);

    const links = within(screen.getByRole("navigation", { name: "Tài khoản" })).getAllByRole("link");
    expect(links.map((l) => l.textContent)).toEqual(["Tổng quan", "Vé của tôi", "Đơn hàng", "Danh sách chờ", "Thông báo", "Hồ sơ"]);
    expect(screen.getByRole("link", { name: label })).toHaveAttribute("aria-current", "page");
    expect(links.filter((l) => l.getAttribute("aria-current") === "page")).toHaveLength(1);
  });
});

describe("AccountOverview", () => {
  it("greets the buyer and leads with their next event and its ticket", async () => {
    render(<AccountOverview />);

    expect(screen.getByRole("heading", { level: 1, name: /Nguyễn An/ })).toBeInTheDocument();
    const next = await screen.findByRole("region", { name: "Sự kiện sắp tới của bạn" });
    expect(within(next).getByRole("heading", { name: "Sự kiện soonest" })).toBeInTheDocument();
    expect(within(next).getByRole("link", { name: /Mở vé/ })).toHaveAttribute("href", "/tickets/soonest-t1");
  });

  it("shows the three latest orders with a link to all of them", async () => {
    render(<AccountOverview />);

    const orders = await screen.findByRole("region", { name: "Đơn hàng gần đây" });
    expect(within(orders).getAllByRole("link", { name: /Đơn [abc]/ })).toHaveLength(3);
    expect(within(orders).getByRole("link", { name: "Xem tất cả" })).toHaveAttribute("href", "/account/orders");
    expect(data.orders).toHaveBeenCalledWith(undefined, 3, expect.anything());
  });

  it("says what to do next when there is nothing yet", async () => {
    data.tickets.mockResolvedValue([]);
    data.orders.mockResolvedValue({ items: [], nextCursor: null });
    render(<AccountOverview />);

    expect(await screen.findByText("Bạn chưa có vé cho sự kiện nào sắp diễn ra.")).toBeInTheDocument();
    expect(await screen.findByText("Chưa có đơn hàng nào.")).toBeInTheDocument();
    expect(await screen.findByText("Bạn chưa theo dõi sự kiện nào.")).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Khám phá sự kiện" })).toHaveAttribute("href", "/events");
  });

  it("lists the followed events", async () => {
    data.follows.mockResolvedValue([
      { id: "f", slug: "followed", title: "Sự kiện followed", category: "concert", startsAt: "2026-12-31T13:00:00Z", venueName: "V", city: "C", coverImageUrl: null, priceFrom: 1, nextSaleOpensAt: null, isOnSale: true, isSoldOut: false },
    ]);
    render(<AccountOverview />);

    const follows = await screen.findByRole("region", { name: "Bạn đang theo dõi" });
    expect(within(follows).getByRole("link", { name: /Sự kiện followed/ })).toHaveAttribute("href", "/events/followed");
  });

  it("asks to confirm the email when it is not confirmed yet", () => {
    auth.user.emailConfirmed = false;
    render(<AccountOverview />);

    expect(screen.getByRole("status")).toHaveTextContent(/Email chưa được xác nhận/);
  });
});
