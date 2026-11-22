import { act, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { ApiError } from "@/lib/errors";
import type { DashboardSnapshot } from "@/lib/dashboard/api";

const feed = vi.hoisted(() => ({
  options: null as null | { onSnapshot: (s: DashboardSnapshot) => void; onStatus: (live: boolean) => void },
  stop: vi.fn(async () => undefined),
}));
const api = vi.hoisted(() => ({ getDashboard: vi.fn(), downloadAttendees: vi.fn() }));
vi.mock("@/lib/dashboard/api", () => ({
  getDashboard: api.getDashboard,
  downloadAttendees: api.downloadAttendees,
  connectDashboardFeed: (_id: string, options: typeof feed.options) => {
    feed.options = options;
    return { stop: feed.stop };
  },
}));
vi.mock("./sales-chart", () => ({ SalesChart: () => <div data-testid="chart" /> }));
vi.mock("./seat-heatmap", () => ({ SeatHeatmap: () => <div data-testid="heatmap" /> }));

import { DashboardView, Funnel } from "./dashboard-view";

const kpi = (label: string) => within(screen.getByRole("region", { name: "Chỉ số chính" })).getByText(label).nextSibling;

const snapshot = (over: Partial<DashboardSnapshot> = {}): DashboardSnapshot => ({
  eventId: "e1",
  at: "2026-12-01T03:00:05Z",
  sold: 120,
  revenue: 96_000_000,
  currency: "VND",
  remaining: 380,
  tiers: [{ tierId: "vip", name: "VIP", color: "#7c3aed", price: 2_000_000, capacity: 100, sold: 40, held: 5, remaining: 55, revenue: 80_000_000 }],
  funnel: { queueJoined: 1000, admitted: 800, held: 400, ordersCreated: 200, paid: 120 },
  queue: { waiting: 200, activeShoppers: 35, admissionRate: 4.5 },
  recentOrders: [{ orderId: "o1", buyerName: "Nguyễn An", tickets: 2, total: 4_000_000, paidAt: "2026-12-01T03:00:01Z" }],
  ...over,
});

beforeEach(() => {
  // Reduced motion: numbers jump to their value, so the assertions do not wait for the count-up.
  vi.stubGlobal("matchMedia", (query: string) => ({ matches: query.includes("reduce"), addEventListener: vi.fn(), removeEventListener: vi.fn() }));
});

afterEach(() => {
  vi.clearAllMocks();
  vi.unstubAllGlobals();
  feed.options = null;
});

describe("DashboardView", () => {
  it("renders the KPIs, tiers and recent orders from the first snapshot, then follows the live feed", async () => {
    api.getDashboard.mockResolvedValue(snapshot());
    render(<DashboardView eventId="e1" />);

    expect(await screen.findByRole("heading", { name: "Bảng điều khiển bán vé" })).toBeInTheDocument();
    expect(kpi("Đã bán")).toHaveTextContent("120");
    expect(kpi("Đang trong hàng chờ")).toHaveTextContent("200");
    expect(kpi("Đang mua")).toHaveTextContent("35");
    expect(screen.getByText("Nguyễn An")).toBeInTheDocument();
    expect(screen.getByRole("progressbar", { name: "VIP: đã bán 40%" })).toBeInTheDocument();
    expect(screen.getByText("Đang kết nối lại…")).toBeInTheDocument();

    act(() => {
      feed.options!.onStatus(true);
      feed.options!.onSnapshot(snapshot({ sold: 121, at: "2026-12-01T03:00:06Z" }));
    });

    expect(screen.getByText("Live")).toBeInTheDocument();
    await waitFor(() => expect(kpi("Đã bán")).toHaveTextContent("121"));
  });

  it("says when the event is not the organizer's", async () => {
    api.getDashboard.mockRejectedValue(new ApiError(404, { code: "dashboard.not_found", title: "x" }));
    render(<DashboardView eventId="e1" />);

    expect(await screen.findByText("Không tìm thấy sự kiện")).toBeInTheDocument();
  });

  it("exports the attendees and shows why an export failed", async () => {
    api.getDashboard.mockResolvedValue(snapshot());
    api.downloadAttendees.mockRejectedValueOnce(new ApiError(503, { code: "system.busy", title: "x" })).mockResolvedValueOnce(undefined);
    render(<DashboardView eventId="e1" />);

    fireEvent.click(await screen.findByRole("button", { name: "Xuất CSV" }));
    expect(await screen.findByRole("alert")).toHaveTextContent("Hệ thống đang bận");
    fireEvent.click(screen.getByRole("button", { name: "Xuất CSV" }));
    await waitFor(() => expect(api.downloadAttendees).toHaveBeenCalledTimes(2));
  });

  it("stops the feed when the page goes away", async () => {
    api.getDashboard.mockResolvedValue(snapshot());
    const { unmount } = render(<DashboardView eventId="e1" />);
    await screen.findByRole("heading", { name: "Bảng điều khiển bán vé" });

    unmount();

    expect(feed.stop).toHaveBeenCalled();
  });
});

describe("Funnel", () => {
  it("shows each step with the drop-off from the one before", () => {
    render(<Funnel funnel={snapshot().funnel} />);

    const steps = screen.getAllByRole("listitem");
    expect(steps).toHaveLength(5);
    expect(steps[0]).toHaveTextContent("Vào hàng chờ");
    expect(steps[1]).toHaveTextContent("−20%");
    expect(steps[4]).toHaveTextContent("Đã thanh toán");
    expect(steps[4]).toHaveTextContent("−40%");
  });
});
