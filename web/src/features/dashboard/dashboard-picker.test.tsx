import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { isNavActive } from "@/features/organizer/organizer-shell";

const api = vi.hoisted(() => ({ listEvents: vi.fn() }));
vi.mock("@/features/organizer/api", () => ({ listEvents: api.listEvents, organizerKeys: { events: (f: object) => ["organizer", "events", f] } }));

import { DashboardPicker } from "./dashboard-picker";

const event = (id: string, status: string, startsAt: string) => ({ id, slug: id, title: `Sự kiện ${id}`, status, startsAt, venueName: "Nhà hát", seatCount: 100, coverImageUrl: null });

const renderPicker = () =>
  render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
      <DashboardPicker />
    </QueryClientProvider>,
  );

describe("DashboardPicker", () => {
  it("lists the events with something to watch, on sale first, each linking to its dashboard", async () => {
    api.listEvents.mockResolvedValue([
      event("a", "Ended", "2026-10-01T12:00:00Z"),
      event("b", "Draft", "2026-12-01T12:00:00Z"),
      event("c", "Published", "2026-12-20T12:00:00Z"),
      event("d", "Suspended", "2026-12-10T12:00:00Z"),
    ]);
    renderPicker();

    const links = await screen.findAllByRole("link", { name: /Sự kiện/ });
    expect(links.map((l) => l.textContent)).toEqual(["Sự kiện d", "Sự kiện c", "Sự kiện a"]);
    expect(links[1]).toHaveAttribute("href", "/organizer/events/c/dashboard");
    expect(screen.getByText("Tạm ẩn")).toBeInTheDocument();
  });

  it("explains when nothing is on sale yet", async () => {
    api.listEvents.mockResolvedValue([event("b", "Draft", "2026-12-01T12:00:00Z")]);
    renderPicker();

    expect(await screen.findByText("Chưa có sự kiện để theo dõi")).toBeInTheDocument();
  });
});

describe("isNavActive", () => {
  it("puts an event's dashboard under Dashboard, not under Sự kiện", () => {
    expect(isNavActive("/organizer/events/e1/dashboard", "/organizer/dashboard")).toBe(true);
    expect(isNavActive("/organizer/events/e1/dashboard", "/organizer/events")).toBe(false);
    expect(isNavActive("/organizer/events/e1/info", "/organizer/events")).toBe(true);
    expect(isNavActive("/organizer/dashboard", "/organizer/dashboard")).toBe(true);
    expect(isNavActive("/organizer/venues", "/organizer/dashboard")).toBe(false);
  });
});
