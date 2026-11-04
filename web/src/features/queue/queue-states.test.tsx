import { act, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { QueueStatus } from "@/lib/queue/types";
import { AccessCodeForm } from "./access-code-form";
import { AdmittedState } from "./admitted-state";
import { ClosedState } from "./closed-state";
import { PreState } from "./pre-state";
import { WaitingState } from "./waiting-state";

const push = vi.fn();
vi.mock("next/navigation", () => ({ useRouter: () => ({ push }) }));

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

describe("WaitingState", () => {
  it("shows the place, how many are ahead, a rounded wait and the do-not-reload promise", () => {
    render(<WaitingState status={status()} startPosition={2000} connectionIssues={0} />);

    expect(screen.getByText("Còn 1.233 người phía trước")).toBeInTheDocument();
    expect(screen.getByText("Vị trí của bạn: #1.234")).toBeInTheDocument();
    expect(screen.getByText("Dự kiến chờ khoảng 4 phút")).toBeInTheDocument();
    expect(screen.getByText("Đừng tải lại trang — bạn sẽ không mất lượt.")).toBeInTheDocument();
  });

  it("draws progress from the first known position to the front", () => {
    render(<WaitingState status={status({ position: 500 })} startPosition={2000} connectionIssues={0} />);

    expect(screen.getByRole("progressbar", { name: "Tiến độ trong hàng chờ" })).toHaveAttribute("aria-valuenow", "75");
  });

  it("says you are next at the front of the line", () => {
    render(<WaitingState status={status({ position: 1, aheadOfYou: 0, estimatedWaitSeconds: 1 })} startPosition={10} connectionIssues={0} />);

    expect(screen.getByText("Bạn là người tiếp theo")).toBeInTheDocument();
    expect(screen.getByText("Dự kiến chờ dưới 1 phút")).toBeInTheDocument();
  });

  it("only mentions reconnecting while polls are failing", () => {
    const { rerender } = render(<WaitingState status={status()} startPosition={2000} connectionIssues={0} />);
    expect(screen.queryByRole("status")).not.toBeInTheDocument();

    rerender(<WaitingState status={status()} startPosition={2000} connectionIssues={2} />);
    expect(screen.getByRole("status")).toHaveTextContent("Mất kết nối, đang thử lại");
  });

  it("offers browser notifications only while permission is undecided", async () => {
    const onEnable = vi.fn();
    render(<WaitingState status={status()} startPosition={2000} connectionIssues={0} notification="default" onEnableNotification={onEnable} />);

    await userEvent.click(screen.getByRole("button", { name: "Báo cho tôi khi đến lượt" }));

    expect(onEnable).toHaveBeenCalled();
  });
});

describe("PreState", () => {
  beforeEach(() => vi.useFakeTimers());
  afterEach(() => vi.useRealTimers());

  it("explains the random order and counts down on server time, not the device clock", () => {
    // The device clock is an hour fast, but the server says the sale opens in 90 seconds.
    vi.setSystemTime(new Date("2026-12-01T11:00:00Z"));
    render(<PreState status={status({ status: "Pre", position: null, serverNow: "2026-12-01T09:58:30Z" })} />);

    expect(screen.getByText("Mọi người vào phòng chờ trước giờ mở bán sẽ được xếp thứ tự ngẫu nhiên.")).toBeInTheDocument();
    expect(screen.getByRole("timer")).toHaveTextContent("01:30");

    act(() => vi.advanceTimersByTime(30_000));
    expect(screen.getByRole("timer")).toHaveTextContent("01:00");
  });
});

describe("AdmittedState", () => {
  beforeEach(() => {
    vi.useFakeTimers();
    push.mockClear();
  });
  afterEach(() => vi.useRealTimers());

  const admitted = status({ status: "Admitted", position: null, admissionToken: "t", tokenExpiresAt: "2026-12-01T10:15:00Z" });

  it("announces the turn, the shopping window, and a way to go now", () => {
    render(<AdmittedState status={admitted} slug="show" />);

    expect(screen.getByRole("heading", { name: "Đến lượt bạn!" })).toBeInTheDocument();
    expect(screen.getByText("Bạn có 10 phút để chọn ghế và thanh toán.")).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Chọn ghế ngay" })).toHaveAttribute("href", "/events/show/seats");
  });

  it("goes to seat selection after 3 seconds, not before", () => {
    render(<AdmittedState status={admitted} slug="show" />);

    act(() => vi.advanceTimersByTime(2_900));
    expect(push).not.toHaveBeenCalled();
    act(() => vi.advanceTimersByTime(200));
    expect(push).toHaveBeenCalledWith("/events/show/seats");
  });

  it("does not redirect after it was unmounted", () => {
    const { unmount } = render(<AdmittedState status={admitted} slug="show" />);
    unmount();

    act(() => vi.advanceTimersByTime(5_000));

    expect(push).not.toHaveBeenCalled();
  });
});

describe("ClosedState", () => {
  it.each([
    ["Closed", "Đợt bán đã kết thúc"],
    ["SoldOut", "Vé đã bán hết"],
    ["AllHeld", "Tất cả vé đang được giữ"],
    ["Expired", "Đã hết thời gian mua vé"],
  ] as const)("explains %s", (kind, title) => {
    render(<ClosedState kind={kind} slug="show" onRejoin={() => {}} />);

    expect(screen.getByRole("heading", { name: title })).toBeInTheDocument();
  });

  it("lets an expired buyer rejoin", async () => {
    const onRejoin = vi.fn();
    render(<ClosedState kind="Expired" slug="show" onRejoin={onRejoin} />);

    await userEvent.click(screen.getByRole("button", { name: "Vào lại phòng chờ" }));

    expect(onRejoin).toHaveBeenCalled();
  });
});

describe("AccessCodeForm", () => {
  it("submits the trimmed code and blocks an empty one", async () => {
    const onSubmit = vi.fn();
    render(<AccessCodeForm onSubmit={onSubmit} />);

    await userEvent.click(screen.getByRole("button", { name: "Vào phòng chờ" }));
    expect(onSubmit).not.toHaveBeenCalled();

    await userEvent.type(screen.getByLabelText("Mã truy cập"), "  VIP-2026 ");
    await userEvent.click(screen.getByRole("button", { name: "Vào phòng chờ" }));

    expect(onSubmit).toHaveBeenCalledWith("VIP-2026");
  });

  it("shows the server's complaint about the last code", () => {
    render(<AccessCodeForm onSubmit={() => {}} error="Mã truy cập không đúng." />);

    expect(screen.getByRole("alert")).toHaveTextContent("Mã truy cập không đúng.");
  });
});
