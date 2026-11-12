import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { clearSession, setSession, type Session } from "@/lib/api/session";
import { notification, notificationPage, notifications } from "./notification-fixtures";

const router = vi.hoisted(() => ({ push: vi.fn(), replace: vi.fn() }));
vi.mock("next/navigation", () => ({ useRouter: () => router }));

const api = vi.hoisted(() => ({ listNotifications: vi.fn(), markNotificationRead: vi.fn(), markAllNotificationsRead: vi.fn() }));
vi.mock("@/lib/notifications/api", () => api);

const toast = vi.hoisted(() => ({ error: vi.fn() }));
vi.mock("sonner", () => ({ toast }));

const { NotificationBell } = await import("./notification-bell");

const session: Session = {
  accessToken: "tok",
  expiresAt: new Date(Date.now() + 600_000).toISOString(),
  user: { id: "u1", email: "an@example.com", fullName: "An", phoneNumber: null, avatarUrl: null, emailConfirmed: true, roles: ["Customer"] },
};

function renderBell() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <NotificationBell />
    </QueryClientProvider>,
  );
}

const bellButton = (name: string | RegExp = /thông báo/i) => screen.findByRole("button", { name });

beforeEach(() => {
  vi.resetAllMocks();
  document.cookie = "tr_session=1; path=/";
  setSession(session);
});

afterEach(() => {
  document.cookie = "tr_session=; expires=Thu, 01 Jan 1970 00:00:00 GMT; path=/";
  act(() => clearSession());
});

describe("NotificationBell", () => {
  it("Badge_shows_unread_count_and_caps_at_99_plus", async () => {
    api.listNotifications.mockResolvedValue(notificationPage(notifications(3), 150));
    renderBell();

    const button = await bellButton("150 thông báo chưa đọc");
    expect(within(button).getByText("99+")).toBeInTheDocument();
  });

  it.each([
    [99, "99"],
    [100, "99+"],
    [1, "1"],
  ])("shows %i unread as %s", async (count, shown) => {
    api.listNotifications.mockResolvedValue(notificationPage(notifications(1), count));
    renderBell();

    const button = await bellButton(`${count} thông báo chưa đọc`);
    expect(within(button).getByText(shown)).toBeInTheDocument();
  });

  it("shows no badge and a plain name when everything is read", async () => {
    api.listNotifications.mockResolvedValue(notificationPage([notification(1, { read: true })], 0));
    renderBell();

    const button = await bellButton("Thông báo");
    expect(button).toHaveTextContent("");
  });

  it("Popover_lists_latest_ten_with_empty_state", async () => {
    const user = userEvent.setup();
    api.listNotifications.mockResolvedValue(notificationPage(notifications(9), 9));
    const { unmount } = renderBell();

    await user.click(await bellButton("9 thông báo chưa đọc"));

    const dialog = await screen.findByRole("dialog", { name: "Thông báo" });
    expect(api.listNotifications).toHaveBeenCalledWith(undefined, 10);
    expect(within(dialog).getAllByRole("listitem")).toHaveLength(9);
    expect(within(dialog).getByText("Thanh toán thành công 1")).toBeInTheDocument();
    expect(within(dialog).getByRole("link", { name: "Xem tất cả" })).toHaveAttribute("href", "/account/notifications");
    unmount();

    api.listNotifications.mockResolvedValue(notificationPage([], 0));
    renderBell();
    await user.click(await bellButton("Thông báo"));
    expect(await screen.findByText("Bạn chưa có thông báo nào")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Đánh dấu tất cả đã đọc" })).toBeNull();
  });

  it("shows at most ten notifications even when more are cached", async () => {
    const user = userEvent.setup();
    api.listNotifications.mockResolvedValue(notificationPage(notifications(9).concat(notifications(3, 10)), 12));
    renderBell();

    await user.click(await bellButton("12 thông báo chưa đọc"));

    expect(within(await screen.findByRole("dialog")).getAllByRole("listitem")).toHaveLength(10);
  });

  it("says it is loading, then offers a retry when the list cannot be loaded", async () => {
    const user = userEvent.setup();
    api.listNotifications.mockRejectedValueOnce(new Error("boom"));
    renderBell();

    await user.click(await bellButton("Thông báo"));

    expect(await screen.findByText("Không tải được thông báo.")).toBeInTheDocument();
    api.listNotifications.mockResolvedValue(notificationPage([notification(1)]));
    await user.click(screen.getByRole("button", { name: "Thử lại" }));
    expect(await screen.findByText("Thanh toán thành công 1")).toBeInTheDocument();
  });

  it("Clicking_an_item_marks_it_read_and_navigates", async () => {
    const user = userEvent.setup();
    const target = notification(2);
    api.listNotifications.mockResolvedValue(notificationPage([notification(1), target], 2));
    // The api is slow: the badge must already have dropped (optimistic).
    api.markNotificationRead.mockReturnValue(new Promise(() => undefined));
    renderBell();

    await user.click(await bellButton("2 thông báo chưa đọc"));
    await user.click(await screen.findByRole("link", { name: /Thanh toán thành công 2/ }));

    expect(api.markNotificationRead).toHaveBeenCalledWith(target.id);
    expect(router.push).toHaveBeenCalledWith(target.link);
    expect(await bellButton("1 thông báo chưa đọc")).toBeInTheDocument();
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
  });

  it("marks an item read but never navigates to a link that is not an app path", async () => {
    const user = userEvent.setup();
    const unsafe = notification(1, { link: "https://evil.example/phish" });
    api.listNotifications.mockResolvedValue(notificationPage([unsafe], 1));
    api.markNotificationRead.mockResolvedValue(undefined);
    renderBell();

    await user.click(await bellButton("1 thông báo chưa đọc"));
    expect(screen.queryByRole("link", { name: /Thanh toán thành công 1/ })).toBeNull();
    await user.click(await screen.findByRole("button", { name: /Thanh toán thành công 1/ }));

    expect(api.markNotificationRead).toHaveBeenCalledWith(unsafe.id);
    expect(router.push).not.toHaveBeenCalled();
  });

  it("puts the count back and tells the user when marking one read fails", async () => {
    const user = userEvent.setup();
    api.listNotifications.mockResolvedValue(notificationPage([notification(1, { link: null })], 1));
    api.markNotificationRead.mockRejectedValue(new Error("boom"));
    renderBell();

    await user.click(await bellButton("1 thông báo chưa đọc"));
    await user.click(await screen.findByRole("button", { name: /Thanh toán thành công 1/ }));

    await waitFor(() => expect(toast.error).toHaveBeenCalledWith("Không đánh dấu được thông báo là đã đọc."));
    expect(await bellButton("1 thông báo chưa đọc")).toBeInTheDocument();
  });

  it("Mark_all_read_clears_the_badge", async () => {
    const user = userEvent.setup();
    api.listNotifications.mockResolvedValue(notificationPage(notifications(3), 3));
    // The server remembers: the refetch after the mutation settles sees everything read.
    api.markAllNotificationsRead.mockImplementation(async () => {
      api.listNotifications.mockResolvedValue(notificationPage(notifications(3).map((n) => ({ ...n, read: true }))));
    });
    renderBell();

    await user.click(await bellButton("3 thông báo chưa đọc"));
    await user.click(await screen.findByRole("button", { name: "Đánh dấu tất cả đã đọc" }));

    expect(api.markAllNotificationsRead).toHaveBeenCalledTimes(1);
    expect(await bellButton("Thông báo")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Đánh dấu tất cả đã đọc" })).toBeDisabled();
  });

  it("Bell_hidden_without_session_hint", () => {
    document.cookie = "tr_session=; expires=Thu, 01 Jan 1970 00:00:00 GMT; path=/";
    const { container } = renderBell();

    expect(container).toBeEmptyDOMElement();
    expect(api.listNotifications).not.toHaveBeenCalled();
  });

  it("is reachable and operable by keyboard", async () => {
    const user = userEvent.setup();
    api.listNotifications.mockResolvedValue(notificationPage(notifications(2), 2));
    renderBell();

    const button = await bellButton("2 thông báo chưa đọc");
    button.focus();
    await user.keyboard("{Enter}");
    expect(await screen.findByRole("dialog", { name: "Thông báo" })).toBeInTheDocument();
    await user.keyboard("{Escape}");
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
    expect(button).toHaveFocus();
  });
});
