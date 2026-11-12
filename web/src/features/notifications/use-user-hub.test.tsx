import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, render, screen, waitFor } from "@testing-library/react";
import { useEffect } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { AuthProvider } from "@/components/auth/auth-provider";
import { clearSession, setSession, type Session } from "@/lib/api/session";
import type { NotificationItem } from "@/lib/notifications/types";
import { notification, notificationPage, notifications } from "./notification-fixtures";

const router = vi.hoisted(() => ({ push: vi.fn(), replace: vi.fn() }));
vi.mock("next/navigation", () => ({ useRouter: () => router }));

const api = vi.hoisted(() => ({ listNotifications: vi.fn(), markNotificationRead: vi.fn(), markAllNotificationsRead: vi.fn() }));
vi.mock("@/lib/notifications/api", () => api);

const toast = vi.hoisted(() => ({ error: vi.fn(), info: vi.fn() }));
vi.mock("sonner", () => ({ toast: Object.assign(vi.fn(), toast) }));

type FeedOptions = {
  accessToken: () => Promise<string>;
  onNotification: (item: NotificationItem) => void;
  onOrderStatus: (orderId: string, status: string) => void;
  onResync: () => void;
};
const feed = vi.hoisted(() => ({ connect: vi.fn(), stop: vi.fn(() => Promise.resolve()), options: undefined as FeedOptions | undefined }));
vi.mock("@/lib/notifications/realtime", () => ({
  connectUserFeed: (options: FeedOptions) => {
    feed.connect(options);
    feed.options = options;
    return { stop: feed.stop };
  },
}));

const { NotificationBell } = await import("./notification-bell");
const { UserHubProvider, useUserHub } = await import("./use-user-hub");
const { toast: sonner } = await import("sonner");

const session = (id = "u1"): Session => ({
  accessToken: `tok-${id}`,
  expiresAt: new Date(Date.now() + 600_000).toISOString(),
  user: { id, email: `${id}@example.com`, fullName: id, phoneNumber: null, avatarUrl: null, emailConfirmed: true, roles: ["Customer"] },
});

function Subscriber({ orderId, onStatus }: { orderId: string; onStatus: (status: string) => void }) {
  const { subscribeOrderStatus } = useUserHub();
  useEffect(() => subscribeOrderStatus(orderId, onStatus), [subscribeOrderStatus, orderId, onStatus]);
  return null;
}

function mount(children: React.ReactNode = <NotificationBell />) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const view = render(
    <QueryClientProvider client={client}>
      <AuthProvider>
        <UserHubProvider>{children}</UserHubProvider>
      </AuthProvider>
    </QueryClientProvider>,
  );
  return { client, ...view };
}

beforeEach(() => {
  vi.resetAllMocks();
  document.cookie = "tr_session=1; path=/";
  feed.options = undefined;
  api.listNotifications.mockResolvedValue(notificationPage(notifications(2), 2));
  setSession(session());
});

afterEach(() => {
  document.cookie = "tr_session=; expires=Thu, 01 Jan 1970 00:00:00 GMT; path=/";
  act(() => clearSession());
});

describe("UserHubProvider", () => {
  it("opens exactly one connection and stops it when the buyer area unmounts", async () => {
    const { unmount } = mount();
    await waitFor(() => expect(feed.connect).toHaveBeenCalledTimes(1));

    unmount();

    expect(feed.stop).toHaveBeenCalledTimes(1);
    expect(feed.connect).toHaveBeenCalledTimes(1);
  });

  it("does not connect without a signed-in user", () => {
    act(() => clearSession());
    document.cookie = "tr_session=; expires=Thu, 01 Jan 1970 00:00:00 GMT; path=/";
    mount(null);

    expect(feed.connect).not.toHaveBeenCalled();
  });

  it("connects with the current access token", async () => {
    mount(null);
    await waitFor(() => expect(feed.options).toBeDefined());

    await expect(feed.options!.accessToken()).resolves.toBe("tok-u1");
  });

  it("reconnects for another user and stops the old connection", async () => {
    mount(null);
    await waitFor(() => expect(feed.connect).toHaveBeenCalledTimes(1));

    act(() => setSession(session("u2")));

    await waitFor(() => expect(feed.connect).toHaveBeenCalledTimes(2));
    expect(feed.stop).toHaveBeenCalledTimes(1);
  });

  it("Hub_notification_shows_a_toast_and_bumps_the_badge", async () => {
    mount();
    expect(await screen.findByRole("button", { name: "2 thông báo chưa đọc" })).toBeInTheDocument();
    const pushed = notification(7);

    act(() => feed.options!.onNotification(pushed));

    expect(await screen.findByRole("button", { name: "3 thông báo chưa đọc" })).toBeInTheDocument();
    expect(sonner).toHaveBeenCalledWith(pushed.title, expect.objectContaining({ description: pushed.body }));
  });

  it("shows one toast per notification id: a push delivered twice replaces its own toast", async () => {
    mount(null);
    await waitFor(() => expect(feed.options).toBeDefined());
    const pushed = notification(7);

    act(() => feed.options!.onNotification(pushed));
    act(() => feed.options!.onNotification(pushed));

    const ids = vi.mocked(sonner).mock.calls.map((call) => (call[1] as { id?: unknown }).id);
    expect(ids).toEqual([pushed.id, pushed.id]);
  });

  it("ignores a late push of the previous user once the connection was replaced", async () => {
    const mine = vi.fn();
    const { client } = mount(<Subscriber orderId="o1" onStatus={mine} />);
    await waitFor(() => expect(feed.options).toBeDefined());
    const previous = feed.options!;

    act(() => setSession(session("u2")));
    await waitFor(() => expect(feed.connect).toHaveBeenCalledTimes(2));
    const fetches = api.listNotifications.mock.calls.length;
    act(() => {
      previous.onNotification(notification(9));
      previous.onOrderStatus("o1", "Paid");
      previous.onResync();
    });

    expect(sonner).not.toHaveBeenCalled();
    expect(mine).not.toHaveBeenCalled();
    const items = client.getQueryData<{ pages: { items: { id: string }[] }[] }>(["notifications", "list"])?.pages.flatMap((p) => p.items) ?? [];
    expect(items.map((i) => i.id)).not.toContain(notification(9).id);
    expect(api.listNotifications.mock.calls.length).toBe(fetches);
  });

  it("opens the notification's page from the toast", async () => {
    mount(null);
    await waitFor(() => expect(feed.options).toBeDefined());
    const pushed = notification(7);

    act(() => feed.options!.onNotification(pushed));
    const { action } = vi.mocked(sonner).mock.calls[0]![1] as { action: { label: string; onClick: () => void } };
    action.onClick();

    expect(action.label).toBe("Xem");
    expect(router.push).toHaveBeenCalledWith(pushed.link);
  });

  it("offers no action on a toast whose link is missing or not an app path", async () => {
    mount(null);
    await waitFor(() => expect(feed.options).toBeDefined());

    act(() => feed.options!.onNotification(notification(1, { link: null })));
    act(() => feed.options!.onNotification(notification(2, { link: "//evil.example" })));

    for (const call of vi.mocked(sonner).mock.calls) {
      expect(call[1]).not.toHaveProperty("action");
    }
    expect(sonner).toHaveBeenCalledTimes(2);
  });

  it("Hub_reconnect_triggers_a_refetch", async () => {
    mount();
    await screen.findByRole("button", { name: "2 thông báo chưa đọc" });
    expect(api.listNotifications).toHaveBeenCalledTimes(1);
    api.listNotifications.mockResolvedValue(notificationPage(notifications(5), 5));

    act(() => feed.options!.onResync());

    expect(await screen.findByRole("button", { name: "5 thông báo chưa đọc" })).toBeInTheDocument();
    expect(api.listNotifications).toHaveBeenCalledTimes(2);
  });

  it("tells only the subscribers of that order about a status change, until they unsubscribe", async () => {
    const mine = vi.fn();
    const other = vi.fn();
    const { rerender, client } = mount(
      <>
        <Subscriber orderId="o1" onStatus={mine} />
        <Subscriber orderId="o2" onStatus={other} />
      </>,
    );
    await waitFor(() => expect(feed.options).toBeDefined());

    act(() => feed.options!.onOrderStatus("o1", "Paid"));
    expect(mine).toHaveBeenCalledWith("Paid");
    expect(other).not.toHaveBeenCalled();

    rerender(
      <QueryClientProvider client={client}>
        <AuthProvider>
          <UserHubProvider>
            <Subscriber orderId="o2" onStatus={other} />
          </UserHubProvider>
        </AuthProvider>
      </QueryClientProvider>,
    );
    act(() => feed.options!.onOrderStatus("o1", "Refunded"));
    expect(mine).toHaveBeenCalledTimes(1);
    expect(feed.connect).toHaveBeenCalledTimes(1);
  });

  it("gives consumers outside the buyer area a harmless no-op subscription", () => {
    function Probe() {
      const { subscribeOrderStatus } = useUserHub();
      expect(() => subscribeOrderStatus("o1", vi.fn())()).not.toThrow();
      return <p>ok</p>;
    }

    render(<Probe />);

    expect(screen.getByText("ok")).toBeInTheDocument();
  });
});
