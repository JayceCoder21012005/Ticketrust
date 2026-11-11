import { act, render, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { AuthProvider } from "@/components/auth/auth-provider";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { clearSession, setSession, type Session } from "@/lib/api/session";
import type { Order, OrderStatus } from "@/lib/checkout/types";

const router = vi.hoisted(() => ({ replace: vi.fn(), push: vi.fn() }));
vi.mock("next/navigation", () => ({ useRouter: () => router }));

const orders = vi.hoisted(() => ({ getOrder: vi.fn() }));
vi.mock("@/lib/checkout/orders-api", () => orders);

vi.mock("@/lib/notifications/api", () => ({ listNotifications: vi.fn(), markNotificationRead: vi.fn(), markAllNotificationsRead: vi.fn() }));
vi.mock("sonner", () => ({ toast: Object.assign(vi.fn(), { error: vi.fn() }) }));

type FeedOptions = { onOrderStatus: (orderId: string, status: string) => void };
const feed = vi.hoisted(() => ({ options: undefined as FeedOptions | undefined, stop: vi.fn(() => Promise.resolve()) }));
vi.mock("@/lib/notifications/realtime", () => ({
  connectUserFeed: (options: FeedOptions) => {
    feed.options = options;
    return { stop: feed.stop };
  },
}));

const { ResultView } = await import("./result-view");
const { UserHubProvider } = await import("@/features/notifications/use-user-hub");

const NOW = Date.parse("2026-12-01T10:00:00Z");

const order = (status: OrderStatus, id = "o1"): Order => ({
  id,
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
});

const session: Session = {
  accessToken: "tok",
  expiresAt: new Date(NOW + 600_000).toISOString(),
  user: { id: "u1", email: "an@example.com", fullName: "An", phoneNumber: null, avatarUrl: null, emailConfirmed: true, roles: ["Customer"] },
};

const flush = (ms = 0) => act(() => vi.advanceTimersByTimeAsync(ms));

function mount(orderId = "o1") {
  const client = new QueryClient();
  return render(
    <QueryClientProvider client={client}>
      <AuthProvider>
        <UserHubProvider>
          <ResultView orderId={orderId} />
        </UserHubProvider>
      </AuthProvider>
    </QueryClientProvider>,
  );
}

/** The hub says the order changed: what `OrderStatusChanged` does inside the real provider. */
const push = (orderId: string, status: string) => act(() => feed.options?.onOrderStatus(orderId, status));

describe("ResultView with the user hub", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.useFakeTimers({ now: NOW, toFake: ["Date", "setTimeout", "clearTimeout", "setInterval", "clearInterval"] });
    feed.options = undefined;
    document.cookie = "tr_session=1; path=/";
    setSession(session);
    localStorage.clear();
  });

  afterEach(() => {
    vi.useRealTimers();
    document.cookie = "tr_session=; expires=Thu, 01 Jan 1970 00:00:00 GMT; path=/";
    act(() => clearSession());
  });

  it("Order_status_push_triggers_immediate_refetch", async () => {
    orders.getOrder.mockResolvedValue(order("AwaitingPayment"));
    mount();
    await flush();
    expect(orders.getOrder).toHaveBeenCalledTimes(1);
    expect(screen.getByRole("heading", { level: 1, name: "Đang xác nhận thanh toán" })).toBeInTheDocument();

    // Well before the next 1.5 s beat.
    orders.getOrder.mockResolvedValue(order("Paid"));
    await flush(200);
    push("o1", "Paid");
    await flush();

    expect(orders.getOrder).toHaveBeenCalledTimes(2);
    expect(screen.getByRole("heading", { level: 1, name: "Thanh toán thành công" })).toBeInTheDocument();
  });

  it("ignores a push for another order and keeps its own beat", async () => {
    orders.getOrder.mockResolvedValue(order("AwaitingPayment"));
    mount();
    await flush();

    push("someone-else", "Paid");
    await flush();
    expect(orders.getOrder).toHaveBeenCalledTimes(1);

    await flush(1_500);
    expect(orders.getOrder).toHaveBeenCalledTimes(2);
  });

  it("Polling_still_works_when_the_hub_never_connects", async () => {
    // The feed object exists but never reports anything, as when the websocket cannot be opened.
    orders.getOrder.mockResolvedValueOnce(order("AwaitingPayment")).mockResolvedValueOnce(order("AwaitingPayment")).mockResolvedValue(order("Paid"));
    mount();
    await flush();
    expect(feed.options).toBeDefined();

    await flush(1_500);
    await flush(1_500);

    expect(orders.getOrder).toHaveBeenCalledTimes(3);
    expect(screen.getByRole("heading", { level: 1, name: "Thanh toán thành công" })).toBeInTheDocument();
  });

  it("unsubscribes when the page unmounts, so a late push does nothing", async () => {
    orders.getOrder.mockResolvedValue(order("AwaitingPayment"));
    const { unmount } = mount();
    await flush();

    unmount();
    const calls = orders.getOrder.mock.calls.length;
    push("o1", "Paid");
    await flush(5_000);

    expect(orders.getOrder).toHaveBeenCalledTimes(calls);
  });

  it("follows the order id: after a change only the new order's push counts", async () => {
    orders.getOrder.mockImplementation(async (id: string) => order("AwaitingPayment", id));
    const view = mount("o1");
    await flush();
    view.rerender(
      <QueryClientProvider client={new QueryClient()}>
        <AuthProvider>
          <UserHubProvider>
            <ResultView orderId="o2" />
          </UserHubProvider>
        </AuthProvider>
      </QueryClientProvider>,
    );
    await flush();
    const before = orders.getOrder.mock.calls.length;

    push("o1", "Paid");
    await flush();
    expect(orders.getOrder).toHaveBeenCalledTimes(before);

    push("o2", "Paid");
    await flush();
    expect(orders.getOrder).toHaveBeenCalledTimes(before + 1);
    expect(orders.getOrder).toHaveBeenLastCalledWith("o2");
  });

  it("a push while a fetch is out is not lost: the order is read again right after", async () => {
    let resolveFirst: (o: Order) => void = () => undefined;
    orders.getOrder.mockImplementationOnce(() => new Promise<Order>((resolve) => (resolveFirst = resolve)));
    orders.getOrder.mockResolvedValue(order("Paid"));
    mount();
    await flush();

    push("o1", "Paid");
    await flush();
    expect(orders.getOrder).toHaveBeenCalledTimes(1);

    resolveFirst(order("AwaitingPayment"));
    await flush();

    expect(orders.getOrder).toHaveBeenCalledTimes(2);
    expect(screen.getByRole("heading", { level: 1, name: "Thanh toán thành công" })).toBeInTheDocument();
  });
});
