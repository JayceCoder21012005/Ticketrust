import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { clearSession, setSession, type Session } from "@/lib/api/session";
import { notification, notificationPage, notifications } from "./notification-fixtures";

const router = vi.hoisted(() => ({ push: vi.fn(), replace: vi.fn() }));
vi.mock("next/navigation", () => ({ useRouter: () => router }));

const auth = vi.hoisted(() => ({ status: "authenticated" }));
vi.mock("@/components/auth/auth-provider", () => ({ useAuth: () => ({ status: auth.status }) }));

const api = vi.hoisted(() => ({ listNotifications: vi.fn(), markNotificationRead: vi.fn(), markAllNotificationsRead: vi.fn() }));
vi.mock("@/lib/notifications/api", () => api);

const toast = vi.hoisted(() => ({ error: vi.fn() }));
vi.mock("sonner", () => ({ toast }));

const { NotificationsPage } = await import("./notifications-page");

const session: Session = {
  accessToken: "tok",
  expiresAt: new Date(Date.now() + 600_000).toISOString(),
  user: { id: "u1", email: "an@example.com", fullName: "An", phoneNumber: null, avatarUrl: null, emailConfirmed: true, roles: ["Customer"] },
};

function renderPage() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <NotificationsPage />
    </QueryClientProvider>,
  );
}

beforeEach(() => {
  vi.resetAllMocks();
  auth.status = "authenticated";
  setSession(session);
});

afterEach(() => clearSession());

describe("NotificationsPage", () => {
  it("List_page_loads_more_with_cursor", async () => {
    const user = userEvent.setup();
    api.listNotifications
      .mockResolvedValueOnce(notificationPage(notifications(3), 3, "cursor-2"))
      .mockResolvedValueOnce(notificationPage([notification(4, { read: true })], 3, null));
    renderPage();

    expect(await screen.findAllByRole("listitem")).toHaveLength(3);
    expect(api.listNotifications.mock.calls[0]![0]).toBeUndefined();

    await user.click(screen.getByRole("button", { name: "Tải thêm" }));

    await waitFor(() => expect(screen.getAllByRole("listitem")).toHaveLength(4));
    expect(api.listNotifications.mock.calls[1]![0]).toBe("cursor-2");
    expect(screen.queryByRole("button", { name: "Tải thêm" })).toBeNull();
    expect(screen.getByRole("status")).toHaveTextContent("Đã tải thêm 1 thông báo, đang hiển thị 4.");
  });

  it("focuses the heading once the list has loaded", async () => {
    api.listNotifications.mockResolvedValue(notificationPage(notifications(1)));
    renderPage();

    const heading = await screen.findByRole("heading", { level: 1, name: "Thông báo" });
    await waitFor(() => expect(heading).toHaveFocus());
  });

  it("shows unread items as unread to assistive technology", async () => {
    api.listNotifications.mockResolvedValue(notificationPage([notification(1), notification(2, { read: true })]));
    renderPage();

    const rows = await screen.findAllByRole("listitem");
    expect(within(rows[0]!).getByText("Chưa đọc")).toBeInTheDocument();
    expect(within(rows[1]!).queryByText("Chưa đọc")).toBeNull();
  });

  it("marks a clicked item read and opens its page", async () => {
    const user = userEvent.setup();
    const target = notification(1);
    api.listNotifications.mockResolvedValue(notificationPage([target]));
    // The server remembers: the refetch after the mutation settles sees the row read.
    api.markNotificationRead.mockImplementation(async () => {
      api.listNotifications.mockResolvedValue(notificationPage([{ ...target, read: true }]));
    });
    renderPage();

    await user.click(await screen.findByRole("link", { name: /Thanh toán thành công 1/ }));

    expect(api.markNotificationRead).toHaveBeenCalledWith(target.id);
    expect(router.push).toHaveBeenCalledWith(target.link);
    await waitFor(() => expect(screen.queryByText("Chưa đọc")).toBeNull());
  });

  it("marks everything read", async () => {
    const user = userEvent.setup();
    api.listNotifications.mockResolvedValue(notificationPage(notifications(2)));
    api.markAllNotificationsRead.mockImplementation(async () => {
      api.listNotifications.mockResolvedValue(notificationPage(notifications(2).map((n) => ({ ...n, read: true }))));
    });
    renderPage();

    await user.click(await screen.findByRole("button", { name: "Đánh dấu tất cả đã đọc" }));

    expect(api.markAllNotificationsRead).toHaveBeenCalledTimes(1);
    await waitFor(() => expect(screen.queryByText("Chưa đọc")).toBeNull());
    expect(screen.getByRole("button", { name: "Đánh dấu tất cả đã đọc" })).toBeDisabled();
  });

  it("shows the empty state", async () => {
    api.listNotifications.mockResolvedValue(notificationPage([]));
    renderPage();

    expect(await screen.findByText("Bạn chưa có thông báo nào")).toBeInTheDocument();
  });

  it("shows an error with a retry when the first page fails", async () => {
    const user = userEvent.setup();
    api.listNotifications.mockRejectedValueOnce(new Error("boom"));
    renderPage();

    expect(await screen.findByText("Không tải được thông báo.")).toBeInTheDocument();
    api.listNotifications.mockResolvedValue(notificationPage(notifications(1)));
    await user.click(screen.getByRole("button", { name: "Thử lại" }));

    expect(await screen.findAllByRole("listitem")).toHaveLength(1);
  });

  it("keeps the rows and says so when loading more fails", async () => {
    const user = userEvent.setup();
    api.listNotifications.mockResolvedValueOnce(notificationPage(notifications(2), 2, "cursor-2")).mockRejectedValueOnce(new Error("boom"));
    renderPage();

    await user.click(await screen.findByRole("button", { name: "Tải thêm" }));

    expect(await screen.findByText("Không tải thêm được thông báo. Vui lòng thử lại.")).toBeInTheDocument();
    expect(screen.getAllByRole("listitem")).toHaveLength(2);
  });

  it("sends a visitor whose session is gone to login", async () => {
    auth.status = "anonymous";
    api.listNotifications.mockResolvedValue(notificationPage([]));
    renderPage();

    await waitFor(() => expect(router.replace).toHaveBeenCalledWith("/login?returnUrl=%2Faccount%2Fnotifications"));
  });
});
