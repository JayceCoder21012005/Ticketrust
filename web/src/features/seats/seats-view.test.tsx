import { act, fireEvent, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { ApiError } from "@/lib/errors";
import type { Hold, SeatMap } from "@/lib/seats/types";

const router = vi.hoisted(() => ({ replace: vi.fn(), push: vi.fn() }));
vi.mock("next/navigation", () => ({ useRouter: () => router }));

const toast = vi.hoisted(() => ({ error: vi.fn(), success: vi.fn() }));
vi.mock("sonner", () => ({ toast }));

const availability = vi.hoisted(() => ({ fetchSeatMap: vi.fn(), fetchAvailability: vi.fn() }));
vi.mock("@/lib/seats/availability", () => availability);

const holds = vi.hoisted(() => ({ createHold: vi.fn(), getMyHold: vi.fn(), releaseMyHold: vi.fn() }));
vi.mock("@/lib/seats/holds-api", async (original) => ({ ...(await original<typeof import("@/lib/seats/holds-api")>()), ...holds }));

vi.mock("@/lib/seats/realtime", () => ({ connectSeatFeed: vi.fn(() => ({ stop: () => Promise.resolve() })) }));
vi.mock("@/lib/api/session", () => ({ getAccessToken: () => "tok", refreshAccessToken: () => Promise.resolve("tok") }));

const admission = vi.hoisted(() => ({ loadAdmission: vi.fn() }));
vi.mock("@/lib/queue/admission-store", () => admission);

vi.mock("./lazy-seat-map", async () => ({ LazySeatMap: (await import("./seat-map/seat-map")).SeatMap }));

const { SeatsView } = await import("./seats-view");

const now = Date.parse("2026-12-01T10:00:00Z");
const event = {
  id: "e1",
  slug: "su-kien",
  title: "Sự kiện",
  tiers: [
    { id: "vip", name: "VIP", price: 1_500_000, currency: "VND", kind: "Seated" as const, color: "#e11d48" },
    { id: "ga", name: "Đứng", price: 500_000, currency: "VND", kind: "GeneralAdmission" as const, color: "#16a34a" },
  ],
  phases: [
    { id: "p1", name: "Mở bán", opensAt: "2026-12-01T09:00:00Z", closesAt: "2026-12-01T12:00:00Z", queueEnabled: true, maxTicketsPerUser: 3, requiresAccessCode: false },
  ],
};

const seatMap: SeatMap = {
  eventId: "e1",
  version: "v",
  sections: [{ name: "A", seatCount: 4 }],
  seats: [0, 1, 2, 3].map((i) => ({ index: i, section: "A", row: "1", number: String(i + 1), x: 100 + i * 24, y: 100, tierId: "vip" })),
};

const hold = (seats: number[], expiresInMs = 480_000): Hold => ({
  holdId: "h1",
  seats: seats.map((i) => ({ index: i, section: "A", row: "1", number: String(i + 1), tierId: "vip", price: 1_500_000 })),
  ga: [],
  total: seats.length * 1_500_000,
  expiresAt: new Date(now + expiresInMs).toISOString(),
  serverNow: new Date(now).toISOString(),
});

function arrange({ myHold = null as Hold | null, unavailable = [] as number[] } = {}) {
  admission.loadAdmission.mockReturnValue({ eventId: "e1", slug: "su-kien", token: "adm", expiresAt: new Date(now + 600_000).toISOString() });
  availability.fetchSeatMap.mockResolvedValue(seatMap);
  availability.fetchAvailability.mockResolvedValue({ serverNow: new Date(now).toISOString(), version: 1, unavailable, tiers: [{ tierId: "ga", remaining: 10 }], offsetMs: 0 });
  holds.getMyHold.mockResolvedValue(myHold);
}

const seatButton = (number: number) => screen.findByRole("button", { name: new RegExp(`ghế ${number},`) });

describe("SeatsView", () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it("sends buyers without an admission back to the waiting room", async () => {
    arrange();
    admission.loadAdmission.mockReturnValue(null);

    render(<SeatsView event={event} />);

    await vi.waitFor(() => expect(router.replace).toHaveBeenCalledWith("/events/su-kien/queue"));
  });

  it("shows the seats of my current hold as mine, with the countdown", async () => {
    arrange({ myHold: hold([1]) });

    render(<SeatsView event={event} />);

    expect(await screen.findByRole("button", { name: /ghế 2,.*bạn đang giữ/ })).toBeInTheDocument();
    expect(screen.getByText(/Giữ chỗ còn/)).toBeInTheDocument();
  });

  it("holds the selection on Continue and shows the countdown", async () => {
    arrange();
    holds.createHold.mockResolvedValue(hold([0, 2]));
    const user = userEvent.setup();
    render(<SeatsView event={event} />);

    await user.click(await seatButton(1));
    await user.click(await seatButton(3));
    await user.click(screen.getByRole("button", { name: "Tiếp tục" }));

    expect(holds.createHold).toHaveBeenCalledWith("e1", { seatIndexes: [0, 2], ga: [], replaceExisting: false }, "adm", expect.any(String));
    expect(await screen.findByText(/Giữ chỗ còn/)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /ghế 1,.*bạn đang giữ/ })).toBeInTheDocument();
  });

  it("keeps the other seats after a conflict and says how many were taken", async () => {
    arrange();
    holds.createHold.mockRejectedValue(new ApiError(409, { code: "hold.seats_unavailable", unavailable: [2] }));
    const user = userEvent.setup();
    render(<SeatsView event={event} />);

    await user.click(await seatButton(1));
    await user.click(await seatButton(3));
    await user.click(screen.getByRole("button", { name: "Tiếp tục" }));

    await vi.waitFor(() => expect(toast.error).toHaveBeenCalledWith("1 ghế vừa có người chọn"));
    expect(screen.getByRole("button", { name: /ghế 3,.*đã có người chọn/ })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /ghế 1,.*đang chọn/ })).toBeInTheDocument();
  });

  it("retries the same selection with the same idempotency key", async () => {
    arrange();
    holds.createHold.mockRejectedValueOnce(new ApiError(503, { code: "system.busy" })).mockResolvedValueOnce(hold([0]));
    const user = userEvent.setup();
    render(<SeatsView event={event} />);

    await user.click(await seatButton(1));
    await user.click(screen.getByRole("button", { name: "Tiếp tục" }));
    await vi.waitFor(() => expect(toast.error).toHaveBeenCalled());
    await user.click(screen.getByRole("button", { name: "Tiếp tục" }));

    await vi.waitFor(() => expect(holds.createHold).toHaveBeenCalledTimes(2));
    expect(holds.createHold.mock.calls[0][3]).toBe(holds.createHold.mock.calls[1][3]);
  });

  it("a final refusal gets a fresh idempotency key on the next try, so it is not replayed", async () => {
    arrange();
    holds.createHold.mockRejectedValueOnce(new ApiError(409, { code: "hold.insufficient_quantity" })).mockResolvedValueOnce(hold([1, 2]));
    const user = userEvent.setup();
    render(<SeatsView event={event} />);

    const best = await screen.findByRole("button", { name: /Chọn chỗ tốt nhất/ });
    await user.click(best);
    await vi.waitFor(() => expect(toast.error).toHaveBeenCalled());
    await user.click(best);

    await vi.waitFor(() => expect(holds.createHold).toHaveBeenCalledTimes(2));
    expect(holds.createHold.mock.calls[0][3]).not.toBe(holds.createHold.mock.calls[1][3]);
  });

  it("sends buyers whose admission ended back to the waiting room", async () => {
    arrange();
    holds.createHold.mockRejectedValue(new ApiError(403, { code: "admission.invalid" }));
    const user = userEvent.setup();
    render(<SeatsView event={event} />);

    await user.click(await seatButton(1));
    await user.click(screen.getByRole("button", { name: "Tiếp tục" }));

    await vi.waitFor(() => expect(router.replace).toHaveBeenCalledWith("/events/su-kien/queue"));
  });

  it("asks the api for the best seats of a tier", async () => {
    arrange();
    holds.createHold.mockResolvedValue(hold([1, 2]));
    const user = userEvent.setup();
    render(<SeatsView event={event} />);

    await user.click(await screen.findByRole("button", { name: /Chọn chỗ tốt nhất/ }));

    expect(holds.createHold).toHaveBeenCalledWith("e1", { bestAvailable: { tierId: "vip", quantity: 2 }, ga: [], replaceExisting: false }, "adm", expect.any(String));
  });

  it("caps the standing-ticket stepper at the per-buyer limit", async () => {
    arrange();
    const user = userEvent.setup();
    render(<SeatsView event={event} />);

    const more = await screen.findByRole("button", { name: "Thêm 1 vé Đứng" });
    for (let i = 0; i < 5; i++) await user.click(more);

    expect(screen.getByTestId("ga-quantity-ga")).toHaveTextContent("3");
    expect(more).toBeDisabled();
  });

  it("releases the hold and returns to selection", async () => {
    arrange({ myHold: hold([1]) });
    holds.releaseMyHold.mockResolvedValue(undefined);
    const user = userEvent.setup();
    render(<SeatsView event={event} />);

    await user.click(await screen.findByRole("button", { name: "Bỏ giữ chỗ" }));

    await vi.waitFor(() => expect(holds.releaseMyHold).toHaveBeenCalledWith("e1"));
    expect(await screen.findByRole("button", { name: "Tiếp tục" })).toBeInTheDocument();
  });

  it("shows the expiry dialog when the hold runs out and goes back to selection", async () => {
    vi.useFakeTimers({ toFake: ["setInterval", "setTimeout", "Date"] });
    vi.setSystemTime(now);
    arrange({ myHold: hold([1], 2_000) });
    render(<SeatsView event={event} />);
    await act(async () => {
      await vi.advanceTimersByTimeAsync(10);
    });

    await act(async () => {
      await vi.advanceTimersByTimeAsync(3_000);
    });

    expect(screen.getByRole("alertdialog", { name: "Hết thời gian giữ chỗ" })).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Chọn lại" }));
    await act(async () => {
      await vi.advanceTimersByTimeAsync(10);
    });
    expect(screen.queryByRole("alertdialog")).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Tiếp tục" })).toBeInTheDocument();
  });

  it("offers a retry when the seat map cannot load", async () => {
    arrange();
    availability.fetchSeatMap.mockRejectedValueOnce(new Error("503"));
    const user = userEvent.setup();
    render(<SeatsView event={event} />);

    await user.click(await screen.findByRole("button", { name: "Thử lại" }));

    expect(await seatButton(1)).toBeInTheDocument();
  });
});
