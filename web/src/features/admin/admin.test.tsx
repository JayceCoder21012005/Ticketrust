import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { TooltipProvider } from "@/components/ui/tooltip";
import { ApiError } from "@/lib/errors";

const router = vi.hoisted(() => ({ push: vi.fn(), replace: vi.fn() }));
const search = vi.hoisted(() => ({ value: new URLSearchParams() }));
vi.mock("next/navigation", () => ({
  useRouter: () => router,
  usePathname: () => "/admin/users",
  useSearchParams: () => search.value,
  notFound: () => {
    throw new Error("NEXT_NOT_FOUND");
  },
}));

const auth = vi.hoisted(() => ({ status: "authenticated", user: { id: "a1", roles: ["Admin"] } as { id: string; roles: string[] } | null }));
vi.mock("@/components/auth/auth-provider", () => ({ useAuth: () => auth }));

const api = vi.hoisted(() => ({ searchUsers: vi.fn(), lockUser: vi.fn(), grantRole: vi.fn(), refundOrder: vi.fn(), searchOrders: vi.fn() }));
vi.mock("@/lib/admin/api", () => api);

import { AdminGate } from "./admin-shell";
import { AuditDiff, parseChanges } from "./audit-diff";
import { CommandPalette, jumpTargets } from "./command-palette";
import { OrdersPage, UsersPage } from "./admin-pages";
import { ReasonDialog } from "./reason-dialog";

beforeEach(() => {
  // The sidebar asks whether the screen is a phone.
  vi.stubGlobal("matchMedia", (query: string) => ({ matches: false, media: query, addEventListener: vi.fn(), removeEventListener: vi.fn() }));
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.clearAllMocks();
  search.value = new URLSearchParams();
  auth.user = { id: "a1", roles: ["Admin"] };
});

const user = (over: object = {}) => ({
  id: "u1",
  email: "an@example.com",
  fullName: "Nguyễn An",
  phoneNumber: null,
  emailConfirmed: true,
  locked: false,
  lockoutEnd: null,
  roles: ["Customer"],
  createdAt: "2026-11-01T03:00:00Z",
  ...over,
});

describe("jumpTargets", () => {
  it("offers user, order and event for an id, user and orders for an email, and an event search otherwise", () => {
    const id = "0196a1c2-0000-7000-8000-000000000001";
    expect(jumpTargets(id).map((t) => t.href)).toEqual([`/admin/users/${id}`, `/admin/orders?q=${id}`, `/admin/events?q=${id}`]);
    expect(jumpTargets(" an@example.com ").map((t) => t.kind)).toEqual(["user", "order"]);
    expect(jumpTargets("Sơn Tùng")[0]!.href).toBe(`/admin/events?q=${encodeURIComponent("Sơn Tùng")}`);
    expect(jumpTargets("a")).toEqual([]);
  });
});

describe("CommandPalette", () => {
  it("opens with Ctrl+K or ⌘K and jumps to the chosen user", async () => {
    render(<CommandPalette />);

    fireEvent.keyDown(window, { key: "k", ctrlKey: true });
    const input = await screen.findByPlaceholderText("Id hoặc email…");
    const id = "0196a1c2-0000-7000-8000-000000000001";
    fireEvent.change(input, { target: { value: id } });
    fireEvent.click(screen.getByRole("option", { name: `Người dùng ${id}` }));

    expect(router.push).toHaveBeenCalledWith(`/admin/users/${id}`);
  });
});

describe("AuditDiff", () => {
  it("shows before and after side by side and marks the changed fields", () => {
    render(<AuditDiff changes={JSON.stringify({ status: { before: "Valid", after: "Listed" }, title: { before: "A", after: "A" } })} />);

    const rows = screen.getAllByRole("row").slice(1);
    expect(rows[0]).toHaveAttribute("data-changed", "true");
    expect(within(rows[0]!).getByText("Valid")).toBeInTheDocument();
    expect(within(rows[0]!).getByText("Listed")).toBeInTheDocument();
    expect(rows[1]).toHaveAttribute("data-changed", "false");
  });

  it("lists an explicit action's details as they are", () => {
    expect(parseChanges(JSON.stringify({ role: "Admin", reason: "x" }))).toEqual({ details: { role: "Admin", reason: "x" } });
    render(<AuditDiff changes={JSON.stringify({ reason: "Nhân viên mới" })} />);
    expect(screen.getByText("Nhân viên mới")).toBeInTheDocument();
  });
});

describe("ReasonDialog", () => {
  it("needs a reason and the target typed back before a destructive action runs", async () => {
    const onConfirm = vi.fn().mockResolvedValue(undefined);
    render(<ReasonDialog open onOpenChange={vi.fn()} title="Khoá tài khoản" description="x" confirmLabel="Khoá" confirmText="an@example.com" destructive onConfirm={onConfirm} />);

    expect(screen.getByRole("alertdialog")).toBeInTheDocument();
    const button = screen.getByRole("button", { name: "Khoá" });
    expect(button).toBeDisabled();
    fireEvent.change(screen.getByLabelText("Lý do (ghi vào nhật ký)"), { target: { value: "Bot" } });
    expect(button).toBeDisabled();
    fireEvent.change(screen.getByLabelText(/Nhập/), { target: { value: "an@example.com" } });
    fireEvent.click(button);

    await waitFor(() => expect(onConfirm).toHaveBeenCalledWith("Bot", "an@example.com"));
  });

  it("keeps the dialog open with the api's reason when the action fails", async () => {
    const onConfirm = vi.fn().mockRejectedValue(new ApiError(409, { code: "admin.own_admin_role", title: "x" }));
    render(<ReasonDialog open onOpenChange={vi.fn()} title="Thu hồi" description="x" confirmLabel="Thu hồi" onConfirm={onConfirm} />);
    fireEvent.change(screen.getByLabelText("Lý do (ghi vào nhật ký)"), { target: { value: "x" } });
    fireEvent.click(screen.getByRole("button", { name: "Thu hồi" }));

    expect(await screen.findByRole("alert")).toBeInTheDocument();
  });
});

describe("AdminGate", () => {
  it("renders the not-found page for anyone but an admin", () => {
    auth.user = { id: "u1", roles: ["Organizer"] };
    expect(() => render(<AdminGate>bí mật</AdminGate>)).toThrow("NEXT_NOT_FOUND");
  });

  it("lets an admin in", () => {
    render(<AdminGate>khu quản trị</AdminGate>);
    expect(screen.getByText("khu quản trị")).toBeInTheDocument();
  });
});

describe("UsersPage", () => {
  it("searches through the url, hides a column and locks a user with a reason", async () => {
    search.value = new URLSearchParams("q=an");
    api.searchUsers.mockResolvedValue({ items: [user()], nextCursor: null });
    api.lockUser.mockResolvedValue(user({ locked: true }));
    render(<TooltipProvider><UsersPage /></TooltipProvider>);

    expect(await screen.findByRole("link", { name: "an@example.com" })).toBeInTheDocument();
    expect(api.searchUsers).toHaveBeenCalledWith("an", null);

    fireEvent.change(screen.getByRole("textbox", { name: "Tìm người dùng" }), { target: { value: "binh" } });
    fireEvent.submit(screen.getByRole("search"));
    expect(router.replace).toHaveBeenCalledWith("/admin/users?q=binh", { scroll: false });

    fireEvent.pointerDown(screen.getByRole("button", { name: "Cột" }), { button: 0, ctrlKey: false });
    fireEvent.click(await screen.findByRole("menuitemcheckbox", { name: "Điện thoại" }));
    await waitFor(() => expect(screen.queryByRole("columnheader", { name: "Điện thoại" })).not.toBeInTheDocument());
    // The menu stays open for the next column, and switching it back brings the column back.
    fireEvent.click(screen.getByRole("menuitemcheckbox", { name: "Họ tên" }));
    await waitFor(() => expect(screen.queryByRole("columnheader", { name: "Họ tên" })).not.toBeInTheDocument());
    fireEvent.click(screen.getByRole("menuitemcheckbox", { name: "Điện thoại" }));
    expect(await screen.findByRole("columnheader", { name: "Điện thoại" })).toBeInTheDocument();
    fireEvent.keyDown(screen.getByRole("menu"), { key: "Escape" });
    await waitFor(() => expect(screen.queryByRole("menu")).not.toBeInTheDocument());

    fireEvent.pointerDown(screen.getByRole("button", { name: "Thao tác" }), { button: 0, ctrlKey: false });
    fireEvent.click(await screen.findByRole("menuitem", { name: "Khoá tài khoản" }));
    fireEvent.change(screen.getByLabelText("Lý do (ghi vào nhật ký)"), { target: { value: "Bot" } });
    fireEvent.change(screen.getByLabelText(/Nhập/), { target: { value: "an@example.com" } });
    fireEvent.click(screen.getByRole("button", { name: "Khoá" }));

    await waitFor(() => expect(api.lockUser).toHaveBeenCalledWith("u1", "Bot"));
    await waitFor(() => expect(api.searchUsers).toHaveBeenCalledTimes(2));
  });
});

describe("OrdersPage", () => {
  it("refunds a paid primary order only after its id is typed back", async () => {
    api.searchOrders.mockResolvedValue({
      items: [{ id: "o1", eventId: "e1", eventTitle: "Đêm nhạc", status: "Paid", kind: "Primary", total: 1_000_000, tickets: 1, createdAt: "2026-11-01T03:00:00Z", paidAt: "2026-11-01T03:01:00Z" }],
      nextCursor: null,
    });
    api.refundOrder.mockResolvedValue({});
    render(<TooltipProvider><OrdersPage /></TooltipProvider>);

    fireEvent.pointerDown(await screen.findByRole("button", { name: "Thao tác" }), { button: 0, ctrlKey: false });
    fireEvent.click(await screen.findByRole("menuitem", { name: "Hoàn tiền thủ công" }));
    fireEvent.change(screen.getByLabelText("Lý do (ghi vào nhật ký)"), { target: { value: "Khách yêu cầu" } });
    fireEvent.change(screen.getByLabelText(/Nhập/), { target: { value: "o1" } });
    fireEvent.click(screen.getByRole("button", { name: "Hoàn tiền" }));

    await waitFor(() => expect(api.refundOrder).toHaveBeenCalledWith("o1", "Khách yêu cầu", "o1"));
  });
});
