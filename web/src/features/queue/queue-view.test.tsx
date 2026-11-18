import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { TEST_TOKEN } from "@/lib/captcha/turnstile";
import { ApiError } from "@/lib/errors";
import type { QueueStatus } from "@/lib/queue/types";
import { QueueView } from "./queue-view";

const push = vi.fn();
vi.mock("next/navigation", () => ({ useRouter: () => ({ push }) }));

const queue = vi.hoisted(() => ({ current: {} as Record<string, unknown>, join: vi.fn(), leave: vi.fn() }));
vi.mock("@/lib/queue/use-queue", () => ({ useQueue: () => ({ ...queue.current, join: queue.join, leave: queue.leave }) }));

const event = { id: "e1", slug: "show", title: "Đêm nhạc thử", category: "concert", coverImageUrl: null };

const status = (overrides: Partial<QueueStatus> = {}): QueueStatus => ({
  status: "Waiting",
  position: 1234,
  aheadOfYou: 1233,
  estimatedWaitSeconds: 240,
  opensAt: "2026-12-01T10:00:00Z",
  serverNow: "2026-12-01T10:05:00Z",
  pollAfterMs: 4_000,
  admissionToken: null,
  tokenExpiresAt: null,
  ...overrides,
});

const view = (overrides: Record<string, unknown> = {}) => {
  queue.current = { phase: "active", status: status(), startPosition: 2000, offsetMs: 0, error: null, connectionIssues: 0, ...overrides };
};

describe("QueueView", () => {
  beforeEach(() => {
    push.mockClear();
    queue.join.mockReset().mockResolvedValue(undefined);
    queue.leave.mockReset().mockResolvedValue(undefined);
    document.title = "Phòng chờ";
  });

  it("joins the waiting room as soon as it opens, once", () => {
    view({ phase: "idle", status: null });

    const { rerender } = render(<QueueView event={event} />);
    rerender(<QueueView event={event} />);

    expect(queue.join).toHaveBeenCalledTimes(1);
    expect(queue.join).toHaveBeenCalledWith(undefined, TEST_TOKEN);
  });

  it("shows the event, a joining message and the place in the tab title once waiting", () => {
    view({ phase: "joining", status: null });
    const { rerender } = render(<QueueView event={event} />);
    expect(screen.getByRole("heading", { level: 1, name: "Đêm nhạc thử" })).toBeInTheDocument();
    expect(screen.getByText("Đang vào phòng chờ…")).toBeInTheDocument();

    view();
    rerender(<QueueView event={event} />);

    expect(screen.getByText("Còn 1.233 người phía trước")).toBeInTheDocument();
    expect(document.title).toBe("(#1.234) TicketRush");
  });

  it("shows the countdown and the fairness promise before opening", () => {
    view({ status: status({ status: "Pre", position: null, aheadOfYou: null, estimatedWaitSeconds: null }) });

    render(<QueueView event={event} />);

    expect(screen.getByText("Mọi người vào phòng chờ trước giờ mở bán sẽ được xếp thứ tự ngẫu nhiên.")).toBeInTheDocument();
    expect(screen.getByRole("timer")).toBeInTheDocument();
  });

  it("celebrates admission", () => {
    view({ status: status({ status: "Admitted", position: null, admissionToken: "t", tokenExpiresAt: "2026-12-01T10:15:00Z" }) });

    render(<QueueView event={event} />);

    expect(screen.getAllByText("Đến lượt bạn!").length).toBeGreaterThan(0);
    expect(screen.getByRole("link", { name: "Chọn ghế ngay" })).toBeInTheDocument();
  });

  it.each([
    ["AllHeld", "Tất cả vé đang được giữ"],
    ["SoldOut", "Vé đã bán hết"],
    ["Closed", "Đợt bán đã kết thúc"],
    ["Expired", "Đã hết thời gian mua vé"],
  ] as const)("explains the %s state", (kind, title) => {
    view({ status: status({ status: kind, position: null }) });

    render(<QueueView event={event} />);

    expect(screen.getByRole("heading", { name: title })).toBeInTheDocument();
  });

  it("asks for the presale code when the api wants one, and retries with it", async () => {
    view({ phase: "failed", status: null, error: new ApiError(403, { code: "queue.invalid_access_code" }) });
    render(<QueueView event={event} />);
    expect(screen.queryByRole("alert")).not.toBeInTheDocument();

    await userEvent.type(screen.getByLabelText("Mã truy cập"), "VIP-2026");
    await userEvent.click(screen.getByRole("button", { name: "Vào phòng chờ" }));

    expect(queue.join).toHaveBeenLastCalledWith("VIP-2026", TEST_TOKEN);
  });

  it("complains once a submitted code was refused", async () => {
    view({ phase: "failed", status: null, error: new ApiError(403, { code: "queue.invalid_access_code" }) });
    const { rerender } = render(<QueueView event={event} />);
    await userEvent.type(screen.getByLabelText("Mã truy cập"), "sai");
    await userEvent.click(screen.getByRole("button", { name: "Vào phòng chờ" }));

    rerender(<QueueView event={event} />);

    expect(screen.getByRole("alert")).toHaveTextContent("Mã truy cập không đúng.");
  });

  it("maps a closed sale error to the closed state and other errors to a retry", async () => {
    view({ phase: "failed", status: null, error: new ApiError(409, { code: "queue.closed" }) });
    const { rerender } = render(<QueueView event={event} />);
    expect(screen.getByRole("heading", { name: "Đợt bán đã kết thúc" })).toBeInTheDocument();

    view({ phase: "failed", status: null, error: new ApiError(503, { code: "system.busy" }) });
    rerender(<QueueView event={event} />);
    expect(screen.getByRole("alert")).toHaveTextContent("Hệ thống đang bận");
    queue.join.mockClear();
    await userEvent.click(screen.getByRole("button", { name: "Thử lại" }));
    expect(queue.join).toHaveBeenCalledTimes(1);
  });

  it("leaves the waiting room and goes back to the event", async () => {
    view();
    render(<QueueView event={event} />);

    await userEvent.click(screen.getByRole("button", { name: "Rời phòng chờ" }));

    expect(queue.leave).toHaveBeenCalled();
    expect(push).toHaveBeenCalledWith("/events/show");
  });
});
