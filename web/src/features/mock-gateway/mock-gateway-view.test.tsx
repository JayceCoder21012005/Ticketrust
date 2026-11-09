import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { MockSession } from "@/lib/checkout/types";
import { ApiError } from "@/lib/errors";

const gateway = vi.hoisted(() => ({ getMockSession: vi.fn(), completeMockSession: vi.fn() }));
vi.mock("@/lib/checkout/orders-api", () => gateway);

const navigation = vi.hoisted(() => ({ navigateTo: vi.fn() }));
vi.mock("@/lib/checkout/navigate", () => navigation);

const { MockGatewayView } = await import("./mock-gateway-view");

const session = (extra: Partial<MockSession> = {}): MockSession => ({
  sessionId: "s1",
  orderId: "o1",
  eventId: "e1",
  label: "Đơn hàng 0F3A9C1E",
  amount: 5_800_000,
  currency: "VND",
  status: "Pending",
  expiresAt: "2026-12-01T10:10:00Z",
  serverNow: "2026-12-01T10:00:00Z",
  ...extra,
});

describe("MockGatewayView", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    gateway.getMockSession.mockResolvedValue(session());
  });

  it("shows the label and amount with the four outcomes", async () => {
    render(<MockGatewayView sessionId="s1" />);

    expect(await screen.findByRole("heading", { level: 1, name: "Cổng thanh toán giả lập" })).toBeInTheDocument();
    expect(screen.getByText("Đơn hàng 0F3A9C1E")).toBeInTheDocument();
    expect(screen.getByText(/5\.800\.000/)).toBeInTheDocument();
    for (const name of ["Thành công", "Thất bại", "Thành công – webhook trễ 3 phút", "Gửi webhook 2 lần"]) {
      expect(screen.getByRole("button", { name })).toBeEnabled();
    }
  });

  it.each([
    ["Thành công", "succeeded"],
    ["Thất bại", "failed"],
    ["Thành công – webhook trễ 3 phút", "succeeded_delayed"],
    ["Gửi webhook 2 lần", "succeeded_twice"],
  ])("%s completes the session as %s and returns to the order's result page", async (label, outcome) => {
    gateway.completeMockSession.mockResolvedValue(session({ status: outcome === "failed" ? "Failed" : "Succeeded" }));
    const user = userEvent.setup();
    render(<MockGatewayView sessionId="s1" />);

    await user.click(await screen.findByRole("button", { name: label }));

    expect(gateway.completeMockSession).toHaveBeenCalledWith("s1", outcome);
    await waitFor(() => expect(navigation.navigateTo).toHaveBeenCalledWith("/checkout/o1/result"));
  });

  it("locks the buttons while a completion is running", async () => {
    gateway.completeMockSession.mockReturnValue(new Promise(() => undefined));
    const user = userEvent.setup();
    render(<MockGatewayView sessionId="s1" />);

    await user.click(await screen.findByRole("button", { name: /^Thành công$/ }));

    for (const button of screen.getAllByRole("button")) expect(button).toBeDisabled();
    expect(gateway.completeMockSession).toHaveBeenCalledTimes(1);
  });

  it("says it is not available, with no way to act, when the api has no mock gateway (404)", async () => {
    gateway.getMockSession.mockRejectedValue(new ApiError(404, { code: "mock_gateway.session_not_found" }));
    render(<MockGatewayView sessionId="s1" />);

    expect(await screen.findByRole("heading", { name: "Cổng thanh toán giả lập không khả dụng" })).toBeInTheDocument();
    expect(screen.queryByRole("button")).not.toBeInTheDocument();
  });

  it("disables the outcomes of a session that is already over and still leads back to the order", async () => {
    gateway.getMockSession.mockResolvedValue(session({ status: "Succeeded" }));
    render(<MockGatewayView sessionId="s1" />);

    expect(await screen.findByText("Phiên này đã thanh toán thành công.")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /^Thất bại/ })).toBeDisabled();
    expect(screen.getByRole("link", { name: "Quay lại đơn hàng" })).toHaveAttribute("href", "/checkout/o1/result");
  });

  it("reports a failed completion and lets the tester try again", async () => {
    gateway.completeMockSession.mockRejectedValueOnce(new ApiError(409, { code: "mock_gateway.already_completed" }));
    const user = userEvent.setup();
    render(<MockGatewayView sessionId="s1" />);

    await user.click(await screen.findByRole("button", { name: /^Thành công$/ }));

    expect(await screen.findByRole("alert")).toHaveTextContent("Không hoàn tất được phiên thanh toán.");
    expect(screen.getByRole("button", { name: /^Thành công$/ })).toBeEnabled();
    expect(navigation.navigateTo).not.toHaveBeenCalled();
  });

  it("offers a retry when the session cannot be loaded", async () => {
    gateway.getMockSession.mockRejectedValueOnce(new TypeError("offline")).mockResolvedValue(session());
    const user = userEvent.setup();
    render(<MockGatewayView sessionId="s1" />);

    await user.click(await screen.findByRole("button", { name: "Thử lại" }));

    expect(await screen.findByRole("button", { name: /^Thất bại/ })).toBeEnabled();
  });
});
